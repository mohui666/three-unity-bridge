using ThreeUnity.WebHost;
using Xunit;

namespace ThreeUnityWebHost.Tests;

public sealed class HostRecoveryTests
{
    [Fact]
    public void IdentityIsStableForSameGameAcrossHostRelaunches()
    {
        var root = Path.Combine(Path.GetTempPath(), "three-unity-host-tests", "profiles");

        var first = HostIdentity.Create("Name To Shop", root);
        var second = HostIdentity.Create("  NAME TO SHOP  ", root);

        Assert.Equal(first, second);
        Assert.Equal("https", first.Origin.Scheme);
        Assert.Equal(first.VirtualHostName, first.Origin.Host);
        Assert.StartsWith(Path.GetFullPath(root), first.UserDataFolder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifferentGamesDoNotShareOriginOrUserData()
    {
        var root = Path.Combine(Path.GetTempPath(), "three-unity-host-tests", "profiles");

        var shop = HostIdentity.Create("Name To Shop", root);
        var cubes = HostIdentity.Create("Little Cubes", root);

        Assert.NotEqual(shop.StorageKey, cubes.StorageKey);
        Assert.NotEqual(shop.VirtualHostName, cubes.VirtualHostName);
        Assert.NotEqual(shop.UserDataFolder, cubes.UserDataFolder);
        Assert.NotEqual(shop.Origin, cubes.Origin);
    }

    [Fact]
    public void GamesWhoseReadableSlugsCollideStillRemainIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "three-unity-host-tests", "profiles");

        var slash = HostIdentity.Create("Game/A", root);
        var question = HostIdentity.Create("Game?A", root);

        Assert.StartsWith("game-a-", slash.StorageKey, StringComparison.Ordinal);
        Assert.StartsWith("game-a-", question.StorageKey, StringComparison.Ordinal);
        Assert.NotEqual(slash.StorageKey, question.StorageKey);
        Assert.NotEqual(slash.VirtualHostName, question.VirtualHostName);
    }

