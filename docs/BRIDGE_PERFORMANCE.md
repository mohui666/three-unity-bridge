# Bridge performance and backpressure

The Web Bridge intentionally keeps rendering and DOM UI in the original web build while moving selected simulation responsibilities into Unity. That boundary is only useful when synchronization cannot stall the Unity main thread or accumulate obsolete states.

## Outbound policy

`ThreeUnityWebBridgeLauncher` exposes two delivery classes:

- `SendToWeb(message)` is for handshakes, ready/fallback notifications, commands, and other reliable control traffic. It uses a FIFO queue capped at 1,024 pending messages. A full queue is retryable backpressure, reported by the exponentially rate-limited `THREE_UNITY_WEB_BRIDGE_RELIABLE_BACKPRESSURE`; it does not become an actual drop unless an accepted message remains pending when its physical connection retires.
- `SendLatestToWeb(stream, message)` is for replaceable realtime snapshots. Each stream key retains at most one pending value; replacing it increments `coalesced`.

Both are drained by `ThreeUnityWebBridgeWriter`, never by Unity's `Update` or `FixedUpdate` thread. `ThreeUnityLogicBridge` automatically routes protocol types ending in `.state` through latest-value delivery. Entries carry a lightweight logic-session owner token. A successful `bridge.restart` atomically purges both reliable and latest entries belonging to the retired owner before the new owner can enqueue readiness; the externally retained lease does not keep the retired connection alive. After 32 consecutive reliable dequeues, one pending latest stream is forced through, bounding realtime starvation without reordering reliable traffic.

Built-in modules enqueue `ThreeUnityLogicOutgoingMessage`, which carries the serialized JSON together with the `type` and `sessionId` already known by its producer. `ThreeUnityLogicBridge` therefore classifies reliable/latest delivery and constructs the stream key without reparsing its own output on the Unity main thread. The metadata interface is optional: third-party modules that only implement the original string dequeue contract retain compatibility through one header-parse fallback.

One `FlushOutgoing` invocation accepts at most 256 messages into the transport. The module queue and any retry head retain the remaining FIFO work for later Unity callbacks. This bounds burst work independently of the 1,024-message transport capacity without dropping or reordering reliable traffic; `flushBudgetStops` records invocations that consumed the full budget and `maxFlush` records the largest successful batch.

## WebView host pumps

The helper process keeps named-pipe I/O away from the WinForms/WebView2 UI thread:

- Web→Unity messages enter a bounded `Channel<string>` and one reader task writes them to the pipe in arrival order. This removes overlapping `async void` writes to a shared `StreamWriter`.
- Unity→Web messages are read off the UI context, queued, and posted to WebView2 in batches of at most 64 per UI dispatch. A burst schedules one drain chain rather than one `BeginInvoke` per message.
- Each host-side direction is capped at 1,024 pending messages. Overflow disconnects the bridge rather than silently losing a control message.
- Successful navigation emits page-ready even for an unchanged packaging-only site, but outbound dispatch stays closed until the current document sends the reserved listener-ready ACK after installing its WebView listener.
- `ContentLoading.NavigationId` defines the document epoch. Redirected/replaced navigation completions cannot reuse an earlier document's ACK, a hard reload retires the physical Host, and same-document hash/history navigation remains in the current generation.
- A dedicated background watcher checks the Unity parent PID every 250ms. It does not depend on the embedded child window's WinForms timer/message pump, so an abruptly terminated Player cannot leave an orphan helper.
- Unity assigns the Host to a kill-on-close Windows Job before permitting WebView initialization. Relaunch waits for the authoritative Job active-process count to reach zero. If `TerminateJobObject` fails while a child remains, termination is retried every 250ms with exponentially bounded error logs; killing only the root Host is never treated as proof that its WebView2 children drained.

The lifecycle acceptance run observed the host exit 140ms after its Player parent. The normal message-pump run remained connected for more than 1,320 fixed ticks with zero Unity-side backlog or fallback.

## Session isolation and authority reacquisition

Every SDK-managed connection carries a compact random `sessionId` on every control, input, collision, and state envelope. Sequence numbers are scoped to one sender and one session. Unity binds a fresh logic-module instance to the first hello; a `bridge.restart` must name the currently active session as `previousSessionId` before Unity atomically retires the old module and its queued outbound owner. Both sides reject foreign-session traffic before it can affect sequence tracking, watchdog timestamps, handlers, simulation state, or the next session's queue capacity.

Fallback is sticky within a session. In particular, the transport can legally deliver a reliable `bridge.fallback` before an older coalesced `*.state`; that tail state is counted and discarded rather than reactivating Unity authority. An adapter enables `ReconnectBackoff` only after `ready.features` advertises `session-restart-v1`; otherwise it remains on the original JavaScript simulation without issuing futile restart requests. When negotiated, retries run after 250, 500, 1,000, 2,000, and 4,000ms. The new module is bootstrapped from the current JavaScript snapshot, and authority changes only after the first valid new-session state. Profile/capability mismatches, unavailable transport, and invalid state payloads are terminal instead of entering a retry loop.

