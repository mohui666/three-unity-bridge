# name-to-shop thin adapter

This adapter is the game-specific edge of `shop-flight-v1`. Copy
`unity-flight-adapter.js` into a Three.js/Vite game, then connect these callbacks:

```js
const flightBridge = attachUnityFlightAuthority({
  getSnapshot: () => ({ time: flightT, amplitude: flyAmp, flying: mode.flying }),
  applyState: state => {
    flightT = state.time
    flyAmp = state.amplitude
    applyFlightStateToScene({ shopRoot, camera, controls }, state)
  },
  runFallbackFrame: dt => updateOriginalJavaScriptFlight(dt),
  onAuthorityChange: active => {
    if (active) stopTheJavaScriptFlightTween()
  },
})
```

Call `flightBridge.update(dt)` once per animation frame,
`flightBridge.requestFlying(boolean)` for input, and `flightBridge.reset()` when
the active shop changes. The original JavaScript simulation remains the safe
fallback and runs automatically until the first valid Unity state arrives.
