using System;

namespace ThreeUnity.Bridge.Logic
{
    /// <summary>
    /// Reusable state-stream policy: publish every meaningful simulation change,
    /// suppress identical fixed-tick snapshots, and retain a watchdog-safe heartbeat.
    /// </summary>
    public sealed class ThreeUnityStateEmissionGate
    {
        private readonly float heartbeatSeconds;
        private readonly float minimumIntervalSeconds;
        private bool hasEmitted;
        private bool wasStateChanging;
        private float elapsedSinceEmission;
        private float changeBudgetSeconds;
        private long emitted;
        private long suppressed;
        private long heartbeats;
        private long rateLimited;

        public ThreeUnityStateEmissionGate(float heartbeatSeconds = 0.2f, float minimumIntervalSeconds = 0f)
        {
            if (!IsFinite(heartbeatSeconds) || heartbeatSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(heartbeatSeconds));
            if (!IsFinite(minimumIntervalSeconds) || minimumIntervalSeconds < 0f
                || minimumIntervalSeconds > heartbeatSeconds)
                throw new ArgumentOutOfRangeException(nameof(minimumIntervalSeconds));
            this.heartbeatSeconds = heartbeatSeconds;
            this.minimumIntervalSeconds = minimumIntervalSeconds;
        }

        public bool ShouldEmit(float deltaTime, bool stateChanged, bool force = false)
        {
            if (!IsFinite(deltaTime) || deltaTime <= 0f)
                throw new ArgumentOutOfRangeException(nameof(deltaTime));

            elapsedSinceEmission += deltaTime;
            if (minimumIntervalSeconds <= 0f)
                changeBudgetSeconds = 0f;
            else if (!stateChanged)
                changeBudgetSeconds = minimumIntervalSeconds;
            else if (!wasStateChanging)
                changeBudgetSeconds = minimumIntervalSeconds;
            else
                changeBudgetSeconds = Math.Min(heartbeatSeconds, changeBudgetSeconds + deltaTime);
            var heartbeatDue = hasEmitted && elapsedSinceEmission + 0.000001f >= heartbeatSeconds;
            var changedStateDue = stateChanged
                && (minimumIntervalSeconds <= 0f
                    || changeBudgetSeconds + 0.000001f >= minimumIntervalSeconds);
            if (!hasEmitted || changedStateDue || force || heartbeatDue)
            {
                var wasInitialized = hasEmitted;
                hasEmitted = true;
                elapsedSinceEmission = 0f;
                if (!wasInitialized || force)
                    changeBudgetSeconds = 0f;
                else if (changedStateDue && minimumIntervalSeconds > 0f)
                    changeBudgetSeconds = Math.Max(0f, changeBudgetSeconds - minimumIntervalSeconds);
                emitted++;
                if (heartbeatDue && !stateChanged && !force)
                    heartbeats++;
                wasStateChanging = stateChanged;
                return true;
            }

            suppressed++;
            if (stateChanged && !changedStateDue)
                rateLimited++;
            wasStateChanging = stateChanged;
            return false;
        }

        /// <summary>
        /// Makes the next eligible fixed tick publish immediately without discarding
        /// lifetime counters. Use after a new bootstrap or authority invalidation.
        /// </summary>
        public void ResetState()
        {
            hasEmitted = false;
            elapsedSinceEmission = 0f;
            changeBudgetSeconds = 0f;
            wasStateChanging = false;
        }

        public ThreeUnityStateEmissionMetrics Snapshot()
        {
            return new ThreeUnityStateEmissionMetrics
            {
                Emitted = emitted,
                Suppressed = suppressed,
                Heartbeats = heartbeats,
                RateLimited = rateLimited,
                HeartbeatSeconds = heartbeatSeconds,
                MinimumIntervalSeconds = minimumIntervalSeconds,
            };
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public sealed class ThreeUnityStateEmissionMetrics
    {
        public long Emitted { get; internal set; }
        public long Suppressed { get; internal set; }
        public long Heartbeats { get; internal set; }
        public long RateLimited { get; internal set; }
        public float HeartbeatSeconds { get; internal set; }
        public float MinimumIntervalSeconds { get; internal set; }
    }

    public interface IThreeUnityLogicTelemetry
    {
        ThreeUnityStateEmissionMetrics GetStateEmissionMetrics();
    }
}
