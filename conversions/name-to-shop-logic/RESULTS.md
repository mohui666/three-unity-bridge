# name-to-shop repair and Unity logic Bridge result

Test date: 2026-08-30  
Upstream: <https://github.com/Marshall-Jimmy/name-to-shop>  
Upstream commit: `4006af40121a2a4ad2fcc309f2de9bf3e30b410f`  
Unity: `6000.3.22f1`  
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

Verification: 35/35 Node tests passed in the current checkout, covering the upstream regressions plus the Bridge adapter/session cases; the geometry suite still includes 500 seeds for every footprint and inset type. The previously recorded Vite production build, desktop/portrait/short-wide physical-typewriter visual checks, 16-character Chinese-name cursor-redraw soak, and 96-shop WebGPU self-check remain the visual/build evidence for the repaired game. Rounded and L-shaped interiors were also opened and visually checked after the geometry repair.

## Bridge ownership

Unity now owns only the flight simulation: target flying state, 2.2 s/2.0 s sine ramps, flight time, root position, and root rotation. Three.js still owns deterministic shop generation, all visual assets/materials, WebGPU/WebGL rendering, DOM HUD, camera controls, audio, screenshots/GLB export, sharing, collection state, and the original JavaScript fallback.

The reusable SDK exports `createShopFlightAuthority`; the name-to-shop adapter only maps the returned pose onto `shopRoot`, camera, and OrbitControls. Messages are protocol-versioned and sequence/generation scoped. Missing transport, handshake, first state, or subsequent state automatically restores JavaScript authority.

Session recovery is capability-negotiated. The adapter starts automatic retry only when `ready.features` contains `session-restart-v1`; during retry, the original JavaScript flight update remains authoritative, and a new Unity module receives the current live shop-flight snapshot before it can take over. If the feature is absent, the adapter remains in safe JavaScript fallback without sending restart requests.

The new Unity package accepts legacy clients that omit `sessionId`, but a session-scoped new SDK deliberately does not accept sessionless replies from an old Unity package: it never becomes authoritative and safely times out to JavaScript. Upgrade the SDK and Unity package together to use restart.

## Current session-restart Player evidence

The current `shop-flight-v1` Player reconnect run passed. Its log is `unity-winding-verify-20260830/NameToShopLogicBridge-Reconnect-session-v1-final.log` (`Length=3371`, SHA-256 `6EE40F7068783F019C76DB1C33042F4AE036C536070BDA538328CCA1C2247483`). The decisive markers occurred in this order:

```text
39: THREE_UNITY_LOGIC_READY profile=shop-flight-v1
41: THREE_UNITY_LOGIC_FALLBACK profile=shop-flight-v1 reason=web-request
42: THREE_UNITY_LOGIC_SESSION_RESTART profile=shop-flight-v1 restarts=1
43: THREE_UNITY_LOGIC_READY profile=shop-flight-v1
44: THREE_UNITY_BRIDGE_PERF ... dropped=0 ... sessionRestarts=1 sessionRejected=0 sequenceRejected=0 ...
45: THREE_UNITY_LOGIC_TICK profile=shop-flight-v1 ticks=120
```

Harness cleanup reported `OrphanHost=False` and `RemainingProcesses=0`. This verifies that the flight adapter safely entered JavaScript fallback, created exactly one fresh Unity module, rejected no valid session or sequence traffic, and resumed ticking only after the new session became ready.

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

The transient `outPending=1` is the newest state waiting for the background writer; the queue never exceeded one item and no reliable message was dropped. Current automated validation passed 51/51 Unity EditMode tests, 68/68 root TypeScript tests, and 35/35 name-to-shop tests. This includes the deterministic 100,000-state flood, reliable queue ordering/capacity, heartbeat scheduling, idle suppression, session routing, negotiated retry, generation/tick fencing, invalid-state termination, and the existing motor/protocol regressions. The current real session-restart Player evidence is recorded above.

The WebView Host was then upgraded from overlapping `async void` pipe writes and one `BeginInvoke` per Unity message to two bounded single-consumer pumps. Web→Unity writes are serialized in arrival order; Unity→Web messages are dispatched to WebView2 in batches of up to 64 without allowing more than 1,024 pending messages. The rebuilt Player completed its bidirectional hello/ready/bootstrap exchange and remained healthy for more than 1,320 fixed ticks. A separate abrupt-parent-exit test terminated Player PID 44212 and observed child Host PID 38956 exit 140ms later, confirming that the 250ms parent watcher prevents orphaned WebView processes.
