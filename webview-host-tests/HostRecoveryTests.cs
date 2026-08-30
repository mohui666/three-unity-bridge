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
