using NUnit.Framework;
using ThreeUnity.Bridge.Logic;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityInputFreshnessGateTests
    {
        [Test]
        public void StreamExpiresAtItsDeadlineAndRecoversOnTheNextSample()
        {
            var gate = new ThreeUnityInputFreshnessGate(0.5f);

            Assert.That(gate.Advance(0.02f), Is.False);
            Assert.That(gate.MarkReceived(), Is.False, "The first sample is not a recovery.");
            Assert.That(gate.Advance(0.4f), Is.True);
            Assert.That(gate.Advance(0.1f), Is.False);
            var expired = gate.Snapshot();
            Assert.That(expired.Fresh, Is.False);
            Assert.That(expired.Expirations, Is.EqualTo(1));
            Assert.That(expired.Recoveries, Is.EqualTo(0));

            Assert.That(gate.MarkReceived(), Is.True);
            var recovered = gate.Snapshot();
            Assert.That(recovered.Fresh, Is.True);
            Assert.That(recovered.AgeSeconds, Is.Zero);
            Assert.That(recovered.Recoveries, Is.EqualTo(1));
        }

    }
}
