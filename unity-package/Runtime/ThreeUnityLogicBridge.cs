using System;
using ThreeUnity.Bridge.Logic;
using UnityEngine;

namespace ThreeUnity.Bridge
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ThreeUnityWebBridgeLauncher))]
    public sealed class ThreeUnityLogicBridge : MonoBehaviour
    {
        [SerializeField] private ThreeUnityWebBridgeLauncher launcher;
        [SerializeField] private string logicProfile;

        private ThreeUnityLogicSessionRouter router;
        private ThreeUnityWebBridgeLease activeLease;
        private bool hasPendingOutgoing;
        private string pendingOutgoingJson;
        private bool pendingOutgoingIsLatest;
        private string pendingOutgoingStreamKey;
        private long fixedTicks;

        public void Configure(ThreeUnityWebBridgeLauncher webLauncher, string profile)
        {
            launcher = webLauncher;
            logicProfile = profile;
        }

        private void Awake()
        {
            if (launcher == null)
                launcher = GetComponent<ThreeUnityWebBridgeLauncher>();
            router = new ThreeUnityLogicSessionRouter(
                logicProfile,
                initialHostGeneration: launcher == null ? 0 : launcher.PageGeneration);
            if (router.CurrentModule == null)
                enabled = false;
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
                activeLease = null;
                ClearPendingOutgoing();
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
                    Debug.Log("THREE_UNITY_LOGIC_SESSION_RESTART profile=" + logicProfile
                        + " restarts=" + router.SessionRestarts);
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

            while (router != null)
            {
                if (!hasPendingOutgoing)
                {
                    if (!router.TryDequeueOutgoing(out pendingOutgoingJson))
                        return;

                    hasPendingOutgoing = true;
                    pendingOutgoingIsLatest = LogicEnvelopeParser.TryParseHeader(
                            pendingOutgoingJson,
                            out var header,
                            out _)
                        && header.type.EndsWith(".state", StringComparison.Ordinal);
                    pendingOutgoingStreamKey = pendingOutgoingIsLatest
                        ? LatestStreamKey(header)
                        : null;
                }

                var sent = pendingOutgoingIsLatest
                    ? launcher.SendLatestToWeb(
                        activeLease,
                        pendingOutgoingStreamKey,
                        pendingOutgoingJson)
                    : launcher.SendToWeb(activeLease, pendingOutgoingJson);
                if (!sent)
                {
                    // A full reliable queue or a lease retired between validation
                    // and enqueue must not consume the module's message. Keep this
                    // exact envelope at the head and retry it before dequeuing more.
                    activeLease = null;
                    return;
                }

                ClearPendingOutgoing();
            }
        }

        private void ClearPendingOutgoing()
        {
            hasPendingOutgoing = false;
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
                + " dropped=" + outbound.ReliableDropped
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
                + " sessionRejected=" + (router?.SessionRejected ?? 0)
                + " sequenceRejected=" + (router?.SequenceRejected ?? 0)
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
            ClearPendingOutgoing();
        }

        private static string LatestStreamKey(LogicEnvelopeHeader header)
        {
            return string.IsNullOrWhiteSpace(header.sessionId)
                ? header.type
                : header.sessionId + ":" + header.type;
        }
    }
}
