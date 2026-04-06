using System;
using System.Linq;
using System.Reflection;

namespace SiroccoMod.Mods.Skins
{
    public class GameReflectionBridge
    {
        public Type? GameAuthorityType { get; }
        public object? GameAuthorityInstance { get; }
        public bool IsValid { get; }

        public GameReflectionBridge()
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (asm == null) return;

            GameAuthorityType = asm.GetType("Il2CppWartide.GameAuthority");
            if (GameAuthorityType == null) return;

            var instanceProp = GameAuthorityType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            GameAuthorityInstance = instanceProp?.GetValue(null);

            IsValid = GameAuthorityInstance != null;
        }
    }
}
