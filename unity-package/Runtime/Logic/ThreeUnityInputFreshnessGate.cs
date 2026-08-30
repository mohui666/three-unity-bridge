using System;

namespace ThreeUnity.Bridge.Logic
{
    /// <summary>
    /// Tracks a replaceable realtime input stream and expires retained controls
    /// when its producer stops sending heartbeats. This prevents a frozen web
    /// event loop from leaving movement, jump, or sprint held indefinitely.
    /// </summary>
    public sealed class ThreeUnityInputFreshnessGate
    {
        private readonly float timeoutSeconds;
        private bool hasReceived;
        private float ageSeconds;
        private long expirations;
        private long recoveries;

        public ThreeUnityInputFreshnessGate(float timeoutSeconds)
        {
            if (timeoutSeconds <= 0f || float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds))
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            this.timeoutSeconds = timeoutSeconds;
        }

        public bool IsFresh { get; private set; }

        /// <returns>True when this sample recovered an expired stream.</returns>
        public bool MarkReceived()
        {
            var recovered = hasReceived && !IsFresh;
            hasReceived = true;
            ageSeconds = 0f;
            IsFresh = true;
            if (recovered)
                recoveries++;
            return recovered;
        }

        public bool Advance(float deltaTime)
        {
            if (deltaTime <= 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
                throw new ArgumentOutOfRangeException(nameof(deltaTime));
            if (!hasReceived)
                return false;

            ageSeconds += deltaTime;
            if (IsFresh && ageSeconds >= timeoutSeconds)
            {
                IsFresh = false;
                expirations++;
            }
            return IsFresh;
        }

        public void ResetState()
        {
            hasReceived = false;
            ageSeconds = 0f;
            IsFresh = false;
        }

        public ThreeUnityInputFreshnessMetrics Snapshot()
        {
            return new ThreeUnityInputFreshnessMetrics
            {
                Fresh = IsFresh,
                AgeSeconds = ageSeconds,
                TimeoutSeconds = timeoutSeconds,
                Expirations = expirations,
                Recoveries = recoveries,
            };
        }
    }

    public sealed class ThreeUnityInputFreshnessMetrics
    {
        public bool Fresh { get; internal set; }
        public float AgeSeconds { get; internal set; }
        public float TimeoutSeconds { get; internal set; }
        public long Expirations { get; internal set; }
        public long Recoveries { get; internal set; }
        public long NeutralizedTicks { get; internal set; }
    }

    public interface IThreeUnityInputTelemetry
    {
        ThreeUnityInputFreshnessMetrics GetInputFreshnessMetrics();
    }
}
