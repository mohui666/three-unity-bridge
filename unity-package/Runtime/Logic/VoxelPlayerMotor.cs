using System;
using UnityEngine;

namespace ThreeUnity.Bridge.Logic
{
    [Serializable]
    public struct VoxelPlayerBootstrap
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Yaw;
        public float Pitch;
        public float Speed;
        public float SprintSpeed;
        public float FlySpeed;
        public float Gravity;
        public float JumpStrength;
        public float WaterJumpStrength;
        public float Width;
        public float Height;
        public float EyeHeight;
        public float CollisionTolerance;
        public bool Flying;
    }

    [Serializable]
    public struct VoxelPlayerInput
    {
        public float MoveX;
        public float MoveZ;
        public float Yaw;
        public float Pitch;
        public bool JumpHeld;
        public bool SprintHeld;
        public bool FlyToggle;
    }

    public sealed class VoxelPlayerMotor
    {
        private float speed;
        private float sprintSpeed;
        private float flySpeed;
        private float gravity;
        private float jumpStrength;
        private float waterJumpStrength;
        private float width;
        private float height;
        private float eyeHeight;
        private float collisionTolerance;
        private bool initialized;

        public Vector3 Position { get; private set; }
        public Vector3 Velocity { get; private set; }
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public bool Flying { get; private set; }
        public bool OnGround { get; private set; }
        public bool InFluid { get; private set; }
        public bool IsSprinting { get; private set; }

        public void Initialize(VoxelPlayerBootstrap bootstrap)
        {
            if (bootstrap.Speed <= 0f || bootstrap.SprintSpeed <= 0f || bootstrap.FlySpeed <= 0f)
                throw new ArgumentException("Movement speeds must be positive.", nameof(bootstrap));
            if (bootstrap.Width <= 0f || bootstrap.Height <= 0f || bootstrap.EyeHeight <= 0f)
                throw new ArgumentException("Player dimensions must be positive.", nameof(bootstrap));
            if (bootstrap.CollisionTolerance < 0f)
                throw new ArgumentException("Collision tolerance cannot be negative.", nameof(bootstrap));

            Position = bootstrap.Position;
            Velocity = bootstrap.Velocity;
            Yaw = bootstrap.Yaw;
            Pitch = Mathf.Clamp(bootstrap.Pitch, -Mathf.PI / 2f, Mathf.PI / 2f);
            Flying = bootstrap.Flying;
            speed = bootstrap.Speed;
            sprintSpeed = bootstrap.SprintSpeed;
            flySpeed = bootstrap.FlySpeed;
            gravity = bootstrap.Gravity;
            jumpStrength = bootstrap.JumpStrength;
            waterJumpStrength = bootstrap.WaterJumpStrength;
            width = bootstrap.Width;
            height = bootstrap.Height;
            eyeHeight = bootstrap.EyeHeight;
            collisionTolerance = bootstrap.CollisionTolerance;
            OnGround = false;
            InFluid = false;
            IsSprinting = false;
            initialized = true;
        }

        public void Step(VoxelPlayerInput input, float deltaTime, IVoxelCollisionSource collision)
        {
            if (!initialized)
                throw new InvalidOperationException("Voxel player motor must be initialized before stepping.");
            if (collision == null)
                throw new ArgumentNullException(nameof(collision));
            if (deltaTime <= 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
                throw new ArgumentOutOfRangeException(nameof(deltaTime));

            Yaw = input.Yaw;
            Pitch = Mathf.Clamp(input.Pitch, -Mathf.PI / 2f, Mathf.PI / 2f);
            if (input.FlyToggle)
            {
                Flying = !Flying;
                if (!Flying)
                    Velocity = new Vector3(Velocity.x, 0f, Velocity.z);
            }

            InFluid = IsInFluid(collision);
            ApplyMovement(input);
            if (!Flying)
                ApplyGravity(deltaTime);

            var oldPosition = Position;
            Position += Velocity * deltaTime;
            ResolveCollisions(oldPosition, collision);
        }

        private void ApplyMovement(VoxelPlayerInput input)
        {
            var sinYaw = Mathf.Sin(Yaw);
            var cosYaw = Mathf.Cos(Yaw);
            var right = new Vector3(cosYaw, 0f, -sinYaw);

            if (Flying)
            {
                var cosPitch = Mathf.Cos(Pitch);
                var forward = new Vector3(-sinYaw * cosPitch, Mathf.Sin(Pitch), -cosYaw * cosPitch);
                var direction = forward * input.MoveZ + right * input.MoveX;
                if (input.JumpHeld) direction.y += 1f;
                if (input.SprintHeld) direction.y -= 1f;
                Velocity = direction.sqrMagnitude > 0f ? direction.normalized * flySpeed : Vector3.zero;
                IsSprinting = false;
                return;
            }

            var horizontalForward = new Vector3(-sinYaw, 0f, -cosYaw);
            var horizontal = horizontalForward * input.MoveZ + right * input.MoveX;
            if (horizontal.sqrMagnitude > 1f)
                horizontal.Normalize();
            IsSprinting = input.SprintHeld;
            var currentSpeed = IsSprinting ? sprintSpeed : speed;
            Velocity = new Vector3(horizontal.x * currentSpeed, Velocity.y, horizontal.z * currentSpeed);

            if (input.JumpHeld && (OnGround || InFluid))
                Velocity = new Vector3(Velocity.x, InFluid ? waterJumpStrength : jumpStrength, Velocity.z);
        }

        private void ApplyGravity(float deltaTime)
        {
            var verticalVelocity = Velocity.y + gravity * (InFluid ? 0.3f : 1f) * deltaTime;
            verticalVelocity = InFluid
                ? Mathf.Clamp(verticalVelocity, -6f, 6f)
                : Mathf.Max(verticalVelocity, -50f);
            Velocity = new Vector3(Velocity.x, verticalVelocity, Velocity.z);
        }

        private bool IsInFluid(IVoxelCollisionSource collision)
        {
            var feet = Position.y - eyeHeight;
            var x = Mathf.FloorToInt(Position.x);
            var z = Mathf.FloorToInt(Position.z);
            return collision.IsFluid(x, Mathf.FloorToInt(feet + 0.3f), z)
                || collision.IsFluid(x, Mathf.FloorToInt(feet + 1f), z);
        }

        private void ResolveCollisions(Vector3 oldPosition, IVoxelCollisionSource collision)
        {
            var playerFeet = Position.y - eyeHeight;
            var playerHead = playerFeet + height;
            var halfWidth = width / 2f;
            var tolerance = collisionTolerance;

            OnGround = false;
            var feetY = Mathf.FloorToInt(playerFeet - tolerance);
            for (var xSample = -1; xSample <= 1 && !OnGround; xSample++)
            {
                for (var zSample = -1; zSample <= 1; zSample++)
                {
                    var checkX = Mathf.FloorToInt(Position.x + xSample * 0.3f);
                    var checkZ = Mathf.FloorToInt(Position.z + zSample * 0.3f);
                    if (!collision.IsSolid(checkX, feetY, checkZ, Flying))
                        continue;

                    var blockTop = feetY + 1f;
                    if (playerFeet <= blockTop + tolerance && Velocity.y <= 0f)
                    {
                        Position = new Vector3(Position.x, blockTop + eyeHeight + tolerance, Position.z);
                        Velocity = new Vector3(Velocity.x, 0f, Velocity.z);
                        OnGround = true;
                        break;
                    }
                }
            }

            var headY = Mathf.FloorToInt(playerHead + tolerance);
            for (var xSample = -1; xSample <= 1; xSample++)
            {
                for (var zSample = -1; zSample <= 1; zSample++)
                {
                    var checkX = Mathf.FloorToInt(Position.x + xSample * 0.3f);
                    var checkZ = Mathf.FloorToInt(Position.z + zSample * 0.3f);
                    if (!collision.IsSolid(checkX, headY, checkZ, Flying) || Velocity.y <= 0f)
                        continue;

                    Position = new Vector3(
                        Position.x,
                        headY - height + eyeHeight - tolerance,
                        Position.z);
                    Velocity = new Vector3(Velocity.x, 0f, Velocity.z);
                    break;
                }
            }

            var bodyY = new[]
            {
                Mathf.FloorToInt(playerFeet + 0.2f),
                Mathf.FloorToInt(playerFeet + 0.9f),
            };
            foreach (var checkY in bodyY)
            {
                ResolveXCollision(oldPosition, checkY, halfWidth, tolerance, collision);
            }
            foreach (var checkY in bodyY)
            {
                ResolveZCollision(oldPosition, checkY, halfWidth, tolerance, collision);
            }
        }

        private void ResolveXCollision(
            Vector3 oldPosition,
            int checkY,
            float halfWidth,
            float tolerance,
            IVoxelCollisionSource collision)
        {
            if (Velocity.x > 0f || Position.x > oldPosition.x)
            {
                var checkX = Mathf.FloorToInt(Position.x + halfWidth + tolerance);
                if (SamplesZHit(checkX, checkY, halfWidth, collision))
                {
                    Position = new Vector3(checkX - halfWidth - tolerance, Position.y, Position.z);
                    Velocity = new Vector3(Mathf.Min(0f, Velocity.x), Velocity.y, Velocity.z);
                }
            }
            if (Velocity.x < 0f || Position.x < oldPosition.x)
            {
                var checkX = Mathf.FloorToInt(Position.x - halfWidth - tolerance);
                if (SamplesZHit(checkX, checkY, halfWidth, collision))
                {
                    Position = new Vector3(checkX + 1f + halfWidth + tolerance, Position.y, Position.z);
                    Velocity = new Vector3(Mathf.Max(0f, Velocity.x), Velocity.y, Velocity.z);
                }
            }
        }

        private void ResolveZCollision(
            Vector3 oldPosition,
            int checkY,
            float halfWidth,
            float tolerance,
            IVoxelCollisionSource collision)
        {
            if (Velocity.z > 0f || Position.z > oldPosition.z)
            {
                var checkZ = Mathf.FloorToInt(Position.z + halfWidth + tolerance);
                if (SamplesXHit(checkZ, checkY, halfWidth, collision))
                {
                    Position = new Vector3(Position.x, Position.y, checkZ - halfWidth - tolerance);
                    Velocity = new Vector3(Velocity.x, Velocity.y, Mathf.Min(0f, Velocity.z));
                }
            }
            if (Velocity.z < 0f || Position.z < oldPosition.z)
            {
                var checkZ = Mathf.FloorToInt(Position.z - halfWidth - tolerance);
                if (SamplesXHit(checkZ, checkY, halfWidth, collision))
                {
                    Position = new Vector3(Position.x, Position.y, checkZ + 1f + halfWidth + tolerance);
                    Velocity = new Vector3(Velocity.x, Velocity.y, Mathf.Max(0f, Velocity.z));
                }
            }
        }

        private bool SamplesZHit(int x, int y, float halfWidth, IVoxelCollisionSource collision)
        {
            for (var sample = -1; sample <= 1; sample++)
            {
                var z = Mathf.FloorToInt(Position.z + sample * halfWidth);
                if (collision.IsSolid(x, y, z, Flying)) return true;
            }
            return false;
        }

        private bool SamplesXHit(int z, int y, float halfWidth, IVoxelCollisionSource collision)
        {
            for (var sample = -1; sample <= 1; sample++)
            {
                var x = Mathf.FloorToInt(Position.x + sample * halfWidth);
                if (collision.IsSolid(x, y, z, Flying)) return true;
            }
            return false;
        }
    }
}
