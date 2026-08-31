# AGENTS.md

## Mission

This repository builds a reusable Three.js-to-Unity bridge. Preserve the original web game's rendering, DOM UI, input, audio, assets, and save behavior while moving only suitable runtime responsibilities to Unity. Changes must improve the generic bridge rather than hard-code one game.

## Repository map

- `src/`: TypeScript exporter, CLI, protocol, browser logic client, transport helpers, and reusable simulation helpers.
- `tests/`: Node test suite for the TypeScript API and protocol.
- `webview-host/`: .NET 8 Windows WebView2 host embedded into the Unity Player window.
- `webview-host-tests/`: .NET tests for host lifecycle and recovery behavior.
- `unity-package/`: Unity UPM package. Runtime, Editor tooling, shaders, samples, and EditMode tests live here.
- `examples/`: minimal exporter and reusable browser-side logic adapters.
- `benchmarks/`: deterministic transport and collision benchmarks.
- `conversion-tools/`: repeatable capture and physical recovery harnesses.
- `docs/`: architecture, protocol, performance, and validation notes.
- `conversion-work/`, `conversions/`, `unity-smoke/`, and `unity-winding-verify*/`: ignored local validation workspaces and generated evidence. They may contain upstream clones, converted samples, reports, or generated Unity projects; never stage them.

## Non-negotiable invariants

1. Web Bridge packaging must preserve the source `dist` bytes and relative paths. Do not replace the original game with a visual approximation.
2. DOM UI, CSS, Three.js/WebGPU/WebGL rendering, input, audio, persistence, and browser fallbacks must keep working unless a documented bridge capability explicitly owns that behavior.
3. Browser/Unity protocol messages are versioned and session-scoped. A restarted session must not receive queued messages from an older session.
4. Reliable messages retain order and are retried without duplication. Realtime state is bounded and coalesced by session plus message type; it must not starve forever behind reliable traffic.
5. Unity's main thread must not block on pipe or WebView I/O.
6. A Host generation may create WebView child processes only after it is assigned to its Windows Job. A replacement Host must not launch until the retired Job reports zero active processes.
7. Unity-to-Web startup messages may be released only after navigation and the browser message listener are both ready.
8. Recovery telemetry must distinguish physical page generation, pipe connection generation, and logical session generation. Do not reset backoff on a partial handshake.
9. Generic Runtime and Host code must not branch on a game name. Game-specific mapping belongs in a reusable profile/module plus a thin adapter.
10. Compile success alone is not acceptance. Tests, a real Player build, runtime logs, asset-fidelity checks, and manual Game View observations are separate evidence.

## Working rules

- Inspect `git status --short --branch` before editing and preserve unrelated user work.
- Use `rg`/`rg --files` for discovery and `apply_patch` for hand edits.
- Do not use Computer Use or GUI automation in this repository. Prefer CLI builds, test XML, process inspection, and runtime logs.
- Do not add or require hashes, checksums, file fingerprints, or duplicate integrity verification. A hash match must never gate startup, import, packaging, or bridge success.
- Do not add defensive branches for hypothetical failures, silent fallbacks, automatic repair, redundant retries, or parallel compatibility paths unless the requested behavior defines them. Follow the declared contract directly and surface a precise error when it is violated.
- Do not edit generated `dist/`, `dist-tests/`, `.NET bin/obj`, Unity `Library/Temp/Logs/Build`, or ignored integration workspaces as if they were source.
- Do not copy source from an ignored upstream clone into the bridge without checking its license and keeping game fixes separated from reusable bridge changes.
- Commit and push task-owned changes automatically after the required validation unless the user asks to leave them uncommitted or unpushed. Do not open a PR or rewrite history unless the user explicitly asks. Stage only paths owned by the current task.
- Never commit credentials, local machine paths, WebView profiles, crash dumps, packaged Players, or dependency directories.
- Keep TypeScript as ESM and compatible with the Node version in `package.json`.
- Keep Runtime C# compatible with the Unity version currently used by the validation project. Avoid Editor-only APIs in `unity-package/Runtime`.
- Add or update tests for every protocol, lifecycle, buffering, importer, or conversion behavior change.

## Required validation

Run focused tests while iterating, then the relevant full gates before handing off:

```powershell
npm test
npm run build
dotnet test .\webview-host-tests\ThreeUnityWebHost.Tests.csproj -c Release
```

For Unity package changes, install/copy `unity-package` into a disposable Unity project and run EditMode tests in batch mode. The authoritative result is the generated XML with a completed, nonzero test count and zero failures; a process exit or `Total: 0` is not a pass.

For Web Bridge lifecycle or packaging changes, also:

1. Build a Windows Player through `three-unity build-web-unity` with the supported Unity editor.
2. Launch the Player and collect its runtime log.
3. Exercise the changed failure path (Host kill, connect timeout, page/listener timeout, logical restart, or shutdown as applicable).
4. Confirm recovery reaches page ready, bridge ready, and a post-recovery logic tick.
5. Confirm there is never more than one active Host generation and no Host/WebView child remains after Player exit.
6. Confirm the packaged web asset set and relative paths through the real Player load path; do not add hash or checksum verification.
7. Report visual/UI/input acceptance separately; automation logs do not prove visual fidelity.

## Performance work

- Add a reproducible benchmark before claiming an optimization.
- Measure message counts, characters, allocations/CPU when available, queue depth, coalescing, backpressure, rejected/dropped messages, and recovery counts.
- Compare the same workload and protocol version before and after the change.
- Favor bounded queues, metadata parsed once, latest-state coalescing, edge-triggered input, and low-frequency heartbeats.
- A faster path that changes gameplay, drops reliable messages, crosses sessions, or removes the original UI is a regression.

## Documentation and evidence

- Update `README.md`, `docs/BRIDGE_PERFORMANCE.md`, `unity-package/CHANGELOG.md`, and other tracked documentation when behavior or verified evidence changes. Keep per-game conversion samples and reports under ignored local paths.
- Record exact commands, Unity/.NET/Node versions, test counts, and important log markers.
- Label manual observations, automated assertions, benchmark results, and unresolved limitations accurately. Do not present an unverified assumption as a completed feature.
