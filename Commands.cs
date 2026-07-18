using Lunaris;
using ErenshorBossTimers.Core;

namespace ErenshorBossTimers
{
    // Lunaris auto-registers these as /rbt <command> based on the
    // sanitized plugin name from the [LunarisPlugin] attribute in Plugin.cs.
    //
    // These methods MUST be parameterless. Lunaris reflects each method's
    // parameter list to build the command's argument signature, so a
    // `(string args)` parameter makes the argument REQUIRED and a bare
    // /rbt <cmd> is rejected with "Usage: /rbt <cmd> <string>".
    //
    // Lunaris also renders every command as "/rbt <name> » <description>"
    // on the plugin card, one line each, with no way to hide them. So the
    // attribute descriptions are kept to a couple of words to keep that card
    // compact; the full explanations live in `/rbt help`, printed to the
    // console on demand.
    public class Commands
    {
        [LunarisCommand("toggle", "Show/hide the overlay")]
        public static void Command_Toggle()
        {
            var plugin = BossTimersPlugin.Instance;
            if (plugin == null) return;

            plugin.Overlay.Visible = !plugin.Overlay.Visible;
            Log.Chat($"[RBT] Overlay {(plugin.Overlay.Visible ? "shown" : "hidden")}.");
        }

        [LunarisCommand("clear", "Clear active timers")]
        public static void Command_Clear()
        {
            BossTimersPlugin.Instance?.Timers.ClearAll();
            Log.Chat("[RBT] Cleared active timers.");
        }

        [LunarisCommand("reload", "Reload auras.json")]
        public static void Command_Reload()
        {
            var plugin = BossTimersPlugin.Instance;
            if (plugin == null) return;

            plugin.Triggers.Load();
            Log.Chat($"[RBT] Reloaded {plugin.Triggers.Auras.Count} aura definitions.");
        }

        [LunarisCommand("test", "Show a 5s test bar")]
        public static void Command_Test()
        {
            BossTimersPlugin.Instance?.Timers.Start("Test Timer", 5f, "#7F77DD");
        }

        [LunarisCommand("detail", "Cycle alert detail")]
        public static void Command_Detail()
        {
            // Parameterless, so cycle rather than take an argument (Lunaris makes
            // any parameter a required arg). Also available as a dropdown in the
            // Lunaris settings window; this routes through the same config so the
            // two stay in sync.
            BossTimersPlugin.Instance?.CycleDetail();
        }

        [LunarisCommand("zone", "Show the current zone name")]
        public static void Command_Zone()
        {
            // Prints GameData.SceneName so you can copy it into an aura's "Zone"
            // field to scope that aura to this zone.
            var z = Zones.Current();
            Log.Chat($"[RBT] Current zone: {(string.IsNullOrEmpty(z) ? "(unknown)" : z)}");
        }

#if RBT_DEV
        [LunarisCommand("record", "Toggle fight recording")]
        public static void Command_Record()
        {
            var rec = BossTimersPlugin.Instance?.Recorder;
            if (rec == null) return;

            rec.Enabled = !rec.Enabled;
            Log.Chat($"[RBT] Fight recording {(rec.Enabled ? "on" : "off")}.");
        }

        [LunarisCommand("dump", "Dump recent chat lines")]
        public static void Command_Dump()
        {
            BossTimersPlugin.Instance?.Recorder?.DumpChatLines();
        }
#endif

        [LunarisCommand("help", "List all commands")]
        public static void Command_Help()
        {
            // The full reference, on demand, posted to the in-game chat window.
            Log.Chat("[RBT] Commands (colors, scale and alert detail are in the Options window):");
            Log.Chat("  /rbt toggle  - show or hide the timer overlay");
            Log.Chat("  /rbt clear   - remove all active timer bars");
            Log.Chat("  /rbt reload  - reload auras.json after editing it (no restart needed)");
            Log.Chat("  /rbt test    - show a 5-second test bar to check rendering");
            Log.Chat("  /rbt detail  - cycle alert text: Minimal / GameFaithful / Descriptive");
            Log.Chat("  /rbt zone    - print the current zone name (for an aura's Zone field)");
            Log.Chat("  /rbt help    - show this list");
#if RBT_DEV
            Log.Chat("  /rbt record  - [dev] toggle fight recording to plugins\\fights");
            Log.Chat("  /rbt dump    - [dev] print recent chat lines to the console");
#endif
        }
    }
}
