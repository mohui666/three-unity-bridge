# Unity-Authoritative Logic Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move LittleCubes player simulation and name-to-shop flight simulation into testable Unity-authoritative modules while retaining each game's original Three.js renderer and DOM UI.

**Architecture:** A versioned JSON protocol runs over the existing WebView2/named-pipe transport. Thin Web adapters capture input or high-level commands and mirror only the data each Unity module needs; pure C# motors advance at Unity fixed timestep and send authoritative states back, with watchdog-driven fallback to original JavaScript behavior.

**Tech Stack:** TypeScript 5.9, Node.js 20 test runner, JavaScript/Vite, Unity 6.3, C#, Unity Test Framework 1.6.0, WebView2, JSON-lines named pipes.

**Spec:** `docs/superpowers/specs/2026-08-30-unity-authoritative-logic-bridge-design.md`

## Global Constraints

- Keep ordinary `build-web-unity` packaging-only and backward compatible.
- Enable logic only with `--logic-profile voxel-player-v1` or `--logic-profile shop-flight-v1`.
- Protocol coordinates remain in source-Web coordinates; do not invert Z in logic modules.
- Web retains rendering and UI; Unity owns only the behavior named by the active profile.
- Never disable JavaScript authority until the first valid Unity state arrives.
- Fall back to JavaScript after 500 ms without authoritative state or two seconds without readiness.
- Reject stale input/state sequence numbers and stale collision revisions.
- Do not add game-name branches to the protocol, host, launcher, CLI, or scene builder.
- Preserve the packaging-only LittleCubes build as a regression baseline.
- Pin name-to-shop validation to commit `4006af40121a2a4ad2fcc309f2de9bf3e30b410f`.
- Do not commit or push; this repository has no existing commits and the user did not request Git history changes.

---

## File structure

### Generic browser SDK

- `src/logic/protocol.ts` — envelopes, payload types, runtime validation, and sequence helpers.
- `src/logic/collision.ts` — deterministic collision bitset encoding/decoding and cell indexing.
- `src/logic/client.ts` — transport abstraction, WebView2 transport, handshake, latest-value stream, and watchdog.
- `src/logic/index.ts` — public logic SDK exports.
- `tests/logic-protocol.test.ts` — protocol and collision tests.
- `tests/logic-client.test.ts` — transport, ordering, and watchdog tests.

### Unity runtime

- `unity-package/Runtime/Logic/LogicEnvelope.cs` — JSON header and payload DTOs.
- `unity-package/Runtime/Logic/IThreeUnityLogicModule.cs` — profile module contract.
- `unity-package/Runtime/Logic/VoxelCollisionWindow.cs` — revisioned solid/fluid bitset lookup.
- `unity-package/Runtime/Logic/VoxelPlayerMotor.cs` — pure player simulation.
- `unity-package/Runtime/Logic/VoxelPlayerLogicModule.cs` — protocol adapter for the motor.
- `unity-package/Runtime/Logic/ShopFlightMotor.cs` — pure name-to-shop flight simulation.
- `unity-package/Runtime/Logic/ShopFlightLogicModule.cs` — protocol adapter for flight.
- `unity-package/Runtime/ThreeUnityLogicBridge.cs` — module registry, pipe dispatch, fixed ticking, logging, and fallback.
- `unity-package/Tests/Editor/*.cs` — EditMode tests for pure logic.

### CLI and Unity scene wiring

- `src/cli-options.ts` — supported logic-profile parsing independent of CLI side effects.
- `src/cli.ts` — forward selected profile to Unity.
- `unity-package/Editor/ThreeUnityWebBatchBuilder.cs` — attach/configure the logic component.
- `unity-package/Runtime/ThreeUnityWebBridgeLauncher.cs` — expose connection status without changing transport ownership.

### Game adapters

- `conversion-work/little-cubes/src/bridge/UnityLogicAdapter.js` — LittleCubes mapping.
- `conversion-work/little-cubes/test/unity-logic-adapter.test.js` — fake-game adapter tests.
- `conversion-work/name-to-shop/src/bridge/unity-flight-adapter.js` — name-to-shop mapping in the pinned validation checkout.
- `conversion-work/name-to-shop/test/unity-flight-adapter.test.js` — flight adapter tests.
- `examples/logic-adapters/` — retained copies of both thin adapters and integration notes.

