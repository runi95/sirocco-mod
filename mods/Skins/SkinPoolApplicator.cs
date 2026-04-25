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
    /// Pool application — applies skins to ship pool objects locally.
    /// </summary>
    public static class SkinPoolApplicator
    {
        public static void Prefix_PostSkinningFinishInit()
        {
            if (SkinSystem.HasRun) return;
            SkinSystem.MarkHasRun();

            // If we already set _playerSkinData (host), let the native
            // SkinAllPlayableShipSkinsWithEquippedSkins coroutine handle application.
            // It runs after PostSkinningFinishInit and waits for the ship pool GUID
            // lookup to be ready (our early calls fail with KeyNotFoundException).
            if (SkinSystem.NetworkSkinDataSet)
            {
                MelonLogger.Msg("[SkinApply] PostSkinningFinishInit — _playerSkinData set, letting native coroutine apply skins");
                return;
            }

            MelonLogger.Msg("[SkinApply] PostSkinningFinishInit intercepted — applying skins NOW...");

            try
            {
                var reflection = new GameReflectionBridge();
                if (reflection.IsValid)
                    ApplyRandomSkins(reflection);
                else
                    MelonLogger.Warning("[SkinApply] GameReflectionBridge not valid at this point");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SkinApply] Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Try to apply skins from server-synced _playerSkinData on SimulationManager.
        /// Uses ApplySkinToPlayersShipPool(int, string, NetworkedSkinData) which takes
        /// network skin data directly — no need to reconstruct OldUnitySkinData.
        /// Returns true if server skin data was found and applied.
        /// </summary>
        internal static bool TryApplyServerSkinData(GameReflectionBridge reflection)
        {
            var gaInstance = reflection.GameAuthorityInstance;
            var gaType = reflection.GameAuthorityType;
            if (gaInstance == null || gaType == null) return false;

            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (asm == null) return false;

                var pool = gaType.GetProperty("_playerShipsObjectPool", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
                if (pool == null) return false;

                var simManager = gaType.GetProperty("_simulationManager", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
                if (simManager == null) return false;

                var skinDataArr = simManager.GetType().GetProperty("_playerSkinData", HarmonyPatcher.FLAGS)?.GetValue(simManager);
                if (skinDataArr == null) { MelonLogger.Msg("[SkinApply] _playerSkinData is null"); return false; }

                int totalSlots = IL2CppArrayHelper.GetLength(skinDataArr);
                MelonLogger.Msg($"[SkinApply] _playerSkinData has {totalSlots} slots");
                if (totalSlots == 0) return false;

                var pnsdItem = skinDataArr.GetType().GetProperty("Item");
                if (pnsdItem == null) return false;

                // Find the NetworkedSkinData type to resolve the correct overload
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });
                var nsdType = allTypes.FirstOrDefault(t => t.Name == "NetworkedSkinData" && t.Namespace?.Contains("Mirror") == true);
                if (nsdType == null) { MelonLogger.Warning("[SkinApply] NetworkedSkinData type not found"); return false; }

                var applyMethod = pool.GetType().GetMethod("ApplySkinToPlayersShipPool", HarmonyPatcher.FLAGS,
                    null, new[] { typeof(int), typeof(string), nsdType }, null);
                if (applyMethod == null)
                {
                    MelonLogger.Warning("[SkinApply] ApplySkinToPlayersShipPool(int, string, NetworkedSkinData) method not found");
                    return false;
                }

                int appliedCount = 0;
                int nonEmptyCount = 0;
                for (int i = 0; i < totalSlots; i++)
                {
                    try
                    {
                        var pnsd = pnsdItem.GetValue(skinDataArr, new object[] { i });
                        if (pnsd == null) { MelonLogger.Msg($"[SkinApply]   slot[{i}]: null"); continue; }

                        var playerIndex = pnsd.GetType().GetProperty("PlayerIndex", HarmonyPatcher.FLAGS)?.GetValue(pnsd);
                        if (playerIndex == null) continue;
                        int idx = (int)playerIndex;

                        var networkedSkins = pnsd.GetType().GetProperty("NetworkedSkins", HarmonyPatcher.FLAGS)?.GetValue(pnsd);
                        if (networkedSkins == null) { MelonLogger.Msg($"[SkinApply]   slot[{i}] idx={idx}: NetworkedSkins=null"); continue; }
                        int skinCount = IL2CppArrayHelper.GetLength(networkedSkins);
                        if (skinCount == 0) { MelonLogger.Msg($"[SkinApply]   slot[{i}] idx={idx}: NetworkedSkins empty"); continue; }
                        nonEmptyCount++;

                        var nsdItem = networkedSkins.GetType().GetProperty("Item");
                        var nsd = nsdItem?.GetValue(networkedSkins, new object[] { 0 });
                        if (nsd == null) continue;

                        var shipId = nsd.GetType().GetProperty("ShipUniversalID", HarmonyPatcher.FLAGS)?.GetValue(nsd)?.ToString() ?? "";
                        if (string.IsNullOrEmpty(shipId)) continue;

                        applyMethod.Invoke(pool, new object[] { idx, shipId, nsd });
                        appliedCount++;

                        var palette = nsd.GetType().GetProperty("ColorPaletteUniversalID", HarmonyPatcher.FLAGS)?.GetValue(nsd)?.ToString() ?? "";
                        var elemCount = nsd.GetType().GetProperty("ElementCount", HarmonyPatcher.FLAGS)?.GetValue(nsd);
                        MelonLogger.Msg($"[SkinApply] Applied server skin to player {idx}: ship='{shipId}' palette='{palette}' elements={elemCount}");
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException?.Message ?? ex.Message;
                        MelonLogger.Warning($"[SkinApply] Failed to apply server skin for slot {i}: {inner}");
                    }
                }

                MelonLogger.Msg($"[SkinApply] Server skin data: {nonEmptyCount} non-empty, {appliedCount} applied");
                return appliedCount > 0;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SkinApply] TryApplyServerSkinData error: {ex.Message}");
                return false;
            }
        }

        internal static void ApplyRandomSkins(GameReflectionBridge reflection)
        {
            var gaInstance = reflection.GameAuthorityInstance;
            var gaType = reflection.GameAuthorityType;
            if (gaInstance == null || gaType == null) return;

            MelonLogger.Msg("[SkinApply] Applying random skins (shared seed for host/client sync)...");

            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (asm == null) return;

                // Get pool
                var pool = gaType.GetProperty("_playerShipsObjectPool", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
                if (pool == null) { MelonLogger.Warning("[SkinApply] Pool is null"); return; }

                // Get DataRegistry
                var registry = gaType.GetProperty("_dataRegistry", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
                if (registry == null) { MelonLogger.Warning("[SkinApply] Registry is null"); return; }

                // Get Sampan visual GUID
                string? sampanGuid = GetSampanGuid(registry);
                if (sampanGuid == null) { MelonLogger.Warning("[SkinApply] No Sampan GUID"); return; }

                // Get skin data from DataRegistry
                var skinElements = registry.GetType().GetProperty("_skinElementReferences", HarmonyPatcher.FLAGS)?.GetValue(registry);
                var skinSubElements = registry.GetType().GetProperty("_skinSubElementReferences", HarmonyPatcher.FLAGS)?.GetValue(registry);
                var skinPalettes = registry.GetType().GetProperty("_skinColorPaletteReferences", HarmonyPatcher.FLAGS)?.GetValue(registry);

                // Get the Sampan's valid mesh swap slot IDs
                var validMeshSlotIds = GetValidMeshSwapSlotIds(pool, sampanGuid, asm);
                // Fallback: if detection failed, use known Sampan slots.
                if (validMeshSlotIds.Count == 0)
                {
                    validMeshSlotIds.Add(1); // Harpoon
                    validMeshSlotIds.Add(2); // Sail
                    validMeshSlotIds.Add(3); // Trim
                }
                MelonLogger.Msg($"[SkinApply] Valid mesh swap slot IDs: [{string.Join(", ", validMeshSlotIds)}]");

                // Get mesh swap containers and find Sampan-compatible ones
                var allMeshSwaps = registry.GetType().GetProperty("_meshSwapReferences", HarmonyPatcher.FLAGS)?.GetValue(registry);
                var sampanMeshSwaps = FindSampanMeshSwaps(allMeshSwaps, registry, validMeshSlotIds, asm);
                MelonLogger.Msg($"[SkinApply] Sampan mesh swaps: {sampanMeshSwaps.Count}");

                int elemCount = IL2CppArrayHelper.GetLength(skinElements);
                int subElemCount = IL2CppArrayHelper.GetLength(skinSubElements);
                int paletteCount = IL2CppArrayHelper.GetLength(skinPalettes);
                MelonLogger.Msg($"[SkinApply] DataRegistry: {elemCount} elements, {subElemCount} subElements, {paletteCount} palettes");

                if (elemCount == 0 || subElemCount == 0) return;

                // Find SkinSlotRegistry from ALL objects (including inactive pool ships)
                int slotCount = 0;
                int[] slotTypesByIndex = Array.Empty<int>();
                var registryType = asm.GetType("Il2CppWartide.SkinSlotRegistry");
                if (registryType != null)
                {
                    var resources = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("UnityEngine.Resources", throwOnError: false))
                        .FirstOrDefault(t => t != null);
                    var findAll = resources?.GetMethod("FindObjectsOfTypeAll",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);
                    var allRegs = findAll?.Invoke(null, new object[] { registryType }) as Array;

                    MelonLogger.Msg($"[SkinApply] FindObjectsOfTypeAll<SkinSlotRegistry>: {allRegs?.Length ?? 0}");

                    if (allRegs != null && allRegs.Length > 0)
                    {
                        // Use the first one to get slot info
                        var reg = allRegs.GetValue(0);
                        var getSlotLen = reg?.GetType().GetMethod("GetSlotLength", HarmonyPatcher.FLAGS);
                        slotCount = (int)(getSlotLen?.Invoke(reg, null) ?? 0);

                        var getSlots = reg?.GetType().GetMethod("GetElementSlots", HarmonyPatcher.FLAGS);
                        var slots = getSlots?.Invoke(reg, null);
                        if (slots != null)
                        {
                            int sLen = IL2CppArrayHelper.GetLength(slots);
                            slotTypesByIndex = new int[sLen];
                            var slotItem = slots.GetType().GetProperty("Item");
                            for (int si = 0; si < sLen; si++)
                            {
                                var slot = slotItem?.GetValue(slots, new object[] { si });
                                if (slot == null) continue;
                                string slotName = slot.GetType().GetProperty("SlotName", HarmonyPatcher.FLAGS)?.GetValue(slot)?.ToString() ?? "?";
                                var typesVal = slot.GetType().GetProperty("PossibleElementTypes", HarmonyPatcher.FLAGS)?.GetValue(slot);
                                int types = typesVal != null ? (int)typesVal : 0;
                                slotTypesByIndex[si] = types;
                                MelonLogger.Msg($"[SkinApply]   Slot[{si}] '{slotName}' types={types} (Wood={types & 2}, Metal={types & 4}, Cloth={types & 8}, Ornate={types & 16})");
                            }
                        }
                    }
                }

                // Fallback: hardcode Sampan slot types from empirical testing
                // (FindObjectsOfTypeAll can't find SkinSlotRegistry due to IL2CPP type mapping)
                if (slotCount == 0 || slotTypesByIndex.Length == 0 || slotTypesByIndex.All(t => t == 0))
                {
                    slotCount = 4;
                    // From ShipVisual_Sampan.prefab SkinSlotRegistry._slots:
                    //   Slot[0] "Wood"  PossibleElementTypes=2 (Wood)
                    //   Slot[1] "Metal" PossibleElementTypes=4 (Metal)
                    //   Slot[2] "Cowl"  PossibleElementTypes=8 (Cloth)
                    //   Slot[3] "Sail"  PossibleElementTypes=8 (Cloth, isSailSlot=true)
                    slotTypesByIndex = new[] { 2, 4, 8, 8 };
                    MelonLogger.Msg("[SkinApply] Sampan slot types from prefab: [Wood=2, Metal=4, Cloth=8, Cloth=8]");

                    // Log element type counts for verification (after elemItem is declared below)
                }

                if (slotCount == 0)
                    slotCount = GetSlotCountFromPool(pool, sampanGuid, asm);
                MelonLogger.Msg($"[SkinApply] Ship slot count: {slotCount}");
                if (slotCount <= 0) return;

                // Get types we need
                var skinSubElemDataType = asm.GetType("Il2CppWartide.SkinSubElementData");
                var skinDataType = asm.GetType("Il2CppWartide.OldUnitySkinData");
                if (skinSubElemDataType == null || skinDataType == null) return;

                // Get player mappings
                var mappings = gaType.GetMethod("GetPlayerConnectionMappings", HarmonyPatcher.FLAGS)?.Invoke(gaInstance, null);
                int mappingCount = mappings != null ? IL2CppArrayHelper.GetLength(mappings) : 0;
                var mappingItem = mappings?.GetType().GetProperty("Item");

                MelonLogger.Msg($"[SkinApply] mappingCount={mappingCount}");

                // Fallback: if no mappings at all, iterate all 10 player slots
                if (mappingCount == 0)
                {
                    MelonLogger.Msg("[SkinApply] No mappings — applying skins for all 10 player slots");
                    mappingCount = 10;
                }

                // Use a shared deterministic seed so host and client generate identical skins.
                // Round UTC time to the nearest minute — both machines start within seconds of each other.
                long minutesSinceEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                int sharedSeed = (int)(minutesSinceEpoch ^ 0x5DEADBEE);
                MelonLogger.Msg($"[SkinApply] Shared RNG seed: {sharedSeed} (minute={minutesSinceEpoch})");
                var rng = new Random(sharedSeed);
                var elemItem = skinElements!.GetType().GetProperty("Item");
                var subElemItem = skinSubElements!.GetType().GetProperty("Item");
                var palItem = skinPalettes!.GetType().GetProperty("Item");

                // Collect per-player skin info for network sync
                var allPlayerSkins = new List<SkinGenerator.PlayerSkinInfo>();

                // Log element type distribution
                {
                    var typeCounts = new Dictionary<int, int>();
                    for (int ei = 0; ei < elemCount; ei++)
                    {
                        var el = elemItem?.GetValue(skinElements, new object[] { ei });
                        if (el == null) continue;
                        var etv = el.GetType().GetProperty("_elementType", HarmonyPatcher.FLAGS)?.GetValue(el);
                        int et = etv != null ? (int)etv : 0;
                        if (!typeCounts.ContainsKey(et)) typeCounts[et] = 0;
                        typeCounts[et]++;
                    }
                    foreach (var kv in typeCounts)
                        MelonLogger.Msg($"[SkinApply] ElementType {kv.Key}: {kv.Value} elements");
                }

                // Start from index 1 — slot 0 is always unused and applying to it
                // wastes RNG calls on retries, desyncing host/client sequences.
                for (int pi = 1; pi < mappingCount; pi++)
                {
                    try
                    {
                        string name = $"Player_{pi}";
                        if (mappingItem != null && mappings != null && pi < IL2CppArrayHelper.GetLength(mappings))
                        {
                            var mapping = mappingItem.GetValue(mappings, new object[] { pi });
                            if (mapping != null)
                            {
                                var displayName = mapping.GetType().GetProperty("DisplayName", HarmonyPatcher.FLAGS)?.GetValue(mapping)?.ToString();
                                if (!string.IsNullOrEmpty(displayName))
                                    name = displayName;
                            }
                        }

                        // Build SkinSubElementData array sized to slot count
                        var arrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<>).MakeGenericType(skinSubElemDataType);
                        var subElemArr = arrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)slotCount });
                        var arrItemProp = arrType.GetProperty("Item");

                        int playerIndex = pi;
                        var psi = new SkinGenerator.PlayerSkinInfo { PlayerIndex = playerIndex, ShipUniversalID = sampanGuid };
                        var elemQuals = new float[slotCount];
                        var elemIDs = new string[slotCount];
                        var subElemIDs = new string[slotCount];

                        for (int s = 0; s < slotCount; s++)
                        {
                            var entry = skinSubElemDataType.GetConstructor(Type.EmptyTypes)!.Invoke(null);

                            // Use the actual slot type if we discovered it, else fallback to random
                            int slotFlag = (s < slotTypesByIndex.Length) ? slotTypesByIndex[s] : 0;
                            object? elem = SkinGenerator.PickCompatibleElement(skinElements!, elemItem!, elemCount, slotFlag, rng);

                            // Fallback: try other types if this one has no elements
                            if (elem == null)
                                elem = SkinGenerator.PickCompatibleElement(skinElements!, elemItem!, elemCount, 0, rng);

                            // Use the element's own sub-element
                            object? subElem = null;
                            if (elem != null)
                            {
                                // Try _subElement first (single default)
                                subElem = elem.GetType().GetProperty("_subElement", HarmonyPatcher.FLAGS)?.GetValue(elem);

                                // If null, try _subElements array (most elements use this)
                                if (subElem == null)
                                {
                                    var subElemsArr = elem.GetType().GetProperty("_subElements", HarmonyPatcher.FLAGS)?.GetValue(elem);
                                    if (subElemsArr != null && IL2CppArrayHelper.GetLength(subElemsArr) > 0)
                                        subElem = subElemsArr.GetType().GetProperty("Item")?.GetValue(subElemsArr, new object[] { 0 });
                                }
                            }

                            // Last resort: random sub-element
                            if (subElem == null)
                            {
                                for (int a = 0; a < 10 && subElem == null; a++)
                                    subElem = subElemItem!.GetValue(skinSubElements, new object[] { rng.Next(0, subElemCount) });
                            }

                            if (elem != null)
                                skinSubElemDataType.GetProperty("Element", HarmonyPatcher.FLAGS)?.SetValue(entry, elem);
                            if (subElem != null)
                                skinSubElemDataType.GetProperty("SubElement", HarmonyPatcher.FLAGS)?.SetValue(entry, subElem);
                            float quality = 0.5f + (float)rng.NextDouble() * 0.5f;
                            skinSubElemDataType.GetProperty("ElementQuality", HarmonyPatcher.FLAGS)?.SetValue(entry, quality);

                            // Extract universal IDs (asset names) for network sync
                            string elemName = elem?.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(elem)?.ToString() ?? "";
                            string subName = subElem?.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(subElem)?.ToString() ?? "";

                            // Also set the universal ID fields on SkinSubElementData
                            if (!string.IsNullOrEmpty(elemName))
                                skinSubElemDataType.GetProperty("SkinElementUniversalID", HarmonyPatcher.FLAGS)?.SetValue(entry, elemName);
                            if (!string.IsNullOrEmpty(subName))
                                skinSubElemDataType.GetProperty("SkinSubElementUniversalID", HarmonyPatcher.FLAGS)?.SetValue(entry, subName);

                            elemQuals[s] = quality;
                            elemIDs[s] = elemName;
                            subElemIDs[s] = subName;

                            int actualElemType = 0;
                            if (elem != null)
                            {
                                var etVal = elem.GetType().GetProperty("_elementType", HarmonyPatcher.FLAGS)?.GetValue(elem);
                                if (etVal != null) actualElemType = (int)etVal;
                            }
                            MelonLogger.Msg($"[SkinApply]   Slot[{s}]: slotType={slotFlag} elemType={actualElemType} elem='{elemName}' sub='{subName}' q={quality:F2}");

                            arrItemProp!.SetValue(subElemArr, entry, new object[] { s });
                        }

                        psi.ElementQualities = elemQuals;
                        psi.ElementUniversalIDs = elemIDs;
                        psi.SubElementUniversalIDs = subElemIDs;

                        // Pick random non-test color palette per player
                        object? palette = null;
                        string palName = "";
                        for (int pa = 0; pa < 20 && palette == null; pa++)
                        {
                            int idx = rng.Next(1, paletteCount);
                            var candidate = palItem!.GetValue(skinPalettes, new object[] { idx });
                            if (candidate == null) continue;
                            string pn = candidate.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(candidate)?.ToString() ?? "";
                            if (pn.Contains("TEST", StringComparison.OrdinalIgnoreCase)) continue;
                            palette = candidate;
                            palName = pn;
                        }
                        if (palette == null)
                        {
                            palette = palItem!.GetValue(skinPalettes, new object[] { 1 });
                            palName = palette?.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(palette)?.ToString() ?? "";
                        }
                        int skinSeed = rng.Next();
                        float overallQuality = 0.5f + (float)rng.NextDouble() * 0.5f;

                        psi.UsesColorPalette = true;
                        psi.ColorPaletteUniversalID = palName;
                        psi.OverallQuality = overallQuality;
                        psi.Seed = skinSeed;

                        // Build OldUnitySkinData
                        var skinData = skinDataType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
                        skinDataType.GetProperty("ShipVisualPrefabGuid", HarmonyPatcher.FLAGS)?.SetValue(skinData, sampanGuid);
                        skinDataType.GetProperty("Seed", HarmonyPatcher.FLAGS)?.SetValue(skinData, skinSeed);
                        MelonLogger.Msg($"[SkinApply] Player {pi} '{name}': palette='{palName}' seed={skinSeed}");
                        skinDataType.GetProperty("OverallQuality", HarmonyPatcher.FLAGS)?.SetValue(skinData, overallQuality);
                        skinDataType.GetProperty("UsesColorPalette", HarmonyPatcher.FLAGS)?.SetValue(skinData, true);
                        skinDataType.GetProperty("SkinColorPaletteData", HarmonyPatcher.FLAGS)?.SetValue(skinData, palette);
                        skinDataType.GetProperty("SkinSubElementAppliedData", HarmonyPatcher.FLAGS)?.SetValue(skinData, subElemArr);
                        // Don't include mesh swaps in OldUnitySkinData — ApplyMeshSwap destroys
                        // MeshSwapBaseObjects after each swap, and the game's preset skin deck
                        // (SkinAllPlayableShipSkinsWithPresetSkinDeck) also applies mesh swaps.
                        // Setting HasMeshSwaps here would consume the base objects before the
                        // preset deck runs, causing "TargetPartID not found" errors.
                        var meshSwapDataType = asm!.GetType("Il2CppWartide.MeshSwapData");
                        {
                            skinDataType.GetProperty("HasMeshSwaps", HarmonyPatcher.FLAGS)?.SetValue(skinData, false);
                            if (meshSwapDataType != null)
                            {
                                var meshArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<>).MakeGenericType(meshSwapDataType);
                                skinDataType.GetProperty("MeshSwaps", HarmonyPatcher.FLAGS)?.SetValue(skinData,
                                    meshArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { 0L }));
                            }
                        }

                        // Apply via pool — retry with different random data on failure
                        var applyMethod = pool.GetType().GetMethod("ApplySkinPresetToPlayersShipPool", HarmonyPatcher.FLAGS);
                        bool applied = false;
                        for (int attempt = 0; attempt < 5 && !applied; attempt++)
                        {
                            try
                            {
                                if (attempt > 0)
                                {
                                    // Rebuild with new random values
                                    skinDataType.GetProperty("Seed", HarmonyPatcher.FLAGS)?.SetValue(skinData, rng.Next());
                                    for (int s = 0; s < slotCount; s++)
                                    {
                                        var entry = skinSubElemDataType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
                                        object? e2 = null;
                                        for (int a = 0; a < 10 && e2 == null; a++)
                                            e2 = elemItem!.GetValue(skinElements, new object[] { rng.Next(0, elemCount) });
                                        object? se2 = null;
                                        for (int a = 0; a < 10 && se2 == null; a++)
                                            se2 = subElemItem!.GetValue(skinSubElements, new object[] { rng.Next(0, subElemCount) });
                                        if (e2 != null) skinSubElemDataType.GetProperty("Element", HarmonyPatcher.FLAGS)?.SetValue(entry, e2);
                                        if (se2 != null) skinSubElemDataType.GetProperty("SubElement", HarmonyPatcher.FLAGS)?.SetValue(entry, se2);
                                        skinSubElemDataType.GetProperty("ElementQuality", HarmonyPatcher.FLAGS)?.SetValue(entry, (float)rng.NextDouble());
                                        arrItemProp!.SetValue(subElemArr, entry, new object[] { s });
                                    }
                                }
                                applyMethod!.Invoke(pool, new object[] { playerIndex, sampanGuid, skinData });
                                applied = true;
                            }
                            catch { }
                        }
                        MelonLogger.Msg(applied
                            ? $"[SkinApply] Applied skin to player {playerIndex} '{name}'!"
                            : $"[SkinApply] Player {playerIndex} '{name}' failed after 5 attempts");

                        allPlayerSkins.Add(psi);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[SkinApply] Player {pi} outer error: {ex.Message}");
                    }
                }

                // Network sync is handled in Postfix_InitializeBackendSourcedData (earlier timing).
                // Only fall back here on the host — clients must never overwrite _playerSkinData
                // or the state mismatch will cause a disconnect.
                if (!SkinSystem.NetworkSkinDataSet && IsServerActive())
                {
                    MelonLogger.Msg("[SkinSync] Fallback: setting network skin data from PostSkinningFinishInit");
                    var simManager = gaType.GetProperty("_simulationManager", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
                    if (simManager != null)
                        SkinNetworkSync.SetNetworkSkinData(simManager, allPlayerSkins, asm!);
                }

                // Find all SkinSlotRegistry in scene — this tells us if visible ships have them
                try
                {
                    var unityObj = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("UnityEngine.Object", throwOnError: false))
                        .FirstOrDefault(t => t != null);
                    var findAll = unityObj?.GetMethod("FindObjectsOfType", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(Type) }, null);
                    var allRegs = findAll?.Invoke(null, new object[] { registryType! }) as Array;
                    MelonLogger.Msg($"[SkinApply] Active SkinSlotRegistry in scene: {allRegs?.Length ?? 0}");

                    // Also find all active GameObjects with "Ship" in name for debugging
                    var goType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("UnityEngine.GameObject", throwOnError: false))
                        .FirstOrDefault(t => t != null);
                    var allGOs = findAll?.Invoke(null, new object[] { goType! }) as Array;
                    if (allGOs != null)
                    {
                        int shipCount = 0;
                        for (int i = 0; i < allGOs.Length; i++)
                        {
                            var go = allGOs.GetValue(i);
                            string goName = go?.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(go)?.ToString() ?? "";
                            if (goName.Contains("Ship") || goName.Contains("Sampan") || goName.Contains("Player"))
                            {
                                if (shipCount < 5) MelonLogger.Msg($"[SkinApply]   GO: '{goName}'");
                                shipCount++;
                            }
                        }
                        MelonLogger.Msg($"[SkinApply] Ship-related GameObjects: {shipCount}");
                    }
                }
                catch { }

                MelonLogger.Msg("[SkinApply] Done!");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SkinApply] Error: {ex}");
            }
        }

        internal static void ApplyToAllSceneRegistries(Assembly asm, object dataRegistry, Random rng)
        {
            try
            {
                var registryType = asm.GetType("Il2CppWartide.SkinSlotRegistry");
                if (registryType == null) return;

                // FindObjectsOfType (active scene objects only, not prefabs)
                var unityObject = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("UnityEngine.Object", throwOnError: false))
                    .FirstOrDefault(t => t != null);
                var findMethod = unityObject?.GetMethod("FindObjectsOfType",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(Type) }, null);
                var allRegistries = findMethod?.Invoke(null, new object[] { registryType }) as Array;

                int count = allRegistries?.Length ?? 0;
                MelonLogger.Msg($"[SkinApply] Found {count} active SkinSlotRegistry in scene");

                if (count == 0) return;

                // Get skin data from DataRegistry
                var skinElements = dataRegistry.GetType().GetProperty("_skinElementReferences", HarmonyPatcher.FLAGS)?.GetValue(dataRegistry);
                var skinSubElements = dataRegistry.GetType().GetProperty("_skinSubElementReferences", HarmonyPatcher.FLAGS)?.GetValue(dataRegistry);
                var skinPalettes = dataRegistry.GetType().GetProperty("_skinColorPaletteReferences", HarmonyPatcher.FLAGS)?.GetValue(dataRegistry);
                int elemCount = IL2CppArrayHelper.GetLength(skinElements);
                int subElemCount = IL2CppArrayHelper.GetLength(skinSubElements);
                int paletteCount = IL2CppArrayHelper.GetLength(skinPalettes);
                var elemItem = skinElements?.GetType().GetProperty("Item");
                var subElemItem = skinSubElements?.GetType().GetProperty("Item");
                var palItem = skinPalettes?.GetType().GetProperty("Item");
                var skinSubElemDataType = asm.GetType("Il2CppWartide.SkinSubElementData");
                if (skinSubElemDataType == null) return;

                // Find the simple ApplySkinToAllSlots overload
                var applyMethod = registryType.GetMethods(HarmonyPatcher.FLAGS)
                    .FirstOrDefault(m => m.Name == "ApplySkinToAllSlots"
                        && m.GetParameters().Length == 5
                        && m.GetParameters()[2].ParameterType.Name.Contains("SkinSubElementData"));

                if (applyMethod == null)
                {
                    MelonLogger.Warning("[SkinApply] ApplySkinToAllSlots(5-param) not found");
                    return;
                }

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var reg = allRegistries!.GetValue(i);
                        if (reg == null) continue;

                        string regName = reg.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(reg)?.ToString() ?? "?";

                        // Get slot count for this specific registry
                        var getSlotLen = reg.GetType().GetMethod("GetSlotLength", HarmonyPatcher.FLAGS);
                        int slotCount = (int)(getSlotLen?.Invoke(reg, null) ?? 0);

                        if (slotCount == 0) continue;

                        // Build element data array
                        var arrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<>).MakeGenericType(skinSubElemDataType!);
                        var subElemArr = arrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)slotCount });
                        var arrItemProp = arrType.GetProperty("Item");

                        for (int s = 0; s < slotCount; s++)
                        {
                            var entry = skinSubElemDataType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
                            var elem = elemItem!.GetValue(skinElements, new object[] { rng.Next(0, elemCount) });
                            var subElem = subElemItem!.GetValue(skinSubElements, new object[] { rng.Next(0, subElemCount) });
                            skinSubElemDataType.GetProperty("Element", HarmonyPatcher.FLAGS)?.SetValue(entry, elem);
                            skinSubElemDataType.GetProperty("SubElement", HarmonyPatcher.FLAGS)?.SetValue(entry, subElem);
                            skinSubElemDataType.GetProperty("ElementQuality", HarmonyPatcher.FLAGS)?.SetValue(entry, (float)rng.NextDouble());
                            arrItemProp!.SetValue(subElemArr, entry, new object[] { s });
                        }

                        // Pick palette
                        var palette = palItem!.GetValue(skinPalettes, new object[] { rng.Next(1, paletteCount) });

                        // Apply!
                        applyMethod.Invoke(reg, new object[] { true, palette!, subElemArr, (float)rng.NextDouble(), rng.Next() });
                        MelonLogger.Msg($"[SkinApply]   Scene registry [{i}] '{regName}' ({slotCount} slots) — applied!");
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException ?? ex;
                        MelonLogger.Msg($"[SkinApply]   Scene registry [{i}] failed: {inner.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SkinApply] Scene registries error: {ex.Message}");
            }
        }

        internal static void ApplyDirectToShip(object pool, int playerIndex, string guid,
            object? palette, object subElemArr, float quality, int seed, Assembly asm)
        {
            try
            {
                // Navigate to the actual ship GameObjects in the inner pool
                var playerPoolProp = pool.GetType().GetProperty("_playerPool", HarmonyPatcher.FLAGS);
                var playerPool = playerPoolProp?.GetValue(pool);
                if (playerPool == null) return;

                var item = playerPool.GetType().GetProperty("Item");
                var entry = item?.GetValue(playerPool, new object[] { playerIndex });
                if (entry == null) return;

                var poolOfShips = entry.GetType().GetProperty("_poolOfShips", HarmonyPatcher.FLAGS)?.GetValue(entry);
                if (poolOfShips == null) return;

                var innerPoolItem = poolOfShips.GetType().GetProperty("Item");
                object? innerPool = null;
                try { innerPool = innerPoolItem?.GetValue(poolOfShips, new object[] { guid }); } catch { return; }
                if (innerPool == null) return;

                var queueProp = innerPool.GetType().GetProperty("_circularQueuePool", HarmonyPatcher.FLAGS);
                var queue = queueProp?.GetValue(innerPool);
                if (queue == null) return;

                int queueLen = IL2CppArrayHelper.GetLength(queue);
                var queueItem = queue.GetType().GetProperty("Item");

                var registryType = asm.GetType("Il2CppWartide.SkinSlotRegistry");
                if (registryType == null) return;

                // Find the ApplySkinToAllSlots overload that takes SkinSubElementData[]
                var applyMethod = registryType.GetMethods(HarmonyPatcher.FLAGS)
                    .FirstOrDefault(m => m.Name == "ApplySkinToAllSlots"
                        && m.GetParameters().Length == 5
                        && m.GetParameters()[2].ParameterType.Name.Contains("SkinSubElementData"));

                for (int qi = 0; qi < queueLen; qi++)
                {
                    try
                    {
                        var shipGo = queueItem?.GetValue(queue, new object[] { qi });
                        if (shipGo == null) continue;

                        // Find SkinSlotRegistry on the ship
                        var getComp = shipGo.GetType().GetMethods(HarmonyPatcher.FLAGS)
                            .FirstOrDefault(m => m.Name == "GetComponentInChildren"
                                && m.GetParameters().Length == 1
                                && m.GetParameters()[0].ParameterType == typeof(Type));

                        object? skinRegistry = null;
                        if (getComp != null)
                            skinRegistry = getComp.Invoke(shipGo, new object[] { registryType });

                        if (skinRegistry == null) continue;

                        if (applyMethod != null)
                        {
                            // ApplySkinToAllSlots(usesColorPalette, colorPalette, elementData, overallQuality, seed)
                            applyMethod.Invoke(skinRegistry, new object[]
                            {
                                palette != null, palette!, subElemArr, quality, seed
                            });
                            MelonLogger.Msg($"[SkinApply]   Direct apply to ship {qi} in player {playerIndex} pool OK");
                        }
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException ?? ex;
                        MelonLogger.Msg($"[SkinApply]   Direct ship {qi} failed: {inner.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[SkinApply]   Direct apply error: {ex.Message}");
            }
        }

        internal static int GetSlotCountFromPool(object pool, string guid, Assembly asm)
        {
            try
            {
                // Access _playerPool[1]._poolOfShips dictionary
                var playerPoolProp = pool.GetType().GetProperty("_playerPool", HarmonyPatcher.FLAGS);
                var playerPool = playerPoolProp?.GetValue(pool);
                if (playerPool == null) return 0;

                int count = IL2CppArrayHelper.GetLength(playerPool);
                var item = playerPool.GetType().GetProperty("Item");

                for (int i = 1; i < count; i++)
                {
                    var entry = item?.GetValue(playerPool, new object[] { i });
                    if (entry == null) continue;

                    // Get the _circularQueuePool from the inner pool via the dictionary
                    var poolOfShips = entry.GetType().GetProperty("_poolOfShips", HarmonyPatcher.FLAGS)?.GetValue(entry);
                    if (poolOfShips == null) continue;

                    // Try to get the inner pool for our GUID
                    // Dictionary<string, PlayerShipInnerPool> — use TryGetValue or indexer
                    var innerPoolItem = poolOfShips.GetType().GetProperty("Item");
                    object? innerPool = null;
                    try { innerPool = innerPoolItem?.GetValue(poolOfShips, new object[] { guid }); } catch { }
                    if (innerPool == null) continue;

                    // Get _circularQueuePool from inner pool
                    var queueProp = innerPool.GetType().GetProperty("_circularQueuePool", HarmonyPatcher.FLAGS);
                    var queue = queueProp?.GetValue(innerPool);
                    if (queue == null) continue;

                    int queueLen = IL2CppArrayHelper.GetLength(queue);
                    if (queueLen == 0) continue;

                    // Get the first ship GameObject
                    var queueItem = queue.GetType().GetProperty("Item");
                    var shipGo = queueItem?.GetValue(queue, new object[] { 0 });
                    if (shipGo == null) continue;

                    // Find SkinSlotRegistry component on the ship
                    var registryType = asm.GetType("Il2CppWartide.SkinSlotRegistry");
                    if (registryType == null) return 0;

                    var getComp = shipGo.GetType().GetMethods(HarmonyPatcher.FLAGS)
                        .FirstOrDefault(m => m.Name == "GetComponentInChildren" && m.IsGenericMethod && m.GetParameters().Length == 0);

                    if (getComp == null)
                    {
                        getComp = shipGo.GetType().GetMethods(HarmonyPatcher.FLAGS)
                            .FirstOrDefault(m => m.Name == "GetComponentInChildren" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));
                        if (getComp != null)
                        {
                            var registry = getComp.Invoke(shipGo, new object[] { registryType });
                            if (registry != null)
                            {
                                var getSlotLen = registry.GetType().GetMethod("GetSlotLength", HarmonyPatcher.FLAGS);
                                if (getSlotLen != null)
                                    return (int)(getSlotLen.Invoke(registry, null) ?? 0);
                            }
                        }
                    }
                    else
                    {
                        var specific = getComp.MakeGenericMethod(registryType);
                        var registry = specific.Invoke(shipGo, null);
                        if (registry != null)
                        {
                            var getSlotLen = registry.GetType().GetMethod("GetSlotLength", HarmonyPatcher.FLAGS);
                            if (getSlotLen != null)
                                return (int)(getSlotLen.Invoke(registry, null) ?? 0);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SkinApply] GetSlotCount error: {ex.Message}");
            }
            return 0;
        }

        internal static HashSet<int> GetValidMeshSwapSlotIds(
            object pool, string guid, Assembly asm)
        {
            var result = new HashSet<int>();
            try
            {
                var playerPool = pool.GetType().GetProperty("_playerPool", HarmonyPatcher.FLAGS)?.GetValue(pool);
                if (playerPool == null) return result;
                int count = IL2CppArrayHelper.GetLength(playerPool);
                var item = playerPool.GetType().GetProperty("Item");

                for (int i = 1; i < count; i++)
                {
                    var entry = item?.GetValue(playerPool, new object[] { i });
                    if (entry == null) continue;
                    var poolOfShips = entry.GetType().GetProperty("_poolOfShips", HarmonyPatcher.FLAGS)?.GetValue(entry);
                    if (poolOfShips == null) continue;
                    object? innerPool = null;
                    try { innerPool = poolOfShips.GetType().GetProperty("Item")?.GetValue(poolOfShips, new object[] { guid }); } catch { continue; }
                    if (innerPool == null) continue;
                    var queue = innerPool.GetType().GetProperty("_circularQueuePool", HarmonyPatcher.FLAGS)?.GetValue(innerPool);
                    if (queue == null || IL2CppArrayHelper.GetLength(queue) == 0) continue;
                    var shipGo = queue.GetType().GetProperty("Item")?.GetValue(queue, new object[] { 0 });
                    if (shipGo == null) continue;

                    var registryType = asm.GetType("Il2CppWartide.SkinSlotRegistry");
                    var getComp = shipGo.GetType().GetMethods(HarmonyPatcher.FLAGS)
                        .FirstOrDefault(m => m.Name == "GetComponentInChildren"
                            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));
                    var reg = getComp?.Invoke(shipGo, new object[] { registryType! });
                    if (reg == null) continue;

                    var getMeshSlots = reg.GetType().GetMethod("GetElementMeshSwapSlots", HarmonyPatcher.FLAGS);
                    var meshSlots = getMeshSlots?.Invoke(reg, null);
                    if (meshSlots == null) continue;

                    int msCount = IL2CppArrayHelper.GetLength(meshSlots);
                    var msItem = meshSlots.GetType().GetProperty("Item");
                    for (int s = 0; s < msCount; s++)
                    {
                        var slot = msItem?.GetValue(meshSlots, new object[] { s });
                        if (slot == null) continue;
                        var slotIdVal = slot.GetType().GetProperty("SlotID", HarmonyPatcher.FLAGS)?.GetValue(slot);
                        if (slotIdVal != null) result.Add((int)slotIdVal);
                        string slotName = slot.GetType().GetProperty("Name", HarmonyPatcher.FLAGS)?.GetValue(slot)?.ToString() ?? "?";
                        MelonLogger.Msg($"[SkinApply]   MeshSwapSlot: '{slotName}' ID={slotIdVal}");
                    }
                    break;
                }
            }
            catch { }
            return result;
        }

        internal static List<object> FindSampanMeshSwaps(
            object? meshSwapArray, object registry,
            HashSet<int> validSlotIds, Assembly asm)
        {
            var result = new List<object>();
            if (meshSwapArray == null) return result;

            try
            {
                // Find the Sampan entity data to match AssociatedPlayerShip
                object? sampanEntity = null;
                var entityArray = registry.GetType().GetProperty("_simEntityData", HarmonyPatcher.FLAGS)?.GetValue(registry);
                if (entityArray != null)
                {
                    int entCount = IL2CppArrayHelper.GetLength(entityArray);
                    var entItem = entityArray.GetType().GetProperty("Item");
                    for (int i = 0; i < entCount; i++)
                    {
                        var ent = entItem?.GetValue(entityArray, new object[] { i });
                        if (ent == null) continue;
                        string name = ent.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(ent)?.ToString() ?? "";
                        if (name.Contains("Sampan", StringComparison.OrdinalIgnoreCase))
                        {
                            sampanEntity = ent;
                            break;
                        }
                    }
                }

                int msCount = IL2CppArrayHelper.GetLength(meshSwapArray);
                var msItem = meshSwapArray.GetType().GetProperty("Item");

                for (int i = 0; i < msCount; i++)
                {
                    try
                    {
                        var container = msItem?.GetValue(meshSwapArray, new object[] { i });
                        if (container == null) continue;

                        // Check if this mesh swap is for the Sampan
                        var assocShip = container.GetType().GetProperty("AssociatedPlayerShip", HarmonyPatcher.FLAGS)?.GetValue(container);
                        if (assocShip == null) continue;

                        // Match by name since reference comparison may not work
                        string shipName = assocShip.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(assocShip)?.ToString() ?? "";
                        if (!shipName.Contains("Sampan", StringComparison.OrdinalIgnoreCase)) continue;

                        int slotId = -1;
                        try { slotId = (int)(container.GetType().GetProperty("MeshSwapSlotID", HarmonyPatcher.FLAGS)?.GetValue(container) ?? -1); } catch { }

                        // Only include mesh swaps whose slot ID exists on the Sampan
                        if (validSlotIds.Count > 0 && !validSlotIds.Contains(slotId)) continue;

                        string msName = container.GetType().GetProperty("name", HarmonyPatcher.FLAGS)?.GetValue(container)?.ToString() ?? "?";
                        MelonLogger.Msg($"[SkinApply]   Available mesh swap: '{msName}' slotID={slotId}");

                        result.Add(container);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SkinApply] FindSampanMeshSwaps error: {ex.Message}");
            }

            return result;
        }

        internal static int[] GetSlotTypesFromPool(object pool, string guid, int slotCount, Assembly asm)
        {
            var result = new int[slotCount];
            try
            {
                var playerPool = pool.GetType().GetProperty("_playerPool", HarmonyPatcher.FLAGS)?.GetValue(pool);
                if (playerPool == null) return result;
                int count = IL2CppArrayHelper.GetLength(playerPool);
                var item = playerPool.GetType().GetProperty("Item");

                for (int i = 1; i < count; i++)
                {
                    var entry = item?.GetValue(playerPool, new object[] { i });
                    if (entry == null) continue;

                    var poolOfShips = entry.GetType().GetProperty("_poolOfShips", HarmonyPatcher.FLAGS)?.GetValue(entry);
                    if (poolOfShips == null) continue;

                    object? innerPool = null;
                    try { innerPool = poolOfShips.GetType().GetProperty("Item")?.GetValue(poolOfShips, new object[] { guid }); } catch { continue; }
                    if (innerPool == null) continue;

                    var queue = innerPool.GetType().GetProperty("_circularQueuePool", HarmonyPatcher.FLAGS)?.GetValue(innerPool);
                    if (queue == null || IL2CppArrayHelper.GetLength(queue) == 0) continue;

                    var shipGo = queue.GetType().GetProperty("Item")?.GetValue(queue, new object[] { 0 });
                    if (shipGo == null) continue;

                    var registryType = asm.GetType("Il2CppWartide.SkinSlotRegistry");
                    var getComp = shipGo.GetType().GetMethods(HarmonyPatcher.FLAGS)
                        .FirstOrDefault(m => m.Name == "GetComponentInChildren"
                            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));
                    var reg = getComp?.Invoke(shipGo, new object[] { registryType! });
                    if (reg == null) continue;

                    var getSlots = reg.GetType().GetMethod("GetElementSlots", HarmonyPatcher.FLAGS);
                    var slots = getSlots?.Invoke(reg, null);
                    if (slots == null) continue;

                    int slotsLen = IL2CppArrayHelper.GetLength(slots);
                    var slotItem = slots.GetType().GetProperty("Item");
                    for (int s = 0; s < slotsLen && s < slotCount; s++)
                    {
                        var slot = slotItem?.GetValue(slots, new object[] { s });
                        if (slot == null) continue;
                        var typesProp = slot.GetType().GetProperty("PossibleElementTypes", HarmonyPatcher.FLAGS);
                        var typesVal = typesProp?.GetValue(slot);
                        if (typesVal != null)
                        {
                            result[s] = (int)typesVal;
                            string slotName = slot.GetType().GetProperty("SlotName", HarmonyPatcher.FLAGS)?.GetValue(slot)?.ToString() ?? "?";
                            MelonLogger.Msg($"[SkinApply]   Slot[{s}] '{slotName}' types={result[s]}");
                        }
                    }
                    break;
                }
            }
            catch { }
            return result;
        }

        internal static string? GetSampanGuid(object registry)
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
                    if (!name.Contains("Sampan", StringComparison.OrdinalIgnoreCase)) continue;
                    return entity.GetType().GetMethod("GetVisualPrefabGuid", HarmonyPatcher.FLAGS)?.Invoke(entity, null)?.ToString();
                }
                catch { }
            }
            return null;
        }

        private static bool IsServerActive()
        {
            try
            {
                var nsType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "Mirror.NetworkServer" || t.FullName == "Il2CppMirror.NetworkServer");
                if (nsType == null) return false;

                var activeProp = nsType.GetProperty("active", BindingFlags.Public | BindingFlags.Static);
                return activeProp != null && (bool)(activeProp.GetValue(null) ?? false);
            }
            catch { return false; }
        }
    }
}
