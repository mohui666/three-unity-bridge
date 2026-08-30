using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ThreeUnity.WebHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var options = HostOptions.Parse(args);
        Application.Run(new BridgeForm(options));
    }
}

public sealed record HostOptions(
    int ParentPid,
    string WebRoot,
    string Entry,
    string PipeName,
    string StorageId,
    bool HasExplicitStorageId)
{
    public static HostOptions Parse(string[] args)
    {
        string Value(string name, string fallback = "")
        {
            var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        if (!int.TryParse(Value("--parent-pid"), out var parentPid) || parentPid <= 0)
            throw new ArgumentException("--parent-pid is required.");
        var webRoot = Path.GetFullPath(Value("--web-root"));
        if (!Directory.Exists(webRoot)) throw new DirectoryNotFoundException(webRoot);
        var entry = HostIdentity.NormalizeEntry(webRoot, Value("--entry", "index.html"));
        var explicitStorageId = Value("--storage-id").Trim();
        var hasExplicitStorageId = explicitStorageId.Length > 0;
        var storageId = hasExplicitStorageId
            ? explicitStorageId
            : HostIdentity.CreateLegacyStorageId(webRoot, entry);
        return new HostOptions(parentPid, webRoot, entry, Value("--pipe"), storageId, hasExplicitStorageId);
    }
}

public sealed record HostStorageIdentity(
    string StorageKey,
    string VirtualHostName,
    string UserDataFolder,
    Uri Origin)
{
    public Uri EntryUri(string entry)
    {
        var escapedEntry = string.Join('/', entry.Split('/').Select(Uri.EscapeDataString));
        return new Uri(Origin, escapedEntry);
    }
}

public static class HostIdentity
{
    private const int MaxStorageIdCharacters = 1024;

    public static HostStorageIdentity Create(string storageId, string userDataRoot)
    {
        var canonical = (storageId ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if (canonical.Length == 0)
            throw new ArgumentException("A non-empty storage id is required.", nameof(storageId));
        if (canonical.Length > MaxStorageIdCharacters)
            throw new ArgumentException($"Storage id exceeds {MaxStorageIdCharacters} characters.", nameof(storageId));

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDataRoot));
        if (canonicalRoot.Length == 0)
            throw new ArgumentException("A non-empty user-data root is required.", nameof(userDataRoot));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var slug = SanitizeSlug(canonical, 16);
        var storageKey = $"{slug}-{hash}";
        var virtualHostName = $"{slug}-{hash[..40]}.threeunity.invalid";
        var userDataFolder = Path.GetFullPath(Path.Combine(canonicalRoot, storageKey));
        var relativeFolder = Path.GetRelativePath(canonicalRoot, userDataFolder);
        if (Path.IsPathRooted(relativeFolder)
            || relativeFolder.Equals("..", StringComparison.Ordinal)
            || relativeFolder.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The derived WebView user-data folder escaped its configured root.");

        return new HostStorageIdentity(
            storageKey,
            virtualHostName,
            userDataFolder,
            new Uri($"https://{virtualHostName}/", UriKind.Absolute));
    }

    public static string CreateLegacyStorageId(string webRoot, string entry)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webRoot))
            .Replace('\\', '/')
            .ToLowerInvariant();
        return $"legacy-location:{canonicalRoot}|{entry.ToLowerInvariant()}";
    }

    public static string NormalizeEntry(string webRoot, string entry)
    {
        var normalized = (entry ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0)
            normalized = "index.html";
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webRoot));
        var candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("--entry must stay inside --web-root.", nameof(entry));
        if (!File.Exists(candidate))
            throw new FileNotFoundException("The WebView entry file was not found.", candidate);
        return relative.Replace('\\', '/');
    }

    private static string SanitizeSlug(string value, int maximumLength)
    {
        var slug = new StringBuilder(maximumLength);
        var separatorPending = false;
        foreach (var character in value)
        {
            var asciiLetter = character is >= 'a' and <= 'z';
            var digit = character is >= '0' and <= '9';
            if (asciiLetter || digit)
            {
                if (separatorPending && slug.Length > 0 && slug.Length < maximumLength)
                    slug.Append('-');
                separatorPending = false;
                if (slug.Length < maximumLength)
                    slug.Append(character);
            }
            else
            {
                separatorPending = slug.Length > 0;
            }
            if (slug.Length >= maximumLength)
                break;
        }
        return slug.Length == 0 ? "game" : slug.ToString().TrimEnd('-');
    }
}

