using System.Reflection;
using HarmonyLib;

namespace SeedRestart;

/// <summary>Optional, read-only state inspection and a scoped entry guard. No dependency on QuickSL.</summary>
internal sealed class QuickSlCompatibility
{
    private static readonly BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Type? _service;
    private readonly object? _controller;
    public bool Supported { get; }
    public bool Detected => _service != null;

    public QuickSlCompatibility()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "QuickSL");
        if (assembly is null) { Supported = true; return; }
        _service = assembly.GetType("QuickSL.Core.QuickSlService");
        var coordinator = assembly.GetType("QuickSL.Core.MultiplayerQuickSlCoordinator");
        _controller = coordinator?.GetField("Controller", Flags)?.GetValue(null);
        var lockField = _service?.GetField("RunLock", Flags);
        var state = ReadState();
        Supported = assembly.GetName().Version == new Version(1, 9, 0, 0) &&
            lockField?.GetValue(null) is SemaphoreSlim && state != null;
        if (!Supported) return;
        foreach (var name in new[] { "RequestQuickSlAsync", "RunQuickSlAsync", "RunSinglePlayerQuickSlAsync" })
        {
            var method = _service!.GetMethod(name, Flags);
            if (method?.ReturnType != typeof(Task)) { Supported = false; return; }
        }
        foreach (var name in new[] { "RequestQuickSlAsync", "RunQuickSlAsync", "RunSinglePlayerQuickSlAsync" })
            ModEntry.Harmony.Patch(_service!.GetMethod(name, Flags),
                prefix: new HarmonyMethod(typeof(QuickSlCompatibility), nameof(BeforeQuickSl)));
    }

    private object? ReadState() => _controller?.GetType().GetProperty("State", Flags)?.GetValue(_controller);

    public bool IsBusy
    {
        get
        {
            if (_service is null) return false;
            if (!Supported) return true;
            try
            {
                if (_service.GetField("RunLock", Flags)?.GetValue(null) is SemaphoreSlim gate && gate.CurrentCount == 0) return true;
                var state = ReadState() ?? throw new InvalidOperationException("QuickSL state unavailable");
                return new[] { "HostInitiateState", "HostVoteState", "HostLoadBarrierState", "HostSetupBarrierState",
                    "ClientLoadBarrierState", "ClientRunBeginBarrierState", "ClientInitiateState", "ActiveClientRequestId" }
                    .Any(name => state.GetType().GetProperty(name, Flags)?.GetValue(state) != null);
            }
            catch { return true; }
        }
    }

    private static bool BeforeQuickSl(ref Task __result)
    {
        var controller = ModEntry.Controller;
        if (controller is null) return true;
        if (controller.IsCommitted)
        {
            // Only the actual new-run transition owns this guard. Ordinary SL is untouched.
            controller.NotifyBusySl();
            __result = Task.CompletedTask;
            return false;
        }
        controller.OnQuickSlRequested();
        return true;
    }
}
