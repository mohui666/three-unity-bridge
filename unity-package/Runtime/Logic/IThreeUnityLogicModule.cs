using System;

namespace ThreeUnity.Bridge.Logic
{
    /// <summary>
    /// Carries transport classification beside an already serialized envelope.
    /// Modules that produced the envelope already know these fields, so the
    /// bridge does not need to parse its own JSON again on Unity's main thread.
    /// </summary>
    public readonly struct ThreeUnityLogicOutgoingMessage
    {
        public ThreeUnityLogicOutgoingMessage(string json, string type, string sessionId)
        {
            Json = json;
            Type = type;
            SessionId = sessionId;
        }

        public string Json { get; }
        public string Type { get; }
        public string SessionId { get; }
        public bool IsLatestState => Type != null
            && Type.EndsWith(".state", StringComparison.Ordinal);
        public string StreamKey => !IsLatestState
            ? null
            : string.IsNullOrWhiteSpace(SessionId)
                ? Type
                : SessionId + ":" + Type;
    }

    /// <summary>
    /// Optional zero-reparse fast path. Third-party modules can keep implementing
    /// only IThreeUnityLogicModule; the router falls back to header parsing for
    /// their legacy string queue.
    /// </summary>
    public interface IThreeUnityLogicOutgoingMetadata
    {
        bool TryDequeueOutgoingMessage(out ThreeUnityLogicOutgoingMessage message);
    }

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
        public const string RuntimeLifecycle = "runtime-lifecycle-v1";
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
