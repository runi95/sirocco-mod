using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.NoMoreAFKAutoDisconnect.NoMoreAFKAutoDisconnectPlugin), "No More AFK Auto Disconnect", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.NoMoreAFKAutoDisconnect
{
    /// <summary>
    /// Prevents the client-side AFK detection from kicking the player.
    /// Patches IsLocalPlayerAfk to always report the player as active.
    /// </summary>
    public class NoMoreAFKAutoDisconnectPlugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[NoMoreAFKAutoDisconnect] Assembly-CSharp not found");
                return;
            }

            var type = asm.GetType("Il2CppWartide.UserInputManager");
            if (type == null)
            {
                MelonLogger.Warning("[NoMoreAFKAutoDisconnect] UserInputManager not found");
                return;
            }

            var method = type.GetMethod("IsLocalPlayerAfk", HarmonyPatcher.FLAGS);
            if (method == null)
            {
                MelonLogger.Warning("[NoMoreAFKAutoDisconnect] IsLocalPlayerAfk not found");
                return;
            }

            var prefix = new HarmonyLib.HarmonyMethod(typeof(NoMoreAFKAutoDisconnectPlugin), nameof(Prefix));
            HarmonyInstance.Patch(method, prefix: prefix);
            MelonLogger.Msg("[NoMoreAFKAutoDisconnect] Installed – AFK kick disabled");
        }

        private static bool Prefix(ref bool __result)
        {
            __result = false;
            return false;
        }
    }
}
