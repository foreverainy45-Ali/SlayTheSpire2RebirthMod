#if RUNTIME_PROBE
using System.Text.Json;
using Godot;
using HarmonyLib;
using JmcModLib.Prefabs;
using JmcModLib.UI.PauseMenu;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace SeedRestart;

// Compiled only into isolated integration-test builds. Never included in distribution.
internal static class RuntimeProbe
{
    public static void Start(NGame game, RestartController controller)
    {
        var directory = System.Environment.GetEnvironmentVariable("SEEDRESTART_PROBE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        if (!OS.GetUserDataDir().Contains("SlayTheSpire2_SeedRestart_Test", StringComparison.Ordinal))
            throw new InvalidOperationException("Probe refuses to touch a real game profile: " + OS.GetUserDataDir());
        Directory.CreateDirectory(directory);
        if (System.Environment.GetEnvironmentVariable("SEEDRESTART_PROBE_AUTO_SL") == "1")
        {
            var quickSl = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "QuickSL");
            var popup = quickSl?.GetType("QuickSL.UI.QuickSlPopupService");
            if (popup != null)
            {
                ModEntry.Harmony.Patch(AccessTools.Method(popup, "ShowConfirmationAsync"), prefix: new HarmonyMethod(typeof(RuntimeProbe), nameof(AutoApproveTestSl)));
                _quickSlAutoApprovalInstalled = true;
            }
            else
                Log.Info("[SeedRestart.Probe] QuickSL has not loaded yet; confirmation auto-approval will be installed lazily.");
        }
        Log.Info("[SeedRestart.Probe] isolated user data: " + OS.GetUserDataDir());
        _ = Run(game, controller, directory);
    }

