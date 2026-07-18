using HarmonyLib;
using Lunaris;
using ErenshorBossTimers.Core;

namespace ErenshorBossTimers.Patches
{
    // Erenshor funnels essentially all chat/combat text through
    // UpdateSocialLog.GlobalAddLine - a single choke point for pulls, aggro,
    // casts, deaths, etc. That makes it the most WeakAuras-like hook available.
    //
    // Signature verified against Assembly-CSharp.dll metadata:
    //   private static void GlobalAddLine(ChatLogLine incoming)
    // It is private, hence the string name rather than nameof. It is not
    // overloaded, so no argument-types array is needed. The line text is
    // incoming.MyChatString; incoming.MyLogType is a ChatLogLine.LogType enum
    // (PlayerHits, NPCEmotes, SpellResults, BattleMechanicText, ...) that a
    // future AuraDefinition could filter on to avoid matching say/shout chat.
    [HarmonyPatch(typeof(UpdateSocialLog), "GlobalAddLine")]
    public static class ChatLogPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ChatLogLine __0)
        {
            string line = __0?.MyChatString;
            if (string.IsNullOrEmpty(line)) return;

            try
            {
#if RBT_DEV
                // Dev builds record the raw line (with timestamp and health) so
                // aura timings can be measured. Release builds have no recorder.
                BossTimersPlugin.Instance?.Recorder?.Record(__0);
#endif

                BossTimersPlugin.Instance?.Triggers.OnChatLine(line);

                // HP-watch auras need to see the log too, to count how many times
                // a limited-charge mechanic has actually fired this pull.
                BossTimersPlugin.Instance?.Hp?.OnChatLine(line);
            }
            catch (System.Exception ex)
            {
                Log.Info($"[RBT] Trigger error on line '{line}': {ex}");
            }
        }
    }
}