---

### Task 1: Versioned protocol and collision bitsets

**Files:**
- Create: `src/logic/protocol.ts`
- Create: `src/logic/collision.ts`
- Create: `src/logic/index.ts`
- Create: `tests/logic-protocol.test.ts`
- Modify: `package.json`

**Interfaces:**
- Produces: `LogicEnvelope<T>`, `parseLogicEnvelope(text)`, `encodeLogicEnvelope(type, seq, payload)`, `CollisionVolume`, `encodeCollisionVolume(volume)`, and `collisionIndex(size, x, y, z)`.
- Bit order: `index = ((y * size.z) + z) * size.x + x`.

- [ ] **Step 1: Write failing protocol and bitset tests**

```ts
test("rejects unsupported protocol versions", () => {
  assert.throws(
    () => parseLogicEnvelope('{"protocol":2,"type":"bridge.hello","seq":1,"payload":{}}'),
    /Unsupported logic protocol 2/,
  );
});

test("encodes collision cells X-fastest then Z then Y", () => {
  const encoded = encodeCollisionVolume({
    revision: 7,
    origin: { x: -1, y: 2, z: 3 },
    size: { x: 2, y: 2, z: 2 },
    solid: [true, false, false, true, false, false, false, true],
    fluid: Array(8).fill(false),
  });
  assert.equal(encoded.solidBits, "iQ==");
  assert.equal(collisionIndex(encoded.size, 1, 1, 1), 7);
});
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `npx tsc -p tsconfig.tests.json && node --test dist-tests/tests/logic-protocol.test.js`

Expected: TypeScript fails because `src/logic/protocol.ts` and `src/logic/collision.ts` do not exist.

- [ ] **Step 3: Implement the minimum protocol API**

Use protocol version `1`; require a non-empty `type`, non-negative integer `seq`, and object `payload`. Pack bits with bit zero as the first cell and return base64 through `Buffer` in Node or `btoa` in browsers without importing Node-only modules into the browser bundle.

```ts
export interface LogicEnvelope<T = unknown> {
  protocol: 1;
  type: string;
  seq: number;
  payload: T;
}

export function parseLogicEnvelope(text: string): LogicEnvelope;
export function encodeLogicEnvelope<T>(type: string, seq: number, payload: T): string;
```

- [ ] **Step 4: Export the SDK entry point**

Add `./logic` to `package.json` exports, pointing types/import to `dist/logic/index.d.ts` and `dist/logic/index.js`.

- [ ] **Step 5: Run focused and full Node tests**

Run: `npm test`

Expected: all existing eight tests plus the new protocol tests pass.

- [ ] **Step 6: Review only owned changes**

Run: `git diff -- src/logic tests/logic-protocol.test.ts package.json`

Expected: protocol/bitset implementation and public export only; no generated `dist` files in Git status.

---

### Task 2: Browser logic client, ordering, and watchdog

**Files:**
- Create: `src/logic/client.ts`
- Create: `tests/logic-client.test.ts`
- Modify: `src/logic/index.ts`

**Interfaces:**
- Consumes: `LogicEnvelope`, `parseLogicEnvelope`, and `encodeLogicEnvelope` from Task 1.
- Produces: `LogicTransport`, `WebViewLogicTransport`, `LogicClient`, `createWebViewLogicClient()`.

```ts
export interface LogicTransport {
  readonly available: boolean;
  send(message: string): void;
  subscribe(handler: (message: string) => void): () => void;
}

