using System;
using System.Reflection;
using HarmonyLib;
using Lunaris;
using Lunaris.Config;
using ErenshorBossTimers.Core;
using ErenshorBossTimers.UI;

namespace ErenshorBossTimers
{
    // The plugin name is what Lunaris sanitises into the command prefix
    // (name.Replace(" ","").ToLower()), so "RBT" gives /rbt. It is also the card
    // title, so the full name lives in the description below.
    [LunarisPlugin("RBT", "1.2.0", "Ruly", "Ruly's Boss Timers - WeakAuras-style boss encounter alerts")]
    [LunarisPermission(LunarisPermission.None)]
    public class BossTimersPlugin : LunarisPlugin
    {
        internal static BossTimersPlugin Instance { get; private set; }

        internal TimerManager Timers { get; private set; }
        internal TriggerEngine Triggers { get; private set; }
        internal OverlayRenderer Overlay { get; private set; }
        internal HpWatcher Hp { get; private set; }

#if RBT_DEV
        // Dev builds only - see Core/FightRecorder.cs. Release builds ship no
        // recorder at all.
        internal FightRecorder Recorder { get; private set; }
#endif

        private Harmony _harmony;
        private const string HarmonyId = "com.ruly.erenshorbosstimers";

        // Lunaris renders this in its settings window. May be null if config
        // registration ever fails - every use is guarded, and the mod falls back
        // to BossTimersSettings' built-in defaults.
        private IConfigHandle<BossTimersConfig> _cfg;

        // Standard Unity lifecycle message - matches the pattern shown in the
        // Lunaris docs example, and Lunaris clearly supports it since that's
        // exactly what the docs' "Getting Started" sample uses.
        private void Awake()
        {
            Instance = this;
            Logging.Log("[RBT] Loading...");

            Timers = new TimerManager();
            Triggers = new TriggerEngine(Timers);
            Overlay = new OverlayRenderer(Timers);
            Hp = new HpWatcher(Timers, Triggers);
#if RBT_DEV
            Recorder = new FightRecorder();
#endif

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(BossTimersPlugin).Assembly);

            // Register the settings object so Lunaris shows it in its window.
            // Guarded: a config-API hiccup must not stop the mod loading.
            try
            {
                _cfg = Config.Register<BossTimersConfig>();
                SyncConfig();
            }
            catch (Exception ex)
            {
                Log.Info($"[RBT] Settings registration failed, using defaults: {ex.Message}");
            }

            Logging.Log("[RBT] Loaded. Try /rbt toggle in-game.");
        }

        private void Update()
        {
            float dt = UnityEngine.Time.deltaTime;
            Timers.Tick(dt);
            Hp.Poll(dt);

            // Mirror the Lunaris settings window into our plain settings holder
            // every frame, so edits the player makes there take effect live. This
            // is deliberately poll-based rather than relying on a change callback,
            // which is cheap (a few field copies) and robust to callback quirks.
            SyncConfig();
        }

        /// <summary>Copies the live config values into BossTimersSettings.</summary>
        private void SyncConfig()
        {
            if (_cfg == null) return;
            BossTimersConfig c;
            try { c = _cfg.Get(); } catch { return; }
            if (c == null) return;

            BossTimersSettings.Detail = c.Detail;
            BossTimersSettings.Scale = c.Scale;
            BossTimersSettings.LockOverlay = c.LockOverlay;
            BossTimersSettings.RespectZones = c.RespectZones;
            BossTimersSettings.ThemeWarning = ToVec(c.Warning);
            BossTimersSettings.ThemeDanger = ToVec(c.Danger);
            BossTimersSettings.ThemeInfo = ToVec(c.Info);
        }

        private static System.Numerics.Vector4 ToVec(UnityEngine.Color c)
            => new System.Numerics.Vector4(c.r, c.g, c.b, c.a);

