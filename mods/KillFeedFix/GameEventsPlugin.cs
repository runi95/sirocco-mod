using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SiroccoMod;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.KillFeedFix.GameEventsPlugin), "P2P Game Events Fix", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.KillFeedFix
{
    /// <summary>
    /// Fixes kill feed toasts, connection notifications, and match timeout warnings
    /// not appearing in P2P mode.
    ///
    /// The base game's GameEventBroadcaster.Trigger*Event methods only send RPCs when
    /// IsServerOnly() is true, and only call the local HUD when IsSinglePlayer() is true.
    /// In P2P the host starts via StartSinglePlayer so IsSinglePlayer() returns true, but
    /// remote clients never get the RPC. Additionally, the GameEventBroadcaster's
    /// NetworkIdentity may lack remote clients as observers, so RPCs don't reach them.
    ///
    /// This patch ensures all server connections are registered as observers on the
    /// broadcaster before sending, so the RPC reliably reaches all clients.
    /// </summary>
    public class GameEventsPlugin : MelonMod
    {
        private static PropertyInfo? _networkServerActiveProp;
        private static FieldInfo? _serverConnectionsField;

        private static PropertyInfo? _broadcasterInstanceField;
        private static MethodInfo? _rpcOnKillEventMethod;
        private static MethodInfo? _rpcOnConnectionEventMethod;
        private static MethodInfo? _rpcOnMatchTimeoutMethod;

        private static PropertyInfo? _netIdentityProp;
        private static MethodInfo? _addObserverMethod;

        private static bool _observersFixed;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[GameEvents] Assembly-CSharp not found");
                return;
            }

            var broadcasterType = asm.GetType("Il2CppWartide.GameEventBroadcaster");
            if (broadcasterType == null) { MelonLogger.Warning("[GameEvents] GameEventBroadcaster not found"); return; }

            CacheReflection(broadcasterType);
            ResolveNetworkTypes();

            PatchMethod(broadcasterType, "TriggerKillEvent", nameof(Prefix_TriggerKillEvent));
            PatchMethod(broadcasterType, "TriggerConnectionEvent", nameof(Prefix_TriggerConnectionEvent));
            PatchMethod(broadcasterType, "TriggerMatchTimeoutWarningEvent", nameof(Prefix_TriggerMatchTimeoutWarningEvent));

            MelonLogger.Msg("[GameEvents] Installed");
        }

        private void CacheReflection(Type broadcasterType)
        {
            _broadcasterInstanceField = broadcasterType.GetProperty("_instance", HarmonyPatcher.FLAGS);
            _rpcOnKillEventMethod = broadcasterType.GetMethod("RpcOnKillEvent", HarmonyPatcher.FLAGS);
            _rpcOnConnectionEventMethod = broadcasterType.GetMethod("RPCOnConnectionEvent", HarmonyPatcher.FLAGS);
            _rpcOnMatchTimeoutMethod = broadcasterType.GetMethod("RPCOnMatchTimeoutWarningEvent", HarmonyPatcher.FLAGS);
        }

        private void PatchMethod(Type type, string methodName, string prefixName)
        {
            var method = type.GetMethod(methodName, HarmonyPatcher.FLAGS);
            if (method == null)
            {
                MelonLogger.Warning($"[GameEvents] {methodName} not found");
                return;
            }

            var prefix = new HarmonyLib.HarmonyMethod(typeof(GameEventsPlugin).GetMethod(prefixName, HarmonyPatcher.FLAGS));
            HarmonyInstance.Patch(method, prefix: prefix);
            MelonLogger.Msg($"[GameEvents] Patched {methodName}");
        }

        private static void ResolveNetworkTypes()
        {
            try
            {
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });

                var nsType = allTypes.FirstOrDefault(t =>
                    t.FullName == "Mirror.NetworkServer" || t.FullName == "Il2CppMirror.NetworkServer");
                if (nsType != null)
                {
                    _networkServerActiveProp = nsType.GetProperty("active", BindingFlags.Public | BindingFlags.Static);
                    _serverConnectionsField = nsType.GetField("connections", BindingFlags.Public | BindingFlags.Static);
                }

                var nbType = allTypes.FirstOrDefault(t =>
                    t.FullName == "Mirror.NetworkBehaviour" || t.FullName == "Il2CppMirror.NetworkBehaviour");
                if (nbType != null)
                {
                    _netIdentityProp = nbType.GetProperty("netIdentity", HarmonyPatcher.FLAGS);
                }

                var niType = allTypes.FirstOrDefault(t =>
                    t.FullName == "Mirror.NetworkIdentity" || t.FullName == "Il2CppMirror.NetworkIdentity");
                if (niType != null)
                {
                    _addObserverMethod = niType.GetMethod("AddObserver", HarmonyPatcher.FLAGS);
                }
            }
            catch { }
        }

        private static bool IsNetworkServerActive()
        {
            if (_networkServerActiveProp == null)
            {
                ResolveNetworkTypes();
                if (_networkServerActiveProp == null) return false;
            }

            return (bool)(_networkServerActiveProp.GetValue(null) ?? false);
        }

        private static object? GetBroadcasterInstance()
        {
            return _broadcasterInstanceField?.GetValue(null);
        }

        private static void EnsureBroadcasterHasObservers(object broadcasterInstance)
        {
            if (_observersFixed) return;
            _observersFixed = true;

            if (_netIdentityProp == null || _addObserverMethod == null || _serverConnectionsField == null)
                return;

            try
            {
                var netIdentity = _netIdentityProp.GetValue(broadcasterInstance);
                if (netIdentity == null) return;

                var connections = _serverConnectionsField.GetValue(null);
                if (connections == null) return;

                var valuesProperty = connections.GetType().GetProperty("Values", HarmonyPatcher.FLAGS);
                var values = valuesProperty?.GetValue(connections);
                if (values == null) return;

                foreach (var conn in (IEnumerable)values)
                {
                    try { _addObserverMethod.Invoke(netIdentity, new[] { conn }); }
                    catch { }
                }
            }
            catch { }
        }

        public static bool Prefix_TriggerKillEvent(object __0)
        {
            if (!IsNetworkServerActive()) return true;

            try
            {
                var instance = GetBroadcasterInstance();
                if (instance == null) return true;

                EnsureBroadcasterHasObservers(instance);
                _rpcOnKillEventMethod?.Invoke(instance, new[] { __0 });
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[GameEvents] TriggerKillEvent error: {ex.Message}");
                return true;
            }
        }

        public static bool Prefix_TriggerConnectionEvent(object __0)
        {
            if (!IsNetworkServerActive()) return true;

            try
            {
                var instance = GetBroadcasterInstance();
                if (instance == null) return true;

                EnsureBroadcasterHasObservers(instance);
                _rpcOnConnectionEventMethod?.Invoke(instance, new[] { __0 });
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[GameEvents] TriggerConnectionEvent error: {ex.Message}");
                return true;
            }
        }

        public static bool Prefix_TriggerMatchTimeoutWarningEvent(int __0)
        {
            if (!IsNetworkServerActive()) return true;

            try
            {
                var instance = GetBroadcasterInstance();
                if (instance == null) return true;

                EnsureBroadcasterHasObservers(instance);
                _rpcOnMatchTimeoutMethod?.Invoke(instance, new object[] { __0 });
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[GameEvents] TriggerMatchTimeoutWarningEvent error: {ex.Message}");
                return true;
            }
        }
    }
}
