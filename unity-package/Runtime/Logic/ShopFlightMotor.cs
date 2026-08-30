using System;
using UnityEngine;

namespace ThreeUnity.Bridge.Logic
{
    public sealed class ShopFlightMotor
    {
        private const float TakeoffDuration = 2.2f;
        private const float LandingDuration = 2f;
        private const float TimeScale = 0.55f;

        private bool initialized;
        private bool ramping;
        private float rampFrom;
        private float rampTo;
        private float rampElapsed;
        private float rampDuration;

        public float FlightTime { get; private set; }
        public float Amplitude { get; private set; }
        public bool Flying { get; private set; }
        public Vector3 Position { get; private set; }
        public Vector3 Rotation { get; private set; }

        public void Initialize(float flightTime, float amplitude, bool flying)
        {
            if (!IsFinite(flightTime) || flightTime < 0f)
                throw new ArgumentOutOfRangeException(nameof(flightTime));
            if (!IsFinite(amplitude) || amplitude < 0f || amplitude > 1f)
                throw new ArgumentOutOfRangeException(nameof(amplitude));

            FlightTime = flightTime;
            Amplitude = amplitude;
            Flying = flying;
            initialized = true;
            ConfigureRampIfNeeded();
            UpdatePose();
        }

        public void SetFlying(bool flying)
        {
            EnsureInitialized();
            if (Flying == flying && (ramping || Mathf.Approximately(Amplitude, flying ? 1f : 0f)))
                return;

            var wasFlying = Flying;
            Flying = flying;
            if (flying && !wasFlying)
                FlightTime = 0f;
            BeginRamp(flying ? 1f : 0f, flying ? TakeoffDuration : LandingDuration);
        }

        public void Step(float deltaTime)
        {
            EnsureInitialized();
            if (!IsFinite(deltaTime) || deltaTime <= 0f)
                throw new ArgumentOutOfRangeException(nameof(deltaTime));

            if (ramping)
            {
                rampElapsed = Mathf.Min(rampDuration, rampElapsed + deltaTime);
                var progress = rampDuration <= 0f ? 1f : rampElapsed / rampDuration;
                var eased = 0.5f - Mathf.Cos(Mathf.PI * progress) * 0.5f;
                Amplitude = Mathf.LerpUnclamped(rampFrom, rampTo, eased);
                if (progress >= 1f)
                {
                    Amplitude = rampTo;
                    ramping = false;
                }
            }

            if (Flying || Amplitude > 0.001f)
            {
                FlightTime += deltaTime * TimeScale;
                UpdatePose();
            }
            else
            {
                Amplitude = 0f;
                Position = Vector3.zero;
                Rotation = Vector3.zero;
            }
        }

        private void ConfigureRampIfNeeded()
        {
            var target = Flying ? 1f : 0f;
            if (Mathf.Approximately(Amplitude, target))
            {
                ramping = false;
                return;
            }
            BeginRamp(target, Flying ? TakeoffDuration : LandingDuration);
        }

        private void BeginRamp(float target, float duration)
        {
            rampFrom = Amplitude;
            rampTo = target;
            rampElapsed = 0f;
            rampDuration = duration;
            ramping = !Mathf.Approximately(rampFrom, rampTo);
            if (!ramping) Amplitude = target;
        }

        private void UpdatePose()
        {
            if (!Flying && Amplitude <= 0.001f)
            {
                Position = Vector3.zero;
                Rotation = Vector3.zero;
                return;
            }

            var x = (Mathf.Sin(FlightTime * 0.42f) * 9f + Mathf.Sin(FlightTime * 0.17f) * 4f) * Amplitude;
            var z = (Mathf.Cos(FlightTime * 0.33f) * 8f + Mathf.Cos(FlightTime * 0.21f) * 3.5f) * Amplitude;
            var y = (12f + Mathf.Sin(FlightTime * 0.5f) * 2.2f) * Amplitude;
            Position = new Vector3(x, y, z);
            Rotation = new Vector3(
                Mathf.Cos(FlightTime * 0.3f) * 0.03f * Amplitude,
                0f,
                Mathf.Sin(FlightTime * 0.4f) * 0.05f * Amplitude);
        }

        private void EnsureInitialized()
        {
            if (!initialized)
                throw new InvalidOperationException("Shop flight motor must be initialized before use.");
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
