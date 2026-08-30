using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ThreeUnity.Bridge
{
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class ThreeUnityWebBridgeLauncher : MonoBehaviour
    {
        private const int HostGracefulExitMilliseconds = 750;
        private const int HostKillWaitMilliseconds = 1000;
        private const int HostTerminationRetryMilliseconds = 250;
        private const int HostJobAssignmentGateMilliseconds = 4500;
        private const string HostJobAssignedMarker = "THREE_UNITY_HOST_JOB_ASSIGNED";
        private const string HostPageReadyMarker = "THREE_UNITY_WEB_HOST_PAGE_READY";
        private const string HostFatalMarkerPrefix = "THREE_UNITY_WEB_HOST_FATAL";

        [SerializeField] private string webRootDirectory = "ThreeUnityWeb";
        [SerializeField] private string entryPage = "index.html";
        [SerializeField] private string hostDirectory = "ThreeUnityWebHost";
        [SerializeField] private string storageIdentifier;

        private readonly ThreeUnityGenerationQueue<string> messagesFromWeb = new ThreeUnityGenerationQueue<string>();
        private readonly ThreeUnityGenerationQueue<string> hostDiagnostics = new ThreeUnityGenerationQueue<string>();
        private readonly ConcurrentQueue<LifecycleSignal> lifecycleSignals = new ConcurrentQueue<LifecycleSignal>();
        private readonly List<ConnectionResources> retiredConnections = new List<ConnectionResources>();
        private readonly ThreeUnityWebBridgeLifecycle lifecycle = new ThreeUnityWebBridgeLifecycle();
        private readonly ThreeUnityOutboundBuffer emptyOutbound = new ThreeUnityOutboundBuffer();
        private readonly ThreeUnityOutboundMetricsAccumulator outboundMetrics = new ThreeUnityOutboundMetricsAccumulator();
        private readonly object leaseIssuerIdentity = new object();

        private ConnectionResources activeConnection;
        private IThreeUnityWindowsHostJob hostJob;
        private long hostJobPageGeneration;
        private long hostJobConnectionGeneration;
        private ThreeUnityWebBridgeLease legacyGenerationlessLease;
        private Process hostProcess;
        private ConnectionResources trackedHostConnection;
        private long trackedHostPageGeneration;
        private long trackedHostConnectionGeneration;
        private long hostCleanupDeadlineMilliseconds;
        private bool hostTerminationStarted;
        private bool rootHostKillIssued;
        private bool jobTerminationSucceeded;
        private long nextHostTerminationAttemptMilliseconds;
        private long jobTerminationFailures;
        private bool hostCleanupTimeoutLogged;
        private bool trackedHostAssignedToJob;
        private int stopping;
        private int stopStarted;
        private long activePageGeneration;
        private long activeConnectionGeneration;
        private long webMessagesReceived;
        private long webCharactersReceived;
        private long unityMessagesWritten;
        private long unityCharactersWritten;
        private int maxInboundPending;
        private long transportGenerationRejected;
        private long hostDiagnosticsReceived;
        private long hostDiagnosticsRejected;
        private long legacyGenerationlessRejected;
        private int jobAssignedProcesses;

        public long PageGeneration => lifecycle.PageGeneration;
        public long ConnectionGeneration => lifecycle.ConnectionGeneration;
        public bool IsConnected => lifecycle.State == ThreeUnityHostLifecycleState.Connected;

        public void Configure(string webRoot, string entry)
        {
            webRootDirectory = string.IsNullOrEmpty(webRoot) ? "ThreeUnityWeb" : webRoot;
            entryPage = string.IsNullOrEmpty(entry) ? "index.html" : entry;
        }

        public void ConfigureStorageIdentifier(string identifier)
        {
            storageIdentifier = identifier;
        }

        private void Start()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                ValidateWindowsHostFiles();
                EnsureHostJob();
                lifecycle.Start(NowMilliseconds());
                PumpLifecycle();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                StopBridge();
            }
#else
            Debug.LogError("Three Unity Web Bridge currently supports Windows Player builds only.");
            StopBridge();
#endif
        }

        private void Update()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            PumpLifecycle();
