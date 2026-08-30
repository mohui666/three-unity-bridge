# Unity-Authoritative Logic Bridge Design

## Goal

Upgrade Three Unity Bridge from a packaging-only WebView wrapper into a staged migration bridge. The first retained vertical slice moves LittleCubes player kinematics into Unity while preserving the original Three.js renderer, DOM HUD, menus, block rendering, world generation, mods, and browser-facing behavior.

The result must remain playable when the logic bridge is unavailable. A bridge failure falls back to the original JavaScript player update instead of leaving the game frozen.

## Chosen approach

Use a staged authority bridge:

- Web owns rendering, DOM UI, pointer-lock input capture, chunks, block rendering, block selection, raycasting, and the canonical voxel world during phase one.
- Unity owns player position, velocity, gravity, jump, sprint, fly mode, fluid motion, and voxel collision resolution.
- Web mirrors a small read-only collision window around the player to Unity and sends input snapshots.
- Unity advances a pure C# voxel motor at a fixed timestep and returns authoritative player states.
- Web applies those states to the existing player/camera and continues rendering normally.

This approach extracts real behavior without recreating LittleCubes visuals or requiring a full JavaScript-to-C# compiler. It also creates the protocol and ownership boundaries needed to migrate block rules, world state, and persistence later.

Rejected alternatives:

1. **RPC-only wrapper:** easy to add, but JavaScript remains authoritative and no meaningful logic is extracted.
2. **Immediate full native conversion:** would require replacing renderer, UI, world streaming, persistence, and mods simultaneously, causing the same fidelity regressions as the earlier static conversion.
3. **Unity `CharacterController` plus generated GameObjects:** would require continuously materializing nearby voxel colliders and would couple the bridge to Unity scene physics. A pure data motor is smaller, deterministic, and reusable outside a rendered Unity scene.

## Components

### Versioned logic protocol

Add a browser-safe SDK exported by the npm package. It owns message envelopes, sequence numbers, transport detection, validation, and dispatch. The existing WebView2/named-pipe transport remains unchanged.

Every message is one JSON object with:

- `protocol`: integer protocol version, initially `1`.
- `type`: stable message type.
- `seq`: monotonically increasing sender sequence.
- `payload`: type-specific data.

Unknown message types are ignored. Invalid versions or payloads produce a diagnostic message and do not enable Unity authority.

Phase-one messages:

- `bridge.hello` — Web advertises the game id and `voxel-player-v1` capability.
- `bridge.ready` — Unity accepts the capability and reports its fixed timestep.
- `player.bootstrap` — Web sends current position, rotation, movement tuning, body dimensions, and fly state.
- `player.input` — Web sends movement axes, predicted yaw/pitch, jump/sprint state, and fly-toggle edge.
- `world.collision` — Web sends the current collision-window revision, dimensions, and base64 bitsets for solid and fluid cells.
- `world.invalidate` — Web reports that a nearby block mutation requires a new collision snapshot.
- `player.state` — Unity returns authoritative position, velocity, movement flags, fixed tick, and acknowledged input sequence.
- `bridge.fallback` — either side explicitly returns control to the JavaScript motor.

### Web logic client

The generic client is transport-agnostic and accepts an injectable transport for tests. In WebView2 it uses `window.chrome.webview.postMessage` and the `message` event. Outside WebView2 it reports unavailable without throwing.

The client coalesces high-frequency input so only the latest unsent snapshot matters. It rejects out-of-order Unity states and exposes connection/authority/watchdog events to a game adapter.

If no `bridge.ready` arrives within two seconds, or no `player.state` arrives for 500 milliseconds after activation, the client disables external authority and the original JavaScript update resumes.

### LittleCubes adapter

Add one thin game-specific adapter, called immediately after each `Game` instance is created. This is the only LittleCubes-specific mapping layer.

The adapter:

1. Keeps the original `Player.update` method as the fallback.
2. Sends `player.bootstrap` and an initial collision window after the handshake.
3. Captures LittleCubes input through its existing `InputManager`.
4. Predicts camera yaw/pitch locally for pointer responsiveness while sending the canonical orientation input to Unity.
5. Stops running JavaScript movement/physics only after Unity returns the first valid state.
6. Applies authoritative position, velocity, fly/ground/fluid flags to the existing player, then runs its camera and raycast updates.
7. Sends a new collision snapshot when the player crosses a voxel boundary or a block is placed/broken nearby.
8. Restores the original update immediately on timeout, protocol error, or explicit fallback.

Existing LittleCubes save/export continues to work because authoritative state is applied back to the existing `Player` before serialization.

### Collision window

The phase-one window is an 11×9×11 volume centered on the latest integer player cell: five blocks on each horizontal side, three below the eye cell, and five above it. Two bitsets describe solid and fluid cells. Bit ordering is X-fastest, then Z, then Y, and is fixed by the protocol tests.

Snapshots carry an integer revision. Unity replaces only with a newer revision. Unknown cells are treated as blocking while walking and empty while flying; this prevents walking through an unloaded edge without trapping fly mode. The Web adapter refreshes the window before ordinary motion reaches its edge.

Protocol coordinates stay in native Three.js/LittleCubes coordinates. No Z inversion occurs in this pure-data phase. A future native-rendering consumer must perform coordinate conversion at its own boundary.

### Unity logic runtime

