using System;
using System.Numerics;
using ImGuiNET;
using ErenshorBossTimers.Core;

namespace ErenshorBossTimers.UI
{
    /// <summary>
    /// Draws one progress bar per active timer, WeakAuras-bar-style.
    ///
    /// Called from BossTimersPlugin.OnImGuiDraw(), which Lunaris invokes every
    /// frame from inside a valid ImGui frame (ImGuiWrap.OnDraw -> Bridge.OnGUI).
    /// Drawing from Update() instead does NOT work.
    /// </summary>
    public class OverlayRenderer : IDisposable
    {
        private readonly TimerManager _timers;
        public bool Visible = true;

        public OverlayRenderer(TimerManager timers)
        {
            _timers = timers;
        }

        // Bars have to stay readable on top of whatever the game is drawing -
        // bright grass, fire, a boss model. That means an opaque dark backing
        // rather than a translucent one, near-black behind the unfilled part of
        // each bar for maximum contrast, and saturated fill colors (see Boost).
        private static readonly Vector4 WindowBg = new Vector4(0.04f, 0.04f, 0.06f, 0.94f);
        private static readonly Vector4 BorderCol = new Vector4(0f, 0f, 0f, 1f);
        private static readonly Vector4 BarTrack = new Vector4(0.02f, 0.02f, 0.03f, 1f);
        private static readonly Vector4 TextCol = new Vector4(1f, 1f, 1f, 1f);

        public void Render()
        {
            if (!Visible || _timers.Active.Count == 0) return;

            // These must be pushed BEFORE Begin(), or the window background and
            // border keep Lunaris's default (translucent) styling.
            ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderCol);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BarTrack);
            ImGui.PushStyleColor(ImGuiCol.Text, TextCol);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 8f));

            // The style stack is GLOBAL to the ImGui context. If anything below
            // throws, these 8 entries (and the window) must still be unwound or
            // they leak into every other plugin's windows every frame - so the
            // pops live in a finally. This is cheap insurance against exactly the
            // "unexpected ImGui.NET behaviour" class of bug that has bitten before.
            bool begun = false;
            try
            {
                var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize;
                if (BossTimersSettings.LockOverlay) flags |= ImGuiWindowFlags.NoMove;

                ImGui.Begin("##BossTimers", flags);
                begun = true;

                // Scale applies to both the bar geometry and the font. Clamp
                // defensively so a bad config value can't produce a zero/huge overlay.
                float scale = BossTimersSettings.Scale;
                if (scale < 0.3f) scale = 0.3f; else if (scale > 4f) scale = 4f;
                ImGui.SetWindowFontScale(scale);
                var barSize = new Vector2(280f * scale, 28f * scale);

                foreach (var t in _timers.Active)
                {
                    float frac = t.Mode == TimerMode.Gauge ? t.Fraction
                               : t.Mode == TimerMode.Persistent ? 1f
                               : (t.Duration > 0f ? Math.Max(0f, t.Remaining / t.Duration) : 0f);

                    ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Boost(ResolveColor(t)));
                    try { ImGui.ProgressBar(frac, barSize, Label(t)); }
                    finally { ImGui.PopStyleColor(); }
                }
            }
            finally
            {
                // Reset font scale (window-scoped) then close the window and unwind
                // the 4+4 outer style entries - always, even on an exception above.
                if (begun) { ImGui.SetWindowFontScale(1f); ImGui.End(); }
                ImGui.PopStyleVar(4);
                ImGui.PopStyleColor(4);
            }
        }

        // A game broadcast can be a full sentence ("...opens a void and prepares
        // to share a moment of pain that your raid is inflicting on him!"). Clip
        // it to fit the bar; the untruncated line is right there in the game's
        // own chat log if the player wants it.
        private const int MaxLineChars = 46;

        /// <summary>
        /// Resolves what a bar says, per the global detail setting. This is where
        /// the "add timing, not knowledge" principle lives:
        ///   Descriptive  - the aura's authored Name ("STOP DPS - void!").
        ///   GameFaithful - the game's own broadcast line for log bars; only the
        ///                  boss name for HP pre-warnings (which fire before the
        ///                  game has said anything, so naming the mechanic would
        ///                  spoil it).
        ///   Minimal      - boss name for HP bars; nothing but the timer for log
        ///                  bars.
        /// The timer/percent suffix is always shown - that is pure timing, which
        /// the mod is allowed to add.
        /// </summary>
        private static string Label(ActiveTimer t)
        {
            string primary;
            switch (BossTimersSettings.Detail)
            {
                case AlertDetail.Descriptive:
                    primary = t.Name;
                    break;
                case AlertDetail.Minimal:
                    // Log bars carry no BossName, so they show only the timer.
                    primary = t.BossName ?? "";
                    break;
                default: // GameFaithful
                    primary = Clip(t.GameLine) ?? t.BossName ?? t.Name;
                    break;
            }

            string suffix;
            switch (t.Mode)
            {
                case TimerMode.Gauge:       suffix = $"{t.Pct:0.#}%"; break;
                case TimerMode.Persistent:  suffix = $"{t.Elapsed:0}s"; break;
                default:                    suffix = $"{t.Remaining:0.0}s"; break;
            }

            return string.IsNullOrEmpty(primary) ? suffix : $"{primary}  {suffix}";
        }

        /// <summary>
        /// A timer's color: its named theme (live-editable in the settings window)
        /// if it has one, otherwise its literal ColorHex.
        /// </summary>
        private static Vector4 ResolveColor(ActiveTimer t)
        {
            var themed = BossTimersSettings.Theme(t.Theme);
            return themed ?? ParseColor(t.ColorHex);
        }

        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            // ASCII "..." not the ellipsis glyph - ImGui's default font may not
            // include U+2026, which would render as a missing-glyph box.
            return s.Length <= MaxLineChars ? s : s.Substring(0, MaxLineChars - 3).TrimEnd() + "...";
        }

        /// <summary>
        /// Drives the brightest channel to full while keeping the hue, so a
        /// muted config colour still renders as a vivid bar. Configured hex
        /// values set the hue; this guarantees the intensity.
        /// </summary>
        private static Vector4 Boost(Vector4 c)
        {
            float max = Math.Max(c.X, Math.Max(c.Y, c.Z));
            if (max <= 0.001f) return c;

            float scale = 1f / max;
            return new Vector4(
                Math.Min(1f, c.X * scale),
                Math.Min(1f, c.Y * scale),
                Math.Min(1f, c.Z * scale),
                1f);
        }

        private static readonly Vector4 FallbackColor = new Vector4(0.85f, 0.35f, 0.19f, 1f);

        private static Vector4 ParseColor(string hex)
        {
            // A typo'd ColorHex in auras.json must not throw: this runs inside
            // the ImGui frame, every frame, so an exception here would break the
            // draw rather than just look wrong.
            try
            {
                string h = (hex ?? "").TrimStart('#');
                if (h.Length < 6) return FallbackColor;

                byte r = Convert.ToByte(h.Substring(0, 2), 16);
                byte g = Convert.ToByte(h.Substring(2, 2), 16);
                byte b = Convert.ToByte(h.Substring(4, 2), 16);
                return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
            }
            catch
            {
                return FallbackColor;
            }
        }

        public void Dispose()
        {
            // The Lunaris docs are explicit that ImGui state is the #1 source
            // of leaks on mod reload. There's nothing to dispose yet in this
            // minimal version, but if you add ImGui textures, fonts, or
            // custom contexts later, release them here.
        }
    }
}
