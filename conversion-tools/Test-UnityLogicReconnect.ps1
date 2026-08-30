[CmdletBinding(DefaultParameterSetName = 'SuspendResume')]
param(
    [Parameter(Mandatory = $true)]
    [string] $PlayerExe,

    [Parameter(Mandatory = $true)]
    [string] $LogFile,

    [Parameter(ParameterSetName = 'SuspendResume')]
    [ValidateRange(600, 60000)]
    [int] $SuspendMilliseconds = 1200,

    [Parameter(Mandatory = $true, ParameterSetName = 'KillHost')]
    [switch] $KillHost,

    [Parameter(Mandatory = $true, ParameterSetName = 'HangBeforeConnect')]
    [switch] $HangBeforeConnect,

    [Parameter(ParameterSetName = 'HangBeforeConnect')]
    [ValidateRange(10500, 60000)]
    [int] $HangMilliseconds = 11500,

    [ValidateRange(1, 600)]
    [int] $StartupTimeoutSeconds = 30,

    [ValidateRange(1, 600)]
    [int] $RecoveryTimeoutSeconds = 30,

    [ValidateRange(1, 60)]
    [int] $ShutdownTimeoutSeconds = 10,

    [string[]] $PlayerArguments = @(),

    [switch] $OverwriteLog,

    [Parameter(ParameterSetName = 'SuspendResume')]
    [switch] $SkipInputStale,

    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:FailurePattern = '(?m)^(?:.*THREE_UNITY_LOGIC_PROTOCOL_ERROR.*|.*THREE_UNITY_WEB_BRIDGE_RELIABLE_OVERFLOW.*|.*THREE_UNITY_WEB_BRIDGE_INBOUND_OVERFLOW.*|.*THREE_UNITY_WEB_BRIDGE_HOST_CLEANUP_TIMEOUT.*|.*THREE_UNITY_WEB_HOST_DIAGNOSTIC.*THREE_UNITY_WEB_HOST_FATAL.*|Crash!!!|Unhandled Exception:.*|.*Native Crash Reporting.*)$'
$script:PreConnectEvidencePattern = '(?m)^.*(?:THREE_UNITY_WEB_BRIDGE_CONNECTED|THREE_UNITY_WEB_BRIDGE_PAGE_READY|THREE_UNITY_LOGIC_READY)\b.*$'
$script:ConnectTimeoutPattern = '(?m)^.*THREE_UNITY_WEB_BRIDGE_DISCONNECTED\b(?=[^\n]*\breason=connect-timeout\b)[^\n]*'
$script:MaxConcurrentHostsObserved = 0

function Resolve-LeafPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $ParameterName
    )

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.File]::Exists($resolved)) {
        throw [System.IO.FileNotFoundException]::new("$ParameterName does not exist: $resolved", $resolved)
    }
    return $resolved
}

function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string] $Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [System.Text.StringBuilder]::new()
    [void] $builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void] $builder.Append(('\' * (($backslashes * 2) + 1)))
            [void] $builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void] $builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void] $builder.Append($character)
    }
    if ($backslashes -gt 0) {
        [void] $builder.Append(('\' * ($backslashes * 2)))
    }
    [void] $builder.Append('"')
    return $builder.ToString()
}

function Read-SharedTextFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not [System.IO.File]::Exists($Path)) {
        return $null
    }

    try {
        $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            $share)
        try {
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
            try {
                return $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    catch [System.IO.IOException] {
        return $null
    }
}

function Test-ProcessExited {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process] $Process)

    try {
        $Process.Refresh()
        return $Process.HasExited
    }
    catch [System.InvalidOperationException] {
        return $true
    }
}

function Get-AtMostOneExactChildHost {
    param([Parameter(Mandatory = $true)][int] $PlayerId)

    $children = @(
        Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId = $PlayerId" |
            Where-Object { $_.Name -eq 'ThreeUnityWebHost.exe' }
    )
    $script:MaxConcurrentHostsObserved = [Math]::Max(
        $script:MaxConcurrentHostsObserved,
        $children.Count)
    if ($children.Count -gt 1) {
        $ids = ($children.ProcessId -join ',')
        throw "Player PID $PlayerId owns multiple simultaneous ThreeUnityWebHost children ($ids)."
    }
    if ($children.Count -eq 0) {
        return $null
    }

    $child = $children[0]
    $parentArgument = '(?:^|\s)--parent-pid\s+"?' + [regex]::Escape([string] $PlayerId) + '"?(?:\s|$)'
    if ([string]::IsNullOrWhiteSpace([string] $child.CommandLine) -or $child.CommandLine -notmatch $parentArgument) {
        throw "Refusing fault injection: child PID $($child.ProcessId) does not declare --parent-pid $PlayerId."
    }
    if ([System.IO.Path]::GetFileName([string] $child.ExecutablePath) -ne 'ThreeUnityWebHost.exe') {
        throw "Refusing fault injection: child PID $($child.ProcessId) executable is not ThreeUnityWebHost.exe."
    }
    return $child
}

