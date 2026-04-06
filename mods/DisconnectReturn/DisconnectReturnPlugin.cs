using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.DisconnectReturn.DisconnectReturnPlugin), "Disconnect Return To Spawn", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.DisconnectReturn
{
    /// <summary>
    /// Forces disconnected players' ships back to harbor in P2P mode.
    ///
    /// Root cause: In P2P mode, all server-side navigation processing in
    /// PreFixedUpdatePopulatePlayerController is gated by IsServerOnly(), which reads the
    /// _isServerOnly field on GameAuthority. This is false in P2P mode, so the server never
    /// processes navigation — it's entirely client-driven. When a client disconnects, the
    /// entity retains its last destination forever.
    ///
    /// The game already detects disconnects (OnServerDisconnect fires via Steam P2P timeout)
    /// and calls SetConnected(false) + CommandServerClearNavigation(harborPos) every 5 seconds.
    /// But those navigation commands are ignored because IsServerOnly() is false.
    ///
    /// Fix: When OnServerDisconnect fires, set _isServerOnly = true on the GameAuthority
    /// instance. This enables the server-side navigation pipeline, allowing the existing
    /// disconnect-to-harbor logic to work.
    /// </summary>
    public class DisconnectReturnPlugin : MelonMod
    {
        private static PropertyInfo? _gaInstanceProp;
        private static PropertyInfo? _isServerOnlyProp;
        private static bool _wasToggled;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[DisconnectReturn] Assembly-CSharp not found");
                return;
            }

            var gaType = asm.GetType("Il2CppWartide.GameAuthority");
            if (gaType != null)
            {
                _gaInstanceProp = gaType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _isServerOnlyProp = gaType.GetProperty("_isServerOnly", HarmonyPatcher.FLAGS);
            }

            var netManagerType = asm.GetType("Il2CppWartide.WartideNetworkManager");
            if (netManagerType == null)
            {
                MelonLogger.Warning("[DisconnectReturn] WartideNetworkManager not found");
                return;
            }

            var disconnectMethod = netManagerType.GetMethod("OnServerDisconnect", HarmonyPatcher.FLAGS);
            if (disconnectMethod == null)
            {
                MelonLogger.Warning("[DisconnectReturn] OnServerDisconnect not found");
                return;
            }

            var postfix = new HarmonyLib.HarmonyMethod(typeof(DisconnectReturnPlugin), nameof(Postfix_OnServerDisconnect));
            HarmonyInstance.Patch(disconnectMethod, postfix: postfix);

            // Restore _isServerOnly before shutdown to prevent freeze
            var stopMethod = netManagerType.GetMethod("StopSinglePlayerHost", HarmonyPatcher.FLAGS);
            if (stopMethod != null)
            {
                var prefix = new HarmonyLib.HarmonyMethod(typeof(DisconnectReturnPlugin), nameof(Prefix_StopHost));
                HarmonyInstance.Patch(stopMethod, prefix: prefix);
            }

            MelonLogger.Msg("[DisconnectReturn] Installed");
        }

        private static void Postfix_OnServerDisconnect()
        {
            try
            {
                if (_gaInstanceProp == null || _isServerOnlyProp == null)
                {
                    MelonLogger.Warning("[DisconnectReturn] Reflection handles null");
                    return;
                }

                var gaInstance = _gaInstanceProp.GetValue(null);
                if (gaInstance == null)
                {
                    MelonLogger.Warning("[DisconnectReturn] GameAuthority.Instance is null");
                    return;
                }

                bool current = (bool)_isServerOnlyProp.GetValue(gaInstance)!;
                if (current)
                {
                    MelonLogger.Msg("[DisconnectReturn] _isServerOnly already true");
                    return;
                }

                _isServerOnlyProp.SetValue(gaInstance, true);
                _wasToggled = true;
                MelonLogger.Msg("[DisconnectReturn] Set _isServerOnly=true to enable server navigation for disconnected player");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[DisconnectReturn] Error: {ex.Message}");
            }
        }
        private static void Prefix_StopHost()
        {
            RestoreServerOnly();
        }

        public override void OnApplicationQuit()
        {
            RestoreServerOnly();
        }

        private static void RestoreServerOnly()
        {
            if (!_wasToggled) return;
            _wasToggled = false;

            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null) return;

                _isServerOnlyProp?.SetValue(gaInstance, false);
                MelonLogger.Msg("[DisconnectReturn] Restored _isServerOnly=false");
            }
            catch { }
        }
    }
}
