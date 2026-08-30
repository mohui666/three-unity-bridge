# name-to-shop repair and Unity logic Bridge result

Test date: 2026-08-30  
Upstream: <https://github.com/Marshall-Jimmy/name-to-shop>  
Upstream commit: `4006af40121a2a4ad2fcc309f2de9bf3e30b410f`  
Unity: `6000.3.22f1`  
Node.js / npm: `v22.19.0` / `10.9.3`  
.NET SDK: `8.0.424`  
Logic profile: `shop-flight-v1`

## Upstream repair

- Fixed narrow-screen HUD overflow, title clipping, toast/title overlap, and missing accessible button names.
- Made specific business terms win over generic substrings (`猫猫咖啡` and `魔法药水铺`).
- Removed the rapid takeoff/landing altitude jump by reversing from the current amplitude.
- Added all 22 missing configured egg-decoration generators and fixed nested model lookup.
- Corrected shared GLTF/PBR/canvas ownership so shop disposal does not destroy cached resources.
- Reused the finite lighting-environment presets so repeated WebGPU shop generation no longer destroys live pipeline resources.
- Replaced the world-axis hero camera with a facade-relative pose, so deep, L-shaped, and angled footprints no longer present a blank side wall instead of the entrance. Angled-door offsets now apply both normal components.
- Rebuilt the rounded footprint in strict counter-clockwise order; the old point sequence self-intersected for every rounded seed. Replaced centroid-pull insets with true inward edge offsets, so concave L-shaped floors, ceilings, and movement boundaries stay inside the building.
- Corrected the side-wall normal sign and moved wallpaper onto the actual inner wall face. Counters, ceiling fixtures, floor furniture, and wall-mounted decor now validate their complete rotated footprint, keep clearance from walls/other objects, and roll back unused hover/animation/resource registrations instead of clipping through geometry.
- Reflowed the 3D typewriter function row from the actual keyboard width; clear, delete, dice, and space keys now remain inside the deck without intersecting each other.
- Completed a physical-typewriter UI pass: stopped cumulative canvas-transform drift, fitted long text by measured pixels, synchronized 3D/DOM input, restored unfocused hardware-keyboard input, removed duplicate key textures and double enter validation, released replaced plate geometry, and added aspect-aware framing with guarded orbit/zoom limits. The DOM input card now stays clear of the physical controls on desktop, portrait, and short-wide windows.

Verification: 36/36 Node tests passed in the current checkout, covering the upstream regressions plus the Bridge adapter/session/lifecycle cases; the geometry suite still includes 500 seeds for every footprint and inset type. The Vite production build also passed in this round. Previously recorded desktop/portrait/short-wide physical-typewriter visual checks, 16-character Chinese-name cursor-redraw soak, and 96-shop WebGPU self-check remain the visual evidence for the repaired game. This lifecycle round deliberately used CLI automation only and adds no new manual visual-acceptance claim.

## Bridge ownership

Unity now owns only the flight simulation: target flying state, 2.2 s/2.0 s sine ramps, flight time, root position, and root rotation. Three.js still owns deterministic shop generation, all visual assets/materials, WebGPU/WebGL rendering, DOM HUD, camera controls, audio, screenshots/GLB export, sharing, collection state, and the original JavaScript fallback.

The reusable SDK exports `createShopFlightAuthority`; the name-to-shop adapter only maps the returned pose onto `shopRoot`, camera, and OrbitControls. Messages are protocol-versioned and sequence/generation scoped. Missing transport, handshake, first state, or subsequent state automatically restores JavaScript authority.

Session recovery is capability-negotiated. The adapter starts automatic retry only when `ready.features` contains `session-restart-v1`; during retry, the original JavaScript flight update remains authoritative, and a new Unity module receives the current live shop-flight snapshot before it can take over. If the feature is absent, the adapter remains in safe JavaScript fallback without sending restart requests.

Player runtime lifecycle is independently capability-negotiated through `runtime-lifecycle-v1`. Unity sends a session-scoped, latest-only focus/pause state after `bridge.ready`, and the browser returns a reliable acknowledgement. name-to-shop keeps its clock current but skips tween, Three.js engine update, and render work while inactive; an existing Web Audio context is suspended and resumed. Unsupported or invalid lifecycle traffic leaves the original browser loop active.

The new Unity package accepts legacy clients that omit `sessionId`, but a session-scoped new SDK deliberately does not accept sessionless replies from an old Unity package: it never becomes authoritative and safely times out to JavaScript. Upgrade the SDK and Unity package together to use restart.

