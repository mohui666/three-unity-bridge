using System;
using NUnit.Framework;
using ThreeUnity.Bridge.Logic;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class VoxelPlayerMotorTests
    {
        private sealed class FakeCollision : IVoxelCollisionSource
        {
            public Func<int, int, int, bool> Solid { get; set; } = (_, _, _) => false;
            public Func<int, int, int, bool> Fluid { get; set; } = (_, _, _) => false;

            public bool IsSolid(int x, int y, int z, bool flying) => Solid(x, y, z);
            public bool IsFluid(int x, int y, int z) => Fluid(x, y, z);
        }

        [Test]
        public void WalkingUsesYawAndNormalizesDiagonalInput()
        {
            var forward = CreateMotor();
            forward.Step(new VoxelPlayerInput { MoveZ = 1f, Yaw = 0f }, 0.02f, new FakeCollision());
            Assert.That(forward.Velocity.x, Is.EqualTo(0f).Within(0.001f));
            Assert.That(forward.Velocity.z, Is.EqualTo(-5f).Within(0.001f));

            var diagonal = CreateMotor();
            diagonal.Step(new VoxelPlayerInput { MoveX = 1f, MoveZ = 1f }, 0.02f, new FakeCollision());
            Assert.That(new Vector2(diagonal.Velocity.x, diagonal.Velocity.z).magnitude,
                Is.EqualTo(5f).Within(0.001f));
        }

        [Test]
        public void SprintSelectsBootstrapSprintSpeed()
        {
            var motor = CreateMotor();
            motor.Step(new VoxelPlayerInput { MoveZ = 1f, SprintHeld = true }, 0.02f, new FakeCollision());

            Assert.That(new Vector2(motor.Velocity.x, motor.Velocity.z).magnitude,
                Is.EqualTo(8f).Within(0.001f));
            Assert.That(motor.IsSprinting, Is.True);
        }

        [Test]
        public void GravityLandsOnAFlatFloor()
        {
            var floor = new FakeCollision { Solid = (_, y, _) => y <= 0 };
            var motor = CreateMotor(y: 4f);

            for (var step = 0; step < 200 && !motor.OnGround; step++)
                motor.Step(default, 0.02f, floor);

            Assert.That(motor.OnGround, Is.True);
            Assert.That(motor.Position.y, Is.EqualTo(2.65f).Within(0.001f));
            Assert.That(motor.Velocity.y, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void GroundedJumpFiresOnceWhileJumpIsHeld()
        {
            var floor = new FakeCollision { Solid = (_, y, _) => y <= 0 };
            var motor = CreateMotor(y: 4f);
            Land(motor, floor);

            motor.Step(new VoxelPlayerInput { JumpHeld = true }, 0.02f, floor);
            var firstVelocity = motor.Velocity.y;
            motor.Step(new VoxelPlayerInput { JumpHeld = true }, 0.02f, floor);

            Assert.That(firstVelocity, Is.EqualTo(6.7f).Within(0.001f));
            Assert.That(motor.Velocity.y, Is.LessThan(firstVelocity));
            Assert.That(motor.OnGround, Is.False);
        }

        [Test]
        public void HeadCollisionStopsAnUpwardJump()
        {
            var room = new FakeCollision { Solid = (_, y, _) => y <= 0 || y == 3 };
            var motor = CreateMotor(y: 4f);
            Land(motor, room);

            motor.Step(new VoxelPlayerInput { JumpHeld = true }, 0.02f, room);

            Assert.That(motor.Position.y, Is.EqualTo(2.75f).Within(0.001f));
            Assert.That(motor.Velocity.y, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void WallCollisionStopsOneAxisAndPreservesSliding()
        {
            var wall = new FakeCollision { Solid = (x, y, _) => x == 1 && y >= 1 && y <= 2 };
            var motor = CreateMotor(y: 2.65f);

            motor.Step(new VoxelPlayerInput { MoveX = 1f, MoveZ = 1f }, 0.1f, wall);

            Assert.That(motor.Position.x, Is.EqualTo(0.7f).Within(0.001f));
            Assert.That(motor.Velocity.x, Is.EqualTo(0f).Within(0.001f));
            Assert.That(motor.Position.z, Is.LessThan(0.5f));
            Assert.That(motor.Velocity.z, Is.LessThan(0f));
        }

        [Test]
        public void FlyModeSupportsStandaloneAscentDescentAndToggle()
        {
            var motor = CreateMotor(flying: true);
            var empty = new FakeCollision();

            motor.Step(new VoxelPlayerInput { JumpHeld = true }, 0.1f, empty);
            Assert.That(motor.Velocity.y, Is.EqualTo(9f).Within(0.001f));
            motor.Step(new VoxelPlayerInput { SprintHeld = true }, 0.1f, empty);
            Assert.That(motor.Velocity.y, Is.EqualTo(-9f).Within(0.001f));
            motor.Step(new VoxelPlayerInput { FlyToggle = true }, 0.02f, empty);

            Assert.That(motor.Flying, Is.False);
            Assert.That(motor.Velocity.y, Is.LessThan(0f));
        }

        [Test]
        public void FluidUsesReducedGravityWaterJumpAndTerminalVelocity()
        {
            var water = new FakeCollision { Fluid = (_, _, _) => true };
            var jumping = CreateMotor();
            jumping.Step(new VoxelPlayerInput { JumpHeld = true }, 0.1f, water);
            Assert.That(jumping.InFluid, Is.True);
            Assert.That(jumping.Velocity.y, Is.EqualTo(4.05f).Within(0.001f));

            var sinking = CreateMotor();
            for (var step = 0; step < 20; step++) sinking.Step(default, 0.1f, water);
            Assert.That(sinking.Velocity.y, Is.EqualTo(-6f).Within(0.001f));
        }

        private static VoxelPlayerMotor CreateMotor(float y = 10f, bool flying = false)
        {
            var motor = new VoxelPlayerMotor();
            motor.Initialize(new VoxelPlayerBootstrap
            {
                Position = new Vector3(0.5f, y, 0.5f),
                Velocity = Vector3.zero,
                Speed = 5f,
                SprintSpeed = 8f,
                FlySpeed = 9f,
                Gravity = -15f,
                JumpStrength = 7f,
                WaterJumpStrength = 4.5f,
                Width = 0.5f,
                Height = 1.8f,
                EyeHeight = 1.6f,
                CollisionTolerance = 0.05f,
                Flying = flying,
            });
            return motor;
        }

        private static void Land(VoxelPlayerMotor motor, IVoxelCollisionSource collision)
        {
            for (var step = 0; step < 200 && !motor.OnGround; step++)
                motor.Step(default, 0.02f, collision);
            Assert.That(motor.OnGround, Is.True, "The test motor never reached the floor.");
        }
    }
}
