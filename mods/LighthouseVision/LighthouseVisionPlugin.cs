using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using SiroccoMod;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.LighthouseVision.LighthouseVisionPlugin), "P2P Lighthouse Vision Fix", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.LighthouseVision
{
    /// <summary>
    /// Fixes lighthouse vision not working for the non-host team in P2P mode.
    ///
    /// In P2P mode, ApplyMinimapAndVisionSources checks IsServerOnly() — which is false.
    /// It then calls IsTeamAllyOfLocalPlayer() and for enemy lighthouses sets IsActive=false,
    /// so they never generate vision. The non-host team can see the lighthouse is claimed but
    /// gets no actual vision from it.
    ///
    /// Fix: Native hook on IsTeamAllyOfLocalPlayer() to always return true. This makes all
    /// lighthouse code take the "ally" path, which sets ShowTeamA/ShowTeamB based on actual
    /// ownership — the same behavior as the server-only (dedicated server) path. The vision
    /// job then correctly filters by each player's team.
    ///
    /// IsTeamAllyOfLocalPlayer is only called from 3 locations, all in lighthouse/outpost code.
    /// Minor side effect: all owned lighthouses show ally-colored material instead of
    /// distinguishing ally vs enemy.
    /// </summary>
    public class LighthouseVisionPlugin : MelonMod
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte d_IsTeamAllyOfLocalPlayer(byte param1, IntPtr methodInfo);
        private static d_IsTeamAllyOfLocalPlayer? _original;
        private static d_IsTeamAllyOfLocalPlayer? _hookDelegate;

        public override void OnInitializeMelon()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");

            if (asm == null)
            {
                MelonLogger.Error("[LighthouseVision] Assembly-CSharp not found");
                return;
            }

            var gaType = asm.GetType("Il2CppWartide.GameAuthority");
            if (gaType == null)
            {
                MelonLogger.Warning("[LighthouseVision] GameAuthority not found");
                return;
            }

            InstallNativeHook(gaType);
        }

        private static unsafe void InstallNativeHook(Type gaType)
        {
            try
            {
                var nativeField = gaType.GetField(
                    "NativeMethodInfoPtr_IsTeamAllyOfLocalPlayer_Public_Static_Boolean_Boolean_0",
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (nativeField == null)
                {
                    MelonLogger.Warning("[LighthouseVision] IsTeamAllyOfLocalPlayer NativeMethodInfoPtr not found");
                    return;
                }

                IntPtr methodInfoPtr = (IntPtr)nativeField.GetValue(null)!;
                if (methodInfoPtr == IntPtr.Zero)
                {
                    MelonLogger.Warning("[LighthouseVision] methodInfoPtr is zero");
                    return;
                }

                IntPtr methodPtr = *(IntPtr*)methodInfoPtr;
                if (methodPtr == IntPtr.Zero)
                {
                    MelonLogger.Warning("[LighthouseVision] methodPtr is zero");
                    return;
                }

                _hookDelegate = new d_IsTeamAllyOfLocalPlayer(Hook_IsTeamAllyOfLocalPlayer);
                IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);

                IntPtr originalPtr = methodPtr;
#pragma warning disable CS0618
                MelonUtils.NativeHookAttach((IntPtr)(&originalPtr), hookPtr);
#pragma warning restore CS0618
                _original = Marshal.GetDelegateForFunctionPointer<d_IsTeamAllyOfLocalPlayer>(originalPtr);

                MelonLogger.Msg("[LighthouseVision] Native hook installed on IsTeamAllyOfLocalPlayer");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[LighthouseVision] Native hook failed: {ex}");
            }
        }

        private static byte Hook_IsTeamAllyOfLocalPlayer(byte param1, IntPtr methodInfo)
        {
            return 1; // Always ally — makes lighthouse code set vision flags for both teams
        }
    }
}
