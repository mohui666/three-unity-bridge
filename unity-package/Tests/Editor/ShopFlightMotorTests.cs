using NUnit.Framework;
using ThreeUnity.Bridge.Logic;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ShopFlightMotorTests
    {
        [Test]
        public void TakeoffUsesOriginalEaseAndOrbitEquations()
        {
            var motor = CreateMotor();
            motor.SetFlying(true);

            motor.Step(1.1f);

            Assert.That(motor.Amplitude, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(motor.FlightTime, Is.EqualTo(0.605f).Within(0.0001f));
            Assert.That(motor.Position.x, Is.EqualTo(
                (Mathf.Sin(0.605f * 0.42f) * 9f + Mathf.Sin(0.605f * 0.17f) * 4f) * 0.5f
            ).Within(0.0001f));
            Assert.That(motor.Position.z, Is.EqualTo(
                (Mathf.Cos(0.605f * 0.33f) * 8f + Mathf.Cos(0.605f * 0.21f) * 3.5f) * 0.5f
            ).Within(0.0001f));
            Assert.That(motor.Position.y, Is.EqualTo(
                (12f + Mathf.Sin(0.605f * 0.5f) * 2.2f) * 0.5f
            ).Within(0.0001f));
            Assert.That(motor.Rotation.z, Is.EqualTo(Mathf.Sin(0.605f * 0.4f) * 0.05f * 0.5f).Within(0.0001f));
            Assert.That(motor.Rotation.x, Is.EqualTo(Mathf.Cos(0.605f * 0.3f) * 0.03f * 0.5f).Within(0.0001f));
        }

        [Test]
        public void LandingContinuesFromCurrentAmplitudeWithoutJumping()
        {
            var motor = CreateMotor(time: 2f, amplitude: 0.35f, flying: true);
            motor.SetFlying(false);

            motor.Step(1f);

            Assert.That(motor.Amplitude, Is.EqualTo(0.175f).Within(0.0001f));
            Assert.That(motor.Flying, Is.False);
            Assert.That(motor.Position.y, Is.GreaterThan(0f));
        }

        [Test]
        public void ReversingARampStartsFromTheCurrentAmplitude()
        {
            var motor = CreateMotor();
            motor.SetFlying(true);
            motor.Step(0.55f);
            var partial = motor.Amplitude;

            motor.SetFlying(false);
            motor.Step(0.5f);

            Assert.That(partial, Is.EqualTo(0.1464466f).Within(0.0001f));
            Assert.That(motor.Amplitude, Is.LessThan(partial));
            Assert.That(motor.Amplitude, Is.GreaterThan(0f));
        }

        [Test]
        public void FinishedLandingResetsTheRootPose()
        {
            var motor = CreateMotor(time: 3f, amplitude: 1f, flying: true);
            motor.SetFlying(false);

            motor.Step(2f);

            Assert.That(motor.Amplitude, Is.EqualTo(0f));
            Assert.That(motor.Position, Is.EqualTo(Vector3.zero));
            Assert.That(motor.Rotation, Is.EqualTo(Vector3.zero));
        }

        private static ShopFlightMotor CreateMotor(float time = 0f, float amplitude = 0f, bool flying = false)
        {
            var motor = new ShopFlightMotor();
            motor.Initialize(time, amplitude, flying);
            return motor;
        }
    }
}
