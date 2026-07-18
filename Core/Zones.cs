using System;
using UnityEngine;

namespace ErenshorBossTimers.Core
{
    /// <summary>
    /// Zone scoping. The game exposes the current zone as the static string
    /// GameData.SceneName; an aura with a Zone set is only evaluated while that
    /// string contains the aura's Zone (case-insensitive). Kept here so the
    /// AuraDefinition data model stays free of any game dependency.
    /// </summary>
    public static class Zones
    {
        /// <summary>Current zone name, or null if the game isn't ready.</summary>
        public static string Current()
        {
            try { return GameData.SceneName; }
            catch { return null; }
        }

        /// <summary>
        /// Whether an aura should be evaluated right now given its Zone. Auras
        /// with no Zone (or when zone filtering is off) always pass; a zoned aura
        /// passes only when the current zone name contains its Zone text.
        /// </summary>
        public static bool InScope(AuraDefinition aura)
        {
            if (!BossTimersSettings.RespectZones) return true;
            if (string.IsNullOrEmpty(aura.Zone)) return true;

            string current = Current();
            return !string.IsNullOrEmpty(current)
                && current.IndexOf(aura.Zone, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
