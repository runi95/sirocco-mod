using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;
using UnityEngine;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.Reconnect.ReconnectPlugin), "P2P Reconnect", "2.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.Reconnect
{
    /// <summary>
    /// Enables mid-match P2P reconnection for clients who crashed and restarted.
    ///
    /// Client-side patches:
    ///   Patch 1 — Postfix on SimulationManager.HasCompletedProtoLobbyClientOrServer():
    ///     Unblocks the ProtoLobby wait loop by detecting _serverGameStarted SyncVar.
    ///   Patch 2 — Finalizer on SimulationManager.LateUpdateExternal():
    ///     Suppresses transient entity errors during resync.
    ///
    /// Server-side patches:
    ///   Patch 3 — Prefix on WartideNetworkManager.OnServerAddPlayer():
    ///     Tracks connection address (Steam ID) → player mapping index.
    ///     When game is running and PlayerId doesn't match, checks address.
    ///   Patch 4 — Postfix on GameAuthority.GetPlayerMappingIndexByPlayerId():
    ///     When a pending address match exists, overrides the lookup result
    ///     so the game's built-in reconnection logic handles everything.
    ///   Patch 5 — Postfix on WartideNetworkManager.OnServerAddPlayer():
    ///     Records address → mapping index after successful player registration.
    /// </summary>
    public class ReconnectPlugin : MelonMod
    {
        // Client-side reflection
        private static PropertyInfo? _serverGameStartedProp;
        private static FieldInfo? _serverGameStartedField;
        private static PropertyInfo? _resyncProgressProp;
        private static FieldInfo? _resyncProgressField;

        // Server-side reflection
        private static PropertyInfo? _gaInstanceProp;
        private static MethodInfo? _getIsGameRunningMethod;
        private static MethodInfo? _getPlayerMappingIndexMethod;

        // Client-side state
        private static bool _isReconnecting;
        private static float _reconnectStartTime;

        // Server-side state: maps connection address (Steam ID string) → player mapping index
        private static readonly Dictionary<string, int> _addressToMappingIndex = new();
        private static int _pendingSteamIdMappingIndex = -1;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[Reconnect] Assembly-CSharp not found");
                return;
            }

            // ================================================================
            // Resolve SimulationManager targets (client-side)
            // ================================================================
            var simType = asm.GetType("Il2CppWartide.SimulationManager");
            if (simType == null)
            {
                MelonLogger.Error("[Reconnect] SimulationManager type not found");
                return;
            }

            _serverGameStartedProp = simType.GetProperty("_serverGameStarted", HarmonyPatcher.FLAGS);
            if (_serverGameStartedProp == null)
                _serverGameStartedField = simType.GetField("_serverGameStarted", HarmonyPatcher.FLAGS);
            if (_serverGameStartedProp == null && _serverGameStartedField == null)
                MelonLogger.Warning("[Reconnect] _serverGameStarted not found (property or field)");

            _resyncProgressProp = simType.GetProperty("_resyncProgressState", HarmonyPatcher.FLAGS);
            if (_resyncProgressProp == null)
                _resyncProgressField = simType.GetField("_resyncProgressState", HarmonyPatcher.FLAGS);
            if (_resyncProgressProp == null && _resyncProgressField == null)
                MelonLogger.Warning("[Reconnect] _resyncProgressState not found (property or field)");

            // Patch 1: HasCompletedProtoLobbyClientOrServer postfix
            var hasCompletedMethod = simType.GetMethod("HasCompletedProtoLobbyClientOrServer", HarmonyPatcher.FLAGS);
            if (hasCompletedMethod != null)
            {
                HarmonyInstance.Patch(hasCompletedMethod,
                    postfix: new HarmonyLib.HarmonyMethod(
                        typeof(ReconnectPlugin).GetMethod(nameof(Postfix_HasCompletedProtoLobby), HarmonyPatcher.FLAGS)));
                MelonLogger.Msg("[Reconnect] Patched HasCompletedProtoLobbyClientOrServer");
            }
            else
            {
                MelonLogger.Warning("[Reconnect] HasCompletedProtoLobbyClientOrServer not found");
            }

            // Patch 2: LateUpdateExternal finalizer
            var lateUpdateExt = simType.GetMethod("LateUpdateExternal", HarmonyPatcher.FLAGS);
            if (lateUpdateExt != null)
            {
                HarmonyInstance.Patch(lateUpdateExt,
                    finalizer: new HarmonyLib.HarmonyMethod(
                        typeof(ReconnectPlugin).GetMethod(nameof(Finalizer_LateUpdateExternal), HarmonyPatcher.FLAGS)));
                MelonLogger.Msg("[Reconnect] Patched LateUpdateExternal");
            }
            else
            {
                MelonLogger.Warning("[Reconnect] LateUpdateExternal not found");
            }

            // ================================================================
            // Resolve GameAuthority targets (server-side)
            // ================================================================
            var gaType = asm.GetType("Il2CppWartide.GameAuthority");
            if (gaType != null)
            {
                _gaInstanceProp = gaType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _getIsGameRunningMethod = gaType.GetMethod("GetIsGameRunning", HarmonyPatcher.FLAGS);
                _getPlayerMappingIndexMethod = gaType.GetMethod("GetPlayerMappingIndexByPlayerId", HarmonyPatcher.FLAGS);

                // Patch 4: GetPlayerMappingIndexByPlayerId postfix
                if (_getPlayerMappingIndexMethod != null)
                {
                    HarmonyInstance.Patch(_getPlayerMappingIndexMethod,
                        postfix: new HarmonyLib.HarmonyMethod(
                            typeof(ReconnectPlugin).GetMethod(nameof(Postfix_GetPlayerMappingIndexByPlayerId), HarmonyPatcher.FLAGS)));
                    MelonLogger.Msg("[Reconnect] Patched GetPlayerMappingIndexByPlayerId");
                }
            }

            // ================================================================
            // Resolve WartideNetworkManager targets (server-side)
            // ================================================================
            var netMgrType = asm.GetType("Il2CppWartide.WartideNetworkManager");
            if (netMgrType != null)
            {
                var addPlayerMethod = netMgrType.GetMethod("OnServerAddPlayer", HarmonyPatcher.FLAGS);
                if (addPlayerMethod != null)
                {
                    // Patch 3: OnServerAddPlayer prefix
                    HarmonyInstance.Patch(addPlayerMethod,
                        prefix: new HarmonyLib.HarmonyMethod(
                            typeof(ReconnectPlugin).GetMethod(nameof(Prefix_OnServerAddPlayer), HarmonyPatcher.FLAGS)),
                        postfix: new HarmonyLib.HarmonyMethod(
                            typeof(ReconnectPlugin).GetMethod(nameof(Postfix_OnServerAddPlayer), HarmonyPatcher.FLAGS)));
                    MelonLogger.Msg("[Reconnect] Patched OnServerAddPlayer (prefix + postfix)");
                }
            }

            MelonLogger.Msg("[Reconnect] Installed (v2: client ProtoLobby bypass + server SteamID matching)");
        }

        // ================================================================
        // CLIENT PATCH 1: Unblock ProtoLobby wait loop
        // ================================================================
        public static void Postfix_HasCompletedProtoLobby(object __instance, ref bool __result)
        {
            if (__result)
                return;

            try
            {
                bool serverGameStarted = GetServerGameStarted(__instance);
                if (!serverGameStarted)
                    return;

                __result = true;

                if (!_isReconnecting)
                {
                    _isReconnecting = true;
                    _reconnectStartTime = Time.time;
                    MelonLogger.Msg("[Reconnect] Reconnection detected — overriding HasCompletedProtoLobby to true");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Reconnect] Postfix_HasCompletedProtoLobby error: {ex.Message}");
            }
        }

        // ================================================================
        // CLIENT PATCH 2: Suppress transient errors during resync
        // ================================================================
        public static Exception? Finalizer_LateUpdateExternal(object __instance, Exception? __exception)
        {
            if (__exception == null)
            {
                if (_isReconnecting)
                {
                    int resyncState = GetResyncProgressState(__instance);
                    if (resyncState >= 4) // ResyncProgress.Finished
                    {
                        _isReconnecting = false;
                        MelonLogger.Msg("[Reconnect] Resync finished — reconnection complete");
                    }
                }
                return null;
            }

            if (!_isReconnecting)
                return __exception;

            if (Time.time - _reconnectStartTime > 120f)
            {
                _isReconnecting = false;
                MelonLogger.Warning("[Reconnect] Reconnection error suppression timed out after 120s");
                return __exception;
            }

            if (__exception is ArgumentOutOfRangeException
                || __exception is IndexOutOfRangeException
                || __exception is InvalidOperationException
                || __exception.GetType().Name.Contains("NativeArray")
                || (__exception.Message != null && __exception.Message.Contains("NativeArray")))
            {
                return null;
            }

            return __exception;
        }

        // ================================================================
        // SERVER PATCH 3: Prefix on OnServerAddPlayer
        // Detects reconnection by connection address when PlayerId doesn't match.
        // Sets _pendingSteamIdMappingIndex so Patch 4 can override the lookup.
        // ================================================================
        public static void Prefix_OnServerAddPlayer(object __instance, object __0, int __1, bool __2, int __3, uint __4, string __5)
        {
            // __0 = conn, __4 = playerId, __5 = playerName
            try
            {
                string? address = GetConnectionAddress(__0);
                if (string.IsNullOrEmpty(address) || address == "localhost")
                    return;

                if (!IsGameRunning())
                    return;

                // Check if the PlayerId already matches an existing player
                int existingIndex = CallGetPlayerMappingIndexByPlayerId(__4);
                if (existingIndex >= 0)
                    return; // Normal reconnection with same PlayerId, let original handle

                // PlayerId doesn't match — try address (Steam ID) matching
                if (_addressToMappingIndex.TryGetValue(address, out int mappingIndex))
                {
                    _pendingSteamIdMappingIndex = mappingIndex;
                    MelonLogger.Msg($"[Reconnect] Server: Address match for '{__5}' (address={address}, mappingIndex={mappingIndex}). " +
                                    $"Original PlayerId in slot doesn't match new PlayerId {__4} — overriding lookup.");
                }
                else
                {
                    MelonLogger.Warning($"[Reconnect] Server: No address match for '{__5}' (address={address}, playerId={__4}). " +
                                        "Player not previously tracked — cannot reconnect.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Reconnect] Prefix_OnServerAddPlayer error: {ex.Message}");
                _pendingSteamIdMappingIndex = -1;
            }
        }

        // ================================================================
        // SERVER PATCH 4: Postfix on GetPlayerMappingIndexByPlayerId
        // If the normal lookup returned -1 but we have a pending address match,
        // override the result so the game's reconnection code handles everything.
        // ================================================================
        public static void Postfix_GetPlayerMappingIndexByPlayerId(uint __0, ref int __result)
        {
            if (__result >= 0)
                return; // Already found by PlayerId, no need to override

            if (_pendingSteamIdMappingIndex < 0)
                return; // No pending match

            __result = _pendingSteamIdMappingIndex;
            _pendingSteamIdMappingIndex = -1;
            MelonLogger.Msg($"[Reconnect] Server: Overriding PlayerId lookup — returning mapping index {__result} (matched by address)");
        }

        // ================================================================
        // SERVER PATCH 5: Postfix on OnServerAddPlayer
        // Records address → mapping index after player registration.
        // ================================================================
        public static void Postfix_OnServerAddPlayer(object __instance, object __0, int __1, bool __2, int __3, uint __4, string __5)
        {
            // __0 = conn, __4 = playerId
            try
            {
                string? address = GetConnectionAddress(__0);
                if (string.IsNullOrEmpty(address) || address == "localhost")
                    return;

                int index = CallGetPlayerMappingIndexByPlayerId(__4);
                if (index >= 0)
                {
                    _addressToMappingIndex[address] = index;
                    MelonLogger.Msg($"[Reconnect] Server: Tracked address={address} → mappingIndex={index} (playerId={__4})");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Reconnect] Postfix_OnServerAddPlayer error: {ex.Message}");
            }
            finally
            {
                _pendingSteamIdMappingIndex = -1;
            }
        }

        // ================================================================
        // Client helpers
        // ================================================================
        private static bool GetServerGameStarted(object instance)
        {
            if (_serverGameStartedProp != null)
                return (bool)_serverGameStartedProp.GetValue(instance)!;
            if (_serverGameStartedField != null)
                return (bool)_serverGameStartedField.GetValue(instance)!;
            return false;
        }

        private static int GetResyncProgressState(object instance)
        {
            object? val = null;
            if (_resyncProgressProp != null)
                val = _resyncProgressProp.GetValue(instance);
            else if (_resyncProgressField != null)
                val = _resyncProgressField.GetValue(instance);

            if (val == null) return 0;
            return Convert.ToInt32(val);
        }

        // ================================================================
        // Server helpers
        // ================================================================
        private static string? GetConnectionAddress(object conn)
        {
            try
            {
                // Mirror's NetworkConnection.address — for Steam P2P this is the Steam ID string
                var prop = conn.GetType().GetProperty("address", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                    return prop.GetValue(conn)?.ToString();

                // Fallback: check base types
                var type = conn.GetType().BaseType;
                while (type != null)
                {
                    prop = type.GetProperty("address", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                        return prop.GetValue(conn)?.ToString();
                    type = type.BaseType;
                }
            }
            catch { }
            return null;
        }

        private static bool IsGameRunning()
        {
            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null || _getIsGameRunningMethod == null) return false;
                return (bool)_getIsGameRunningMethod.Invoke(gaInstance, null)!;
            }
            catch { return false; }
        }

        private static int CallGetPlayerMappingIndexByPlayerId(uint playerId)
        {
            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null || _getPlayerMappingIndexMethod == null) return -1;
                return (int)_getPlayerMappingIndexMethod.Invoke(gaInstance, new object[] { playerId })!;
            }
            catch { return -1; }
        }
    }
}
