# Changelog

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
