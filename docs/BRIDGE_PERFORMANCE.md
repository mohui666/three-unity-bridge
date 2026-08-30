# Bridge performance and backpressure

The Web Bridge intentionally keeps rendering and DOM UI in the original web build while moving selected simulation responsibilities into Unity. That boundary is only useful when synchronization cannot stall the Unity main thread or accumulate obsolete states.

## Outbound policy

`ThreeUnityWebBridgeLauncher` exposes two delivery classes:

- `SendToWeb(message)` is for handshakes, ready/fallback notifications, commands, and other reliable control traffic. It uses a FIFO queue capped at 1,024 pending messages. An overflow is visible as `THREE_UNITY_WEB_BRIDGE_RELIABLE_OVERFLOW` and increments `dropped`.
- `SendLatestToWeb(stream, message)` is for replaceable realtime snapshots. Each stream key retains at most one pending value; replacing it increments `coalesced`.

Both are drained by `ThreeUnityWebBridgeWriter`, never by Unity's `Update` or `FixedUpdate` thread. `ThreeUnityLogicBridge` automatically routes protocol types ending in `.state` through latest-value delivery.

## WebView host pumps

The helper process keeps named-pipe I/O away from the WinForms/WebView2 UI thread:

- Web→Unity messages enter a bounded `Channel<string>` and one reader task writes them to the pipe in arrival order. This removes overlapping `async void` writes to a shared `StreamWriter`.
- Unity→Web messages are read off the UI context, queued, and posted to WebView2 in batches of at most 64 per UI dispatch. A burst schedules one drain chain rather than one `BeginInvoke` per message.
- Each host-side direction is capped at 1,024 pending messages. Overflow disconnects the bridge rather than silently losing a control message.
- A dedicated background watcher checks the Unity parent PID every 250ms. It does not depend on the embedded child window's WinForms timer/message pump, so an abruptly terminated Player cannot leave an orphan helper.

The lifecycle acceptance run observed the host exit 140ms after its Player parent. The normal message-pump run remained connected for more than 1,320 fixed ticks with zero Unity-side backlog or fallback.

## Session isolation and authority reacquisition

Every SDK-managed connection carries a compact random `sessionId` on every control, input, collision, and state envelope. Sequence numbers are scoped to one sender and one session. Unity binds a fresh logic-module instance to the first hello; a `bridge.restart` must name the currently active session as `previousSessionId` before Unity atomically retires the old module. Both sides reject foreign-session traffic before it can affect sequence tracking, watchdog timestamps, handlers, or simulation state.

Fallback is sticky within a session. In particular, the transport can legally deliver a reliable `bridge.fallback` before an older coalesced `*.state`; that tail state is counted and discarded rather than reactivating Unity authority. An adapter enables `ReconnectBackoff` only after `ready.features` advertises `session-restart-v1`; otherwise it remains on the original JavaScript simulation without issuing futile restart requests. When negotiated, retries run after 250, 500, 1,000, 2,000, and 4,000ms. The new module is bootstrapped from the current JavaScript snapshot, and authority changes only after the first valid new-session state. Profile/capability mismatches, unavailable transport, and invalid state payloads are terminal instead of entering a retry loop.

Compatibility is intentionally conservative. The new Unity package continues to accept legacy sessionless clients, but those connections do not gain session restart. In the opposite direction, a session-scoped new SDK connected to an old Unity package rejects sessionless replies, never becomes authoritative, and safely times out to JavaScript. Deploy the SDK and Unity package together when `session-restart-v1` is required.

This recovery path covers a live WebView/pipe whose logic session stalled. A permanently disconnected pipe still degrades to JavaScript authority after bounded retries; restarting a crashed WebView host is a separate transport-lifecycle operation because it also recreates the page and its game state.

## State emission policy

`ThreeUnityStateEmissionGate` is reusable by logic profiles. Its default behavior is:

1. Emit the first authoritative state immediately.
2. Emit every semantically changed state immediately.
3. Allow a profile to force an acknowledgement state immediately.
4. Suppress identical fixed-tick states.
5. Emit an unchanged heartbeat every 200ms, safely below the JavaScript client's 500ms state watchdog.

Profiles decide which fields are semantically relevant; sequence/tick counters must not themselves count as a physical change. The gate exposes emitted, suppressed, and heartbeat counters through `IThreeUnityLogicTelemetry`.

## Web input emission policy

`RealtimeInputGate<T>` is the matching Web→Unity policy for replaceable controller input. An adapter supplies semantic equality, optional edge-preserving merge logic, and its definition of an urgent transition. The gate then:

