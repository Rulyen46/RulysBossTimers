using System.Numerics;

namespace ErenshorBossTimers.Core
{
    /// <summary>
    /// How much an alert is allowed to say. The mod's guiding principle is that
    /// it adds attention and timing, NOT knowledge the game withholds - so the
    /// default reveals only what the game itself has already broadcast.
    /// </summary>
    public enum AlertDetail
    {
        /// <summary>Bar + timer only. Log alerts show no words; HP pre-warnings
        /// show just the boss name. The least hand-holding.</summary>
        Minimal,

        /// <summary>Default. Log alerts show the game's OWN broadcast line (the
        /// text the player just saw in chat); HP pre-warnings, which fire before
        /// the game has said anything, show only the boss name - never a
        /// description of the mechanic that is coming.</summary>
        GameFaithful,

        /// <summary>The author's coaching label ("STOP DPS", "boss resets"). Most
        /// helpful, but reveals what/when beyond the game's own telegraphs. Opt-in.</summary>
        Descriptive
    }

    /// <summary>
    /// Global, runtime mod settings. Kept as plain values (no Lunaris or Unity
    /// dependency) so the renderer and core logic can read them freely; Plugin.cs
    /// mirrors the Lunaris settings window into here each frame.
    /// </summary>
    public static class BossTimersSettings
    {
        public static AlertDetail Detail = AlertDetail.GameFaithful;

        /// <summary>Overlay size multiplier (bar dimensions + font).</summary>
        public static float Scale = 1f;

        /// <summary>When true, the overlay window can't be dragged.</summary>
        public static bool LockOverlay = false;

        /// <summary>When true, auras with a Zone set only fire in that zone.
        /// Turn off to evaluate every aura everywhere (the safety valve if zone
        /// names ever change).</summary>
        public static bool RespectZones = true;

        // Named color themes. Auras with a matching Theme use these instead of a
        // literal ColorHex, so a whole palette is editable from one settings page.
        public static Vector4 ThemeWarning = new Vector4(0.88f, 0.70f, 0.25f, 1f);
        public static Vector4 ThemeDanger  = new Vector4(0.88f, 0.25f, 0.13f, 1f);
        public static Vector4 ThemeInfo    = new Vector4(0.25f, 0.63f, 0.88f, 1f);

        /// <summary>Resolves a theme name to its color, or null if unknown/blank.</summary>
        public static Vector4? Theme(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            switch (name.Trim().ToLowerInvariant())
            {
                case "warning": return ThemeWarning;
                case "danger":  return ThemeDanger;
                case "info":    return ThemeInfo;
                default:        return null;
            }
        }
    }
}