export class LogicClient {
  constructor(transport: LogicTransport, now?: () => number);
  start(gameId: string, capabilities: string[]): void;
  send<T>(type: string, payload: T): number;
  sendLatest<T>(stream: string, type: string, payload: T): number;
  flushLatest(): void;
  pollWatchdog(): void;
  on(type: string, handler: (envelope: LogicEnvelope) => void): () => void;
  get ready(): boolean;
  get authorityActive(): boolean;
  activateAuthority(): void;
  fallback(reason: string): void;
}
```

- [ ] **Step 1: Write failing tests with a real fake transport**

Cover unavailable transport, hello emission, stale state rejection, latest input coalescing, two-second ready timeout, and 500 ms active-state timeout. Use an injected numeric clock; do not sleep in tests.

```ts
const transport = new MemoryTransport();
let now = 0;
const client = new LogicClient(transport, () => now);
client.sendLatest("player-input", "player.input", { moveX: 1 });
client.sendLatest("player-input", "player.input", { moveX: -1 });
client.flushLatest();
assert.equal(JSON.parse(transport.sent.at(-1)!).payload.moveX, -1);
```

- [ ] **Step 2: Verify RED**

Run: `npx tsc -p tsconfig.tests.json && node --test dist-tests/tests/logic-client.test.js`

Expected: compilation fails because `LogicClient` is missing.

- [ ] **Step 3: Implement transport and client state machine**

`WebViewLogicTransport.available` is true only when `window.chrome.webview.postMessage` and `addEventListener` exist. `bridge.ready` marks readiness, `player.state`/`flight.state` refresh the active watchdog, and stale per-type `seq` values are ignored.

- [ ] **Step 4: Verify GREEN and full regression**

Run: `npm test`

Expected: all Node tests pass with no timer handles left open.

- [ ] **Step 5: Review the client boundary**

Confirm `client.ts` imports no `three`, no Unity-specific profile classes, and no game names.

---

### Task 3: Pure Unity collision window and voxel motor

**Files:**
- Create: `unity-package/Runtime/Logic/VoxelCollisionWindow.cs`
- Create: `unity-package/Runtime/Logic/VoxelPlayerMotor.cs`
- Create: `unity-package/Tests/Editor/ThreeUnity.Bridge.Tests.asmdef`
- Create: `unity-package/Tests/Editor/VoxelCollisionWindowTests.cs`
- Create: `unity-package/Tests/Editor/VoxelPlayerMotorTests.cs`
- Modify for test project only: `unity-winding-verify-20260830/Packages/manifest.json`

**Interfaces:**
- Produces: `IVoxelCollisionSource`, `VoxelCollisionWindow.Replace(...)`, `VoxelPlayerMotor.Initialize(...)`, `VoxelPlayerMotor.Step(input, deltaTime, collision)`.

```csharp
public interface IVoxelCollisionSource
{
    bool IsSolid(int x, int y, int z, bool flying);
    bool IsFluid(int x, int y, int z);
}