## Current physical lifecycle and document-gate evidence

The current production `dist` was rebuilt, copied unchanged into a Unity `6000.3.22f1` Player, and compared file-by-file against `StreamingAssets/ThreeUnityWeb`: 2,837 source files, 2,837 packaged files, zero missing/extra/mismatched files. The source manifest SHA-256 is `B82ED82E1F0632726C14B306B1760D1D4D0C27DA4A5F4C977D33330560DF3562`. The packaged Host implementation DLL is SHA-256 `3C641951C9A3C54EEF1625FA30506236B5B3E04E1D6294BE90742CB830AFB91C`; the Player's `ThreeUnity.Bridge.Runtime.dll` is `F8345C7B99838AA0C8BE9784B3E00C18CC8068968BEA8A78ECC578052E802C1E`.

The listener gate is now document-scoped: page-ready still supports untouched packaging-only pages, but Unity-to-Web dispatch requires an ACK from the current document after its listener is installed. A redirect/new document resets that latch and stale navigation completion is ignored. `ContentLoading` rather than `NavigationStarting` identifies a new document, so hash/history routing does not cause a false physical restart.

Four current-run fault tests passed:

| Mode | Required recovery | Result | Log SHA-256 |
| --- | --- | --- | --- |
| Host kill | old Host exit → Job zero → different PID → page/logic ready → tick | `MaxConcurrentHostsObserved=1`, `OrphanHost=False` | `59A4BEF07B9B65ACE80285345F00E024FEC0CD07DA3A341ADCD3F73F546D7B25` |
| Connect timeout | no early connection/readiness → `connect-timeout` → replacement ready/tick | `MaxConcurrentHostsObserved=1`, `OrphanHost=False` | `1592E44CDAE9D9AA8328DE5D172546321FFA8251B929849D41D7D383E2106206` |
| Page-ready timeout | connected but no early page/logic ready → old handle exit → Job zero → replacement ready/tick | `MaxConcurrentHostsObserved=1`, `OrphanHost=False` | `F52A4050C637670572136A817FAFB94D6D769B06F3F515FCAA6F2178404659C8` |
| Logical restart + runtime lifecycle | focus transitions → ready/lifecycle ACK → session restart → new ready/lifecycle ACK → later tick | same Host, `OrphanHost=False`, `metadataFast=21`, `metadataFallback=0`, `flushBudgetStops=0`, `maxFlush=2`, `lifecycleEmitted=2`, `lifecycleAck=2`, `lifecycleAckRejected=0` | `2D48BAF276DB2C3E5159042BF3DD55A82E28038D7A74EB4F0FB4F33B9D7D3BFC` |

`shop-flight-v1` is a command/state profile and has no retained movement input, so its logical test explicitly uses `-SkipInputStale`; all protocol, lifecycle, process, failure-marker, and shutdown checks remain enabled. Every passing physical log has zero reliable backpressure/drop, cleanup-timeout, fatal-diagnostic, and crash markers. The failed-input-stale precondition run is not counted as acceptance evidence.

Current automated gates pass 75/75 root TypeScript tests, 28/28 WebView Host .NET tests, 14/14 LittleCubes adapter tests, 36/36 name-to-shop tests, and 107/107 Unity EditMode tests. The Unity XML (`runtime-lifecycle-v2-editmode.xml`, 89,476 bytes) has SHA-256 `94765093F01404565A13B438729AB68DEEC34BFDD45E6F24D7FA16CE0460B6CF`.

## Current session-restart Player evidence

The current `shop-flight-v1` Player reconnect run passed. Its log is `unity-winding-verify-20260830/NameToShopLogicBridge-RuntimeLifecycle-v2.log` (`Length=4892`, SHA-256 `2D48BAF276DB2C3E5159042BF3DD55A82E28038D7A74EB4F0FB4F33B9D7D3BFC`). The decisive markers occurred in this order:

```text
40: THREE_UNITY_RUNTIME_LIFECYCLE source=focus focused=0 paused=0 active=0 revision=1
42: THREE_UNITY_RUNTIME_LIFECYCLE source=focus focused=1 paused=0 active=1 revision=2
45: THREE_UNITY_LOGIC_READY profile=shop-flight-v1
48: THREE_UNITY_LOGIC_SESSION_RESTART profile=shop-flight-v1 restarts=1 outboundPurged=0
49: THREE_UNITY_LOGIC_READY profile=shop-flight-v1
50: THREE_UNITY_BRIDGE_PERF ... backpressure=0 dropped=0 ... sessionRestarts=1 ... metadataFast=21 metadataFallback=0 flushBudgetStops=0 maxFlush=2 ... lifecycleEmitted=2 lifecycleAck=2 lifecycleAckRejected=0 lifecycleActive=1 ...
51: THREE_UNITY_LOGIC_TICK profile=shop-flight-v1 ticks=120
```

