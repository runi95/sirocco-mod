using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using SiroccoMod;
using SiroccoMod.Helpers;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.Chat.ChatPlugin), "P2P Chat Fix", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.Chat
{
    /// <summary>
    /// Fixes in-game text chat for P2P mode.
    ///
    /// Harmony patches (managed C# wrappers):
    ///   - CommandSendChatMessage: intercept local player sending chat
    ///   - AddChatMessage: fix "Splayer" display name to real name
    ///
    /// Native hook (IL2CPP method directly):
    ///   - InvokeUserCode_CommandSendChatMessage: Mirror RPC dispatch for remote client commands
    /// </summary>
    public class ChatPlugin : MelonMod
    {
        // Cached reflection handles
        private static Type? _gameAuthorityType;
        private static PropertyInfo? _gaInstanceProp;
        private static MethodInfo? _getMappingsMethod;
        private static MethodInfo? _rpcBroadcastMethod;
        private static PropertyInfo? _mappingControllerProp;
        private static PropertyInfo? _mappingDisplayNameProp;
        private static PropertyInfo? _mappingPlayerIdProp;

        // HUD_Messages
        private static MethodInfo? _addChatMessageMethod;
        private static PropertyInfo? _hudInstanceProp;
        private static FieldInfo? _hudInstanceField;

        // Mirror NetworkReader
        private static MethodInfo? _readerReadStringMethod;
        private static Type? _networkReaderType;
        private static ConstructorInfo? _networkReaderPtrCtor;

        // Reentrancy guard for AddChatMessage
        private static bool _insideOurAddChat;

        // Local player cache
        private static string? _cachedLocalName;
        private static uint _cachedLocalId;

        // Native hook delegate (prevent GC)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_InvokeUserCode(IntPtr obj, IntPtr reader, IntPtr senderConn, IntPtr methodInfo);
        private static d_InvokeUserCode? _originalInvokeUserCode;
        private static d_InvokeUserCode? _hookDelegate;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[ChatPatch] Assembly-CSharp not found");
                return;
            }

            var pcType = asm.GetType("Il2CppWartide.PlayerController");
            var hudType = asm.GetType("Il2CppWartide.HUD_Messages");
            _gameAuthorityType = asm.GetType("Il2CppWartide.GameAuthority");

            if (pcType == null) { MelonLogger.Warning("[ChatPatch] PlayerController not found"); return; }
            if (hudType == null) { MelonLogger.Warning("[ChatPatch] HUD_Messages not found"); return; }

            CacheReflection(asm, pcType, hudType);
            ResolveReadStringMethod();

            PatchManaged(pcType, hudType);
            InstallNativeHook(pcType);

            MelonLogger.Msg("[ChatPatch] Installed");
        }

        // ================================================================
        // Setup
        // ================================================================

        private void CacheReflection(Assembly asm, Type pcType, Type hudType)
        {
            if (_gameAuthorityType != null)
            {
                _gaInstanceProp = _gameAuthorityType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _getMappingsMethod = _gameAuthorityType.GetMethod("GetPlayerConnectionMappings", BindingFlags.Public | BindingFlags.Instance);
            }

            _rpcBroadcastMethod = pcType.GetMethod("RpcBroadcastChatMessage", HarmonyPatcher.FLAGS);

            var mappingType = asm.GetType("Il2CppWartide.PlayerConnectionMapping");
            if (mappingType != null)
            {
                _mappingControllerProp = mappingType.GetProperty("Controller", BindingFlags.Public | BindingFlags.Instance);
                _mappingDisplayNameProp = mappingType.GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance);
                _mappingPlayerIdProp = mappingType.GetProperty("PlayerId", BindingFlags.Public | BindingFlags.Instance);
            }

            _addChatMessageMethod = hudType.GetMethod("AddChatMessage", HarmonyPatcher.FLAGS);
            _hudInstanceProp = hudType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _hudInstanceField = hudType.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
        }

        private static void ResolveReadStringMethod()
        {
            try
            {
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });

                var readerType = allTypes.FirstOrDefault(t =>
                    t.FullName == "Mirror.NetworkReader" || t.FullName == "Il2CppMirror.NetworkReader");

                if (readerType != null)
                {
                    _networkReaderType = readerType;
                    _networkReaderPtrCtor = readerType.GetConstructor(new[] { typeof(IntPtr) });
                    _readerReadStringMethod = readerType.GetMethod("ReadString",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                    if (_readerReadStringMethod != null) return;
                }

                // Try extension method: NetworkReaderExtensions.ReadString(NetworkReader)
                var extTypes = allTypes.Where(t =>
                    t.Name.Contains("NetworkReader") && t.Name.Contains("Extension"));
                foreach (var extType in extTypes)
                {
                    var method = extType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "ReadString" && m.GetParameters().Length == 1);
                    if (method != null)
                    {
                        _readerReadStringMethod = method;
                        return;
                    }
                }

                MelonLogger.Warning("[ChatPatch] NetworkReader.ReadString not found");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ChatPatch] Error resolving ReadString: {ex.Message}");
            }
        }

        // ================================================================
        // Harmony patches
        // ================================================================

        private void PatchManaged(Type pcType, Type hudType)
        {
            TryPatch(pcType, "CommandSendChatMessage", nameof(Prefix_CommandSendChat));
            TryPatch(hudType, "AddChatMessage", nameof(Prefix_AddChatMessage));
        }

        private void TryPatch(Type type, string methodName, string prefixName)
        {
            var method = type.GetMethod(methodName, HarmonyPatcher.FLAGS);
            if (method == null)
            {
                MelonLogger.Warning($"[ChatPatch] Method not found: {type.Name}.{methodName}");
                return;
            }

            var prefix = new HarmonyLib.HarmonyMethod(typeof(ChatPlugin).GetMethod(prefixName, HarmonyPatcher.FLAGS));
            HarmonyInstance.Patch(method, prefix: prefix);
            MelonLogger.Msg($"[ChatPatch] Patched {type.Name}.{methodName}");
        }

        public static bool Prefix_CommandSendChat(object __instance, string __0)
        {
            try
            {
                if (string.IsNullOrEmpty(__0)) return false;

                MelonLogger.Msg($"[ChatPatch] '{GetLocalName()}' says: {__0}");

                if (!IsServerActive()) return true;

                BroadcastChat(GetLocalId(), GetLocalName(), __0);
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[ChatPatch] CommandSendChat error: {ex}");
                return true;
            }
        }

        public static bool Prefix_AddChatMessage(object __instance, uint __0, string __1, string __2, bool __3)
        {
            if (_insideOurAddChat) return true;
            if (__1 != "Splayer" && !string.IsNullOrEmpty(__1)) return true;

            string fixedName = __3 ? GetLocalName() : ResolveNameByAccountId(__0);
            uint fixedId = __3 ? GetLocalId() : __0;

            CallAddChatMessage(__instance, fixedId, fixedName, __2, __3);
            return false;
        }

        // ================================================================
        // Native hook
        // ================================================================

        private static unsafe void InstallNativeHook(Type pcType)
        {
            try
            {
                var nativeField = pcType.GetField(
                    "NativeMethodInfoPtr_InvokeUserCode_CommandSendChatMessage__String_Protected_Static_Void_NetworkBehaviour_NetworkReader_NetworkConnectionToClient_0",
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (nativeField == null)
                {
                    MelonLogger.Warning("[ChatPatch] NativeMethodInfoPtr for InvokeUserCode not found");
                    return;
                }

                IntPtr methodInfoPtr = (IntPtr)nativeField.GetValue(null)!;
                if (methodInfoPtr == IntPtr.Zero) return;

                IntPtr methodPtr = *(IntPtr*)methodInfoPtr;
                if (methodPtr == IntPtr.Zero) return;

                _hookDelegate = new d_InvokeUserCode(NativeHook_InvokeUserCode);
                IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);

                IntPtr originalPtr = methodPtr;
#pragma warning disable CS0618
                MelonUtils.NativeHookAttach((IntPtr)(&originalPtr), hookPtr);
#pragma warning restore CS0618
                _originalInvokeUserCode = Marshal.GetDelegateForFunctionPointer<d_InvokeUserCode>(originalPtr);

                MelonLogger.Msg("[ChatPatch] Native hook installed for chat commands");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[ChatPatch] Native hook failed: {ex}");
            }
        }

        private static void NativeHook_InvokeUserCode(IntPtr obj, IntPtr reader, IntPtr senderConn, IntPtr methodInfo)
        {
            try
            {
                string message = ReadStringFromReader(reader);
                if (string.IsNullOrEmpty(message)) return;

                string senderName = "Player";
                uint senderAccountId = 0;
                ResolveSenderFromInstance(obj, ref senderName, ref senderAccountId);

                BroadcastChat(senderAccountId, senderName, message);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[ChatPatch] NativeHook error: {ex}");
                try { _originalInvokeUserCode?.Invoke(obj, reader, senderConn, methodInfo); } catch { }
            }
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static string ReadStringFromReader(IntPtr readerPtr)
        {
            if (readerPtr == IntPtr.Zero || _readerReadStringMethod == null) return "";

            try
            {
                object? readerObj = _networkReaderPtrCtor?.Invoke(new object[] { readerPtr });
                if (readerObj == null) return "";

                var result = _readerReadStringMethod.IsStatic
                    ? _readerReadStringMethod.Invoke(null, new[] { readerObj })
                    : _readerReadStringMethod.Invoke(readerObj, null);

                return result?.ToString() ?? "";
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ChatPatch] ReadString failed: {ex.Message}");
                return "";
            }
        }

        private static void ResolveSenderFromInstance(IntPtr instancePtr, ref string name, ref uint accountId)
        {
            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null || _getMappingsMethod == null) return;

                var mappings = _getMappingsMethod.Invoke(gaInstance, null);
                if (mappings == null) return;

                foreach (var mapping in IL2CppArrayHelper.Iterate(mappings))
                {
                    var controller = _mappingControllerProp?.GetValue(mapping);
                    if (controller == null) continue;

                    IntPtr controllerPtr = controller is Il2CppObjectBase il2cppObj
                        ? IL2CPP.Il2CppObjectBaseToPtr(il2cppObj)
                        : IntPtr.Zero;

                    if (controllerPtr == instancePtr)
                    {
                        var n = _mappingDisplayNameProp?.GetValue(mapping)?.ToString();
                        if (!string.IsNullOrEmpty(n)) name = n;
                        accountId = (uint)(_mappingPlayerIdProp?.GetValue(mapping) ?? 0u);
                        return;
                    }
                }
            }
            catch { }
        }

        private static void BroadcastChat(uint senderAccountId, string senderName, string message)
        {
            var gaInstance = _gaInstanceProp?.GetValue(null);
            if (gaInstance == null || _getMappingsMethod == null || _rpcBroadcastMethod == null) return;

            var mappings = _getMappingsMethod.Invoke(gaInstance, null);
            if (mappings == null) return;

            int sent = 0;
            foreach (var mapping in IL2CppArrayHelper.Iterate(mappings))
            {
                try
                {
                    var controller = _mappingControllerProp?.GetValue(mapping);
                    if (controller == null) continue;

                    var connProp = controller.GetType().GetProperty("connectionToClient", HarmonyPatcher.FLAGS);
                    var conn = connProp?.GetValue(controller);
                    if (conn == null) continue;

                    _rpcBroadcastMethod.Invoke(controller, new object[]
                    {
                        conn, senderAccountId, senderName, message, false
                    });
                    sent++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ChatPatch] Broadcast failed for a player: {ex.Message}");
                }
            }
            MelonLogger.Msg($"[ChatPatch] Broadcast to {sent} players");
        }

        private static void CallAddChatMessage(object hudInstance, uint accountId,
            string senderName, string message, bool ownMessage)
        {
            if (_addChatMessageMethod == null) return;
            try
            {
                _insideOurAddChat = true;
                _addChatMessageMethod.Invoke(hudInstance, new object[] { accountId, senderName, message, ownMessage });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ChatPatch] CallAddChatMessage failed: {ex.Message}");
            }
            finally
            {
                _insideOurAddChat = false;
            }
        }

        private static string ResolveNameByAccountId(uint accountId)
        {
            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null || _getMappingsMethod == null) return "Player";

                var mappings = _getMappingsMethod.Invoke(gaInstance, null);
                if (mappings == null) return "Player";

                foreach (var mapping in IL2CppArrayHelper.Iterate(mappings))
                {
                    var pid = (uint)(_mappingPlayerIdProp?.GetValue(mapping) ?? 0u);
                    if (pid == accountId)
                    {
                        var name = _mappingDisplayNameProp?.GetValue(mapping)?.ToString();
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                }
            }
            catch { }

            return "Player";
        }

        private static string GetLocalName()
        {
            if (_cachedLocalName != null) return _cachedLocalName;
            ResolveLocalPlayer();
            return _cachedLocalName ?? "Unknown";
        }

        private static uint GetLocalId()
        {
            if (_cachedLocalId != 0) return _cachedLocalId;
            ResolveLocalPlayer();
            return _cachedLocalId;
        }

        private static void ResolveLocalPlayer()
        {
            try
            {
                var gaInstance = _gaInstanceProp?.GetValue(null);
                if (gaInstance == null || _getMappingsMethod == null) return;

                var mappings = _getMappingsMethod.Invoke(gaInstance, null);
                if (mappings == null) return;

                foreach (var mapping in IL2CppArrayHelper.Iterate(mappings))
                {
                    var controller = _mappingControllerProp?.GetValue(mapping);
                    if (controller == null) continue;

                    var isLocalProp = controller.GetType().GetProperty("isLocalPlayer", BindingFlags.Public | BindingFlags.Instance);
                    if (isLocalProp == null) continue;

                    bool isLocal = (bool)(isLocalProp.GetValue(controller) ?? false);
                    if (!isLocal) continue;

                    var name = _mappingDisplayNameProp?.GetValue(mapping)?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        _cachedLocalName = name;
                        _cachedLocalId = (uint)(_mappingPlayerIdProp?.GetValue(mapping) ?? 0u);
                    }
                    return;
                }
            }
            catch { }
        }

        private static bool IsServerActive()
        {
            try
            {
                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } });

                var nsType = allTypes.FirstOrDefault(t =>
                    t.FullName == "Mirror.NetworkServer" || t.FullName == "Il2CppMirror.NetworkServer");
                if (nsType == null) return false;

                var activeProp = nsType.GetProperty("active", BindingFlags.Public | BindingFlags.Static);
                return activeProp != null && (bool)(activeProp.GetValue(null) ?? false);
            }
            catch { return false; }
        }
    }
}
