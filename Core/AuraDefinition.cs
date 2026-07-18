using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace ErenshorBossTimers.Core
{
    /// <summary>
    /// One user-defined "aura" - the equivalent of a single WeakAuras entry.
    /// There are two kinds, distinguished by whether WatchNpcName is set:
    ///
    /// 1. Log auras (MatchPattern): MatchPattern is tested against every
    ///    combat/chat line. A match starts a bar. If DurationSeconds is greater
    ///    than zero the bar counts down; otherwise it persists until StopPattern
    ///    matches a later line.
    ///
    /// 2. HP-watch auras (WatchNpcName): polls a live mob's health and shows a
    ///    bar as it closes on HpThresholdPercent. Use this to see a threshold
    ///    mechanic coming, which log text alone cannot do - the game only prints
    ///    the line once the mechanic has already fired.
    /// </summary>
    public class AuraDefinition
    {
        public string Name { get; set; }
        public string MatchPattern { get; set; }

        /// <summary>
        /// Optional. When this matches a log line, the bar is cleared. This is
        /// what lets a bar persist for as long as a mechanic is active rather
        /// than for a fixed number of seconds.
        /// </summary>
        public string StopPattern { get; set; }

        /// <summary>
        /// Zero or less means "persist until StopPattern matches" rather than
        /// counting down. A persistent aura with no StopPattern would never
        /// clear, so TriggerEngine.Load warns about that combination.
        /// </summary>
        public float DurationSeconds { get; set; }

        public string ColorHex { get; set; } = "#D85A30";

        /// <summary>
        /// Optional named color theme ("Warning" / "Danger" / "Info"). When set
        /// and recognised, the theme's color (editable in the Lunaris settings
        /// window) is used instead of ColorHex, so a whole palette can be recolored
        /// in one place. Unset or unknown falls back to ColorHex.
        /// </summary>
        public string Theme { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Optional zone scoping. When set, the aura is only evaluated while the
        /// current zone name (GameData.SceneName) contains this text
        /// (case-insensitive). Blank = every zone. Stops one boss's telegraph
        /// from firing another zone's aura (e.g. two different "opens a void"
        /// bosses) and keeps the on-screen list short. Discover a zone's name
        /// in-game with /rbt zone.
        /// </summary>
        public string Zone { get; set; }

        /// <summary>
        /// Set this (a substring of the mob's name, case-insensitive) to make
        /// this an HP-watch aura instead of a log aura.
        /// </summary>
        public string WatchNpcName { get; set; }

        /// <summary>The health percentage the mechanic fires at.</summary>
        public float HpThresholdPercent { get; set; } = 50f;

        /// <summary>
        /// How far above the threshold to start showing the bar. With a
        /// threshold of 50 and this at 10, the bar appears at 60% and empties
        /// as the mob approaches 50%.
        /// </summary>
        public float WarnWithinPercent { get; set; } = 10f;

        /// <summary>
        /// HP-watch only. Some threshold mechanics have limited charges - Animation
        /// of Grace splits at 1/3 health exactly twice, then crosses it freely.
        /// Zero means unlimited; set it and the bar stops warning about a mechanic
        /// that can no longer fire. Charges refill on a new pull (see HpWatcher).
        /// </summary>
        public int MaxTriggers { get; set; }

        /// <summary>
        /// HP-watch only, and required for MaxTriggers to do anything: the log line
        /// proving the mechanic ACTUALLY fired. Charges are counted from this rather
        /// than from HP crossings, so a crossing that produces no mechanic (exactly
        /// the case we are trying to suppress) never burns one.
        /// </summary>
        public string ConfirmPattern { get; set; }

        [JsonIgnore]
        public Regex CompiledPattern { get; private set; }

        [JsonIgnore]
        public Regex CompiledStopPattern { get; private set; }

        [JsonIgnore]
        public Regex CompiledConfirmPattern { get; private set; }

        [JsonIgnore]
        public bool IsHpWatch => !string.IsNullOrEmpty(WatchNpcName);

        // Patterns run against every chat line, and auras.json is shared as
        // community packs, so a catastrophic-backtracking pattern from an
        // untrusted pack must not hang the game's main thread. A per-match
        // timeout turns that into a contained RegexMatchTimeoutException instead
        // of a freeze. Throws ArgumentException on a malformed pattern - callers
        // (TriggerEngine.Load) catch it and disable just that aura.
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        private static Regex Build(string pattern)
        {
            return string.IsNullOrEmpty(pattern)
                ? null
                : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);
        }

        public void Compile()
        {
            CompiledPattern = Build(MatchPattern);
            CompiledStopPattern = Build(StopPattern);
            CompiledConfirmPattern = Build(ConfirmPattern);
        }
    }
}
