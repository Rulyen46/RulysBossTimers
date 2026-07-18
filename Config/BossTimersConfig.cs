using Lunaris.Config;
using UnityEngine;
using ErenshorBossTimers.Core;

namespace ErenshorBossTimers
{
    /// <summary>
    /// The settings object Lunaris renders in its plugin-settings window. Field
    /// types map to widgets automatically: enum -> dropdown, float+[ConfigRange]
    /// -> slider, bool -> checkbox, UnityEngine.Color -> native color picker.
    /// [Config(label, section, tooltip)] labels and groups each field.
    ///
    /// Plugin.cs registers this via Config.Register&lt;T&gt;() and mirrors the live
    /// values into BossTimersSettings each frame, so the rest of the mod never
    /// depends on Lunaris config types.
    /// </summary>
    public class BossTimersConfig
    {
        [Config("Alert detail", "Display",
            "How much an alert reveals. Game-faithful shows only what the game itself broadcasts; " +
            "pre-warnings never name a mechanic the game hasn't announced yet.")]
        public AlertDetail Detail = AlertDetail.GameFaithful;

        [Config("Overlay scale", "Display", "Size of the timer bars and their text.")]
        [ConfigRange(0.6f, 2.5f)]
        public float Scale = 1f;

        [Config("Lock overlay", "Display", "Stop the overlay window from being dragged.")]
        public bool LockOverlay = false;

        [Config("Respect aura zones", "Display",
            "When on, auras that name a zone only fire in that zone. Turn off to run every aura everywhere.")]
        public bool RespectZones = true;

        [Config("Warning color", "Colors", "Auras themed 'Warning' - an incoming mechanic (HP pre-warnings).")]
        public Color Warning = new Color(0.88f, 0.70f, 0.25f, 1f);

        [Config("Danger color", "Colors", "Auras themed 'Danger' - stop or avoid, now.")]
        public Color Danger = new Color(0.88f, 0.25f, 0.13f, 1f);

        [Config("Info color", "Colors", "Auras themed 'Info' - status, e.g. adds are up.")]
        public Color Info = new Color(0.25f, 0.63f, 0.88f, 1f);
    }
}
