using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ErenshorBossTimers.Core
{
    /// <summary>
    /// Drives HP-watch auras by polling a live mob's health.
    ///
    /// This exists because the combat log announces threshold mechanics only
    /// once they have already fired ("Tojokom summons a companion" prints as the
    /// add appears, not before). Watching health directly is the only way to see
    /// one coming.
    ///
    /// All the game members used here are public: NPCTable.LiveNPCs, NPC.NPCName,
    /// Stats.CurrentHP/CurrentMaxHP/MyName, and GameData.PlayerControl.CurrentTarget.
    /// NPC.MyStats is private, so the Stats component is fetched off the NPC's
    /// GameObject instead of reflecting into the field.
    /// </summary>
    public class HpWatcher
    {
        // NPC.MyStats is private, but it is the reference the game itself uses,
        // so prefer it over assuming Stats sits on the NPC's GameObject. If a
        // game update renames the field this goes null and GetComponent covers.
        private static readonly FieldInfo NpcMyStats = AccessTools.Field(typeof(NPC), "MyStats");

        private readonly TimerManager _timers;
        private readonly TriggerEngine _triggers;

        // Polling 5x/second is far below the noise floor of a Unity frame and
        // plenty for a health bar a human is reading.
        private const float PollInterval = 0.2f;
        private float _accum;

        // A mob sitting at (or near) full health is either freshly pulled or has
        // leashed after a wipe - either way, a new pull, so charges refill.
        private const float FullHpPercent = 99f;

        // ...but a split HEALS the boss to full, which would look identical for a
        // moment. Measured across three pulls, the raid pushed it back under 99%
        // within 0.06-2.59s of every split, so ignoring full health for a few
        // seconds after a confirmed trigger separates the two cases cleanly. This
        // is deliberately not keyed off 95%: after a split the raid switches to
        // the adds and the boss can idle in the high 90s for a minute.
        private const float ConfirmGraceSeconds = 8f;

        private class WatchState
        {
            public int Triggers;
            public float LastConfirm = -9999f;

            // Latched true once HP has crossed DOWN through the threshold this
            // cycle (the mechanic point was reached). While latched, the warning
            // is suppressed, so a partial heal that pushes HP back above the band
            // and a re-descent (e.g. Arbor's Sapling healing it, then the raid
            // damaging it back down) does NOT re-warn - the mechanic only fired
            // once. Cleared on a return to full HP or a confirm line (a real new
            // cycle), which is how charge-based bosses like Grace re-arm.
            public bool FiredThisCycle;
        }

        private readonly Dictionary<string, WatchState> _state =
            new Dictionary<string, WatchState>();

        private WatchState State(string name)
        {
            WatchState s;
            if (!_state.TryGetValue(name, out s))
            {
                s = new WatchState();
                _state[name] = s;
            }
            return s;
        }

        /// <summary>
        /// Counts a mechanic that actually fired, for auras with MaxTriggers.
        /// Driven by the log line rather than by HP crossings: the whole point is
        /// to stop warning on crossings that produce nothing.
        ///
        /// Assumes the game logs on the main thread (which it does - the Harmony
        /// patch on UpdateSocialLog.GlobalAddLine, and Poll() from Update(), share
        /// _state and Time.time). If Erenshor ever logged from a worker thread this
        /// would need a lock; the calling patch's try/catch would contain the
        /// resulting Time.time exception, but the shared-dictionary access would be
        /// a genuine race, so this assumption is worth stating.
        /// </summary>
        public void OnChatLine(string line)
        {
            var auras = _triggers.Auras;
            for (int i = 0; i < auras.Count; i++)
            {
                var aura = auras[i];
                if (!aura.Enabled || !aura.IsHpWatch) continue;
                if (!Zones.InScope(aura)) continue;
                if (aura.CompiledConfirmPattern == null) continue;
                if (!aura.CompiledConfirmPattern.IsMatch(line)) continue;

                var st = State(aura.Name);
                st.Triggers++;
                st.LastConfirm = Time.time;
                // The mechanic fired and confirmed - re-arm the warning so a
                // charge-based boss (Grace) can warn again on its next approach.
                // Reliable even when the HP reset is too fast for the poll to see.
                st.FiredThisCycle = false;
                _timers.Stop(aura.Name);
                Log.Info($"[RBT] {aura.Name}: fired {st.Triggers}"
                         + (aura.MaxTriggers > 0 ? $"/{aura.MaxTriggers}" : "") + " time(s) this pull");
            }
        }

        public HpWatcher(TimerManager timers, TriggerEngine triggers)
        {
            _timers = timers;
            _triggers = triggers;
        }

        public void Poll(float deltaTime)
        {
            _accum += deltaTime;
            if (_accum < PollInterval) return;
            _accum = 0f;

            var auras = _triggers.Auras;
            for (int i = 0; i < auras.Count; i++)
            {
                var aura = auras[i];
                if (!aura.Enabled || !aura.IsHpWatch) continue;

                // Off-zone: make sure no stale bar lingers, then skip.
                if (!Zones.InScope(aura)) { _timers.Stop(aura.Name); continue; }

                try { Evaluate(aura); }
                catch (Exception ex)
                {
                    Log.Info($"[RBT] HP watch '{aura.Name}' failed: {ex.Message}");
                    _timers.Stop(aura.Name);
                }
            }
        }

        private void Evaluate(AuraDefinition aura)
        {
            var st = State(aura.Name);
            var stats = FindStats(aura.WatchNpcName);

            // Mob absent, dead, or not yet spawned - make sure no stale bar
            // lingers from a previous pull, and refill charges for the next one.
            if (stats == null || stats.CurrentMaxHP <= 0 || stats.CurrentHP <= 0)
            {
                _timers.Stop(aura.Name);
                st.Triggers = 0;
                st.FiredThisCycle = false;
                return;
            }

            float pct = 100f * stats.CurrentHP / stats.CurrentMaxHP;
            float warnAt = aura.HpThresholdPercent + aura.WarnWithinPercent;

            // Back at full health = a genuinely new cycle: re-arm the warning, and
            // (unless the mechanic just fired - grace window) refill charges. This
            // covers a wipe/leash, where the mob survives and never hits the
            // absent-mob branch above.
            if (pct >= FullHpPercent)
            {
                st.FiredThisCycle = false;
                if (Time.time - st.LastConfirm > ConfirmGraceSeconds)
                    st.Triggers = 0;
            }

            // Charges spent: the mechanic cannot fire again this pull, so warning
            // about it would be a lie.
            if (aura.MaxTriggers > 0 && st.Triggers >= aura.MaxTriggers)
            {
                _timers.Stop(aura.Name);
                return;
            }

            // Crossed down through the threshold - the mechanic point is reached.
            // Latch it so a later heal-and-re-descent within the same cycle can't
            // re-warn (the mechanic is one-shot per cycle; it re-arms only on a
            // full-HP reset or a confirm line).
            if (pct <= aura.HpThresholdPercent)
            {
                st.FiredThisCycle = true;
                _timers.Stop(aura.Name);
                return;
            }

            // Above the warning band - nothing to show yet.
            if (pct > warnAt)
            {
                _timers.Stop(aura.Name);
                return;
            }

            // In the band, but the threshold was already reached this cycle: stay
            // silent until a reset re-arms us.
            if (st.FiredThisCycle)
            {
                _timers.Stop(aura.Name);
                return;
            }

            // Bar drains from full to empty as the mob closes on the threshold.
            // Pass the mob name, not a formatted label: the renderer decides what
            // text to show per the detail setting. A pre-warning must not name the
            // mechanic the game hasn't broadcast yet, so the mob name is all we send.
            float frac = (pct - aura.HpThresholdPercent) / aura.WarnWithinPercent;
            _timers.SetGauge(aura.Name, frac, aura.WatchNpcName, pct, aura.ColorHex, aura.Theme);
        }

        /// <summary>
        /// The Stats for an NPC, preferring the game's own private MyStats
        /// reference over assuming Stats sits on the NPC's GameObject.
        /// </summary>
        internal static Stats StatsOf(NPC npc)
        {
            if (npc == null) return null;
            var s = NpcMyStats != null ? NpcMyStats.GetValue(npc) as Stats : null;
            if (s == null) s = npc.GetComponent<Stats>();
            return s;
        }

        /// <summary>
        /// Finds the live mob whose name contains nameContains. Scans LiveNPCs
        /// first so it works no matter what the player has targeted, and falls
        /// back to the current target if the mob isn't in that list.
        /// </summary>
        private static Stats FindStats(string nameContains)
        {
            var live = NPCTable.LiveNPCs;
            if (live != null)
            {
                for (int i = 0; i < live.Count; i++)
                {
                    var npc = live[i];
                    // Unity's == null also catches destroyed objects, so don't
                    // switch these to ?. - it would bypass that check.
                    if (npc == null) continue;
                    if (string.IsNullOrEmpty(npc.NPCName)) continue;
                    if (npc.NPCName.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var s = StatsOf(npc);
                    if (s != null && s.CurrentHP > 0) return s;
                }
            }

            var pc = GameData.PlayerControl;
            if (pc == null) return null;
            var target = pc.CurrentTarget;
            if (target == null) return null;

            var ts = target.MyStats;
            if (ts == null || string.IsNullOrEmpty(ts.MyName)) return null;
            if (ts.MyName.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) return null;

            return ts;
        }
    }
}
