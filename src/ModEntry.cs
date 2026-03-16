using IamFlaw.Core;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;

namespace IamFlaw;

[ModInitializer(nameof(OnModLoaded))]
public static class ModEntry
{
    public const string ModId = "IamFlaw";

    public static void OnModLoaded()
    {
        Log.Info($"[{ModEntry.ModId}] ModLoader invoked by StS2.");
        ModBootstrap.Initialize();
    }
}