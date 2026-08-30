using System;
using System.Collections.Generic;

namespace ThreeUnity.Bridge
{
    /// <summary>
    /// Thread-safe bridge output buffer. Reliable control messages remain ordered
    /// and bounded; high-frequency streams keep only their newest value. Entries
    /// may carry a lightweight logical owner so a restarted browser session can
    /// atomically retire its backlog without disturbing unrelated senders.
    /// </summary>
    public sealed class ThreeUnityOutboundBuffer
    {
        internal const int DefaultReliableBurstLimit = 32;

        private readonly object sync = new object();
        private readonly int reliableCapacity;
        private readonly int reliableBurstLimit;
        private Queue<Entry> reliable = new Queue<Entry>();
        private Dictionary<string, Entry> latest =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private Queue<string> latestOrder = new Queue<string>();
        private int consecutiveReliableDequeues;
        private long reliableQueued;
        private long latestQueued;
        private long latestCoalesced;
        private long reliableBackpressureRejected;
        private long reliableDropped;
        private long ownerPurgedReliable;
        private long ownerPurgedLatest;
        private long reliableBurstYields;
        private long dequeued;
        private int maxPending;

        public ThreeUnityOutboundBuffer(
            int reliableCapacity = 1024,
            int reliableBurstLimit = DefaultReliableBurstLimit)
        {
            if (reliableCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(reliableCapacity));
            if (reliableBurstLimit <= 0)
                throw new ArgumentOutOfRangeException(nameof(reliableBurstLimit));
            this.reliableCapacity = reliableCapacity;
            this.reliableBurstLimit = reliableBurstLimit;
        }

        public bool EnqueueReliable(string message)
        {
            return EnqueueReliable(null, message);
        }

        internal bool EnqueueReliable(object owner, string message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            lock (sync)
            {
                if (reliable.Count >= reliableCapacity)
                {
                    // Rejection is recoverable backpressure. The caller retains
                    // the exact envelope and retries it, so this is not a drop.
                    reliableBackpressureRejected++;
                    return false;
                }

                reliable.Enqueue(new Entry(owner, message));
                reliableQueued++;
                ObservePendingLocked();
                return true;
            }
        }

        public void EnqueueLatest(string stream, string message)
        {
            EnqueueLatest(null, stream, message);
        }

        internal void EnqueueLatest(object owner, string stream, string message)
        {
            if (string.IsNullOrWhiteSpace(stream))
                throw new ArgumentException("A latest-value stream key is required.", nameof(stream));
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            lock (sync)
            {
                if (latest.ContainsKey(stream))
                {
                    latest[stream] = new Entry(owner, message);
                    latestCoalesced++;
                }
                else
                {
                    latest.Add(stream, new Entry(owner, message));
                    latestOrder.Enqueue(stream);
                }
                latestQueued++;
                ObservePendingLocked();
            }
        }

        public bool TryDequeue(out string message)
        {
            lock (sync)
            {
                if (reliable.Count > 0
                    && (latest.Count == 0
                        || consecutiveReliableDequeues < reliableBurstLimit))
                {
                    message = reliable.Dequeue().Message;
                    consecutiveReliableDequeues++;
                    dequeued++;
                    return true;
                }

                if (TryDequeueLatestLocked(out message))
                {
                    if (reliable.Count > 0)
                        reliableBurstYields++;
                    consecutiveReliableDequeues = 0;
                    dequeued++;
                    return true;
                }

                if (reliable.Count > 0)
                {
                    // Defensive fallback for an inconsistent latest-order queue.
                    message = reliable.Dequeue().Message;
                    consecutiveReliableDequeues++;
                    dequeued++;
                    return true;
                }

                consecutiveReliableDequeues = 0;
                message = null;
                return false;
            }
        }

        /// <summary>
        /// Atomically removes every queued reliable and latest-value entry owned
        /// by the exact logical epoch token. A null owner is deliberately rejected
        /// so session cleanup cannot erase compatibility API traffic.
        /// </summary>
        internal int PurgeOwner(object owner)
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));

            lock (sync)
            {
                var retainedReliable = new Queue<Entry>(reliable.Count);
                var removedReliable = 0;
                while (reliable.Count > 0)
                {
                    var entry = reliable.Dequeue();
                    if (ReferenceEquals(entry.Owner, owner))
                        removedReliable++;
                    else
                        retainedReliable.Enqueue(entry);
                }
                reliable = retainedReliable;

                var retainedLatest = new Dictionary<string, Entry>(StringComparer.Ordinal);
                var retainedLatestOrder = new Queue<string>(latestOrder.Count);
                var removedLatest = 0;
                while (latestOrder.Count > 0)
                {
                    var stream = latestOrder.Dequeue();
                    if (!latest.TryGetValue(stream, out var entry))
                        continue;
                    if (ReferenceEquals(entry.Owner, owner))
                    {
                        removedLatest++;
                        continue;
                    }
                    retainedLatest.Add(stream, entry);
                    retainedLatestOrder.Enqueue(stream);
                }
                latest = retainedLatest;
                latestOrder = retainedLatestOrder;

                ownerPurgedReliable += removedReliable;
                ownerPurgedLatest += removedLatest;
                if (reliable.Count == 0)
                    consecutiveReliableDequeues = 0;
                return removedReliable + removedLatest;
            }
        }

        public ThreeUnityOutboundBufferSnapshot Snapshot()
        {
            lock (sync)
            {
                return new ThreeUnityOutboundBufferSnapshot
                {
                    ReliableQueued = reliableQueued,
                    LatestQueued = latestQueued,
                    LatestCoalesced = latestCoalesced,
                    ReliableBackpressureRejected = reliableBackpressureRejected,
                    ReliableDropped = reliableDropped,
                    OwnerPurgedReliable = ownerPurgedReliable,
                    OwnerPurgedLatest = ownerPurgedLatest,
                    ReliableBurstYields = reliableBurstYields,
                    Dequeued = dequeued,
                    PendingReliable = reliable.Count,
                    PendingLatest = latest.Count,
                    MaxPending = maxPending,
                };
            }
        }

        private bool TryDequeueLatestLocked(out string message)
        {
            while (latestOrder.Count > 0)
            {
                var stream = latestOrder.Dequeue();
                if (!latest.TryGetValue(stream, out var entry))
                    continue;
                latest.Remove(stream);
                message = entry.Message;
                return true;
            }
            message = null;
            return false;
        }

        private void ObservePendingLocked()
        {
            maxPending = Math.Max(maxPending, reliable.Count + latest.Count);
        }

        private readonly struct Entry
        {
            public Entry(object owner, string message)
            {
                Owner = owner;
                Message = message;
            }

            public object Owner { get; }
            public string Message { get; }
        }
    }

    public sealed class ThreeUnityOutboundBufferSnapshot
    {
        public long ReliableQueued { get; internal set; }
        public long LatestQueued { get; internal set; }
        public long LatestCoalesced { get; internal set; }
        public long ReliableBackpressureRejected { get; internal set; }
        public long ReliableDropped { get; internal set; }
        public long OwnerPurgedReliable { get; internal set; }
        public long OwnerPurgedLatest { get; internal set; }
        public long OwnerPurged => OwnerPurgedReliable + OwnerPurgedLatest;
        public long ReliableBurstYields { get; internal set; }
        public long Dequeued { get; internal set; }
        public int PendingReliable { get; internal set; }
        public int PendingLatest { get; internal set; }
        public int MaxPending { get; internal set; }
    }
}
