using System;
using System.Linq;
using MelonLoader;
using SiroccoMod;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.SuppressErrorBroadcast.SuppressErrorBroadcastPlugin), "Suppress Error Broadcast", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.SuppressErrorBroadcast
{
    /// <summary>
    /// Suppresses GameAuthority.BroadcastErrorToClients in P2P mode.
    ///
    /// The game's error handler broadcasts every server error to all clients via
    /// TargetRpc. When a client disconnects, the broadcast to that client fails,
    /// which logs another error, which triggers another broadcast — creating an
    /// infinite cascade that floods the log and freezes the host.
    /// </summary>
    public class SuppressErrorBroadcastPlugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[SuppressErrorBroadcast] Assembly-CSharp not found");
                return;
            }

            var gaType = asm.GetType("Il2CppWartide.GameAuthority");
            if (gaType == null)
            {
                MelonLogger.Error("[SuppressErrorBroadcast] GameAuthority type not found");
                return;
            }

            var method = gaType.GetMethod("BroadcastErrorToClients", HarmonyPatcher.FLAGS);
            if (method != null)
            {
                HarmonyInstance.Patch(method,
                    prefix: new HarmonyLib.HarmonyMethod(
                        typeof(SuppressErrorBroadcastPlugin).GetMethod(nameof(Prefix_BroadcastErrorToClients), HarmonyPatcher.FLAGS)));
                MelonLogger.Msg("[SuppressErrorBroadcast] Patched BroadcastErrorToClients");
            }
            else
            {
                MelonLogger.Warning("[SuppressErrorBroadcast] BroadcastErrorToClients not found");
            }

            MelonLogger.Msg("[SuppressErrorBroadcast] Installed");
        }

        public static bool Prefix_BroadcastErrorToClients()
        {
            return false;
        }
    }
}
