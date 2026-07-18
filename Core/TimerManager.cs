using System.Collections.Generic;

namespace ErenshorBossTimers.Core
{
    public enum TimerMode
    {
        /// <summary>Ticks down and removes itself at zero.</summary>
        Countdown,
        /// <summary>Stays up, counting elapsed time, until Stop() is called.</summary>
        Persistent,
        /// <summary>Bar length is driven externally (by HpWatcher), never ticks.</summary>
        Gauge
    }

    public class ActiveTimer
    {
        /// <summary>Stable id = the aura's Name. Used for dedupe/stop, and shown
        /// only in Descriptive detail. Never the game-faithful label.</summary>
        public string Name;
        public float Duration;
        public float Remaining;
        public float Elapsed;
        public string ColorHex;
        /// <summary>Optional theme name; when set, overrides ColorHex at draw time.</summary>
        public string Theme;
        public TimerMode Mode;

        /// <summary>Gauge only: 0..1 bar fill, set by whoever owns the gauge.</summary>
        public float Fraction;
        /// <summary>Gauge only: the mob's live HP percent, for display.</summary>
        public float Pct;

        /// <summary>The watched mob's name (HP-watch bars). Safe to show pre-broadcast.</summary>
        public string BossName;
        /// <summary>The actual combat-log line that started this bar (log bars).
        /// This is what Game-faithful detail displays - the game's own words.</summary>
        public string GameLine;
    }

    /// <summary>
    /// Holds all currently-shown bars and ticks the time-based ones. This is
    /// deliberately dumb/decoupled from both the trigger sources (chat lines,
    /// HP polling) and the render target (ImGui) - any of them can be swapped
    /// out without touching this class.
    /// </summary>
    public class TimerManager
    {
        private readonly List<ActiveTimer> _active = new List<ActiveTimer>();
        public IReadOnlyList<ActiveTimer> Active => _active;

        /// <summary>
        /// Starts a bar. duration greater than zero counts down; zero or less
        /// persists until Stop(name). Re-triggering a running aura restarts its
        /// clock, same as most WeakAuras "restart" trigger behavior.
        /// </summary>
        public void Start(string name, float duration, string colorHex, string gameLine = null, string theme = null)
        {
            var mode = duration > 0f ? TimerMode.Countdown : TimerMode.Persistent;
            var existing = _active.Find(t => t.Name == name);
            if (existing != null)
            {
                existing.Mode = mode;
                existing.Duration = duration;
                existing.Remaining = duration;
                existing.Elapsed = 0f;
                existing.ColorHex = colorHex;
                existing.Theme = theme;
                if (gameLine != null) existing.GameLine = gameLine;
                return;
            }

            _active.Add(new ActiveTimer
            {
                Name = name,
                Duration = duration,
                Remaining = duration,
                Elapsed = 0f,
                ColorHex = colorHex,
                Theme = theme,
                Mode = mode,
                GameLine = gameLine
            });
        }

        /// <summary>Adds or updates an externally-driven gauge bar (HP-watch).</summary>
        public void SetGauge(string name, float fraction, string bossName, float pct, string colorHex, string theme = null)
        {
            var existing = _active.Find(t => t.Name == name);
            if (existing == null)
            {
                existing = new ActiveTimer { Name = name, Mode = TimerMode.Gauge };
                _active.Add(existing);
            }

            existing.Mode = TimerMode.Gauge;
            existing.Fraction = fraction < 0f ? 0f : (fraction > 1f ? 1f : fraction);
            existing.BossName = bossName;
            existing.Pct = pct;
            existing.ColorHex = colorHex;
            existing.Theme = theme;
        }

        /// <summary>Removes a bar by name. Safe to call when it isn't showing.</summary>
        public void Stop(string name)
        {
            int i = _active.FindIndex(t => t.Name == name);
            if (i >= 0) _active.RemoveAt(i);
        }

        public bool IsActive(string name) => _active.Exists(t => t.Name == name);

        public void Tick(float deltaTime)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var t = _active[i];
                switch (t.Mode)
                {
                    case TimerMode.Countdown:
                        t.Remaining -= deltaTime;
                        if (t.Remaining <= 0f) _active.RemoveAt(i);
                        break;
                    case TimerMode.Persistent:
                        t.Elapsed += deltaTime;
                        break;
                    // Gauge bars are owned by HpWatcher; it adds, updates and
                    // removes them, so there is nothing to tick here.
                }
            }
        }

        public void ClearAll() => _active.Clear();
    }
}