    private static async Task Run(NGame game, RestartController controller, string directory)
    {
        try
        {
            await game.GameStartupComplete;
            NModalContainer.Instance?.Clear();
            SaveManager.Instance.SetFtuesEnabled(false);
            var mode = System.Environment.GetEnvironmentVariable("SEEDRESTART_PROBE_MODE") ?? "single";
            int expectedPlayers = int.TryParse(System.Environment.GetEnvironmentVariable("SEEDRESTART_PROBE_PLAYERS"), out int expected) ? expected : 2;
            if (mode == "single")
            {
                await game.StartNewSingleplayerRun(Player.CreateForNewRun<Ironclad>(UnlockState.all, 1), true,
                    ActModel.GetDefaultList(), [], "SEEDTEST1", GameMode.Standard, false, 0);
            }
            long serial = 0;
            int frame = 0;
            while (game.IsInsideTree())
            {
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
                if (++frame % 8 != 0) continue;
                EnsureQuickSlAutoApprovalIfAvailable();
                if (mode != "single" && RunManager.Instance.DebugOnlyGetState() == null)
                {
                    foreach (var screen in game.FindChildren("*", "", true, false).OfType<NCharacterSelectScreen>())
                    {
                        var lobby = screen.Lobby;
                        if (lobby == null || lobby.Players.Count < expectedPlayers) continue;
                        for (int i = 0; i < lobby.Players.Count; i++)
                        {
                            var entry = lobby.Players[i];
                            entry.unlockState = UnlockState.all.ToSerializable();
                            lobby.Players[i] = entry;
                        }
                        var character = mode == "host" ? (CharacterModel)ModelDb.Character<Ironclad>() : ModelDb.Character<Silent>();
                        if (lobby.LocalPlayer.character.Id != character.Id) lobby.SetLocalCharacter(character, false);
                        if (!lobby.LocalPlayer.isReady) lobby.SetReady(true);
                    }
                }
                var manager = RunManager.Instance;
                var state = manager.DebugOnlyGetState();
                var localPlayer = state == null ? null : LocalContext.GetMe(state);
                var request = AccessTools.Field(typeof(RestartController), "_request").GetValue(controller);
                var commandPath = Path.Combine(directory, "command.json");
                if (File.Exists(commandPath))
                {
                    JsonDocument command;
                    try
                    {
                        using var stream = new FileStream(commandPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        command = JsonDocument.Parse(stream);
                    }
                    catch (IOException) { continue; }
                    catch (JsonException) { continue; }
                    using var commandScope = command;
                    var id = command.RootElement.GetProperty("id").GetInt64();
                    if (id > serial)
                    {
                        serial = id;
                        string operation = command.RootElement.GetProperty("operation").GetString()!;
                        Log.Info("[SeedRestart.Probe] command " + serial + " " + operation);
                        switch (operation)
                        {
                            case "request":
                                AccessTools.Method(typeof(RestartController), "Bind").Invoke(controller, null);
                                AccessTools.Method(typeof(RestartController), "SendRequest").Invoke(controller, null);
                                break;
                            case "request_ui":
                                _ = RestartMenu.RestartFromPauseMenu();
                                break;
                            case "confirm_ui":
                                PressPopupButton(true);
                                break;
                            case "cancel_ui":
                                PressPopupButton(false);
                                break;
                            case "agree":
                                if (!PressPopupButton(true)) AccessTools.Method(typeof(RestartController), "Vote").Invoke(controller, [true]);
                                break;
                            case "reject":
                                if (!PressPopupButton(false)) AccessTools.Method(typeof(RestartController), "Vote").Invoke(controller, [false]);
                                break;
                            case "cancel": AccessTools.Method(typeof(RestartController), "Abort").Invoke(controller, ["probe cancellation"]); break;
                            case "sl":
                                var service = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "QuickSL").GetType("QuickSL.Core.QuickSlService")!;
                                EnsureQuickSlAutoApproval();
                                _ = (Task)AccessTools.Method(service, "RequestQuickSlAsync").Invoke(null, null)!;
                                break;
                            case "combat":
                                _ = manager.EnterMapCoord(state!.Map.GetPointsInRow(1).First().coord);
                                break;
                            case "exit": game.GetTree().Quit(); return;
                        }
                    }
                }
                var status = new
                {
                    time = DateTimeOffset.UtcNow, serial,
                    seed = state?.Rng.StringSeed, room = state?.CurrentRoom?.GetType().Name,
                    players = state?.Players.Select(p => new { id = p.NetId, character = p.Character.Id.ToString() }),
                    netType = manager.NetService?.Type.ToString(), connected = manager.NetService?.IsConnected,
                    netIdentity = manager.NetService == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(manager.NetService),
                    request, committed = controller.IsCommitted,
                    preparing = AccessTools.Field(typeof(RestartController), "_preparing").GetValue(controller),
                    failed = AccessTools.Field(typeof(RestartController), "_failedAfterCommit").GetValue(controller),
                    userDir = OS.GetUserDataDir(), isChangingRooms = manager.IsChangingRooms,
                    slBusy = new QuickSlCompatibilityView(controller).Busy,
                    pauseMenuButtonRegistered = PauseMenuRegistry.GetEntries(typeof(RestartMenu).Assembly)
                        .Any(e => e.Key == "seed_restart.pause_menu.rebirth"),
                    popupOpen = NModalContainer.Instance?.OpenModal != null,
                    popupAvailable = JmcConfirmationPopup.IsAvailable,
                    losses = localPlayer == null ? (long?)null : SaveManager.Instance.Progress
                        .GetStatsForCharacter(localPlayer.Character.Id)?.TotalLosses,
                    runs = SaveManager.Instance.Progress.NumberOfRuns,
                    historyFiles = Directory.GetFiles(OS.GetUserDataDir(), "*.run", SearchOption.AllDirectories).Length
                };
                File.WriteAllText(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(directory, "error.txt"), e.ToString()); Log.Error("[SeedRestart.Probe] " + e); }
    }

    private static bool AutoApproveTestSl(ref Task<bool> __result)
    {
        __result = Task.FromResult(true);
        return false;
    }

    private static bool _quickSlAutoApprovalInstalled;
    private static void EnsureQuickSlAutoApprovalIfAvailable()
    {
        if (_quickSlAutoApprovalInstalled || System.Environment.GetEnvironmentVariable("SEEDRESTART_PROBE_AUTO_SL") != "1") return;
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "QuickSL")) return;
        EnsureQuickSlAutoApproval();
    }

    private static void EnsureQuickSlAutoApproval()
    {
        if (_quickSlAutoApprovalInstalled || System.Environment.GetEnvironmentVariable("SEEDRESTART_PROBE_AUTO_SL") != "1") return;
        var popup = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "QuickSL").GetType("QuickSL.UI.QuickSlPopupService")!;
        ModEntry.Harmony.Patch(AccessTools.Method(popup, "ShowConfirmationAsync"), prefix: new HarmonyMethod(typeof(RuntimeProbe), nameof(AutoApproveTestSl)));
        _quickSlAutoApprovalInstalled = true;
    }

    private static bool PressPopupButton(bool confirm)
    {
        var modal = NModalContainer.Instance?.OpenModal;
        if (modal == null)
        {
            Log.Warn("[SeedRestart.Probe] popup command ignored because no native popup is open");
            return false;
        }
        var popup = ((Node)modal).GetNode<NVerticalPopup>("VerticalPopup");
        var button = confirm ? popup.YesButton : popup.NoButton;
        button.EmitSignal(MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl.SignalName.Released, button);
        return true;
    }

    private sealed class QuickSlCompatibilityView(RestartController controller)
    {
        public bool Busy => ((QuickSlCompatibility)AccessTools.Field(typeof(RestartController), "_sl").GetValue(controller)!).IsBusy;
    }
}
#endif