function Wait-ForCurrentRunLog {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][datetime] $LaunchTimeUtc,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Player,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Test-ProcessExited $Player) {
            throw "Unity Player exited before it created the requested log file (exit code $($Player.ExitCode))."
        }
        [void] (Get-AtMostOneExactChildHost -PlayerId $Player.Id)
        if ([System.IO.File]::Exists($Path)) {
            $item = Get-Item -LiteralPath $Path
            # FAT/legacy timestamp precision can be coarse, hence the two-second tolerance.
            if ($item.Length -gt 0 -and $item.LastWriteTimeUtc -ge $LaunchTimeUtc.AddSeconds(-2)) {
                return
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for current-run log: $Path"
}

function Wait-ForLogEvent {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][string] $SuccessPattern,
        [Parameter(Mandatory = $true)][int] $StartIndex,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Player
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Test-ProcessExited $Player) {
            throw "Unity Player exited while waiting for $Description (exit code $($Player.ExitCode))."
        }
        [void] (Get-AtMostOneExactChildHost -PlayerId $Player.Id)

        $content = Read-SharedTextFile $Path
        if ($null -ne $content) {
            $safeStart = [Math]::Min($StartIndex, $content.Length)
            $unread = $content.Substring($safeStart)
            $success = [regex]::Match(
                $unread,
                $SuccessPattern,
                [System.Text.RegularExpressions.RegexOptions]::None,
                [timespan]::FromSeconds(1))
            $successIndex = if ($success.Success) { $safeStart + $success.Index } else { -1 }

            $failure = [regex]::Match(
                $unread,
                $script:FailurePattern,
                [System.Text.RegularExpressions.RegexOptions]::None,
                [timespan]::FromSeconds(1))
            $failureIndex = if ($failure.Success) { $safeStart + $failure.Index } else { -1 }
            if ($failureIndex -ge 0 -and ($successIndex -lt 0 -or $failureIndex -lt $successIndex)) {
                throw "Explicit failure while waiting for ${Description}: $($failure.Value.Trim())"
            }
            if ($successIndex -ge $safeStart) {
                return [pscustomobject]@{
                    Description = $Description
                    Index = $successIndex
                    NextIndex = $successIndex + $success.Length
                    Marker = $success.Value.Trim()
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for $Description in $Path"
}

function Get-ExactChildHost {
    param(
        [Parameter(Mandatory = $true)][int] $PlayerId,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Player,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Test-ProcessExited $Player) {
            throw "Unity Player exited before ThreeUnityWebHost started (exit code $($Player.ExitCode))."
        }

        $child = Get-AtMostOneExactChildHost -PlayerId $PlayerId
        if ($null -ne $child) {
            return $child
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for the exact ThreeUnityWebHost child of Player PID $PlayerId."
}

function Assert-NoPreConnectEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [switch] $AllowEvidenceAfterTimeout
    )

    $content = Read-SharedTextFile $Path
    if ($null -eq $content) {
        return
    }

    $failure = [regex]::Match($content, $script:FailurePattern)
    if ($failure.Success) {
        throw "Explicit failure before initial Host connection: $($failure.Value.Trim())"
    }

    $evidence = [regex]::Match($content, $script:PreConnectEvidencePattern)
    $timeout = [regex]::Match($content, $script:ConnectTimeoutPattern)
    if ($evidence.Success -and (-not $AllowEvidenceAfterTimeout -or -not $timeout.Success `
        -or $evidence.Index -lt $timeout.Index)) {
        throw "HangBeforeConnect captured the Host too late; connection/readiness evidence already appeared before the expected connect-timeout: $($evidence.Value.Trim())"
    }

    $disconnect = [regex]::Match(
        $content,
        '(?m)^.*THREE_UNITY_WEB_BRIDGE_DISCONNECTED\b.*$')
    if ($disconnect.Success -and (-not $timeout.Success -or $disconnect.Index -lt $timeout.Index)) {
        throw "Unexpected disconnect before the required connect-timeout: $($disconnect.Value.Trim())"
    }
}

function Get-ExactChildHostBeforeConnect {
    param(
        [Parameter(Mandatory = $true)][int] $PlayerId,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Player,
        [Parameter(Mandatory = $true)][string] $LogPath,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Test-ProcessExited $Player) {
            throw "Unity Player exited before the pre-connect Host could be captured (exit code $($Player.ExitCode))."
        }

        Assert-NoPreConnectEvidence -Path $LogPath
        $child = Get-AtMostOneExactChildHost -PlayerId $PlayerId
        if ($null -ne $child) {
            # Re-read after process discovery so a marker written during the CIM
            # query cannot turn a late capture into a false pass.
            Assert-NoPreConnectEvidence -Path $LogPath
            return $child
        }
        Start-Sleep -Milliseconds 25
    }
    throw "Timed out after $TimeoutSeconds seconds waiting to capture ThreeUnityWebHost before its first connection."
}

function Wait-ForCapturedHostExit {
    param(
        [Parameter(Mandatory = $true)][IntPtr] $Handle,
        [Parameter(Mandatory = $true)][int] $HostId,
        [Parameter(Mandatory = $true)][int] $PlayerId,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Player,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Test-ProcessExited $Player) {
            throw "Unity Player exited while waiting for captured Host PID $HostId to exit (exit code $($Player.ExitCode))."
        }

        $current = Get-AtMostOneExactChildHost -PlayerId $PlayerId
        $exited = [ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit($Handle, 0)
        if (-not $exited -and $null -ne $current -and [int] $current.ProcessId -ne $HostId) {
            throw "Replacement Host PID $($current.ProcessId) appeared before captured Host PID $HostId exited."
        }
        if ($exited) {
            return
        }
        Start-Sleep -Milliseconds 50
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for captured Host PID $HostId to exit."
}

function Wait-ForReplacementHost {
    param(
        [Parameter(Mandatory = $true)][int] $PlayerId,
        [Parameter(Mandatory = $true)][int] $OldHostId,
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Player,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Test-ProcessExited $Player) {
            throw "Unity Player exited before it launched a replacement ThreeUnityWebHost (exit code $($Player.ExitCode))."
        }
        $current = Get-AtMostOneExactChildHost -PlayerId $PlayerId
        if ($null -ne $current) {
            if ([int] $current.ProcessId -eq $OldHostId) {
                Start-Sleep -Milliseconds 50
                continue
            }
            return $current
        }
        Start-Sleep -Milliseconds 50
    }
    throw "Timed out after $TimeoutSeconds seconds waiting for a replacement ThreeUnityWebHost after PID $OldHostId."
}

function Wait-ForPlayerExitWithHostMonitor {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process] $Player,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (Test-ProcessExited $Player) {
            return $true
        }
        [void] (Get-AtMostOneExactChildHost -PlayerId $Player.Id)
        Start-Sleep -Milliseconds 100
    }
    return (Test-ProcessExited $Player)
}

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw 'This fault harness only supports Windows.'
}

$playerPath = Resolve-LeafPath -Path $PlayerExe -ParameterName 'PlayerExe'
if ([System.IO.Path]::GetExtension($playerPath) -ne '.exe') {
    throw "PlayerExe must be a Windows .exe: $playerPath"
}
$logPath = [System.IO.Path]::GetFullPath($LogFile)
if ([string]::Equals($playerPath, $logPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'LogFile must not overwrite PlayerExe.'
}
if ([System.IO.Directory]::Exists($logPath)) {
    throw "LogFile points to a directory: $logPath"
}
if ([System.IO.File]::Exists($logPath) -and -not $OverwriteLog) {
    throw "LogFile already exists. Use a unique path, or pass -OverwriteLog explicitly: $logPath"
}
if ($PlayerArguments | Where-Object { $_ -ieq '-logFile' -or $_ -imatch '^-logFile=' }) {
    throw 'PlayerArguments must not contain -logFile; use the dedicated LogFile parameter.'
}

$logDirectory = [System.IO.Path]::GetDirectoryName($logPath)
if ([string]::IsNullOrWhiteSpace($logDirectory)) {
    throw "Could not resolve the LogFile parent directory: $logPath"
}

$modeName = if ($HangBeforeConnect) {
    'HangBeforeConnect'
}
elseif ($KillHost) {
    'KillHost'
}
else {
    'SuspendResume'
}
$plan = [pscustomobject]@{
    Mode = $modeName
    PlayerExe = $playerPath
    LogFile = $logPath
    SuspendMilliseconds = if ($modeName -eq 'SuspendResume') { $SuspendMilliseconds } else { $null }
    HangMilliseconds = if ($HangBeforeConnect) { $HangMilliseconds } else { $null }
    StartupTimeoutSeconds = $StartupTimeoutSeconds
    RecoveryTimeoutSeconds = $RecoveryTimeoutSeconds
    ShutdownTimeoutSeconds = $ShutdownTimeoutSeconds
    HostSelection = 'Name=ThreeUnityWebHost.exe AND ParentProcessId=<new Player PID> AND matching --parent-pid'
    NativeAction = if ($HangBeforeConnect) {
        'NtSuspendProcess before first connection; hold beyond the 10-second deadline; resume only if the retained old Host handle is still alive'
    }
    elseif ($KillHost) {
        'TerminateProcess on one retained handle; require old exit before one replacement Host'
    }
    else {
        'NtSuspendProcess/NtResumeProcess on one retained handle for the captured PID only'
    }
    EventSequence = if ($HangBeforeConnect) {
        'DISCONNECTED(reason=connect-timeout) -> RELAUNCH_SCHEDULED -> RELAUNCHED -> LOGIC_TRANSPORT_RESET -> PAGE_READY -> LOGIC_READY -> LOGIC_TICK'
    }
    elseif ($KillHost) {
        'READY -> DISCONNECTED -> RELAUNCH_SCHEDULED -> RELAUNCHED -> LOGIC_TRANSPORT_RESET -> READY -> LOGIC_TICK'
    }
    elseif ($SkipInputStale) {
        'READY -> SESSION_RESTART -> READY -> LOGIC_TICK'
    }
    else {
        'READY -> INPUT_STALE -> SESSION_RESTART -> READY -> LOGIC_TICK'
    }
    KillHost = [bool] $KillHost
    HangBeforeConnect = [bool] $HangBeforeConnect
    SkipInputStale = [bool] $SkipInputStale
    ExistingLogWillBeOverwritten = [bool] ([System.IO.File]::Exists($logPath) -and $OverwriteLog)
}
if ($DryRun) {
    Write-Host 'THREE_UNITY_FAULT_HARNESS_DRY_RUN no process will be launched or faulted'
    $plan
    return
}

if (-not [System.IO.Directory]::Exists($logDirectory)) {
    [void] [System.IO.Directory]::CreateDirectory($logDirectory)
}
if ([System.IO.File]::Exists($logPath)) {
    [System.IO.File]::WriteAllText($logPath, [string]::Empty, [System.Text.UTF8Encoding]::new($false))
}

if (-not ('ThreeUnity.Bridge.Tools.NativeProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ThreeUnity.Bridge.Tools
{
    public static class NativeProcess
    {
        private const uint ProcessTerminate = 0x0001;
        private const uint ProcessSuspendResume = 0x0800;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint Synchronize = 0x00100000;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitTimeout = 0x00000102;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

        [DllImport("ntdll.dll")]
        private static extern uint NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern uint NtResumeProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(uint status);

        public static IntPtr OpenExact(int processId)
        {
            IntPtr handle = OpenProcess(ProcessTerminate | ProcessSuspendResume | ProcessQueryLimitedInformation | Synchronize, false, processId);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess failed for PID " + processId);
            return handle;
        }

        public static void Suspend(IntPtr handle)
        {
            uint status = NtSuspendProcess(handle);
            if (status != 0)
                throw new Win32Exception((int)RtlNtStatusToDosError(status), "NtSuspendProcess failed with NTSTATUS 0x" + status.ToString("X8"));
        }

        public static void Resume(IntPtr handle)
        {
            uint status = NtResumeProcess(handle);
            if (status != 0)
                throw new Win32Exception((int)RtlNtStatusToDosError(status), "NtResumeProcess failed with NTSTATUS 0x" + status.ToString("X8"));
        }

        public static bool WaitForExit(IntPtr handle, int milliseconds)
        {
            if (milliseconds < 0)
                throw new ArgumentOutOfRangeException("milliseconds");
            uint result = WaitForSingleObject(handle, (uint)milliseconds);
            if (result == WaitObject0)
                return true;
            if (result == WaitTimeout)
                return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject failed");
        }

        public static void Terminate(IntPtr handle)
        {
            if (!TerminateProcess(handle, 73))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "TerminateProcess failed for the captured Host handle");
        }
    }
}
'@
}

$player = $null
$playerPid = 0
$hostIdentity = $null
$hostHandle = [IntPtr]::Zero
$replacementHostIdentity = $null
$replacementHostHandle = [IntPtr]::Zero
$hostSuspended = $false
$runFailure = $null
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$events = [System.Collections.Generic.List[object]]::new()
$launchUtc = [datetime]::UtcNow

try {
    $arguments = @($PlayerArguments) + @('-logFile', $logPath)
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $playerPath
    $startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($playerPath)
    $startInfo.UseShellExecute = $false
    $startInfo.Arguments = (($arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument $_ }) -join ' ')
    $player = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $player) {
        throw 'System.Diagnostics.Process.Start returned null.'
    }
    $playerPid = $player.Id
    Write-Host "THREE_UNITY_FAULT_HARNESS_START playerPid=$playerPid"

    if ($HangBeforeConnect) {
        $hostIdentity = Get-ExactChildHostBeforeConnect -PlayerId $playerPid -Player $player `
            -LogPath $logPath -TimeoutSeconds $StartupTimeoutSeconds
    }
    else {
        Wait-ForCurrentRunLog -Path $logPath -LaunchTimeUtc $launchUtc -Player $player -TimeoutSeconds $StartupTimeoutSeconds
        $hostIdentity = Get-ExactChildHost -PlayerId $playerPid -Player $player -TimeoutSeconds $StartupTimeoutSeconds
    }
    $hostHandle = [ThreeUnity.Bridge.Tools.NativeProcess]::OpenExact([int] $hostIdentity.ProcessId)
    Write-Host "THREE_UNITY_FAULT_HARNESS_HOST playerPid=$playerPid hostPid=$($hostIdentity.ProcessId)"

    if ($HangBeforeConnect) {
        Assert-NoPreConnectEvidence -Path $logPath
        [ThreeUnity.Bridge.Tools.NativeProcess]::Suspend($hostHandle)
        $hostSuspended = $true
        # Check again after suspension to reject the narrow discovery/open race.
        Assert-NoPreConnectEvidence -Path $logPath
        $faultCursor = 0
    }
    else {
        $ready = Wait-ForLogEvent -Path $logPath -Description 'initial logic READY' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_READY\b.*$' -StartIndex 0 `
            -TimeoutSeconds $StartupTimeoutSeconds -Player $player
        $events.Add($ready)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($ready.Marker)"

        $contentAtFaultStart = Read-SharedTextFile $logPath
        $faultCursor = if ($null -ne $contentAtFaultStart) {
            [Math]::Max($ready.NextIndex, $contentAtFaultStart.Length)
        }
        else {
            $ready.NextIndex
        }
    }
    if ($HangBeforeConnect) {
        $oldHostId = [int] $hostIdentity.ProcessId
        $hangStarted = [System.Diagnostics.Stopwatch]::StartNew()
        Write-Host "THREE_UNITY_FAULT_HARNESS_HANG_BEFORE_CONNECT hostPid=$oldHostId milliseconds=$HangMilliseconds"
        while ($hangStarted.ElapsedMilliseconds -lt $HangMilliseconds) {
            if (Test-ProcessExited $player) {
                throw "Unity Player exited while its pre-connect WebHost was suspended (exit code $($player.ExitCode))."
            }

            Assert-NoPreConnectEvidence -Path $logPath -AllowEvidenceAfterTimeout
            $currentHost = Get-AtMostOneExactChildHost -PlayerId $playerPid
            $oldHostExited = [ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit($hostHandle, 0)
            if (-not $oldHostExited -and $null -ne $currentHost `
                -and [int] $currentHost.ProcessId -ne $oldHostId) {
                throw "Replacement Host PID $($currentHost.ProcessId) appeared before suspended Host PID $oldHostId exited."
            }

            $remaining = $HangMilliseconds - [int] $hangStarted.ElapsedMilliseconds
            if ($remaining -gt 0) {
                Start-Sleep -Milliseconds ([Math]::Min(25, $remaining))
            }
        }

        # The Host may have been terminated by the launcher's bounded retirement
        # while suspended. Resume only a still-live retained handle.
        if ([ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit($hostHandle, 0)) {
            $hostSuspended = $false
            Write-Host "THREE_UNITY_FAULT_HARNESS_HUNG_HOST_EXITED hostPid=$oldHostId"
        }
        else {
            try {
                [ThreeUnity.Bridge.Tools.NativeProcess]::Resume($hostHandle)
                $hostSuspended = $false
                Write-Host "THREE_UNITY_FAULT_HARNESS_RESUME hostPid=$oldHostId"
            }
            catch {
                if ([ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit($hostHandle, 0)) {
                    $hostSuspended = $false
                    Write-Host "THREE_UNITY_FAULT_HARNESS_HUNG_HOST_EXITED hostPid=$oldHostId"
                }
                else {
                    throw
                }
            }
        }

        $disconnected = Wait-ForLogEvent -Path $logPath -Description 'pre-connect Host timeout' `
            -SuccessPattern $script:ConnectTimeoutPattern -StartIndex $faultCursor `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($disconnected)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($disconnected.Marker)"

        Wait-ForCapturedHostExit -Handle $hostHandle -HostId $oldHostId -PlayerId $playerPid `
            -Player $player -TimeoutSeconds $RecoveryTimeoutSeconds
        Write-Host "THREE_UNITY_FAULT_HARNESS_HOST_EXITED hostPid=$oldHostId"

        $scheduled = Wait-ForLogEvent -Path $logPath -Description 'Host relaunch scheduling after connect timeout' `
            -SuccessPattern '(?m)^.*THREE_UNITY_WEB_BRIDGE_RELAUNCH_SCHEDULED\b(?=[^\n]*\breason=connect-timeout\b)[^\n]*' `
            -StartIndex $disconnected.NextIndex -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($scheduled)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($scheduled.Marker)"

        $relaunched = Wait-ForLogEvent -Path $logPath -Description 'replacement Host relaunch' `
            -SuccessPattern '(?m)^.*THREE_UNITY_WEB_BRIDGE_RELAUNCHED\b.*$' -StartIndex $scheduled.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($relaunched)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($relaunched.Marker)"

        $replacementHostIdentity = Wait-ForReplacementHost -PlayerId $playerPid -OldHostId $oldHostId `
            -Player $player -TimeoutSeconds $RecoveryTimeoutSeconds
        $replacementHostId = [int] $replacementHostIdentity.ProcessId
        $relaunchPidMatch = [regex]::Match($relaunched.Marker, '(?:^|\s)pid=(\d+)(?:\s|$)')
        if (-not $relaunchPidMatch.Success) {
            throw "Replacement Host marker does not contain a pid: $($relaunched.Marker)"
        }
        if ([int] $relaunchPidMatch.Groups[1].Value -ne $replacementHostId) {
            throw "Replacement Host marker PID $($relaunchPidMatch.Groups[1].Value) does not match exact child PID $replacementHostId."
        }
        if ($replacementHostId -eq $oldHostId) {
            throw "Replacement Host reused the captured old PID $oldHostId."
        }
        if (-not [ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit($hostHandle, 0)) {
            throw "Replacement Host PID $replacementHostId appeared before suspended Host PID $oldHostId signaled exit."
        }
        $replacementHostHandle = [ThreeUnity.Bridge.Tools.NativeProcess]::OpenExact($replacementHostId)
        Write-Host "THREE_UNITY_FAULT_HARNESS_REPLACEMENT_HOST playerPid=$playerPid oldHostPid=$oldHostId hostPid=$replacementHostId"

        $transportReset = Wait-ForLogEvent -Path $logPath -Description 'logic transport generation reset' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_TRANSPORT_RESET\b.*$' -StartIndex $relaunched.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($transportReset)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($transportReset.Marker)"

        $pageReady = Wait-ForLogEvent -Path $logPath -Description 'replacement WebView page READY' `
            -SuccessPattern '(?m)^.*THREE_UNITY_WEB_BRIDGE_PAGE_READY\b.*$' -StartIndex $transportReset.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($pageReady)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($pageReady.Marker)"

        $secondReady = Wait-ForLogEvent -Path $logPath -Description 'post-timeout logic READY' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_READY\b.*$' -StartIndex $pageReady.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($secondReady)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($secondReady.Marker)"

        $authoritativeTick = Wait-ForLogEvent -Path $logPath -Description 'post-timeout authoritative logic tick' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_TICK\b.*$' -StartIndex $secondReady.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($authoritativeTick)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($authoritativeTick.Marker)"
    }
    elseif ($KillHost) {
        $oldHostId = [int] $hostIdentity.ProcessId
        Write-Host "THREE_UNITY_FAULT_HARNESS_KILL_HOST hostPid=$oldHostId"
        [ThreeUnity.Bridge.Tools.NativeProcess]::Terminate($hostHandle)
        Wait-ForCapturedHostExit -Handle $hostHandle -HostId $oldHostId -PlayerId $playerPid `
            -Player $player -TimeoutSeconds $RecoveryTimeoutSeconds
        Write-Host "THREE_UNITY_FAULT_HARNESS_HOST_EXITED hostPid=$oldHostId"

        $disconnected = Wait-ForLogEvent -Path $logPath -Description 'physical Host disconnect' `
            -SuccessPattern '(?m)^.*THREE_UNITY_WEB_BRIDGE_DISCONNECTED\b.*$' -StartIndex $faultCursor `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($disconnected)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($disconnected.Marker)"

        $scheduled = Wait-ForLogEvent -Path $logPath -Description 'Host relaunch scheduling' `
            -SuccessPattern '(?m)^.*THREE_UNITY_WEB_BRIDGE_RELAUNCH_SCHEDULED\b.*$' -StartIndex $disconnected.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($scheduled)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($scheduled.Marker)"

        $relaunched = Wait-ForLogEvent -Path $logPath -Description 'replacement Host relaunch' `
            -SuccessPattern '(?m)^.*THREE_UNITY_WEB_BRIDGE_RELAUNCHED\b.*$' -StartIndex $scheduled.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($relaunched)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($relaunched.Marker)"

        $replacementHostIdentity = Wait-ForReplacementHost -PlayerId $playerPid -OldHostId $oldHostId `
            -Player $player -TimeoutSeconds $RecoveryTimeoutSeconds
        $replacementHostId = [int] $replacementHostIdentity.ProcessId
        $relaunchPidMatch = [regex]::Match($relaunched.Marker, '(?:^|\s)pid=(\d+)(?:\s|$)')
        if (-not $relaunchPidMatch.Success) {
            throw "Replacement Host marker does not contain a pid: $($relaunched.Marker)"
        }
        if ([int] $relaunchPidMatch.Groups[1].Value -ne $replacementHostId) {
            throw "Replacement Host marker PID $($relaunchPidMatch.Groups[1].Value) does not match exact child PID $replacementHostId."
        }
        if (-not [ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit($hostHandle, 0)) {
            throw "Replacement Host PID $replacementHostId appeared before killed Host PID $oldHostId signaled exit."
        }
        $replacementHostHandle = [ThreeUnity.Bridge.Tools.NativeProcess]::OpenExact($replacementHostId)
        Write-Host "THREE_UNITY_FAULT_HARNESS_REPLACEMENT_HOST playerPid=$playerPid oldHostPid=$oldHostId hostPid=$replacementHostId"

        $transportReset = Wait-ForLogEvent -Path $logPath -Description 'logic transport generation reset' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_TRANSPORT_RESET\b.*$' -StartIndex $relaunched.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($transportReset)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($transportReset.Marker)"

        $secondReady = Wait-ForLogEvent -Path $logPath -Description 'post-relaunch logic READY' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_READY\b.*$' -StartIndex $transportReset.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($secondReady)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($secondReady.Marker)"

        $authoritativeTick = Wait-ForLogEvent -Path $logPath -Description 'post-relaunch authoritative logic tick' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_TICK\b.*$' -StartIndex $secondReady.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($authoritativeTick)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($authoritativeTick.Marker)"
    }
    else {
        $staleDuringSuspend = $null
        try {
            [ThreeUnity.Bridge.Tools.NativeProcess]::Suspend($hostHandle)
            $hostSuspended = $true
            $suspendStarted = [System.Diagnostics.Stopwatch]::StartNew()
            Write-Host "THREE_UNITY_FAULT_HARNESS_SUSPEND hostPid=$($hostIdentity.ProcessId) milliseconds=$SuspendMilliseconds"
            while ($suspendStarted.ElapsedMilliseconds -lt $SuspendMilliseconds) {
                if (Test-ProcessExited $player) {
                    throw "Unity Player exited while its WebHost was suspended (exit code $($player.ExitCode))."
                }
                [void] (Get-AtMostOneExactChildHost -PlayerId $playerPid)
                $content = Read-SharedTextFile $logPath
                if ($null -ne $content -and $content.Length -ge $faultCursor) {
                    $faultLog = $content.Substring($faultCursor)
                    $failure = [regex]::Match($faultLog, $script:FailurePattern)
                    $match = if ($SkipInputStale) {
                        $null
                    }
                    else {
                        [regex]::Match($faultLog, '(?m)^.*THREE_UNITY_INPUT_STALE\b.*$')
                    }
                    if ($failure.Success -and ($null -eq $match -or -not $match.Success -or $failure.Index -lt $match.Index)) {
                        throw "Explicit failure during Host suspension: $($failure.Value.Trim())"
                    }
                    if ($null -ne $match -and $match.Success) {
                        $absoluteIndex = $faultCursor + $match.Index
                        $staleDuringSuspend = [pscustomobject]@{
                            Description = 'input stale safety gate'
                            Index = $absoluteIndex
                            NextIndex = $absoluteIndex + $match.Length
                            Marker = $match.Value.Trim()
                        }
                    }
                }
                $remaining = $SuspendMilliseconds - [int] $suspendStarted.ElapsedMilliseconds
                if ($remaining -gt 0) {
                    Start-Sleep -Milliseconds ([Math]::Min(50, $remaining))
                }
            }
        }
        finally {
            if ($hostSuspended) {
                [ThreeUnity.Bridge.Tools.NativeProcess]::Resume($hostHandle)
                $hostSuspended = $false
                Write-Host "THREE_UNITY_FAULT_HARNESS_RESUME hostPid=$($hostIdentity.ProcessId)"
            }
        }

        if ($SkipInputStale) {
            $restartCursor = $faultCursor
            Write-Host 'THREE_UNITY_FAULT_HARNESS_SKIP_INPUT_STALE command/state profile mode'
        }
        else {
            $stale = if ($null -ne $staleDuringSuspend) {
                $staleDuringSuspend
            }
            else {
                Wait-ForLogEvent -Path $logPath -Description 'input stale safety gate' `
                    -SuccessPattern '(?m)^.*THREE_UNITY_INPUT_STALE\b.*$' -StartIndex $faultCursor `
                    -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
            }
            $events.Add($stale)
            Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($stale.Marker)"
            $restartCursor = $stale.NextIndex
        }

        $restart = Wait-ForLogEvent -Path $logPath -Description 'logic session restart' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_SESSION_RESTART\b.*$' -StartIndex $restartCursor `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($restart)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($restart.Marker)"

        $secondReady = Wait-ForLogEvent -Path $logPath -Description 'post-restart logic READY' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_READY\b.*$' -StartIndex $restart.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($secondReady)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($secondReady.Marker)"

        $authoritativeTick = Wait-ForLogEvent -Path $logPath -Description 'post-restart authoritative logic tick' `
            -SuccessPattern '(?m)^.*THREE_UNITY_LOGIC_TICK\b.*$' -StartIndex $secondReady.NextIndex `
            -TimeoutSeconds $RecoveryTimeoutSeconds -Player $player
        $events.Add($authoritativeTick)
        Write-Host "THREE_UNITY_FAULT_HARNESS_EVENT $($authoritativeTick.Marker)"
    }
}
catch {
    $runFailure = $_
}
finally {
    if ($hostSuspended -and $hostHandle -ne [IntPtr]::Zero) {
        try {
            if (-not [ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit($hostHandle, 0)) {
                [ThreeUnity.Bridge.Tools.NativeProcess]::Resume($hostHandle)
                Write-Host "THREE_UNITY_FAULT_HARNESS_FINALLY_RESUME hostPid=$($hostIdentity.ProcessId)"
            }
            $hostSuspended = $false
        }
        catch {
            if ([ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit($hostHandle, 0)) {
                $hostSuspended = $false
            }
            else {
                $cleanupFailures.Add("Could not resume still-live captured host in finally: $($_.Exception.Message)")
            }
        }
    }
    if ($null -ne $player -and -not (Test-ProcessExited $player)) {
        try {
            $currentHost = Get-AtMostOneExactChildHost -PlayerId $playerPid
            if ($null -ne $currentHost) {
                $currentHostId = [int] $currentHost.ProcessId
                $isInitial = $null -ne $hostIdentity -and $currentHostId -eq [int] $hostIdentity.ProcessId
                $isReplacement = $null -ne $replacementHostIdentity `
                    -and $currentHostId -eq [int] $replacementHostIdentity.ProcessId
                if (-not $isInitial -and -not $isReplacement) {
                    if ($replacementHostHandle -eq [IntPtr]::Zero) {
                        $replacementHostIdentity = $currentHost
                        $replacementHostHandle = [ThreeUnity.Bridge.Tools.NativeProcess]::OpenExact($currentHostId)
                    }
                    $cleanupFailures.Add(
                        "Unexpected Host PID $currentHostId was captured during cleanup.")
                }
            }
        }
        catch {
            $cleanupFailures.Add("Pre-shutdown Host inventory failed: $($_.Exception.Message)")
        }
    }
    if ($null -ne $player) {
        try {
            if (-not (Test-ProcessExited $player)) {
                [void] $player.CloseMainWindow()
                if (-not (Wait-ForPlayerExitWithHostMonitor -Player $player -TimeoutSeconds $ShutdownTimeoutSeconds)) {
                    Write-Warning "Player PID $playerPid did not close in $ShutdownTimeoutSeconds seconds; terminating only that launched process tree."
                    try {
                        $player.Kill($true)
                    }
                    catch [System.Management.Automation.MethodException] {
                        Stop-Process -Id $playerPid -Force
                    }
                    if (-not (Wait-ForPlayerExitWithHostMonitor -Player $player -TimeoutSeconds $ShutdownTimeoutSeconds)) {
                        $cleanupFailures.Add("Launched Player PID $playerPid did not exit after bounded termination.")
                    }
                }
            }
        }
        catch {
            $cleanupFailures.Add("Player cleanup failed: $($_.Exception.Message)")
        }
    }

    $capturedHosts = @()
    if ($null -ne $hostIdentity -and $hostHandle -ne [IntPtr]::Zero) {
        $capturedHosts += [pscustomobject]@{
            Identity = $hostIdentity
            Handle = $hostHandle
            Role = 'initial'
        }
    }
    if ($null -ne $replacementHostIdentity -and $replacementHostHandle -ne [IntPtr]::Zero) {
        $capturedHosts += [pscustomobject]@{
            Identity = $replacementHostIdentity
            Handle = $replacementHostHandle
            Role = 'replacement'
        }
    }
    foreach ($capturedHost in $capturedHosts) {
        try {
            if (-not [ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit(
                $capturedHost.Handle,
                $ShutdownTimeoutSeconds * 1000)) {
                $orphanMessage = "Captured $($capturedHost.Role) ThreeUnityWebHost PID $($capturedHost.Identity.ProcessId) remained alive after Player shutdown."
                [ThreeUnity.Bridge.Tools.NativeProcess]::Terminate($capturedHost.Handle)
                $orphanMessage += ' The retained exact process handle was terminated to avoid leaving an orphan.'
                if (-not [ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit(
                    $capturedHost.Handle,
                    $ShutdownTimeoutSeconds * 1000)) {
                    $orphanMessage += ' It still did not signal exit within the second bounded interval.'
                }
                $cleanupFailures.Add($orphanMessage)
            }
        }
        catch {
            $cleanupFailures.Add("$($capturedHost.Role) Host orphan check failed: $($_.Exception.Message)")
        }
        if (-not [ThreeUnity.Bridge.Tools.NativeProcess]::CloseHandle($capturedHost.Handle)) {
            $cleanupFailures.Add("CloseHandle failed for the captured $($capturedHost.Role) Host handle.")
        }
    }
    $hostHandle = [IntPtr]::Zero
    $replacementHostHandle = [IntPtr]::Zero

    if ($playerPid -gt 0) {
        try {
            $remainingHost = Get-AtMostOneExactChildHost -PlayerId $playerPid
            if ($null -ne $remainingHost) {
                $remainingHandle = [ThreeUnity.Bridge.Tools.NativeProcess]::OpenExact([int] $remainingHost.ProcessId)
                try {
                    [ThreeUnity.Bridge.Tools.NativeProcess]::Terminate($remainingHandle)
                    [void] [ThreeUnity.Bridge.Tools.NativeProcess]::WaitForExit(
                        $remainingHandle,
                        $ShutdownTimeoutSeconds * 1000)
                }
                finally {
                    [void] [ThreeUnity.Bridge.Tools.NativeProcess]::CloseHandle($remainingHandle)
                }
                $cleanupFailures.Add(
                    "Uncaptured orphan ThreeUnityWebHost PID $($remainingHost.ProcessId) remained after Player shutdown and was terminated.")
            }
        }
        catch {
            $cleanupFailures.Add("Final Host orphan inventory failed: $($_.Exception.Message)")
        }
    }
    if ($null -ne $player) {
        $player.Dispose()
    }
}

if ($null -ne $runFailure) {
    $detail = if ($cleanupFailures.Count -gt 0) {
        " Cleanup: $($cleanupFailures -join ' ')"
    }
    else {
        ''
    }
    throw [System.InvalidOperationException]::new("Unity logic reconnect fault harness failed: $($runFailure.Exception.Message)$detail", $runFailure.Exception)
}
if ($cleanupFailures.Count -gt 0) {
    throw "Unity logic reconnect passed its markers but cleanup failed: $($cleanupFailures -join ' ')"
}

$replacementHostPid = if ($null -ne $replacementHostIdentity) {
    [int] $replacementHostIdentity.ProcessId
}
else {
    $null
}
Write-Host "THREE_UNITY_FAULT_HARNESS_PASS mode=$modeName player=$playerPath hostPid=$($hostIdentity.ProcessId) replacementHostPid=$replacementHostPid maxConcurrentHosts=$script:MaxConcurrentHostsObserved"
[pscustomobject]@{
    Passed = $true
    Mode = $modeName
    PlayerExe = $playerPath
    PlayerPid = $playerPid
    HostPid = [int] $hostIdentity.ProcessId
    ReplacementHostPid = $replacementHostPid
    LogFile = $logPath
    SuspendMilliseconds = if ($modeName -eq 'SuspendResume') { $SuspendMilliseconds } else { $null }
    HangMilliseconds = if ($HangBeforeConnect) { $HangMilliseconds } else { $null }
    KillHost = [bool] $KillHost
    HangBeforeConnect = [bool] $HangBeforeConnect
    SkipInputStale = [bool] $SkipInputStale
    MaxConcurrentHostsObserved = $script:MaxConcurrentHostsObserved
    Events = @($events)
    OrphanHost = $false
}
