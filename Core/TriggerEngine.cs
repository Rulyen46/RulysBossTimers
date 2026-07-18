using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Lunaris;

namespace ErenshorBossTimers.Core
{
    public class TriggerEngine
    {
        private readonly TimerManager _timers;
        private List<AuraDefinition> _auras = new List<AuraDefinition>();

        // Config.Register<T>() is Lunaris's documented "preferred" config
        // approach, but the docs don't show its actual method signature or
        // where the resulting file lives, so guessing at it risked giving
        // you code that compiles against a fictional API. This uses plain
        // Newtonsoft JSON (bundled with Lunaris) next to the DLL instead -
        // it definitely works, and you can migrate to Config.Register<T>
        // later once you've confirmed its real shape (check Lunaris.dll in
        // ILSpy, or ask in the Lunaris repo's issues).
        private static string ConfigPath =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "auras.json");

        public TriggerEngine(TimerManager timers)
        {
            _timers = timers;
            Load();
        }

        public IReadOnlyList<AuraDefinition> Auras => _auras;

        public void OnChatLine(string line)
        {
            foreach (var aura in _auras)
            {
                if (!aura.Enabled) continue;

                // HP-watch auras are driven by HpWatcher polling health, not by
                // log text.
                if (aura.IsHpWatch) continue;

                // Skip auras scoped to a different zone - this is what stops one
                // boss's telegraph firing another zone's same-worded aura.
                if (!Zones.InScope(aura)) continue;

                // Stop wins over start, so a line that somehow matches both
                // clears the bar rather than re-arming it forever.
                if (aura.CompiledStopPattern != null && aura.CompiledStopPattern.IsMatch(line))
                {
                    _timers.Stop(aura.Name);
                    continue;
                }

                if (aura.CompiledPattern != null && aura.CompiledPattern.IsMatch(line))
                {
                    // Pass the actual matched line: in Game-faithful detail the bar
                    // shows the game's own words, so we hand it the exact text the
                    // player just saw rather than the aura's coaching Name.
                    _timers.Start(aura.Name, aura.DurationSeconds, aura.ColorHex, line, aura.Theme);
                }
            }
        }

