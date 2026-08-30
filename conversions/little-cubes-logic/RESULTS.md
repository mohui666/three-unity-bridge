# LittleCubes Unity-authoritative Bridge result

Test date: 2026-08-30  
Unity: `6000.3.22f1`  
Logic profile: `voxel-player-v1`

## Ownership boundary

Unity owns player movement, sprinting, jumping/flying, gravity, water state, and voxel collision. The original web game keeps Three.js rendering, world generation and edits, UI, pointer/touch input, camera presentation, save data, and its exact JavaScript player update as the automatic fallback.

The web adapter samples an 11×9×11 collision window around the authoritative player. Spatial overlap is reused between adjacent windows, edits explicitly invalidate their affected cells, and the negotiated `collision-delta-v2` format sends only packed changed cells. Older profiles receive complete windows.

## Deterministic performance checks

```text
INPUT_TRANSPORT_BENCHMARK {"fps":240,"durationMs":10000,"sessionIdCharacters":14,"baselineMessages":2400,"optimizedMessages":140,"messageReductionPercent":94.2,"baselineCharacters":438045,"optimizedCharacters":26741,"characterReductionPercent":93.9,"flyToggleDelivered":1,"movementStartsDelivered":1,"movementStopsDelivered":1}
COLLISION_TRANSPORT_BENCHMARK {"steps":1000,"sessionIdCharacters":14,"windowCells":1089,"baselineSampledCells":1089000,"optimizedSampledCells":99990,"reusedCells":989010,"samplingReductionPercent":90.8,"baselineEnvelopeCharacters":563665,"optimizedEnvelopeCharacters":308654,"characterReductionPercent":45.2,"fullMessages":1,"deltaMessages":999,"deltaChangedCells":61082}
```

The input gate emits digital start/stop/reverse transitions immediately, caps continuous mouse/stick values at 60 Hz, preserves the one-frame fly-toggle edge while coalescing, and sends unchanged input every 250 ms. The collision benchmark round-trips every optimized update against its full reference volume.

## Automated verification

- Bridge TypeScript tests: 68/68 passed.
- LittleCubes adapter tests: 14/14 passed.
- Focused ESLint for `src/bridge/UnityLogicAdapter.js`: passed. The full LittleCubes lint remains blocked by pre-existing CRLF findings outside the focused Bridge file.
- Unity EditMode tests: 51/51 passed in `unity-winding-verify-20260830/TestResults-session-router.xml`.
- Both session-aware transport benchmarks passed their reduction and round-trip assertions.
- Unity `6000.3.22f1` rebuilt the current `StandaloneWindows64` Player successfully.

These checks cover session isolation, sticky fallback, negotiated retry, current-snapshot bootstrap, stale/foreign/tail-state rejection, invalid-state termination, and the handler-deadline race. The current real suspend/resume/reacquire result is recorded below.

## Session restart compatibility

The adapter retries only after Unity advertises `session-restart-v1` in `ready.features`. During retry the untouched JavaScript update remains authoritative; a fresh Unity module is bootstrapped from the current JavaScript player snapshot, and authority transfers only after a valid state from the new session. Without the advertised feature, it remains in JavaScript fallback and does not send restart requests.

The new Unity package still accepts a legacy client that omits `sessionId`. The inverse pairing is deliberately fail-safe: the session-scoped new SDK rejects replies from an old Unity package, never activates Unity authority, and times out to JavaScript. Upgrade the SDK and Unity package together to enable restart.

## Current session-restart Player evidence

The Unity `6000.3.22f1` Player build passed, then the reconnect harness suspended and resumed its `ThreeUnityWebHost`. The resulting log is:

```text
unity-winding-verify-20260830/LittleCubesLogic-Reconnect-session-v1.log
SHA256 B2237FB6EB4B8B1236A895FBA75B3ABCE565ADF1F4C70673F692C0F5646179FA
```

The decisive markers occurred in this order:

```text
40: THREE_UNITY_LOGIC_READY profile=voxel-player-v1
41: THREE_UNITY_INPUT_STALE profile=voxel-player-v1 action=neutralize
44: THREE_UNITY_LOGIC_SESSION_RESTART profile=voxel-player-v1 restarts=1
45: THREE_UNITY_LOGIC_READY profile=voxel-player-v1
47: THREE_UNITY_LOGIC_TICK profile=voxel-player-v1 ticks=120
```