public sealed class TerminalFaultGate
{
    private readonly object sync = new();
    private bool converged;
    private string? reason;

    public bool HasConverged
    {
        get { lock (sync) return converged; }
    }

    public string? Reason
    {
        get { lock (sync) return reason; }
    }

    public bool TryConverge(string terminalReason)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(terminalReason) ? "terminal-fault" : terminalReason.Trim();
        lock (sync)
        {
            if (converged)
                return false;
            reason = normalizedReason;
            converged = true;
            return true;
        }
    }
}

public static class HostDiagnosticMarkers
{
    // Unity must send this exact internal control line before the host starts
    // any window or WebView2 initialization work. It is consumed by the host
    // and is never forwarded to the web page.
    public const string JobAssigned = "THREE_UNITY_HOST_JOB_ASSIGNED";

    // This marker is written only after WebView2 reports a successful first
    // navigation. Unity uses it to distinguish a merely connected pipe from a
    // page that is actually ready, so repeated navigation failures retain the
    // exponential restart backoff.
    public const string PageReady = "THREE_UNITY_WEB_HOST_PAGE_READY";
}

public sealed class HostJobAssignmentGate
{
    private readonly TaskCompletionSource assigned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsAssigned => assigned.Task.IsCompletedSuccessfully;

    // The return value means "this is an internal message and was consumed".
    // Repeated markers remain internal even though only the first completes the
    // one-shot gate.
    public bool TryAccept(string message)
    {
        if (!string.Equals(message, HostDiagnosticMarkers.JobAssigned, StringComparison.Ordinal))
            return false;

        assigned.TrySetResult();
        return true;
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        assigned.Task.WaitAsync(timeout, cancellationToken);
}

internal sealed class BridgeForm : Form
{
    private const int GwlStyle = -16;
    private const int WsChild = 0x40000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const uint SwpShowWindow = 0x0040;
    private const int MaxPendingMessages = 1024;
    private const int MaxWebViewMessagesPerDispatch = 64;

    private readonly HostOptions options;
    private readonly WebView2 webView = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer parentTimer = new() { Interval = 100 };
    private readonly CancellationTokenSource bridgeCancellation = new();
    private readonly Channel<string> messagesToUnity = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxPendingMessages)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly ConcurrentQueue<string> messagesToWebView = new();
    private readonly TerminalFaultGate terminalFault = new();
    private readonly HostJobAssignmentGate hostJobAssignment = new();
    private Process? parent;
    private IntPtr parentWindow;
    private NamedPipeClientStream? pipe;
    private StreamReader? pipeReader;
    private StreamWriter? pipeWriter;
    private Thread? parentWatchThread;
    private Task? pipeReaderTask;
    private Task? pipeWriterTask;
    private int pendingWebViewMessages;
    private int webViewDispatchScheduled;
    private int bridgeDisconnected;
    private int bridgeDisposed;
    private int pageReadyReported;

    public BridgeForm(HostOptions options)
    {
        this.options = options;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Controls.Add(webView);
        Shown += OnShown;
        FormClosed += (_, _) => DisposeBridge();
        parentTimer.Tick += (_, _) => SyncToParent();
    }

