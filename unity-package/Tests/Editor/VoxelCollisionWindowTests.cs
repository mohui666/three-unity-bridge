using System;
using NUnit.Framework;
using ThreeUnity.Bridge.Logic;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class VoxelCollisionWindowTests
    {
        [Test]
        public void ReplaceDecodesXFastestSolidAndFluidBits()
        {
            var window = new VoxelCollisionWindow();

            Assert.That(window.Replace(
                4,
                new Vector3Int(10, 20, 30),
                new Vector3Int(2, 2, 2),
                "iQ==",
                "Qg=="), Is.True);

            Assert.That(window.Revision, Is.EqualTo(4));
            Assert.That(window.IsSolid(10, 20, 30, false), Is.True, "index 0");
            Assert.That(window.IsSolid(11, 20, 31, false), Is.True, "index 3");
            Assert.That(window.IsSolid(11, 21, 31, false), Is.True, "index 7");
            Assert.That(window.IsSolid(10, 21, 30, false), Is.False, "index 4");
            Assert.That(window.IsFluid(11, 20, 30), Is.True, "index 1");
            Assert.That(window.IsFluid(10, 21, 31), Is.True, "index 6");
        }

        [Test]
        public void ReplaceRejectsStaleRevisionsWithoutChangingTheWindow()
        {
            var window = new VoxelCollisionWindow();
            Assert.That(window.Replace(8, Vector3Int.zero, Vector3Int.one, "AQ==", "AA=="), Is.True);

            Assert.That(window.Replace(8, Vector3Int.zero, Vector3Int.one, "AA==", "AQ=="), Is.False);
            Assert.That(window.Replace(7, Vector3Int.zero, Vector3Int.one, "AA==", "AQ=="), Is.False);

            Assert.That(window.Revision, Is.EqualTo(8));
            Assert.That(window.IsSolid(0, 0, 0, false), Is.True);
            Assert.That(window.IsFluid(0, 0, 0), Is.False);
        }

        [Test]
        public void UnknownCellsBlockWalkingButNotFlying()
        {
            var window = new VoxelCollisionWindow();
            window.Replace(1, Vector3Int.zero, Vector3Int.one, "AA==", "AA==");

            Assert.That(window.IsSolid(2, 0, 0, false), Is.True);
            Assert.That(window.IsSolid(2, 0, 0, true), Is.False);
            Assert.That(window.IsFluid(2, 0, 0), Is.False);
        }

        [Test]
        public void ReplaceRejectsBitsetsTooShortForDimensions()
        {
            var window = new VoxelCollisionWindow();

            Assert.Throws<ArgumentException>(() => window.Replace(
                1,
                Vector3Int.zero,
                new Vector3Int(3, 3, 3),
                "AA==",
                "AA=="));
        }

        [Test]
        public void SparseDeltaChangesExactCellValues()
        {
            var window = new VoxelCollisionWindow();
            window.Replace(4, Vector3Int.zero, new Vector3Int(2, 2, 2), "iQ==", "Qg==");

            var result = window.ApplyDelta(
                4,
                5,
                Vector3Int.zero,
                new Vector3Int(2, 2, 2),
                2,
                "AAU=");

            Assert.That(result, Is.EqualTo(CollisionDeltaApplyResult.Applied));
            Assert.That(window.Revision, Is.EqualTo(5));
            Assert.That(window.LastDeltaChangeCount, Is.EqualTo(2));
            Assert.That(window.IsSolid(0, 0, 0, false), Is.False, "index 0 was cleared");
            Assert.That(window.IsSolid(0, 0, 1, false), Is.True, "index 2 was set");
            Assert.That(window.IsSolid(1, 0, 1, false), Is.True, "unchanged index 3 was retained");
        }

        [Test]
        public void ShiftedDeltaCopiesSpatialOverlapAndDefaultsNewCellsToEmpty()
        {
            var window = new VoxelCollisionWindow();
            window.Replace(0, Vector3Int.zero, new Vector3Int(2, 1, 1), "Aw==", "AA==");

            Assert.That(window.ApplyDelta(
                0,
                1,
                new Vector3Int(1, 0, 0),
                new Vector3Int(2, 1, 1),
                0,
                ""), Is.EqualTo(CollisionDeltaApplyResult.Applied));

            Assert.That(window.IsSolid(1, 0, 0, false), Is.True, "world x=1 was copied from the overlap");
            Assert.That(window.IsSolid(2, 0, 0, true), Is.False, "the new world x=2 cell defaults to empty");
        }

        [Test]
        public void DeltaBaseMismatchRequestsResyncWithoutMutatingTheWindow()
        {
            var window = new VoxelCollisionWindow();
            window.Replace(8, Vector3Int.zero, Vector3Int.one, "AQ==", "AA==");

            var result = window.ApplyDelta(7, 9, Vector3Int.zero, Vector3Int.one, 0, "");

            Assert.That(result, Is.EqualTo(CollisionDeltaApplyResult.BaseMismatch));
            Assert.That(window.Revision, Is.EqualTo(8));
            Assert.That(window.IsSolid(0, 0, 0, false), Is.True);
        }

        [Test]
        public void InvalidDeltaIsAtomic()
        {
            var window = new VoxelCollisionWindow();
            window.Replace(2, Vector3Int.zero, Vector3Int.one, "AQ==", "AA==");

            Assert.Throws<ArgumentException>(() => window.ApplyDelta(
                2,
                3,
                Vector3Int.zero,
                Vector3Int.one,
                1,
                "gA=="));
            Assert.That(window.Revision, Is.EqualTo(2));
            Assert.That(window.IsSolid(0, 0, 0, false), Is.True);
        }
    }
}
