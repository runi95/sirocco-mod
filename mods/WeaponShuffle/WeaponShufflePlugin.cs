using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using SiroccoMod;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.WeaponShuffle.WeaponShufflePlugin), "Weapon Tier Shuffle", "6.2.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.WeaponShuffle
{
    // Flips WartideSettings.RunTimePlatformSettings.RandomizeWeaponTiersAtGameStart to true at runtime
    // so the base game's Fisher-Yates shuffle in EconomyManager.Initialize runs. The shipped build keeps
    // the flag false; the modded host enables it via this mod.
    //
    // Implementation notes:
    // - IL2CPP-interop wraps source-level public fields as properties — use GetProperty, not GetField.
    //   (BalanceTweaks does the same for `_simEntityData` etc.)
    // - Patch EconomyManager.Initialize with a Harmony prefix because OnSceneWasInitialized fires too late:
    //   Initialize runs from a MonoBehaviour Awake/Start chain, before MelonLoader's scene callback.
    //
    // Must be installed on both server and clients — engine's GetEffectiveRequiredWeaponTier requires the
    // flag on each side to honor the synced tier; clients without the mod fall back to authored tiers.
    public class WeaponShufflePlugin : MelonMod
    {
        const string TargetAssembly = "Assembly-CSharp";
        static PropertyInfo? _runtimeSettings;
        static PropertyInfo? _flag;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == TargetAssembly);
            if (asm == null)
            {
                MelonLogger.Error($"[WeaponShuffle] {TargetAssembly} not loaded");
                return;
            }

            var econType = asm.GetType("Il2CppWartide.EconomyManager");
            var settingsType = asm.GetType("Il2CppWartide.WartideSettings");
            var platformType = asm.GetType("Il2CppWartide.PlatformSpecificGameSettings");
            if (econType == null || settingsType == null || platformType == null)
            {
                MelonLogger.Error($"[WeaponShuffle] type lookup failed (econ={econType != null}, settings={settingsType != null}, platform={platformType != null})");
                return;
            }

            _runtimeSettings = settingsType.GetProperty("RunTimePlatformSettings", HarmonyPatcher.FLAGS);
            _flag = platformType.GetProperty("RandomizeWeaponTiersAtGameStart", HarmonyPatcher.FLAGS);
            if (_runtimeSettings == null || _flag == null)
            {
                MelonLogger.Error($"[WeaponShuffle] property lookup failed (runtime={_runtimeSettings != null}, flag={_flag != null})");
                return;
            }

            var initMethod = econType.GetMethod("Initialize", HarmonyPatcher.FLAGS);
            if (initMethod == null)
            {
                MelonLogger.Error("[WeaponShuffle] EconomyManager.Initialize not found");
                return;
            }

            try
            {
                var prefixMethod = typeof(WeaponShufflePlugin).GetMethod(nameof(BeforeEconomyInitialize), HarmonyPatcher.FLAGS);
                HarmonyInstance.Patch(initMethod, prefix: new HarmonyMethod(prefixMethod));
                MelonLogger.Msg("[WeaponShuffle] Hooked EconomyManager.Initialize");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[WeaponShuffle] Harmony patch failed: {ex.Message}");
            }
        }

        // Runs immediately before EconomyManager.Initialize's body, so the flag is true by the time
        // the `if (RandomizeWeaponTiersAtGameStart)` check is evaluated.
        static void BeforeEconomyInitialize()
        {
            try
            {
                if (_runtimeSettings == null || _flag == null) return;

                var instance = _runtimeSettings.GetValue(null);
                if (instance == null)
                {
                    MelonLogger.Warning("[WeaponShuffle] RunTimePlatformSettings null at Initialize — flag not flipped");
                    return;
                }

                _flag.SetValue(instance, true);
                MelonLogger.Msg("[WeaponShuffle] RandomizeWeaponTiersAtGameStart = true");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[WeaponShuffle] prefix failed: {ex.Message}");
            }
        }
    }
}
