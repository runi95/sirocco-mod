using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;
namespace SiroccoMod.Mods.Skins
{
    public static class SkinSystem
    {
        private static bool _triggered;
        private static bool _hasRun;
        private static int _delayFrames;

        internal static List<SkinGenerator.PlayerSkinInfo>? EarlyGeneratedSkins;
        internal static bool NetworkSkinDataSet;
        internal static Dictionary<string, string>? NameToGuidMap;

        internal static bool HasRun => _hasRun;
        internal static void MarkHasRun() { _hasRun = true; }

        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

                // Patch 1: PostSkinningFinishInit for local skin application
                var poolType = asm?.GetType("Il2CppWartide.PlayerShipsObjectPool");
                if (poolType != null)
                {
                    var method = poolType.GetMethod("PostSkinningFinishInit", HarmonyPatcher.FLAGS);
                    if (method != null)
                    {
                        var prefix = new HarmonyLib.HarmonyMethod(
                            typeof(SkinPoolApplicator).GetMethod(nameof(SkinPoolApplicator.Prefix_PostSkinningFinishInit), HarmonyPatcher.FLAGS));
                        harmony.Patch(method, prefix: prefix);
                        MelonLogger.Msg("[SkinApply] Patched PostSkinningFinishInit");
                    }
                }

                // Patch 2: InitializeBackendSourcedData for early network skin data injection
                var simType = asm?.GetType("Il2CppWartide.SimulationManager");
                if (simType != null)
                {
                    var initMethod = simType.GetMethod("InitializeBackendSourcedData", HarmonyPatcher.FLAGS);
                    if (initMethod != null)
                    {
                        var postfix = new HarmonyLib.HarmonyMethod(
                            typeof(SkinNetworkSync).GetMethod(nameof(SkinNetworkSync.Postfix_InitializeBackendSourcedData), HarmonyPatcher.FLAGS));
                        harmony.Patch(initMethod, postfix: postfix);
                        MelonLogger.Msg("[SkinApply] Patched InitializeBackendSourcedData");
                    }
                }

            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SkinApply] Install failed: {ex.Message}");
            }
        }

        internal static void Trigger()
        {
            if (_triggered) return;
            _triggered = true;
            _delayFrames = 180;
        }

        public static void OnUpdate()
        {
            if (!_triggered || _hasRun) return;
            if (_delayFrames > 0) { _delayFrames--; return; }
            _hasRun = true;
            var reflection = new GameReflectionBridge();
            if (reflection.IsValid)
                SkinPoolApplicator.ApplyRandomSkins(reflection);
        }

        public static void Reset()
        {
            _triggered = false;
            _hasRun = false;
            _delayFrames = 0;
            NetworkSkinDataSet = false;
            EarlyGeneratedSkins = null;
            NameToGuidMap = null;
        }
    }
}
