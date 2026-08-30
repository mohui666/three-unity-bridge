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
            module.Handle(json, header);
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
            if (disposed || module == null)
            {
                json = null;
                return false;
            }
            return module.TryDequeueOutgoing(out json);
        }

        public void ForceFallback(string reason)
        {
            if (disposed || module == null)
                return;
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
            Retire(retired);
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