#endif
        }

        private void PumpLifecycle()
        {
            if (Volatile.Read(ref stopping) != 0)
                return;

            var nowMilliseconds = NowMilliseconds();
            DrainHostDiagnostics(nowMilliseconds);
            DrainLifecycleSignals(nowMilliseconds);
            ObserveTrackedHost(nowMilliseconds);
            ObserveConnectTimeout(nowMilliseconds);
            ObservePageReadyTimeout(nowMilliseconds);
            ObservePageStability(nowMilliseconds);
            AdvanceTrackedHostCleanup(nowMilliseconds);
            CleanupRetiredConnections(false);

            if (lifecycle.State == ThreeUnityHostLifecycleState.WaitingToLaunch
                && hostProcess == null)
                TryLaunchWindowsHost(nowMilliseconds);
        }

        private void ValidateWindowsHostFiles()
        {
            var hostPath = GetHostPath();
            var webRoot = GetWebRootPath();
            if (!File.Exists(hostPath))
                throw new FileNotFoundException("Three Unity WebView host is missing.", hostPath);
            if (!Directory.Exists(webRoot))
                throw new DirectoryNotFoundException(webRoot);
        }

        private void TryLaunchWindowsHost(long nowMilliseconds)
        {
            if (!lifecycle.TryBeginLaunch(
                    nowMilliseconds,
                    hostProcess == null,
                    out var pageGeneration,
                    out var connectionGeneration))
                return;

            ConnectionResources connection = null;
            Process launchedProcess = null;
            try
            {
                EnsureHostJob();
                hostJobPageGeneration = pageGeneration;
                hostJobConnectionGeneration = connectionGeneration;
                trackedHostAssignedToJob = false;
                var pipeName = $"three-unity-{GetCurrentProcessId()}-{pageGeneration}-{Guid.NewGuid():N}";
                connection = new ConnectionResources(pageGeneration, connectionGeneration, pipeName);
                connection.Lease = new ThreeUnityWebBridgeLease(
                    leaseIssuerIdentity,
                    connection.LeaseIdentity,
                    pageGeneration,
                    connectionGeneration);
                activeConnection = connection;
                Interlocked.Exchange(ref activePageGeneration, pageGeneration);
                Interlocked.Exchange(ref activeConnectionGeneration, connectionGeneration);
                connection.ReaderThread = new Thread(() => AcceptPipe(connection))
                {
                    IsBackground = true,
                    Name = "ThreeUnityWebBridgePipe-" + pageGeneration,
                };
                connection.ReaderThread.Start();

                var arguments = string.Join(" ", new[]
                {
                    "--parent-pid", GetCurrentProcessId().ToString(),
                    "--web-root", Quote(GetWebRootPath()),
                    "--entry", Quote(entryPage),
                    "--pipe", Quote(pipeName),
                    "--storage-id", Quote(ResolveStorageIdentifier()),
                });
                launchedProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = GetHostPath(),
                        Arguments = arguments,
                        WorkingDirectory = Path.GetDirectoryName(GetHostPath()),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardError = true,
                    },
                };
                connection.ErrorHandler = (_, eventArguments) =>
                {
                    if (eventArguments.Data == null)
                        return;
                    hostDiagnostics.Enqueue(
                        connection.PageGeneration,
                        connection.ConnectionGeneration,
                        eventArguments.Data);
                    Interlocked.Increment(ref hostDiagnosticsReceived);
                };
                launchedProcess.ErrorDataReceived += connection.ErrorHandler;
                if (!launchedProcess.Start())
                    throw new InvalidOperationException("Could not start Three Unity WebView host.");

                hostProcess = launchedProcess;
                trackedHostConnection = connection;
                trackedHostPageGeneration = pageGeneration;
                trackedHostConnectionGeneration = connectionGeneration;
                if (hostJob == null)
                    throw new InvalidOperationException("Windows Host Job was not available after initialization.");
                hostJob.Assign(launchedProcess);
                trackedHostAssignedToJob = true;
                Interlocked.Increment(ref jobAssignedProcesses);
                connection.JobAssignedSignal.Set();
                hostCleanupDeadlineMilliseconds = 0;
                ResetHostTerminationState();
                hostCleanupTimeoutLogged = false;
                launchedProcess.BeginErrorReadLine();
                var marker = lifecycle.Relaunches > 0
                    ? "THREE_UNITY_WEB_BRIDGE_RELAUNCHED"
                    : "THREE_UNITY_WEB_BRIDGE_STARTED";
                Debug.Log(marker
                    + " pid=" + launchedProcess.Id
                    + " entry=" + entryPage
                    + " pageGeneration=" + pageGeneration
                    + " connectionGeneration=" + connectionGeneration
                    + " relaunches=" + lifecycle.Relaunches);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (hostProcess == null && launchedProcess != null)
                {
                    DetachHostDiagnostics(launchedProcess, connection);
                    DisposeQuietly(launchedProcess);
                }
                if (connection != null)
                    RetireActiveConnection(pageGeneration, connectionGeneration);
                if (lifecycle.TryReportFault(
                    pageGeneration,
                    connectionGeneration,
                    ThreeUnityHostFaultReason.HostLaunchFailure))
                {
                    LogDisconnect(ThreeUnityHostFaultReason.HostLaunchFailure, pageGeneration, connectionGeneration);
                    if (hostProcess == null)
                        CompleteRetirementWithoutHost(pageGeneration, connectionGeneration, nowMilliseconds);
                    else
                        BeginTrackedHostCleanup(nowMilliseconds);
                }
            }
        }

        private void AcceptPipe(ConnectionResources connection)
        {
            try
            {
                connection.Pipe.WaitForConnection();
                if (connection.IsRetired || Volatile.Read(ref stopping) != 0)
                    return;

                if (!connection.JobAssignedSignal.Wait(HostJobAssignmentGateMilliseconds))
                {
                    ReportFaultFromWorker(connection, ThreeUnityHostFaultReason.HostLaunchFailure);
                    return;
                }
                if (connection.IsRetired || Volatile.Read(ref stopping) != 0)
                    return;

                var writer = new StreamWriter(connection.Pipe, new UTF8Encoding(false), 4096, true)
                {
                    AutoFlush = true,
                };
                connection.Writer = writer;
                // This internal control line opens the Host's one-shot gate. It
                // is written only after AssignProcessToJobObject succeeds and
                // is deliberately excluded from gameplay transport telemetry.
                connection.Writer.WriteLine(HostJobAssignedMarker);
                connection.WriterThread = new Thread(() => WritePipe(connection))
                {
                    IsBackground = true,
                    Name = "ThreeUnityWebBridgeWriter-" + connection.PageGeneration,
                };
                connection.WriterThread.Start();
                lifecycleSignals.Enqueue(LifecycleSignal.Connected(connection));

                using (var reader = new StreamReader(connection.Pipe, Encoding.UTF8, false, 4096, true))
                {
                    while (!connection.IsRetired && Volatile.Read(ref stopping) == 0)
                    {
                        var message = reader.ReadLine();
                        if (message == null)
                        {
                            ReportFaultFromWorker(connection, ThreeUnityHostFaultReason.ReaderEof);
                            break;
                        }

                        var overflowBefore = messagesFromWeb.OverflowDropped;
                        messagesFromWeb.Enqueue(
                            connection.PageGeneration,
                            connection.ConnectionGeneration,
                            message);
                        Interlocked.Increment(ref webMessagesReceived);
                        Interlocked.Add(ref webCharactersReceived, message.Length + 1L);
                        ObserveMax(ref maxInboundPending, messagesFromWeb.Pending);
                        if (messagesFromWeb.OverflowDropped != overflowBefore)
                        {
                            ReportFaultFromWorker(
                                connection,
                                ThreeUnityHostFaultReason.InboundOverflow);
                            break;
                        }
                    }
                }
            }
            catch (IOException)
            {
                ReportFaultFromWorker(connection, ThreeUnityHostFaultReason.ReaderIOException);
            }
            catch (ObjectDisposedException)
            {
                // Expected when the main thread retires this exact generation.
            }
        }

        private void WritePipe(ConnectionResources connection)
        {
            try
            {
                while (!connection.IsRetired && Volatile.Read(ref stopping) == 0)
                {
                    if (!connection.Outbound.TryDequeue(out var message))
                    {
                        connection.OutboundSignal.WaitOne(50);
                        continue;
                    }

                    if (!IsWritableGeneration(connection))
                    {
                        Interlocked.Increment(ref transportGenerationRejected);
                        continue;
                    }

                    connection.Writer.WriteLine(message);
                    Interlocked.Increment(ref unityMessagesWritten);
                    Interlocked.Add(ref unityCharactersWritten, message.Length + 1L);
                }
            }
            catch (IOException)
            {
                ReportFaultFromWorker(connection, ThreeUnityHostFaultReason.WriterIOException);
            }
            catch (ObjectDisposedException)
            {
                // Expected when the main thread retires this exact generation.
            }
        }

        private void ReportFaultFromWorker(ConnectionResources connection, ThreeUnityHostFaultReason reason)
        {
            if (connection.IsRetired || Volatile.Read(ref stopping) != 0)
                return;
            lifecycleSignals.Enqueue(LifecycleSignal.Fault(connection, reason));
        }

        private void DrainLifecycleSignals(long nowMilliseconds)
        {
            while (lifecycleSignals.TryDequeue(out var signal))
            {
                if (signal.Kind == LifecycleSignalKind.Connected)
                {
                    if (lifecycle.TryMarkConnected(
                        signal.PageGeneration,
                        signal.ConnectionGeneration,
                        nowMilliseconds))
                    {
                        Debug.Log("THREE_UNITY_WEB_BRIDGE_CONNECTED"
                            + " pageGeneration=" + signal.PageGeneration
                            + " connectionGeneration=" + signal.ConnectionGeneration
                            + " connected=" + lifecycle.SuccessfulConnections);
                    }
                    else if (signal.PageGeneration != lifecycle.PageGeneration
                        || signal.ConnectionGeneration != lifecycle.ConnectionGeneration)
                    {
                        LogGenerationRejection(
                            "connected",
                            signal.PageGeneration,
                            signal.ConnectionGeneration,
                            false);
                    }
                    continue;
                }

                if (!lifecycle.TryReportFault(
                    signal.PageGeneration,
                    signal.ConnectionGeneration,
                    signal.FaultReason))
                {
                    if (signal.PageGeneration != lifecycle.PageGeneration
                        || signal.ConnectionGeneration != lifecycle.ConnectionGeneration)
                    {
                        LogGenerationRejection(
                            "fault",
                            signal.PageGeneration,
                            signal.ConnectionGeneration,
                            false);
                    }
                    continue;
                }

                LogDisconnect(signal.FaultReason, signal.PageGeneration, signal.ConnectionGeneration);
                if (signal.FaultReason == ThreeUnityHostFaultReason.InboundOverflow)
                {
                    Debug.LogError("THREE_UNITY_WEB_BRIDGE_INBOUND_OVERFLOW"
                        + " pageGeneration=" + signal.PageGeneration
                        + " connectionGeneration=" + signal.ConnectionGeneration
                        + " dropped=" + messagesFromWeb.OverflowDropped);
                }
                RetireActiveConnection(signal.PageGeneration, signal.ConnectionGeneration);
                BeginTrackedHostCleanup(nowMilliseconds);
                if (hostProcess == null)
                    CompleteRetirementWithoutHost(signal.PageGeneration, signal.ConnectionGeneration, nowMilliseconds);
            }
        }

        private void DrainHostDiagnostics(long nowMilliseconds)
        {
            var currentPage = lifecycle.PageGeneration;
            var currentConnection = lifecycle.ConnectionGeneration;
            while (true)
            {
                var found = hostDiagnostics.TryDequeueCurrent(
                    currentPage,
                    currentConnection,
                    out var diagnostic,
                    out var rejected);
                if (rejected > 0)
                {
                    Interlocked.Add(ref hostDiagnosticsRejected, rejected);
                    RecordGenerationRejections(
                        rejected,
                        "host-diagnostic",
                        currentPage,
                        currentConnection);
                }
                if (!found)
                    return;
                var canRefineRetiringFault = lifecycle.State == ThreeUnityHostLifecycleState.Retiring
                    && currentPage == lifecycle.PageGeneration
                    && currentConnection == lifecycle.ConnectionGeneration;
                if (!lifecycle.CanAcceptGeneration(currentPage, currentConnection)
                    && !canRefineRetiringFault)
                {
                    Interlocked.Increment(ref hostDiagnosticsRejected);
                    RecordGenerationRejections(
                        1,
                        "host-diagnostic-retired",
                        currentPage,
                        currentConnection);
                    continue;
                }

                if (string.Equals(
                    diagnostic,
                    HostPageReadyMarker,
                    StringComparison.Ordinal))
                {
                    if (lifecycle.TryMarkPageReady(
                        currentPage,
                        currentConnection,
                        nowMilliseconds))
                    {
                        Debug.Log("THREE_UNITY_WEB_BRIDGE_PAGE_READY"
                            + " pageGeneration=" + currentPage
                            + " connectionGeneration=" + currentConnection);
                    }
                    continue;
                }

                var message = "THREE_UNITY_WEB_HOST_DIAGNOSTIC"
                    + " pageGeneration=" + currentPage
                    + " connectionGeneration=" + currentConnection
                    + " message=" + diagnostic;
                if (diagnostic.StartsWith(HostFatalMarkerPrefix, StringComparison.Ordinal))
                {
                    Debug.LogError(message);
                    var previousReason = lifecycle.LastDisconnectReason;
                    var reported = false;
                    if (lifecycle.State == ThreeUnityHostLifecycleState.Starting
                        || lifecycle.State == ThreeUnityHostLifecycleState.Connected)
                    {
                        reported = lifecycle.TryReportFault(
                            currentPage,
                            currentConnection,
                            ThreeUnityHostFaultReason.HostFatal);
                    }
                    var refined = !reported && lifecycle.TryRefineRetiringFault(
                        currentPage,
                        currentConnection,
                        ThreeUnityHostFaultReason.HostFatal);
                    if (reported)
                    {
                        LogDisconnect(
                            ThreeUnityHostFaultReason.HostFatal,
                            currentPage,
                            currentConnection);
                        RetireActiveConnection(currentPage, currentConnection);
                        BeginTrackedHostCleanup(nowMilliseconds);
                        if (hostProcess == null)
                        {
                            CompleteRetirementWithoutHost(
                                currentPage,
                                currentConnection,
                                nowMilliseconds);
                        }
                    }
                    else if (refined)
                    {
                        Debug.LogWarning("THREE_UNITY_WEB_BRIDGE_FAULT_REFINED"
                            + " from=" + FaultReasonName(previousReason)
                            + " to=host-fatal"
                            + " pageGeneration=" + currentPage
                            + " connectionGeneration=" + currentConnection);
                    }
                    continue;
                }
                Debug.LogWarning(message);
            }
        }

        private void ObserveTrackedHost(long nowMilliseconds)
        {
            var process = hostProcess;
            if (process == null || !HasExited(process))
                return;

            var pageGeneration = trackedHostPageGeneration;
            var connectionGeneration = trackedHostConnectionGeneration;
            try { process.WaitForExit(); }
            catch (InvalidOperationException) { }
            // WaitForExit drains redirected stderr callbacks. Process any fatal
            // diagnostic before falling back to the less specific exit reason.
            DrainHostDiagnostics(nowMilliseconds);
            if (lifecycle.State == ThreeUnityHostLifecycleState.Starting
                || lifecycle.State == ThreeUnityHostLifecycleState.Connected)
            {
                var reason = lifecycle.State == ThreeUnityHostLifecycleState.Starting
                    ? ThreeUnityHostFaultReason.HostExitedBeforeConnect
                    : ThreeUnityHostFaultReason.HostExited;
                if (lifecycle.TryReportFault(pageGeneration, connectionGeneration, reason))
                {
                    LogDisconnect(reason, pageGeneration, connectionGeneration);
                    RetireActiveConnection(pageGeneration, connectionGeneration);
                }
            }

            DetachHostDiagnostics(process, trackedHostConnection);
            trackedHostConnection = null;
            DisposeQuietly(process);
            if (ReferenceEquals(hostProcess, process))
                hostProcess = null;
            BeginTrackedJobTermination(nowMilliseconds);
            CompleteRetirementWithoutHost(pageGeneration, connectionGeneration, nowMilliseconds);
        }

        private void ObserveConnectTimeout(long nowMilliseconds)
        {
            if (!lifecycle.TryReportConnectTimeout(
                nowMilliseconds,
                out var pageGeneration,
                out var connectionGeneration))
                return;

            LogDisconnect(
                ThreeUnityHostFaultReason.ConnectTimeout,
                pageGeneration,
                connectionGeneration);
            RetireActiveConnection(pageGeneration, connectionGeneration);
            BeginTrackedHostCleanup(nowMilliseconds);
            if (hostProcess == null)
                CompleteRetirementWithoutHost(pageGeneration, connectionGeneration, nowMilliseconds);
        }

        private void ObservePageReadyTimeout(long nowMilliseconds)
        {
            if (!lifecycle.TryReportPageReadyTimeout(
                nowMilliseconds,
                out var pageGeneration,
                out var connectionGeneration))
                return;

            LogDisconnect(
                ThreeUnityHostFaultReason.PageReadyTimeout,
                pageGeneration,
                connectionGeneration);
            RetireActiveConnection(pageGeneration, connectionGeneration);
            BeginTrackedHostCleanup(nowMilliseconds);
            if (hostProcess == null)
                CompleteRetirementWithoutHost(pageGeneration, connectionGeneration, nowMilliseconds);
        }

        private void ObservePageStability(long nowMilliseconds)
        {
            if (!lifecycle.TryMarkPageStable(nowMilliseconds))
                return;
            Debug.Log("THREE_UNITY_WEB_BRIDGE_PAGE_STABLE"
                + " pageGeneration=" + lifecycle.PageGeneration
                + " connectionGeneration=" + lifecycle.ConnectionGeneration);
        }

        private void BeginTrackedHostCleanup(long nowMilliseconds)
        {
            var process = hostProcess;
            if (process == null || HasExited(process))
            {
                BeginTrackedJobTermination(nowMilliseconds);
                return;
            }
            if (hostCleanupDeadlineMilliseconds != 0)
                return;

            try { process.CloseMainWindow(); }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
            hostCleanupDeadlineMilliseconds = nowMilliseconds + HostGracefulExitMilliseconds;
        }

        private void AdvanceTrackedHostCleanup(long nowMilliseconds)
        {
            if (lifecycle.State != ThreeUnityHostLifecycleState.Retiring)
                return;

            var pageGeneration = trackedHostPageGeneration != 0
                ? trackedHostPageGeneration
                : lifecycle.PageGeneration;
            var connectionGeneration = trackedHostConnectionGeneration != 0
                ? trackedHostConnectionGeneration
                : lifecycle.ConnectionGeneration;
            var process = hostProcess;
            if (process != null && HasExited(process))
            {
                ObserveTrackedHost(nowMilliseconds);
                return;
            }

            if (hostTerminationStarted)
            {
                BeginTrackedJobTermination(nowMilliseconds);
                CompleteRetirementWithoutHost(
                    pageGeneration,
                    connectionGeneration,
                    nowMilliseconds);
                if (lifecycle.State != ThreeUnityHostLifecycleState.Retiring
                    || hostCleanupDeadlineMilliseconds == 0
                    || nowMilliseconds < hostCleanupDeadlineMilliseconds
                    || hostCleanupTimeoutLogged)
                    return;

                hostCleanupTimeoutLogged = true;
                var activeProcesses = SafeActiveJobProcessCount();
                Debug.LogError("THREE_UNITY_WEB_BRIDGE_HOST_CLEANUP_TIMEOUT"
                    + " pid=" + SafeProcessId(process)
                    + " pageGeneration=" + pageGeneration
                    + " connectionGeneration=" + connectionGeneration
                    + " activeJobProcesses=" + activeProcesses);
                return;
            }

            if (process == null)
            {
                BeginTrackedJobTermination(nowMilliseconds);
                CompleteRetirementWithoutHost(
                    pageGeneration,
                    connectionGeneration,
                    nowMilliseconds);
                return;
            }

            if (hostCleanupDeadlineMilliseconds == 0
                || nowMilliseconds < hostCleanupDeadlineMilliseconds)
                return;

            BeginTrackedJobTermination(nowMilliseconds);
            CompleteRetirementWithoutHost(
                pageGeneration,
                connectionGeneration,
                nowMilliseconds);
        }

        private void BeginTrackedJobTermination(long nowMilliseconds)
        {
            if (!hostTerminationStarted)
            {
                hostTerminationStarted = true;
                hostCleanupDeadlineMilliseconds = AddDeadline(
                    nowMilliseconds,
                    HostKillWaitMilliseconds);
            }

            if (nowMilliseconds < nextHostTerminationAttemptMilliseconds)
                return;
            nextHostTerminationAttemptMilliseconds = AddDeadline(
                nowMilliseconds,
                HostTerminationRetryMilliseconds);

            var usableAssignedJob = trackedHostAssignedToJob
                && hostJob != null
                && !hostJob.IsDisposed;
            if (!usableAssignedJob)
                jobTerminationSucceeded = true;
            else if (!jobTerminationSucceeded)
            {
                try
                {
                    hostJob.Terminate();
                    jobTerminationSucceeded = true;
                }
                catch (InvalidOperationException exception)
                {
                    LogJobTerminationFailure(exception.Message);
                }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    LogJobTerminationFailure(exception.Message);
                }
            }

            // A failed Job termination may still allow the root Host to be
            // killed, but that does not prove its WebView2 children are gone.
            // Keep the two facts separate so the Job call is retried above.
            if (!jobTerminationSucceeded && !rootHostKillIssued)
            {
                var process = hostProcess;
                try
                {
                    if (process != null && !HasExited(process))
                        process.Kill();
                    rootHostKillIssued = true;
                }
                catch (InvalidOperationException) { rootHostKillIssued = true; }
                catch (System.ComponentModel.Win32Exception exception)
                {
                    Debug.LogError("THREE_UNITY_WEB_BRIDGE_PROCESS_KILL_FAILED " + exception.Message);
                }
            }
        }

        private void CompleteRetirementWithoutHost(
            long pageGeneration,
            long connectionGeneration,
            long nowMilliseconds)
        {
            var process = hostProcess;
            if (process != null)
            {
                if (!HasExited(process))
                    return;
                DisposeQuietly(process);
                if (ReferenceEquals(hostProcess, process))
                    hostProcess = null;
            }

            if (hostJob != null)
            {
                if (hostJobPageGeneration != pageGeneration
                    || hostJobConnectionGeneration != connectionGeneration)
                    return;
                var activeProcesses = SafeActiveJobProcessCount();
                if (activeProcesses != 0)
                {
                    BeginTrackedJobTermination(nowMilliseconds);
                    activeProcesses = SafeActiveJobProcessCount();
                }
                if (activeProcesses != 0)
                    return;
                Debug.Log("THREE_UNITY_WEB_BRIDGE_JOB_DRAINED"
                    + " pageGeneration=" + pageGeneration
                    + " connectionGeneration=" + connectionGeneration
                    + " activeProcesses=0");
                DisposeCurrentHostJob();
            }

            if (!lifecycle.CompleteRetirement(pageGeneration, connectionGeneration, nowMilliseconds))
                return;
            trackedHostPageGeneration = 0;
            trackedHostConnectionGeneration = 0;
            hostCleanupDeadlineMilliseconds = 0;
            ResetHostTerminationState();
            hostCleanupTimeoutLogged = false;
            trackedHostAssignedToJob = false;
            Debug.Log("THREE_UNITY_WEB_BRIDGE_RELAUNCH_SCHEDULED"
                + " pageGeneration=" + pageGeneration
                + " connectionGeneration=" + connectionGeneration
                + " reason=" + FaultReasonName(lifecycle.LastDisconnectReason)
                + " delayMs=" + lifecycle.LastRetryDelayMilliseconds
                + " relaunches=" + lifecycle.Relaunches);
        }

        private void RetireActiveConnection(long pageGeneration, long connectionGeneration)
        {
            var connection = activeConnection;
            if (connection == null
                || connection.PageGeneration != pageGeneration
                || connection.ConnectionGeneration != connectionGeneration)
                return;
            activeConnection = null;
            if (ReferenceEquals(legacyGenerationlessLease, connection.Lease))
                legacyGenerationlessLease = null;
            Interlocked.Exchange(ref activePageGeneration, 0);
            Interlocked.Exchange(ref activeConnectionGeneration, 0);
            var removedInbound = messagesFromWeb.Clear();
            if (removedInbound > 0)
                Interlocked.Add(ref transportGenerationRejected, removedInbound);
            RetireConnection(connection);
        }

        private void RetireConnection(ConnectionResources connection)
        {
            if (!connection.Retire())
                return;
            outboundMetrics.Retire(connection.Outbound.Snapshot());
            retiredConnections.Add(connection);
        }

        private static void DetachHostDiagnostics(Process process, ConnectionResources connection)
        {
            if (process == null || connection == null)
                return;
            var handler = connection.ErrorHandler;
            if (handler == null)
                return;
            try { process.CancelErrorRead(); }
            catch (InvalidOperationException) { }
            process.ErrorDataReceived -= handler;
            connection.ErrorHandler = null;
        }

        private void CleanupRetiredConnections(bool waitForThreads)
        {
            for (var index = retiredConnections.Count - 1; index >= 0; index--)
            {
                var connection = retiredConnections[index];
                if (!waitForThreads && connection.HasLiveThreads)
                    continue;
                connection.Dispose(waitForThreads ? 500 : 0);
                retiredConnections.RemoveAt(index);
            }
        }
        [Obsolete("Capture and retain the ThreeUnityWebBridgeLease returned by TryReceiveFromWeb; generationless replies cannot be isolated after a Host relaunch.")]
        public bool TryReceiveFromWeb(out string json)
        {
            if (!TryGetLegacyGenerationlessLease(out var legacyLease))
            {
                LogLegacyGenerationlessRejection("receive");
                json = null;
                return false;
            }
            if (!TryReceiveFromWeb(
                out json,
                out ThreeUnityWebBridgeLease receivedLease))
                return false;
            if (ReferenceEquals(legacyLease, receivedLease))
                return true;

            LogLegacyGenerationlessRejection("receive");
            json = null;
            return false;
        }

        public bool TryReceiveFromWeb(out string json, out long pageGeneration)
        {
            if (TryReceiveFromWeb(out json, out ThreeUnityWebBridgeLease lease))
            {
                pageGeneration = lease.PageGeneration;
                return true;
            }
            pageGeneration = 0;
            return false;
        }

        public bool TryReceiveFromWeb(
            out string json,
            out ThreeUnityWebBridgeLease lease)
        {
            lease = null;
            var currentPage = lifecycle.PageGeneration;
            var currentConnection = lifecycle.ConnectionGeneration;
            var connection = activeConnection;
            while (true)
            {
                var found = messagesFromWeb.TryDequeueCurrent(
                    currentPage,
                    currentConnection,
                    out json,
                    out var rejected);
                if (rejected > 0)
                    RecordGenerationRejections(rejected, "inbound", currentPage, currentConnection);
                if (!found)
                    break;
                if (!lifecycle.CanAcceptGeneration(currentPage, currentConnection))
                {
                    RecordGenerationRejections(1, "inbound-retired", currentPage, currentConnection);
                    continue;
                }
                if (connection == null
                    || !ReferenceEquals(connection, activeConnection)
                    || connection.IsRetired
                    || connection.Lease == null
                    || !connection.Lease.Matches(
                        leaseIssuerIdentity,
                        connection.LeaseIdentity,
                        currentPage,
                        currentConnection))
                {
                    RecordGenerationRejections(1, "inbound-lease", currentPage, currentConnection);
                    continue;
                }
                lease = connection.Lease;
                if (lifecycle.TryMarkBridgeReady(currentPage, currentConnection))
                {
                    Debug.Log("THREE_UNITY_WEB_BRIDGE_BRIDGE_READY"
                        + " pageGeneration=" + currentPage
                        + " connectionGeneration=" + currentConnection);
                }
                return true;
            }

            json = null;
            return false;
        }

        public bool TryAcquireCurrentLease(out ThreeUnityWebBridgeLease lease)
        {
            var connection = activeConnection;
            if (connection != null
                && TryGetWritableConnection(connection.Lease, out _))
            {
                lease = connection.Lease;
                return true;
            }
            lease = null;
            return false;
        }

        public bool IsLeaseCurrent(ThreeUnityWebBridgeLease lease)
        {
            return TryGetWritableConnection(lease, out _);
        }

        [Obsolete("Use TryAcquireCurrentLease and SendToWeb(lease, message). This compatibility API fails closed after the first physical Host relaunch.")]
        public void SendToWeb(string message)
        {
            if (!TryGetLegacyGenerationlessLease(out var lease))
            {
                LogLegacyGenerationlessRejection("outbound");
                return;
            }
            SendToWeb(lease, message);
        }

        [Obsolete("Use SendToWeb(ThreeUnityWebBridgeLease, string) so both page and connection identity are checked.")]
        public bool SendToWeb(long pageGeneration, string message)
        {
            if (!TryGetWritableConnection(pageGeneration, out var connection))
            {
                LogGenerationRejection("outbound", pageGeneration, lifecycle.ConnectionGeneration);
                return false;
            }
            return SendToWeb(connection.Lease, message);
        }

        public bool SendToWeb(ThreeUnityWebBridgeLease lease, string message)
        {
            return SendToWeb(lease, null, message);
        }

        internal bool SendToWeb(
            ThreeUnityWebBridgeLease lease,
            object owner,
            string message)
        {
            if (!TryGetWritableConnection(lease, out var connection))
            {
                LogGenerationRejection(
                    "outbound-lease",
                    lease == null ? 0 : lease.PageGeneration,
                    lease == null ? 0 : lease.ConnectionGeneration);
                return false;
            }
            if (!connection.Outbound.EnqueueReliable(owner, message ?? string.Empty))
            {
                var rejected = connection.Outbound.Snapshot().ReliableBackpressureRejected;
                // A stalled writer can make the retained head retry every frame.
                // Preserve exact retry metrics but keep logs exponentially bounded.
                if (IsPowerOfTwo(rejected))
                {
                    Debug.LogWarning("THREE_UNITY_WEB_BRIDGE_RELIABLE_BACKPRESSURE"
                        + " pageGeneration=" + lease.PageGeneration
                        + " connectionGeneration=" + lease.ConnectionGeneration
                        + " rejected=" + rejected);
                }
                return false;
            }
            connection.OutboundSignal.Set();
            return true;
        }

        [Obsolete("Use TryAcquireCurrentLease and SendLatestToWeb(lease, stream, message). This compatibility API fails closed after the first physical Host relaunch.")]
        public void SendLatestToWeb(string stream, string message)
        {
            if (!TryGetLegacyGenerationlessLease(out var lease))
            {
                LogLegacyGenerationlessRejection("outbound-latest");
                return;
            }
            SendLatestToWeb(lease, stream, message);
        }

        [Obsolete("Use SendLatestToWeb(ThreeUnityWebBridgeLease, string, string) so both page and connection identity are checked.")]
        public bool SendLatestToWeb(long pageGeneration, string stream, string message)
        {
            if (!TryGetWritableConnection(pageGeneration, out var connection))
            {
                LogGenerationRejection("outbound-latest", pageGeneration, lifecycle.ConnectionGeneration);
                return false;
            }
            return SendLatestToWeb(connection.Lease, stream, message);
        }

        public bool SendLatestToWeb(
            ThreeUnityWebBridgeLease lease,
            string stream,
            string message)
        {
            return SendLatestToWeb(lease, null, stream, message);
        }

        internal bool SendLatestToWeb(
            ThreeUnityWebBridgeLease lease,
            object owner,
            string stream,
            string message)
        {
            if (!TryGetWritableConnection(lease, out var connection))
            {
                LogGenerationRejection(
                    "outbound-latest-lease",
                    lease == null ? 0 : lease.PageGeneration,
                    lease == null ? 0 : lease.ConnectionGeneration);
                return false;
            }
            connection.Outbound.EnqueueLatest(owner, stream, message ?? string.Empty);
            connection.OutboundSignal.Set();
            return true;
        }

        internal int PurgeOutbound(
            ThreeUnityWebBridgeLease lease,
            object owner)
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));
            if (!TryGetWritableConnection(lease, out var connection))
                return 0;
            return connection.Outbound.PurgeOwner(owner);
        }

        public ThreeUnityBridgeTransportMetrics GetTransportMetrics()
        {
            var connection = activeConnection;
            return new ThreeUnityBridgeTransportMetrics
            {
                WebMessagesReceived = Interlocked.Read(ref webMessagesReceived),
                WebCharactersReceived = Interlocked.Read(ref webCharactersReceived),
                UnityMessagesWritten = Interlocked.Read(ref unityMessagesWritten),
                UnityCharactersWritten = Interlocked.Read(ref unityCharactersWritten),
                InboundPending = messagesFromWeb.Pending,
                MaxInboundPending = Volatile.Read(ref maxInboundPending),
                Outbound = outboundMetrics.Combine(
                    connection == null ? emptyOutbound.Snapshot() : connection.Outbound.Snapshot()),
                Connected = lifecycle.State == ThreeUnityHostLifecycleState.Connected,
                PageGeneration = lifecycle.PageGeneration,
                ConnectionGeneration = lifecycle.ConnectionGeneration,
                DisconnectReason = FaultReasonName(lifecycle.LastDisconnectReason),
                Disconnects = lifecycle.Disconnects,
                Relaunches = lifecycle.Relaunches,
                GenerationRejected = lifecycle.GenerationRejected
                    + Interlocked.Read(ref transportGenerationRejected),
                DuplicateFaultsRejected = lifecycle.DuplicateFaultsRejected,
                HostDiagnosticsReceived = Interlocked.Read(ref hostDiagnosticsReceived),
                HostDiagnosticsRejected = Interlocked.Read(ref hostDiagnosticsRejected),
                InboundOverflowDropped = messagesFromWeb.OverflowDropped,
                HostDiagnosticsOverflowDropped = hostDiagnostics.OverflowDropped,
                LegacyGenerationlessRejected = Interlocked.Read(ref legacyGenerationlessRejected),
                PageReady = lifecycle.CurrentPageReady,
                BridgeReady = lifecycle.CurrentBridgeReady,
                BackoffReset = lifecycle.BackoffResetForCurrentGeneration,
                JobAssignedProcesses = Volatile.Read(ref jobAssignedProcesses),
                ActiveJobProcesses = SafeActiveJobProcessCount(),
            };
        }

        private bool TryGetLegacyGenerationlessLease(out ThreeUnityWebBridgeLease lease)
        {
            lease = legacyGenerationlessLease;
            if (lease == null)
            {
                // If the first legacy call happens after a relaunch, there is no
                // way to know which page initiated it. Fail closed.
                if (lifecycle.Relaunches > 0 || !TryAcquireCurrentLease(out lease))
                    return false;
                legacyGenerationlessLease = lease;
            }
            return TryGetWritableConnection(lease, out _);
        }

        private bool TryGetWritableConnection(long pageGeneration, out ConnectionResources connection)
        {
            connection = activeConnection;
            return connection != null
                && pageGeneration > 0
                && pageGeneration == lifecycle.PageGeneration
                && connection.PageGeneration == pageGeneration
                && connection.ConnectionGeneration == lifecycle.ConnectionGeneration
                && lifecycle.CanAcceptGeneration(pageGeneration, connection.ConnectionGeneration)
                && !connection.IsRetired;
        }

        private bool TryGetWritableConnection(
            ThreeUnityWebBridgeLease lease,
            out ConnectionResources connection)
        {
            connection = activeConnection;
            return lease != null
                && connection != null
                && connection.Lease != null
                && lease.Matches(
                    leaseIssuerIdentity,
                    connection.LeaseIdentity,
                    lifecycle.PageGeneration,
                    lifecycle.ConnectionGeneration)
                && ReferenceEquals(connection.Lease, lease)
                && lifecycle.CanAcceptGeneration(
                    lease.PageGeneration,
                    lease.ConnectionGeneration)
                && !connection.IsRetired;
        }

        private bool IsWritableGeneration(ConnectionResources connection)
        {
            return !connection.IsRetired
                && Interlocked.Read(ref activePageGeneration) == connection.PageGeneration
                && Interlocked.Read(ref activeConnectionGeneration) == connection.ConnectionGeneration;
        }

        private void RecordGenerationRejections(
            int count,
            string direction,
            long pageGeneration,
            long connectionGeneration)
        {
            lifecycle.RecordGenerationRejections(count);
            LogGenerationRejection(direction, pageGeneration, connectionGeneration, false);
        }

        private void LogGenerationRejection(
            string direction,
            long pageGeneration,
            long connectionGeneration,
            bool increment = true)
        {
            if (increment)
                lifecycle.RecordGenerationRejection();
            var rejected = lifecycle.GenerationRejected + Interlocked.Read(ref transportGenerationRejected);
            if (rejected > 8 && (rejected & (rejected - 1)) != 0)
                return;
            Debug.LogWarning("THREE_UNITY_WEB_BRIDGE_GENERATION_REJECTED"
                + " direction=" + direction
                + " pageGeneration=" + pageGeneration
                + " connectionGeneration=" + connectionGeneration
                + " currentPageGeneration=" + lifecycle.PageGeneration
                + " currentConnectionGeneration=" + lifecycle.ConnectionGeneration
                + " rejected=" + rejected);
        }

        private void LogLegacyGenerationlessRejection(string direction)
        {
            var rejected = Interlocked.Increment(ref legacyGenerationlessRejected);
            if (rejected > 8 && (rejected & (rejected - 1)) != 0)
                return;
            Debug.LogWarning("THREE_UNITY_WEB_BRIDGE_LEGACY_API_REJECTED"
                + " direction=" + direction
                + " pageGeneration=" + lifecycle.PageGeneration
                + " connectionGeneration=" + lifecycle.ConnectionGeneration
                + " relaunches=" + lifecycle.Relaunches
                + " rejected=" + rejected);
        }

        private void LogDisconnect(
            ThreeUnityHostFaultReason reason,
            long pageGeneration,
            long connectionGeneration)
        {
            Debug.LogWarning("THREE_UNITY_WEB_BRIDGE_DISCONNECTED"
                + " reason=" + FaultReasonName(reason)
                + " pageGeneration=" + pageGeneration
                + " connectionGeneration=" + connectionGeneration
                + " disconnects=" + lifecycle.Disconnects);
        }

        private void OnDestroy() => StopBridge();
        private void OnApplicationQuit() => StopBridge();

        private void StopBridge()
        {
            if (Interlocked.Exchange(ref stopStarted, 1) != 0)
                return;

            Interlocked.Exchange(ref stopping, 1);
            lifecycle.Stop();
            Interlocked.Exchange(ref activePageGeneration, 0);
            Interlocked.Exchange(ref activeConnectionGeneration, 0);

            var connection = activeConnection;
            activeConnection = null;
            legacyGenerationlessLease = null;
            if (connection != null)
            {
                RetireConnection(connection);
            }

            var removed = messagesFromWeb.Clear();
            if (removed > 0)
                Interlocked.Add(ref transportGenerationRejected, removed);
            var removedDiagnostics = hostDiagnostics.Clear();
            if (removedDiagnostics > 0)
            {
                Interlocked.Add(ref hostDiagnosticsRejected, removedDiagnostics);
                Interlocked.Add(ref transportGenerationRejected, removedDiagnostics);
            }

            var process = hostProcess;
            DetachHostDiagnostics(process, trackedHostConnection);
            trackedHostConnection = null;
            if (process != null)
            {
                try
                {
                    if (!HasExited(process))
                    {
                        try { process.CloseMainWindow(); }
                        catch (InvalidOperationException) { }
                        if (!process.WaitForExit(HostGracefulExitMilliseconds))
                        {
                            if (trackedHostAssignedToJob
                                && hostJob != null
                                && !hostJob.IsDisposed)
                                hostJob.Terminate();
                            else
                                process.Kill();
                            if (!process.WaitForExit(HostKillWaitMilliseconds))
                            {
                                process.Kill();
                                process.WaitForExit(HostKillWaitMilliseconds);
                            }
                        }
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception exception) { Debug.LogWarning(exception.Message); }
                finally { DisposeQuietly(process); }
            }
            hostProcess = null;
            trackedHostAssignedToJob = false;
            DisposeCurrentHostJob();

            CleanupRetiredConnections(true);
            while (lifecycleSignals.TryDequeue(out _)) { }
            Debug.Log("THREE_UNITY_WEB_BRIDGE_STOPPED"
                + " pageGeneration=" + lifecycle.PageGeneration
                + " connectionGeneration=" + lifecycle.ConnectionGeneration);
        }

        private string GetHostPath()
        {
            return Path.Combine(Application.streamingAssetsPath, hostDirectory, "ThreeUnityWebHost.exe");
        }

        private void EnsureHostJob()
        {
            if (!ThreeUnityWindowsHostJob.IsSupported)
                return;
            if (hostJob == null || hostJob.IsDisposed)
            {
                hostJob = new ThreeUnityWindowsHostJob();
                hostJobPageGeneration = 0;
                hostJobConnectionGeneration = 0;
                return;
            }
            if (hostJobPageGeneration != 0 || hostJobConnectionGeneration != 0)
            {
                throw new InvalidOperationException(
                    "A prior ThreeUnity Web Host Job is still owned by an active generation.");
            }
        }

        private int SafeActiveJobProcessCount()
        {
            var job = hostJob;
            if (job == null || job.IsDisposed)
                return 0;
            try { return job.ActiveProcessCount; }
            catch (InvalidOperationException exception)
            {
                if (!hostCleanupTimeoutLogged)
                    Debug.LogError("THREE_UNITY_WEB_BRIDGE_JOB_QUERY_FAILED " + exception.Message);
                return int.MaxValue;
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                if (!hostCleanupTimeoutLogged)
                    Debug.LogError("THREE_UNITY_WEB_BRIDGE_JOB_QUERY_FAILED " + exception.Message);
                return int.MaxValue;
            }
        }

        private void DisposeCurrentHostJob()
        {
            var job = hostJob;
            hostJob = null;
            hostJobPageGeneration = 0;
            hostJobConnectionGeneration = 0;
            trackedHostAssignedToJob = false;
            DisposeQuietly(job);
        }

        private string GetWebRootPath()
        {
            return Path.Combine(Application.streamingAssetsPath, webRootDirectory);
        }

        private string ResolveStorageIdentifier()
        {
            var raw = string.IsNullOrWhiteSpace(storageIdentifier)
                ? Application.productName
                : storageIdentifier;
            return ThreeUnityWebBridgeLifecycle.BuildStorageIdentifier(raw);
        }

        private static int GetCurrentProcessId()
        {
            using (var current = Process.GetCurrentProcess())
                return current.Id;
        }

        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private static int SafeProcessId(Process process)
        {
            if (process == null)
                return -1;
            try { return process.Id; }
            catch (InvalidOperationException) { return -1; }
        }

        private static long NowMilliseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        }

        private static bool IsPowerOfTwo(long value)
        {
            return value > 0 && (value & (value - 1)) == 0;
        }

        private void LogJobTerminationFailure(string message)
        {
            jobTerminationFailures++;
            if (!IsPowerOfTwo(jobTerminationFailures))
                return;
            Debug.LogError("THREE_UNITY_WEB_BRIDGE_JOB_TERMINATE_FAILED"
                + " attempts=" + jobTerminationFailures
                + " message=" + message);
        }

        private void ResetHostTerminationState()
        {
            hostTerminationStarted = false;
            rootHostKillIssued = false;
            jobTerminationSucceeded = false;
            nextHostTerminationAttemptMilliseconds = 0;
            jobTerminationFailures = 0;
        }

        private static long AddDeadline(long nowMilliseconds, int delayMilliseconds)
        {
            return nowMilliseconds > long.MaxValue - delayMilliseconds
                ? long.MaxValue
                : nowMilliseconds + delayMilliseconds;
        }

        private static void DisposeQuietly(IDisposable disposable)
        {
            if (disposable == null)
                return;
            try { disposable.Dispose(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        private static void ObserveMax(ref int target, int value)
        {
            var observed = Volatile.Read(ref target);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(ref target, value, observed);
                if (previous == observed)
                    return;
                observed = previous;
            }
        }

        private static string FaultReasonName(ThreeUnityHostFaultReason reason)
        {
            switch (reason)
            {
                case ThreeUnityHostFaultReason.HostExitedBeforeConnect: return "host-exited-before-connect";
                case ThreeUnityHostFaultReason.HostExited: return "host-exited";
                case ThreeUnityHostFaultReason.ReaderEof: return "reader-eof";
                case ThreeUnityHostFaultReason.ReaderIOException: return "reader-io-exception";
                case ThreeUnityHostFaultReason.WriterIOException: return "writer-io-exception";
                case ThreeUnityHostFaultReason.HostLaunchFailure: return "host-launch-failure";
                case ThreeUnityHostFaultReason.ConnectTimeout: return "connect-timeout";
                case ThreeUnityHostFaultReason.PageReadyTimeout: return "page-ready-timeout";
                case ThreeUnityHostFaultReason.HostFatal: return "host-fatal";
                case ThreeUnityHostFaultReason.InboundOverflow: return "inbound-overflow";
                default: return "none";
            }
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private enum LifecycleSignalKind
        {
            Connected,
            Fault,
        }

        private readonly struct LifecycleSignal
        {
            private LifecycleSignal(
                LifecycleSignalKind kind,
                long pageGeneration,
                long connectionGeneration,
                ThreeUnityHostFaultReason faultReason)
            {
                Kind = kind;
                PageGeneration = pageGeneration;
                ConnectionGeneration = connectionGeneration;
                FaultReason = faultReason;
            }

            public LifecycleSignalKind Kind { get; }
            public long PageGeneration { get; }
            public long ConnectionGeneration { get; }
            public ThreeUnityHostFaultReason FaultReason { get; }

            public static LifecycleSignal Connected(ConnectionResources connection)
            {
                return new LifecycleSignal(
                    LifecycleSignalKind.Connected,
                    connection.PageGeneration,
                    connection.ConnectionGeneration,
                    ThreeUnityHostFaultReason.None);
            }

            public static LifecycleSignal Fault(
                ConnectionResources connection,
                ThreeUnityHostFaultReason reason)
            {
                return new LifecycleSignal(
                    LifecycleSignalKind.Fault,
                    connection.PageGeneration,
                    connection.ConnectionGeneration,
                    reason);
            }
        }

        private sealed class ConnectionResources
        {
            private int retired;

            public ConnectionResources(long pageGeneration, long connectionGeneration, string pipeName)
            {
                PageGeneration = pageGeneration;
                ConnectionGeneration = connectionGeneration;
                LeaseIdentity = new object();
                Pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                Outbound = new ThreeUnityOutboundBuffer();
                OutboundSignal = new AutoResetEvent(false);
                JobAssignedSignal = new ManualResetEventSlim(false);
            }

            public long PageGeneration { get; }
            public long ConnectionGeneration { get; }
            public object LeaseIdentity { get; }
            public ThreeUnityWebBridgeLease Lease { get; set; }
            public NamedPipeServerStream Pipe { get; }
            public ThreeUnityOutboundBuffer Outbound { get; }
            public AutoResetEvent OutboundSignal { get; }
            public ManualResetEventSlim JobAssignedSignal { get; }
            public StreamWriter Writer { get; set; }
            public Thread ReaderThread { get; set; }
            public Thread WriterThread { get; set; }
            public bool IsRetired => Volatile.Read(ref retired) != 0;
            public bool HasLiveThreads => (ReaderThread != null && ReaderThread.IsAlive)
                || (WriterThread != null && WriterThread.IsAlive);

            public DataReceivedEventHandler ErrorHandler { get; set; }

            public bool Retire()
            {
                if (Interlocked.Exchange(ref retired, 1) != 0)
                    return false;
                try { OutboundSignal.Set(); }
                catch (ObjectDisposedException) { }
                try { JobAssignedSignal.Set(); }
                catch (ObjectDisposedException) { }
                DisposeQuietly(Pipe);
                return true;
            }

            public void Dispose(int joinMilliseconds)
            {
                Retire();
                if (ReaderThread != null && ReaderThread != Thread.CurrentThread)
                    ReaderThread.Join(joinMilliseconds);
                if (WriterThread != null && WriterThread != Thread.CurrentThread)
                    WriterThread.Join(joinMilliseconds);
                DisposeQuietly(Writer);
                DisposeQuietly(Pipe);
                DisposeQuietly(OutboundSignal);
                DisposeQuietly(JobAssignedSignal);
                ReaderThread = null;
                WriterThread = null;
                Writer = null;
            }
        }
    }

    public sealed class ThreeUnityBridgeTransportMetrics
    {
        public long WebMessagesReceived { get; internal set; }
        public long WebCharactersReceived { get; internal set; }
        public long UnityMessagesWritten { get; internal set; }
        public long UnityCharactersWritten { get; internal set; }
        public int InboundPending { get; internal set; }
        public int MaxInboundPending { get; internal set; }
        public ThreeUnityOutboundBufferSnapshot Outbound { get; internal set; }
        public bool Connected { get; internal set; }
        public long PageGeneration { get; internal set; }
        public long ConnectionGeneration { get; internal set; }
        public string DisconnectReason { get; internal set; }
        public long Disconnects { get; internal set; }
        public long Relaunches { get; internal set; }
        public long GenerationRejected { get; internal set; }
        public long DuplicateFaultsRejected { get; internal set; }
        public long HostDiagnosticsReceived { get; internal set; }
        public long HostDiagnosticsRejected { get; internal set; }
        public long InboundOverflowDropped { get; internal set; }
        public long HostDiagnosticsOverflowDropped { get; internal set; }
        public long LegacyGenerationlessRejected { get; internal set; }
        public bool PageReady { get; internal set; }
        public bool BridgeReady { get; internal set; }
        public bool BackoffReset { get; internal set; }
        public int JobAssignedProcesses { get; internal set; }
        public int ActiveJobProcesses { get; internal set; }
    }
}