Harness cleanup reported `MaxConcurrentHostsObserved=1`, `OrphanHost=False`, and a post-run exact-process scan found zero Player/Host processes. This verifies that the flight adapter safely entered JavaScript fallback, created exactly one fresh Unity module, completed one lifecycle state/ACK round trip in each logical session, rejected no valid lifecycle/session/sequence traffic, resumed ticking only after the new session became ready, classified every built-in outgoing envelope without the legacy JSON-header fallback, and stayed below the per-flush work cap during normal gameplay traffic.

## Previous Player evidence (before session restart)

The earlier runtime log below remains evidence for the original flight Bridge, command path, and transport before session restart was added.

Build command:

```powershell
node dist/cli.js build-web-unity conversion-work/name-to-shop/dist unity-winding-verify-20260830 `
  --name NameToShopLogicBridge --logic-profile shop-flight-v1 `
  --unity "C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe" `
  -o unity-winding-verify-20260830/Build/NameToShopLogicBridge/NameToShopLogicBridge.exe
```

The generated Windows Player launched with both `NameToShopLogicBridge` and its child `ThreeUnityWebHost` responding. Its Player log contained:

```text
THREE_UNITY_WEB_BRIDGE_STARTED ... entry=index.html
THREE_UNITY_LOGIC_READY profile=shop-flight-v1
THREE_UNITY_LOGIC_TICK profile=shop-flight-v1 ticks=120
THREE_UNITY_FLIGHT_COMMAND profile=shop-flight-v1 generation=2 flying=True seq=4
THREE_UNITY_FLIGHT_COMMAND profile=shop-flight-v1 generation=2 flying=False seq=5
```

There was no `THREE_UNITY_LOGIC_FALLBACK`, protocol exception, or Unity error in the acceptance interval. The flight command markers prove that real HUD input crossed into the Unity logic module; `LOGIC_READY` occurs only after Web hello, Unity ready, and Web bootstrap.

## Async transport and idle-state benchmark

The reusable transport now writes the named pipe from a background writer thread. Reliable control messages use a bounded queue, while realtime `*.state` streams retain only their newest pending value. A module-level state gate emits every real change immediately but replaces identical 50 Hz snapshots with a 200 ms heartbeat.

Both measurements below used the same production `name-to-shop` dist, Unity project, profile, machine, and 240-fixed-tick acceptance point:

| Measurement | Before idle suppression | After idle suppression | Reduction |
| --- | ---: | ---: | ---: |
| Unity→Web messages | 184 | 20 | 89.1% |
| Unity→Web characters | 40,166 | 4,262 | 89.4% |

The optimized Player reported:

```text
THREE_UNITY_BRIDGE_PERF profile=shop-flight-v1 writer=background rx=3 tx=20 rxChars=343 txChars=4262 coalesced=0 dropped=0 inPending=0 outPending=1 maxIn=1 maxOut=1 stateEmitted=20 stateSuppressed=168 heartbeats=18
THREE_UNITY_LOGIC_TICK profile=shop-flight-v1 ticks=240
```

The transient `outPending=1` is the newest state waiting for the background writer; the queue never exceeded one item and no reliable message was dropped. Current automated validation counts are recorded in the physical-lifecycle section above. Coverage includes the deterministic 100,000-state flood, reliable queue ordering/capacity, heartbeat scheduling, idle suppression, session routing and owner purge, negotiated retry, generation/tick fencing, document-scoped listener readiness, retried Job termination, invalid-state termination, and the existing motor/protocol regressions.

The WebView Host was then upgraded from overlapping `async void` pipe writes and one `BeginInvoke` per Unity message to two bounded single-consumer pumps. Web→Unity writes are serialized in arrival order; Unity→Web messages are dispatched to WebView2 in batches of up to 64 without allowing more than 1,024 pending messages. The rebuilt Player completed its bidirectional hello/ready/bootstrap exchange and remained healthy for more than 1,320 fixed ticks. A separate abrupt-parent-exit test terminated Player PID 44212 and observed child Host PID 38956 exit 140ms later, confirming that the 250ms parent watcher prevents orphaned WebView processes.