public struct VoxelPlayerInput
{
    public float MoveX, MoveZ, Yaw, Pitch;
    public bool JumpHeld, SprintHeld, FlyToggle;
}
```

- [ ] **Step 1: Add Unity Test Framework 1.6.0 to the verification project**

Add `"com.unity.test-framework": "1.6.0"` to the verification project's `Packages/manifest.json`. Do not add it as a runtime dependency of the distributed bridge package.

- [ ] **Step 2: Write failing collision-window tests**

Assert X-fastest indexing, solid/fluid lookup, stale revision rejection, and unknown-cell behavior for walking versus flying.

- [ ] **Step 3: Run Unity EditMode tests and verify RED**

Run Unity 6.3 in batch mode with `-runTests -testPlatform EditMode -testFilter ThreeUnity.Bridge.Tests.VoxelCollisionWindowTests` and an XML result path under `unity-winding-verify-20260830/TestResults`.

Expected: compile failure because `VoxelCollisionWindow` does not exist.

- [ ] **Step 4: Implement the collision window and verify GREEN**

Decode base64 into byte arrays once during `Replace`; never decode or allocate in lookup methods. Reject dimensions whose bit count exceeds the decoded buffer.

- [ ] **Step 5: Write failing motor tests**

Use a fake collision source with a flat floor and optional wall/fluid cells. Test walking at yaw zero, diagonal normalization, sprint speed, gravity/floor landing, one grounded jump, ceiling collision, wall sliding, fly ascent/descent, and water gravity/jump.

```csharp
motor.Initialize(new VoxelPlayerBootstrap { x = 0.5f, y = 2.62f, z = 0.5f, speed = 5f, sprintSpeed = 8f });
motor.Step(new VoxelPlayerInput { MoveZ = 1f, Yaw = 0f }, 0.02f, floor);
Assert.That(motor.Velocity.z, Is.EqualTo(-5f).Within(0.001f));
```

- [ ] **Step 6: Verify motor tests fail for missing implementation, then implement minimally**

Port the numerical behavior from LittleCubes `Player.js`: body width `0.6`, height `1.8`, eye height `1.62`, gravity `-20`, jump velocity `7`, water gravity multiplier `0.3`, and terminal velocities `-50` walking / `-6..6` in fluid unless bootstrap overrides them.

- [ ] **Step 7: Run all Unity EditMode motor tests**

Expected: all collision and motor tests pass with a completed XML result, not `Total: 0`.

---

### Task 4: Unity module registry, protocol DTOs, and CLI profile wiring

**Files:**
- Create: `unity-package/Runtime/Logic/LogicEnvelope.cs`
- Create: `unity-package/Runtime/Logic/IThreeUnityLogicModule.cs`
- Create: `unity-package/Runtime/Logic/VoxelPlayerLogicModule.cs`
- Create: `unity-package/Runtime/ThreeUnityLogicBridge.cs`
- Create: `unity-package/Tests/Editor/LogicModuleRegistryTests.cs`
- Create: `src/cli-options.ts`
- Create: `tests/cli-options.test.ts`
- Modify: `src/cli.ts`
- Modify: `unity-package/Editor/ThreeUnityWebBatchBuilder.cs`
- Modify: `unity-package/Runtime/ThreeUnityWebBridgeLauncher.cs`

**Interfaces:**
- Produces: `normalizeLogicProfile(value)`, `IThreeUnityLogicModule.Profile`, `ThreeUnityLogicBridge.Configure(profile)`, and a registry supporting `voxel-player-v1` and later `shop-flight-v1`.

- [ ] **Step 1: Write failing CLI-profile tests**

```ts
assert.equal(normalizeLogicProfile(undefined), "");
assert.equal(normalizeLogicProfile("voxel-player-v1"), "voxel-player-v1");
assert.throws(() => normalizeLogicProfile("LittleCubes"), /Unsupported logic profile/);
```

- [ ] **Step 2: Verify RED, implement parsing, and verify GREEN**

Run focused Node tests, then add `--logic-profile` to CLI help and forward `-threeUnityLogicProfile <profile>` only when non-empty.

- [ ] **Step 3: Write failing Unity registry tests**

Assert that an empty profile creates no module, `voxel-player-v1` creates `VoxelPlayerLogicModule`, and a game name is rejected.

- [ ] **Step 4: Implement DTOs and module contract**

Use `JsonUtility` header-first parsing. Modules receive the raw JSON plus parsed header so payload classes remain strongly typed. Malformed messages return a diagnostic without throwing out of `Update`.

```csharp
public interface IThreeUnityLogicModule
{
    string Profile { get; }
    bool IsAuthoritative { get; }
    void Handle(string json, LogicEnvelopeHeader header);
    void FixedTick(float deltaTime);
    bool TryDequeueOutgoing(out string json);
    void ForceFallback(string reason);
}
```

- [ ] **Step 5: Implement `VoxelPlayerLogicModule`**

Require hello, bootstrap, and collision before ticking. Keep only the newest input sequence. Send `bridge.ready` and a `player.state` after each active tick. Log readiness once and tick totals every 120 ticks.

- [ ] **Step 6: Wire scene builder and launcher**

The builder always creates `ThreeUnityWebBridgeLauncher`; it creates `ThreeUnityLogicBridge` only for a non-empty validated profile. `ThreeUnityLogicBridge` reads via `TryReceiveFromWeb` in `Update` and writes via `SendToWeb`.

- [ ] **Step 7: Run Node tests, Unity EditMode tests, and a packaging-only regression build**

Expected: ordinary `build-web-unity` still builds and contains no active logic component; profile build compiles and logs the selected profile.

---

### Task 5: LittleCubes Web adapter with safe fallback

**Files:**
- Create: `conversion-work/little-cubes/src/bridge/UnityLogicAdapter.js`
- Create: `conversion-work/little-cubes/test/unity-logic-adapter.test.js`
- Modify: `conversion-work/little-cubes/src/main.js`
- Modify: `conversion-work/little-cubes/package.json`

**Interfaces:**
- Consumes: `LogicClient` and collision encoding from `three-unity-bridge/logic`.
- Produces: `attachUnityVoxelAuthority(game, options?)` returning `{ dispose(), get authorityActive() }`.

- [ ] **Step 1: Add a nested Node test command and write failing adapter tests**

Use a fake game with a fake player/input/world and a fake client. Assert that the original `player.update` runs before readiness, stops only after the first state, applies newer states, ignores stale states, refreshes collision on voxel-boundary crossing, and restores the original update on fallback.

- [ ] **Step 2: Verify RED**

Run: `npm --prefix conversion-work/little-cubes test`

Expected: module-not-found failure for `UnityLogicAdapter.js`.

- [ ] **Step 3: Implement the adapter without changing renderer or HUD classes**

Capture keys through existing `InputManager`, update predicted yaw/pitch from mouse delta, send one latest input per game update, and generate the 11×9×11 collision volume from `world.isSolid` / `world.isFluid`.

- [ ] **Step 4: Integrate at the one stable creation point**

Immediately after `game = new Game(...)` in `startGame`, dispose the previous adapter and call `attachUnityVoxelAuthority(game)`. Do not expose the entire `game` object globally.

- [ ] **Step 5: Run adapter tests, LittleCubes lint, and Vite build**

Expected: adapter tests pass, lint passes, and the generated page still contains the original HUD/menu markup.

---

### Task 6: LittleCubes authoritative integration acceptance

**Files:**
- Modify generated only: `conversion-work/little-cubes/dist/**`
- Modify generated only: `unity-winding-verify-20260830/Assets/StreamingAssets/**`
- Create generated Player: `unity-winding-verify-20260830/Build/LittleCubesLogic/LittleCubesLogic.exe`
- Create: `conversions/little-cubes-logic/RESULTS.md`

**Interfaces:**
- Consumes: CLI profile wiring, Unity voxel module, and LittleCubes adapter.
- Produces: runtime log evidence and a retained acceptance record.

- [ ] **Step 1: Build LittleCubes and package with authority enabled**

Run `npm --prefix conversion-work/little-cubes run build`, then:

```powershell
node dist/cli.js build-web-unity conversion-work/little-cubes/dist unity-winding-verify-20260830 `
  --name LittleCubesLogic --logic-profile voxel-player-v1 `
  --unity "C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe"
```

- [ ] **Step 2: Launch and collect automated runtime evidence**

Require `THREE_UNITY_LOGIC_READY profile=voxel-player-v1` and at least one periodic authoritative tick log in `Player.log`. Confirm no protocol parse exceptions or watchdog fallback during ordinary movement.

- [ ] **Step 3: Perform Game View acceptance**

Verify original menus/HUD, mouse look, WASD, sprint, jump, fly toggle, water behavior, block place/break, save/export, and continue. Record manual visual/input checks separately from automated logs.

- [ ] **Step 4: Prove fallback**

Build or launch with logic disabled and verify the original JavaScript movement still works. Record the packaging-only baseline separately.

- [ ] **Step 5: Write exact results**

Record commands, Unity version, profile, output paths, log markers, pass/fail counts, manual checks, and known deviations in `conversions/little-cubes-logic/RESULTS.md`.

---

### Task 7: name-to-shop cross-project flight authority

**Files:**
- Clone working validation checkout: `conversion-work/name-to-shop/**`
- Create: `unity-package/Runtime/Logic/ShopFlightMotor.cs`
- Create: `unity-package/Runtime/Logic/ShopFlightLogicModule.cs`
- Create: `unity-package/Tests/Editor/ShopFlightMotorTests.cs`
- Create: `conversion-work/name-to-shop/src/bridge/unity-flight-adapter.js`
- Create: `conversion-work/name-to-shop/test/unity-flight-adapter.test.js`
- Modify: `conversion-work/name-to-shop/src/main.js`
- Create: `examples/logic-adapters/name-to-shop/unity-flight-adapter.js`
- Create: `conversions/name-to-shop-logic/RESULTS.md`

**Interfaces:**
- Consumes: generic `LogicClient`, Unity module registry, and existing transport.
- Produces: `shop-flight-v1`, `flight.bootstrap`, `flight.command`, and `flight.state`.

- [ ] **Step 1: Clone and pin the test repository**

Clone `https://github.com/Marshall-Jimmy/name-to-shop.git` into `conversion-work/name-to-shop`, checkout detached commit `4006af40121a2a4ad2fcc309f2de9bf3e30b410f`, install with `npm ci`, and record the commit before edits.

- [ ] **Step 2: Establish an unmodified baseline**

Run its existing Vite build and self-check page, package the unmodified `dist` with ordinary Web Bridge, and verify typing, deterministic shop generation, HUD, entering/exiting, flying, screenshot, and GLB export.

- [ ] **Step 3: Write failing Unity flight-motor tests**

Test the exact original equations:

```csharp
x = (Mathf.Sin(t * 0.42f) * 9f + Mathf.Sin(t * 0.17f) * 4f) * amplitude;
z = (Mathf.Cos(t * 0.33f) * 8f + Mathf.Cos(t * 0.21f) * 3.5f) * amplitude;
y = (12f + Mathf.Sin(t * 0.5f) * 2.2f) * amplitude;
rotationZ = Mathf.Sin(t * 0.4f) * 0.05f * amplitude;
rotationX = Mathf.Cos(t * 0.3f) * 0.03f * amplitude;
```

Also test 2.2-second takeoff and 2.0-second landing ramps using `0.5 - cos(pi * progress) / 2`.

- [ ] **Step 4: Verify RED, implement `ShopFlightMotor`, and verify GREEN**

The motor owns `flightT`, amplitude, requested flying state, and root transform. The protocol module maps `flight.command` to the motor and emits `flight.state` each fixed tick.

- [ ] **Step 5: Write failing Web adapter tests**

Assert JavaScript formula fallback before authority, takeoff/landing command emission, newer-state application, camera target/camera position delta following, and watchdog restoration.

- [ ] **Step 6: Implement the thin adapter and isolate original fallback**

Move the existing lines that advance `flightT/flyAmp` and root transforms into a named fallback callback. In authoritative mode the adapter applies Unity state; all generator animation, sign flicker, UI, audio, and camera rendering remain in the original tick.

- [ ] **Step 7: Register `shop-flight-v1` without game-name branching**

Add the profile to the generic module registry and `normalizeLogicProfile`; keep all name-to-shop mapping in its Web adapter and flight module.

- [ ] **Step 8: Build, package, and compare**

Build with `--logic-profile shop-flight-v1`. Verify the same name produces the same shop, takeoff/orbit/landing and camera follow remain usable, Unity logs authoritative flight ticks, and disabling the profile restores JavaScript flight.

- [ ] **Step 9: Retain reusable evidence**

Copy the thin adapter into `examples/logic-adapters/name-to-shop`, document its integration points, and write baseline/authority/fallback results plus the pinned upstream commit in `conversions/name-to-shop-logic/RESULTS.md`.

---

### Task 8: Documentation, npm package, and final verification

**Files:**
- Modify: `README.md`
- Modify: `unity-package/CHANGELOG.md`
- Modify: `package.json`
- Create generated: `three-unity-bridge-0.1.0.tgz`
- Create generated: final LittleCubes and name-to-shop ZIP distributions.

**Interfaces:**
- Consumes: both profiles and all acceptance evidence.
- Produces: reusable package and exact user commands.

- [ ] **Step 1: Document authority profiles and adapter contract**

Explain ownership, `--logic-profile`, fallback, build/runtime prerequisites, the protocol sequence, and how a new game supplies a thin Web adapter without modifying the host or CLI.

- [ ] **Step 2: Run the complete automated suite**

Run root Node tests, both game adapter tests, LittleCubes lint/build, name-to-shop build/self-check, WebView host build, Unity EditMode tests, packaging-only build, and both authority-profile builds.

- [ ] **Step 3: Audit npm contents**

Run `npm pack --dry-run --json`; require no `webview-host/bin`, `webview-host/obj`, Unity `Library`, generated game dist, or cloned repository files.

- [ ] **Step 4: Install the tarball and rebuild from the installed package**

Install into an ignored smoke prefix and use the installed CLI to build at least one profile Player. This proves package-relative Unity, host, and SDK paths work.

- [ ] **Step 5: Create and fully read back distribution ZIPs**

Compress each clean Unity build directory, open every ZIP entry, read every byte, and compare total uncompressed bytes with the source directory.

- [ ] **Step 6: Report exact evidence**

Provide artifact paths, sizes, SHA-256 hashes, test counts, Unity log markers, upstream commit, user-visible acceptance, fallback evidence, and any remaining unsupported logic categories.
