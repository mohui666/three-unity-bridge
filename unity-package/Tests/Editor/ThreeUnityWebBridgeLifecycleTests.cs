using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ThreeUnity.Bridge.Tests
{
    public sealed class ThreeUnityWebBridgeLifecycleTests
    {
        [Test]
        public void HostExitBeforeConnectSchedulesFirstBoundedRetry()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle();
            Assert.That(lifecycle.Start(100), Is.True);
            Assert.That(lifecycle.TryBeginLaunch(100, true, out var page, out var connection), Is.True);

            Assert.That(lifecycle.TryReportFault(
                page,
                connection,
                ThreeUnityHostFaultReason.HostExitedBeforeConnect), Is.True);
            Assert.That(lifecycle.CompleteRetirement(page, connection, 125), Is.True);

            Assert.That(lifecycle.LastDisconnectReason,
                Is.EqualTo(ThreeUnityHostFaultReason.HostExitedBeforeConnect));
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));
            Assert.That(lifecycle.TryBeginLaunch(374, true, out _, out _), Is.False);
            Assert.That(lifecycle.TryBeginLaunch(375, true, out var nextPage, out _), Is.True);
            Assert.That(nextPage, Is.EqualTo(page + 1));
            Assert.That(lifecycle.Relaunches, Is.EqualTo(1));
        }

        [Test]
        public void ReaderEofAndWriterFailureConvergeToOneFaultPerGeneration()
        {
            var lifecycle = StartedLifecycle(out var page, out var connection);

            Assert.That(lifecycle.TryReportFault(
                page,
                connection,
                ThreeUnityHostFaultReason.ReaderEof), Is.True);
            Assert.That(lifecycle.TryReportFault(
                page,
                connection,
                ThreeUnityHostFaultReason.WriterIOException), Is.False);
            Assert.That(lifecycle.Disconnects, Is.EqualTo(1));
            Assert.That(lifecycle.DuplicateFaultsRejected, Is.EqualTo(1));

            var writerOnly = StartedLifecycle(out page, out connection);
            Assert.That(writerOnly.TryReportFault(
                page,
                connection,
                ThreeUnityHostFaultReason.WriterIOException), Is.True);
            Assert.That(writerOnly.State, Is.EqualTo(ThreeUnityHostLifecycleState.Retiring));
            Assert.That(writerOnly.Disconnects, Is.EqualTo(1));
        }

        [Test]
        public void StaleGenerationSignalsAreRejected()
        {
            var lifecycle = StartedLifecycle(out var firstPage, out var firstConnection);
            Assert.That(lifecycle.TryReportFault(
                firstPage,
                firstConnection,
                ThreeUnityHostFaultReason.ReaderEof), Is.True);
            Assert.That(lifecycle.CompleteRetirement(firstPage, firstConnection, 0), Is.True);
            Assert.That(lifecycle.TryBeginLaunch(250, true, out var currentPage, out var currentConnection), Is.True);

            Assert.That(lifecycle.TryMarkConnected(firstPage, firstConnection), Is.False);
            Assert.That(lifecycle.TryReportFault(
                firstPage,
                firstConnection,
                ThreeUnityHostFaultReason.WriterIOException), Is.False);
            Assert.That(lifecycle.CanAcceptGeneration(firstPage, firstConnection), Is.False);
            Assert.That(lifecycle.CanAcceptGeneration(currentPage, currentConnection), Is.True);
            Assert.That(lifecycle.GenerationRejected, Is.EqualTo(2));
        }

        [Test]
        public void BackoffProgressesToFourSecondsAndBridgeMessageResetsIt()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle();
            Assert.That(lifecycle.Start(0), Is.True);
            var expectedDelays = new[] { 250, 500, 1000, 2000, 4000, 4000 };
            long now = 0;

            foreach (var expectedDelay in expectedDelays)
            {
                Assert.That(lifecycle.TryBeginLaunch(now, true, out var page, out var connection), Is.True);
                Assert.That(lifecycle.TryReportFault(
                    page,
                    connection,
                    ThreeUnityHostFaultReason.HostExitedBeforeConnect), Is.True);
                Assert.That(lifecycle.CompleteRetirement(page, connection, now), Is.True);
                Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(expectedDelay));
                now = lifecycle.NextLaunchAtMilliseconds;
            }

            Assert.That(lifecycle.TryBeginLaunch(now, true, out var recoveredPage, out var recoveredConnection), Is.True);
            Assert.That(lifecycle.TryMarkConnected(recoveredPage, recoveredConnection, now), Is.True);
            Assert.That(lifecycle.TryMarkPageReady(recoveredPage, recoveredConnection, now), Is.True);
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.False);
            Assert.That(lifecycle.TryMarkBridgeReady(recoveredPage, recoveredConnection), Is.True);
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.True);
            Assert.That(lifecycle.TryReportFault(
                recoveredPage,
                recoveredConnection,
                ThreeUnityHostFaultReason.ReaderEof), Is.True);
            Assert.That(lifecycle.CompleteRetirement(recoveredPage, recoveredConnection, now), Is.True);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));
        }

        [Test]
        public void PipeAndNavigationDoNotResetBackoffBeforeStabilityEvidence()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(
                pageStabilityWindowMilliseconds: 20);
            lifecycle.Start(0);
            Assert.That(lifecycle.TryBeginLaunch(0, true, out var page, out var connection), Is.True);
            Assert.That(lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.HostExitedBeforeConnect), Is.True);
            Assert.That(lifecycle.CompleteRetirement(page, connection, 0), Is.True);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));

            Assert.That(lifecycle.TryBeginLaunch(250, true, out page, out connection), Is.True);
            Assert.That(lifecycle.TryMarkConnected(page, connection), Is.True);
            Assert.That(lifecycle.CurrentPageReady, Is.False);
            Assert.That(lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.HostExited), Is.True);
            Assert.That(lifecycle.CompleteRetirement(page, connection, 250), Is.True);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(500));

            Assert.That(lifecycle.TryBeginLaunch(750, true, out page, out connection), Is.True);
            Assert.That(lifecycle.TryMarkConnected(page, connection, 750), Is.True);
            Assert.That(lifecycle.TryMarkPageReady(page, connection, 751), Is.True);
            Assert.That(lifecycle.TryMarkPageReady(page, connection, 751), Is.False);
            Assert.That(lifecycle.CurrentPageReady, Is.True);
            Assert.That(lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.HostExited), Is.True);
            Assert.That(lifecycle.CompleteRetirement(page, connection, 750), Is.True);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(1000));

            Assert.That(lifecycle.TryBeginLaunch(1750, true, out page, out connection), Is.True);
            Assert.That(lifecycle.TryMarkConnected(page, connection, 1750), Is.True);
            Assert.That(lifecycle.TryMarkPageReady(page, connection, 1751), Is.True);
            Assert.That(lifecycle.TryMarkPageStable(1770), Is.False);
            Assert.That(lifecycle.TryMarkPageStable(1771), Is.True);
            Assert.That(lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.HostExited), Is.True);
            Assert.That(lifecycle.CompleteRetirement(page, connection, 1771), Is.True);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));
        }

        [Test]
        public void NoSecondLaunchOccursUntilExactTrackedHostHasExited()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle();
            lifecycle.Start(0);
            Assert.That(lifecycle.TryBeginLaunch(0, true, out var page, out var connection), Is.True);
            Assert.That(lifecycle.TryBeginLaunch(10000, true, out _, out _), Is.False);

            Assert.That(lifecycle.TryReportFault(
                page,
                connection,
                ThreeUnityHostFaultReason.ReaderIOException), Is.True);
            Assert.That(lifecycle.TryBeginLaunch(10000, true, out _, out _), Is.False);
            Assert.That(lifecycle.CompleteRetirement(page, connection, 0), Is.True);
            Assert.That(lifecycle.TryBeginLaunch(250, false, out _, out _), Is.False);
            Assert.That(lifecycle.TryBeginLaunch(250, true, out _, out _), Is.True);
        }

        [Test]
        public void ShutdownIsIdempotentAndNeverRelaunches()
        {
            var lifecycle = StartedLifecycle(out var page, out var connection);

            lifecycle.Stop();
            lifecycle.Stop();

            Assert.That(lifecycle.State, Is.EqualTo(ThreeUnityHostLifecycleState.Stopped));
            Assert.That(lifecycle.TryReportFault(
                page,
                connection,
                ThreeUnityHostFaultReason.ReaderEof), Is.False);
            Assert.That(lifecycle.CompleteRetirement(page, connection, 0), Is.False);
            Assert.That(lifecycle.TryBeginLaunch(long.MaxValue, true, out _, out _), Is.False);
            Assert.That(lifecycle.Start(0), Is.False);
            Assert.That(lifecycle.Relaunches, Is.Zero);
        }

        [Test]
        public void StartingGenerationTimesOutAtDeadlineButNotBeforeIt()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(100);
            Assert.That(lifecycle.Start(500), Is.True);
            Assert.That(lifecycle.TryBeginLaunch(500, true, out var page, out var connection), Is.True);
            Assert.That(lifecycle.ConnectDeadlineAtMilliseconds, Is.EqualTo(600));

            Assert.That(lifecycle.TryReportConnectTimeout(599, out var beforePage, out var beforeConnection), Is.False);
            Assert.That(beforePage, Is.EqualTo(page));
            Assert.That(beforeConnection, Is.EqualTo(connection));
            Assert.That(lifecycle.State, Is.EqualTo(ThreeUnityHostLifecycleState.Starting));

            Assert.That(lifecycle.TryReportConnectTimeout(600, out var timedOutPage, out var timedOutConnection), Is.True);
            Assert.That(timedOutPage, Is.EqualTo(page));
            Assert.That(timedOutConnection, Is.EqualTo(connection));
            Assert.That(lifecycle.State, Is.EqualTo(ThreeUnityHostLifecycleState.Retiring));
            Assert.That(lifecycle.LastDisconnectReason, Is.EqualTo(ThreeUnityHostFaultReason.ConnectTimeout));
            Assert.That(lifecycle.Disconnects, Is.EqualTo(1));
            Assert.That(lifecycle.HasPendingConnectDeadline, Is.False);
        }

        [Test]
        public void ConnectedOrStoppedGenerationCannotLaterTimeOut()
        {
            var connected = new ThreeUnityWebBridgeLifecycle(10);
            connected.Start(0);
            connected.TryBeginLaunch(0, true, out var page, out var connection);
            Assert.That(connected.TryMarkConnected(page, connection), Is.True);
            Assert.That(connected.HasPendingConnectDeadline, Is.False);
            Assert.That(connected.TryReportConnectTimeout(100, out _, out _), Is.False);
            Assert.That(connected.State, Is.EqualTo(ThreeUnityHostLifecycleState.Connected));

            var stopped = new ThreeUnityWebBridgeLifecycle(10);
            stopped.Start(0);
            stopped.TryBeginLaunch(0, true, out _, out _);
            stopped.Stop();
            Assert.That(stopped.TryReportConnectTimeout(100, out _, out _), Is.False);
            Assert.That(stopped.State, Is.EqualTo(ThreeUnityHostLifecycleState.Stopped));
            Assert.That(stopped.HasPendingConnectDeadline, Is.False);
        }

        [Test]
        public void RelaunchUsesOnlyNewGenerationDeadline()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(100);
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var oldPage, out var oldConnection);
            lifecycle.TryReportFault(oldPage, oldConnection, ThreeUnityHostFaultReason.ReaderEof);
            lifecycle.CompleteRetirement(oldPage, oldConnection, 10);
            Assert.That(lifecycle.TryBeginLaunch(260, true, out var page, out var connection), Is.True);

            Assert.That(page, Is.EqualTo(oldPage + 1));
            Assert.That(lifecycle.ConnectDeadlineAtMilliseconds, Is.EqualTo(360));
            Assert.That(lifecycle.TryReportConnectTimeout(100, out _, out _), Is.False);
            Assert.That(lifecycle.TryReportConnectTimeout(360, out var timedOutPage, out var timedOutConnection), Is.True);
            Assert.That(timedOutPage, Is.EqualTo(page));
            Assert.That(timedOutConnection, Is.EqualTo(connection));
        }

        [Test]
        public void ConnectedGenerationUsesIndependentPageReadyDeadline()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(
                connectTimeoutMilliseconds: 10,
                pageReadyTimeoutMilliseconds: 20,
                pageStabilityWindowMilliseconds: 5);
            lifecycle.Start(100);
            lifecycle.TryBeginLaunch(100, true, out var page, out var connection);

            Assert.That(lifecycle.ConnectDeadlineAtMilliseconds, Is.EqualTo(110));
            Assert.That(lifecycle.TryMarkConnected(page, connection, 105), Is.True);
            Assert.That(lifecycle.HasPendingConnectDeadline, Is.False);
            Assert.That(lifecycle.ConnectDeadlineAtMilliseconds, Is.EqualTo(long.MaxValue));
            Assert.That(lifecycle.HasPendingPageReadyDeadline, Is.True);
            Assert.That(lifecycle.PageReadyDeadlineAtMilliseconds, Is.EqualTo(125));
            Assert.That(lifecycle.TryReportConnectTimeout(1000, out _, out _), Is.False);

            Assert.That(lifecycle.TryReportPageReadyTimeout(124, out var beforePage, out var beforeConnection), Is.False);
            Assert.That(beforePage, Is.EqualTo(page));
            Assert.That(beforeConnection, Is.EqualTo(connection));
            Assert.That(lifecycle.TryReportPageReadyTimeout(125, out var timedOutPage, out var timedOutConnection), Is.True);
            Assert.That(timedOutPage, Is.EqualTo(page));
            Assert.That(timedOutConnection, Is.EqualTo(connection));
            Assert.That(lifecycle.State, Is.EqualTo(ThreeUnityHostLifecycleState.Retiring));
            Assert.That(lifecycle.LastDisconnectReason, Is.EqualTo(ThreeUnityHostFaultReason.PageReadyTimeout));
            Assert.That(lifecycle.HasPendingPageReadyDeadline, Is.False);
        }

        [Test]
        public void PageReadyStartsStabilityWindowWithoutImmediatelyResettingBackoff()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(
                pageReadyTimeoutMilliseconds: 100,
                pageStabilityWindowMilliseconds: 20);
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var page, out var connection);
            lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.HostExitedBeforeConnect);
            lifecycle.CompleteRetirement(page, connection, 0);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));

            lifecycle.TryBeginLaunch(250, true, out page, out connection);
            lifecycle.TryMarkConnected(page, connection, 260);
            Assert.That(lifecycle.PageReadyDeadlineAtMilliseconds, Is.EqualTo(360));
            Assert.That(lifecycle.TryMarkPageReady(page, connection, 270), Is.True);
            Assert.That(lifecycle.HasPendingPageReadyDeadline, Is.False);
            Assert.That(lifecycle.PageReadyDeadlineAtMilliseconds, Is.EqualTo(long.MaxValue));
            Assert.That(lifecycle.HasPendingPageStabilityDeadline, Is.True);
            Assert.That(lifecycle.PageStabilityDeadlineAtMilliseconds, Is.EqualTo(290));
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.False);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));
            Assert.That(lifecycle.TryReportPageReadyTimeout(1000, out _, out _), Is.False);

            Assert.That(lifecycle.TryMarkPageStable(289), Is.False);
            Assert.That(lifecycle.TryMarkPageStable(290), Is.True);
            Assert.That(lifecycle.TryMarkPageStable(291), Is.False);
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.True);
            Assert.That(lifecycle.HasPendingPageStabilityDeadline, Is.False);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.Zero);
        }

        [Test]
        public void FirstCurrentBridgeMessageImmediatelyResetsBackoffAndIsIdempotent()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(
                pageStabilityWindowMilliseconds: 2000);
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var page, out var connection);
            lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.ReaderEof);
            lifecycle.CompleteRetirement(page, connection, 0);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));

            lifecycle.TryBeginLaunch(250, true, out page, out connection);
            lifecycle.TryMarkConnected(page, connection, 251);
            lifecycle.TryMarkPageReady(page, connection, 252);
            Assert.That(lifecycle.HasPendingPageStabilityDeadline, Is.True);
            Assert.That(lifecycle.TryMarkBridgeReady(page, connection), Is.True);
            Assert.That(lifecycle.CurrentBridgeReady, Is.True);
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.True);
            Assert.That(lifecycle.HasPendingPageStabilityDeadline, Is.False);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.Zero);
            Assert.That(lifecycle.TryMarkBridgeReady(page, connection), Is.False);

            lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.HostExited);
            lifecycle.CompleteRetirement(page, connection, 252);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));
        }

        [Test]
        public void BridgeMessageBeforeNavigationReadyCannotResetBackoffEarly()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(
                pageReadyTimeoutMilliseconds: 100,
                pageStabilityWindowMilliseconds: 20);
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var page, out var connection);
            lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.ReaderEof);
            lifecycle.CompleteRetirement(page, connection, 0);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));

            lifecycle.TryBeginLaunch(250, true, out page, out connection);
            lifecycle.TryMarkConnected(page, connection, 251);
            Assert.That(lifecycle.TryMarkBridgeReady(page, connection), Is.True);
            Assert.That(lifecycle.CurrentBridgeReady, Is.True);
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.False);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));

            Assert.That(lifecycle.TryMarkPageReady(page, connection, 252), Is.True);
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.True);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.Zero);
            Assert.That(lifecycle.HasPendingPageStabilityDeadline, Is.False);
        }

        [Test]
        public void EarlyBridgeThenNavigationFailureKeepsIncreasingRetryDelay()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle();
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var page, out var connection);
            lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.HostExitedBeforeConnect);
            lifecycle.CompleteRetirement(page, connection, 0);
            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(250));

            lifecycle.TryBeginLaunch(250, true, out page, out connection);
            lifecycle.TryMarkConnected(page, connection, 251);
            lifecycle.TryMarkBridgeReady(page, connection);
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.False);
            lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.PageReadyTimeout);
            lifecycle.CompleteRetirement(page, connection, 252);

            Assert.That(lifecycle.LastRetryDelayMilliseconds, Is.EqualTo(500));
        }

        [Test]
        public void FatalDiagnosticCanRefinePipeEofWithoutCountingAnotherDisconnect()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle();
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var page, out var connection);
            lifecycle.TryMarkConnected(page, connection, 1);
            Assert.That(lifecycle.TryReportFault(
                page,
                connection,
                ThreeUnityHostFaultReason.ReaderEof), Is.True);

            Assert.That(lifecycle.TryRefineRetiringFault(
                page,
                connection,
                ThreeUnityHostFaultReason.HostFatal), Is.True);
            Assert.That(lifecycle.LastDisconnectReason, Is.EqualTo(ThreeUnityHostFaultReason.HostFatal));
            Assert.That(lifecycle.Disconnects, Is.EqualTo(1));
            Assert.That(lifecycle.TryRefineRetiringFault(
                page,
                connection,
                ThreeUnityHostFaultReason.HostFatal), Is.False);
        }

        [Test]
        public void StaleReadinessSignalsCannotChangeCurrentGenerationDeadlinesOrBackoff()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(
                pageReadyTimeoutMilliseconds: 100,
                pageStabilityWindowMilliseconds: 20);
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var oldPage, out var oldConnection);
            lifecycle.TryReportFault(oldPage, oldConnection, ThreeUnityHostFaultReason.ReaderEof);
            lifecycle.CompleteRetirement(oldPage, oldConnection, 0);
            lifecycle.TryBeginLaunch(250, true, out var page, out var connection);
            lifecycle.TryMarkConnected(page, connection, 260);
            var currentDeadline = lifecycle.PageReadyDeadlineAtMilliseconds;

            Assert.That(lifecycle.TryMarkPageReady(oldPage, oldConnection, 270), Is.False);
            Assert.That(lifecycle.TryMarkBridgeReady(oldPage, oldConnection), Is.False);
            Assert.That(lifecycle.GenerationRejected, Is.EqualTo(2));
            Assert.That(lifecycle.CurrentPageReady, Is.False);
            Assert.That(lifecycle.CurrentBridgeReady, Is.False);
            Assert.That(lifecycle.BackoffResetForCurrentGeneration, Is.False);
            Assert.That(lifecycle.PageReadyDeadlineAtMilliseconds, Is.EqualTo(currentDeadline));
            Assert.That(lifecycle.HasPendingPageReadyDeadline, Is.True);
        }

        [Test]
        public void FaultRetirementAndStopClearEveryReadinessDeadline()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(
                pageReadyTimeoutMilliseconds: 100,
                pageStabilityWindowMilliseconds: 20);
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var page, out var connection);
            lifecycle.TryMarkConnected(page, connection, 1);
            Assert.That(lifecycle.HasPendingPageReadyDeadline, Is.True);
            lifecycle.TryReportFault(page, connection, ThreeUnityHostFaultReason.HostExited);
            AssertAllReadinessDeadlinesCleared(lifecycle);
            lifecycle.CompleteRetirement(page, connection, 1);
            AssertAllReadinessDeadlinesCleared(lifecycle);

            lifecycle.TryBeginLaunch(251, true, out page, out connection);
            lifecycle.TryMarkConnected(page, connection, 252);
            lifecycle.TryMarkPageReady(page, connection, 253);
            Assert.That(lifecycle.HasPendingPageStabilityDeadline, Is.True);
            lifecycle.Stop();
            AssertAllReadinessDeadlinesCleared(lifecycle);
            Assert.That(lifecycle.TryReportPageReadyTimeout(long.MaxValue, out _, out _), Is.False);
            Assert.That(lifecycle.TryMarkPageStable(long.MaxValue), Is.False);
        }

        [Test]
        public void PageReadySignalBeforeConnectedSignalStillUsesCurrentGenerationOnly()
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle(
                pageReadyTimeoutMilliseconds: 100,
                pageStabilityWindowMilliseconds: 20);
            lifecycle.Start(0);
            lifecycle.TryBeginLaunch(0, true, out var page, out var connection);

            Assert.That(lifecycle.TryMarkPageReady(page, connection, 5), Is.True);
            Assert.That(lifecycle.TryMarkConnected(page, connection, 10), Is.True);
            Assert.That(lifecycle.HasPendingPageReadyDeadline, Is.False);
            Assert.That(lifecycle.PageStabilityDeadlineAtMilliseconds, Is.EqualTo(25));
            Assert.That(lifecycle.TryMarkPageStable(24), Is.False);
            Assert.That(lifecycle.TryMarkPageStable(25), Is.True);
        }

        [Test]
        public void GenerationQueueDropsOldPageBeforeReturningCurrentMessage()
        {
            var queue = new ThreeUnityGenerationQueue<string>();
            queue.Enqueue(1, 1, "old-page");
            queue.Enqueue(2, 2, "current-page");

            Assert.That(queue.TryDequeueCurrent(2, 2, out var value, out var rejected), Is.True);
            Assert.That(value, Is.EqualTo("current-page"));
            Assert.That(rejected, Is.EqualTo(1));
            Assert.That(queue.Pending, Is.Zero);
        }

        [Test]
        public void GenerationQueueReportsRejectedEntriesEvenWithoutCurrentMessage()
        {
            var queue = new ThreeUnityGenerationQueue<string>();
            queue.Enqueue(1, 1, "old-page");

            Assert.That(queue.TryDequeueCurrent(2, 2, out _, out var rejected), Is.False);
            Assert.That(rejected, Is.EqualTo(1));
            Assert.That(queue.Pending, Is.Zero);
        }

        [Test]
        public void GenerationQueueIsBoundedAndKeepsNewestEntries()
        {
            var queue = new ThreeUnityGenerationQueue<string>(2);
            queue.Enqueue(2, 2, "oldest");
            queue.Enqueue(2, 2, "middle");
            queue.Enqueue(2, 2, "newest");

            Assert.That(queue.Capacity, Is.EqualTo(2));
            Assert.That(queue.Pending, Is.EqualTo(2));
            Assert.That(queue.OverflowDropped, Is.EqualTo(1));
            Assert.That(queue.TryDequeueCurrent(2, 2, out var first, out var firstRejected), Is.True);
            Assert.That(first, Is.EqualTo("middle"));
            Assert.That(firstRejected, Is.Zero);
            Assert.That(queue.TryDequeueCurrent(2, 2, out var second, out _), Is.True);
            Assert.That(second, Is.EqualTo("newest"));
        }

        [Test]
        public void GenerationQueueLimitsStaleWorkPerDequeue()
        {
            var queue = new ThreeUnityGenerationQueue<string>(8);
            queue.Enqueue(1, 1, "stale-1");
            queue.Enqueue(1, 1, "stale-2");
            queue.Enqueue(1, 1, "stale-3");
            queue.Enqueue(2, 2, "current");

            Assert.That(queue.TryDequeueCurrent(2, 2, 2, out _, out var firstRejected), Is.False);
            Assert.That(firstRejected, Is.EqualTo(2));
            Assert.That(queue.Pending, Is.EqualTo(2));

            Assert.That(queue.TryDequeueCurrent(2, 2, 1, out var value, out var secondRejected), Is.True);
            Assert.That(secondRejected, Is.EqualTo(1));
            Assert.That(value, Is.EqualTo("current"));
            Assert.That(queue.Pending, Is.Zero);
        }

        [Test]
        public void LateDiagnosticFromRetiredHostIsRejectedBeforeCurrentDiagnostic()
        {
            var diagnostics = new ThreeUnityGenerationQueue<string>();
            diagnostics.Enqueue(7, 7, "THREE_UNITY_WEB_HOST_FATAL old");
            diagnostics.Enqueue(8, 8, "current diagnostic");

            Assert.That(diagnostics.TryDequeueCurrent(8, 8, out var value, out var rejected), Is.True);
            Assert.That(value, Is.EqualTo("current diagnostic"));
            Assert.That(rejected, Is.EqualTo(1));
        }

        [Test]
        public void RetiredOutboundCountersRemainVisibleWhilePendingIsCurrentOnly()
        {
            var accumulator = new ThreeUnityOutboundMetricsAccumulator();
            var retired = new ThreeUnityOutboundBuffer(1);
            Assert.That(retired.EnqueueReliable("first"), Is.True);
            Assert.That(retired.EnqueueReliable("overflow"), Is.False);
            retired.EnqueueLatest("state", "old");
            retired.EnqueueLatest("state", "new");
            accumulator.Retire(retired.Snapshot());

            var current = new ThreeUnityOutboundBuffer();
            Assert.That(current.EnqueueReliable("current"), Is.True);
            current.EnqueueLatest("state", "current-state");
            Assert.That(current.TryDequeue(out _), Is.True);
            var combined = accumulator.Combine(current.Snapshot());

            Assert.That(combined.ReliableQueued, Is.EqualTo(2));
            Assert.That(combined.LatestQueued, Is.EqualTo(3));
            Assert.That(combined.LatestCoalesced, Is.EqualTo(1));
            Assert.That(combined.ReliableDropped, Is.EqualTo(1));
            Assert.That(combined.Dequeued, Is.EqualTo(1));
            Assert.That(combined.PendingReliable, Is.Zero);
            Assert.That(combined.PendingLatest, Is.EqualTo(1));
            Assert.That(combined.MaxPending, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void StorageIdentifierIsStableSanitizedAndProductSpecific()
        {
            var first = ThreeUnityWebBridgeLifecycle.BuildStorageIdentifier("Name To Shop!");
            var same = ThreeUnityWebBridgeLifecycle.BuildStorageIdentifier("Name To Shop!");
            var other = ThreeUnityWebBridgeLifecycle.BuildStorageIdentifier("Little Cubes");
            var unicode = ThreeUnityWebBridgeLifecycle.BuildStorageIdentifier("商店");

            Assert.That(first, Is.EqualTo(same));
            Assert.That(first, Does.Match("^name-to-shop-[0-9a-f]{12}$"));
            Assert.That(other, Is.Not.EqualTo(first));
            Assert.That(unicode, Does.Match("^game-[0-9a-f]{12}$"));
        }

        [Test]
        public void LauncherStopIsIdempotentBeforeStart()
        {
            var gameObject = new GameObject("bridge-lifecycle-test");
            var launcher = gameObject.AddComponent<ThreeUnityWebBridgeLauncher>();
            var stop = typeof(ThreeUnityWebBridgeLauncher).GetMethod(
                "StopBridge",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(stop, Is.Not.Null);
            Assert.DoesNotThrow(() => stop.Invoke(launcher, null));
            Assert.DoesNotThrow(() => stop.Invoke(launcher, null));
            Assert.That(launcher.GetTransportMetrics().Relaunches, Is.Zero);
            Assert.DoesNotThrow(() => Object.DestroyImmediate(gameObject));
        }

        private static void AssertAllReadinessDeadlinesCleared(
            ThreeUnityWebBridgeLifecycle lifecycle)
        {
            Assert.That(lifecycle.HasPendingConnectDeadline, Is.False);
            Assert.That(lifecycle.ConnectDeadlineAtMilliseconds, Is.EqualTo(long.MaxValue));
            Assert.That(lifecycle.HasPendingPageReadyDeadline, Is.False);
            Assert.That(lifecycle.PageReadyDeadlineAtMilliseconds, Is.EqualTo(long.MaxValue));
            Assert.That(lifecycle.HasPendingPageStabilityDeadline, Is.False);
            Assert.That(lifecycle.PageStabilityDeadlineAtMilliseconds, Is.EqualTo(long.MaxValue));
        }

        private static ThreeUnityWebBridgeLifecycle StartedLifecycle(
            out long page,
            out long connection)
        {
            var lifecycle = new ThreeUnityWebBridgeLifecycle();
            Assert.That(lifecycle.Start(0), Is.True);
            Assert.That(lifecycle.TryBeginLaunch(0, true, out page, out connection), Is.True);
            return lifecycle;
        }
    }
}
