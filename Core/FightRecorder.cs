#if RBT_DEV
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ErenshorBossTimers.Core
{
    /// <summary>
    /// DEV-ONLY. Records every chat/combat line to
    /// <c>plugins\fights\session-yyyyMMdd-HHmmss.jsonl</c> so the timings behind an
    /// aura can be MEASURED rather than eyeballed. scripts/fightlog.py in the
    /// erenshor-boss-timers skill reads these files.
    ///
    /// This whole file is compiled only when RBT_DEV is defined (the Debug
    /// configuration). Release builds - the ones that ship - contain no recorder,
    /// no file writing, and no /rbt record or /rbt dump commands. Writing a log of
    /// everything a player's raid says is not something to ship on by default.
    ///
    /// Each event is one JSON object per line:
    ///   {"t":..,"type":..,"text":..,"tgt":..,"hp":..,"hpNow":..,"hpMax":..,
    ///    "src":..,"srcHp":..}
    /// t is UnityEngine.Time.time (seconds since GAME start, so it is monotonic
    /// within a session but meaningless across files). "tgt" is whatever the player
    /// happens to have targeted; "src" is the live mob actually NAMED in the line,
    /// which is the one worth trusting when stamping a mechanic with a health value.
    /// </summary>
    public class FightRecorder : IDisposable
    {
        // The recorder is on by default in a dev build - a mechanic that only
        // shows up once per pull is not worth losing because recording was off.
        public bool Enabled { get; set; } = true;

        private StreamWriter _writer;

        // Recent raw lines, for /rbt dump. Small on purpose: this is a "what did
        // the game just print" peek, not a second copy of the session log.
        private const int RecentCapacity = 60;
        private readonly Queue<string> _recent = new Queue<string>();

        private static string FightsDir =>
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                "fights");

        public FightRecorder()
        {
            // A recorder that throws must never stop the mod loading, so every
            // file operation here is guarded and simply degrades to no recording.
            try
            {
                Directory.CreateDirectory(FightsDir);
                string path = Path.Combine(
                    FightsDir,
                    "session-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl");

                _writer = new StreamWriter(path, false, new UTF8Encoding(false));
                // AutoFlush because the usual way a session ends is a game crash
                // or an alt-F4, neither of which runs a finaliser.
                _writer.AutoFlush = true;
                _writer.WriteLine(
                    "{\"session\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    + "\",\"note\":\"t is UnityEngine.Time.time (seconds since game start)\"}");

                Log.Info("[RBT] Recording fight data to " + path);
            }
            catch (Exception ex)
            {
                _writer = null;
                Log.Info("[RBT] Fight recording disabled: " + ex.Message);
            }
        }

        public void Record(ChatLogLine incoming)
        {
            if (incoming == null) return;
            string text = incoming.MyChatString;
            if (string.IsNullOrEmpty(text)) return;

            // Keep the ring buffer fed even when recording to disk is off, so
            // /rbt dump still works after /rbt record toggles it.
            _recent.Enqueue(text);
            while (_recent.Count > RecentCapacity) _recent.Dequeue();

            if (!Enabled || _writer == null) return;

            try
            {
                var sb = new StringBuilder(256);
                sb.Append("{\"t\":").Append(Num(Time.time));
                sb.Append(",\"type\":\"").Append(incoming.MyLogType).Append('"');
                sb.Append(",\"text\":").Append(Str(text));

                AppendTarget(sb);
                AppendSource(sb, text);

                sb.Append('}');
                _writer.WriteLine(sb.ToString());
            }
            catch (Exception ex)
            {
                // One bad line must not kill the session log or spam the console,
                // so stop recording rather than failing on every subsequent line.
                Log.Info("[RBT] Recording stopped after error: " + ex.Message);
                CloseWriter();
            }
        }

        /// <summary>Whatever the player currently has targeted, and its health.</summary>
        private static void AppendTarget(StringBuilder sb)
        {
            var pc = GameData.PlayerControl;
            if (pc == null) return;
            var target = pc.CurrentTarget;
            // Unity's == null also catches destroyed objects - do not switch to ?.
            if (target == null) return;
            var st = target.MyStats;
            if (st == null || string.IsNullOrEmpty(st.MyName)) return;

            sb.Append(",\"tgt\":").Append(Str(st.MyName));
            if (st.CurrentMaxHP > 0)
            {
                sb.Append(",\"hp\":").Append(Num(100f * st.CurrentHP / st.CurrentMaxHP));
                sb.Append(",\"hpNow\":").Append(st.CurrentHP);
                sb.Append(",\"hpMax\":").Append(st.CurrentMaxHP);
            }
        }

        /// <summary>
        /// The live mob whose name appears in the line, and ITS health. This is the
        /// value analysis should use: the player's target drifts to adds mid-fight
        /// and would otherwise stamp a boss mechanic with an add's health.
        /// Longest name wins so "Fernallan High Priest" is not matched as "Fernalla".
        /// </summary>
        private static void AppendSource(StringBuilder sb, string text)
        {
            var live = NPCTable.LiveNPCs;
            if (live == null) return;

            Stats best = null;
            int bestLen = 0;
            for (int i = 0; i < live.Count; i++)
            {
                var npc = live[i];
                if (npc == null) continue;
                string name = npc.NPCName;
                if (string.IsNullOrEmpty(name) || name.Length <= bestLen) continue;
                if (text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var st = HpWatcher.StatsOf(npc);
                if (st == null || st.CurrentMaxHP <= 0) continue;

                best = st;
                bestLen = name.Length;
            }

            if (best == null) return;
            sb.Append(",\"src\":").Append(Str(best.MyName));
            sb.Append(",\"srcHp\":").Append(Num(100f * best.CurrentHP / best.CurrentMaxHP));
        }

        /// <summary>Prints the most recent lines to the dev console, tagged so the
        /// skill's log greps can filter them out.</summary>
        public void DumpChatLines()
        {
            Log.Chat("[RBT] Dumped " + _recent.Count + " recent lines to the Lunaris console.");
            foreach (var line in _recent)
                Log.Info("[RBT] [dump] " + line);
        }

        /// <summary>Invariant-culture numbers - a comma decimal separator would
        /// produce JSON that fightlog.py cannot parse.</summary>
        private static string Num(float v) =>
            v.ToString("0.#####", CultureInfo.InvariantCulture);

        private static string Str(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private void CloseWriter()
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }

        public void Dispose() => CloseWriter();
    }
}
#endif
