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
    internal enum RunStartProfile
    {
        NativeCharacterSingleplayer,
        PreparedPlayerState
    }

    private static bool _forceNeowForNativeRestart;
    public static RunStartProfile StartProfile { get; private set; }
    public static bool UsesNativeSingleplayerStart => StartProfile == RunStartProfile.NativeCharacterSingleplayer;

    public static bool TrySelectRuntimeProfile(string version, out string reason)
    {
        string normalized = version.Trim().TrimStart('v');
        RunStartProfile expected = normalized switch
        {
            "0.107.1" or "0.111.1" => RunStartProfile.NativeCharacterSingleplayer,
            "0.113.1" => RunStartProfile.PreparedPlayerState,
            _ => default
        };
        if (normalized is not ("0.107.1" or "0.111.1" or "0.113.1"))
        {
            reason = $"unsupported release {version}";
            return false;
        }

        try
        {
            return TrySelectKnownRuntimeProfile(version, expected, out reason);
        }
        catch (Exception e)
        {
            reason = $"release {version} compatibility inspection failed: {e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    private static bool TrySelectKnownRuntimeProfile(string version, RunStartProfile expected, out string reason)
    {
        var singleplayerStarts = typeof(NGame).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == "StartNewSingleplayerRun")
            .ToArray();
        bool nativeStart = singleplayerStarts.Any(m =>
            m.GetParameters() is var p && p.Length == 8 && p[0].ParameterType == typeof(CharacterModel));
        bool preparedStart = singleplayerStarts.Any(m =>
            m.GetParameters() is var p && p.Length == 9 && p[0].ParameterType == typeof(Player));
        bool hasCustomRunFlag = AccessTools.Property(typeof(RunState), "IsCustomGame") != null;
        int createRunParameterCount = typeof(RunState)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "CreateForNewRun")
            .GetParameters().Length;
        bool hasInternalStart = AccessTools.Method(typeof(NGame), "StartRun", [typeof(RunState)]) != null;
        bool hasCompatibleLobby = typeof(StartRunLobby)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(c => c.GetParameters() is var p &&
                ((p.Length == 4 && p[0].ParameterType == typeof(GameMode)) ||
                 (p.Length == 5 && p[0].ParameterType == typeof(GameMode) && p[1].ParameterType == typeof(bool))));
        bool hasChaoticRngConstructor = typeof(Rng).GetConstructor([typeof(ulong)]) != null ||
            typeof(Rng).GetConstructor([typeof(uint), typeof(int)]) != null;
        bool hasNamedRngConstructor = typeof(Rng).GetConstructor([typeof(ulong), typeof(string)]) != null ||
            typeof(Rng).GetConstructor([typeof(uint), typeof(string)]) != null;

        bool shapeMatches = expected switch
        {
            RunStartProfile.NativeCharacterSingleplayer =>
                nativeStart && !preparedStart && !hasCustomRunFlag && createRunParameterCount == 6,
            RunStartProfile.PreparedPlayerState =>
                !nativeStart && preparedStart && hasCustomRunFlag && createRunParameterCount == 7,
            _ => false
        };
        if (!shapeMatches || !hasInternalStart || !hasCompatibleLobby ||
            !hasChaoticRngConstructor || !hasNamedRngConstructor)
        {
            reason = $"release {version} API shape did not match {expected} " +
                $"(native={nativeStart}, prepared={preparedStart}, custom={hasCustomRunFlag}, " +
                $"createArgs={createRunParameterCount}, startRun={hasInternalStart}, lobby={hasCompatibleLobby}, " +
                $"chaoticRng={hasChaoticRngConstructor}, namedRng={hasNamedRngConstructor})";
            return false;
        }

        StartProfile = expected;
        reason = $"{expected} (CreateForNewRun={createRunParameterCount} args)";
        return true;
    }

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

    public static void EndRunAsLoss(RunManager manager)
    {
        var method = AccessTools.Method(typeof(RunManager), "OnEnded", [typeof(bool)])
            ?? throw new MissingMethodException("RunManager.OnEnded(bool)");
        method.Invoke(manager, [false]);
    }

    public static bool EnsureFirstActNeow(RunState state)
    {
        var firstAct = state.Acts.FirstOrDefault()
            ?? throw new InvalidOperationException("新局没有第一幕");
        var rooms = AccessTools.Field(typeof(ActModel), "_rooms")?.GetValue(firstAct)
            ?? throw new MissingFieldException(typeof(ActModel).FullName, "_rooms");
        var ancientField = AccessTools.Field(rooms.GetType(), "_ancient")
            ?? throw new MissingFieldException(rooms.GetType().FullName, "_ancient");
        var current = ancientField.GetValue(rooms) as AncientEventModel;
        if (current?.Id.ToString() == "EVENT.NEOW") return false;

        var neow = ModelDb.AllAncients.SingleOrDefault(model => model.Id.ToString() == "EVENT.NEOW")
            ?? throw new InvalidOperationException("模型库中未找到 EVENT.NEOW");
        ancientField.SetValue(rooms, neow);
        return true;
    }

    public static bool IsCustomGame(RunState state) =>
        (bool?)AccessTools.Property(typeof(RunState), "IsCustomGame")?.GetValue(state) ?? state.GameMode.ToString() == "Custom";

    public static UnlockState GenerateOfficialUnlockState() =>
        (UnlockState)(AccessTools.Method(typeof(SaveManager), "GenerateUnlockStateFromProgress")?.Invoke(SaveManager.Instance, null)
            ?? throw new MissingMethodException("SaveManager.GenerateUnlockStateFromProgress"));

    public static bool ManagerFlag(RunManager manager, string propertyName) =>
        (bool?)AccessTools.Property(typeof(RunManager), propertyName)?.GetValue(manager) ?? false;

    public static IEnumerable<ulong> ConnectedHostPeerIds(INetHostGameService host)
    {
        // v0.107.1 and public-beta expose ConnectedPeers on the interface. v0.111.1
        // keeps the same concrete property but removes it from INetHostGameService.
        // Reading the concrete member preserves the beta handshake-complete semantics.
        if (AccessTools.Property(host.GetType(), "ConnectedPeers")?.GetValue(host) is IEnumerable peers)
        {
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                object? id = AccessTools.Field(peer.GetType(), "peerId")?.GetValue(peer)
                    ?? AccessTools.Property(peer.GetType(), "PeerId")?.GetValue(peer);
                if (id != null) yield return Convert.ToUInt64(id);
            }
            yield break;
        }

        // Defensive fallback for another official build that hides the concrete list.
        if (host.NetHost != null)
            foreach (ulong id in host.NetHost.ConnectedPeerIds) yield return id;
    }

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

    public static async Task<RunState> StartNativeSingleplayer(NGame game, RestartPlan plan)
    {
        var method = typeof(NGame).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "StartNewSingleplayerRun" &&
                m.GetParameters() is var p && p.Length == 8 && p[0].ParameterType == typeof(CharacterModel));
        var character = plan.NewState.Players.Single().Character;
        var acts = plan.NewState.Acts.Select(a => ModelDb.GetById<ActModel>(a.Id)).ToList();
        var modifiers = plan.NewState.Modifiers.Select(m => ModifierModel.FromSerializable(m.ToSerializable())).ToList();
        Task task;
        _forceNeowForNativeRestart = true;
        try
        {
            task = (Task)method.Invoke(game,
                [character, plan.ShouldSave, acts, modifiers, plan.NewState.Rng.StringSeed,
                    plan.NewState.GameMode, plan.NewState.AscensionLevel, plan.DailyTime])!;
        }
        finally { _forceNeowForNativeRestart = false; }
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
            if (_forceNeowForNativeRestart) __result.ExtraFields.StartedWithNeow = true;
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.GenerateRooms))]
    private static class NativeGeneratedRoomsNeowPatch
    {
        static void Postfix(RunManager __instance)
        {
            if (!_forceNeowForNativeRestart) return;
            var state = __instance.DebugOnlyGetState()
                ?? throw new InvalidOperationException("正式版生成房间后没有 RunState");
            if (EnsureFirstActNeow(state))
                MegaCrit.Sts2.Core.Logging.Log.Info(
                    "[SeedRestart] Added Neow after the official room generator for a profile without NEOW_EPOCH.");
            state.ExtraFields.StartedWithNeow = true;
        }
    }

    public static Rng CreateChaoticRng()
    {
        var chaotic = AccessTools.Method(typeof(Rng), "CreateChaoticSeed");
        if (chaotic != null)
        {
            var value = chaotic.Invoke(null, null)!;
            var constructor = typeof(Rng).GetConstructor([value.GetType()]);
            if (constructor != null) return (Rng)constructor.Invoke([value]);
        }
        var ulongConstructor = typeof(Rng).GetConstructor([typeof(ulong)]);
        if (ulongConstructor != null)
        {
            ulong seed = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(sizeof(ulong)));
            return (Rng)ulongConstructor.Invoke([seed]);
        }
        var legacyConstructor = typeof(Rng).GetConstructor([typeof(uint), typeof(int)])
            ?? throw new MissingMethodException("Compatible chaotic Rng constructor");
        uint legacySeed = BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(sizeof(uint)));
        return (Rng)legacyConstructor.Invoke([legacySeed, 0]);
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