Add `ThreeUnityLogicBridge` beside `ThreeUnityWebBridgeLauncher` in the generated scene. It drains Web messages on the Unity main thread, retains only the latest input, owns the collision window, advances `VoxelPlayerMotor` in `FixedUpdate`, and sends `player.state` after each authoritative tick.

The runtime dispatches profiles through a small module registry rather than branching on game names. Each module declares a profile id, accepts validated envelopes, advances on Unity's fixed tick, and emits state envelopes. `voxel-player-v1` is the first production module.

`VoxelPlayerMotor` is a pure C# class with no `MonoBehaviour`, `Transform`, or Unity physics dependency. It contains the migrated LittleCubes behavior:

- horizontal movement relative to yaw;
- sprint speed selection;
- gravity and terminal velocity;
- grounded jump and water jump;
- fly movement and fly toggle;
- body-width, feet, head, wall, floor, and ceiling voxel collision;
- solid/fluid queries through a small interface.

The scene builder enables this component only when the CLI receives `--logic-profile voxel-player-v1`. Ordinary `build-web-unity` remains packaging-only and backward compatible.

## Data flow

1. Web game starts using its original JavaScript motor.
2. Web client sends `bridge.hello`.
3. Unity replies `bridge.ready` when the requested profile is installed.
4. Web sends bootstrap state and collision window.
5. Unity initializes the motor and returns its first state.
6. LittleCubes switches to external authority.
7. Each Web frame sends the latest input; each Unity fixed tick advances the motor and sends state.
8. Web applies state, updates camera/raycast/HUD, and renders the original scene.
9. Block changes or player cell changes refresh the collision window.
10. A watchdog failure restores the saved original `Player.update` method.

## Backpressure and error handling

- Input and player-state streams are latest-value streams; stale sequence numbers are discarded.
- Collision snapshots are revisioned and bounded to the fixed window size.
- A single malformed message cannot terminate the pipe thread or Unity update loop.
- Protocol activation is atomic: Web continues local simulation until bootstrap, collision, and first Unity state all succeed.
- Fallback is visible in both browser console and Unity log with a reason.
- Unity logs `THREE_UNITY_LOGIC_READY`, periodic authoritative tick counts, and `THREE_UNITY_LOGIC_FALLBACK` for automated smoke evidence.

## CLI and packaging

Extend the existing command:

```powershell
npx three-unity build-web-unity .\dist C:\Path\To\UnityProject `
  --name LittleCubesLogic `
  --logic-profile voxel-player-v1
```

The profile argument is forwarded to the Unity batch builder. The npm package includes protocol code, Unity runtime code, and tests but still excludes generated `bin` and `obj` directories.

## Cross-project validation

After LittleCubes passes, validate reuse against `Marshall-Jimmy/name-to-shop` pinned at commit `4006af40121a2a4ad2fcc309f2de9bf3e30b410f`.

First build and run the repository's unmodified Vite `dist` through packaging-only Web Bridge to establish visual, WebGPU/WebGL2 fallback, HUD, input, screenshot, and GLB-export baselines. Then add a second thin adapter and Unity module named `shop-flight-v1` using the same protocol SDK, transport, scene launcher, and module registry.

`shop-flight-v1` moves only the shop-flight simulation into Unity: flying command state, flight time, amplitude ramp, root position, and root rotation. The Web adapter keeps deterministic name/DNA generation, the 298 component generators, rendering, HUD, audio, camera following, interior mode, exports, and sharing. Unity returns a `flight.state` envelope each fixed tick; Web applies the authoritative shop-root transform and moves its camera target by the resulting delta.

This validation must not add game-name branches to the host, launcher, protocol client, CLI, or scene builder. All name-to-shop-specific code stays in its adapter and `shop-flight-v1` module.

## Testing

Implementation follows red-green-refactor.

Node tests cover:

- protocol envelope validation and unsupported versions;
- deterministic collision-bitset encoding and indexing;
- sequence rejection and latest-value coalescing;
- unavailable WebView transport behavior;
- watchdog fallback;
- LittleCubes adapter fallback before authority and state application after activation.

Unity EditMode tests cover the pure motor:

- walking direction and sprint speed;
- gravity and floor collision;
- jump gating;
- wall and ceiling resolution;
- fly-mode movement;
- fluid gravity and jump;
- collision-window revision and lookup.

Integration acceptance requires:

- existing npm tests remain green;
- Web host and Unity scripts compile with zero errors;
- `build-web-unity --logic-profile voxel-player-v1` produces a Windows Player;
- original HUD, rendering, menus, block interactions, and save/export remain usable;
- Unity logs active authoritative ticks while the user can move, jump, sprint, and fly;
- disabling or timing out the logic component demonstrably returns to JavaScript movement.
- packaging the pinned name-to-shop baseline without visual or HUD loss;
- running `shop-flight-v1` with Unity logs showing authoritative flight ticks while takeoff, orbit, camera follow, and landing remain usable;
- disabling `shop-flight-v1` and observing the original JavaScript flight fallback.

## Phase-one boundary

This phase does not move chunk generation, mesh generation, block placement rules, inventory, DOM UI, mods, deterministic shop generation, or persistence into Unity. It establishes the reusable authority protocol, migrates one complete LittleCubes behavior slice, and proves the module boundary with the isolated name-to-shop flight slice. Subsequent profiles can move voxel mutation and save-state authority without replacing the transport or packaging architecture.