Line 46 reported `sessionRestarts=1`, `dropped=0`, `inputExpired=1`, and `inputRecovered=1`. The run contained no protocol error, reliable overflow, or crash marker. Harness cleanup reported `OrphanHost=False`, so the launched WebView Host did not remain after Player shutdown.

This is real runtime evidence that LittleCubes neutralizes stale controls, falls back while the Host is unavailable, creates exactly one fresh Unity logic session, and resumes Unity simulation only after the new session is ready. The independent name-to-shop `shop-flight-v1` run has also passed and is documented in its own result file.

A later unattended final rebuild stopped at the game's start menu and therefore emitted no Web hello. That timeout is not a Bridge failure—the game never entered the runtime path—and no additional final-runtime claim is derived from that unattended attempt. The ordered reconnect evidence above remains the real `voxel-player-v1` acceptance result.

## Previous Player evidence (before session restart)

This runtime evidence was collected before `session-restart-v1`. It still verifies the transport, input/collision reductions, and fallback behavior of that Player build, but it does not verify reacquisition by the current revision.

The rebuilt Player is:

```text
.\unity-winding-verify-20260830\Build\LittleCubesLogic\LittleCubesLogic.exe
```

It completed the real WebView2 hello/ready/bootstrap exchange, accepted keyboard movement, and continued past 1,680 fixed ticks. Once collision traffic stabilized, Web→Unity receive counts increased by only 9–10 messages per 120 fixed ticks:

```text
THREE_UNITY_LOGIC_TICK profile=voxel-player-v1 ticks=1320
THREE_UNITY_BRIDGE_PERF profile=voxel-player-v1 writer=background rx=571 ... dropped=0 ... collisionFull=1 collisionDelta=112 collisionCells=933 collisionResync=0
THREE_UNITY_LOGIC_TICK profile=voxel-player-v1 ticks=1440
THREE_UNITY_BRIDGE_PERF profile=voxel-player-v1 writer=background rx=581 ... dropped=0 ... collisionFull=1 collisionDelta=112 collisionCells=933 collisionResync=0
THREE_UNITY_LOGIC_TICK profile=voxel-player-v1 ticks=1560
THREE_UNITY_BRIDGE_PERF profile=voxel-player-v1 writer=background rx=590 ... dropped=0 ... collisionFull=1 collisionDelta=112 collisionCells=933 collisionResync=0
```

At Unity's 50 Hz fixed step, this is 3.75–4.17 messages/s instead of the prior idle 432 messages per 120 ticks (180 messages/s), a 97.7–97.9% runtime reduction. The normal acceptance log contains no protocol error, timeout, logic fallback, collision resync, dropped reliable message, or Unity exception.

## Previous input-stall and shutdown recovery

`ThreeUnityInputFreshnessGate` makes the 250ms input heartbeat enforceable. If no sample arrives for 500ms, Unity retains yaw/pitch but clears movement, jump, sprint, and toggle actions until another sample arrives.

In a separate real-Player fault run, `ThreeUnityWebHost` was suspended for 1.2 seconds and then resumed. The Player recorded:

```text
THREE_UNITY_INPUT_STALE profile=voxel-player-v1 action=neutralize
THREE_UNITY_INPUT_RECOVERED profile=voxel-player-v1 seq=578
THREE_UNITY_LOGIC_FALLBACK profile=voxel-player-v1 reason=web-request
THREE_UNITY_BRIDGE_PERF ... dropped=0 ... inputExpired=1 inputRecovered=1 inputNeutralized=46
```

The Web state watchdog intentionally requested fallback after its own 500ms deadline, so the final authority became the untouched JavaScript player update. This verifies a safe sequence under a blocked host: Unity first stops retained input, then both sides converge on local fallback rather than running two motors.

The same run was closed through the Player's normal window path. Before the repair, Unity called bridge cleanup from both `OnApplicationQuit` and `OnDestroy`, producing two closed-pipe `ObjectDisposedException` traces. Cleanup is now interlocked and exception-safe; the rebuilt Player exited with no closed-pipe or shutdown exception, and its WebView child exited with it.
