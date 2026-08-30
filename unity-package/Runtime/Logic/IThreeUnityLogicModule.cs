using System;

namespace ThreeUnity.Bridge.Logic
{
    public interface IThreeUnityLogicModule : IDisposable
    {
        string Profile { get; }
        string SessionId { get; }
        bool IsAuthoritative { get; }
        bool IsFallback { get; }
        void BindSession(string sessionId);
        void Handle(string json, LogicEnvelopeHeader header);
        void FixedTick(float deltaTime);
        bool TryDequeueOutgoing(out string json);
        void ForceFallback(string reason);
    }

    public static class ThreeUnityLogicFeatures
    {
        public const string SessionRestart = "session-restart-v1";
    }

    public sealed class ThreeUnityCollisionMetrics
    {
        public long FullMessages { get; internal set; }
        public long DeltaMessages { get; internal set; }
        public long DeltaCells { get; internal set; }
        public long ResyncRequests { get; internal set; }
    }

    public interface IThreeUnityCollisionTelemetry
    {
        ThreeUnityCollisionMetrics GetCollisionMetrics();
    }

    public static class ThreeUnityLogicModuleRegistry
    {
        public static IThreeUnityLogicModule Create(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile))
                return null;
            if (string.Equals(profile, "voxel-player-v1", StringComparison.Ordinal))
                return new VoxelPlayerLogicModule();
            if (string.Equals(profile, "shop-flight-v1", StringComparison.Ordinal))
                return new ShopFlightLogicModule();
            throw new ArgumentException("Unsupported logic profile '" + profile + "'.", nameof(profile));
        }
    }
}
