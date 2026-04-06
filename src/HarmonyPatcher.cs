using System.Reflection;

namespace SiroccoMod
{
    public static class HarmonyPatcher
    {
        public const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static;
    }
}
