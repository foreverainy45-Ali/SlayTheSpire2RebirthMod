using System.Reflection;
using Godot;
using HarmonyLib;
using JmcModLib.Prefabs;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SeedRestart.Core;
using SeedRestart.Network;

namespace SeedRestart;

internal sealed class RestartController
{
    private readonly NGame _game;
    private readonly RestartUi _ui;
    private readonly QuoteCatalog _quotes;
    private QuickSlCompatibility _sl;
    private INetGameService? _net;
    private RestartBallot? _ballot;
    private Envelope? _request;
    private Envelope? _pendingRequest;
    private RestartPlan? _plan;
    private readonly HashSet<ulong> _setup = [];
    private readonly HashSet<string> _closed = [];
    private bool _preparing, _readySent, _setupStarted, _startSent, _starting, _committed, _dropNative;
    private bool _actionWasPaused, _combatWasPaused, _ownsPause;
    private bool _failedAfterCommit;
    private string? _votePromptRequest;
    private DateTimeOffset _deadline;
    private string? _recovery;
    private string? _currentQuote;
    private RunState? _observedState;
    private ulong _host;
    private bool _ticking;
    internal bool IsCommitted => _committed;
    internal bool DropOldNative => _dropNative;
    internal bool NativeLoadingOpen => _ui.NativeLoadingOpen;
    internal bool NativeVoteOpen => _ui.NativeVoteOpen;
    internal string? LoadingStatus => _ui.LoadingStatus;
    internal string? LoadingQuote => _ui.LoadingQuote;
    internal string? RenderedQuote => _ui.RenderedQuote;
    internal bool QuoteBottomCentered => _ui.QuoteBottomCentered;
    private bool IsHost => _net?.Type is NetGameType.Host or NetGameType.Singleplayer;
    private bool IsMulti => _net?.Type is NetGameType.Host or NetGameType.Client;
    private bool IsDaily => (_observedState ?? RunManager.Instance.DebugOnlyGetState())?.GameMode == GameMode.Daily;
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public RestartController(NGame game)
    {
        _game = game;
        _ui = new(game);
        string assemblyDir = Path.GetDirectoryName(typeof(RestartController).Assembly.Location) ?? AppContext.BaseDirectory;
        string quotePath = Path.Combine(assemblyDir, "SeedRestart.quotes.cfg");
        _quotes = QuoteCatalog.Load(quotePath);
        if (_quotes.Error == null) Log.Info($"[SeedRestart] Loaded {_quotes.Count} waiting quotes from {quotePath}.");
        else Log.Warn("[SeedRestart] " + _quotes.Error + "; waiting screen will omit the quote.");
        _sl = new();
        game.GetTree().ProcessFrame += Tick;
    }

