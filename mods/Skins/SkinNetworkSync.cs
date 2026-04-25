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
    /// Network sync — Harmony patches and helpers for injecting/sending skin data over Mirror.
    /// </summary>
    public static class SkinNetworkSync
    {
        /// <summary>
        /// Runs AFTER SimulationManager.InitializeBackendSourcedData — generates random skins
        /// and sets _playerSkinData so the game's Mirror sync delivers them to all clients.
        /// </summary>
        public static void Postfix_InitializeBackendSourcedData(object __instance)
        {
            if (SkinSystem.NetworkSkinDataSet) return;
            MelonLogger.Msg("[SkinSync] InitializeBackendSourcedData postfix — injecting network skin data...");

            try
            {
                var reflection = new GameReflectionBridge();
                if (!reflection.IsValid)
                {
                    MelonLogger.Warning("[SkinSync] GameReflectionBridge not valid");
                    return;
                }

                var skinChoices = SkinGenerator.GenerateRandomSkinChoices(reflection);
                if (skinChoices.Count == 0)
                {
                    MelonLogger.Warning("[SkinSync] No skin choices generated");
                    return;
                }

                SkinSystem.EarlyGeneratedSkins = skinChoices;

                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (asm == null) return;

                SetNetworkSkinData(__instance, skinChoices, asm);
                SkinSystem.NetworkSkinDataSet = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SkinSync] Postfix error: {ex}");
            }
        }

        /// <summary>
        /// Replaces WartideNetworkManager.OnServerRequestMatchInfo — the native handler sends
        /// empty skin data in non-authoritative (P2P) mode. We construct and send a
        /// ResponseMatchInfoMessage that includes our skin data from _playerSkinData.
        /// Returns false to skip native handler when successful; true to fall through.
        /// </summary>
        public static bool Prefix_OnServerRequestMatchInfo(object __instance, object conn)
        {
            try
            {
                string connInfo = "?";
                try { connInfo = conn.GetType().GetProperty("connectionId", HarmonyPatcher.FLAGS)?.GetValue(conn)?.ToString() ?? "?"; } catch { }
                MelonLogger.Msg($"[SkinSync] >>> OnServerRequestMatchInfo called! connId={connInfo} networkSkinDataSet={SkinSystem.NetworkSkinDataSet}");

                if (!SkinSystem.NetworkSkinDataSet)
                {
                    MelonLogger.Msg("[SkinSync] >>> No skin data, falling through to native handler");
                    return true;
                }

                var reflection = new GameReflectionBridge();
                if (!reflection.IsValid) return true;
                var gaInstance = reflection.GameAuthorityInstance;
                var gaType = reflection.GameAuthorityType;
                if (gaInstance == null || gaType == null) return true;

                var simManager = gaType.GetProperty("_simulationManager", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
                if (simManager == null) return true;

                // Read data from SimulationManager
                var skinData = simManager.GetType().GetProperty("_playerSkinData", HarmonyPatcher.FLAGS)?.GetValue(simManager);
                if (skinData == null) { MelonLogger.Warning("[SkinSync] >>> _playerSkinData null"); return true; }

                // Get captain IDs — in P2P mode these are default (all same type)
                var captainIDs = simManager.GetType().GetProperty("_playerCaptainIDs", HarmonyPatcher.FLAGS)?.GetValue(simManager);

                // Find ResponseMatchInfoMessage type
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });
                var responseType = allTypes.FirstOrDefault(t => t.Name == "ResponseMatchInfoMessage");
                if (responseType == null) { MelonLogger.Warning("[SkinSync] >>> ResponseMatchInfoMessage type not found"); return true; }

                // Discover and log all fields/properties
                var fields = responseType.GetFields(HarmonyPatcher.FLAGS);
                var props = responseType.GetProperties(HarmonyPatcher.FLAGS);
                foreach (var f in fields)
                    MelonLogger.Msg($"[SkinSync] >>>   field: '{f.Name}' type={f.FieldType.Name}");
                foreach (var p in props)
                    MelonLogger.Msg($"[SkinSync] >>>   prop: '{p.Name}' type={p.PropertyType.Name}");

                // Create the response
                object? response = null;
                try { response = responseType.GetConstructor(Type.EmptyTypes)?.Invoke(null) ?? Activator.CreateInstance(responseType); }
                catch { }
                if (response == null) { MelonLogger.Warning("[SkinSync] >>> Can't create response"); return true; }

                // Set properties by name (discovered from runtime field enumeration)
                var skinProp = responseType.GetProperty("playerSkinData", HarmonyPatcher.FLAGS);
                var captainProp = responseType.GetProperty("captainTypeIDValues", HarmonyPatcher.FLAGS);
                var mmrProp = responseType.GetProperty("mmrValues", HarmonyPatcher.FLAGS);
                var displayNamesProp = responseType.GetProperty("playerDisplayNames", HarmonyPatcher.FLAGS);
                var totalPlayersProp = responseType.GetProperty("totalPlayersInMatch", HarmonyPatcher.FLAGS);
                var accountIdsProp = responseType.GetProperty("playerAccountIDs", HarmonyPatcher.FLAGS);

                // Skin data (required)
                if (skinProp == null || !skinProp.CanWrite) { MelonLogger.Warning("[SkinSync] >>> playerSkinData prop not writable"); return true; }
                skinProp.SetValue(response, skinData);
                MelonLogger.Msg("[SkinSync] >>> Set playerSkinData");

                // Captain IDs — SimulationManager stores NativeArray<TypeID>, but the response
                // expects Il2CppStructArray<int>. Build the right type.
                try
                {
                    if (captainIDs != null && captainProp != null && captainProp.CanWrite)
                    {
                        // Try direct set first
                        try { captainProp.SetValue(response, captainIDs); MelonLogger.Msg("[SkinSync] >>> Set captainTypeIDValues (direct)"); }
                        catch
                        {
                            // TypeID is a struct wrapping an int. Extract via ToString→int or cast.
                            int captainLen = IL2CppArrayHelper.GetLength(captainIDs);
                            var intArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<>).MakeGenericType(typeof(int));
                            var captainArr = intArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)captainLen });
                            var captainItem = intArrType.GetProperty("Item");
                            var srcItem = captainIDs.GetType().GetProperty("Item");
                            for (int ci = 0; ci < captainLen; ci++)
                            {
                                var val = srcItem?.GetValue(captainIDs, new object[] { ci });
                                int intVal = 0;
                                if (val != null)
                                {
                                    // TypeID is a struct with [FieldOffset(0)] public int Value
                                    var valueField = val.GetType().GetField("Value", HarmonyPatcher.FLAGS);
                                    if (valueField != null)
                                        intVal = (int)valueField.GetValue(val)!;
                                    else if (int.TryParse(val.ToString(), out int parsed))
                                        intVal = parsed;
                                }
                                captainItem!.SetValue(captainArr, intVal, new object[] { ci });
                            }
                            captainProp.SetValue(response, captainArr);
                            MelonLogger.Msg($"[SkinSync] >>> Set captainTypeIDValues (converted, len={captainLen})");
                        }
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[SkinSync] >>> captainIDs error: {ex.Message}"); }

                // MMR values
                try
                {
                    var playerMMRs = simManager.GetType().GetProperty("_playerMMRs", HarmonyPatcher.FLAGS)?.GetValue(simManager);
                    if (playerMMRs != null && mmrProp != null && mmrProp.CanWrite)
                    {
                        mmrProp.SetValue(response, playerMMRs);
                        MelonLogger.Msg("[SkinSync] >>> Set mmrValues");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[SkinSync] >>> mmrValues error: {ex.Message}"); }

                // Display names — must not be null or client crashes at LogInitializing
                try
                {
                    if (displayNamesProp != null && displayNamesProp.CanWrite)
                    {
                        var displayNames = simManager.GetType().GetProperty("_playerDisplayNames", HarmonyPatcher.FLAGS)?.GetValue(simManager);
                        if (displayNames == null)
                        {
                            // Build empty string array matching slot count
                            int slots = IL2CppArrayHelper.GetLength(skinData);
                            var strArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray);
                            displayNames = strArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)slots });
                            // Fill with player names from mappings or defaults
                            var mappings = gaType.GetMethod("GetPlayerConnectionMappings", HarmonyPatcher.FLAGS)?.Invoke(gaInstance, null);
                            var mappingItem = mappings?.GetType().GetProperty("Item");
                            int mappingLen = mappings != null ? IL2CppArrayHelper.GetLength(mappings) : 0;
                            var strItem = strArrType.GetProperty("Item");
                            for (int di = 0; di < slots; di++)
                            {
                                string name = $"Player {di}";
                                if (mappingItem != null && di < mappingLen)
                                {
                                    var m = mappingItem.GetValue(mappings, new object[] { di });
                                    var dn = m?.GetType().GetProperty("DisplayName", HarmonyPatcher.FLAGS)?.GetValue(m)?.ToString();
                                    if (!string.IsNullOrEmpty(dn)) name = dn;
                                }
                                strItem!.SetValue(displayNames, name, new object[] { di });
                            }
                        }
                        displayNamesProp.SetValue(response, displayNames);
                        MelonLogger.Msg($"[SkinSync] >>> Set playerDisplayNames (len={IL2CppArrayHelper.GetLength(displayNames)})");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[SkinSync] >>> displayNames error: {ex.Message}"); }

                // Account IDs — must not be null or client crashes
                try
                {
                    if (accountIdsProp != null && accountIdsProp.CanWrite)
                    {
                        int slots = IL2CppArrayHelper.GetLength(skinData);
                        var uintArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<>).MakeGenericType(typeof(uint));
                        var accountIds = uintArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)slots });
                        accountIdsProp.SetValue(response, accountIds);
                        MelonLogger.Msg($"[SkinSync] >>> Set playerAccountIDs (len={slots})");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[SkinSync] >>> accountIDs error: {ex.Message}"); }

                // Total players in match
                try
                {
                    if (totalPlayersProp != null && totalPlayersProp.CanWrite)
                    {
                        int totalPlayers = IL2CppArrayHelper.GetLength(skinData); // 11 slots
                        totalPlayersProp.SetValue(response, totalPlayers);
                        MelonLogger.Msg($"[SkinSync] >>> Set totalPlayersInMatch={totalPlayers}");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[SkinSync] >>> totalPlayers error: {ex.Message}"); }

                // Send via conn.Send<ResponseMatchInfoMessage>
                var sendMethods = conn.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Send" && m.IsGenericMethod).ToArray();
                MelonLogger.Msg($"[SkinSync] >>> Found {sendMethods.Length} Send methods");
                foreach (var sm in sendMethods)
                    MelonLogger.Msg($"[SkinSync] >>>   Send: params={sm.GetParameters().Length} ({string.Join(", ", sm.GetParameters().Select(p => p.Name + ":" + p.ParameterType.Name))})");

                bool sent = false;
                foreach (var sm in sendMethods)
                {
                    try
                    {
                        var genericSend = sm.MakeGenericMethod(responseType);
                        int paramCount = sm.GetParameters().Length;
                        if (paramCount == 1)
                            genericSend.Invoke(conn, new[] { response });
                        else if (paramCount == 2)
                            genericSend.Invoke(conn, new object[] { response, 0 });
                        sent = true;
                        MelonLogger.Msg($"[SkinSync] >>> Sent via Send with {paramCount} params!");
                        break;
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[SkinSync] >>> Send({sm.GetParameters().Length} params) failed: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
                if (!sent) { MelonLogger.Warning("[SkinSync] >>> All Send attempts failed"); return true; }
                MelonLogger.Msg("[SkinSync] >>> Sent custom ResponseMatchInfoMessage with skin data!");

                // Also call PreloadData.SetTargetConnectionCount
                try
                {
                    var preloadType = allTypes.FirstOrDefault(t => t.Name == "PreloadData");
                    var setCountMethod = preloadType?.GetMethod("SetTargetConnectionCount", BindingFlags.Public | BindingFlags.Static);
                    var networkServerType2 = allTypes.FirstOrDefault(t => t.Name == "NetworkServer" && t.Namespace?.Contains("Mirror") == true);
                    var connsProp2 = networkServerType2?.GetProperty("connections", BindingFlags.Public | BindingFlags.Static);
                    var conns2 = connsProp2?.GetValue(null);
                    int count = conns2 != null ? IL2CppArrayHelper.GetLength(conns2) : 2;
                    setCountMethod?.Invoke(null, new object[] { count });
                    MelonLogger.Msg($"[SkinSync] >>> SetTargetConnectionCount({count})");
                }
                catch { }

                return false; // Skip native handler
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SkinSync] >>> Prefix error: {ex.Message}");
                return true; // Fall through to native on error
            }
        }

        /// <summary>
        /// Build NetworkedSkinData / PlayerNetworkedSkinData arrays and set on the given SimulationManager's _playerSkinData.
        /// </summary>
        internal static void SetNetworkSkinData(object simManager, List<SkinGenerator.PlayerSkinInfo> playerSkins, Assembly asm)
        {
            try
            {
                if (playerSkins.Count == 0)
                {
                    MelonLogger.Warning("[SkinSync] No player skins to sync");
                    return;
                }

                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });

                var nsdType = allTypes.FirstOrDefault(t => t.Name == "NetworkedSkinData" && t.Namespace?.Contains("Mirror") == true);
                var pnsdType = allTypes.FirstOrDefault(t => t.Name == "PlayerNetworkedSkinData" && t.Namespace?.Contains("Mirror") == true);

                if (nsdType == null || pnsdType == null)
                {
                    MelonLogger.Warning($"[SkinSync] Types not found: NSD={nsdType != null}, PNSD={pnsdType != null}");
                    return;
                }

                // Array must be sized for ALL player slots (0-10 = 11 entries).
                // Index 0 is unused; players are at indices 1-10.
                int totalSlots = 11;
                MelonLogger.Msg($"[SkinSync] Building network skin data: {playerSkins.Count} players, {totalSlots} array slots...");

                var pnsdArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<>).MakeGenericType(pnsdType);
                var pnsdArr = pnsdArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)totalSlots });
                var pnsdArrItem = pnsdArrType.GetProperty("Item");
                var nsdArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<>).MakeGenericType(nsdType);
                var stringArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray);
                var floatArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<>).MakeGenericType(typeof(float));

                var slotIdEnumType = allTypes.FirstOrDefault(t => t.Name == "MeshSwapSlotID");
                var slotIdArrType = slotIdEnumType != null
                    ? typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<>).MakeGenericType(slotIdEnumType)
                    : null;

                // Fill empty entries for all slots (game expects non-null PlayerNetworkedSkinData at each index)
                for (int i = 0; i < totalSlots; i++)
                {
                    var emptyPnsd = pnsdType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
                    pnsdType.GetProperty("PlayerIndex", HarmonyPatcher.FLAGS)?.SetValue(emptyPnsd, i);
                    var emptyNsdArr = nsdArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { 0L });
                    pnsdType.GetProperty("NetworkedSkins", HarmonyPatcher.FLAGS)?.SetValue(emptyPnsd, emptyNsdArr);
                    pnsdArrItem!.SetValue(pnsdArr, emptyPnsd, new object[] { i });
                }

                // Translate asset names to backend GUIDs
                var guidMap = SkinSystem.NameToGuidMap ?? new Dictionary<string, string>();

                // Now fill actual skin data at the correct player indices
                for (int i = 0; i < playerSkins.Count; i++)
                {
                    var psi = playerSkins[i];

                    // ShipUniversalID is already a GUID (set by GenerateRandomSkinChoices)
                    var nsd = nsdType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
                    nsdType.GetProperty("ShipUniversalID", HarmonyPatcher.FLAGS)?.SetValue(nsd, psi.ShipUniversalID ?? "");
                    nsdType.GetProperty("OverallQuality", HarmonyPatcher.FLAGS)?.SetValue(nsd, psi.OverallQuality);
                    nsdType.GetProperty("UsesColorPalette", HarmonyPatcher.FLAGS)?.SetValue(nsd, psi.UsesColorPalette);
                    // Translate palette name to GUID
                    string palGuid = SkinGenerator.TranslateToGuid(psi.ColorPaletteUniversalID, guidMap);
                    nsdType.GetProperty("ColorPaletteUniversalID", HarmonyPatcher.FLAGS)?.SetValue(nsd, palGuid);
                    nsdType.GetProperty("ElementCount", HarmonyPatcher.FLAGS)?.SetValue(nsd, psi.ElementUniversalIDs.Length);

                    // ElementQualities
                    var eqArr = floatArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)psi.ElementQualities.Length });
                    var eqItem = floatArrType.GetProperty("Item");
                    for (int j = 0; j < psi.ElementQualities.Length; j++)
                        eqItem!.SetValue(eqArr, psi.ElementQualities[j], new object[] { j });
                    nsdType.GetProperty("ElementQualities", HarmonyPatcher.FLAGS)?.SetValue(nsd, eqArr);

                    // ElementUniversalIDs — translate names to GUIDs
                    var eidArr = stringArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)psi.ElementUniversalIDs.Length });
                    var eidItem = stringArrType.GetProperty("Item");
                    for (int j = 0; j < psi.ElementUniversalIDs.Length; j++)
                        eidItem!.SetValue(eidArr, SkinGenerator.TranslateToGuid(psi.ElementUniversalIDs[j], guidMap), new object[] { j });
                    nsdType.GetProperty("ElementUniversalIDs", HarmonyPatcher.FLAGS)?.SetValue(nsd, eidArr);

                    // SubElementUniversalIDs — translate names to GUIDs
                    var seidArr = stringArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { (long)psi.SubElementUniversalIDs.Length });
                    for (int j = 0; j < psi.SubElementUniversalIDs.Length; j++)
                        eidItem!.SetValue(seidArr, SkinGenerator.TranslateToGuid(psi.SubElementUniversalIDs[j], guidMap), new object[] { j });
                    nsdType.GetProperty("SubElementUniversalIDs", HarmonyPatcher.FLAGS)?.SetValue(nsd, seidArr);

                    // MeshSwaps — leave empty so the equipped skin coroutine doesn't consume
                    // MeshSwapBaseObjects; the game's preset skin deck handles mesh swaps separately
                    // and ApplyMeshSwap destroys the base object after each swap, preventing reuse.
                    nsdType.GetProperty("MeshSwapCount", HarmonyPatcher.FLAGS)?.SetValue(nsd, 0);
                    nsdType.GetProperty("MeshSwapUniversalIDs", HarmonyPatcher.FLAGS)?.SetValue(nsd,
                        stringArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { 0L }));
                    if (slotIdArrType != null)
                    {
                        nsdType.GetProperty("SlotIDs", HarmonyPatcher.FLAGS)?.SetValue(nsd,
                            slotIdArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { 0L }));
                    }

                    var pnsd = pnsdType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
                    pnsdType.GetProperty("PlayerIndex", HarmonyPatcher.FLAGS)?.SetValue(pnsd, psi.PlayerIndex);
                    var nsdSingleArr = nsdArrType.GetConstructor(new[] { typeof(long) })!.Invoke(new object[] { 1L });
                    nsdArrType.GetProperty("Item")!.SetValue(nsdSingleArr, nsd, new object[] { 0 });
                    pnsdType.GetProperty("NetworkedSkins", HarmonyPatcher.FLAGS)?.SetValue(pnsd, nsdSingleArr);
                    // Place at the player's actual index (not sequential i)
                    if (psi.PlayerIndex >= 0 && psi.PlayerIndex < totalSlots)
                        pnsdArrItem!.SetValue(pnsdArr, pnsd, new object[] { psi.PlayerIndex });

                    MelonLogger.Msg($"[SkinSync] Player {psi.PlayerIndex}: ship='{psi.ShipUniversalID}' palette='{psi.ColorPaletteUniversalID}' " +
                        $"elements={psi.ElementUniversalIDs.Length} meshSwaps={psi.MeshSwapUniversalIDs.Length}");
                }

                // Set _playerSkinData on SimulationManager
                simManager.GetType().GetProperty("_playerSkinData", HarmonyPatcher.FLAGS)?.SetValue(simManager, pnsdArr);
                MelonLogger.Msg($"[SkinSync] Set _playerSkinData on SimulationManager ({playerSkins.Count} entries)");

                var readBack = simManager.GetType().GetProperty("_playerSkinData", HarmonyPatcher.FLAGS)?.GetValue(simManager);
                int readLen = readBack != null ? IL2CppArrayHelper.GetLength(readBack) : -1;
                MelonLogger.Msg($"[SkinSync] Verification: _playerSkinData length = {readLen}");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[SkinSync] Error: {ex}");
            }
        }
    }
}
