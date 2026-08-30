using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeUnity.Bridge.Logic
{
    public sealed class ShopFlightLogicModule : IThreeUnityLogicModule,
        IThreeUnityLogicOutgoingMetadata,
        IThreeUnityLogicTelemetry
    {
        [Serializable]
        private sealed class HelloMessage { public HelloPayload payload; }
        [Serializable]
        private sealed class HelloPayload { public string gameId; public string[] capabilities; }
        [Serializable]
        private sealed class BootstrapMessage { public BootstrapPayload payload; }
        [Serializable]
        private sealed class BootstrapPayload
        {
            public int generation;
            public float time;
            public float amplitude;
            public bool flying;
        }
        [Serializable]
        private sealed class CommandMessage { public CommandPayload payload; }
        [Serializable]
        private sealed class CommandPayload
        {
            public int generation;
            public bool flying;
        }
        [Serializable]
        private sealed class ReadyPayload
        {
            public string profile;
            public float fixedDeltaTime;
            public string[] features;
        }
        [Serializable]
        private sealed class FlightStatePayload
        {
            public int generation;
            public float time;
            public float amplitude;
            public bool flying;
            public Vector3 position;
            public Vector3 rotation;
            public long tick;
            public long ackCommandSeq;
        }
        [Serializable]
        private sealed class FallbackPayload { public string reason; }

        private readonly Queue<ThreeUnityLogicOutgoingMessage> outgoing = new Queue<ThreeUnityLogicOutgoingMessage>();
        private readonly ShopFlightMotor motor = new ShopFlightMotor();
        private readonly ThreeUnityStateEmissionGate stateEmission = new ThreeUnityStateEmissionGate();
        private long outgoingSequence;
        private long latestBootstrapSequence = -1;
        private long latestCommandSequence = -1;
        private long tick;
        private int generation = -1;
        private bool helloAccepted;
        private bool bootstrapped;
        private bool fallback;
        private bool loggedReady;
        private bool hasEmittedState;
        private int lastGeneration;
        private float lastFlightTime;
        private float lastAmplitude;
        private bool lastFlying;
        private Vector3 lastPosition;
        private Vector3 lastRotation;
        private long lastAcknowledgedCommand = long.MinValue;
        private string sessionId;
        private bool sessionBound;
        private bool disposed;

        public string Profile => "shop-flight-v1";
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
                case "flight.bootstrap":
                    HandleBootstrap(json, header.seq);
                    break;
                case "flight.command":
                    HandleCommand(json, header.seq);
                    break;
                case "bridge.fallback":
                    ForceFallback("web-request");
                    break;
            }
        }

        public void FixedTick(float deltaTime)
        {
            if (fallback || disposed || !helloAccepted || !bootstrapped)
                return;

            motor.Step(deltaTime);
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
                || lastGeneration != generation
                || !Mathf.Approximately(lastFlightTime, motor.FlightTime)
                || !Mathf.Approximately(lastAmplitude, motor.Amplitude)
                || lastFlying != motor.Flying
                || lastPosition != motor.Position
                || lastRotation != motor.Rotation;
            var acknowledgementChanged = lastAcknowledgedCommand != latestCommandSequence;
            if (!stateEmission.ShouldEmit(deltaTime, stateChanged, acknowledgementChanged))
                return;

            Enqueue("flight.state", new FlightStatePayload
            {
                generation = generation,
                time = motor.FlightTime,
                amplitude = motor.Amplitude,
                flying = motor.Flying,
                position = motor.Position,
                rotation = motor.Rotation,
                tick = tick,
                ackCommandSeq = latestCommandSequence,
            });
            hasEmittedState = true;
            lastGeneration = generation;
            lastFlightTime = motor.FlightTime;
            lastAmplitude = motor.Amplitude;
            lastFlying = motor.Flying;
            lastPosition = motor.Position;
            lastRotation = motor.Rotation;
            lastAcknowledgedCommand = latestCommandSequence;
        }

        public ThreeUnityStateEmissionMetrics GetStateEmissionMetrics() => stateEmission.Snapshot();

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
                    ThreeUnityLogicFeatures.SessionRestart,
                    ThreeUnityLogicFeatures.RuntimeLifecycle,
                },
            });
        }

        private void HandleBootstrap(string json, long sequence)
        {
            if (sequence <= latestBootstrapSequence)
                return;
            var payload = JsonUtility.FromJson<BootstrapMessage>(json)?.payload;
            if (payload == null)
                throw new ArgumentException("flight.bootstrap payload is missing.");
            if (payload.generation < 0)
                throw new ArgumentOutOfRangeException(nameof(payload.generation));

            motor.Initialize(payload.time, payload.amplitude, payload.flying);
            generation = payload.generation;
            latestBootstrapSequence = sequence;
            latestCommandSequence = -1;
            bootstrapped = true;
            IsAuthoritative = false;
            hasEmittedState = false;
            lastAcknowledgedCommand = long.MinValue;
            stateEmission.ResetState();
        }

        private void HandleCommand(string json, long sequence)
        {
            if (sequence <= latestCommandSequence)
                return;
            var payload = JsonUtility.FromJson<CommandMessage>(json)?.payload;
            if (payload == null)
                throw new ArgumentException("flight.command payload is missing.");
            if (!bootstrapped || payload.generation != generation)
                return;

            motor.SetFlying(payload.flying);
            latestCommandSequence = sequence;
            Debug.Log("THREE_UNITY_FLIGHT_COMMAND profile=" + Profile
                + " generation=" + generation
                + " flying=" + payload.flying
                + " seq=" + sequence);
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
