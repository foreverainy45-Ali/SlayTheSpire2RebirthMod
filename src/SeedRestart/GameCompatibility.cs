using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace SeedRestart;

internal static class GameCompatibility
{
    private static bool _forceNeowForOfficialRestart;
    public static StartRunLobby CreateHiddenLobby(RestartPlan plan, INetGameService service)
    {
        var listener = DispatchProxy.Create<IStartRunLobbyListener, NoOpLobbyListener>();
        var constructors = typeof(StartRunLobby).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object? lobby = null;
        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length == 5 && parameters[0].ParameterType == typeof(GameMode) && parameters[1].ParameterType == typeof(bool))
                lobby = constructor.Invoke([plan.NewState.GameMode, IsCustomGame(plan.NewState), service, listener, 4]);
            else if (parameters.Length == 4 && parameters[0].ParameterType == typeof(GameMode))
                lobby = constructor.Invoke([plan.NewState.GameMode, service, listener, 4]);
            if (lobby != null) break;
        }
        var hidden = lobby as StartRunLobby ?? throw new MissingMethodException("Compatible StartRunLobby constructor");
        var playersProperty = AccessTools.Property(typeof(StartRunLobby), "Players")
            ?? throw new MissingMemberException("StartRunLobby.Players");
        var players = playersProperty.GetValue(hidden) as IList
            ?? throw new InvalidOperationException("StartRunLobby.Players is not a mutable list");
        var playerType = playersProperty.PropertyType.GetGenericArguments().Single();
        foreach (var snapshot in plan.LobbyPlayers)
        {
            var player = Activator.CreateInstance(playerType) ?? throw new InvalidOperationException("Lobby player creation failed");
            SetField(playerType, player, "id", snapshot.Id, true);
            SetField(playerType, player, "slotId", snapshot.SlotId, true);
            SetField(playerType, player, "character", snapshot.Character, true);
            SetField(playerType, player, "unlockState", snapshot.UnlockState, true);
            SetField(playerType, player, "isReady", true, true);
            SetField(playerType, player, "isModded", true, false);
            SetField(playerType, player, "maxMultiplayerAscensionUnlocked", snapshot.Ascension, false);
            SetField(playerType, player, "maxStandardAscension", snapshot.Ascension, false);
            SetField(playerType, player, "maxBlitzAscension", snapshot.Ascension, false);
            players.Add(player);
        }
        return hidden;
    }

    public static void DisposeSynchronizerIfPresent(RunManager manager, string propertyName)
    {
        var value = AccessTools.Property(typeof(RunManager), propertyName)?.GetValue(manager);
        if (value == null) return;
        AccessTools.Method(value.GetType(), nameof(IDisposable.Dispose))?.Invoke(value, null);
    }

    public static bool IsCustomGame(RunState state) =>
        (bool?)AccessTools.Property(typeof(RunState), "IsCustomGame")?.GetValue(state) ?? state.GameMode.ToString() == "Custom";

    public static bool IsOfficial107 => AccessTools.Property(typeof(RunState), "IsCustomGame") == null;

    public static UnlockState GenerateOfficialUnlockState() =>
        (UnlockState)(AccessTools.Method(typeof(SaveManager), "GenerateUnlockStateFromProgress")?.Invoke(SaveManager.Instance, null)
            ?? throw new MissingMethodException("SaveManager.GenerateUnlockStateFromProgress"));

    public static bool ManagerFlag(RunManager manager, string propertyName) =>
        (bool?)AccessTools.Property(typeof(RunManager), propertyName)?.GetValue(manager) ?? false;

    public static bool IsSupportedMode(GameMode mode) => mode.ToString() is "Standard" or "Daily" or "Blitz";

    public static RunState CreateRunState(IReadOnlyList<Player> players, IReadOnlyList<ActModel> acts,
        IReadOnlyList<ModifierModel> modifiers, GameMode mode, bool isCustom, int ascension, string seed, bool multiplayer)
    {
        var method = typeof(RunState).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "CreateForNewRun");
        var args = method.GetParameters().Length == 7
            ? new object?[] { players, acts, modifiers, mode, isCustom, ascension, seed }
            : new object?[] { players, acts, modifiers, mode, ascension, seed };
        return (RunState)method.Invoke(null, args)!;
    }

    public static async Task<RunState> StartOfficialSingleplayer(NGame game, RestartPlan plan)
    {
        var method = typeof(NGame).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "StartNewSingleplayerRun" && m.GetParameters().Length == 8);
        var character = plan.NewState.Players.Single().Character;
        var acts = plan.NewState.Acts.Select(a => ModelDb.GetById<ActModel>(a.Id)).ToList();
        var modifiers = plan.NewState.Modifiers.Select(m => ModifierModel.FromSerializable(m.ToSerializable())).ToList();
        Task task;
        _forceNeowForOfficialRestart = true;
        try
        {
            task = (Task)method.Invoke(game,
                [character, plan.ShouldSave, acts, modifiers, plan.NewState.Rng.StringSeed,
                    plan.NewState.GameMode, plan.NewState.AscensionLevel, plan.DailyTime])!;
        }
        finally { _forceNeowForOfficialRestart = false; }
        await task;
        return RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("正式版原生新局入口未返回 RunState");
    }

    [HarmonyPatch]
    private static class OfficialNeowPatch
    {
        static MethodBase TargetMethod() => typeof(RunState).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "CreateForNewRun");

        static void Postfix(RunState __result)
        {
            if (_forceNeowForOfficialRestart) __result.ExtraFields.StartedWithNeow = true;
        }
    }

    public static Rng CreateChaoticRng()
    {
        var chaotic = AccessTools.Method(typeof(Rng), "CreateChaoticSeed");
        if (chaotic != null)
        {
            var value = chaotic.Invoke(null, null)!;
            return (Rng)(Activator.CreateInstance(typeof(Rng), value)
                ?? throw new InvalidOperationException("Rng creation failed"));
        }
        uint seed = BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(sizeof(uint)));
        return (Rng)(Activator.CreateInstance(typeof(Rng), seed, 0)
            ?? throw new InvalidOperationException("Rng creation failed"));
    }

    public static ulong DeterministicHash(string value)
    {
        var hash = AccessTools.Method(typeof(StringHelper), "GetDeterministicHashCode")!.Invoke(null, [value]);
        return hash switch
        {
            int signed => unchecked((uint)signed),
            uint unsigned => unsigned,
            long signed => unchecked((ulong)signed),
            ulong unsigned => unsigned,
            _ => throw new InvalidOperationException("Unsupported deterministic hash type: " + hash?.GetType().FullName)
        };
    }

    public static Rng CreateNamedRng(ulong hash, string name)
    {
        var uintConstructor = typeof(Rng).GetConstructor([typeof(uint), typeof(string)]);
        if (uintConstructor != null) return (Rng)uintConstructor.Invoke([unchecked((uint)hash), name]);
        var ulongConstructor = typeof(Rng).GetConstructor([typeof(ulong), typeof(string)])
            ?? throw new MissingMethodException("Compatible Rng constructor");
        return (Rng)ulongConstructor.Invoke([hash, name]);
    }

    private static void SetField(Type type, object instance, string name, object value, bool required)
    {
        var field = AccessTools.Field(type, name);
        if (field != null) field.SetValue(instance, value);
        else if (required) throw new MissingFieldException(type.FullName, name);
    }

    private class NoOpLobbyListener : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(void) ? null : targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
