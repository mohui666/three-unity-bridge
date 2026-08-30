using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeUnity.Bridge.Logic
{
    public enum ThreeUnityLogicRouteResult
    {
        Handled,
        Restarted,
        Rejected,
    }

    /// <summary>
    /// Owns exactly one logic-module generation and keeps session/lifecycle policy
    /// outside individual game profiles. All methods are called from Unity's main
    /// thread; the injected factory keeps the state machine independently testable.
    /// </summary>
    public sealed class ThreeUnityLogicSessionRouter : IDisposable,
        IThreeUnityLogicTelemetry,
        IThreeUnityCollisionTelemetry,
        IThreeUnityInputTelemetry
    {
        [Serializable]
        private sealed class SessionTransitionMessage
        {
            public SessionTransitionPayload payload;
        }

        [Serializable]
        private sealed class SessionTransitionPayload
        {
            public string previousSessionId;
            public string[] capabilities;
        }

        [Serializable]
        private sealed class RuntimeLifecyclePayload
        {
            public bool focused;
            public bool paused;
            public bool active;
            public long revision;
        }

        [Serializable]
        private sealed class RuntimeLifecycleAckMessage
        {
            public RuntimeLifecycleAckPayload payload;
        }

        [Serializable]
        private sealed class RuntimeLifecycleAckPayload
        {
            public long revision;
            public bool active;
        }

        private readonly string profile;
        private readonly Func<string, IThreeUnityLogicModule> moduleFactory;
        private readonly Dictionary<string, long> receivedSequences = new Dictionary<string, long>(StringComparer.Ordinal);
        private IThreeUnityLogicModule module;
        private string activeSessionId;
        private bool scopedSession;
        private bool awaitingScopedHello;
        private bool legacyHandled;
        private bool disposed;
        private long hostGeneration;
        private long transportResets;
        private long generationRejected;
        private long sessionRestarts;
        private long sessionRejected;
        private long sequenceRejected;
        private long retiredOutgoingDiscarded;
        private long outgoingMetadataFastPath;
        private long outgoingMetadataFallbackParses;
        private long retiredStateEmitted;
        private long retiredStateSuppressed;
        private long retiredStateHeartbeats;
        private long retiredStateRateLimited;
        private long retiredCollisionFull;
        private long retiredCollisionDelta;
        private long retiredCollisionCells;
        private long retiredCollisionResync;
        private long retiredInputExpired;
        private long retiredInputRecovered;
        private long retiredInputNeutralized;
        private bool applicationFocused = true;
        private bool applicationPaused;
        private long applicationLifecycleRevision;
        private bool lifecycleNegotiated;
        private bool lifecycleReadyBarrier;
        private bool hasPendingLifecycle;
        private ThreeUnityLogicOutgoingMessage pendingLifecycle;
        private readonly Dictionary<long, bool> emittedLifecycleStates = new Dictionary<long, bool>();
        private readonly Queue<long> emittedLifecycleOrder = new Queue<long>();
        private long lifecycleSequence;
        private long lifecycleChanges;
        private long lifecycleEmitted;
        private long lifecycleCoalesced;
        private long lifecycleAcknowledged;
        private long lifecycleAckRejected;

        public ThreeUnityLogicSessionRouter(
            string profile,
            Func<string, IThreeUnityLogicModule> moduleFactory = null,
            long initialHostGeneration = 0)
        {
            if (initialHostGeneration < 0)
                throw new ArgumentOutOfRangeException(nameof(initialHostGeneration));
            this.profile = profile;
            this.moduleFactory = moduleFactory ?? ThreeUnityLogicModuleRegistry.Create;
            hostGeneration = initialHostGeneration;
            module = this.moduleFactory(profile);
        }

        public IThreeUnityLogicModule CurrentModule => module;
        public string ActiveSessionId => activeSessionId;
        public bool HasScopedSession => scopedSession;
        public bool IsAwaitingHello => scopedSession && awaitingScopedHello;
        public long HostGeneration => hostGeneration;
        public long TransportResets => transportResets;
        public long GenerationRejected => generationRejected;
        public long SessionRestarts => sessionRestarts;
        public long SessionRejected => sessionRejected;
        public long SequenceRejected => sequenceRejected;
        public long RetiredOutgoingDiscarded => retiredOutgoingDiscarded;
        public long OutgoingMetadataFastPath => outgoingMetadataFastPath;
        public long OutgoingMetadataFallbackParses => outgoingMetadataFallbackParses;
        public bool ApplicationFocused => applicationFocused;
        public bool ApplicationPaused => applicationPaused;
        public bool ApplicationActive => applicationFocused && !applicationPaused;
        public long ApplicationLifecycleRevision => applicationLifecycleRevision;
        public long LifecycleChanges => lifecycleChanges;
        public long LifecycleEmitted => lifecycleEmitted;
        public long LifecycleCoalesced => lifecycleCoalesced;
        public long LifecycleAcknowledged => lifecycleAcknowledged;
        public long LifecycleAckRejected => lifecycleAckRejected;

        /// <summary>
        /// Updates Unity Player lifecycle state. The latest state is coalesced per
        /// logical session and is emitted only after that session's bridge.ready.
        /// </summary>
        public bool SetApplicationLifecycle(bool focused, bool paused)
        {
            ThrowIfDisposed();
            if (applicationFocused == focused && applicationPaused == paused)
                return false;

            applicationFocused = focused;
            applicationPaused = paused;
            applicationLifecycleRevision++;
            lifecycleChanges++;
            if (lifecycleNegotiated)
                QueueLifecycleState();
            return true;
        }

        /// <summary>
        /// Accounts for an envelope already dequeued from the module but retained
        /// by the transport retry head when its logical or physical owner retires.
        /// </summary>
        internal void RecordRetainedOutgoingDiscarded()
        {
            ThrowIfDisposed();
            retiredOutgoingDiscarded++;
        }

        public ThreeUnityLogicRouteResult Handle(string json, LogicEnvelopeHeader header)
        {
            return Handle(hostGeneration, json, header);
        }

        public ThreeUnityLogicRouteResult Handle(
            long incomingHostGeneration,
            string json,
            LogicEnvelopeHeader header)
        {
            if (!TryAcceptHostGeneration(incomingHostGeneration))
                return ThreeUnityLogicRouteResult.Rejected;
            if (header == null)
                throw new ArgumentNullException(nameof(header));
            if (module == null)
                return Reject();
            if (header.sessionId != null
                && (string.IsNullOrWhiteSpace(header.sessionId)
                    || !string.Equals(header.sessionId, header.sessionId.Trim(), StringComparison.Ordinal)
                    || header.sessionId.Length > LogicEnvelopeParser.MaxSessionIdLength))
                return Reject();

            var incomingSessionId = NormalizeSessionId(header.sessionId);
            if (string.Equals(header.type, "bridge.restart", StringComparison.Ordinal))
                return HandleRestart(json, header, incomingSessionId);
            if (string.Equals(header.type, "bridge.hello", StringComparison.Ordinal))
                return HandleHello(json, header, incomingSessionId);

            if (scopedSession)
            {
                if (!string.Equals(activeSessionId, incomingSessionId, StringComparison.Ordinal)
                    || awaitingScopedHello
                    || module.IsFallback)
                    return Reject();
            }
            else
            {
                if (incomingSessionId != null || module.IsFallback)
                    return Reject();
                legacyHandled = true;
            }

            if (!AcceptSequence(header))
                return RejectSequence();
            if (string.Equals(header.type, "runtime.lifecycle.ack", StringComparison.Ordinal))
            {
                HandleLifecycleAck(json);
                return ThreeUnityLogicRouteResult.Handled;
            }
            module.Handle(json, header);
            if (module.IsFallback)
                DisableLifecycle(true);
            return ThreeUnityLogicRouteResult.Handled;
        }

        /// <summary>
        /// Performs the transport-generation gate without inspecting message
        /// contents. This lets callers discard stale bytes before JSON parsing.
        /// </summary>
        public bool TryAcceptHostGeneration(long incomingHostGeneration)
        {
            ThrowIfDisposed();
            if (incomingHostGeneration == hostGeneration)
                return true;
            generationRejected++;
            return false;
        }

        /// <summary>
        /// Retires all state owned by an earlier physical WebView/pipe generation.
        /// Equal generations are an idempotent no-op and older generations are
        /// ignored. Call this before routing any message captured for a newer page.
        /// </summary>
        /// <returns>True only when a new generation was installed.</returns>
        public bool ResetForHostGeneration(long newHostGeneration)
        {
            ThrowIfDisposed();
            if (newHostGeneration < 0)
                throw new ArgumentOutOfRangeException(nameof(newHostGeneration));
            if (newHostGeneration <= hostGeneration)
                return false;

            var replacement = moduleFactory(profile);
            if (replacement == null)
                throw new InvalidOperationException("The logic module factory returned null for profile '" + profile + "'.");

            var retired = module;
            module = replacement;
            hostGeneration = newHostGeneration;
            activeSessionId = null;
            scopedSession = false;
            awaitingScopedHello = false;
            legacyHandled = false;
            receivedSequences.Clear();
            ResetLifecycleSession(true);
            transportResets++;
            Retire(retired);
            return true;
        }

        public void FixedTick(float deltaTime)
        {
            ThrowIfDisposed();
            if (module == null || module.IsFallback || (scopedSession && awaitingScopedHello))
                return;
            module.FixedTick(deltaTime);
        }

        public bool TryDequeueOutgoing(out string json)
        {
            if (!TryDequeueOutgoingMessage(out var message))
            {
                json = null;
                return false;
            }
            json = message.Json;
            return true;
        }

        /// <summary>
        /// Uses producer-supplied type/session metadata when available. Custom
        /// legacy modules retain the original string contract and are parsed once
        /// here solely for transport classification.
        /// </summary>
        public bool TryDequeueOutgoingMessage(out ThreeUnityLogicOutgoingMessage message)
        {
            if (disposed || module == null)
            {
                message = default;
                return false;
            }

            if (module.IsFallback)
                DisableLifecycle(true);

            if (lifecycleReadyBarrier)
            {
                if (!TryDequeueModuleOutgoingMessage(out message))
                    return false;
                if (string.Equals(message.Type, "bridge.ready", StringComparison.Ordinal))
                    lifecycleReadyBarrier = false;
                return true;
            }

            if (hasPendingLifecycle)
            {
                message = pendingLifecycle;
                hasPendingLifecycle = false;
                pendingLifecycle = default;
                outgoingMetadataFastPath++;
                lifecycleEmitted++;
                RecordLifecycleEmission(applicationLifecycleRevision, ApplicationActive);
                return true;
            }

            return TryDequeueModuleOutgoingMessage(out message);
        }

        public void ForceFallback(string reason)
        {
            if (disposed || module == null)
                return;
            DisableLifecycle(true);
            module.ForceFallback(reason);
        }

        public ThreeUnityStateEmissionMetrics GetStateEmissionMetrics()
        {
            var current = (module as IThreeUnityLogicTelemetry)?.GetStateEmissionMetrics();
            return new ThreeUnityStateEmissionMetrics
            {
                Emitted = retiredStateEmitted + (current?.Emitted ?? 0),
                Suppressed = retiredStateSuppressed + (current?.Suppressed ?? 0),
                Heartbeats = retiredStateHeartbeats + (current?.Heartbeats ?? 0),
                RateLimited = retiredStateRateLimited + (current?.RateLimited ?? 0),
                HeartbeatSeconds = current?.HeartbeatSeconds ?? 0f,
                MinimumIntervalSeconds = current?.MinimumIntervalSeconds ?? 0f,
            };
        }

        public ThreeUnityCollisionMetrics GetCollisionMetrics()
        {
            var current = (module as IThreeUnityCollisionTelemetry)?.GetCollisionMetrics();
            return new ThreeUnityCollisionMetrics
            {
                FullMessages = retiredCollisionFull + (current?.FullMessages ?? 0),
                DeltaMessages = retiredCollisionDelta + (current?.DeltaMessages ?? 0),
                DeltaCells = retiredCollisionCells + (current?.DeltaCells ?? 0),
                ResyncRequests = retiredCollisionResync + (current?.ResyncRequests ?? 0),
            };
        }

        public ThreeUnityInputFreshnessMetrics GetInputFreshnessMetrics()
        {
            var current = (module as IThreeUnityInputTelemetry)?.GetInputFreshnessMetrics();
            return new ThreeUnityInputFreshnessMetrics
            {
                Fresh = current?.Fresh ?? false,
                AgeSeconds = current?.AgeSeconds ?? 0f,
                TimeoutSeconds = current?.TimeoutSeconds ?? 0f,
                Expirations = retiredInputExpired + (current?.Expirations ?? 0),
                Recoveries = retiredInputRecovered + (current?.Recoveries ?? 0),
                NeutralizedTicks = retiredInputNeutralized + (current?.NeutralizedTicks ?? 0),
            };
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            ResetLifecycleSession(true);
            Retire(module);
            module = null;
            receivedSequences.Clear();
        }

        private ThreeUnityLogicRouteResult HandleHello(
            string json,
            LogicEnvelopeHeader header,
            string incomingSessionId)
        {
            if (incomingSessionId == null)
            {
                if (scopedSession || module.IsFallback)
                    return Reject();
                legacyHandled = true;
                if (!AcceptSequence(header))
                    return RejectSequence();
                module.Handle(json, header);
                DisableLifecycle(true);
                return ThreeUnityLogicRouteResult.Handled;
            }

            var restarted = false;
            if (!scopedSession)
            {
                if (legacyHandled || module.IsFallback)
                {
                    ReplaceModule(incomingSessionId);
                    sessionRestarts++;
                    restarted = true;
                }
                else
                {
                    module.BindSession(incomingSessionId);
                    activeSessionId = incomingSessionId;
                    scopedSession = true;
                    awaitingScopedHello = true;
                    receivedSequences.Clear();
                }
            }
            else if (!string.Equals(activeSessionId, incomingSessionId, StringComparison.Ordinal))
            {
                if (!string.Equals(ReadPreviousSessionId(json), activeSessionId, StringComparison.Ordinal))
                    return Reject();
                ReplaceModule(incomingSessionId);
                sessionRestarts++;
                restarted = true;
            }
            else if (module.IsFallback)
            {
                return Reject();
            }

            if (!AcceptSequence(header))
                return RejectSequence();
            awaitingScopedHello = false;
            module.Handle(json, header);
            ConfigureLifecycleForHello(json);
            return restarted ? ThreeUnityLogicRouteResult.Restarted : ThreeUnityLogicRouteResult.Handled;
        }

        private ThreeUnityLogicRouteResult HandleRestart(
            string json,
            LogicEnvelopeHeader header,
            string incomingSessionId)
        {
            if (!scopedSession
                || incomingSessionId == null
                || string.Equals(activeSessionId, incomingSessionId, StringComparison.Ordinal)
                || !string.Equals(ReadPreviousSessionId(json), activeSessionId, StringComparison.Ordinal))
                return Reject();

            ReplaceModule(incomingSessionId);
            sessionRestarts++;
            awaitingScopedHello = true;
            AcceptSequence(header);
            return ThreeUnityLogicRouteResult.Restarted;
        }

        private void ReplaceModule(string newSessionId)
        {
            var replacement = moduleFactory(profile);
            if (replacement == null)
                throw new InvalidOperationException("The logic module factory returned null for profile '" + profile + "'.");
            try
            {
                replacement.BindSession(newSessionId);
            }
            catch
            {
                replacement.Dispose();
                throw;
            }

            var retired = module;
            module = replacement;
            activeSessionId = newSessionId;
            scopedSession = true;
            awaitingScopedHello = true;
            legacyHandled = false;
            receivedSequences.Clear();
            ResetLifecycleSession(true);
            Retire(retired);
        }

        private bool TryDequeueModuleOutgoingMessage(out ThreeUnityLogicOutgoingMessage message)
        {
            if (module is IThreeUnityLogicOutgoingMetadata metadata)
            {
                if (!metadata.TryDequeueOutgoingMessage(out message))
                    return false;
                outgoingMetadataFastPath++;
                return true;
            }

            if (!module.TryDequeueOutgoing(out var json))
            {
                message = default;
                return false;
            }

            outgoingMetadataFallbackParses++;
            if (LogicEnvelopeParser.TryParseHeader(json, out var header, out _))
            {
                message = new ThreeUnityLogicOutgoingMessage(
                    json,
                    header.type,
                    header.sessionId);
            }
            else
            {
                // Preserve the old behavior for malformed third-party output:
                // it remains reliable transport traffic rather than disappearing.
                message = new ThreeUnityLogicOutgoingMessage(json, null, null);
            }
            return true;
        }

        private void ConfigureLifecycleForHello(string json)
        {
            if (module == null || module.IsFallback || !HelloSupportsLifecycle(json))
            {
                DisableLifecycle(true);
                return;
            }

            lifecycleNegotiated = true;
            lifecycleReadyBarrier = true;
            QueueLifecycleState();
        }

        private void QueueLifecycleState()
        {
            if (!lifecycleNegotiated || string.IsNullOrEmpty(activeSessionId))
                return;
            if (hasPendingLifecycle)
                lifecycleCoalesced++;
            pendingLifecycle = LogicEnvelopeWriter.EncodeMessage(
                "runtime.lifecycle.state",
                lifecycleSequence++,
                activeSessionId,
                new RuntimeLifecyclePayload
                {
                    focused = applicationFocused,
                    paused = applicationPaused,
                    active = ApplicationActive,
                    revision = applicationLifecycleRevision,
                });
            hasPendingLifecycle = true;
        }

        private void HandleLifecycleAck(string json)
        {
            if (!lifecycleNegotiated
                || !TryReadLifecycleAck(json, out var revision, out var active)
                || !emittedLifecycleStates.TryGetValue(revision, out var expectedActive)
                || active != expectedActive)
            {
                lifecycleAckRejected++;
                return;
            }
            emittedLifecycleStates.Remove(revision);
            lifecycleAcknowledged++;
        }

        private void RecordLifecycleEmission(long revision, bool active)
        {
            const int maxTrackedEmissions = 64;
            while (emittedLifecycleOrder.Count >= maxTrackedEmissions)
            {
                var retiredRevision = emittedLifecycleOrder.Dequeue();
                emittedLifecycleStates.Remove(retiredRevision);
            }
            emittedLifecycleOrder.Enqueue(revision);
            emittedLifecycleStates[revision] = active;
        }

        private void DisableLifecycle(bool countPendingAsDiscarded)
        {
            if (hasPendingLifecycle && countPendingAsDiscarded)
                retiredOutgoingDiscarded++;
            lifecycleNegotiated = false;
            lifecycleReadyBarrier = false;
            hasPendingLifecycle = false;
            pendingLifecycle = default;
        }

        private void ResetLifecycleSession(bool countPendingAsDiscarded)
        {
            DisableLifecycle(countPendingAsDiscarded);
            lifecycleSequence = 0;
            emittedLifecycleStates.Clear();
            emittedLifecycleOrder.Clear();
        }

        private static bool HelloSupportsLifecycle(string json)
        {
            try
            {
                var capabilities = JsonUtility.FromJson<SessionTransitionMessage>(json)?.payload?.capabilities;
                return capabilities != null
                    && Array.IndexOf(capabilities, ThreeUnityLogicFeatures.RuntimeLifecycle) >= 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryReadLifecycleAck(string json, out long revision, out bool active)
        {
            revision = -1;
            active = false;
            try
            {
                var payload = JsonUtility.FromJson<RuntimeLifecycleAckMessage>(json)?.payload;
                if (payload == null
                    || payload.revision < 0
                    || !HasJsonProperty(json, "revision")
                    || !HasJsonBooleanProperty(json, "active", payload.active))
                    return false;
                revision = payload.revision;
                active = payload.active;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool HasJsonProperty(string json, string propertyName)
        {
            return json.IndexOf("\"" + propertyName + "\"", StringComparison.Ordinal) >= 0;
        }

        private static bool HasJsonBooleanProperty(string json, string propertyName, bool expected)
        {
            var propertyIndex = json.IndexOf("\"" + propertyName + "\"", StringComparison.Ordinal);
            if (propertyIndex < 0)
                return false;
            var colon = json.IndexOf(':', propertyIndex + propertyName.Length + 2);
            if (colon < 0)
                return false;
            var valueIndex = colon + 1;
            while (valueIndex < json.Length && char.IsWhiteSpace(json[valueIndex]))
                valueIndex++;
            var token = expected ? "true" : "false";
            return valueIndex + token.Length <= json.Length
                && string.CompareOrdinal(json, valueIndex, token, 0, token.Length) == 0;
        }

        private void Retire(IThreeUnityLogicModule retired)
        {
            if (retired == null)
                return;
            CaptureTelemetry(retired);
            while (retired.TryDequeueOutgoing(out _))
                retiredOutgoingDiscarded++;
            retired.Dispose();
        }

        private void CaptureTelemetry(IThreeUnityLogicModule retired)
        {
            var state = (retired as IThreeUnityLogicTelemetry)?.GetStateEmissionMetrics();
            retiredStateEmitted += state?.Emitted ?? 0;
            retiredStateSuppressed += state?.Suppressed ?? 0;
            retiredStateHeartbeats += state?.Heartbeats ?? 0;
            retiredStateRateLimited += state?.RateLimited ?? 0;

            var collision = (retired as IThreeUnityCollisionTelemetry)?.GetCollisionMetrics();
            retiredCollisionFull += collision?.FullMessages ?? 0;
            retiredCollisionDelta += collision?.DeltaMessages ?? 0;
            retiredCollisionCells += collision?.DeltaCells ?? 0;
            retiredCollisionResync += collision?.ResyncRequests ?? 0;

            var input = (retired as IThreeUnityInputTelemetry)?.GetInputFreshnessMetrics();
            retiredInputExpired += input?.Expirations ?? 0;
            retiredInputRecovered += input?.Recoveries ?? 0;
            retiredInputNeutralized += input?.NeutralizedTicks ?? 0;
        }

        private bool AcceptSequence(LogicEnvelopeHeader header)
        {
            if (receivedSequences.TryGetValue(header.type, out var previous) && header.seq <= previous)
                return false;
            receivedSequences[header.type] = header.seq;
            return true;
        }

        private ThreeUnityLogicRouteResult Reject()
        {
            sessionRejected++;
            return ThreeUnityLogicRouteResult.Rejected;
        }

        private ThreeUnityLogicRouteResult RejectSequence()
        {
            sequenceRejected++;
            return Reject();
        }

        private static string ReadPreviousSessionId(string json)
        {
            try
            {
                return NormalizeSessionId(JsonUtility.FromJson<SessionTransitionMessage>(json)?.payload?.previousSessionId);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static string NormalizeSessionId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || value.Length > LogicEnvelopeParser.MaxSessionIdLength)
                return null;
            return value;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ThreeUnityLogicSessionRouter));
        }
    }
}
