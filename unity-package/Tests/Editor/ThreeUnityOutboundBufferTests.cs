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
            Assert.That(metrics.ReliableBackpressureRejected, Is.EqualTo(1));
            Assert.That(metrics.ReliableDropped, Is.Zero);
            Assert.That(metrics.PendingReliable, Is.EqualTo(0));
            Assert.That(metrics.Dequeued, Is.EqualTo(2));
        }

        [Test]
        public void ReliableBurstMustYieldToLatestState()
        {
            var buffer = new ThreeUnityOutboundBuffer(8, 3);
            for (var index = 0; index < 6; index++)
                Assert.That(buffer.EnqueueReliable("reliable-" + index), Is.True);
            buffer.EnqueueLatest("player.state", "state-newest");

            Assert.That(Dequeue(buffer), Is.EqualTo("reliable-0"));
            Assert.That(Dequeue(buffer), Is.EqualTo("reliable-1"));
            Assert.That(Dequeue(buffer), Is.EqualTo("reliable-2"));
            Assert.That(Dequeue(buffer), Is.EqualTo("state-newest"));
            Assert.That(Dequeue(buffer), Is.EqualTo("reliable-3"));
            Assert.That(buffer.Snapshot().ReliableBurstYields, Is.EqualTo(1));
        }

        [Test]
        public void PurgeOwnerIsAtomicPreservesOrderAndRestoresCapacity()
        {
            var buffer = new ThreeUnityOutboundBuffer(3, 2);
            var retiredOwner = new object();
            var survivingOwner = new object();

            Assert.That(buffer.EnqueueReliable(retiredOwner, "old-1"), Is.True);
            Assert.That(buffer.EnqueueReliable(survivingOwner, "keep-1"), Is.True);
            Assert.That(buffer.EnqueueReliable(retiredOwner, "old-2"), Is.True);
            Assert.That(buffer.EnqueueReliable(survivingOwner, "blocked"), Is.False);
            buffer.EnqueueLatest(retiredOwner, "old:state", "old-state");
            buffer.EnqueueLatest(survivingOwner, "keep:state", "keep-state");

            Assert.That(buffer.PurgeOwner(retiredOwner), Is.EqualTo(3));
            Assert.That(buffer.EnqueueReliable(survivingOwner, "keep-2"), Is.True);
            Assert.That(buffer.EnqueueReliable(survivingOwner, "keep-3"), Is.True);

            Assert.That(Dequeue(buffer), Is.EqualTo("keep-1"));
            Assert.That(Dequeue(buffer), Is.EqualTo("keep-2"));
            Assert.That(Dequeue(buffer), Is.EqualTo("keep-state"));
            Assert.That(Dequeue(buffer), Is.EqualTo("keep-3"));
            Assert.That(buffer.TryDequeue(out _), Is.False);

            var metrics = buffer.Snapshot();
            Assert.That(metrics.OwnerPurgedReliable, Is.EqualTo(2));
            Assert.That(metrics.OwnerPurgedLatest, Is.EqualTo(1));
            Assert.That(metrics.OwnerPurged, Is.EqualTo(3));
            Assert.That(metrics.ReliableBackpressureRejected, Is.EqualTo(1));
            Assert.That(metrics.ReliableDropped, Is.Zero);
        }

        private static string Dequeue(ThreeUnityOutboundBuffer buffer)
        {
            Assert.That(buffer.TryDequeue(out var message), Is.True);
            return message;
        }
    }
}
