using System.Reflection;
using Godot;
using HarmonyLib;
using JmcModLib.Core;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;

namespace SeedRestart;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    internal static readonly Harmony Harmony = new("Foreverain.SeedRestart");
    internal static RestartController? Controller;
#if RUNTIME_PROBE
    private static bool _probeStarted;
#endif
    public static void Initialize()
    {
        var version = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown";
        if (version.TrimStart('v') != "0.113.1")
        {
            Log.Warn($"[SeedRestart] Unsupported game {version}; no patches installed.");
            return;
        }
        ModRegistry.Register("SeedRestart", "快速换种重开 / Seed Restart", "0.2.1", Assembly.GetExecutingAssembly()).Done();
        Harmony.PatchAll(Assembly.GetExecutingAssembly());
        EnsureController();
        Log.Info("[SeedRestart] 0.2.1 loaded for public-beta v0.113.1. QuickSL files/configuration untouched.");
    }

    internal static RestartController? EnsureController(NGame? game = null)
    {
        if (Controller != null) return Controller;
        game ??= NGame.Instance;
        if (game == null || !GodotObject.IsInstanceValid(game) || !game.IsInsideTree())
        {
            Log.Debug("[SeedRestart] NGame is not ready yet; controller attachment deferred.");
            return null;
        }
        Controller = new RestartController(game);
        Log.Info("[SeedRestart] Controller attached to active NGame.");
#if RUNTIME_PROBE
        if (!_probeStarted)
        {
            _probeStarted = true;
            RuntimeProbe.Start(game, Controller);
        }
#endif
        return Controller;
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame._Ready))]
    private static class GameReadyPatch
    {
        static void Postfix(NGame __instance)
        {
            EnsureController(__instance);
        }
    }

}