        public void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    _auras = JsonConvert.DeserializeObject<List<AuraDefinition>>(json) ?? new List<AuraDefinition>();
                }
                else
                {
                    _auras = DefaultAuras();
                    Save();
                }
            }
            catch (Exception ex)
            {
                Log.Info($"[RBT] Failed to load auras.json, using defaults: {ex}");
                _auras = DefaultAuras();
            }

            // Validate every aura defensively. auras.json is hand-edited and
            // shared as community packs, so one bad entry must disable only
            // itself - never throw out of here. Load() runs from the constructor,
            // and an exception there would leave the whole plugin half-built and
            // NREing every frame, so this pass is the difference between "one
            // aura is ignored" and "the mod is bricked until the file is fixed".
            var valid = new List<AuraDefinition>();
            foreach (var a in _auras)
            {
                if (a == null) continue;

                if (string.IsNullOrWhiteSpace(a.Name))
                {
                    Log.Info("[RBT] Skipping an aura with no Name.");
                    continue;
                }

                // A NaN or Infinity from hand-edited JSON slips past every > / <=
                // guard downstream (all comparisons are false), producing a bar
                // that never clears or a NaN fraction. Reject it up front.
                if (!IsFinite(a.DurationSeconds) || !IsFinite(a.HpThresholdPercent) ||
                    !IsFinite(a.WarnWithinPercent))
                {
                    Log.Info($"[RBT] Aura '{a.Name}' has a non-numeric value; disabling it.");
                    continue;
                }

                try
                {
                    a.Compile();
                }
                catch (Exception ex)
                {
                    Log.Info($"[RBT] Aura '{a.Name}' has an invalid pattern; disabling it: {ex.Message}");
                    continue;
                }

                // A log aura with no duration and no stop pattern would put up a
                // bar that never goes away. Warn rather than silently doing it.
                if (!a.IsHpWatch && a.DurationSeconds <= 0f && a.CompiledStopPattern == null)
                    Log.Info($"[RBT] Aura '{a.Name}' has no DurationSeconds and no StopPattern, " +
                             "so its bar will never clear. Set one or the other.");

                valid.Add(a);
            }
            _auras = valid;

            // Drop any bars from the previous config. On /rbt reload, an
            // aura that was renamed or removed no longer has anything to clear its
            // Gauge/Persistent bar, which would otherwise hang on screen forever.
            _timers.ClearAll();
        }

        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_auras, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Log.Info($"[RBT] Failed to save auras.json: {ex}");
            }
        }

        private static List<AuraDefinition> DefaultAuras()
        {
            // Both are real, observed in a live fight on 2026-07-16. Patterns
            // omit mob names where the text looks generic, so they fire for any
            // mob using the same mechanic.
            return new List<AuraDefinition>
            {
                // Watches Tojokom's health so you can see the add spawn coming.
                // The log only prints the summon after it happens, so this is
                // the only way to get a warning. Threshold is 51%, read straight
                // from the game data (SpawnAddsEveryXPercent = 51 on Tojokom's
                // NPCFightEvent) - the earlier 50% was an eyeball from one pull.
                new AuraDefinition
                {
                    Name = "Mutt spawn approaching",
                    WatchNpcName = "Tojokom",
                    HpThresholdPercent = 51f,
                    WarnWithinPercent = 10f,
                    ColorHex = "#E0B040",
                    Theme = "Warning",
                    Zone = "Vitheo's Plane of Valor"
                },

                // "Tojokom summons a companion" (BattleMechanicText) fires as
                // Mutt appears. No duration: the bar lasts exactly as long as
                // the add is alive.
                //
                // The stop pattern must cover BOTH kill phrasings. The game says
                // "<mob> has been slain by <player>" when someone else lands the
                // blow but "You have slain <mob>" when you do - matching only the
                // first would hang the bar forever on your own kills.
                new AuraDefinition
                {
                    Name = "Mutt is up",
                    MatchPattern = @"summons a companion",
                    StopPattern = @"(Mutt has been slain|You have slain Mutt)",
                    DurationSeconds = 0f,
                    ColorHex = "#D85A30",
                    Theme = "Info",
                    Zone = "Vitheo's Plane of Valor"
                },

                // Arbor sows at exactly 60% and 30% health - confirmed across two
                // separate fights (59.30/29.38 and 59.49/29.37), while the gap
                // between sows differed by 35s (73.8s vs 38.7s). Matching HP with
                // mismatched intervals is what proves it keys off health, not a
                // timer. The sow line lands within 1s of each crossing, so these
                // bars drain to empty exactly as the mechanic fires and hand off
                // to the "SAPLING - heal lands!" bar below.
                //
                // WarnWithinPercent 10 gives 6-12s of warning (measured); 5 would
                // give ~3s, no better than reacting to the sow line itself.
                new AuraDefinition
                {
                    Name = "Sapling sow at 60%",
                    WatchNpcName = "Arbor",
                    HpThresholdPercent = 60f,
                    WarnWithinPercent = 10f,
                    ColorHex = "#E0B040",
                    Theme = "Warning",
                    Zone = "Plane of the Willow"
                },
                new AuraDefinition
                {
                    Name = "Sapling sow at 30%",
                    WatchNpcName = "Arbor",
                    HpThresholdPercent = 30f,
                    WarnWithinPercent = 10f,
                    ColorHex = "#E0B040",
                    Theme = "Warning",
                    Zone = "Plane of the Willow"
                },

                // Arbor, measured 2026-07-16 across two spawns in one fight:
                // "Arbor sows seeds nearby!" (BattleMechanicText) -> the Sapling's
                // 600751-life heal on Arbor lands +2.72s and +2.71s later. A 10ms
                // spread means a fixed cast, so this bar empties exactly as the
                // heal lands.
                new AuraDefinition
                {
                    Name = "SAPLING - heal lands!",
                    MatchPattern = @"sows seeds",
                    DurationSeconds = 2.72f,
                    ColorHex = "#E04030",
                    Theme = "Danger",
                    Zone = "Plane of the Willow"
                },

                // The Sapling keeps re-casting that heal until it dies, and there
                // is no "begins casting" line to time the repeats off, so this bar
                // just stays up while the add is alive.
                new AuraDefinition
                {
                    Name = "Sapling up - kill it",
                    MatchPattern = @"sows seeds",
                    StopPattern = @"(Sapling has been slain|You have slain Sapling)",
                    DurationSeconds = 0f,
                    ColorHex = "#40A0E0",
                    Theme = "Info",
                    Zone = "Plane of the Willow"
                },

                // Fernallan High Priest, measured 2026-07-16 over 4 casts:
                //   "...opens a void and prepares to share a moment of pain
                //    that your raid is inflicting on him!"
                //   -> "...shares a moment of his pain..., healing himself!"
                //
                // This is a STOP-DPS window: he mirrors the damage the raid deals
                // during the cast back onto the raid. The bar counts down the
                // window - hold damage until it empties, then resume.
                //
                // The gap was 5.52/5.51/5.53/5.51s (27ms spread), so the window
                // length is fixed. 5.51 is the measured MINIMUM, so the bar
                // empties a hair before the hit rather than after it.
                new AuraDefinition
                {
                    Name = "STOP DPS - void!",
                    MatchPattern = @"opens a void",
                    StopPattern = @"(Fernallan High Priest has been slain|You have slain Fernallan High Priest)",
                    DurationSeconds = 5.51f,
                    ColorHex = "#E03020",
                    Theme = "Danger",
                    Zone = "Plane of the Willow"
                },

                // The only confirmed COOLDOWN so far: telegraph-to-telegraph was
                // 23.86/23.85/23.88s (20ms spread) while boss HP at each cast
                // ranged 78.7% -> 7.9%. Fixed interval + scattered HP is the
                // signature of a timer, and the exact inverse of Arbor's sows.
                // Lead time matters more here than for a reactive bar: it lets
                // you avoid starting a long cast that would land inside the
                // stop-DPS window. Both bars stop on the kill so neither lingers.
                new AuraDefinition
                {
                    Name = "Next stop-DPS window",
                    MatchPattern = @"opens a void",
                    StopPattern = @"(Fernallan High Priest has been slain|You have slain Fernallan High Priest)",
                    DurationSeconds = 23.86f,
                    ColorHex = "#8060E0",
                    Theme = "Warning",
                    Zone = "Plane of the Willow"
                },

                // Animation of Grace: at ~1/3 health the boss instantly returns to
                // 100% and splits into "Echo of Grace" adds (~1.6-2.0s later).
                // Confirmed across two pulls (2026-07-16):
                //   pull 1: fired at 33.26% and 33.15%; 3rd crossing (33.28%) did
                //           NOT fire. Reset-to-reset 95.16s.
                //   pull 2: fired at 34.65% and 33.19%; 3rd crossing (33.29%) did
                //           NOT fire. Reset-to-reset 29.05s.
                // Intervals of 95.16s vs 29.05s rule out a timer; HP is the
                // trigger. Exactly two charges per pull, then it stops - the wipe
                // in pull 2 rode the boss down to 21.5% with no further resets.
                //
                // The 34.65% reading is sampling granularity, not a second
                // threshold: the last log line before that reset was 21ms earlier,
                // and crossing 33.33% needs only 44k of a 3.38M pool - one
                // backstab. Treat the trigger as one third.
                //
                // The reset is INVISIBLE in the log until it has already happened
                // (the line below prints once the boss is back at 100%), so an
                // HP-watch is the only way to see it coming. 10% gives ~7s lead.
                //
                // MaxTriggers 2 stops the bar warning on the third approach, when
                // the charges are gone and nothing will happen. Charges are counted
                // off ConfirmPattern - the line proving a split really happened -
                // not off HP crossings, since the crossing we are suppressing is
                // itself a crossing that fires nothing.
                new AuraDefinition
                {
                    Name = "ECHO SPLIT at 33% - boss resets!",
                    WatchNpcName = "Animation of Grace",
                    HpThresholdPercent = 33.3f,
                    // 15 not 10: measured lead across six splits was only
                    // 3.2-7.2s at 10%, but 5.2-11.7s at 15%. Going to 20 buys
                    // just 0.2s on the worst case - the raid bursts through that
                    // band - while leaving the bar up noticeably longer.
                    WarnWithinPercent = 15f,
                    MaxTriggers = 2,
                    ConfirmPattern = @"reels and echoes through time",
                    ColorHex = "#E0B040",
                    Theme = "Warning",
                    Zone = "Soluna's Celestial Plane"
                },

                // "The animation reels and echoes through time, appearing in
                // multiple places at once" - fires as the reset lands. Note it
                // never names the boss, so a name-filtered search hides it.
                new AuraDefinition
                {
                    Name = "BOSS RESET - Echoes up!",
                    MatchPattern = @"reels and echoes through time",
                    StopPattern = @"(Animation of Grace has been slain|You have slain Animation of Grace)",
                    DurationSeconds = 10f,
                    ColorHex = "#E040E0",
                    Theme = "Danger",
                    Zone = "Soluna's Celestial Plane"
                },

                // ---- Tier 1 bosses from BOSS_REVIEW.md, built from extracted game
                // data (not yet fought in person). Cast INTERVALS and add %/second
                // thresholds are read straight from the game's own serialized data
                // and are exact; the short heads-up durations on one-off spawn and
                // AoE alerts are just how long the alert lingers and can be tuned
                // once each boss is fought. Zone names are the game's own scene
                // names - check one in-game with /rbt zone. ----

                // Recurring casts: the bar re-arms on each cast prompt and counts
                // down to the next (interval = CastSpellEveryXSeconds).
                new AuraDefinition { Name = "Gruhglor - next magical attack", MatchPattern = @"channels all of its energy into a magical attack", DurationSeconds = 10f, Theme = "Warning", Zone = "Fernalla's Revival Plains" },
                new AuraDefinition { Name = "Granitus - next quake", MatchPattern = @"Stomps on the ground, causing a quake", DurationSeconds = 10f, Theme = "Warning", Zone = "Windwashed Pass" },
                new AuraDefinition { Name = "Druo - next dark blast", MatchPattern = @"a blast of dark energy hits everyone nearby", DurationSeconds = 6f, Theme = "Warning", Zone = "Underspine Hollow" },
                new AuraDefinition { Name = "Kio - next dark-light channel", MatchPattern = @"Kio channels the dark light of the moon in his favor", DurationSeconds = 15f, Theme = "Warning", Zone = "Shivering Step" },
                // EggSac is left unzoned on purpose - the hatch line is used by egg
                // sacs in more than one place, so scoping it would silence it.
                new AuraDefinition { Name = "EggSac - next hatch", MatchPattern = @"A cluster of eggs suddenly hatches", DurationSeconds = 22f, Theme = "Warning" },

                // Gruhglor also spawns adds at 40% health (SpawnAddsEveryXPercent = 40).
                new AuraDefinition { Name = "Gruhglor - adds at 40%", WatchNpcName = "Gruhglor", HpThresholdPercent = 40f, WarnWithinPercent = 10f, Theme = "Warning", Zone = "Fernalla's Revival Plains" },

                // Add-spawn heads-ups (the game announces the spawn; the duration
                // is just how long the alert lingers).
                new AuraDefinition { Name = "Warder - elemental spawned", MatchPattern = @"The Warder summons an elemental to its side", DurationSeconds = 6f, Theme = "Info", Zone = "Brax's Plane of Elements" },
                new AuraDefinition { Name = "Monarch - spirit spawned", MatchPattern = @"A spirit emerges from the sands", DurationSeconds = 6f, Theme = "Info", Zone = "Braxonian Desert" },
                new AuraDefinition { Name = "Azynthi - children spawned", MatchPattern = @"Azynthi says: Rise, my Children", DurationSeconds = 6f, Theme = "Info", Zone = "Azynthi's Garden" },
                new AuraDefinition { Name = "Jeris - eggs spawned", MatchPattern = @"unearths a cluster of eggs", DurationSeconds = 6f, Theme = "Info", Zone = "Plane of the Willow" },
                new AuraDefinition { Name = "Corruptor - void adds", MatchPattern = @"The Corruptor opens a small, rapidly growing void", DurationSeconds = 6f, Theme = "Info", Zone = "Soluna's Celestial Plane" },

                // Soluna (goddess) phase shouts.
                new AuraDefinition { Name = "Soluna - add wave", MatchPattern = @"Soluna turns her focus to the stars", DurationSeconds = 6f, Theme = "Info", Zone = "Soluna's Celestial Plane" },
                new AuraDefinition { Name = "Soluna - Corruptor emerges", MatchPattern = @"A dark entity emerges from within Soluna", DurationSeconds = 6f, Theme = "Danger", Zone = "Soluna's Celestial Plane" },

                // Fernalla rotating AoEs - "get out" telegraphs.
                new AuraDefinition { Name = "Fernalla - void AoE", MatchPattern = @"unleashing the void beneath", DurationSeconds = 4f, Theme = "Danger", Zone = "Plane of the Willow" },
                new AuraDefinition { Name = "Fernalla - sound wave AoE", MatchPattern = @"unleashing a magical wave of sound", DurationSeconds = 4f, Theme = "Danger", Zone = "Plane of the Willow" },
                new AuraDefinition { Name = "Fernalla - venom AoE", MatchPattern = @"spreads an intoxicating venom", DurationSeconds = 4f, Theme = "Danger", Zone = "Plane of the Willow" },

                // DPS-check self-buffs. DISABLED by default: these telegraphs are
                // GENERIC (many mobs use them - 12 for the spell-resist variant),
                // so as global auras they would spam. To use one on a specific boss,
                // edit auras.json: set Enabled true and fill in Zone (see /rbt zone).
                // ---- Tier 2 bosses from BOSS_REVIEW.md. Every telegraph below was
                // read out of the game's own IL (or serialized PromptOnSpawn), not
                // paraphrased, so the MatchPatterns are trustworthy. What is NOT
                // known is timing: none of these has a measured pull yet, so the
                // durations are "how long the alert lingers", never a claim about
                // when the effect lands. Measure with fightlog before treating any
                // of them as a countdown.
                //
                // Zones are blank because these bosses' scene names have not been
                // captured yet - run /rbt zone in each and fill them in. The lines
                // are specific enough (they name the boss or a unique phrase) that
                // firing globally is not a noise risk in the meantime. ----

                // Zenith & Nadir, in Soluna's Celestial Plane. Mechanics read
                // directly from ZenithNadirScript's IL:
                //
                //  - BalanceLife(): if |Zenith.CurrentHP - Nadir.CurrentHP| > 100000,
                //    BOTH twins are set to the HIGHER of the two. Uneven damage is
                //    undone, not merely penalised. The broadcast below is rate-limited
                //    to once per 360s, so a second balance inside that window happens
                //    SILENTLY - treat the bar as "it has happened at least once
                //    recently", never as a per-occurrence count.
                //  - CheckForSyz(): Syzygy spawns when NADIR (not Zenith) drops below
                //    12% - a hardcoded GetCurHealthAsIntPercentage() < 12 - once per
                //    attempt, guarded by SyzygySpawned.
                //  - SpawnConstellations(): add waves on a repeating timer whose reset
                //    value shortens sharply once Syzygy is up.
                //
                // Measured 2026-07-18 (session-20260718-155902): each twin has
                // 5,850,000 max HP, so BalanceLife's 100,000 gap is only 1.71
                // percentage points - the raid must hold the twins within ~1.7% of
                // each other. Observed burn rate on Nadir was 0.160 %/s, which is
                // what sizes the band below: 2% ~= 12.5s of lead, where 8% would
                // have parked the bar on screen for ~50s.
                //
                // The 12% threshold is exact (source constant). The band is not:
                // it comes from a 100%->90% window with the raid on one target, and
                // DPS near 12% will differ once Syzygy and the faster constellation
                // waves split the raid. Re-check with
                // `fightlog.py hp --npc Nadir --at 12` once a pull actually gets there.
                new AuraDefinition
                {
                    Name = "Syzygy approaching (Nadir 12%)",
                    WatchNpcName = "Nadir",
                    HpThresholdPercent = 12f,
                    WarnWithinPercent = 2f,
                    MaxTriggers = 1,
                    ConfirmPattern = @"beyond your comprehension collides",
                    Theme = "Warning",
                    Zone = "Soluna's Celestial Plane"
                },
                new AuraDefinition { Name = "Syzygy spawned", MatchPattern = @"beyond your comprehension collides", StopPattern = @"(Syzygy has been slain|You have slain Syzygy)", DurationSeconds = 0f, Theme = "Danger", Zone = "Soluna's Celestial Plane" },
                new AuraDefinition { Name = "Zenith/Nadir linked - balance damage", MatchPattern = @"The Universe interferes", DurationSeconds = 8f, Theme = "Warning", Zone = "Soluna's Celestial Plane" },
                new AuraDefinition { Name = "Constellations spawned", MatchPattern = @"mixing the nearby stars", DurationSeconds = 6f, Theme = "Info", Zone = "Soluna's Celestial Plane" },

                // Phantom. A genuine paired window: wards up, then wards down =
                // the damage window. Persistent bar with a clean documented stop.
                new AuraDefinition { Name = "Phantom wards up", MatchPattern = @"calls mysterious lights", StopPattern = @"wards have fallen", DurationSeconds = 0f, Theme = "Warning" },
                new AuraDefinition { Name = "WARDS DOWN - damage window", MatchPattern = @"wards have fallen", DurationSeconds = 8f, Theme = "Info" },

                // Siraethe. Deliberately a lingering alert, not a persistent bar:
                // the ward mob's exact name is unconfirmed, and an aura with no
                // reachable StopPattern hangs on screen for the rest of the fight.
                new AuraDefinition { Name = "Siraethe - ward summoned", MatchPattern = @"summons a protective ward", DurationSeconds = 6f, Theme = "Info" },
                new AuraDefinition { Name = "Siraethe empowered by wards", MatchPattern = @"draws power from her wards", DurationSeconds = 6f, Theme = "Danger" },

                // One-off add spawns - one line, one alert, no repetition.
                new AuraDefinition { Name = "Honsus - Executioner spawned", MatchPattern = @"Vithean Executioner just spawned", DurationSeconds = 6f, Theme = "Info" },
                new AuraDefinition { Name = "Fallen Fernalla - fawns incoming", MatchPattern = @"AWAKEN, MY FAWNS", DurationSeconds = 6f, Theme = "Info" },
                new AuraDefinition { Name = "Gloopa - slimes split", MatchPattern = @"slime splits into more slimes", DurationSeconds = 6f, Theme = "Info" },

                // Inferno Twins - the interrupt cue.
                new AuraDefinition { Name = "Twin energy transfer - interrupt", MatchPattern = @"concentrated energy form, and sends it", DurationSeconds = 6f, Theme = "Danger" },

                // Astra's breath. The one Tier 2 alert that WANTS a real countdown
                // (wind-up -> breath). Left as a linger until a pull is recorded;
                // measure with: fightlog.py lead --from "begins to inhale" --to "cosmic breath"
                new AuraDefinition { Name = "Astra - breath incoming", MatchPattern = @"begins to inhale again", DurationSeconds = 6f, Theme = "Danger" },

                new AuraDefinition { Name = "Boss empowering (melee)", MatchPattern = @"has become more comfortable in battle", DurationSeconds = 6f, Theme = "Warning", Enabled = false },
                new AuraDefinition { Name = "Boss empowering (spell resist)", MatchPattern = @"is learning to bypass your spell resistances", DurationSeconds = 6f, Theme = "Warning", Enabled = false }
            };
        }
    }
}
