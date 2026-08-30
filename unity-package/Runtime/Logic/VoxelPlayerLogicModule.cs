using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeUnity.Bridge.Logic
{
    public sealed class VoxelPlayerLogicModule : IThreeUnityLogicModule,
        IThreeUnityLogicOutgoingMetadata,
        IThreeUnityLogicTelemetry,
        IThreeUnityCollisionTelemetry,
        IThreeUnityInputTelemetry
    {
        public const string CollisionDeltaFeature = "collision-delta-v2";

        [Serializable]
        private sealed class HelloMessage { public HelloPayload payload; }
        [Serializable]
        private sealed class HelloPayload { public string gameId; public string[] capabilities; }
        [Serializable]
        private sealed class BootstrapMessage { public BootstrapPayload payload; }
        [Serializable]
        private sealed class BootstrapPayload
        {
            public Vector3 position;
            public Vector3 velocity;
            public float yaw;
            public float pitch;
            public float speed;
            public float sprintSpeed;
            public float flySpeed;
            public float gravity;
            public float jumpStrength;
            public float waterJumpStrength;
            public float width;
            public float height;
            public float eyeHeight;
            public float collisionTolerance;
            public bool flying;
        }
        [Serializable]
        private sealed class CollisionMessage { public CollisionPayload payload; }
        [Serializable]
        private sealed class CollisionPayload
        {
            public int revision;
            public Vector3Int origin;
            public Vector3Int size;
            public string solidBits;
            public string fluidBits;
        }
        [Serializable]
        private sealed class CollisionDeltaMessage { public CollisionDeltaPayload payload; }
        [Serializable]
        private sealed class CollisionDeltaPayload
        {
            public int baseRevision;
            public int revision;
            public Vector3Int origin;
            public Vector3Int size;
            public int changeCount;
            public string changes;
        }
        [Serializable]
        private sealed class InputMessage { public InputPayload payload; }
        [Serializable]
        private sealed class InputPayload
        {
            public float moveX;
            public float moveZ;
            public float yaw;
            public float pitch;
            public bool jumpHeld;
            public bool sprintHeld;
            public bool flyToggle;
        }
        [Serializable]
        private sealed class ReadyPayload
        {
            public string profile;
            public float fixedDeltaTime;
            public string[] features;
            public float stateRateHz;
        }
        [Serializable]
        private sealed class PlayerStatePayload
        {
            public Vector3 position;
            public Vector3 velocity;
            public float yaw;
            public float pitch;
            public bool flying;
            public bool onGround;
            public bool inFluid;
            public bool isSprinting;
            public long tick;
            public long ackInputSeq;
        }
        [Serializable]
        private sealed class FallbackPayload { public string reason; }
        [Serializable]
        private sealed class CollisionResyncPayload { public int revision; }

        private readonly Queue<ThreeUnityLogicOutgoingMessage> outgoing = new Queue<ThreeUnityLogicOutgoingMessage>();
        private readonly VoxelCollisionWindow collision = new VoxelCollisionWindow();
        private readonly VoxelPlayerMotor motor = new VoxelPlayerMotor();
        private readonly ThreeUnityStateEmissionGate stateEmission = new ThreeUnityStateEmissionGate(0.2f, 1f / 30f);
        private readonly ThreeUnityInputFreshnessGate inputFreshness = new ThreeUnityInputFreshnessGate(0.5f);
        private VoxelPlayerInput latestInput;
        private long latestInputSequence = -1;
        private long outgoingSequence;
        private long tick;
        private bool helloAccepted;
        private bool bootstrapped;
        private bool collisionReady;
        private bool fallback;
        private bool loggedReady;
        private bool hasEmittedState;
        private Vector3 lastPosition;
        private Vector3 lastVelocity;
        private float lastYaw;
        private float lastPitch;
        private bool lastFlying;
        private bool lastOnGround;
        private bool lastInFluid;
        private bool lastIsSprinting;
        private long collisionFullMessages;
        private long collisionDeltaMessages;
        private long collisionDeltaCells;
        private long collisionResyncRequests;
        private long neutralizedInputTicks;
        private string sessionId;
        private bool sessionBound;
        private bool disposed;

        public string Profile => "voxel-player-v1";
        public string SessionId => sessionId;
        public bool IsAuthoritative { get; private set; }
        public bool IsFallback => fallback;

        public void BindSession(string value)
        {
            var normalized = NormalizeSessionId(value);
            if (sessionBound)
            {
                if (!string.Equals(sessionId, normalized, StringComparison.Ordinal))
                    throw new InvalidOperationException("A logic module cannot be rebound to another session.");
                return;
            }

            sessionId = normalized;
            sessionBound = true;
        }

        public void Handle(string json, LogicEnvelopeHeader header)
        {
            if (fallback || disposed || header == null)
                return;
            if (sessionBound
                && !string.Equals(sessionId, NormalizeSessionId(header.sessionId), StringComparison.Ordinal))
                return;

            switch (header.type)
            {
                case "bridge.hello":
                    HandleHello(json, header);
                    break;
                case "player.bootstrap":
                    HandleBootstrap(json);
                    break;
                case "world.collision":
                    HandleCollision(json);
                    break;
                case "world.collision.delta":
                    HandleCollisionDelta(json);
                    break;
                case "player.input":
                    HandleInput(json, header.seq);
                    break;
                case "world.invalidate":
                    collisionReady = false;
                    IsAuthoritative = false;
                    hasEmittedState = false;
                    stateEmission.ResetState();
                    break;
                case "bridge.fallback":
                    ForceFallback("web-request");
                    break;
            }
        }

        public void FixedTick(float deltaTime)
        {
            if (fallback || disposed || !helloAccepted || !bootstrapped || !collisionReady)
                return;

            var wasInputFresh = inputFreshness.IsFresh;
            var motorInput = latestInput;
            if (!inputFreshness.Advance(deltaTime))
            {
                motorInput.MoveX = 0f;
                motorInput.MoveZ = 0f;
                motorInput.JumpHeld = false;
                motorInput.SprintHeld = false;
                motorInput.FlyToggle = false;
                neutralizedInputTicks++;
            }
            if (wasInputFresh && !inputFreshness.IsFresh)
                Debug.LogWarning("THREE_UNITY_INPUT_STALE profile=" + Profile + " action=neutralize");

            motor.Step(motorInput, deltaTime, collision);
            latestInput.FlyToggle = false;
            IsAuthoritative = true;
            tick++;
            if (!loggedReady)
            {
                loggedReady = true;
                Debug.Log("THREE_UNITY_LOGIC_READY profile=" + Profile);
            }
            if (tick % 120 == 0)
                Debug.Log("THREE_UNITY_LOGIC_TICK profile=" + Profile + " ticks=" + tick);

            var stateChanged = !hasEmittedState
                || lastPosition != motor.Position
                || lastVelocity != motor.Velocity
                || !Mathf.Approximately(lastYaw, motor.Yaw)
                || !Mathf.Approximately(lastPitch, motor.Pitch)
                || lastFlying != motor.Flying
                || lastOnGround != motor.OnGround
                || lastInFluid != motor.InFluid
                || lastIsSprinting != motor.IsSprinting;
            if (!stateEmission.ShouldEmit(deltaTime, stateChanged))
                return;

            Enqueue("player.state", new PlayerStatePayload
            {
                position = motor.Position,
                velocity = motor.Velocity,
                yaw = motor.Yaw,
                pitch = motor.Pitch,
                flying = motor.Flying,
                onGround = motor.OnGround,
                inFluid = motor.InFluid,
                isSprinting = motor.IsSprinting,
                tick = tick,
                ackInputSeq = latestInputSequence,
            });
            hasEmittedState = true;
            lastPosition = motor.Position;
            lastVelocity = motor.Velocity;
            lastYaw = motor.Yaw;
            lastPitch = motor.Pitch;
            lastFlying = motor.Flying;
            lastOnGround = motor.OnGround;
            lastInFluid = motor.InFluid;
            lastIsSprinting = motor.IsSprinting;
        }

        public ThreeUnityStateEmissionMetrics GetStateEmissionMetrics() => stateEmission.Snapshot();

        public ThreeUnityInputFreshnessMetrics GetInputFreshnessMetrics()
        {
            var metrics = inputFreshness.Snapshot();
            metrics.NeutralizedTicks = neutralizedInputTicks;
            return metrics;
        }

        public ThreeUnityCollisionMetrics GetCollisionMetrics()
        {
            return new ThreeUnityCollisionMetrics
            {
                FullMessages = collisionFullMessages,
                DeltaMessages = collisionDeltaMessages,
                DeltaCells = collisionDeltaCells,
                ResyncRequests = collisionResyncRequests,
            };
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

        public bool TryDequeueOutgoingMessage(out ThreeUnityLogicOutgoingMessage message)
        {
            if (outgoing.Count == 0)
            {
                message = default;
                return false;
            }
            message = outgoing.Dequeue();
            return true;
        }

        public void ForceFallback(string reason)
        {
            if (fallback || disposed)
                return;
            fallback = true;
            IsAuthoritative = false;
            outgoing.Clear();
            Enqueue("bridge.fallback", new FallbackPayload { reason = reason ?? "unknown" });
            Debug.LogWarning("THREE_UNITY_LOGIC_FALLBACK profile=" + Profile + " reason=" + reason);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            fallback = true;
            IsAuthoritative = false;
            outgoing.Clear();
        }

        private void HandleHello(string json, LogicEnvelopeHeader header)
        {
            BindSession(header.sessionId);
            var message = JsonUtility.FromJson<HelloMessage>(json);
            var capabilities = message?.payload?.capabilities;
            if (capabilities == null || Array.IndexOf(capabilities, Profile) < 0)
            {
                ForceFallback("capability-not-advertised");
                return;
            }
            helloAccepted = true;
            Enqueue("bridge.ready", new ReadyPayload
            {
                profile = Profile,
                fixedDeltaTime = Time.fixedDeltaTime,
                features = new[]
                {
                    CollisionDeltaFeature,
                    ThreeUnityLogicFeatures.SessionRestart,
                    ThreeUnityLogicFeatures.RuntimeLifecycle,
                },
                stateRateHz = 30f,
            });
        }

        private void HandleBootstrap(string json)
        {
            var payload = JsonUtility.FromJson<BootstrapMessage>(json)?.payload;
            if (payload == null)
                throw new ArgumentException("player.bootstrap payload is missing.");
            motor.Initialize(new VoxelPlayerBootstrap
            {
                Position = payload.position,
                Velocity = payload.velocity,
                Yaw = payload.yaw,
                Pitch = payload.pitch,
                Speed = payload.speed,
                SprintSpeed = payload.sprintSpeed,
                FlySpeed = payload.flySpeed,
                Gravity = payload.gravity,
                JumpStrength = payload.jumpStrength,
                WaterJumpStrength = payload.waterJumpStrength,
                Width = payload.width,
                Height = payload.height,
                EyeHeight = payload.eyeHeight,
                CollisionTolerance = payload.collisionTolerance,
                Flying = payload.flying,
            });
            latestInput = new VoxelPlayerInput { Yaw = payload.yaw, Pitch = payload.pitch };
            inputFreshness.ResetState();
            bootstrapped = true;
            IsAuthoritative = false;
            hasEmittedState = false;
            stateEmission.ResetState();
        }

        private void HandleCollision(string json)
        {
            var payload = JsonUtility.FromJson<CollisionMessage>(json)?.payload;
            if (payload == null)
                throw new ArgumentException("world.collision payload is missing.");
            if (collision.Replace(payload.revision, payload.origin, payload.size, payload.solidBits, payload.fluidBits))
                collisionFullMessages++;
            collisionReady = collision.Revision >= 0;
        }

        private void HandleCollisionDelta(string json)
        {
            var payload = JsonUtility.FromJson<CollisionDeltaMessage>(json)?.payload;
            if (payload == null)
                throw new ArgumentException("world.collision.delta payload is missing.");
            var result = collision.ApplyDelta(
                payload.baseRevision,
                payload.revision,
                payload.origin,
                payload.size,
                payload.changeCount,
                payload.changes);
            if (result == CollisionDeltaApplyResult.Applied)
            {
                collisionDeltaMessages++;
                collisionDeltaCells += payload.changeCount;
                collisionReady = true;
                return;
            }
            if (result != CollisionDeltaApplyResult.BaseMismatch)
                return;

            collisionReady = false;
            IsAuthoritative = false;
            collisionResyncRequests++;
            Enqueue("world.collision.resync", new CollisionResyncPayload { revision = collision.Revision });
            Debug.LogWarning("THREE_UNITY_COLLISION_RESYNC profile=" + Profile
                + " have=" + collision.Revision
                + " deltaBase=" + payload.baseRevision
                + " deltaRevision=" + payload.revision);
        }

        private void HandleInput(string json, long sequence)
        {
            if (sequence <= latestInputSequence)
                return;
            var payload = JsonUtility.FromJson<InputMessage>(json)?.payload;
            if (payload == null)
                throw new ArgumentException("player.input payload is missing.");
            latestInput = new VoxelPlayerInput
            {
                MoveX = payload.moveX,
                MoveZ = payload.moveZ,
                Yaw = payload.yaw,
                Pitch = payload.pitch,
                JumpHeld = payload.jumpHeld,
                SprintHeld = payload.sprintHeld,
                FlyToggle = payload.flyToggle,
            };
            if (inputFreshness.MarkReceived())
                Debug.Log("THREE_UNITY_INPUT_RECOVERED profile=" + Profile + " seq=" + sequence);
            latestInputSequence = sequence;
        }

        private void Enqueue(string type, object payload)
        {
            outgoing.Enqueue(LogicEnvelopeWriter.EncodeMessage(
                type,
                outgoingSequence++,
                sessionId,
                payload));
        }

        private static string NormalizeSessionId(string value)
        {
            if (value == null)
                return null;
            if (string.IsNullOrWhiteSpace(value)
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || value.Length > LogicEnvelopeParser.MaxSessionIdLength)
                throw new ArgumentException("Session id is invalid.", nameof(value));
            return value;
        }
    }
}
