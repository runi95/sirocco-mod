using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;
using UnityEngine;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.Reconnect.ReconnectPlugin), "P2P Reconnect", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.Reconnect
{
    /// <summary>
    /// Enables mid-match reconnection in P2P mode.
    ///
    /// Server-side: After mid-match AddPlayer, adds observer and sends RpcNotifyGameStarted.
    ///
    /// Client-side: When RpcNotifyGameStarted arrives, state goes 10→91. But Update
    /// immediately transitions 91→100 (via native code Harmony can't intercept), skipping
    /// LatentInitialize's loading phases. Fix: patch Update (managed MonoBehaviour callback)
    /// with a postfix that forces state back to 91 during reconnection, giving LatentInitialize
    /// time to run. Once LatentInitialize completes naturally, it sets state to 100 itself.
    /// </summary>
    public class ReconnectPlugin : MelonMod
    {
        // Server-side
        private static PropertyInfo? _gaInstanceProp;
        private static MethodInfo? _getIsGameRunningMethod;
        private static PropertyInfo? _simManagerProp;
        private static MethodInfo? _rpcNotifyGameStartedMethod;
        private static PropertyInfo? _netIdentityProp;
        private static MethodInfo? _addObserverMethod;

        // Client-side
        private static PropertyInfo? _resyncProgressProp;
        private static PropertyInfo? _gameAuthorityStateProp;
        private static MethodInfo? _isServerOrSinglePlayerMethod;
        private static bool _hasCompletedInitialJoin;
        private static bool _holdStateAt91;
        private static float _holdStartTime;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[Reconnect] Assembly-CSharp not found");
                return;
            }

            var gaType = asm.GetType("Il2CppWartide.GameAuthority");
            if (gaType != null)
            {
                _gaInstanceProp = gaType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _getIsGameRunningMethod = gaType.GetMethod("GetIsGameRunning", HarmonyPatcher.FLAGS);
                _simManagerProp = gaType.GetProperty("_simulationManager", HarmonyPatcher.FLAGS);
                _isServerOrSinglePlayerMethod = gaType.GetMethod("IsServerOrSinglePlayer", BindingFlags.Public | BindingFlags.Static);
                _gameAuthorityStateProp = gaType.GetProperty("_gameAuthorityState", HarmonyPatcher.FLAGS);

                // Patch Update to hold state at 91 during reconnection
                var updateMethod = gaType.GetMethod("Update", HarmonyPatcher.FLAGS);
                if (updateMethod != null)
                {
                    HarmonyInstance.Patch(updateMethod,
                        postfix: new HarmonyLib.HarmonyMethod(typeof(ReconnectPlugin).GetMethod(nameof(Postfix_Update), HarmonyPatcher.FLAGS)));
                    MelonLogger.Msg("[Reconnect] Patched GameAuthority.Update");
                }
            }

            var simType = asm.GetType("Il2CppWartide.SimulationManager");
            if (simType != null)
            {
                _rpcNotifyGameStartedMethod = simType.GetMethod("RpcNotifyGameStarted", HarmonyPatcher.FLAGS);
                _resyncProgressProp = simType.GetProperty("_resyncProgressState", HarmonyPatcher.FLAGS);

                var lateUpdateExt = simType.GetMethod("LateUpdateExternal", HarmonyPatcher.FLAGS);
                if (lateUpdateExt != null)
                {
                    HarmonyInstance.Patch(lateUpdateExt,
                        finalizer: new HarmonyLib.HarmonyMethod(typeof(ReconnectPlugin).GetMethod(nameof(Finalizer_LateUpdateExternal), HarmonyPatcher.FLAGS)));
                    MelonLogger.Msg("[Reconnect] Patched LateUpdateExternal");
                }

                // Detect when RpcNotifyGameStarted fires to enable state hold
                var rpcHandler = simType.GetMethod("UserCode_RpcNotifyGameStarted", HarmonyPatcher.FLAGS);
                if (rpcHandler != null)
                {
                    HarmonyInstance.Patch(rpcHandler,
                        prefix: new HarmonyLib.HarmonyMethod(typeof(ReconnectPlugin).GetMethod(nameof(Prefix_UserCode_RpcNotifyGameStarted), HarmonyPatcher.FLAGS)));
                    MelonLogger.Msg("[Reconnect] Patched UserCode_RpcNotifyGameStarted");
                }
            }

            // Resolve Mirror observer types
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });
            var nbType = allTypes.FirstOrDefault(t =>
                t.FullName == "Mirror.NetworkBehaviour" || t.FullName == "Il2CppMirror.NetworkBehaviour");
            if (nbType != null)
                _netIdentityProp = nbType.GetProperty("netIdentity", HarmonyPatcher.FLAGS);
            var niType = allTypes.FirstOrDefault(t =>
                t.FullName == "Mirror.NetworkIdentity" || t.FullName == "Il2CppMirror.NetworkIdentity");
            if (niType != null)
                _addObserverMethod = niType.GetMethod("AddObserver", HarmonyPatcher.FLAGS);

            // Server-side: patch OnServerAddPlayer
            var netMgrType = asm.GetType("Il2CppWartide.WartideNetworkManager");
            if (netMgrType != null)
            {
                var addPlayerMethod = netMgrType.GetMethod("OnServerAddPlayer", HarmonyPatcher.FLAGS);
                if (addPlayerMethod != null)
                {
                    HarmonyInstance.Patch(addPlayerMethod,
                        postfix: new HarmonyLib.HarmonyMethod(typeof(ReconnectPlugin).GetMethod(nameof(Postfix_OnServerAddPlayer), HarmonyPatcher.FLAGS)));
                    MelonLogger.Msg("[Reconnect] Patched OnServerAddPlayer");
                }
            }

            MelonLogger.Msg("[Reconnect] Installed");
        }

        // ================================================================
        // CLIENT-SIDE: Track RPC calls to detect reconnection
        // ================================================================
        private static int _rpcCount;

        public static void Prefix_UserCode_RpcNotifyGameStarted()
        {
            _rpcCount++;

            // Initial join: this fires once. Reconnection: fires again.
            // On a restarted client, _rpcCount resets to 0, so first call = 1 (initial), second = reconnection.
            // But _hasCompletedInitialJoin tracks if we've ever had a successful game.
            if (_hasCompletedInitialJoin && !_holdStateAt91)
            {
                _holdStateAt91 = true;
                _holdStartTime = Time.time;
                MelonLogger.Msg("[Reconnect] Client: Reconnection RPC detected, holding state at 91 for LatentInitialize");
            }
        }

        // ================================================================
        // CLIENT-SIDE: After Update runs, force state back to 91 if needed
        // ================================================================
        public static void Postfix_Update(object __instance)
        {
            if (!_holdStateAt91) return;

            try
            {
                bool isServer = (bool)(_isServerOrSinglePlayerMethod?.Invoke(null, null) ?? true);
                if (isServer) return;

                // Timeout after 60 seconds
                if (Time.time - _holdStartTime > 60f)
                {
                    _holdStateAt91 = false;
                    MelonLogger.Msg("[Reconnect] Client: State hold timed out");
                    return;
                }

                if (_gameAuthorityStateProp == null) return;

                int state = Convert.ToInt32(_gameAuthorityStateProp.GetValue(__instance));

                // If Update transitioned past 91 (to 100), force it back to 91
                // so LatentInitialize can process the loading phases
                if (state == 100) // GameRunning
                {
                    _gameAuthorityStateProp.SetValue(__instance, 91); // GameAwaitingServerStarted
                }
            }
            catch { }
        }

        // ================================================================
        // CLIENT-SIDE: Suppress NativeArray errors during resync
        // ================================================================
        public static Exception? Finalizer_LateUpdateExternal(object __instance, Exception? __exception)
        {
            if (__exception == null)
            {
                if (!_hasCompletedInitialJoin)
                    _hasCompletedInitialJoin = true;

                // LatentInitialize completed and game is running — stop holding state
                if (_holdStateAt91)
                {
                    _holdStateAt91 = false;
                    MelonLogger.Msg("[Reconnect] Client: LateUpdateExternal succeeded, releasing state hold");
                }
                return null;
            }

            if (!_hasCompletedInitialJoin) return __exception;

            // Suppress transient errors during reconnection resync
            if (_holdStateAt91) return null;
            if (__exception is ArgumentOutOfRangeException) return null;

            return __exception;
        }

        // ================================================================
        // SERVER-SIDE: Send game start RPC to reconnecting client
        // ================================================================
        public static void Postfix_OnServerAddPlayer(object __instance, object __0)
        {
            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null || _getIsGameRunningMethod == null) return;

                bool isRunning = (bool)_getIsGameRunningMethod.Invoke(gaInstance, null)!;
                if (!isRunning) return;

                var connIdProp = __0.GetType().GetProperty("connectionId", HarmonyPatcher.FLAGS);
                int connId = connIdProp != null ? (int)connIdProp.GetValue(__0)! : -1;
                if (connId == 0) return;

                MelonLogger.Msg($"[Reconnect] Server: Mid-match AddPlayer for connId={connId}");

                if (_simManagerProp != null && _rpcNotifyGameStartedMethod != null)
                {
                    var simManager = _simManagerProp.GetValue(gaInstance);
                    if (simManager != null)
                    {
                        if (_netIdentityProp != null && _addObserverMethod != null)
                        {
                            var netIdentity = _netIdentityProp.GetValue(simManager);
                            if (netIdentity != null)
                                _addObserverMethod.Invoke(netIdentity, new[] { __0 });
                        }

                        MelonCoroutines.Start(DelayedGameStartRpc(simManager, connId));
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Reconnect] OnServerAddPlayer postfix error: {ex.Message}");
            }
        }

        private static IEnumerator DelayedGameStartRpc(object simManager, int connId)
        {
            yield return new WaitForSeconds(2f);

            try
            {
                _rpcNotifyGameStartedMethod?.Invoke(simManager, null);
                MelonLogger.Msg($"[Reconnect] Server: Sent delayed RpcNotifyGameStarted for connId={connId}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[Reconnect] Delayed RPC error: {ex.Message}");
            }
        }
    }
}
