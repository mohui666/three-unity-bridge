using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ThreeUnity.Bridge
{
    public enum ThreeUnityHostLifecycleState
    {
        Stopped,
        WaitingToLaunch,
        Starting,
        Connected,
        Retiring,
    }

    public enum ThreeUnityHostFaultReason
    {
        None,
        HostExitedBeforeConnect,
        HostExited,
        ReaderEof,
        ReaderIOException,
        WriterIOException,
        HostLaunchFailure,
        ConnectTimeout,
        PageReadyTimeout,
        HostFatal,
        InboundOverflow,
    }

    /// <summary>
    /// Main-thread state machine for one physical WebView Host at a time. Threaded
    /// pipe code may only report generation-tagged signals; it never decides when
    /// to launch or retire a process.
    /// </summary>
    public sealed class ThreeUnityWebBridgeLifecycle
    {
        public const long DefaultConnectTimeoutMilliseconds = 10000;
        public const long DefaultPageReadyTimeoutMilliseconds = 20000;
        public const long DefaultPageStabilityWindowMilliseconds = 2000;

        private static readonly int[] RetryDelaysMilliseconds = { 250, 500, 1000, 2000, 4000 };

        private readonly long connectTimeoutMilliseconds;
        private readonly long pageReadyTimeoutMilliseconds;
        private readonly long pageStabilityWindowMilliseconds;
        private ThreeUnityHostLifecycleState state = ThreeUnityHostLifecycleState.Stopped;
        private ThreeUnityHostFaultReason lastDisconnectReason;
        private bool terminalStop;
        private long nextLaunchAtMilliseconds;
        private long connectDeadlineAtMilliseconds = long.MaxValue;
        private bool hasPendingConnectDeadline;
        private long pageReadyDeadlineAtMilliseconds = long.MaxValue;
        private bool hasPendingPageReadyDeadline;
        private long pageStabilityDeadlineAtMilliseconds = long.MaxValue;
        private bool hasPendingPageStabilityDeadline;
        private long pageGeneration;
        private long connectionGeneration;
        private int retryDelayIndex;
        private int lastRetryDelayMilliseconds;
        private long launches;
        private long relaunches;
        private long successfulConnections;
        private bool currentPageReady;
        private bool currentBridgeReady;
        private bool backoffResetForCurrentGeneration;
        private long disconnects;
        private long generationRejected;
        private long duplicateFaultsRejected;

        public ThreeUnityHostLifecycleState State => state;
        public ThreeUnityHostFaultReason LastDisconnectReason => lastDisconnectReason;
        public long PageGeneration => pageGeneration;
        public long ConnectionGeneration => connectionGeneration;
        public long NextLaunchAtMilliseconds => nextLaunchAtMilliseconds;
        public long ConnectTimeoutMilliseconds => connectTimeoutMilliseconds;
        public long ConnectDeadlineAtMilliseconds => connectDeadlineAtMilliseconds;
        public bool HasPendingConnectDeadline => hasPendingConnectDeadline;
        public long PageReadyTimeoutMilliseconds => pageReadyTimeoutMilliseconds;
        public long PageReadyDeadlineAtMilliseconds => pageReadyDeadlineAtMilliseconds;
        public bool HasPendingPageReadyDeadline => hasPendingPageReadyDeadline;
        public long PageStabilityWindowMilliseconds => pageStabilityWindowMilliseconds;
        public long PageStabilityDeadlineAtMilliseconds => pageStabilityDeadlineAtMilliseconds;
        public bool HasPendingPageStabilityDeadline => hasPendingPageStabilityDeadline;
        public int LastRetryDelayMilliseconds => lastRetryDelayMilliseconds;
        public long Launches => launches;
        public long Relaunches => relaunches;
        public long SuccessfulConnections => successfulConnections;
        public bool CurrentPageReady => currentPageReady;
        public bool CurrentBridgeReady => currentBridgeReady;
        public bool BackoffResetForCurrentGeneration => backoffResetForCurrentGeneration;
        public long Disconnects => disconnects;
        public long GenerationRejected => generationRejected;
        public long DuplicateFaultsRejected => duplicateFaultsRejected;
        public bool IsStopped => terminalStop;

        public ThreeUnityWebBridgeLifecycle(
            long connectTimeoutMilliseconds = DefaultConnectTimeoutMilliseconds,
            long pageReadyTimeoutMilliseconds = DefaultPageReadyTimeoutMilliseconds,
            long pageStabilityWindowMilliseconds = DefaultPageStabilityWindowMilliseconds)
        {
            if (connectTimeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(connectTimeoutMilliseconds));
            if (pageReadyTimeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(pageReadyTimeoutMilliseconds));
            if (pageStabilityWindowMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(pageStabilityWindowMilliseconds));
            this.connectTimeoutMilliseconds = connectTimeoutMilliseconds;
            this.pageReadyTimeoutMilliseconds = pageReadyTimeoutMilliseconds;
            this.pageStabilityWindowMilliseconds = pageStabilityWindowMilliseconds;
        }

        public bool Start(long nowMilliseconds)
        {
            if (terminalStop || state != ThreeUnityHostLifecycleState.Stopped)
                return false;

            nextLaunchAtMilliseconds = nowMilliseconds;
            state = ThreeUnityHostLifecycleState.WaitingToLaunch;
            return true;
        }

        public bool TryBeginLaunch(
            long nowMilliseconds,
            bool exactPriorHostHasExited,
            out long newPageGeneration,
            out long newConnectionGeneration)
        {
            newPageGeneration = pageGeneration;
            newConnectionGeneration = connectionGeneration;
            if (terminalStop
                || state != ThreeUnityHostLifecycleState.WaitingToLaunch
                || nowMilliseconds < nextLaunchAtMilliseconds
                || !exactPriorHostHasExited)
                return false;

            pageGeneration++;
            connectionGeneration++;
            launches++;
            if (launches > 1)
                relaunches++;
            newPageGeneration = pageGeneration;
            newConnectionGeneration = connectionGeneration;
            state = ThreeUnityHostLifecycleState.Starting;
            currentPageReady = false;
            currentBridgeReady = false;
            backoffResetForCurrentGeneration = false;
            ClearPageReadyDeadline();
            ClearPageStabilityDeadline();
            connectDeadlineAtMilliseconds = AddSaturated(
                nowMilliseconds,
                connectTimeoutMilliseconds);
            hasPendingConnectDeadline = true;
            return true;
        }

        public bool TryMarkConnected(long candidatePageGeneration, long candidateConnectionGeneration)
        {
            return TryMarkConnected(candidatePageGeneration, candidateConnectionGeneration, 0);
        }

        public bool TryMarkConnected(
            long candidatePageGeneration,
            long candidateConnectionGeneration,
            long nowMilliseconds)
        {
            if (!IsCurrent(candidatePageGeneration, candidateConnectionGeneration))
            {
                generationRejected++;
                return false;
            }

            if (terminalStop || state != ThreeUnityHostLifecycleState.Starting)
                return false;

            state = ThreeUnityHostLifecycleState.Connected;
            connectDeadlineAtMilliseconds = long.MaxValue;
            hasPendingConnectDeadline = false;
            if (!currentPageReady)
            {
                pageReadyDeadlineAtMilliseconds = AddSaturated(
                    nowMilliseconds,
                    pageReadyTimeoutMilliseconds);
                hasPendingPageReadyDeadline = true;
            }
            successfulConnections++;
            return true;
        }

        /// <summary>
        /// Confirms that WebView2 completed the first page navigation for the
        /// current physical Host. Navigation clears its own deadline and starts
        /// a stability window; it does not by itself reset restart backoff.
        /// </summary>
        public bool TryMarkPageReady(
            long candidatePageGeneration,
            long candidateConnectionGeneration)
        {
            return TryMarkPageReady(candidatePageGeneration, candidateConnectionGeneration, 0);
        }

        public bool TryMarkPageReady(
            long candidatePageGeneration,
            long candidateConnectionGeneration,
            long nowMilliseconds)
        {
            if (!IsCurrent(candidatePageGeneration, candidateConnectionGeneration))
            {
                generationRejected++;
                return false;
            }

            if (terminalStop
                || (state != ThreeUnityHostLifecycleState.Starting
                    && state != ThreeUnityHostLifecycleState.Connected)
                || currentPageReady)
                return false;

            currentPageReady = true;
            ClearPageReadyDeadline();
            if (currentBridgeReady)
            {
                ClearPageStabilityDeadline();
                ResetRetryBackoff();
            }
            else if (!backoffResetForCurrentGeneration)
            {
                pageStabilityDeadlineAtMilliseconds = AddSaturated(
                    nowMilliseconds,
                    pageStabilityWindowMilliseconds);
                hasPendingPageStabilityDeadline = true;
            }
            return true;
        }

        /// <summary>
        /// Records the first valid message emitted by the current web page. A
        /// message is stronger stability evidence than navigation completion, but
        /// restart backoff resets only after both have been observed; an early
        /// hello cannot hide a later navigation failure.
        /// </summary>
        public bool TryMarkBridgeReady(
            long candidatePageGeneration,
            long candidateConnectionGeneration)
        {
            if (!IsCurrent(candidatePageGeneration, candidateConnectionGeneration))
            {
                generationRejected++;
                return false;
            }

            if (terminalStop
                || (state != ThreeUnityHostLifecycleState.Starting
                    && state != ThreeUnityHostLifecycleState.Connected)
                || currentBridgeReady)
                return false;

            currentBridgeReady = true;
            if (currentPageReady)
            {
                ClearPageStabilityDeadline();
                ResetRetryBackoff();
            }
            return true;
        }

        /// <summary>
        /// Resets restart backoff for pages that do not emit bridge messages once
        /// the current navigation has survived the configured stability window.
        /// </summary>
        public bool TryMarkPageStable(long nowMilliseconds)
        {
            if (terminalStop
                || state != ThreeUnityHostLifecycleState.Connected
                || !currentPageReady
                || backoffResetForCurrentGeneration
                || !hasPendingPageStabilityDeadline
                || nowMilliseconds < pageStabilityDeadlineAtMilliseconds)
                return false;

            ClearPageStabilityDeadline();
            ResetRetryBackoff();
            return true;
        }

        public bool TryReportFault(
            long candidatePageGeneration,
            long candidateConnectionGeneration,
            ThreeUnityHostFaultReason reason)
        {
            if (!IsCurrent(candidatePageGeneration, candidateConnectionGeneration))
            {
                generationRejected++;
                return false;
            }

            if (terminalStop)
                return false;

            if (state != ThreeUnityHostLifecycleState.Starting
                && state != ThreeUnityHostLifecycleState.Connected)
            {
                duplicateFaultsRejected++;
                return false;
            }

            if (reason == ThreeUnityHostFaultReason.None)
                throw new ArgumentOutOfRangeException(nameof(reason));

            state = ThreeUnityHostLifecycleState.Retiring;
            currentPageReady = false;
            currentBridgeReady = false;
            backoffResetForCurrentGeneration = false;
            connectDeadlineAtMilliseconds = long.MaxValue;
            hasPendingConnectDeadline = false;
            ClearPageReadyDeadline();
            ClearPageStabilityDeadline();
            lastDisconnectReason = reason;
            disconnects++;
            return true;
        }

        /// <summary>
        /// Upgrades the diagnostic reason for an already-retiring current
        /// generation without counting a second disconnect. This is used when a
        /// Host's fatal stderr record arrives just after pipe EOF won the race.
        /// </summary>
        public bool TryRefineRetiringFault(
            long candidatePageGeneration,
            long candidateConnectionGeneration,
            ThreeUnityHostFaultReason reason)
        {
            if (!IsCurrent(candidatePageGeneration, candidateConnectionGeneration))
            {
                generationRejected++;
                return false;
            }
            if (terminalStop || state != ThreeUnityHostLifecycleState.Retiring)
                return false;
            if (reason == ThreeUnityHostFaultReason.None)
                throw new ArgumentOutOfRangeException(nameof(reason));
            if (lastDisconnectReason == reason)
                return false;
            lastDisconnectReason = reason;
            return true;
        }

        /// <summary>
        /// Retires the currently-starting generation when its pipe connection did
        /// not become ready by the launch deadline. This method is intended to be
        /// polled by the main thread; it never applies a deadline retained from a
        /// prior generation.
        /// </summary>
        public bool TryReportConnectTimeout(
            long nowMilliseconds,
            out long timedOutPageGeneration,
            out long timedOutConnectionGeneration)
        {
            timedOutPageGeneration = pageGeneration;
            timedOutConnectionGeneration = connectionGeneration;
            if (terminalStop
                || state != ThreeUnityHostLifecycleState.Starting
                || !hasPendingConnectDeadline
                || nowMilliseconds < connectDeadlineAtMilliseconds)
                return false;

            return TryReportFault(
                pageGeneration,
                connectionGeneration,
                ThreeUnityHostFaultReason.ConnectTimeout);
        }

        /// <summary>
        /// Retires a pipe-connected generation whose first WebView navigation did
        /// not complete by its independent page-ready deadline.
        /// </summary>
        public bool TryReportPageReadyTimeout(
            long nowMilliseconds,
            out long timedOutPageGeneration,
            out long timedOutConnectionGeneration)
        {
            timedOutPageGeneration = pageGeneration;
            timedOutConnectionGeneration = connectionGeneration;
            if (terminalStop
                || state != ThreeUnityHostLifecycleState.Connected
                || currentPageReady
                || !hasPendingPageReadyDeadline
                || nowMilliseconds < pageReadyDeadlineAtMilliseconds)
                return false;

            return TryReportFault(
                pageGeneration,
                connectionGeneration,
                ThreeUnityHostFaultReason.PageReadyTimeout);
        }

        public bool CompleteRetirement(
            long candidatePageGeneration,
            long candidateConnectionGeneration,
            long nowMilliseconds)
        {
            if (!IsCurrent(candidatePageGeneration, candidateConnectionGeneration))
            {
                generationRejected++;
                return false;
            }

            if (terminalStop || state != ThreeUnityHostLifecycleState.Retiring)
                return false;

            var delayIndex = Math.Min(retryDelayIndex, RetryDelaysMilliseconds.Length - 1);
            lastRetryDelayMilliseconds = RetryDelaysMilliseconds[delayIndex];
            if (retryDelayIndex < RetryDelaysMilliseconds.Length - 1)
                retryDelayIndex++;
            nextLaunchAtMilliseconds = nowMilliseconds + lastRetryDelayMilliseconds;
            state = ThreeUnityHostLifecycleState.WaitingToLaunch;
            currentPageReady = false;
            currentBridgeReady = false;
            backoffResetForCurrentGeneration = false;
            connectDeadlineAtMilliseconds = long.MaxValue;
            hasPendingConnectDeadline = false;
            ClearPageReadyDeadline();
            ClearPageStabilityDeadline();
            return true;
        }

        public bool CanAcceptGeneration(long candidatePageGeneration, long candidateConnectionGeneration)
        {
            return !terminalStop
                && IsCurrent(candidatePageGeneration, candidateConnectionGeneration)
                && (state == ThreeUnityHostLifecycleState.Starting
                    || state == ThreeUnityHostLifecycleState.Connected);
        }

        public void RecordGenerationRejection()
        {
            generationRejected++;
        }

        public void RecordGenerationRejections(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            generationRejected += count;
        }

        public void Stop()
        {
            terminalStop = true;
            state = ThreeUnityHostLifecycleState.Stopped;
            currentPageReady = false;
            currentBridgeReady = false;
            backoffResetForCurrentGeneration = false;
            nextLaunchAtMilliseconds = long.MaxValue;
            connectDeadlineAtMilliseconds = long.MaxValue;
            hasPendingConnectDeadline = false;
            ClearPageReadyDeadline();
            ClearPageStabilityDeadline();
        }

        public static string BuildStorageIdentifier(string rawProductName)
        {
            var raw = string.IsNullOrWhiteSpace(rawProductName) ? "three-unity-game" : rawProductName.Trim();
            var slug = new StringBuilder(40);
            var previousWasSeparator = false;
            foreach (var character in raw)
            {
                if ((character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9'))
                {
                    if (slug.Length >= 40)
                        break;
                    slug.Append(char.ToLowerInvariant(character));
                    previousWasSeparator = false;
                    continue;
                }

                if (previousWasSeparator || slug.Length == 0)
                    continue;
                slug.Append('-');
                previousWasSeparator = true;
            }

            while (slug.Length > 0 && slug[slug.Length - 1] == '-')
                slug.Length--;
            if (slug.Length == 0)
                slug.Append("game");

            byte[] digest;
            using (var sha256 = SHA256.Create())
                digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(raw));
            var suffix = new StringBuilder(12);
            for (var index = 0; index < 6; index++)
                suffix.Append(digest[index].ToString("x2"));
            return slug + "-" + suffix;
        }

        private bool IsCurrent(long candidatePageGeneration, long candidateConnectionGeneration)
        {
            return candidatePageGeneration > 0
                && candidatePageGeneration == pageGeneration
                && candidateConnectionGeneration == connectionGeneration;
        }

        private void ResetRetryBackoff()
        {
            retryDelayIndex = 0;
            lastRetryDelayMilliseconds = 0;
            backoffResetForCurrentGeneration = true;
        }

        private void ClearPageReadyDeadline()
        {
            pageReadyDeadlineAtMilliseconds = long.MaxValue;
            hasPendingPageReadyDeadline = false;
        }

        private void ClearPageStabilityDeadline()
        {
            pageStabilityDeadlineAtMilliseconds = long.MaxValue;
            hasPendingPageStabilityDeadline = false;
        }

        private static long AddSaturated(long value, long positiveDelta)
        {
            return value > long.MaxValue - positiveDelta
                ? long.MaxValue
                : value + positiveDelta;
        }
    }

    /// <summary>
    /// A queue whose entries retain the physical page/pipe generation that created
    /// them. Consumers discard all entries from retired generations before delivery.
    /// </summary>
    public sealed class ThreeUnityGenerationQueue<T>
    {
        public const int DefaultCapacity = 1024;
        public const int DefaultStaleWorkBudget = 64;

        private readonly object sync = new object();
        private readonly Queue<Entry> queue;
        private readonly int capacity;
        private long overflowDropped;

        public ThreeUnityGenerationQueue(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
            queue = new Queue<Entry>(Math.Min(capacity, DefaultCapacity));
        }

        public int Capacity => capacity;
        public int Pending
        {
            get
            {
                lock (sync)
                    return queue.Count;
            }
        }

        public long OverflowDropped
        {
            get
            {
                lock (sync)
                    return overflowDropped;
            }
        }

        public void Enqueue(long pageGeneration, long connectionGeneration, T value)
        {
            lock (sync)
            {
                // Keep the newest bounded set. A page producing faster than Unity
                // can consume therefore cannot grow memory without limit.
                if (queue.Count == capacity)
                {
                    queue.Dequeue();
                    overflowDropped++;
                }
                queue.Enqueue(new Entry(pageGeneration, connectionGeneration, value));
            }
        }

        public bool TryDequeueCurrent(
            long pageGeneration,
            long connectionGeneration,
            out T value,
            out int rejected)
        {
            return TryDequeueCurrent(
                pageGeneration,
                connectionGeneration,
                DefaultStaleWorkBudget,
                out value,
                out rejected);
        }

        public bool TryDequeueCurrent(
            long pageGeneration,
            long connectionGeneration,
            int staleWorkBudget,
            out T value,
            out int rejected)
        {
            if (staleWorkBudget < 0)
                throw new ArgumentOutOfRangeException(nameof(staleWorkBudget));

            rejected = 0;
            lock (sync)
            {
                while (queue.Count > 0)
                {
                    var entry = queue.Peek();
                    if (entry.PageGeneration != pageGeneration
                        || entry.ConnectionGeneration != connectionGeneration)
                    {
                        if (rejected >= staleWorkBudget)
                            break;
                        queue.Dequeue();
                        rejected++;
                        continue;
                    }

                    queue.Dequeue();
                    value = entry.Value;
                    return true;
                }
            }

            value = default(T);
            return false;
        }

        public int Clear()
        {
            lock (sync)
            {
                var removed = queue.Count;
                queue.Clear();
                return removed;
            }
        }

        private readonly struct Entry
        {
            public Entry(long pageGeneration, long connectionGeneration, T value)
            {
                PageGeneration = pageGeneration;
                ConnectionGeneration = connectionGeneration;
                Value = value;
            }

            public long PageGeneration { get; }
            public long ConnectionGeneration { get; }
            public T Value { get; }
        }
    }

    /// <summary>
    /// Keeps lifetime outbound counters while pending counts remain scoped to the
    /// currently writable physical connection.
    /// </summary>
    public sealed class ThreeUnityOutboundMetricsAccumulator
    {
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

        public void Retire(ThreeUnityOutboundBufferSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            reliableQueued += snapshot.ReliableQueued;
            latestQueued += snapshot.LatestQueued;
            latestCoalesced += snapshot.LatestCoalesced;
            reliableBackpressureRejected += snapshot.ReliableBackpressureRejected;
            // Pending reliable entries become actual losses only when their
            // physical connection is retired. Full-queue attempts that callers
            // can retry remain a separate backpressure counter.
            reliableDropped += snapshot.ReliableDropped + snapshot.PendingReliable;
            ownerPurgedReliable += snapshot.OwnerPurgedReliable;
            ownerPurgedLatest += snapshot.OwnerPurgedLatest;
            reliableBurstYields += snapshot.ReliableBurstYields;
            dequeued += snapshot.Dequeued;
            maxPending = Math.Max(maxPending, snapshot.MaxPending);
        }

        public ThreeUnityOutboundBufferSnapshot Combine(ThreeUnityOutboundBufferSnapshot current)
        {
            current = current ?? new ThreeUnityOutboundBuffer().Snapshot();
            return new ThreeUnityOutboundBufferSnapshot
            {
                ReliableQueued = reliableQueued + current.ReliableQueued,
                LatestQueued = latestQueued + current.LatestQueued,
                LatestCoalesced = latestCoalesced + current.LatestCoalesced,
                ReliableBackpressureRejected = reliableBackpressureRejected
                    + current.ReliableBackpressureRejected,
                ReliableDropped = reliableDropped + current.ReliableDropped,
                OwnerPurgedReliable = ownerPurgedReliable + current.OwnerPurgedReliable,
                OwnerPurgedLatest = ownerPurgedLatest + current.OwnerPurgedLatest,
                ReliableBurstYields = reliableBurstYields + current.ReliableBurstYields,
                Dequeued = dequeued + current.Dequeued,
                PendingReliable = current.PendingReliable,
                PendingLatest = current.PendingLatest,
                MaxPending = Math.Max(maxPending, current.MaxPending),
            };
        }
    }
}
