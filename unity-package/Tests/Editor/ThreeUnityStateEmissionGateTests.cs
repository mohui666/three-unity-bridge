using NUnit.Framework;
using ThreeUnity.Bridge.Logic;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityStateEmissionGateTests
    {
        [Test]
        public void FirstAndChangedStatesEmitImmediately()
        {
            var gate = new ThreeUnityStateEmissionGate(0.2f);

            Assert.That(gate.ShouldEmit(0.02f, false), Is.True);
            Assert.That(gate.ShouldEmit(0.02f, true), Is.True);
            var metrics = gate.Snapshot();
            Assert.That(metrics.Emitted, Is.EqualTo(2));
            Assert.That(metrics.Suppressed, Is.Zero);
        }

        [Test]
        public void IdenticalStatesAreSuppressedUntilAHeartbeat()
        {
            var gate = new ThreeUnityStateEmissionGate(0.2f);
            Assert.That(gate.ShouldEmit(0.02f, false), Is.True);

            for (var tick = 0; tick < 9; tick++)
                Assert.That(gate.ShouldEmit(0.02f, false), Is.False);
            Assert.That(gate.ShouldEmit(0.02f, false), Is.True);

            var metrics = gate.Snapshot();
            Assert.That(metrics.Emitted, Is.EqualTo(2));
            Assert.That(metrics.Suppressed, Is.EqualTo(9));
            Assert.That(metrics.Heartbeats, Is.EqualTo(1));
        }

        [Test]
        public void ForcedAcknowledgementsBypassSuppression()
        {
            var gate = new ThreeUnityStateEmissionGate();
            Assert.That(gate.ShouldEmit(0.02f, false), Is.True);
            Assert.That(gate.ShouldEmit(0.02f, false), Is.False);
            Assert.That(gate.ShouldEmit(0.02f, false, true), Is.True);
        }

        [Test]
        public void ContinuousChangesRespectAnAverageRateBudget()
        {
            var gate = new ThreeUnityStateEmissionGate(0.2f, 1f / 30f);

            for (var tick = 0; tick < 50; tick++)
                gate.ShouldEmit(0.02f, true);

            var metrics = gate.Snapshot();
            Assert.That(metrics.Emitted, Is.EqualTo(30));
            Assert.That(metrics.RateLimited, Is.EqualTo(20));
            Assert.That(metrics.MinimumIntervalSeconds, Is.EqualTo(1f / 30f).Within(0.000001f));
        }
    }
}
