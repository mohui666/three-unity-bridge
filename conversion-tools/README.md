# Conversion tools

## Unity logic reconnect fault harness

`Test-UnityLogicReconnect.ps1` performs bounded, repeatable Windows Player fault tests without UI automation. It launches one Player with a dedicated Unity log and accepts four mutually exclusive fault modes:

- the default suspend/resume mode retains the existing `NtSuspendProcess` / `NtResumeProcess` session-recovery test;
- `-KillHost` terminates one captured Web Host and verifies physical process, transport-generation, and logic recovery;
- `-HangBeforeConnect` suspends the first Host before its pipe connection can complete, forces the launcher's 10-second connect deadline, and verifies timeout-driven physical recovery;
- `-HangAfterConnect` installs a one-shot inherited Host test gate after pipe connection, holds WebView initialization beyond the 20-second page-ready deadline, and verifies timeout-driven physical recovery.

All modes find the single `ThreeUnityWebHost.exe` whose `ParentProcessId` and `--parent-pid` both match that newly launched Player. Every polling interval rechecks this exact relationship and fails if more than one matching Host is alive at once.

For movement profiles with an input-freshness gate, the default test requires this ordered evidence from the same current-run log:

1. initial `THREE_UNITY_LOGIC_READY`;
2. `THREE_UNITY_INPUT_STALE` after the Host is suspended;
3. `THREE_UNITY_LOGIC_SESSION_RESTART` after resume;
4. another `THREE_UNITY_LOGIC_READY`;
5. a subsequent `THREE_UNITY_LOGIC_TICK`.

Command/state profiles such as `shop-flight-v1` do not retain continuous movement input and therefore do not emit `THREE_UNITY_INPUT_STALE`. Pass `-SkipInputStale` explicitly for those profiles. That mode still scans the complete fault interval for failures and requires the ordered chain `READY -> SESSION_RESTART -> READY -> later LOGIC_TICK`; it does not weaken any process-selection, resume, shutdown, or orphan checks.

It treats protocol errors, reliable-queue backpressure/overflow, native crashes, an early Player exit, missing markers, or an orphaned Host as failures. A `try/finally` always resumes a suspended Host. Shutdown first uses `CloseMainWindow`; only the exact Player tree launched by the harness is terminated if the bounded graceful-close interval expires. The captured Host handle stays open through the orphan check, so even cleanup cannot target a reused PID or an unrelated `ThreeUnityWebHost` instance.

### Physical Host kill and relaunch

`-KillHost` waits for the initial `THREE_UNITY_LOGIC_READY`, terminates only the Host selected above through its retained native process handle, and requires the old handle to signal exit before accepting a replacement child. The replacement PID must be different, must still satisfy the parent/command-line/executable checks, and must equal the `pid=` written by the relaunch marker.

The same current-run log must then contain this ordered chain:

1. `THREE_UNITY_WEB_BRIDGE_DISCONNECTED`;
2. `THREE_UNITY_WEB_BRIDGE_JOB_DRAINED activeProcesses=0`;
3. `THREE_UNITY_WEB_BRIDGE_RELAUNCH_SCHEDULED`;
4. `THREE_UNITY_WEB_BRIDGE_RELAUNCHED`;
5. `THREE_UNITY_LOGIC_TRANSPORT_RESET`;
6. `THREE_UNITY_WEB_BRIDGE_PAGE_READY`;
7. a second `THREE_UNITY_LOGIC_READY`;
8. a subsequent `THREE_UNITY_LOGIC_TICK`.

The initial and replacement Host handles are both retained until Player shutdown. The test fails on overlap, a relaunch marker/PID mismatch, a Host cleanup timeout, fatal Host diagnostics, missing recovery markers, or any captured/uncaptured Host left after shutdown. `MaxConcurrentHostsObserved` in the result must therefore be at most `1`, and a passing result reports `OrphanHost = false`.

### Pre-connect hang and timeout recovery

`-HangBeforeConnect` captures the exact first Host as soon as it appears and suspends it through a retained native handle before waiting for any normal ready marker. If `THREE_UNITY_WEB_BRIDGE_CONNECTED`, `THREE_UNITY_WEB_BRIDGE_PAGE_READY`, or `THREE_UNITY_LOGIC_READY` is already present before capture/suspension, the run fails explicitly as a late injection; it cannot pass by testing an already-connected Host.

`HangMilliseconds` defaults to `11500` and has a hard minimum of `10500`, keeping the Host suspended beyond `ThreeUnityWebBridgeLifecycle`'s default 10-second connect deadline. During the hold the harness continuously checks that at most one exact child exists and rejects any replacement observed before the retained old Host handle signals exit. At the end of the hold it resumes the old Host only if that exact handle is still alive; the outer `finally` repeats this still-alive check so an error cannot leave the captured process suspended.

The same current-run log must contain this strict ordered chain:

1. `THREE_UNITY_WEB_BRIDGE_DISCONNECTED reason=connect-timeout`;
2. `THREE_UNITY_WEB_BRIDGE_RELAUNCH_SCHEDULED reason=connect-timeout`;
3. `THREE_UNITY_WEB_BRIDGE_RELAUNCHED` with a new PID;
4. `THREE_UNITY_LOGIC_TRANSPORT_RESET`;
5. `THREE_UNITY_WEB_BRIDGE_PAGE_READY`;
6. `THREE_UNITY_LOGIC_READY`;
7. a subsequent `THREE_UNITY_LOGIC_TICK`.

