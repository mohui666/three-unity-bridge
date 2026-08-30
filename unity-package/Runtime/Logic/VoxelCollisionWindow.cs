using System;
using UnityEngine;

namespace ThreeUnity.Bridge.Logic
{
    public interface IVoxelCollisionSource
    {
        bool IsSolid(int x, int y, int z, bool flying);
        bool IsFluid(int x, int y, int z);
    }

    public enum CollisionDeltaApplyResult
    {
        Applied,
        Stale,
        BaseMismatch,
    }

    public sealed class VoxelCollisionWindow : IVoxelCollisionSource
    {
        private Vector3Int origin;
        private Vector3Int size;
        private byte[] solidBits = Array.Empty<byte>();
        private byte[] fluidBits = Array.Empty<byte>();

        public int Revision { get; private set; } = -1;
        public int LastDeltaChangeCount { get; private set; }

        public bool Replace(
            int revision,
            Vector3Int nextOrigin,
            Vector3Int nextSize,
            string encodedSolidBits,
            string encodedFluidBits)
        {
            if (revision <= Revision)
                return false;
            if (revision < 0)
                throw new ArgumentOutOfRangeException(nameof(revision));
            if (nextSize.x <= 0 || nextSize.y <= 0 || nextSize.z <= 0)
                throw new ArgumentException("Collision window dimensions must be positive.", nameof(nextSize));

            var cellCount = (long)nextSize.x * nextSize.y * nextSize.z;
            if (cellCount > int.MaxValue)
                throw new ArgumentException("Collision window is too large.", nameof(nextSize));
            var requiredBytes = ((int)cellCount + 7) / 8;
            var nextSolidBits = Convert.FromBase64String(encodedSolidBits);
            var nextFluidBits = Convert.FromBase64String(encodedFluidBits);
            if (nextSolidBits.Length != requiredBytes || nextFluidBits.Length != requiredBytes)
                throw new ArgumentException("Collision bitset lengths do not match the requested dimensions.");

            origin = nextOrigin;
            size = nextSize;
            solidBits = nextSolidBits;
            fluidBits = nextFluidBits;
            Revision = revision;
            LastDeltaChangeCount = 0;
            return true;
        }

        public CollisionDeltaApplyResult ApplyDelta(
            int baseRevision,
            int revision,
            Vector3Int nextOrigin,
            Vector3Int nextSize,
            int expectedChangeCount,
            string encodedChanges)
        {
            if (revision <= Revision)
                return CollisionDeltaApplyResult.Stale;
            if (baseRevision != Revision)
                return CollisionDeltaApplyResult.BaseMismatch;
            if (revision < 0)
                throw new ArgumentOutOfRangeException(nameof(revision));
            if (expectedChangeCount < 0)
                throw new ArgumentOutOfRangeException(nameof(expectedChangeCount));
            if (nextSize.x <= 0 || nextSize.y <= 0 || nextSize.z <= 0)
                throw new ArgumentException("Collision window dimensions must be positive.", nameof(nextSize));

            var cellCountLong = (long)nextSize.x * nextSize.y * nextSize.z;
            if (cellCountLong > int.MaxValue)
                throw new ArgumentException("Collision window is too large.", nameof(nextSize));
            var cellCount = (int)cellCountLong;
            var requiredBytes = (cellCount + 7) / 8;
            var nextSolidBits = new byte[requiredBytes];
            var nextFluidBits = new byte[requiredBytes];

            for (var y = 0; y < nextSize.y; y++)
            {
                for (var z = 0; z < nextSize.z; z++)
                {
                    for (var x = 0; x < nextSize.x; x++)
                    {
                        var worldX = nextOrigin.x + x;
                        var worldY = nextOrigin.y + y;
                        var worldZ = nextOrigin.z + z;
                        var oldX = worldX - origin.x;
                        var oldY = worldY - origin.y;
                        var oldZ = worldZ - origin.z;
                        if (oldX < 0 || oldX >= size.x
                            || oldY < 0 || oldY >= size.y
                            || oldZ < 0 || oldZ >= size.z)
                            continue;

                        var oldIndex = oldX + size.x * (oldZ + size.z * oldY);
                        var nextIndex = x + nextSize.x * (z + nextSize.z * y);
                        WriteBit(nextSolidBits, nextIndex, ReadBit(solidBits, oldIndex));
                        WriteBit(nextFluidBits, nextIndex, ReadBit(fluidBits, oldIndex));
                    }
                }
            }

            var changes = Convert.FromBase64String(encodedChanges ?? string.Empty);
            var offset = 0;
            var previousIndex = -1;
            var actualChangeCount = 0;
            while (offset < changes.Length)
            {
                var packed = ReadNonNegativeVarint(changes, ref offset);
                var flags = (byte)(packed & 0x03);
                var gap = (packed >> 2) + 1;
                var index = (long)previousIndex + gap;
                if (index < 0 || index >= cellCount)
                    throw new ArgumentException("Collision delta index is outside the requested dimensions.", nameof(encodedChanges));
                WriteBit(nextSolidBits, (int)index, (flags & 0x01) != 0);
                WriteBit(nextFluidBits, (int)index, (flags & 0x02) != 0);
                previousIndex = (int)index;
                actualChangeCount++;
            }
            if (actualChangeCount != expectedChangeCount)
                throw new ArgumentException("Collision delta change count does not match its payload.", nameof(expectedChangeCount));

            origin = nextOrigin;
            size = nextSize;
            solidBits = nextSolidBits;
            fluidBits = nextFluidBits;
            Revision = revision;
            LastDeltaChangeCount = actualChangeCount;
            return CollisionDeltaApplyResult.Applied;
        }

        public bool IsSolid(int x, int y, int z, bool flying)
        {
            if (!TryGetIndex(x, y, z, out var index))
                return !flying;
            return ReadBit(solidBits, index);
        }

        public bool IsFluid(int x, int y, int z)
        {
            return TryGetIndex(x, y, z, out var index) && ReadBit(fluidBits, index);
        }

        private bool TryGetIndex(int x, int y, int z, out int index)
        {
            var localX = x - origin.x;
            var localY = y - origin.y;
            var localZ = z - origin.z;
            if (localX < 0 || localX >= size.x
                || localY < 0 || localY >= size.y
                || localZ < 0 || localZ >= size.z)
            {
                index = -1;
                return false;
            }

            index = localX + size.x * (localZ + size.z * localY);
            return true;
        }

        private static bool ReadBit(byte[] bytes, int index)
        {
            return (bytes[index >> 3] & (1 << (index & 7))) != 0;
        }

        private static void WriteBit(byte[] bytes, int index, bool value)
        {
            var mask = (byte)(1 << (index & 7));
            if (value)
                bytes[index >> 3] |= mask;
            else
                bytes[index >> 3] &= (byte)~mask;
        }

        private static long ReadNonNegativeVarint(byte[] bytes, ref int offset)
        {
            long value = 0;
            var shift = 0;
            for (var count = 0; count < 5 && offset < bytes.Length; count++)
            {
                var next = bytes[offset++];
                value |= (long)(next & 0x7f) << shift;
                if ((next & 0x80) == 0)
                    return value;
                shift += 7;
            }
            throw new ArgumentException("Collision delta contains an invalid packed varint.");
        }
    }
}
