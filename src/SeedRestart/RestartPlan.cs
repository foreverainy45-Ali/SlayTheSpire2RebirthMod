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
using MegaCrit.Sts2.Core.Unlocks;

namespace SeedRestart;

internal sealed record LobbyPlayerSnapshot(ulong Id, int SlotId, CharacterModel Character,
    SerializableUnlockState UnlockState, int Ascension);

internal sealed record RestartPlan(RunState OldState, RunState NewState, List<LobbyPlayerSnapshot> LobbyPlayers,
    bool ShouldSave, DateTimeOffset? DailyTime)
{
    public static string Token(RunState? state)
    {
        if (state == null) return "";
        var config = string.Join("|", state.Rng.StringSeed, state.GameMode, GameCompatibility.IsCustomGame(state), state.AscensionLevel,
            string.Join(";", state.Players.Select(p => $"{p.NetId}:{p.Character.Id}")),
            JsonSerializer.Serialize(state.Modifiers.Select(m => m.ToSerializable()).ToList()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
    }

    public static string NewSeed(string oldSeed)
    {
        string seed;
        do { seed = SeedHelper.GetRandomSeed(GameCompatibility.CreateChaoticRng()); } while (seed == oldSeed);
        return seed;
    }

    public static string[] PickActs(RunState state, string seed, bool multiplayer) =>
        ActModel.GetRandomList(GameCompatibility.CreateNamedRng(GameCompatibility.DeterministicHash(seed), "act_selection"), state.UnlockState, multiplayer)
            .Select(a => a.Id.ToString()).ToArray();

    public static string[] ActsForRestart(RunState state, string seed, bool multiplayer) =>
        state.GameMode == GameMode.Daily
            ? state.Acts.Select(a => a.Id.ToString()).ToArray()
            : PickActs(state, seed, multiplayer);

    public static RestartPlan Create(RunState old, string seed, string[] acts, bool multiplayer)
    {
        bool daily = old.GameMode == GameMode.Daily;
        if (string.IsNullOrWhiteSpace(seed) || (daily ? seed != old.Rng.StringSeed : seed == old.Rng.StringSeed))
            throw new InvalidOperationException(daily ? "每日挑战必须保留当日固定种子" : "该模式必须使用新种子");
        if (daily && !acts.SequenceEqual(old.Acts.Select(a => a.Id.ToString())))
            throw new InvalidOperationException("每日挑战必须保留固定幕列表");
        var dailyTime = RunManager.Instance.DailyTime;
        if (daily && !dailyTime.HasValue)
            throw new InvalidOperationException("每日挑战时间信息缺失，已阻止重开");
        var officialUnlocks = GameCompatibility.IsOfficial107 ? GameCompatibility.GenerateOfficialUnlockState() : null;
        var players = old.Players.Select(p => Player.CreateForNewRun(ModelDb.GetById<CharacterModel>(p.Character.Id),
            officialUnlocks ?? p.UnlockState, p.NetId)).ToList();
        var actModels = acts.Select(id => ModelDb.GetById<ActModel>(ModelId.Deserialize(id)).ToMutable()).ToList();
        var modifiers = old.Modifiers.Select(m => ModifierModel.FromSerializable(m.ToSerializable())).ToList();
        var state = GameCompatibility.CreateRunState(players, actModels, modifiers, old.GameMode,
            GameCompatibility.IsCustomGame(old), old.AscensionLevel, seed, multiplayer);
        var lobby = old.Players.Select((p, i) => new LobbyPlayerSnapshot(p.NetId, i,
            ModelDb.GetById<CharacterModel>(p.Character.Id), p.UnlockState.ToSerializable(), old.AscensionLevel)).ToList();
        return new(old, state, lobby, RunManager.Instance.ShouldSave, dailyTime);
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
