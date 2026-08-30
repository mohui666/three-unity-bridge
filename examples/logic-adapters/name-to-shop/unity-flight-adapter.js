import * as THREE from 'three'
import { createShopFlightAuthority } from 'three-unity-bridge/logic'

// Game-specific code is limited to mapping a reusable profile's state back to
// the Three.js scene. Handshake, generation isolation, watchdogs, and fallback
// ownership live in three-unity-bridge/logic.
export function applyFlightStateToScene({ shopRoot, camera, controls }, flightState) {
  shopRoot.position.set(flightState.position.x, flightState.position.y, flightState.position.z)
  shopRoot.rotation.set(flightState.rotation.x, flightState.rotation.y, flightState.rotation.z)
  const center = new THREE.Vector3(flightState.position.x, flightState.position.y + 2, flightState.position.z)
  const delta = center.sub(controls.target)
  controls.target.add(delta)
  camera.position.add(delta)
}

export function attachUnityFlightAuthority(options) {
  return createShopFlightAuthority({ gameId: 'name-to-shop', ...options })
}
