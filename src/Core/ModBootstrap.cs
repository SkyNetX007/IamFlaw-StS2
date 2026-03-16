using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace IamFlaw.Core;

public static class ModBootstrap
{
    private static readonly string LogTag = $"{ModEntry.ModId}.ModBootstrap";

    private static bool _initialized;
    private static Harmony? _harmony;

    public static void Initialize()
    {
        if (_initialized)
        {
            Log.Info($"[{LogTag}] Mod already initialized, skip.");
            return;
        }

        _initialized = true;

        Log.Info($"[{LogTag}] Mod initializing...");

        _harmony = new Harmony(ModEntry.ModId);
        _harmony.PatchAll();

        Log.Info($"[{LogTag}] Harmony patches applied with id '{LogTag}'.");
    }
}