        /// <summary>
        /// Advances the alert-detail setting. Writes THROUGH the config when it
        /// exists (so the settings window and the per-frame sync stay consistent),
        /// and only falls back to the plain holder when there is no config.
        /// </summary>
        internal void CycleDetail()
        {
            int count = System.Enum.GetValues(typeof(AlertDetail)).Length;
            var next = (AlertDetail)(((int)BossTimersSettings.Detail + 1) % count);

            // Get() returns Lunaris's live config instance (verified), so mutating
            // it here is what the settings window and the per-frame SyncConfig both
            // read - that keeps all three in step. Save() persists it. Do the
            // in-memory mutation first so a Save() hiccup can't lose the change,
            // and log failures rather than swallowing them: this is the one path
            // that couldn't be runtime-tested, so silent failure would be invisible.
            if (_cfg != null)
            {
                try
                {
                    var c = _cfg.Get();
                    if (c != null) c.Detail = next;
                    Config.Save();
                }
                catch (Exception ex)
                {
                    Log.Info($"[RBT] Couldn't persist detail setting: {ex.Message}");
                }
            }

            // Apply to the live holder for instant effect (and the only path when
            // there is no config).
            BossTimersSettings.Detail = next;
            Log.Chat($"[RBT] Alert detail: {next}");
        }

        // Lunaris calls this every frame from inside a valid ImGui frame, so
        // all drawing must happen here rather than from Update().
        public override void OnImGuiDraw()
        {
            Overlay.Render();
        }

        private void OnDestroy()
        {
            // Docs are explicit: not cleaning up here causes memory leaks,
            // especially with ImGui. Unpatch Harmony and dispose the overlay.
            try { _harmony?.UnpatchSelf(); }
            catch (Exception ex) { Logging.Log($"[RBT] Unpatch failed: {ex}"); }

            Overlay?.Dispose();
#if RBT_DEV
            Recorder?.Dispose();
#endif

            // Reset the singleton so a hot reload (OnDestroy -> Awake) starts
            // clean. Guarded so a late-destroyed old instance can't wipe a newer one.
            //
            // UnregisterConfig MUST sit inside the same guard. Lunaris gates the
            // Options button on ConfigHandler.Has(SetPluginName), so removing our
            // entry while a newer instance is live grays the button out until the
            // plugin is toggled off and on again. If the reload order is
            // "new Awake -> old OnDestroy" (which it can be), an unguarded call
            // here deletes the config the NEW instance just registered.
            if (Instance == this)
            {
                Instance = null;
                UnregisterConfig();
            }

            Logging.Log("[RBT] Unloaded and cleaned up.");
        }

        /// <summary>
        /// Removes our entry from Lunaris's config registry on unload.
        ///
        /// Lunaris keeps every plugin's ConfigInstance in a STATIC, name-keyed
        /// registry (ConfigHandler) and does NOT replace an existing one when the
        /// plugin hot-reloads. Without this, each reload's Register&lt;T&gt;() adds a
        /// fresh set of fields to the surviving instance, and the settings window
        /// shows them stacked 2x, 3x, ... A fresh game start is clean; only
        /// reloads accumulate, so this clears the entry so the next load re-adds
        /// once.
        ///
        /// ConfigHandler is internal to Lunaris, hence reflection. Fully guarded:
        /// if a Lunaris update moves or renames it, cleanup is simply skipped (a
        /// game restart still resets it) rather than breaking unload. The plugin
        /// key is derived from our own [LunarisPlugin] name the same way Lunaris
        /// sanitises it, so it can't drift from the attribute.
        /// </summary>
        private static void UnregisterConfig()
        {
            try
            {
                var attr = (LunarisPluginAttribute)Attribute.GetCustomAttribute(
                    typeof(BossTimersPlugin), typeof(LunarisPluginAttribute));
                string key = attr?.Name?.Replace(" ", "").ToLower();
                if (string.IsNullOrEmpty(key)) return;

                var handler = typeof(LunarisPlugin).Assembly.GetType("Lunaris.Config.ConfigHandler");
                var remove = handler?.GetMethod("Remove", BindingFlags.Public | BindingFlags.Static);
                remove?.Invoke(null, new object[] { key });
            }
            catch (Exception ex)
            {
                Log.Info($"[RBT] Config registry cleanup skipped: {ex.Message}");
            }
        }
    }
}
