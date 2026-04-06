using MelonLoader;

[assembly: MelonInfo(typeof(SiroccoMod.Mods.Skins.SkinsPlugin), "Sirocco Skins", "1.0.0", "Shadow")]
[assembly: MelonGame("LunchboxEntertainment", "Sirocco")]

namespace SiroccoMod.Mods.Skins
{
    public class SkinsPlugin : MelonMod
    {
        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("Sirocco Skins initializing...");
            SkinSystem.Install(HarmonyInstance);
            MelonLogger.Msg("Sirocco Skins initialized!");
        }

        public override void OnUpdate()
        {
            SkinSystem.OnUpdate();
        }
    }
}
