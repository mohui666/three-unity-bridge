using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace ThreeUnity.Bridge
{
    /// <summary>
    /// Thread-safe bridge output buffer. Control messages are reliable and bounded;
    /// high-frequency streams keep only their newest value so stale simulation states
    /// cannot build an unbounded queue behind a slow WebView or named pipe.
    /// </summary>
    public sealed class ThreeUnityOutboundBuffer
    {
        private readonly Queue<string> reliable = new Queue<string>();
        private readonly object reliableLock = new object();
        private readonly ConcurrentDictionary<string, string> latest = new ConcurrentDictionary<string, string>();
        private readonly int reliableCapacity;
        private int reliablePending;
        private long reliableQueued;
        private long latestQueued;
        private long latestCoalesced;
        private long reliableDropped;
        private long dequeued;
        private int maxPending;

        public ThreeUnityOutboundBuffer(int reliableCapacity = 1024)
        {
            if (reliableCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(reliableCapacity));
            this.reliableCapacity = reliableCapacity;
        }

        public bool EnqueueReliable(string message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            lock (reliableLock)
            {
                if (reliablePending >= reliableCapacity)
                {
                    Interlocked.Increment(ref reliableDropped);
                    return false;
                }

                reliable.Enqueue(message);
                reliablePending++;
            }

            Interlocked.Increment(ref reliableQueued);
            ObservePending();
            return true;
        }

        public void EnqueueLatest(string stream, string message)
        {
            if (string.IsNullOrWhiteSpace(stream))
                throw new ArgumentException("A latest-value stream key is required.", nameof(stream));
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (!latest.TryAdd(stream, message))
            {
                latest[stream] = message;
                Interlocked.Increment(ref latestCoalesced);
            }
            Interlocked.Increment(ref latestQueued);
            ObservePending();
        }

        public bool TryDequeue(out string message)
        {
            lock (reliableLock)
            {
                if (reliable.Count > 0)
                {
                    message = reliable.Dequeue();
                    reliablePending--;
                    Interlocked.Increment(ref dequeued);
                    return true;
                }
            }

            foreach (var pair in latest)
            {
                if (!latest.TryRemove(pair.Key, out message))
                    continue;
                Interlocked.Increment(ref dequeued);
                return true;
            }

            message = null;
            return false;
        }

        public ThreeUnityOutboundBufferSnapshot Snapshot()
        {
            return new ThreeUnityOutboundBufferSnapshot
            {
                ReliableQueued = Interlocked.Read(ref reliableQueued),
                LatestQueued = Interlocked.Read(ref latestQueued),
                LatestCoalesced = Interlocked.Read(ref latestCoalesced),
                ReliableDropped = Interlocked.Read(ref reliableDropped),
                Dequeued = Interlocked.Read(ref dequeued),
                PendingReliable = Math.Max(0, Volatile.Read(ref reliablePending)),
                PendingLatest = latest.Count,
                MaxPending = Volatile.Read(ref maxPending),
            };
        }

        private void ObservePending()
        {
            var value = Math.Max(0, Volatile.Read(ref reliablePending)) + latest.Count;
            var observed = Volatile.Read(ref maxPending);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(ref maxPending, value, observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }
    }

    public sealed class ThreeUnityOutboundBufferSnapshot
    {
        public long ReliableQueued { get; internal set; }
        public long LatestQueued { get; internal set; }
        public long LatestCoalesced { get; internal set; }
        public long ReliableDropped { get; internal set; }
        public long Dequeued { get; internal set; }
        public int PendingReliable { get; internal set; }
        public int PendingLatest { get; internal set; }
        public int MaxPending { get; internal set; }
    }
}
