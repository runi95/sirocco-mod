using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;
using SiroccoMod.Helpers;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.WeaponShuffle.WeaponShufflePlugin), "Weapon Tier Shuffle", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.WeaponShuffle
{
    /// <summary>
    /// Randomizes weapon unlock tiers each game, similar to how ship tiers
    /// are shuffled by the base game's RandomizeShipTiersAtGameStart setting.
    ///
    /// Each weapon has a RequiredWeaponCore value that determines when it
    /// becomes available. This mod shuffles those values across all player
    /// weapons so the unlock order is different every game, and reorders
    /// the weapon grid to match.
    /// </summary>
    public class WeaponShufflePlugin : MelonMod
    {
        private static bool _shuffleApplied;
        private static PropertyInfo? _gaInstanceProp;

        // Maps original TypeID Value → shuffled TypeID Value for server-side translation
        private static Dictionary<int, int> _typeIdMapping = new();
        // Maps TypeID Value → actual TypeID object from weapon containers (properly registered)
        private static Dictionary<int, object> _typeIdObjects = new();
        private static MethodInfo? _addPushWeaponMethod;
        private static bool _inTranslation;
        private static float _translationEnabledTime = float.MaxValue;
        private static int _localPlayerIndex = -1;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[WeaponShuffle] Assembly-CSharp not found");
                return;
            }

            var gaType = asm.GetType("Il2CppWartide.GameAuthority");
            if (gaType != null)
                _gaInstanceProp = gaType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);

            // Patch HUD_WeaponsMenu.Initialize to reorder weapons after items are created
            var menuType = asm.GetType("Il2CppWartide.HUD_WeaponsMenu");
            if (menuType != null)
            {
                // Use GetMethods to avoid AmbiguousMatchException from base class overloads
                var initMethod = menuType.GetMethods(HarmonyPatcher.FLAGS)
                    .FirstOrDefault(m => m.Name == "Initialize" && m.DeclaringType == menuType);
                if (initMethod != null)
                {
                    var postfix = new HarmonyLib.HarmonyMethod(
                        typeof(WeaponShufflePlugin).GetMethod(nameof(Postfix_Initialize), HarmonyPatcher.FLAGS));
                    HarmonyInstance.Patch(initMethod, postfix: postfix);
                    MelonLogger.Msg("[WeaponShuffle] Patched HUD_WeaponsMenu.Initialize");
                }
                else
                {
                    MelonLogger.Warning("[WeaponShuffle] HUD_WeaponsMenu.Initialize not found");
                }
            }
            else
            {
                MelonLogger.Warning("[WeaponShuffle] HUD_WeaponsMenu type not found");
            }

            // Patch AddPushWeaponTransaction to translate weapon selections from unmodded clients
            var econType = asm.GetType("Il2CppWartide.EconomyManager");
            if (econType != null)
            {
                _addPushWeaponMethod = econType.GetMethod("AddPushWeaponTransaction", HarmonyPatcher.FLAGS);
                if (_addPushWeaponMethod != null)
                {
                    var prefix = new HarmonyLib.HarmonyMethod(
                        typeof(WeaponShufflePlugin).GetMethod(nameof(Prefix_AddPushWeapon), HarmonyPatcher.FLAGS));
                    HarmonyInstance.Patch(_addPushWeaponMethod, prefix: prefix);
                    MelonLogger.Msg("[WeaponShuffle] Patched AddPushWeaponTransaction");
                }
            }

            MelonLogger.Msg("[WeaponShuffle] Installed");
        }

        /// <summary>
        /// Translates weapon TypeID from unmodded clients.
        /// Unmodded clients select based on original weapon positions, so we map
        /// original position → shuffled position to equip the correct weapon.
        /// Skips the original call and re-invokes with the translated TypeID.
        /// </summary>
        public static bool Prefix_AddPushWeapon(object __instance, int __0, object __1, bool __2)
        {
            if (_inTranslation || _typeIdMapping.Count == 0 ||
                UnityEngine.Time.time < _translationEnabledTime ||
                __0 == _localPlayerIndex) return true;

            try
            {
                int originalValue = GetTypeIdValue(__1);
                if (originalValue < 0) return true;

                if (!_typeIdMapping.TryGetValue(originalValue, out int newValue) || originalValue == newValue)
                    return true;

                if (_addPushWeaponMethod == null) return true;

                // Use the stored TypeID object from the actual weapon container
                // (properly registered with the correct type, not just a Value)
                if (!_typeIdObjects.TryGetValue(newValue, out var newTypeId))
                    return true;

                MelonLogger.Msg($"[WeaponShuffle] Translated weapon: TypeID {originalValue} -> {newValue}");

                // Re-invoke with translated TypeID, skip original
                _inTranslation = true;
                try
                {
                    _addPushWeaponMethod.Invoke(__instance, new object[] { __0, newTypeId, __2 });
                }
                finally
                {
                    _inTranslation = false;
                }
                return false; // Skip original
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WeaponShuffle] Translation error: {ex.Message}");
            }
            return true;
        }

        private static void ResolveLocalPlayerIndex()
        {
            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null) return;

                var simManager = gaInstance.GetType()
                    .GetProperty("_simulationManager", HarmonyPatcher.FLAGS)?
                    .GetValue(gaInstance);
                if (simManager == null) return;

                var indexProp = simManager.GetType()
                    .GetProperty("_gameAuthorityLocalPlayerIndex", HarmonyPatcher.FLAGS);
                if (indexProp != null)
                {
                    _localPlayerIndex = (int)(indexProp.GetValue(simManager) ?? -1);
                }
                else
                {
                    var indexField = simManager.GetType()
                        .GetField("_gameAuthorityLocalPlayerIndex", HarmonyPatcher.FLAGS);
                    if (indexField != null)
                        _localPlayerIndex = (int)(indexField.GetValue(simManager) ?? -1);
                }

                MelonLogger.Msg($"[WeaponShuffle] Local player index: {_localPlayerIndex}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WeaponShuffle] Failed to resolve local player index: {ex.Message}");
            }
        }

        private static int GetTypeIdValue(object typeId)
        {
            if (typeId == null) return -1;
            var field = typeId.GetType().GetField("Value", HarmonyPatcher.FLAGS);
            return field != null ? (int)(field.GetValue(typeId) ?? -1) : -1;
        }

        private static int GetItemTypeId(object item)
        {
            var typeIdVal = item.GetType().GetProperty("TypeID", HarmonyPatcher.FLAGS)?.GetValue(item);
            return typeIdVal != null ? GetTypeIdValue(typeIdVal) : -1;
        }

        public static void Postfix_Initialize(object __instance)
        {
            try
            {
                MelonLogger.Msg("[WeaponShuffle] Initialize postfix running...");
                ReorderWeaponItems(__instance);

                // Resolve local player index so we don't translate host's own purchases
                ResolveLocalPlayerIndex();

                // Enable translation after a delay to skip the default weapon assignment burst
                _translationEnabledTime = UnityEngine.Time.time + 5f;
                MelonLogger.Msg($"[WeaponShuffle] Weapon translation will activate in 5 seconds (local player index: {_localPlayerIndex})");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WeaponShuffle] Postfix error: {ex}");
            }
        }

        private static void ReorderWeaponItems(object menuInstance)
        {
            // Get _weaponItems (List<HUD_WeaponItem>) — we know this works
            var weaponItems = menuInstance.GetType()
                .GetProperty("_weaponItems", HarmonyPatcher.FLAGS)?
                .GetValue(menuInstance);
            if (weaponItems == null)
            {
                MelonLogger.Warning("[WeaponShuffle] _weaponItems not found");
                return;
            }

            // Get the grid layout — its _items list controls visual positioning
            var gridLayout = menuInstance.GetType()
                .GetProperty("_weaponGridLayout", HarmonyPatcher.FLAGS)?
                .GetValue(menuInstance);
            if (gridLayout == null)
            {
                MelonLogger.Warning("[WeaponShuffle] _weaponGridLayout not found");
                return;
            }

            var layoutItems = gridLayout.GetType()
                .GetProperty("_items", HarmonyPatcher.FLAGS)?
                .GetValue(gridLayout);
            if (layoutItems == null)
            {
                MelonLogger.Warning("[WeaponShuffle] _items not found on grid layout");
                return;
            }

            int count = IL2CppArrayHelper.GetLength(weaponItems);
            int gridCount = IL2CppArrayHelper.GetLength(layoutItems);
            MelonLogger.Msg($"[WeaponShuffle] {count} weapon items, {gridCount} grid items");

            if (count < 2 || gridCount < 2) return;

            var wiProp = weaponItems.GetType().GetProperty("Item");
            var giProp = layoutItems.GetType().GetProperty("Item");
            if (wiProp == null || giProp == null) return;

            // Build paired list: (HUD_WeaponItem, RectTransform, core level)
            int pairCount = Math.Min(count, gridCount);
            var entries = new List<(object weaponItem, object rectTransform, int core, string name)>();

            for (int i = 0; i < pairCount; i++)
            {
                var wi = wiProp.GetValue(weaponItems, new object[] { i });
                var rt = giProp.GetValue(layoutItems, new object[] { i });
                if (wi == null || rt == null) continue;

                int core = 0;
                string name = "";
                try
                {
                    var getCore = wi.GetType().GetMethod("GetRequiredCoreLevel", HarmonyPatcher.FLAGS);
                    if (getCore != null)
                        core = (int)getCore.Invoke(wi, null)!;
                    var getName = wi.GetType().GetMethod("GetName", HarmonyPatcher.FLAGS);
                    if (getName != null)
                        name = getName.Invoke(wi, null)?.ToString() ?? "";
                }
                catch { }

                entries.Add((wi, rt, core, name));
            }

            MelonLogger.Msg($"[WeaponShuffle] Collected {entries.Count} entries, sorting...");
            entries.Sort((a, b) => a.core.CompareTo(b.core));

            // Rewrite both lists in sorted order
            for (int i = 0; i < entries.Count; i++)
            {
                wiProp.SetValue(weaponItems, entries[i].weaponItem, new object[] { i });
                giProp.SetValue(layoutItems, entries[i].rectTransform, new object[] { i });
                MelonLogger.Msg($"[WeaponShuffle]   [{i}] {entries[i].name} (core={entries[i].core})");
            }

            // Refresh the grid layout positions
            var updateLayout = gridLayout.GetType().GetMethod("UpdateLayout", HarmonyPatcher.FLAGS);
            updateLayout?.Invoke(gridLayout, null);

            MelonLogger.Msg($"[WeaponShuffle] Reordered {entries.Count} weapon items by tier");
        }

        public override void OnFixedUpdate()
        {
            if (_shuffleApplied) return;

            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null) return;

                var registry = gaInstance.GetType()
                    .GetProperty("_dataRegistry", HarmonyPatcher.FLAGS)?
                    .GetValue(gaInstance);
                if (registry == null) return;

                var weaponArray = registry.GetType()
                    .GetProperty("_simWeaponData", HarmonyPatcher.FLAGS)?
                    .GetValue(registry);
                if (weaponArray == null) return;

                ShuffleWeaponTiers(weaponArray);
                _shuffleApplied = true;
            }
            catch { }
        }

        private static void ShuffleWeaponTiers(object weaponArray)
        {
            var weapons = new List<WeaponEntry>();

            foreach (var weapon in IL2CppArrayHelper.Iterate(weaponArray))
            {
                string name = weapon.GetType()
                    .GetProperty("name", HarmonyPatcher.FLAGS)?
                    .GetValue(weapon)?.ToString() ?? "";

                if (!name.Contains("CloseRange") &&
                    !name.Contains("MediumRange") &&
                    !name.Contains("LongRange"))
                    continue;

                var coreProp = weapon.GetType()
                    .GetProperty("RequiredWeaponCore", HarmonyPatcher.FLAGS);
                if (coreProp == null || !coreProp.CanRead || !coreProp.CanWrite)
                    continue;

                int coreLevel = (int)(coreProp.GetValue(weapon) ?? -1);
                if (coreLevel < 0) continue;

                int typeId = GetItemTypeId(weapon);
                // Store the actual TypeID object so we can use it for translation later
                var typeIdObj = weapon.GetType().GetProperty("TypeID", HarmonyPatcher.FLAGS)?.GetValue(weapon);
                if (typeIdObj != null && typeId >= 0)
                    _typeIdObjects[typeId] = typeIdObj;
                weapons.Add(new WeaponEntry(weapon, coreProp, name, coreLevel, typeId));
            }

            if (weapons.Count < 2) return;

            // Record original position order (sorted by original core level, then TypeID for stability)
            var originalOrder = weapons
                .OrderBy(w => w.OriginalCoreLevel)
                .ThenBy(w => w.TypeId)
                .Select(w => w.TypeId)
                .ToList();

            // Shuffle core levels
            var coreLevels = weapons.Select(w => w.OriginalCoreLevel).ToList();
            FisherYatesShuffle(coreLevels);

            for (int i = 0; i < weapons.Count; i++)
            {
                var w = weapons[i];
                int newLevel = coreLevels[i];
                w.CoreProp.SetValue(w.Weapon, newLevel);

                if (newLevel != w.OriginalCoreLevel)
                    MelonLogger.Msg($"[WeaponShuffle]   {w.Name}: core {w.OriginalCoreLevel} -> {newLevel}");
            }

            // Record shuffled position order (sorted by new core level, then TypeID)
            var shuffledOrder = weapons
                .OrderBy(w => coreLevels[weapons.IndexOf(w)])
                .ThenBy(w => w.TypeId)
                .Select(w => w.TypeId)
                .ToList();

            // Build position-based mapping: original[i] → shuffled[i]
            _typeIdMapping.Clear();
            for (int i = 0; i < originalOrder.Count; i++)
            {
                if (originalOrder[i] != shuffledOrder[i])
                {
                    _typeIdMapping[originalOrder[i]] = shuffledOrder[i];
                    MelonLogger.Msg($"[WeaponShuffle]   Mapping: TypeID {originalOrder[i]} -> {shuffledOrder[i]}");
                }
            }

            MelonLogger.Msg($"[WeaponShuffle] Shuffled {weapons.Count} weapons, {_typeIdMapping.Count} translations");
        }

        private static void FisherYatesShuffle<T>(List<T> list)
        {
            var rng = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private record WeaponEntry(object Weapon, PropertyInfo CoreProp, string Name, int OriginalCoreLevel, int TypeId);
    }
}
