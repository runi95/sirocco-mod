using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;
using SiroccoMod.Helpers;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.Scoreboard.ScoreboardPlugin), "P2P Scoreboard Fix", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.Scoreboard
{
    /// <summary>
    /// Patches WartideNetworkManager.OnServerRequestMatchInfo to construct and send a
    /// ResponseMatchInfoMessage that includes playerDisplayNames and playerAccountIDs.
    ///
    /// In P2P mode the native handler sends these as null, so clients see blank scoreboards.
    /// This prefix builds a complete response from SimulationManager and GameAuthority data,
    /// sends it, and skips the native handler.
    /// </summary>
    public class ScoreboardPlugin : MelonMod
    {
        private static Type? _gaType;
        private static PropertyInfo? _gaInstanceProp;
        private static MethodInfo? _getMappingsMethod;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[ScoreboardResponse] Assembly-CSharp not found");
                return;
            }

            _gaType = asm.GetType("Il2CppWartide.GameAuthority");
            if (_gaType != null)
            {
                _gaInstanceProp = _gaType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _getMappingsMethod = _gaType.GetMethod("GetPlayerConnectionMappings", BindingFlags.Public | BindingFlags.Instance);
            }

            var netMgrType = asm.GetType("Il2CppWartide.WartideNetworkManager");
            if (netMgrType == null)
            {
                MelonLogger.Warning("[ScoreboardResponse] WartideNetworkManager not found");
                return;
            }

            var method = netMgrType.GetMethod("OnServerRequestMatchInfo", HarmonyPatcher.FLAGS);
            if (method == null)
            {
                MelonLogger.Warning("[ScoreboardResponse] OnServerRequestMatchInfo not found");
                return;
            }

            var prefix = new HarmonyLib.HarmonyMethod(typeof(ScoreboardPlugin), nameof(Prefix_OnServerRequestMatchInfo));
            HarmonyInstance.Patch(method, prefix: prefix);
            MelonLogger.Msg("[ScoreboardResponse] Patched OnServerRequestMatchInfo");
        }

        public static bool Prefix_OnServerRequestMatchInfo(object __instance, object __0)
        {
            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null || _gaType == null) return true;

                var simManager = _gaType.GetProperty("_simulationManager", HarmonyPatcher.FLAGS)?.GetValue(gaInstance);
                if (simManager == null) return true;

                var simType = simManager.GetType();

                // Find ResponseMatchInfoMessage type
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });
                var responseType = allTypes.FirstOrDefault(t => t.Name == "ResponseMatchInfoMessage");
                if (responseType == null) return true;

                // Create response
                var response = responseType.GetConstructor(Type.EmptyTypes)?.Invoke(null)
                            ?? Activator.CreateInstance(responseType);
                if (response == null) return true;

                // Read existing data from SimulationManager
                var skinData = simType.GetProperty("_playerSkinData", HarmonyPatcher.FLAGS)?.GetValue(simManager);
                var captainIDs = simType.GetProperty("_playerCaptainIDs", HarmonyPatcher.FLAGS)?.GetValue(simManager);
                var playerMMRs = simType.GetProperty("_playerMMRs", HarmonyPatcher.FLAGS)?.GetValue(simManager);

                // Determine slot count
                int slots = skinData != null ? IL2CppArrayHelper.GetLength(skinData) : 11;

                // Set skin data (pass through whatever the native handler would send)
                SetProperty(responseType, response, "playerSkinData", skinData);

                // Captain IDs
                TrySetCaptainIDs(responseType, response, captainIDs, slots);

                // MMR values
                SetProperty(responseType, response, "mmrValues", playerMMRs);

                // Total players
                var totalProp = responseType.GetProperty("totalPlayersInMatch", HarmonyPatcher.FLAGS);
                if (totalProp != null && totalProp.CanWrite)
                    totalProp.SetValue(response, slots);

                // Display names — the key scoreboard fix
                SetDisplayNames(responseType, response, gaInstance, slots);

                // Account IDs
                SetAccountIDs(responseType, response, slots);

                // Send via conn.Send<ResponseMatchInfoMessage>
                if (!SendResponse(__0, responseType, response))
                    return true; // fall through to native on failure

                // Update PreloadData.SetTargetConnectionCount
                SetTargetConnectionCount(allTypes);

                MelonLogger.Msg("[ScoreboardResponse] Sent ResponseMatchInfoMessage with scoreboard data");
                return false; // skip native handler
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ScoreboardResponse] Prefix error: {ex.Message}");
                return true;
            }
        }

        private static void SetDisplayNames(Type responseType, object response, object gaInstance, int slots)
        {
            var prop = responseType.GetProperty("playerDisplayNames", HarmonyPatcher.FLAGS);
            if (prop == null || !prop.CanWrite) return;

            var strArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray);
            var displayNames = strArrType.GetConstructor(new[] { typeof(long) })!
                .Invoke(new object[] { (long)slots });
            var strItem = strArrType.GetProperty("Item");

            var mappings = _getMappingsMethod?.Invoke(gaInstance, null);
            var mappingItem = mappings != null ? IL2CppArrayHelper.GetItemProperty(mappings) : null;
            int mappingLen = mappings != null ? IL2CppArrayHelper.GetLength(mappings) : 0;

            for (int i = 0; i < slots; i++)
            {
                string name = $"Player {i}";
                if (mappingItem != null && i < mappingLen)
                {
                    try
                    {
                        var m = mappingItem.GetValue(mappings, new object[] { i });
                        var dn = m?.GetType().GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance)?
                            .GetValue(m)?.ToString();
                        if (!string.IsNullOrEmpty(dn)) name = dn;
                    }
                    catch { }
                }
                strItem!.SetValue(displayNames, name, new object[] { i });
            }

            prop.SetValue(response, displayNames);
        }

        private static void SetAccountIDs(Type responseType, object response, int slots)
        {
            var prop = responseType.GetProperty("playerAccountIDs", HarmonyPatcher.FLAGS);
            if (prop == null || !prop.CanWrite) return;

            var uintArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<>)
                .MakeGenericType(typeof(uint));
            var accountIds = uintArrType.GetConstructor(new[] { typeof(long) })!
                .Invoke(new object[] { (long)slots });

            prop.SetValue(response, accountIds);
        }

        private static void TrySetCaptainIDs(Type responseType, object response, object? captainIDs, int slots)
        {
            var prop = responseType.GetProperty("captainTypeIDValues", HarmonyPatcher.FLAGS);
            if (prop == null || !prop.CanWrite || captainIDs == null) return;

            try
            {
                // Try direct assignment first
                prop.SetValue(response, captainIDs);
            }
            catch
            {
                try
                {
                    // Convert TypeID array to int array
                    int len = IL2CppArrayHelper.GetLength(captainIDs);
                    var intArrType = typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<>)
                        .MakeGenericType(typeof(int));
                    var arr = intArrType.GetConstructor(new[] { typeof(long) })!
                        .Invoke(new object[] { (long)len });
                    var itemProp = intArrType.GetProperty("Item");
                    var srcItem = captainIDs.GetType().GetProperty("Item");

                    for (int i = 0; i < len; i++)
                    {
                        var val = srcItem?.GetValue(captainIDs, new object[] { i });
                        int intVal = 0;
                        if (val != null)
                        {
                            var valueField = val.GetType().GetField("Value", HarmonyPatcher.FLAGS);
                            if (valueField != null)
                                intVal = (int)valueField.GetValue(val)!;
                            else if (int.TryParse(val.ToString(), out int parsed))
                                intVal = parsed;
                        }
                        itemProp!.SetValue(arr, intVal, new object[] { i });
                    }
                    prop.SetValue(response, arr);
                }
                catch { }
            }
        }

        private static void SetProperty(Type type, object obj, string name, object? value)
        {
            if (value == null) return;
            var prop = type.GetProperty(name, HarmonyPatcher.FLAGS);
            if (prop != null && prop.CanWrite)
                prop.SetValue(obj, value);
        }

        private static bool SendResponse(object conn, Type responseType, object response)
        {
            var sendMethods = conn.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Send" && m.IsGenericMethod).ToArray();

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
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static void SetTargetConnectionCount(System.Collections.Generic.IEnumerable<Type> allTypes)
        {
            try
            {
                var preloadType = allTypes.FirstOrDefault(t => t.Name == "PreloadData");
                var setCountMethod = preloadType?.GetMethod("SetTargetConnectionCount", BindingFlags.Public | BindingFlags.Static);
                var networkServerType = allTypes.FirstOrDefault(t =>
                    t.Name == "NetworkServer" && t.Namespace?.Contains("Mirror") == true);
                var connsProp = networkServerType?.GetProperty("connections", BindingFlags.Public | BindingFlags.Static);
                var conns = connsProp?.GetValue(null);
                int count = conns != null ? IL2CppArrayHelper.GetLength(conns) : 2;
                setCountMethod?.Invoke(null, new object[] { count });
            }
            catch { }
        }
    }
}
