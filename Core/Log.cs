using System;

namespace ErenshorBossTimers.Core
{
    /// <summary>
    /// One place all logging goes through, so every file can log regardless of
    /// whether it has the plugin instance in scope.
    ///
    /// Two destinations, because Lunaris's ILog has both:
    ///   Info(...)  -> Logging.Log     : the Lunaris dev console / lunaris.log.
    ///                 For diagnostics (load messages, errors, warnings) the
    ///                 player does not need to see.
    ///   Chat(...)  -> Logging.LogSocial: the game's own chat window, visible in
    ///                 normal play. For command feedback the player DID ask for.
    /// Both fall back to Console.WriteLine if the plugin/logger isn't ready.
    /// </summary>
    public static class Log
    {
        public static void Info(string message)
        {
            try
            {
                var plugin = BossTimersPlugin.Instance;
                if (plugin != null && plugin.Logging != null)
                    plugin.Logging.Log(message);
                else
                    Console.WriteLine(message);
            }
            catch
            {
                Console.WriteLine(message);
            }
        }

        /// <summary>
        /// Posts to the in-game chat window as a SYSTEM message. Building the line
        /// with LogType.SystemMessages puts it in the game's own system channel
        /// (same styling/filter as "You have slain ...", "Found N Gold!"), rather
        /// than plain social chat. Use for user-facing command output. Lunaris
        /// no-ops if chat isn't ready, so mirror to the dev console too.
        /// </summary>
        public static void Chat(string message)
        {
            try
            {
                var plugin = BossTimersPlugin.Instance;
                if (plugin != null && plugin.Logging != null)
                {
                    var line = new ChatLogLine(message, ChatLogLine.LogType.SystemMessages, "white");
                    plugin.Logging.LogSocial(line);
                    plugin.Logging.Log(message); // also to the dev log for a record
                }
                else
                {
                    Console.WriteLine(message);
                }
            }
            catch
            {
                Console.WriteLine(message);
            }
        }
    }
}
