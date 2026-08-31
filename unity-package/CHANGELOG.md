# Changelog

## Unreleased

- Added explicit `ThreeUnityComponentBindings` registration and the hierarchy-scoped `ThreeUnityComponentApplicator`, allowing existing `type + dataJson` descriptors to configure project-owned components without reflection or import-time execution.
- The importer now adds the runtime applicator only to assets that contain component descriptors, and the new Component Binding Door sample demonstrates visible descriptor-driven behavior.
- Added `.threeunity` format v2 for `SkinnedMesh`, stable node-id bone references, four-weight skinning, bind poses, and baked transform animation tracks.
- Added Unity `SkinnedMeshRenderer` import, `AnimationClip` subassets, and `ThreeUnityAnimationPlayer` for controller-free default playback and clip-name playback.
- Added a self-contained animated three-bone sample and focused exporter/importer deformation smoke coverage while retaining v1 static-asset import compatibility.
- Animated bounds now include curve-key deformation samples, and version-2 Unity node names use escaped source names plus stable node ids so every `AnimationClip` path is unambiguous.
- Streamlined the default package regression suite by removing duplicated boundary permutations, test-only benchmarks, obsolete compatibility cases, and synthetic defensive-failure tests while retaining importer, session, queue, lifecycle, profile, and Windows Job contracts.

## 0.1.0 - 2026-08-30

- Added the `.threeunity` scripted importer.
- Added mesh, material, embedded texture, camera, light, and metadata conversion.
- Added Three.js-to-Unity handedness and unit conversion.
- Added detached render-object export support so cameras outside `scene` can be retained.
- Added reusable first-person and orbit showcase controllers for playable conversion smoke tests.
- Fixed triangle winding after the Three.js-to-Unity handedness mirror.
- First-person conversion scenes now capture movement input immediately on Play.
- Preserved Three.js vertex-color materials with a Unity shader that multiplies
  imported mesh colors into the rendered surface.
- Added data-driven runtime profiles and a reusable playable-scene builder for
  controllers, collider policies, fly mode, and voxel hotbars.
- Added a Windows WebView2 bridge mode that packages the original Three.js web
  build unchanged inside a Unity Player with bidirectional message plumbing.
- Added a versioned Unity-authoritative logic protocol with sequence filtering,
  JavaScript fallback, ready/first-state/state watchdogs, and a reusable module registry.
- Added `voxel-player-v1` and `shop-flight-v1`; the latter moves name-to-shop's
  flight clock, easing, position, and rotation into a pure C# fixed-tick motor.
- Moved Unity-to-Web named-pipe writes off the Unity main thread.
- Added a bounded reliable queue and latest-value coalescing for realtime state streams.
- Added a reusable state-emission gate that suppresses identical fixed-tick snapshots
  while retaining immediate changes, acknowledgements, and a watchdog-safe heartbeat.
- Added transport and state-emission telemetry through `THREE_UNITY_BRIDGE_PERF`.
- Serialized Web-to-Unity pipe writes through a single bounded WebView-host pump
  and batched Unity-to-Web UI dispatches instead of scheduling one UI callback per message.
- Added an independent 250 ms parent-process watcher so the embedded WebView helper
  cannot remain orphaned when a Unity Player is terminated abruptly.
- Added document-scoped WebView readiness. Unity-to-Web dispatch now waits for both
  the current `ContentLoading.NavigationId` and its listener ACK; redirect completions
  cannot reuse an old ACK, while hash/history navigation no longer restarts the Host.
- Added per-session outbound owner purge, reliable/latest fairness, lightweight leases,
  and separate backpressure, actual-drop, owner-purge, and fairness telemetry.
- Added a Windows Job zero-process relaunch fence and throttled retry when the first
  `TerminateJobObject` call fails after the root Host has already exited.
- Added physical Host-kill, connect-timeout, and page-ready-timeout Player harnesses
  with exact process handles, one-shot fault injection, overlap detection, and orphan checks.
- Added optional producer-carried outgoing type/session metadata. Built-in modules now
  classify reliable/latest traffic without reparsing their serialized JSON, while legacy
  third-party modules retain the original string-queue fallback and separate telemetry.
- Bounded each logic-to-transport flush to 256 successful messages, retaining burst
  backlog in FIFO order for later Unity callbacks and reporting budget/max-batch telemetry.
- Added capability-negotiated `runtime-lifecycle-v1`: Unity publishes session-scoped,
  latest-only Player focus/pause state after `bridge.ready`, validates reliable browser
  acknowledgements, and purges retired lifecycle state across logical/physical restarts.
- Added a fail-open browser `RuntimeLifecycleGate` and integrated name-to-shop so inactive
  Players skip expensive render/update frames and suspend Web Audio without catch-up bursts.
