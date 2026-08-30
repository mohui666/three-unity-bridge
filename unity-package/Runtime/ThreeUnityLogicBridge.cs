using System;
using ThreeUnity.Bridge.Logic;
using UnityEngine;

namespace ThreeUnity.Bridge
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ThreeUnityWebBridgeLauncher))]
    public sealed class ThreeUnityLogicBridge : MonoBehaviour
    {
        private const int MaxOutgoingMessagesPerFlush = 256;

        [SerializeField] private ThreeUnityWebBridgeLauncher launcher;
        [SerializeField] private string logicProfile;

        private ThreeUnityLogicSessionRouter router;
        private ThreeUnityWebBridgeLease activeLease;
        private object activeOutboundOwner = new object();
        private bool hasPendingOutgoing;
        private object pendingOutgoingOwner;
        private string pendingOutgoingJson;
        private bool pendingOutgoingIsLatest;
        private string pendingOutgoingStreamKey;
        private long outboundFlushBudgetStops;
        private int maxOutgoingMessagesPerFlush;
        private long fixedTicks;
        private bool applicationFocused = true;
        private bool applicationPaused;

        public void Configure(ThreeUnityWebBridgeLauncher webLauncher, string profile)
        {
            launcher = webLauncher;
            logicProfile = profile;
        }

        private void Awake()
        {
            if (launcher == null)
                launcher = GetComponent<ThreeUnityWebBridgeLauncher>();
            applicationFocused = Application.isFocused;
            applicationPaused = false;
            router = new ThreeUnityLogicSessionRouter(
                logicProfile,
                initialHostGeneration: launcher == null ? 0 : launcher.PageGeneration);
            router.SetApplicationLifecycle(applicationFocused, applicationPaused);
            if (router.CurrentModule == null)
                enabled = false;
        }

        private void OnApplicationFocus(bool focused)
        {
            UpdateApplicationLifecycle(focused, applicationPaused, "focus");
        }

        private void OnApplicationPause(bool paused)
        {
            UpdateApplicationLifecycle(applicationFocused, paused, "pause");
        }

        private void UpdateApplicationLifecycle(bool focused, bool paused, string source)
        {
            applicationFocused = focused;
            applicationPaused = paused;
            if (router == null || !router.SetApplicationLifecycle(focused, paused))
                return;
            Debug.Log("THREE_UNITY_RUNTIME_LIFECYCLE"
                + " source=" + source
                + " focused=" + (focused ? 1 : 0)
                + " paused=" + (paused ? 1 : 0)
                + " active=" + (router.ApplicationActive ? 1 : 0)
                + " revision=" + router.ApplicationLifecycleRevision);
            FlushOutgoing();
        }

        /// <summary>
        /// Installs a hard lifecycle boundary before a replacement WebView page can
        /// send logic messages. The launcher must call this after incrementing its
        /// page generation and before publishing any message from that generation.
        /// </summary>
        public bool ResetForHostGeneration(long pageGeneration)
        {
            if (router == null)
                return false;
            var reset = router.ResetForHostGeneration(pageGeneration);
            if (reset)
            {
                RotateOutboundOwner(false, "physical-generation");
                activeLease = null;
                Debug.Log("THREE_UNITY_LOGIC_TRANSPORT_RESET page=" + pageGeneration);
            }
            return reset;
        }

        private void Update()
        {
            if (router == null || router.CurrentModule == null || launcher == null)
                return;

            ObserveLauncherGeneration();

            var processed = 0;
            while (processed++ < 256
                && launcher.TryReceiveFromWeb(out var json, out ThreeUnityWebBridgeLease lease))
            {
                ProcessInboundWithLease(lease, json);
            }
            FlushOutgoing();
        }

        private void ProcessInboundWithLease(ThreeUnityWebBridgeLease lease, string json)
        {
            if (lease == null)
                return;
            var pageGeneration = lease.PageGeneration;
            if (pageGeneration > router.HostGeneration)
                ResetForHostGeneration(pageGeneration);
            if (!router.TryAcceptHostGeneration(pageGeneration))
                return;
            activeLease = lease;
            ProcessInbound(pageGeneration, json);
        }

        // Kept as a focused protocol boundary so generation-routing tests can
        // inject malformed traffic without constructing a physical transport.
        private void ProcessInbound(long pageGeneration, string json)
        {
            if (pageGeneration > router.HostGeneration)
                ResetForHostGeneration(pageGeneration);
            if (!router.TryAcceptHostGeneration(pageGeneration))
                return;
            if (!LogicEnvelopeParser.TryParseHeader(json, out var header, out var error))
            {
                Debug.LogWarning("THREE_UNITY_LOGIC_PROTOCOL_ERROR " + error);
                return;
            }
            try
            {
                var result = router.Handle(json, header);
                if (result == ThreeUnityLogicRouteResult.Restarted)
                {
                    var purged = RotateOutboundOwner(true, "logical-session");
                    Debug.Log("THREE_UNITY_LOGIC_SESSION_RESTART profile=" + logicProfile
                        + " restarts=" + router.SessionRestarts
                        + " outboundPurged=" + purged);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                router.ForceFallback("message-error");
            }
        }

        private void FixedUpdate()
        {
            if (router == null || router.CurrentModule == null)
                return;
            ObserveLauncherGeneration();
            fixedTicks++;
            try
            {
                router.FixedTick(Time.fixedDeltaTime);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                router.ForceFallback("tick-error");
            }
            FlushOutgoing();
            if (fixedTicks % 120 == 0)
                LogTransportMetrics();
        }

        private void FlushOutgoing()
        {
            if (launcher == null || router == null)
                return;
            if (activeLease == null
                || activeLease.PageGeneration != router.HostGeneration
                || !launcher.IsLeaseCurrent(activeLease))
            {
                if (!launcher.TryAcquireCurrentLease(out activeLease)
                    || activeLease.PageGeneration != router.HostGeneration)
                    return;
            }

            var sentThisFlush = 0;
            while (router != null && sentThisFlush < MaxOutgoingMessagesPerFlush)
            {
                if (!hasPendingOutgoing)
                {
                    if (!router.TryDequeueOutgoingMessage(out var outgoing))
                    {
                        RecordOutgoingFlush(sentThisFlush);
                        return;
                    }

                    pendingOutgoingJson = outgoing.Json;
                    hasPendingOutgoing = true;
                    pendingOutgoingOwner = activeOutboundOwner;
                    pendingOutgoingIsLatest = outgoing.IsLatestState;
                    pendingOutgoingStreamKey = outgoing.StreamKey;
                }

                var sent = pendingOutgoingIsLatest
                    ? launcher.SendLatestToWeb(
                        activeLease,
                        pendingOutgoingOwner,
                        pendingOutgoingStreamKey,
                        pendingOutgoingJson)
                    : launcher.SendToWeb(
                        activeLease,
                        pendingOutgoingOwner,
                        pendingOutgoingJson);
                if (!sent)
                {
                    // A full reliable queue or a lease retired between validation
                    // and enqueue must not consume the module's message. Keep this
                    // exact envelope at the head and retry it before dequeuing more.
                    activeLease = null;
                    RecordOutgoingFlush(sentThisFlush);
                    return;
                }

                ClearPendingOutgoing();
                sentThisFlush++;
            }
            RecordOutgoingFlush(sentThisFlush);
        }

        private void RecordOutgoingFlush(int sent)
        {
            if (sent > maxOutgoingMessagesPerFlush)
                maxOutgoingMessagesPerFlush = sent;
            if (sent >= MaxOutgoingMessagesPerFlush)
                outboundFlushBudgetStops++;
        }

        private void ClearPendingOutgoing()
        {
            hasPendingOutgoing = false;
            pendingOutgoingOwner = null;
            pendingOutgoingJson = null;
            pendingOutgoingIsLatest = false;
            pendingOutgoingStreamKey = null;
        }

        private void ObserveLauncherGeneration()
        {
            if (launcher != null && router != null && launcher.PageGeneration > router.HostGeneration)
                ResetForHostGeneration(launcher.PageGeneration);
        }

        private void LogTransportMetrics()
        {
            if (launcher == null)
                return;
            var metrics = launcher.GetTransportMetrics();
            var outbound = metrics.Outbound;
            var state = router?.GetStateEmissionMetrics();
            var collision = router?.GetCollisionMetrics();
            var input = router?.GetInputFreshnessMetrics();
            Debug.Log("THREE_UNITY_BRIDGE_PERF profile=" + (router?.CurrentModule?.Profile ?? logicProfile)
                + " writer=background"
                + " rx=" + metrics.WebMessagesReceived
                + " tx=" + metrics.UnityMessagesWritten
                + " rxChars=" + metrics.WebCharactersReceived
                + " txChars=" + metrics.UnityCharactersWritten
                + " coalesced=" + outbound.LatestCoalesced
                + " backpressure=" + outbound.ReliableBackpressureRejected
                + " dropped=" + outbound.ReliableDropped
                + " ownerPurged=" + outbound.OwnerPurged
                + " fairnessYields=" + outbound.ReliableBurstYields
                + " inPending=" + metrics.InboundPending
                + " outPending=" + (outbound.PendingReliable + outbound.PendingLatest)
                + " maxIn=" + metrics.MaxInboundPending
                + " maxOut=" + outbound.MaxPending
                + " stateEmitted=" + (state?.Emitted ?? 0)
                + " stateSuppressed=" + (state?.Suppressed ?? 0)
                + " heartbeats=" + (state?.Heartbeats ?? 0)
                + " stateRateLimited=" + (state?.RateLimited ?? 0)
                + " collisionFull=" + (collision?.FullMessages ?? 0)
                + " collisionDelta=" + (collision?.DeltaMessages ?? 0)
                + " collisionCells=" + (collision?.DeltaCells ?? 0)
                + " collisionResync=" + (collision?.ResyncRequests ?? 0)
                + " hostGeneration=" + (router?.HostGeneration ?? 0)
                + " transportResets=" + (router?.TransportResets ?? 0)
                + " generationRejected=" + (router?.GenerationRejected ?? 0)
                + " inboundOverflow=" + metrics.InboundOverflowDropped
                + " diagnosticOverflow=" + metrics.HostDiagnosticsOverflowDropped
                + " legacyRejected=" + metrics.LegacyGenerationlessRejected
                + " pageReady=" + (metrics.PageReady ? 1 : 0)
                + " bridgeReady=" + (metrics.BridgeReady ? 1 : 0)
                + " backoffReset=" + (metrics.BackoffReset ? 1 : 0)
                + " jobAssigned=" + metrics.JobAssignedProcesses
                + " jobActive=" + metrics.ActiveJobProcesses
                + " sessionRestarts=" + (router?.SessionRestarts ?? 0)
                + " logicDiscarded=" + (router?.RetiredOutgoingDiscarded ?? 0)
                + " sessionRejected=" + (router?.SessionRejected ?? 0)
                + " sequenceRejected=" + (router?.SequenceRejected ?? 0)
                + " metadataFast=" + (router?.OutgoingMetadataFastPath ?? 0)
                + " metadataFallback=" + (router?.OutgoingMetadataFallbackParses ?? 0)
                + " flushBudgetStops=" + outboundFlushBudgetStops
                + " maxFlush=" + maxOutgoingMessagesPerFlush
                + " lifecycleChanges=" + (router?.LifecycleChanges ?? 0)
                + " lifecycleEmitted=" + (router?.LifecycleEmitted ?? 0)
                + " lifecycleCoalesced=" + (router?.LifecycleCoalesced ?? 0)
                + " lifecycleAck=" + (router?.LifecycleAcknowledged ?? 0)
                + " lifecycleAckRejected=" + (router?.LifecycleAckRejected ?? 0)
                + " lifecycleActive=" + ((router?.ApplicationActive ?? true) ? 1 : 0)
                + " inputFresh=" + ((input?.Fresh ?? false) ? 1 : 0)
                + " inputAgeMs=" + Mathf.RoundToInt((input?.AgeSeconds ?? 0f) * 1000f)
                + " inputExpired=" + (input?.Expirations ?? 0)
                + " inputRecovered=" + (input?.Recoveries ?? 0)
                + " inputNeutralized=" + (input?.NeutralizedTicks ?? 0));
        }

        private void OnDestroy()
        {
            router?.Dispose();
            router = null;
            activeLease = null;
            activeOutboundOwner = null;
            ClearPendingOutgoing();
        }

        private int RotateOutboundOwner(bool purgeCurrentConnection, string reason)
        {
            var retiredOwner = activeOutboundOwner;
            var retainedPendingDiscarded = hasPendingOutgoing;
            var purged = 0;
            if (purgeCurrentConnection && retiredOwner != null && launcher != null)
            {
                var lease = activeLease;
                if ((lease == null || !launcher.IsLeaseCurrent(lease))
                    && launcher.TryAcquireCurrentLease(out var currentLease))
                    lease = currentLease;
                if (lease != null
                    && router != null
                    && lease.PageGeneration == router.HostGeneration)
                    purged = launcher.PurgeOutbound(lease, retiredOwner);
            }

            if (retainedPendingDiscarded && router != null)
                router.RecordRetainedOutgoingDiscarded();
            activeOutboundOwner = new object();
            ClearPendingOutgoing();
            if (purged > 0 || retainedPendingDiscarded)
            {
                Debug.Log("THREE_UNITY_LOGIC_OUTBOUND_EPOCH_RETIRED"
                    + " reason=" + reason
                    + " purged=" + purged
                    + " retainedPendingDiscarded=" + (retainedPendingDiscarded ? 1 : 0));
            }
            return purged;
        }

    }
}
