using MelonLoader;
using LNVR_Tweaks;

[assembly: MelonInfo(typeof(LNVRTweaksMod), LNVR_Tweaks.BuildInfo.Name, LNVR_Tweaks.BuildInfo.Version, LNVR_Tweaks.BuildInfo.Author)]
[assembly: MelonGame("Iconik", "Little Nightmares VR")]

namespace LNVR_Tweaks
{
    public static class BuildInfo
    {
        public const string Name = "LNVR Tweaks";
        public const string Version = "1.0.0";
        public const string Author = "elliotttate";
    }
}