1. Emits the first sample immediately.
2. Lets digital start/stop/reverse transitions bypass the analog rate limit.
3. Coalesces continuously changing mouse, stick, and touch values to a configured maximum rate.
4. Retains one-frame actions through a caller-provided merge function.
5. Suppresses identical render-frame samples and sends a low-rate liveness heartbeat.

The LittleCubes adapter uses a 60 Hz analog ceiling and a 250 ms idle heartbeat. Movement direction changes, jump/sprint transitions, and the fly-toggle edge remain immediate. A deterministic 10-second, 240 FPS mixed-input benchmark, including a 14-character session id on every envelope, reduced 2,400 messages to 140 (94.2%) and 438,045 envelope characters to 26,741 (93.9%) without losing movement start, movement stop, or the single-frame toggle.

`ThreeUnityInputFreshnessGate` consumes that heartbeat on the Unity side. The voxel profile expires retained controls after 500ms, preserves view orientation, and clears movement, jump, sprint, and one-shot actions until a new input arrives. This prevents a blocked WebView pump from making the authoritative motor run forever on an old key-down sample. Metrics expose current freshness, age, expiration/recovery counts, and the number of neutralized fixed ticks.

## Collision window policy

`sampleCollisionVolume` reuses overlapping cells when a voxel window moves and only samples its entering slab plus explicitly invalidated cells. `collision-delta-v2` encodes each changed cell as one varint containing `(gap - 1)` and the two solid/fluid flag bits. Capability negotiation prevents an older Unity profile from decoding the new packed format; clients fall back to full snapshots when v2 is absent.

The deterministic 1,000-step benchmark reduced world samples from 1,089,000 to 99,990 (90.8%) and session-aware protocol characters from 563,665 to 308,654 (45.2%). It used one full snapshot followed by 999 deltas.

## Runtime metrics

Every 120 Unity fixed ticks, the Player writes one marker:

```text
THREE_UNITY_BRIDGE_PERF profile=shop-flight-v1 writer=background rx=3 tx=20 rxChars=343 txChars=4262 coalesced=0 dropped=0 inPending=0 outPending=1 maxIn=1 maxOut=1 stateEmitted=20 stateSuppressed=168 heartbeats=18
```

Interpretation:

- `rx` / `tx`: complete Web→Unity / Unity→Web envelopes.
- `rxChars` / `txChars`: UTF-16 character counts including the line delimiter; these are traffic trend counters, not encoded byte counts.
- `coalesced`: pending latest states replaced before pipe transmission.
- `dropped`: reliable messages rejected by the bounded queue. Acceptance requires zero.
- `inPending` / `outPending`: current queues at sample time.
- `maxIn` / `maxOut`: lifetime high-water marks.
- `stateEmitted` / `stateSuppressed` / `heartbeats`: module-level state decisions before the transport queue.
- `collisionFull` / `collisionDelta` / `collisionCells` / `collisionResync`: full windows, accepted v2 deltas, changed cells carried by those deltas, and recovery requests.
- `sessionRestarts` / `sessionRejected` / `sequenceRejected`: accepted module generations, foreign/closed-session traffic, and stale same-session input rejected by Unity.
- `inputFresh` / `inputAgeMs`: whether the latest replaceable input is inside its deadline and its age at the sample.
- `inputExpired` / `inputRecovered` / `inputNeutralized`: lifetime stale transitions, subsequent samples, and fixed ticks simulated with held actions cleared.

The browser SDK exposes `LogicClient.metrics` for outbound/inbound messages and characters, latest-value coalescing, stale/foreign/terminal/invalid-phase rejection, protocol errors, fallbacks, restarts, and currently pending latest streams. Its `phase` is one of `idle`, `connecting`, `ready`, `active`, `fallback`, or `disposed`.

## Reproducible checks

Run the TypeScript suite and production build:

```powershell
npm test
npm run build
npm run benchmark:input
npm run benchmark:collision
```

After installing the package in a Unity test project, run EditMode tests:

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe" `
  -batchmode -nographics -projectPath C:\Path\To\UnityProject `
  -runTests -testPlatform EditMode `
  -testResults C:\Path\To\results.xml `
  -logFile C:\Path\To\tests.log
```

`ThreeUnityOutboundBufferTests` includes a 100,000-state flood. It must leave exactly one pending message containing the final state. `ThreeUnityStateEmissionGateTests` verifies immediate changes/acknowledgements and deterministic heartbeat suppression.

`input-transport.mjs` asserts at least 90% message and character reduction while verifying critical digital edges. `collision-transport.mjs` asserts at least 90% sampling reduction and 35% envelope-character reduction while round-tripping every optimized window.

Current source validation passed 68/68 root TypeScript tests, 14/14 LittleCubes adapter tests, 35/35 name-to-shop tests, and 51/51 Unity EditMode tests.