    private async void OnShown(object? sender, EventArgs eventArgs)
    {
        try
        {
            parent = Process.GetProcessById(options.ParentPid);
            parentWatchThread = new Thread(WatchParentProcess)
            {
                IsBackground = true,
                Name = "ThreeUnityParentWatch",
            };
            parentWatchThread.Start();
            await ConnectPipe();
            try
            {
                await hostJobAssignment.WaitAsync(TimeSpan.FromSeconds(5), bridgeCancellation.Token);
            }
            catch (TimeoutException exception)
            {
                ConvergeTerminalFault("host-job-assignment-timeout", exception);
                return;
            }

            parentWindow = await WaitForWindow(parent);
            AttachToParent();
            parentTimer.Start();

            var userDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThreeUnity",
                "WebView2");
            var identity = HostIdentity.Create(options.StorageId, userDataRoot);
            Directory.CreateDirectory(identity.UserDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, identity.UserDataFolder);
            await webView.EnsureCoreWebView2Async(environment);
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.WebMessageReceived += OnWebMessage;
            webView.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;
            webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                identity.VirtualHostName,
                options.WebRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            webView.CoreWebView2.Navigate(identity.EntryUri(options.Entry).AbsoluteUri);
        }
        catch (Exception exception)
        {
            ConvergeTerminalFault("host-initialization-failed", exception);
        }
    }

    private static async Task<IntPtr> WaitForWindow(Process process)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            process.Refresh();
            if (process.HasExited) throw new InvalidOperationException("Unity parent process exited before WebView attachment.");
            if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            await Task.Delay(50);
        }
        throw new TimeoutException("Unity main window was not available.");
    }

    private void WatchParentProcess()
    {
        while (Volatile.Read(ref bridgeDisposed) == 0)
        {
            try
            {
                using var monitoredParent = Process.GetProcessById(options.ParentPid);
                if (monitoredParent.HasExited)
                    break;
            }
            catch (ArgumentException) { break; }
            catch (InvalidOperationException) { break; }
            Thread.Sleep(250);
        }
        if (Volatile.Read(ref bridgeDisposed) == 0)
            Environment.Exit(0);
    }

    private void AttachToParent()
    {
        var style = GetWindowLong(Handle, GwlStyle);
        SetWindowLong(Handle, GwlStyle, (style | WsChild) & ~WsPopup);
        if (SetParent(Handle, parentWindow) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        SyncToParent();
    }

    private void SyncToParent()
    {
        if (parent == null || parent.HasExited)
        {
            Close();
            return;
        }
        if (GetClientRect(parentWindow, out var rect))
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top, SwpShowWindow);
    }

    private async Task ConnectPipe()
    {
        if (string.IsNullOrWhiteSpace(options.PipeName)) return;
        pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        pipeReader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        pipeWriter = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        pipeReaderTask = ReadPipeLoop(bridgeCancellation.Token);
        pipeWriterTask = WritePipeLoop(bridgeCancellation.Token);
    }

    private async Task ReadPipeLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (pipeReader != null && !cancellationToken.IsCancellationRequested)
            {
                var message = await pipeReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (message == null)
                {
                    ConvergeTerminalFault("pipe-eof");
                    return;
                }
                if (hostJobAssignment.TryAccept(message))
                    continue;

                messagesToWebView.Enqueue(message);
                if (Interlocked.Increment(ref pendingWebViewMessages) > MaxPendingMessages)
                {
                    ConvergeTerminalFault("unity-to-web-overflow");
                    return;
                }
                if (Volatile.Read(ref pageReadyReported) != 0)
                    ScheduleWebViewDispatch();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException exception) { ConvergeTerminalFault("pipe-read-failed", exception); }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException exception) { ConvergeTerminalFault("pipe-read-disposed", exception); }
    }

    private async Task WritePipeLoop(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in messagesToUnity.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (pipeWriter == null) break;
                await pipeWriter.WriteLineAsync(message).ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested)
                ConvergeTerminalFault("pipe-write-ended");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ChannelClosedException) when (cancellationToken.IsCancellationRequested) { }
        catch (ChannelClosedException exception) { ConvergeTerminalFault("pipe-write-channel-closed", exception); }
        catch (IOException exception) { ConvergeTerminalFault("pipe-write-failed", exception); }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException exception) { ConvergeTerminalFault("pipe-write-disposed", exception); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (Volatile.Read(ref bridgeDisconnected) != 0) return;
        if (!messagesToUnity.Writer.TryWrite(args.WebMessageAsJson))
            ConvergeTerminalFault("web-to-unity-overflow");
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args) =>
        ConvergeTerminalFault($"webview-process-failed:{args.ProcessFailedKind}");

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // A hard reload creates a fresh JavaScript session while Unity still owns
        // the old physical page generation. Retire the whole Host so the launcher
        // increments that generation instead of allowing cross-session delivery.
        if (Volatile.Read(ref pageReadyReported) != 0)
            ConvergeTerminalFault("navigation-after-page-ready");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            ConvergeTerminalFault($"navigation-failed:{args.WebErrorStatus}");
            return;
        }

        if (Interlocked.Exchange(ref pageReadyReported, 1) == 0)
        {
            Console.Error.WriteLine(HostDiagnosticMarkers.PageReady);
            ScheduleWebViewDispatch();
        }
    }

    private void ScheduleWebViewDispatch()
    {
        if (IsDisposed || !IsHandleCreated || Volatile.Read(ref bridgeDisconnected) != 0)
            return;
        if (Interlocked.CompareExchange(ref webViewDispatchScheduled, 1, 0) != 0)
            return;
        try { BeginInvoke((Action)DrainWebViewMessages); }
        catch (InvalidOperationException) { Volatile.Write(ref webViewDispatchScheduled, 0); }
    }

    private void DrainWebViewMessages()
    {
        if (IsDisposed
            || Volatile.Read(ref bridgeDisconnected) != 0
            || Volatile.Read(ref pageReadyReported) == 0
            || webView.CoreWebView2 == null)
        {
            Volatile.Write(ref webViewDispatchScheduled, 0);
            return;
        }

        var dispatched = 0;
        while (dispatched++ < MaxWebViewMessagesPerDispatch && messagesToWebView.TryDequeue(out var message))
        {
            Interlocked.Decrement(ref pendingWebViewMessages);
            try { webView.CoreWebView2.PostWebMessageAsString(message); }
            catch (InvalidOperationException exception)
            {
                ConvergeTerminalFault("webview-message-dispatch-failed", exception);
                return;
            }
        }

        if (!messagesToWebView.IsEmpty)
        {
            try { BeginInvoke((Action)DrainWebViewMessages); }
            catch (InvalidOperationException) { Volatile.Write(ref webViewDispatchScheduled, 0); }
            return;
        }

        Volatile.Write(ref webViewDispatchScheduled, 0);
        if (!messagesToWebView.IsEmpty)
            ScheduleWebViewDispatch();
    }

    private void DisconnectBridge(string reason)
    {
        if (Interlocked.Exchange(ref bridgeDisconnected, 1) != 0)
            return;
        Debug.WriteLine("Three Unity Web Bridge disconnected: " + reason);
        messagesToUnity.Writer.TryComplete(new IOException(reason));
        bridgeCancellation.Cancel();
        try { pipe?.Dispose(); }
        catch (ObjectDisposedException) { }
        while (messagesToWebView.TryDequeue(out _))
            Interlocked.Decrement(ref pendingWebViewMessages);
    }

    private void ConvergeTerminalFault(string reason, Exception? exception = null)
    {
        if (!terminalFault.TryConverge(reason))
            return;

        var report = $"THREE_UNITY_WEB_HOST_FATAL reason={terminalFault.Reason}";
        if (exception != null)
            report += $" exception={exception.GetType().Name} message={exception.Message}";
        Environment.ExitCode = 1;
        try { Trace.TraceError(report); }
        catch (Exception) { }
        try
        {
            Console.Error.WriteLine(report);
            Console.Error.Flush();
        }
        catch (Exception) { }
        try { DisconnectBridge(terminalFault.Reason ?? "terminal-fault"); }
        catch (Exception) { }
        finally { CloseOnUiThread(); }
    }

    private void CloseOnUiThread()
    {
        void CloseIfAlive()
        {
            if (!IsDisposed && !Disposing)
                Close();
        }

        try
        {
            if (IsDisposed || Disposing)
                return;
            if (InvokeRequired)
                BeginInvoke((Action)CloseIfAlive);
            else
                CloseIfAlive();
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void DisposeBridge()
    {
        if (Interlocked.Exchange(ref bridgeDisposed, 1) != 0)
            return;
        parentTimer.Stop();
        if (webView.CoreWebView2 != null)
        {
            webView.CoreWebView2.WebMessageReceived -= OnWebMessage;
            webView.CoreWebView2.ProcessFailed -= OnWebViewProcessFailed;
            webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
        }
        DisconnectBridge("host-disposed");
        pipeReader?.Dispose();
        pipeWriter?.Dispose();
        pipe?.Dispose();
        parentWatchThread = null;
        pipeReaderTask = null;
        pipeWriterTask = null;
        webView.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
