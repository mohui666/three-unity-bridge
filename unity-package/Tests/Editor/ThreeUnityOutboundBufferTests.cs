using NUnit.Framework;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityOutboundBufferTests
    {
        [Test]
        public void LatestValueStreamsCollapseAStateFloodToOneMessage()
        {
            var buffer = new ThreeUnityOutboundBuffer();

            for (var index = 0; index < 100000; index++)
                buffer.EnqueueLatest("player.state", "state-" + index);

            var beforeDrain = buffer.Snapshot();
            Assert.That(beforeDrain.PendingLatest, Is.EqualTo(1));
            Assert.That(beforeDrain.LatestQueued, Is.EqualTo(100000));
            Assert.That(beforeDrain.LatestCoalesced, Is.EqualTo(99999));
            Assert.That(beforeDrain.MaxPending, Is.EqualTo(1));
            Assert.That(buffer.TryDequeue(out var message), Is.True);
            Assert.That(message, Is.EqualTo("state-99999"));
            Assert.That(buffer.TryDequeue(out _), Is.False);
        }

        [Test]
        public void IndependentLatestStreamsRetainTheirNewestValues()
        {
            var buffer = new ThreeUnityOutboundBuffer();
            buffer.EnqueueLatest("player.state", "player-old");
            buffer.EnqueueLatest("flight.state", "flight-new");
            buffer.EnqueueLatest("player.state", "player-new");

            Assert.That(buffer.Snapshot().PendingLatest, Is.EqualTo(2));
            var values = new[] { Dequeue(buffer), Dequeue(buffer) };
            CollectionAssert.AreEquivalent(new[] { "player-new", "flight-new" }, values);
        }

        [Test]
        public void ReliableMessagesStayOrderedAndAreBounded()
        {
            var buffer = new ThreeUnityOutboundBuffer(2);

            Assert.That(buffer.EnqueueReliable("ready"), Is.True);
            Assert.That(buffer.EnqueueReliable("fallback"), Is.True);
            Assert.That(buffer.EnqueueReliable("overflow"), Is.False);
            Assert.That(Dequeue(buffer), Is.EqualTo("ready"));
            Assert.That(Dequeue(buffer), Is.EqualTo("fallback"));

            var metrics = buffer.Snapshot();
            Assert.That(metrics.ReliableDropped, Is.EqualTo(1));
            Assert.That(metrics.PendingReliable, Is.EqualTo(0));
            Assert.That(metrics.Dequeued, Is.EqualTo(2));
        }

        private static string Dequeue(ThreeUnityOutboundBuffer buffer)
        {
            Assert.That(buffer.TryDequeue(out var message), Is.True);
            return message;
        }
    }
}
