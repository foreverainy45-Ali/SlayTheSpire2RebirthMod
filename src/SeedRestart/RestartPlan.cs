using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SeedRestart;

internal sealed record RestartPlan(RunState OldState, RunState NewState, List<StartRunLobbyPlayer> LobbyPlayers, bool ShouldSave)
{
    public static string Token(RunState? state)
    {
        if (state == null) return "";
        var config = string.Join("|", state.Rng.StringSeed, state.GameMode, state.IsCustomGame, state.AscensionLevel,
            string.Join(";", state.Players.Select(p => $"{p.NetId}:{p.Character.Id}")),
            JsonSerializer.Serialize(state.Modifiers.Select(m => m.ToSerializable()).ToList()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
    }

    public static string NewSeed(string oldSeed)
    {
        string seed;
        do { seed = SeedHelper.GetRandomSeed(new Rng(Rng.CreateChaoticSeed())); } while (seed == oldSeed);
        return seed;
    }

    public static string[] PickActs(RunState state, string seed, bool multiplayer) =>
        ActModel.GetRandomList(new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection"), state.UnlockState, multiplayer)
            .Select(a => a.Id.ToString()).ToArray();

    public static RestartPlan Create(RunState old, string seed, string[] acts)
    {
        if (seed == old.Rng.StringSeed || string.IsNullOrWhiteSpace(seed)) throw new InvalidOperationException("必须使用新种子");
        var players = old.Players.Select(p => Player.CreateForNewRun(ModelDb.GetById<CharacterModel>(p.Character.Id), p.UnlockState, p.NetId)).ToList();
        var actModels = acts.Select(id => ModelDb.GetById<ActModel>(ModelId.Deserialize(id)).ToMutable()).ToList();
        var modifiers = old.Modifiers.Select(m => ModifierModel.FromSerializable(m.ToSerializable())).ToList();
        var state = RunState.CreateForNewRun(players, actModels, modifiers, old.GameMode, old.IsCustomGame, old.AscensionLevel, seed);
        var lobby = old.Players.Select((p, i) => new StartRunLobbyPlayer
        {
            id = p.NetId, slotId = i, character = ModelDb.GetById<CharacterModel>(p.Character.Id),
            unlockState = p.UnlockState.ToSerializable(), isModded = true, isReady = true,
            maxStandardAscension = old.AscensionLevel, maxBlitzAscension = old.AscensionLevel
        }).ToList();
        return new(old, state, lobby, RunManager.Instance.ShouldSave);
    }

    public static string WriteRecovery(bool multiplayer, ulong localId, string requestId)
    {
        // The host owns multiplayer autosaves; clients normally have no local MP save.
        // Their frozen snapshot is supplementary recovery evidence, never used as SL data.
        SerializableRun snapshot;
        if (multiplayer && RunManager.Instance.NetService.Type == MegaCrit.Sts2.Core.Multiplayer.Game.NetGameType.Client)
            snapshot = RunManager.Instance.ToSave(null);
        else
        {
            var saved = multiplayer ? SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(localId) : SaveManager.Instance.LoadRunSave();
            var state = RunManager.Instance.DebugOnlyGetState()!;
            if (saved.Success && saved.SaveData?.SerializableRng.Seed == state.Rng.StringSeed) snapshot = saved.SaveData;
            else if (state.TotalFloor == 0 || state.CurrentRoom is MegaCrit.Sts2.Core.Rooms.MapRoom)
                snapshot = RunManager.Instance.ToSave(null);
            else throw new InvalidOperationException("无法备份当前自动存档，已阻止重开");
        }
        var directory = Godot.ProjectSettings.GlobalizePath("user://mod_configs/SeedRestart/recovery");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{localId}-{requestId}.json");
        File.WriteAllText(path, SaveManager.ToJson(snapshot));
        return path;
    }

    public static void SetNetService(MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService service) =>
        AccessTools.PropertySetter(typeof(RunManager), nameof(RunManager.NetService)).Invoke(RunManager.Instance, [service]);
}