Compatibility is intentionally conservative. The new Unity package continues to accept legacy sessionless clients, but those connections do not gain session restart. In the opposite direction, a session-scoped new SDK connected to an old Unity package rejects sessionless replies, never becomes authoritative, and safely times out to JavaScript. Deploy the SDK and Unity package together when `session-restart-v1` is required.

This recovery path covers a live WebView/pipe whose logic session stalled. A permanently disconnected pipe still degrades to JavaScript authority after bounded retries; restarting a crashed WebView host is a separate transport-lifecycle operation because it also recreates the page and its game state.

## Player runtime lifecycle

`runtime-lifecycle-v1` is an optional, session-scoped optimization. The browser advertises it in `bridge.hello`; Unity advertises it back in `bridge.ready.features`. Only then does the router send `runtime.lifecycle.state { focused, paused, active, revision }`, where `active = focused && !paused`. Ready is a hard ordering barrier, rapid focus/pause changes replace the pending state, and a session or physical-page replacement discards the retired state before snapshotting the current Player state into the new session.

The browser validates the invariant and monotonic revision, applies it through `RuntimeLifecycleGate`, and returns reliable `runtime.lifecycle.ack { revision, active }`. Unity tracks up to 64 in-flight revisions so valid acknowledgements remain valid when more than one lifecycle state is already in transit; duplicates, malformed payloads, and wrong active values are counted without forcing the game module into fallback. `lifecycleChanges`, `lifecycleEmitted`, `lifecycleCoalesced`, `lifecycleAck`, `lifecycleAckRejected`, and `lifecycleActive` are included in `THREE_UNITY_BRIDGE_PERF`.

