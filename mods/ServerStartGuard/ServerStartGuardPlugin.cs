using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.ServerStartGuard.ServerStartGuardPlugin), "Server Start Guard", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.ServerStartGuard
{
    /// <summary>
    /// Prevents SteamP2PNetworkTester.StartSteamP2PServer from executing.
    /// Players pressing F1 mid-match would otherwise start a new server and crash the game.
    /// </summary>
    public class ServerStartGuardPlugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[ServerStartGuard] Assembly-CSharp not found");
                return;
            }

            var type = asm.GetType("Il2CppWartide.Testing.SteamP2PNetworkTester");
            if (type == null)
            {
                MelonLogger.Warning("[ServerStartGuard] SteamP2PNetworkTester not found");
                return;
            }

            var method = type.GetMethod("StartSteamP2PServer", HarmonyPatcher.FLAGS);
            if (method == null)
            {
                MelonLogger.Warning("[ServerStartGuard] StartSteamP2PServer not found");
                return;
            }

            var prefix = new HarmonyLib.HarmonyMethod(typeof(ServerStartGuardPlugin), nameof(Prefix));
            HarmonyInstance.Patch(method, prefix: prefix);
            MelonLogger.Msg("[ServerStartGuard] Installed");
        }

        private static bool Prefix()
        {
            MelonLogger.Warning("[ServerStartGuard] Blocked F1 server start");
            return false;
        }
    }
}