    private void Bind()
    {
        if (!_sl.Detected && AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "QuickSL"))
        {
            _sl = new QuickSlCompatibility();
            Log.Info("[SeedRestart] Late-loaded QuickSL compatibility attached; supported=" + _sl.Supported + ".");
        }
        var manager = RunManager.Instance;
        var service = manager.NetService;
        if (service == null || ReferenceEquals(service, _net) || _committed) return;
        if (_net != null) _net.UnregisterMessageHandler<RestartMessage>(Receive);
        if (_request != null) CancelLocal("当前对局已改变");
        _net = service;
        _host = service is NetClientGameService client ? client.HostNetId : service.NetId;
        if (IsMulti) service.RegisterMessageHandler<RestartMessage>(Receive);
        _observedState = manager.DebugOnlyGetState();
    }

    public void Request() => _ = RequestFromPauseMenuAsync();

    public async Task RequestFromPauseMenuAsync()
    {
        try
        {
            Bind();
            Log.Info($"[SeedRestart] Restart request entered; net={_net?.Type.ToString() ?? "none"}, popupAvailable={JmcConfirmationPopup.IsAvailable}.");
            if (_committed) { NotifyBusySl(); return; }
            if (_pendingRequest != null) { Notice("已发出请求，正在等待房主响应"); return; }
            if (_request != null) { Render(); return; }
            var reason = Unavailable();
            if (reason != null)
            {
                Log.Warn("[SeedRestart] Restart request unavailable: " + reason);
                await JmcConfirmationPopup.ShowMessageAsync(
                    "当前无法换种重开", reason, "知道了", true, typeof(RestartController).Assembly);
                return;
            }
            bool multiplayer = _net!.Type is NetGameType.Host or NetGameType.Client;
            bool daily = IsDaily;
            bool confirmed = await JmcConfirmationPopup.ShowConfirmationAsync(
                daily ? "确认重开每日挑战" : "确认换种重开",
                daily
                    ? (multiplayer
                        ? "当前每日挑战会在全员同意后结束，并为每位玩家记为失败。\n\n保留当日固定种子、角色、难度和选项，在当前房间回到第一幕涅奥？"
                        : "当前每日挑战会立即结束并记为失败。\n\n保留当日固定种子、角色、难度和选项，回到第一幕涅奥？")
                    : (multiplayer
                        ? "当前对局会在全员同意后结束，并为每位玩家记为失败。\n\n保留各自角色与难度，在当前房间使用新随机种子重新开始？"
                        : "当前对局会立即结束并记为失败。\n\n保留当前角色与难度，使用新的随机种子重新开始？"),
                multiplayer ? "发起全员确认" : "记为失败并重开", "取消", true, typeof(RestartController).Assembly);
            Log.Info("[SeedRestart] Initial confirmation completed; approved=" + confirmed + ".");
            if (confirmed) SendRequest();
        }
        catch (Exception e) { Error(e); }
    }

    private string? Unavailable()
    {
        var manager = RunManager.Instance;
        var state = manager.DebugOnlyGetState();
        if (state == null || GameCompatibility.ManagerFlag(manager, "HasRunEnded") || manager.IsCleaningUp || _net == null) return "当前不在可重开的对局中";
        if (!_sl.Supported) return "当前 QuickSL 版本尚未适配；为保留其功能，换种重开已停用";
        if (_sl.IsBusy) return "快速 SL 正在审批或加载，请完成后再请求换种";
        if (!GameCompatibility.IsSupportedMode(state.GameMode))
            return "当前仅支持普通模式、每日挑战和 Blitz";
        if (state.Modifiers.Any(m => m.GetType().Assembly != typeof(RunState).Assembly)) return "当前自定义修正项尚未验证兼容";
        if (state.Players.Count is < 1 or > 4 || _net.Type is not (NetGameType.Singleplayer or NetGameType.Host or NetGameType.Client)) return "当前联机模式不受支持";
        if (GameCompatibility.ManagerFlag(manager, "IsChangingRooms") || _game.Transition.InTransition || _net.IsGameLoading) return "正在切换场景，请稍后重试";
        if (IsMulti && !state.Players.Select(p => p.NetId).ToHashSet().SetEquals(CurrentMembers())) return "请等待所有原对局玩家连接完成";
        return null;
    }

    private ulong[] CurrentMembers()
    {
        if (!IsMulti) return [_net!.NetId];
        if (_net is INetHostGameService host) return host.ConnectedPeers.Select(p => p.peerId).Append(_net.NetId).Order().ToArray();
        return RunManager.Instance.DebugOnlyGetState()?.Players.Select(p => p.NetId).Order().ToArray() ?? [_net!.NetId];
    }

    private void SendRequest()
    {
        _ui.Close();
        var reason = Unavailable();
        if (reason != null) { Notice(reason); return; }
        var message = new Envelope { Kind = MessageKind.Request, RequestId = Guid.NewGuid().ToString("N"),
            RunToken = RestartPlan.Token(RunManager.Instance.DebugOnlyGetState()), Initiator = _net!.NetId };
        if (IsHost) BeginVote(message, _net.NetId);
        else { _pendingRequest = message; _deadline = Now.AddSeconds(10); Send(message); }
    }

    private void BeginVote(Envelope request, ulong sender)
    {
        if (!CurrentMembers().Contains(sender) || _closed.Contains(request.RequestId)) return;
        string? reason = _request != null ? "已有另一轮重开投票" : Unavailable();
        if (request.RunToken != RestartPlan.Token(RunManager.Instance.DebugOnlyGetState())) reason = "对局已改变，请重新请求";
        if (reason != null)
        {
            if (sender != _net!.NetId) _net.SendMessage(RestartMessage.From(request with { Kind = MessageKind.RequestRejected, Detail = reason }), sender);
            else Notice(reason);
            return;
        }
        _ballot = new(request.RequestId, request.RunToken, CurrentMembers(), sender, Now);
        _request = request with { Kind = MessageKind.Ballot, Members = _ballot.Members.Order().ToArray(), Votes = [sender], Initiator = sender };
        _observedState = RunManager.Instance.DebugOnlyGetState();
        _deadline = Now.AddSeconds(30);
        Broadcast(_request);
        Render();
    }

    private void Send(Envelope message) => _net!.SendMessage(RestartMessage.From(message));
    private void Broadcast(Envelope message) { if (IsMulti) Send(message); }
    private Envelope Message(MessageKind kind) => _request! with { Kind = kind };

    private void Receive(RestartMessage message, ulong sender)
    {
        // Dispatch after NetMessageBus has finished iterating its shared callback list.
        // Handlers must not synchronously recurse into the bus or destroy the current scene.
        try
        {
            var envelope = message.Parse();
            Callable.From(() => Handle(envelope, sender)).CallDeferred();
        }
        catch (Exception e) { Log.Warn("[SeedRestart] Invalid message: " + e.Message); }
    }

    private void Handle(Envelope e, ulong sender)
    {
        try
        {
            if (_net == null || !_net.IsConnected || _closed.Contains(e.RequestId)) return;
            if (!IsHost && sender == _host && e.Kind == MessageKind.RequestRejected && e.RequestId == _pendingRequest?.RequestId)
            {
                _pendingRequest = null;
                Notice(e.Detail);
                return;
            }
            if (e.Kind == MessageKind.Request) { if (IsHost) BeginVote(e, sender); return; }
            if (IsHost)
            {
                if (_request == null || e.RequestId != _request.RequestId || e.RunToken != _request.RunToken || !_request.Members.Contains(sender)) return;
                switch (e.Kind)
                {
                    case MessageKind.Vote:
                        if (_ballot!.Vote(e.RequestId, sender, e.Approve, Now))
                        {
                            if (_ballot.Phase == RestartPhase.Failed) { Abort(_ballot.Failure!); return; }
                            _request = _request with { Votes = _ballot.Votes.Order().ToArray() };
                            Broadcast(_request);
                        }
                        break;
                    case MessageKind.Ready: if (_ballot!.Phase == RestartPhase.Preparing) _ballot.Ready(e.RequestId, sender, Now); break;
                    case MessageKind.SetupReady: if (_committed && !_startSent) _setup.Add(sender); break;
                    case MessageKind.Loaded: if (_committed) _ballot!.MarkLoaded(e.RequestId, sender); break;
                    case MessageKind.Cancel:
                    case MessageKind.Fail: Abort(string.IsNullOrEmpty(e.Detail) ? "玩家取消或未能准备新局" : e.Detail); break;
                }
            }
            else
            {
                if (sender != _host) return;
                if (e.Kind == MessageKind.Ballot)
                {
                    if (_committed || (_request != null && _request.RequestId != e.RequestId)) return;
                    if (e.RunToken != RestartPlan.Token(RunManager.Instance.DebugOnlyGetState()) ||
                        !e.Members.ToHashSet().SetEquals(CurrentMembers()) || !e.Members.Contains(_net.NetId)) return;
                    if (_request == null)
                    {
                        if (_sl.IsBusy) { Send(e with { Kind = MessageKind.Cancel, Detail = "快速 SL 正在执行" }); return; }
                        _observedState = RunManager.Instance.DebugOnlyGetState();
                        _deadline = Now.AddSeconds(30);
                    }
                    _request = e;
                    if (_pendingRequest?.RequestId == e.RequestId) _pendingRequest = null;
                }
                else
                {
                    if (_request == null || e.RequestId != _request.RequestId || e.RunToken != _request.RunToken) return;
                    switch (e.Kind)
                    {
                        case MessageKind.Prepare:
                            if (_preparing || _committed || !_request.Votes.Contains(_net.NetId)) return;
                            _request = e;
                            StartPrepare();
                            break;
                        case MessageKind.Commit:
                            if (_readySent && !_committed) CommitLocal();
                            break;
                        case MessageKind.Start:
                            if (_committed && _setupStarted && !_starting && _setup.Contains(_net.NetId)) StartLocal();
                            break;
                        case MessageKind.Complete:
                            if (_committed && _starting && RunManager.Instance.DebugOnlyGetState()?.Rng.StringSeed == _request.Seed) CompleteLocal();
                            break;
                        case MessageKind.Cancel:
                        case MessageKind.Fail: CancelLocal(e.Detail); break;
                    }
                }
            }
            Render();
        }
        catch (Exception exception) { Error(exception); }
    }

    private void Vote(bool yes)
    {
        if (_request == null || _preparing || _committed) return;
        if (IsHost)
        {
            _ballot!.Vote(_request.RequestId, _net!.NetId, yes, Now);
            if (!yes) { Abort("玩家拒绝了重开请求"); return; }
            _request = _request with { Votes = _ballot.Votes.Order().ToArray() };
            Broadcast(_request);
        }
        else Send(Message(MessageKind.Vote) with { Approve = yes });
        Render();
    }

    private async Task PromptForVoteAsync(string requestId)
    {
        if (_request == null || _request.RequestId != requestId || _votePromptRequest != null ||
            _request.Votes.Contains(_net!.NetId) || !JmcConfirmationPopup.IsAvailable) return;
        _votePromptRequest = requestId;
        _ui.Close();
        bool approved;
        try
        {
            bool daily = IsDaily;
            approved = await JmcConfirmationPopup.ShowConfirmationAsync(
                daily ? "是否同意重开每日挑战？" : "是否同意换种重开？",
                daily
                    ? "有玩家请求结束当前每日挑战。全员同意后，本局会记为失败，并保留当日固定种子、选项和各自当前角色，在原房间回到第一幕涅奥。"
                    : "有玩家请求结束当前对局。全员同意后，本局会记为失败，并使用各自当前角色在原房间以新种子重新开始。",
                "同意并记为失败", "拒绝", true, typeof(RestartController).Assembly);
        }
        catch (Exception e)
        {
            Log.Error("[SeedRestart] 显示多人确认框失败：" + e);
            return;
        }
        finally
        {
            _votePromptRequest = null;
        }
        if (_request?.RequestId == requestId && !_preparing && !_committed &&
            !_request.Votes.Contains(_net!.NetId)) Vote(approved);
    }

    private void Tick()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            // NRun normally pumps networking. Keep it alive during the scene-free setup gap.
            if (_committed && (NRun.Instance == null || !NRun.Instance.IsInsideTree())) _net?.Update();
            if (!_committed) Bind();
            if (_pendingRequest != null && Now >= _deadline)
            {
                _closed.Add(_pendingRequest.RequestId);
                Send(_pendingRequest with { Kind = MessageKind.Cancel, Detail = "等待房主响应超时" });
                _pendingRequest = null;
                Notice("等待房主响应超时，请重试");
            }
            if (_request == null || _failedAfterCommit) return;
            if (!_net!.IsConnected) { CancelLocal("联机连接已断开"); return; }
            // Before commit every peer must still match the original quorum. During the
            // committed scene-free gap a beta client temporarily has no RunState, so it
            // cannot enumerate the other players and CurrentMembers() would contain only
            // itself. The host can still inspect ConnectedPeers and remains responsible
            // for detecting departures; clients rely on their live host connection and
            // the setup/load barriers after commit.
            if ((!_committed || IsHost) && !CurrentMembers().ToHashSet().SetEquals(_request.Members))
            {
                Abort("玩家成员发生变化");
                return;
            }
            if (!_committed && !ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), _observedState)) { Abort("当前对局已改变"); return; }
            if (!_committed && _sl.IsBusy) { Abort("快速 SL 已开始，取消换种请求"); return; }
            if (Now >= _deadline) { Abort(_committed ? "新局加载超时，请保留恢复文件和日志" : "换种请求超时"); return; }
            if (IsHost && !_preparing && !_committed && _ballot!.Votes.SetEquals(_ballot.Members))
            {
                bool daily = _observedState!.GameMode == GameMode.Daily;
                string seed = daily ? _observedState.Rng.StringSeed : RestartPlan.NewSeed(_observedState.Rng.StringSeed);
                if (_ballot.BeginPrepare(seed, _observedState.Rng.StringSeed, Now, allowSameSeed: daily))
                {
                    _request = _request with { Seed = seed, Acts = RestartPlan.ActsForRestart(_observedState, seed, IsMulti) };
                    Broadcast(Message(MessageKind.Prepare));
                    StartPrepare();
                }
            }
            if (_preparing && !_readySent && !_committed) TryReady();
            if (IsHost && _preparing && !_committed && _ballot!.Commit(Now))
            {
                Broadcast(Message(MessageKind.Commit));
                CommitLocal();
            }
            if (IsHost && _committed && !_startSent && _setup.SetEquals(_request.Members))
            {
                _startSent = true;
                Broadcast(Message(MessageKind.Start));
                StartLocal();
            }
            if (IsHost && _committed && _ballot!.AllLoaded)
            {
                Broadcast(Message(MessageKind.Complete));
                CompleteLocal();
            }
            Render();
        }
        catch (Exception e) { Error(e); }
        finally { _ticking = false; }
    }

    private void StartPrepare()
    {
        if (_sl.IsBusy) { Abort("快速 SL 正在执行"); return; }
        _preparing = true;
        _currentQuote ??= _quotes.Next();
        _deadline = Now.AddSeconds(15);
        _plan = RestartPlan.Create(_observedState!, _request!.Seed, _request.Acts, IsMulti);
    }

    private void TryReady()
    {
        var manager = RunManager.Instance;
        var action = manager.ActionExecutor.CurrentlyRunningAction;
        if (GameCompatibility.ManagerFlag(manager, "IsChangingRooms") || _game.Transition.InTransition ||
            (action != null && action.State is not (GameActionState.GatheringPlayerChoice or GameActionState.Finished or GameActionState.Canceled))) return;
        if (!_ownsPause)
        {
            _actionWasPaused = manager.ActionExecutor.IsPaused;
            _combatWasPaused = CombatManager.Instance.IsPaused;
            manager.ActionExecutor.Pause();
            CombatManager.Instance.Pause();
            _ownsPause = true;
        }
        if (RestartPlan.Token(manager.DebugOnlyGetState()) != _request!.RunToken) { Abort("开局配置已改变"); return; }
        if (SaveManager.Instance.CurrentRunSaveTask is { IsCompleted: false }) return;
        _recovery = RestartPlan.WriteRecovery(IsMulti, _net!.NetId, _request.RequestId);
        _readySent = true;
        _dropNative = true;
        if (IsHost) _ballot!.Ready(_request.RequestId, _net.NetId, Now); else Send(Message(MessageKind.Ready));
    }

    private void CommitLocal()
    {
        _committed = true;
        _deadline = Now.AddSeconds(60);
        _setupStarted = true;
        _ = SetupLocalAsync();
    }

    private async Task SetupLocalAsync()
    {
        try
        {
            await _game.ToSignal(_game.GetTree(), SceneTree.SignalName.ProcessFrame);
            var manager = RunManager.Instance;
            string? lobby = _net!.GetRawLobbyIdentifier();
            // A reroll is a real loss, not an unrecorded reset or an abandoned-history entry.
            // OnEnded(false) synchronously updates progress, creates run history and deletes
            // the old autosave before the fresh seed is installed.
            GameCompatibility.EndRunAsLoss(manager);
            // Native CleanUp does not dispose these services in v0.113.1. They would
            // otherwise leave old-run listeners on a connection that is being reused.
            GameCompatibility.DisposeSynchronizerIfPresent(manager, "MerchantSynchronizer");
            GameCompatibility.DisposeSynchronizerIfPresent(manager, "EventSynchronizer");
            GameCompatibility.DisposeSynchronizerIfPresent(manager, "OneOffSynchronizer");
            GameCompatibility.DisposeSynchronizerIfPresent(manager, "CombatStateSynchronizer");
            GameCompatibility.DisposeSynchronizerIfPresent(manager, "InputSynchronizer");
            // All synchronous cleanup executes against a forwarding facade. No network
            // service/global disconnect patches are installed, so QuickSL keeps its own path.
            RestartPlan.SetNetService(PreservedConnection.Create(_net));
            try { manager.CleanUp(false); }
            finally { RestartPlan.SetNetService(_net); }
            _game.RootSceneContainer.SetCurrentScene(new Control());
            await _game.ToSignal(_game.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_failedAfterCommit) return;
            if (!_net.IsConnected || lobby != _net.GetRawLobbyIdentifier()) throw new InvalidOperationException("联机会话未保持");
            if (IsMulti)
            {
                var plan = _plan ?? throw new InvalidOperationException("重开计划已丢失");
                var hidden = GameCompatibility.CreateHiddenLobby(plan, _net);
                // Removes temporary lobby listeners without disconnecting or disposing input sync.
                hidden.CleanUp(false);
                manager.SetUpNewMultiplayer(plan.NewState, hidden, plan.ShouldSave, plan.DailyTime);
            }
            else if (!GameCompatibility.IsOfficial107)
                manager.SetUpNewSingleplayer(_plan!.NewState, _plan.ShouldSave, _plan.DailyTime);
            // A reroll explicitly returns to Neow even for a profile still in the initial tutorial.
            // The native room generator legitimately leaves Act 1 without an Ancient when
            // NEOW_EPOCH is still locked. Install only the missing Neow room; never reroll
            // the already-generated room pools or change the profile's long-term unlocks.
            if (!GameCompatibility.IsOfficial107 || IsMulti)
            {
                if (GameCompatibility.EnsureFirstActNeow(_plan!.NewState))
                    Log.Info("[SeedRestart] Added Neow to Act 1 for a profile without NEOW_EPOCH; existing room RNG preserved.");
                _plan.NewState.ExtraFields.StartedWithNeow = true;
            }
            _setup.Add(_net.NetId);
            if (!IsHost) Send(Message(MessageKind.SetupReady));
        }
        catch (Exception e) { Error(e); }
    }

    private void StartLocal()
    {
        _starting = true;
        _dropNative = false;
        _net!.SetBufferMessages(true);
        _ = StartLocalAsync();
    }

    private async Task StartLocalAsync()
    {
        try
        {
            var plan = _plan ?? throw new InvalidOperationException("重开计划已丢失");
            if (GameCompatibility.IsOfficial107 && !IsMulti)
                _plan = plan with { NewState = await GameCompatibility.StartOfficialSingleplayer(_game, plan) };
            else
            {
                var start = AccessTools.Method(typeof(NGame), "StartRun", [typeof(RunState)])
                    ?? throw new MissingMethodException("NGame.StartRun");
                await (Task)start.Invoke(_game, [plan.NewState])!;
            }
            if (_failedAfterCommit) return;
            RunManager.Instance.ActionExecutor.Pause();
            CombatManager.Instance.Pause();
            await SaveManager.Instance.SaveRun(null, true);
            if (IsHost && _plan.ShouldSave)
            {
                var saved = IsMulti ? SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(_net!.NetId) : SaveManager.Instance.LoadRunSave();
                if (!saved.Success || saved.SaveData?.SerializableRng.Seed != _request!.Seed)
                    throw new IOException("新局自动存档未确认写入；保留恢复副本");
            }
            if (IsHost) _ballot!.MarkLoaded(_request!.RequestId, _net!.NetId); else Send(Message(MessageKind.Loaded));
        }
        catch (Exception e) { Error(e); }
    }

    private void CompleteLocal()
    {
        _ownsPause = false;
        RunManager.Instance.ActionExecutor.Unpause();
        CombatManager.Instance.Unpause();
        CleanupRecovery();
        Log.Info(IsDaily
            ? $"[SeedRestart] Restarted daily seed {_request!.Seed}; reused original connection."
            : $"[SeedRestart] Completed new seed {_request!.Seed}; reused original connection.");
        Reset();
        _ui.Close();
    }

    private void Abort(string reason)
    {
        if (_request == null) return;
        if (IsHost) Broadcast(Message(_committed ? MessageKind.Fail : MessageKind.Cancel) with { Detail = reason });
        else Send(Message(_committed ? MessageKind.Fail : MessageKind.Cancel) with { Detail = reason });
        CancelLocal(reason);
    }

    private void CancelLocal(string reason)
    {
        Log.Warn("[SeedRestart] " + reason);
        if (_votePromptRequest != null && NModalContainer.Instance?.OpenModal != null)
            NModalContainer.Instance.Clear();
        if (_committed)
        {
            _failedAfterCommit = true;
            _dropNative = false;
            RunManager.Instance.ActionExecutor?.Pause();
            CombatManager.Instance.Pause();
            _ui.Show("failure", "新局同步失败：" + reason + "\n已停止继续操作。请保留游戏日志及恢复文件：\n" + _recovery);
            return;
        }
        if (_ownsPause && ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), _observedState))
        {
            if (!_actionWasPaused) RunManager.Instance.ActionExecutor.Unpause();
            if (!_combatWasPaused) CombatManager.Instance.Unpause();
        }
        // Do not discard incoming old-run messages before commit; see the message guard.
        CleanupRecovery();
        Reset();
        _ui.Close();
        // A notification above QuickSL's own modal would prevent the user voting on SL.
        if (!reason.Contains("快速 SL", StringComparison.Ordinal) && !_sl.IsBusy) Notice(reason);
    }

    private void Reset()
    {
        if (_request != null) { if (_closed.Count > 128) _closed.Clear(); _closed.Add(_request.RequestId); }
        _request = null; _pendingRequest = null; _ballot = null; _plan = null; _setup.Clear();
        _preparing = _readySent = _setupStarted = _startSent = _starting = _committed = _dropNative = _ownsPause = _failedAfterCommit = false;
        _votePromptRequest = null;
        _recovery = null;
        _currentQuote = null;
        _observedState = RunManager.Instance.DebugOnlyGetState();
    }

    private void CleanupRecovery()
    {
        try { if (_recovery != null && File.Exists(_recovery)) File.Delete(_recovery); }
        catch (IOException e) { Log.Warn("[SeedRestart] 新局已保存，恢复副本暂未清理：" + e.Message); }
        catch (UnauthorizedAccessException e) { Log.Warn("[SeedRestart] 恢复副本暂未清理：" + e.Message); }
    }

    public void OnQuickSlRequested()
    {
        if (_pendingRequest != null)
        {
            Send(_pendingRequest with { Kind = MessageKind.Cancel, Detail = "快速 SL 已开始，取消换种请求" });
            _closed.Add(_pendingRequest.RequestId);
            _pendingRequest = null;
        }
        if (_request != null && !_committed) Abort("快速 SL 已开始，取消换种请求");
    }
    public void NotifyBusySl() => Log.Info("[SeedRestart] 新局正在切换，请加载完成后再使用快速 SL。");
    private void Notice(string text) => _ui.Show("notice:" + text, text, ("知道了", _ui.Close));
    private void Error(Exception e)
    {
        Log.Error("[SeedRestart] " + e);
        if (_request != null) Abort("重开失败：" + (e.InnerException?.Message ?? e.Message));
        else Notice("重开失败：" + e.Message);
    }

    private void Render()
    {
        if (_request == null || _failedAfterCommit) return;
        if (_committed || _preparing)
        {
            string status = !_committed
                ? (IsMulti ? "正在准备新局，等待安全切换…" : "正在准备新局，请稍候…")
                : _starting
                    ? (IsMulti ? "正在进入新局，等待其他玩家加载…" : "正在加载新局资源…")
                    : IsDaily ? "正在重建每日挑战…" : "正在创建新种子对局…";
            _ui.ShowLoading(status, _currentQuote);
            return;
        }
        if (!_request.Votes.Contains(_net!.NetId) && _request.Initiator != _net.NetId)
        {
            _ = PromptForVoteAsync(_request.RequestId);
            return;
        }
        string text = $"{(IsDaily ? "请求以当日固定种子重开" : "请求以新随机种子重开")}\n同意 {_request.Votes.Length}/{_request.Members.Length}　剩余 {Math.Max(0, (int)(_deadline - Now).TotalSeconds)} 秒\n" +
            string.Join("　", _request.Members.Select((id, i) => $"玩家{i + 1}：{(_request.Votes.Contains(id) ? "同意" : "等待")}"));
        if (_request.Initiator == _net!.NetId) _ui.ShowVoteWaiting("vote-own", text, "取消请求", () => Abort("发起人取消了重开"));
        else if (_request.Votes.Contains(_net.NetId)) _ui.ShowVoteWaiting("vote-wait", text, "拒绝并取消", () => Vote(false));
        else _ui.Show("vote", text, ("同意", () => Vote(true)), ("拒绝", () => Vote(false)));
    }

    [HarmonyPatch(typeof(NetMessageBus), nameof(NetMessageBus.SendMessageToAllHandlers))]
    private static class MessageGuard
    {
        static bool Prefix(INetMessage message)
        {
            var c = ModEntry.Controller;
            // Never suppress another Mod's messages. The preparation phase remains reversible;
            // dropping is restricted to committed teardown, before the all-setup barrier.
            return c is not { _committed: true, _dropNative: true } || !message.ShouldBuffer ||
                message.GetType().Assembly != typeof(RunManager).Assembly;
        }
    }
}