    [Theory]
    [InlineData("../../CON\\evil")]
    [InlineData("名称 商店 / 🚀")]
    [InlineData("...///...\\...")]
    public void UnsafeStorageIdsBecomeContainedDnsAndPathSegments(string storageId)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "three-unity-host-tests", "profiles"));

        var identity = HostIdentity.Create(storageId, root);
        var relative = Path.GetRelativePath(root, identity.UserDataFolder);

        Assert.False(Path.IsPathRooted(relative));
        Assert.DoesNotContain("..", relative, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, relative);
        Assert.Matches("^[a-z0-9-]+\\.threeunity\\.invalid$", identity.VirtualHostName);
        Assert.All(identity.VirtualHostName.Split('.'), label => Assert.InRange(label.Length, 1, 63));
    }

    [Fact]
    public void ExplicitStorageIdIsPreferredAndLegacyFallbackUsesFullLocation()
    {
        using var firstGame = TemporaryWebRoot.Create("game-a");
        using var secondGame = TemporaryWebRoot.Create("game-b");

        var explicitOptions = HostOptions.Parse(new[]
        {
            "--parent-pid", "123",
            "--web-root", firstGame.WebRoot,
            "--entry", "index.html",
            "--storage-id", "Name To Shop",
            "--pipe", "test-pipe",
        });
        var firstLegacy = HostOptions.Parse(new[]
        {
            "--parent-pid", "123",
            "--web-root", firstGame.WebRoot,
        });
        var secondLegacy = HostOptions.Parse(new[]
        {
            "--parent-pid", "123",
            "--web-root", secondGame.WebRoot,
        });

        Assert.True(explicitOptions.HasExplicitStorageId);
        Assert.Equal("Name To Shop", explicitOptions.StorageId);
        Assert.False(firstLegacy.HasExplicitStorageId);
        Assert.NotEqual(firstLegacy.StorageId, secondLegacy.StorageId);
    }

    [Fact]
    public void EntryUriKeepsStableOriginAndEscapesSegments()
    {
        var identity = HostIdentity.Create("Game", Path.Combine(Path.GetTempPath(), "three-unity-host-tests"));

        var entry = identity.EntryUri("folder/a file.html");

        Assert.Equal(identity.Origin.Scheme, entry.Scheme);
        Assert.Equal(identity.Origin.Host, entry.Host);
        Assert.Equal("/folder/a%20file.html", entry.AbsolutePath);
    }

    [Fact]
    public void EntryTraversalIsRejected()
    {
        using var game = TemporaryWebRoot.Create("traversal");

        var error = Assert.Throws<ArgumentException>(() => HostOptions.Parse(new[]
        {
            "--parent-pid", "123",
            "--web-root", game.WebRoot,
            "--entry", "../outside.html",
        }));

        Assert.Contains("inside", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TerminalFaultConvergesExactlyOnceUnderConcurrency()
    {
        var gate = new TerminalFaultGate();

        var results = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(index => Task.Run(() => gate.TryConverge($"fault-{index}"))));

        Assert.Single(results, result => result);
        Assert.True(gate.HasConverged);
        Assert.StartsWith("fault-", gate.Reason);
        Assert.False(gate.TryConverge("later-fault"));
        Assert.NotEqual("later-fault", gate.Reason);
    }

    [Fact]
    public void EmptyFaultReasonIsNormalized()
    {
        var gate = new TerminalFaultGate();

        Assert.True(gate.TryConverge("  "));

        Assert.Equal("terminal-fault", gate.Reason);
    }

    [Fact]
    public void PageReadyDiagnosticIsStableAndDistinctFromFatalDiagnostics()
    {
        Assert.Equal("THREE_UNITY_WEB_HOST_PAGE_READY", HostDiagnosticMarkers.PageReady);
        Assert.DoesNotContain("FATAL", HostDiagnosticMarkers.PageReady, StringComparison.Ordinal);
    }

    [Fact]
    public void ListenerReadyControlFrameIsExactAndInternal()
    {
        Assert.Equal("THREE_UNITY_WEB_LISTENER_READY", HostWebControlProtocol.ListenerReady);
        Assert.Equal("\"THREE_UNITY_WEB_LISTENER_READY\"", HostWebControlProtocol.ListenerReadyJson);
        Assert.True(HostWebControlProtocol.IsListenerReady(HostWebControlProtocol.ListenerReadyJson));
        Assert.False(HostWebControlProtocol.IsListenerReady(HostWebControlProtocol.ListenerReady));
        Assert.False(HostWebControlProtocol.IsListenerReady(
            "{\"type\":\"THREE_UNITY_WEB_LISTENER_READY\"}"));
        Assert.False(HostWebControlProtocol.IsListenerReady(
            HostWebControlProtocol.ListenerReadyJson + " "));
    }

    [Fact]
    public void NavigationWithoutListenerAckReportsPageReadyButKeepsMessagesBuffered()
    {
        var readiness = new WebPageReadinessCoordinator();
        var queue = new BoundedHostMessageQueue<string>(4);
        Assert.True(queue.TryEnqueue("startup"));

        Assert.True(readiness.BeginDocument(10).Accepted);
        var navigation = readiness.MarkNavigationReady(10);

        Assert.True(navigation.Accepted);
        Assert.True(navigation.ReportPageReady);
        Assert.False(navigation.OpenDispatch);
        Assert.True(readiness.HasReportedPageReady);
        Assert.False(readiness.IsDispatchOpen);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void ListenerAckAfterNavigationReleasesBufferedMessagesInOrderExactlyOnce()
    {
        var readiness = new WebPageReadinessCoordinator();
        var queue = new BoundedHostMessageQueue<string>(4);
        Assert.True(queue.TryEnqueue("first"));
        Assert.True(queue.TryEnqueue("second"));
        readiness.BeginDocument(20);
        Assert.False(readiness.MarkNavigationReady(20).OpenDispatch);

        var listener = readiness.MarkListenerReady();

        Assert.False(listener.ReportPageReady);
        Assert.True(listener.OpenDispatch);
        Assert.True(readiness.IsDispatchOpen);
        var drained = new List<string>();
        while (queue.TryDequeue(out var message))
            drained.Add(message);
        Assert.Equal(new[] { "first", "second" }, drained);
        Assert.False(readiness.MarkListenerReady().OpenDispatch);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void ListenerAckBeforeNavigationStillWaitsForNavigation()
    {
        var readiness = new WebPageReadinessCoordinator();
        readiness.BeginDocument(30);

        var listener = readiness.MarkListenerReady();
        Assert.False(listener.ReportPageReady);
        Assert.False(listener.OpenDispatch);
        Assert.False(readiness.HasReportedPageReady);
        Assert.False(readiness.IsDispatchOpen);

        var navigation = readiness.MarkNavigationReady(30);
        Assert.True(navigation.ReportPageReady);
        Assert.True(navigation.OpenDispatch);
        Assert.True(readiness.HasReportedPageReady);
        Assert.True(readiness.IsDispatchOpen);

        var duplicateNavigation = readiness.MarkNavigationReady(30);
        Assert.False(duplicateNavigation.ReportPageReady);
        Assert.False(duplicateNavigation.OpenDispatch);
    }

    [Fact]
    public async Task ReadinessSignalsReportAndOpenExactlyOnceUnderConcurrency()
    {
        var readiness = new WebPageReadinessCoordinator();
        readiness.BeginDocument(40);
        var signals = Enumerable.Range(0, 128)
            .Select(index => Task.Run(() => index % 2 == 0
                ? readiness.MarkNavigationReady(40)
                : readiness.MarkListenerReady()));

        var results = await Task.WhenAll(signals);

        Assert.True(readiness.HasReportedPageReady);
        Assert.True(readiness.IsDispatchOpen);
        Assert.Single(results, transition => transition.ReportPageReady);
        Assert.Single(results, transition => transition.OpenDispatch);
    }

    [Fact]
    public void RedirectedDocumentCannotReuseListenerAckOrStaleCompletion()
    {
        var readiness = new WebPageReadinessCoordinator();
        Assert.True(readiness.BeginDocument(100).Accepted);
        Assert.True(readiness.MarkListenerReady().Accepted);

        var replacement = readiness.BeginDocument(101);

        Assert.True(replacement.Accepted);
        Assert.False(replacement.RetireHost);
        Assert.False(readiness.IsDispatchOpen);
        Assert.Equal((ulong)101, readiness.CurrentDocumentNavigationId);

        var staleCompletion = readiness.MarkNavigationReady(100);
        Assert.False(staleCompletion.Accepted);
        Assert.False(staleCompletion.ReportPageReady);
        Assert.False(staleCompletion.OpenDispatch);

        var currentCompletion = readiness.MarkNavigationReady(101);
        Assert.True(currentCompletion.ReportPageReady);
        Assert.False(currentCompletion.OpenDispatch);
        Assert.True(readiness.MarkListenerReady().OpenDispatch);
    }

    [Fact]
    public void NewDocumentAfterPageReadyRetiresHostButSameDocumentSignalsDoNot()
    {
        var readiness = new WebPageReadinessCoordinator();
        readiness.BeginDocument(200);
        readiness.MarkListenerReady();
        Assert.True(readiness.MarkNavigationReady(200).ReportPageReady);
        Assert.True(readiness.IsDispatchOpen);

        // Hash/history navigation has no ContentLoading. A completion carrying a
        // different navigation id is therefore stale and cannot disturb the
        // current document latch.
        Assert.False(readiness.MarkNavigationReady(201).Accepted);
        Assert.True(readiness.IsDispatchOpen);
        Assert.False(readiness.BeginDocument(200).RetireHost);

        var hardNavigation = readiness.BeginDocument(202);
        Assert.False(hardNavigation.Accepted);
        Assert.True(hardNavigation.RetireHost);
    }

    [Fact]
    public void ListenerAckBeforeAnyDocumentCannotPrimeFutureNavigation()
    {
        var readiness = new WebPageReadinessCoordinator();

        Assert.False(readiness.MarkListenerReady().Accepted);
        readiness.BeginDocument(300);
        var navigation = readiness.MarkNavigationReady(300);

        Assert.True(navigation.ReportPageReady);
        Assert.False(navigation.OpenDispatch);
        Assert.False(readiness.IsDispatchOpen);
    }

    [Fact]
    public void BoundedWebQueueHasAtomicCapacityAndPreservesAcceptedMessages()
    {
        const int capacity = 32;
        var queue = new BoundedHostMessageQueue<int>(capacity);
        var accepted = 0;

        Parallel.For(0, 10_000, value =>
        {
            if (queue.TryEnqueue(value))
                Interlocked.Increment(ref accepted);
        });

        Assert.Equal(capacity, accepted);
        Assert.Equal(capacity, queue.Count);
        var drained = new HashSet<int>();
        while (queue.TryDequeue(out var value))
            Assert.True(drained.Add(value));
        Assert.Equal(capacity, drained.Count);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void OneShotDelayIsClaimedByOnlyTheFirstHostGeneration()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "three-unity-host-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "delay-once.marker");
        try
        {
            var first = HostTestHooks.ClaimDelayAfterConnect("125", marker);
            var replacement = HostTestHooks.ClaimDelayAfterConnect("125", marker);

            Assert.Equal(TimeSpan.FromMilliseconds(125), first);
            Assert.Equal(TimeSpan.Zero, replacement);
            Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IncompleteTestDelayConfigurationHasNoRuntimeEffect()
    {
        Assert.Equal(TimeSpan.Zero, HostTestHooks.ClaimDelayAfterConnect(null, "marker"));
        Assert.Equal(TimeSpan.Zero, HostTestHooks.ClaimDelayAfterConnect("100", null));
        Assert.Equal(TimeSpan.Zero, HostTestHooks.ClaimDelayAfterConnect("", ""));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("60001")]
    [InlineData("not-a-number")]
    public void InvalidConfiguredTestDelayIsRejectedBeforeMarkerCreation(string milliseconds)
    {
        var marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HostTestHooks.ClaimDelayAfterConnect(milliseconds, marker));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task JobAssignmentGateConsumesOnlyTheExactInternalMarker()
    {
        var gate = new HostJobAssignmentGate();

        Assert.Equal("THREE_UNITY_HOST_JOB_ASSIGNED", HostDiagnosticMarkers.JobAssigned);
        Assert.False(gate.TryAccept(HostDiagnosticMarkers.JobAssigned + " "));
        Assert.False(gate.TryAccept(HostDiagnosticMarkers.JobAssigned.ToLowerInvariant()));
        Assert.False(gate.TryAccept("{\"type\":\"input\"}"));
        Assert.False(gate.IsAssigned);

        Assert.True(gate.TryAccept(HostDiagnosticMarkers.JobAssigned));
        await gate.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(gate.IsAssigned);

        // A duplicate remains a consumed host-control message, but the gate is
        // still the same completed one-shot assignment.
        Assert.True(gate.TryAccept(HostDiagnosticMarkers.JobAssigned));
    }

    [Fact]
    public async Task JobAssignmentGateTimesOutWithoutLaunchingAHost()
    {
        var gate = new HostJobAssignmentGate();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            gate.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.False(gate.IsAssigned);
    }

    private sealed class TemporaryWebRoot : IDisposable
    {
        private readonly string root;
        public string WebRoot { get; }

        private TemporaryWebRoot(string root, string webRoot)
        {
            this.root = root;
            WebRoot = webRoot;
        }

        public static TemporaryWebRoot Create(string name)
        {
            var root = Path.Combine(Path.GetTempPath(), "three-unity-host-tests", Guid.NewGuid().ToString("N"), name);
            var webRoot = Path.Combine(root, "StreamingAssets", "ThreeUnityWeb");
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html>");
            return new TemporaryWebRoot(root, webRoot);
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