The old retained handle must signal exit before the harness accepts the replacement process. The replacement must be the sole exact child, have a different PID, and match the `pid=` in the relaunch marker. Shutdown and orphan checks are identical to `-KillHost`.

### Post-connect page-ready timeout recovery

`-HangAfterConnect` avoids a polling race by setting two test-only environment variables on the newly launched Player. The first Host atomically creates a unique temporary one-shot marker and delays WebView initialization for `PageReadyHangMilliseconds` (default `21500`, minimum `20500`). Replacement Hosts inherit the same variables but see the claimed marker and start normally. The harness requires the marker before accepting the injection, and deletes it after bounded Player/Host cleanup.

The first generation must emit `THREE_UNITY_WEB_BRIDGE_CONNECTED` but must not emit page or logic readiness before `THREE_UNITY_WEB_BRIDGE_DISCONNECTED reason=page-ready-timeout`. Its retained Host handle must then signal exit. The log must prove the retired Windows Job reached zero active processes with `THREE_UNITY_WEB_BRIDGE_JOB_DRAINED` before relaunch scheduling, followed by a different replacement PID, transport reset, page ready, logic ready, and a subsequent logic tick. This proves the 20-second second-stage deadline independently of the 10-second pipe-connect deadline.

Run a no-launch safety check first:

```powershell
pwsh -NoProfile -File .\conversion-tools\Test-UnityLogicReconnect.ps1 `
  -PlayerExe .\unity-winding-verify-20260830\Build\LittleCubesLogic\LittleCubesLogic.exe `
  -LogFile .\unity-winding-verify-20260830\LittleCubesLogic-Reconnect.log `
  -DryRun
```

Run the real fault injection after building a Player that includes `session-restart-v1`:

```powershell
pwsh -NoProfile -File .\conversion-tools\Test-UnityLogicReconnect.ps1 `
  -PlayerExe .\unity-winding-verify-20260830\Build\LittleCubesLogic\LittleCubesLogic.exe `
  -LogFile .\unity-winding-verify-20260830\LittleCubesLogic-Reconnect.log `
  -SuspendMilliseconds 1200 `
  -StartupTimeoutSeconds 30
```

Run a command/state profile without an input-stale gate:

```powershell
pwsh -NoProfile -File .\conversion-tools\Test-UnityLogicReconnect.ps1 `
  -PlayerExe .\unity-winding-verify-20260830\Build\NameToShopLogicBridge\NameToShopLogicBridge.exe `
  -LogFile .\unity-winding-verify-20260830\NameToShopLogicBridge-Reconnect.log `
  -SuspendMilliseconds 1200 `
  -StartupTimeoutSeconds 30 `
  -SkipInputStale
```

Run the physical Host crash/relaunch acceptance test (command/state profiles do not need `-SkipInputStale` in this mode):

```powershell
pwsh -NoProfile -File .\conversion-tools\Test-UnityLogicReconnect.ps1 `
  -PlayerExe .\unity-winding-verify-20260830\Build\NameToShopLogicBridge\NameToShopLogicBridge.exe `
  -LogFile .\unity-winding-verify-20260830\NameToShopLogicBridge-HostKill.log `
  -KillHost `
  -StartupTimeoutSeconds 30 `
  -RecoveryTimeoutSeconds 30
```

Run the pre-connect timeout/relaunch acceptance test:

```powershell
pwsh -NoProfile -File .\conversion-tools\Test-UnityLogicReconnect.ps1 `
  -PlayerExe .\unity-winding-verify-20260830\Build\NameToShopLogicBridge\NameToShopLogicBridge.exe `
  -LogFile .\unity-winding-verify-20260830\NameToShopLogicBridge-ConnectTimeout.log `
  -HangBeforeConnect `
  -HangMilliseconds 11500 `
  -StartupTimeoutSeconds 30 `
  -RecoveryTimeoutSeconds 30
```

Run the post-connect page-ready timeout/relaunch acceptance test:

```powershell
pwsh -NoProfile -File .\conversion-tools\Test-UnityLogicReconnect.ps1 `
  -PlayerExe .\unity-winding-verify-20260830\Build\NameToShopLogicBridge\NameToShopLogicBridge.exe `
  -LogFile .\unity-winding-verify-20260830\NameToShopLogicBridge-PageReadyTimeout.log `
  -HangAfterConnect `
  -PageReadyHangMilliseconds 21500 `
  -StartupTimeoutSeconds 30 `
  -RecoveryTimeoutSeconds 30
```

`-KillHost`, `-HangBeforeConnect`, and `-HangAfterConnect` each belong to a separate PowerShell parameter set from the default `-SuspendMilliseconds` / `-SkipInputStale` mode. The shell therefore rejects mixed fault modes before launching anything. `-HangMilliseconds` is valid only with `-HangBeforeConnect`; `-PageReadyHangMilliseconds` is valid only with `-HangAfterConnect`.

Use a distinct log path for each acceptance run. The harness refuses an existing `LogFile` by default so stale markers cannot satisfy the ordered checks; pass `-OverwriteLog` only when replacing that file is intentional. `RecoveryTimeoutSeconds` defaults to 30 seconds; `ShutdownTimeoutSeconds` defaults to 10 seconds. Extra Unity Player arguments can be passed through `PlayerArguments`, but `-logFile` is reserved for the dedicated `LogFile` parameter.
