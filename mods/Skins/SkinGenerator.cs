using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;
using SiroccoMod.Helpers;

namespace SiroccoMod.Mods.Skins
{
    /// <summary>
    /// Pure generation logic — picks elements, palettes, mesh swaps and returns as PlayerSkinInfo list.
    /// Does NOT apply to pool or set network data.
    /// </summary>
    public static class SkinGenerator
    {
        /// Per-player skin data collected during random generation, used to build network sync data.
        internal class PlayerSkinInfo
        {
            public int PlayerIndex;
            public float OverallQuality;
            public int Seed;
            public bool UsesColorPalette;
            public string? ColorPaletteUniversalID;
            public string? ShipUniversalID;
            public float[] ElementQualities = Array.Empty<float>();
            public string[] ElementUniversalIDs = Array.Empty<string>();
            public string[] SubElementUniversalIDs = Array.Empty<string>();
            public string[] MeshSwapUniversalIDs = Array.Empty<string>();
            public int[] MeshSwapSlotIDs = Array.Empty<int>();
        }

        /// <summary>
        /// Generate random skin choices for all players using DataRegistry.
        /// Does NOT apply to pool — just picks elements, palettes, mesh swaps and returns as PlayerSkinInfo list.
        /// </summary>
        internal static List<PlayerSkinInfo> GenerateRandomSkinChoices(GameReflectionBridge reflection)
        {
            var result = new List<PlayerSkinInfo>();
            var gaInstance = reflection.GameAuthorityInstance;
            var gaType = reflection.GameAuthorityType;
            if (gaInstance == null || gaType == null) return result;

            var registry = gaType.GetProperty("_dataRegistry", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
            if (registry == null) { MelonLogger.Warning("[SkinSync] DataRegistry is null"); return result; }

            // Build name→GUID lookup from _UniversalIdToMappingsDict
            // Each entry has: AssociatedData (BackendReferenceable), Name, UniversalID (GUID)
            var nameToGuid = new Dictionary<string, string>();
            try
            {
                var dictProp = registry.GetType().GetProperty("_UniversalIdToMappingsDict", HarmonyPatcher.FLAGS);
                var dict = dictProp?.GetValue(registry);
                if (dict != null)
                {
                    var valuesProp = dict.GetType().GetProperty("Values");
                    var values = valuesProp?.GetValue(dict);
                    if (values != null)
                    {
                        var getEnum = values.GetType().GetMethod("GetEnumerator");
                        var enumerator = getEnum?.Invoke(values, null);
                        var moveNext = enumerator?.GetType().GetMethod("MoveNext");
                        var currentProp = enumerator?.GetType().GetProperty("Current");

                        while (moveNext != null && (bool)moveNext.Invoke(enumerator, null)!)
                        {
                            var entry = currentProp?.GetValue(enumerator);
                            if (entry == null) continue;

                            string? uid = entry.GetType().GetProperty("UniversalID", HarmonyPatcher.FLAGS)?.GetValue(entry)?.ToString();
                            string? entryName = entry.GetType().GetProperty("Name", HarmonyPatcher.FLAGS)?.GetValue(entry)?.ToString();
                            if (uid == null || entryName == null) continue;

                            nameToGuid[entryName] = uid;
                        }
                    }
                }
                SkinSystem.NameToGuidMap = nameToGuid;
                MelonLogger.Msg($"[SkinSync] Built name→GUID lookup: {nameToGuid.Count} entries");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SkinSync] Dict enumeration failed: {ex.Message}");
            }

            // Find Sampan's GUID
            string? sampanUniversalID = null;
            string? sampanEntityName = GetSampanEntityName(registry);
            if (sampanEntityName != null && nameToGuid.TryGetValue(sampanEntityName, out var sampanGuidVal))
                sampanUniversalID = sampanGuidVal;

            // Also try without "Ship_Player_" prefix — might be stored as just "Sampan"
            if (sampanUniversalID == null)
            {
                foreach (var kv in nameToGuid)
                {
                    if (kv.Key.Contains("Sampan", StringComparison.OrdinalIgnoreCase))
                    {
                        MelonLogger.Msg($"[SkinSync]   Sampan match: '{kv.Key}' → '{kv.Value}'");
                        sampanUniversalID = kv.Value;
                    }
                }
            }
            MelonLogger.Msg($"[SkinSync] Sampan: name='{sampanEntityName}', GUID='{sampanUniversalID}'");

            if (string.IsNullOrEmpty(sampanUniversalID)) { MelonLogger.Warning("[SkinSync] No Sampan GUID found"); return result; }

            // Log a few element GUIDs for reference
            foreach (var kv in nameToGuid)
            {
                if (kv.Key.StartsWith("Element_") || kv.Key.StartsWith("Color_Palette_") || kv.Key.StartsWith("MS_"))
                {
                    MelonLogger.Msg($"[SkinSync]   '{kv.Key}' → '{kv.Value}'");
                    break; // just one example
                }
            }

            var skinElements = registry.GetType().GetProperty("_skinElementReferences", HarmonyPatcher.FLAGS)?.GetValue(registry);
            var skinSubElements = registry.GetType().GetProperty("_skinSubElementReferences", HarmonyPatcher.FLAGS)?.GetValue(registry);
            var skinPalettes = registry.GetType().GetProperty("_skinColorPaletteReferences", HarmonyPatcher.FLAGS)?.GetValue(registry);

            int elemCount = IL2CppArrayHelper.GetLength(skinElements);
            int subElemCount = IL2CppArrayHelper.GetLength(skinSubElements);
            int paletteCount = IL2CppArrayHelper.GetLength(skinPalettes);
            if (elemCount == 0 || subElemCount == 0) return result;

            var elemItem = skinElements!.GetType().GetProperty("Item");
            var subElemItem = skinSubElements!.GetType().GetProperty("Item");
            var palItem = skinPalettes!.GetType().GetProperty("Item");

            // Hardcoded Sampan slot types
            int slotCount = 4;
            int[] slotTypesByIndex = { 2, 4, 8, 8 };

            // Get mesh swap data
            var allMeshSwaps = registry.GetType().GetProperty("_meshSwapReferences", HarmonyPatcher.FLAGS)?.GetValue(registry);
            var sampanMeshSwaps = FindSampanMeshSwapsSimple(allMeshSwaps, registry);

            // Get player count from mappings
            var mappings = gaType.GetMethod("GetPlayerConnectionMappings", HarmonyPatcher.FLAGS)?.Invoke(gaInstance, null);
            if (mappings == null) return result;
            int mappingCount = IL2CppArrayHelper.GetLength(mappings);
            var mappingItem = mappings.GetType().GetProperty("Item");

            long minutesSinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            int sharedSeed = (int)(minutesSinceEpoch ^ 0x5DEADBEE);
            var rng = new Random(sharedSeed);

            for (int pi = 0; pi < mappingCount; pi++)
            {
                try
                {
                    var mapping = mappingItem?.GetValue(mappings, new object[] { pi });
                    if (mapping == null) continue;
                    string name = mapping.GetType().GetProperty("DisplayName", HarmonyPatcher.FLAGS)?.GetValue(mapping)?.ToString() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;

                    // Use entity NAME as ShipUniversalID (not the visual prefab GUID)
                    var psi = new PlayerSkinInfo { PlayerIndex = pi, ShipUniversalID = sampanUniversalID };
                    var elemQuals = new float[slotCount];
                    var elemIDs = new string[slotCount];
                    var subElemIDs = new string[slotCount];

                    for (int s = 0; s < slotCount; s++)
                    {
                        int slotFlag = slotTypesByIndex[s];
                        object? elem = PickCompatibleElement(skinElements!, elemItem!, elemCount, slotFlag, rng);
                        if (elem == null)
                            elem = PickCompatibleElement(skinElements!, elemItem!, elemCount, 0, rng);

                        object? subElem = null;
                        if (elem != null)
                        {
                            subElem = elem.GetType().GetProperty("_subElement", HarmonyPatcher.FLAGS)?.GetValue(elem);
                            if (subElem == null)
                            {
                                var subElemsArr = elem.GetType().GetProperty("_subElements", HarmonyPatcher.FLAGS)?.GetValue(elem);
                                if (subElemsArr != null && IL2CppArrayHelper.GetLength(subElemsArr) > 0)
                                    subElem = subElemsArr.GetType().GetProperty("Item")?.GetValue(subElemsArr, new object[] { 0 });
                            }
                        }
                        if (subElem == null)
                        {
                            for (int a = 0; a < 10 && subElem == null; a++)
                                subElem = subElemItem!.GetValue(skinSubElements, new object[] { rng.Next(0, subElemCount) });
                        }

                        float quality = 0.5f + (float)rng.NextDouble() * 0.5f;
                        elemQuals[s] = quality;
                        elemIDs[s] = elem?.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(elem)?.ToString() ?? "";
                        subElemIDs[s] = subElem?.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(subElem)?.ToString() ?? "";
                    }

                    psi.ElementQualities = elemQuals;
                    psi.ElementUniversalIDs = elemIDs;
                    psi.SubElementUniversalIDs = subElemIDs;

                    // Pick palette
                    string palName = "";
                    for (int pa = 0; pa < 20; pa++)
                    {
                        var candidate = palItem!.GetValue(skinPalettes, new object[] { rng.Next(1, paletteCount) });
                        if (candidate == null) continue;
                        string pn = candidate.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(candidate)?.ToString() ?? "";
                        if (pn.Contains("TEST", StringComparison.OrdinalIgnoreCase)) continue;
                        palName = pn;
                        break;
                    }
                    if (string.IsNullOrEmpty(palName))
                    {
                        var fallback = palItem!.GetValue(skinPalettes, new object[] { 1 });
                        palName = fallback?.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(fallback)?.ToString() ?? "";
                    }

                    psi.UsesColorPalette = true;
                    psi.ColorPaletteUniversalID = palName;
                    psi.OverallQuality = 0.5f + (float)rng.NextDouble() * 0.5f;
                    psi.Seed = rng.Next();

                    // Pick mesh swaps (simplified — group by slot, 50% chance each)
                    if (sampanMeshSwaps.Count > 0)
                    {
                        var bySlot = new Dictionary<int, List<object>>();
                        foreach (var ms in sampanMeshSwaps)
                        {
                            int sid = -1;
                            try { sid = (int)(ms.GetType().GetProperty("MeshSwapSlotID", HarmonyPatcher.FLAGS)?.GetValue(ms) ?? -1); } catch { }
                            if (!bySlot.ContainsKey(sid)) bySlot[sid] = new();
                            bySlot[sid].Add(ms);
                        }
                        var msNames = new List<string>();
                        var msSlotIds = new List<int>();
                        foreach (var (slotId, options) in bySlot)
                        {
                            if (rng.NextDouble() < 0.5)
                            {
                                var pick = options[rng.Next(options.Count)];
                                msNames.Add(pick.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(pick)?.ToString() ?? "");
                                msSlotIds.Add(slotId);
                            }
                        }
                        psi.MeshSwapUniversalIDs = msNames.ToArray();
                        psi.MeshSwapSlotIDs = msSlotIds.ToArray();
                    }

                    result.Add(psi);
                    MelonLogger.Msg($"[SkinSync] Generated skin for player {pi} '{name}': palette='{palName}' elements=[{string.Join(", ", elemIDs)}]");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[SkinSync] Player {pi} generation error: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>Simplified mesh swap finder — doesn't need pool, just checks AssociatedPlayerShip name.</summary>
        internal static List<object> FindSampanMeshSwapsSimple(object? meshSwapArray, object registry)
        {
            var result = new List<object>();
            if (meshSwapArray == null) return result;
            try
            {
                int count = IL2CppArrayHelper.GetLength(meshSwapArray);
                var item = meshSwapArray.GetType().GetProperty("Item");
                for (int i = 0; i < count; i++)
                {
                    var container = item?.GetValue(meshSwapArray, new object[] { i });
                    if (container == null) continue;
                    var assocShip = container.GetType().GetProperty("AssociatedPlayerShip", HarmonyPatcher.FLAGS)?.GetValue(container);
                    if (assocShip == null) continue;
                    string shipName = assocShip.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(assocShip)?.ToString() ?? "";
                    if (shipName.Contains("Sampan", StringComparison.OrdinalIgnoreCase))
                        result.Add(container);
                }
            }
            catch { }
            return result;
        }

        /// <summary>Returns the Sampan entity's name (universal ID for DataRegistry lookup).</summary>
        internal static string? GetSampanEntityName(object registry)
        {
            var entityArray = registry.GetType().GetProperty("_simEntityData", HarmonyPatcher.FLAGS)?.GetValue(registry);
            if (entityArray == null) return null;
            int count = IL2CppArrayHelper.GetLength(entityArray);
            var item = entityArray.GetType().GetProperty("Item");

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var entity = item?.GetValue(entityArray, new object[] { i });
                    if (entity == null) continue;
                    string name = entity.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(entity)?.ToString() ?? "";
                    if (name.Contains("Sampan", StringComparison.OrdinalIgnoreCase))
                        return name;
                }
                catch { }
            }
            return null;
        }

        internal static object? PickCompatibleElement(object elemArray, PropertyInfo elemItem,
            int elemCount, int slotFlag, Random rng)
        {
            if (slotFlag == 0) // No type restriction — pick any non-null
            {
                for (int a = 0; a < 10; a++)
                {
                    var e = elemItem.GetValue(elemArray, new object[] { rng.Next(0, elemCount) });
                    if (e != null) return e;
                }
                return null;
            }

            // Build list of compatible elements
            var compatible = new List<object>();
            for (int i = 0; i < elemCount; i++)
            {
                var e = elemItem.GetValue(elemArray, new object[] { i });
                if (e == null) continue;
                var typeVal = e.GetType().GetProperty("_elementType", HarmonyPatcher.FLAGS)?.GetValue(e);
                if (typeVal != null && ((int)typeVal & slotFlag) != 0)
                    compatible.Add(e);
            }

            if (compatible.Count == 0) return null;
            return compatible[rng.Next(compatible.Count)];
        }

        /// <summary>Translate asset name to backend GUID using the cached name→GUID map. Returns original if not found.</summary>
        internal static string TranslateToGuid(string? name, Dictionary<string, string> guidMap)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return guidMap.TryGetValue(name, out var guid) ? guid : name;
        }
    }
}
