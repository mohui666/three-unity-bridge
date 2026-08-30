const LOGIC_PROFILES = new Set(["voxel-player-v1", "shop-flight-v1"]);

export function normalizeLogicProfile(value: string | undefined): string {
  const profile = value?.trim() ?? "";
  if (profile.length === 0) return "";
  if (!LOGIC_PROFILES.has(profile)) {
    throw new Error(`Unsupported logic profile '${profile}'`);
  }
  return profile;
}