For a real game, build with `build-web-unity`, start the Player with `-logFile`, wait for at least `ticks=240`, and compare the latest `THREE_UNITY_BRIDGE_PERF` marker. A valid run has `writer=background`, `dropped=0`, bounded high-water marks, no protocol fallback, and both Player and WebView host still responding.

In the LittleCubes Player, two idle 120-tick windows before input gating each received 432 Web→Unity messages (180/s). With the gate, stable collision counts at ticks 1,320–1,680 showed only 9–10 messages per 120 ticks (3.75–4.17/s), a 97.7–97.9% real-runtime reduction. The same run reached `collisionFull=1 collisionDelta=112 collisionCells=933 collisionResync=0`, with `dropped=0` and no fallback or protocol error.

## Session restart and shutdown injection

The current LittleCubes Player was rebuilt successfully with Unity `6000.3.22f1`. The fault harness suspended its `ThreeUnityWebHost`, resumed it, and observed the required order in `unity-winding-verify-20260830/LittleCubesLogic-Reconnect-session-v1.log`:

```text
40: THREE_UNITY_LOGIC_READY profile=voxel-player-v1
41: THREE_UNITY_INPUT_STALE profile=voxel-player-v1 action=neutralize
44: THREE_UNITY_LOGIC_SESSION_RESTART profile=voxel-player-v1 restarts=1
45: THREE_UNITY_LOGIC_READY profile=voxel-player-v1
47: THREE_UNITY_LOGIC_TICK profile=voxel-player-v1 ticks=120
```

The intervening performance marker reported `sessionRestarts=1`, `dropped=0`, `inputExpired=1`, and `inputRecovered=1`. The complete run contained no protocol error, reliable-overflow marker, or crash, and harness cleanup reported `OrphanHost=False`. The log SHA-256 is `B2237FB6EB4B8B1236A895FBA75B3ABCE565ADF1F4C70673F692C0F5646179FA`.

This verifies real session reacquisition for `voxel-player-v1`: Unity first neutralized stale input, the Web side temporarily restored JavaScript authority, exactly one fresh Unity module was created, and authority resumed only after the new session reached ready.

The independent `shop-flight-v1` Player run also passed. `unity-winding-verify-20260830/NameToShopLogicBridge-Reconnect-session-v1-final.log` records:

```text
39: THREE_UNITY_LOGIC_READY profile=shop-flight-v1
41: THREE_UNITY_LOGIC_FALLBACK profile=shop-flight-v1 reason=web-request
42: THREE_UNITY_LOGIC_SESSION_RESTART profile=shop-flight-v1 restarts=1
43: THREE_UNITY_LOGIC_READY profile=shop-flight-v1
44: THREE_UNITY_BRIDGE_PERF ... dropped=0 ... sessionRestarts=1 sessionRejected=0 sequenceRejected=0 ...
45: THREE_UNITY_LOGIC_TICK profile=shop-flight-v1 ticks=120
```

Harness cleanup reported `OrphanHost=False` and `RemainingProcesses=0`. The log reported `Length=3371` and has SHA-256 `6EE40F7068783F019C76DB1C33042F4AE036C536070BDA538328CCA1C2247483`. Together, the two runs validate session reacquisition independently for both built-in profiles.

### Earlier fallback and shutdown baseline

Before `session-restart-v1`, the same fault shape verified input neutralization, fallback convergence, and shutdown cleanup. That earlier run suspended only its `ThreeUnityWebHost` process for 1.2 seconds while Unity continued fixed updates. Unity logged exactly one `THREE_UNITY_INPUT_STALE`, neutralized 46 ticks, and accepted one recovery sample after the host resumed:

```text
THREE_UNITY_INPUT_STALE profile=voxel-player-v1 action=neutralize
THREE_UNITY_INPUT_RECOVERED profile=voxel-player-v1 seq=578
THREE_UNITY_LOGIC_FALLBACK profile=voxel-player-v1 reason=web-request
THREE_UNITY_BRIDGE_PERF ... dropped=0 ... inputFresh=1 inputExpired=1 inputRecovered=1 inputNeutralized=46
```

The final fallback is intentional: the existing 500ms JavaScript state watchdog had already switched back to the original local player update while the host pump was unavailable. The sequence therefore prevents stale Unity motion first, then converges both sides on JavaScript authority instead of allowing dual simulation.

The same fault run exposed that Unity invokes both `OnApplicationQuit` and `OnDestroy`. Shutdown is now guarded by a one-shot interlock, captures resources before disposal, and tolerates already-closed pipe/writer/process handles. A regression test injects a writer whose underlying stream is already closed and calls shutdown three times. A rebuilt Player then closed through its normal window path with no `ObjectDisposedException` or `Cannot access a closed pipe` marker.
