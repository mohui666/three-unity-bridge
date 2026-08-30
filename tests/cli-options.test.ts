import assert from "node:assert/strict";
import test from "node:test";
import { normalizeLogicProfile } from "../src/cli-options.js";

test("logic profile normalization preserves packaging-only mode", () => {
  assert.equal(normalizeLogicProfile(undefined), "");
  assert.equal(normalizeLogicProfile(""), "");
  assert.equal(normalizeLogicProfile("   "), "");
});

test("logic profile normalization accepts registered reusable profiles", () => {
  assert.equal(normalizeLogicProfile("voxel-player-v1"), "voxel-player-v1");
  assert.equal(normalizeLogicProfile(" voxel-player-v1 "), "voxel-player-v1");
  assert.equal(normalizeLogicProfile("shop-flight-v1"), "shop-flight-v1");
});

test("logic profile normalization rejects game names and unknown profiles", () => {
  assert.throws(() => normalizeLogicProfile("LittleCubes"), /Unsupported logic profile 'LittleCubes'/);
  assert.throws(() => normalizeLogicProfile("player-v2"), /Unsupported logic profile 'player-v2'/);
});