Compatibility defaults to the original browser behavior. A missing capability, invalid/stale state, callback exception, fallback, or disposed/restarted session leaves the gate active. The name-to-shop integration keeps the clock current but skips tween, engine update, and render work while inactive, suspends an existing Web Audio context, and resumes without a catch-up burst. A deterministic 10,100-frame test rendered only the 100 active frames, skipped 10,000 suspended frames, and reported `resumeCatchup=0`.

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
THREE_UNITY_BRIDGE_PERF profile=shop-flight-v1 writer=background rx=9 tx=18 rxChars=1313 txChars=4167 coalesced=3 backpressure=0 dropped=0 ownerPurged=0 fairnessYields=0 inPending=0 outPending=0 maxIn=4 maxOut=2 stateEmitted=17 stateSuppressed=144 heartbeats=15 sessionRestarts=1 metadataFast=21 metadataFallback=0 flushBudgetStops=0 maxFlush=2 lifecycleChanges=2 lifecycleEmitted=2 lifecycleCoalesced=0 lifecycleAck=2 lifecycleAckRejected=0 lifecycleActive=1
```

Interpretation:

- `rx` / `tx`: complete Web→Unity / Unity→Web envelopes.
- `rxChars` / `txChars`: UTF-16 character counts including the line delimiter; these are traffic trend counters, not encoded byte counts.
- `coalesced`: pending latest states replaced before pipe transmission.
- `backpressure`: rejected reliable enqueue attempts that remain retryable.
- `dropped`: accepted reliable messages that became undeliverable when a physical connection retired. Acceptance requires zero.
- `ownerPurged`: queued messages intentionally removed at a logical-session boundary.
- `fairnessYields`: reliable bursts that yielded one dequeue slot to a pending latest stream.
- `inPending` / `outPending`: current queues at sample time.
- `maxIn` / `maxOut`: lifetime high-water marks.
- `stateEmitted` / `stateSuppressed` / `heartbeats`: module-level state decisions before the transport queue.
- `collisionFull` / `collisionDelta` / `collisionCells` / `collisionResync`: full windows, accepted v2 deltas, changed cells carried by those deltas, and recovery requests.
- `sessionRestarts` / `sessionRejected` / `sequenceRejected`: accepted module generations, foreign/closed-session traffic, and stale same-session input rejected by Unity.
- `metadataFast` / `metadataFallback`: outgoing envelopes classified from producer-supplied metadata versus legacy module envelopes that required one JSON header parse.
- `flushBudgetStops` / `maxFlush`: flush invocations that consumed the 256-message work budget and the largest successful per-invocation batch.
- `lifecycleChanges` / `lifecycleEmitted` / `lifecycleCoalesced`: Player focus/pause changes, states handed to transport, and pending states replaced before handoff.
- `lifecycleAck` / `lifecycleAckRejected` / `lifecycleActive`: validated browser confirmations, rejected confirmations, and the current Player active bit.
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

`ThreeUnityLogicSessionRouterTests.OutgoingMetadataClassificationBenchmarkAvoidsHeaderReparse` classifies the same serialized state 25,000 times through both paths. On the current machine, header parsing used 523,127 `Stopwatch` ticks while the producer metadata path used 6,124 ticks (85.4x fewer elapsed ticks) and avoided all 25,000 header parses. The assertion also checks that both paths classify the same session/type data; the timing is a local comparative benchmark, not a cross-machine absolute promise.

`ThreeUnityLogicBridgeSessionTests.BurstFlushStopsAtBudgetAndPreservesRemainingOrder` queues 4,096 reliable messages. The first invocation accepts exactly 256 and retains 3,840; a second invocation continues at message 256 with no gap or reordering. Its deterministic marker is `THREE_UNITY_OUTBOUND_FLUSH_BENCHMARK queued=4096 firstFlush=256 remainingAfterFirst=3840 budgetStops=2`.

Current source validation passed 75/75 root TypeScript tests, 28/28 WebView Host .NET tests, 14/14 LittleCubes adapter tests, 36/36 name-to-shop tests, and 107/107 Unity EditMode tests. The Unity XML is `unity-winding-verify-20260830/runtime-lifecycle-v2-editmode.xml` (89,476 bytes, SHA-256 `94765093F01404565A13B438729AB68DEEC34BFDD45E6F24D7FA16CE0460B6CF`).

For a real game, build with `build-web-unity`, start the Player with `-logFile`, wait for at least `ticks=240`, and compare the latest `THREE_UNITY_BRIDGE_PERF` marker. A valid built-in profile run has `writer=background`, `dropped=0`, `metadataFast>0`, `metadataFallback=0`, `maxFlush<=256`, bounded high-water marks, no protocol fallback, and both Player and WebView host still responding. `flushBudgetStops=0` is expected for normal low-volume profiles; a positive value is acceptable under an intentional burst when the backlog drains without drops.

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

The independent, rebuilt `shop-flight-v1` Player run also passed. `unity-winding-verify-20260830/NameToShopLogicBridge-RuntimeLifecycle-v2.log` records:

```text
40: THREE_UNITY_RUNTIME_LIFECYCLE source=focus focused=0 paused=0 active=0 revision=1
42: THREE_UNITY_RUNTIME_LIFECYCLE source=focus focused=1 paused=0 active=1 revision=2
45: THREE_UNITY_LOGIC_READY profile=shop-flight-v1
48: THREE_UNITY_LOGIC_SESSION_RESTART profile=shop-flight-v1 restarts=1 outboundPurged=0
49: THREE_UNITY_LOGIC_READY profile=shop-flight-v1
50: THREE_UNITY_BRIDGE_PERF ... backpressure=0 dropped=0 ... sessionRestarts=1 ... metadataFast=21 metadataFallback=0 flushBudgetStops=0 maxFlush=2 ... lifecycleEmitted=2 lifecycleAck=2 lifecycleAckRejected=0 lifecycleActive=1 ...
51: THREE_UNITY_LOGIC_TICK profile=shop-flight-v1 ticks=120
```

Harness cleanup reported `MaxConcurrentHostsObserved=1`, `OrphanHost=False`, and the post-run exact-process scan found zero Player/Host processes. The log reported `Length=4892` and has SHA-256 `2D48BAF276DB2C3E5159042BF3DD55A82E28038D7A74EB4F0FB4F33B9D7D3BFC`. Together, the two runs validate session reacquisition independently for both built-in profiles; the name-to-shop run additionally proves that both sessions completed the runtime-lifecycle round trip, all current built-in output used producer metadata, none used the legacy parse fallback, and normal traffic stayed far below the flush budget.

### Physical Host, connect, and page-ready recovery

The current `name-to-shop` Player exercises three independent physical failures in addition to the logical restart above:

- abrupt Host termination: disconnect → `JOB_DRAINED(activeProcesses=0)` → relaunch schedule → different Host PID → transport reset → page ready → logic ready → later logic tick;
- pre-connect suspension beyond the 10-second connection deadline: `connect-timeout` followed by the same zero-process/relaunch/readiness recovery;
- a one-shot post-connect delay beyond the 20-second page-ready deadline: no page or logic ready is allowed before `page-ready-timeout`, then the old retained Host handle exits and the Job zero fence precedes relaunch.

All three runs observed `MaxConcurrentHostsObserved=1`, `OrphanHost=False`, zero reliable backpressure/drop markers, no cleanup timeout, and no fatal Host diagnostic. Their logs and SHA-256 values are:

- `NameToShopLogicBridge-HostKill-document-job-v2.log`: `59A4BEF07B9B65ACE80285345F00E024FEC0CD07DA3A341ADCD3F73F546D7B25`
- `NameToShopLogicBridge-ConnectTimeout-document-job-v2.log`: `1592E44CDAE9D9AA8328DE5D172546321FFA8251B929849D41D7D383E2106206`
- `NameToShopLogicBridge-PageReadyTimeout-document-job-v2.log`: `F52A4050C637670572136A817FAFB94D6D769B06F3F515FCAA6F2178404659C8`

The command/state `shop-flight-v1` logical restart uses `-SkipInputStale` because it has no retained movement-input freshness gate. It still requires ready → session restart → new ready → lifecycle ACK → later tick and all process/failure checks. The current log is `NameToShopLogicBridge-RuntimeLifecycle-v2.log`, SHA-256 `2D48BAF276DB2C3E5159042BF3DD55A82E28038D7A74EB4F0FB4F33B9D7D3BFC`.

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
