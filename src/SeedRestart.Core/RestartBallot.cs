namespace SeedRestart.Core;

public enum RestartPhase { Idle, Voting, Preparing, Committed, Failed }

/// <summary>Host-owned consent and prepare barrier. No game or transport side effects.</summary>
public sealed class RestartBallot
{
    private readonly HashSet<ulong> _members;
    private readonly HashSet<ulong> _votes = [];
    private readonly HashSet<ulong> _ready = [];
    private readonly HashSet<ulong> _loaded = [];
    public string RequestId { get; }
    public string RunToken { get; }
    public ulong Initiator { get; }
    public IReadOnlySet<ulong> Members => _members;
    public IReadOnlySet<ulong> Votes => _votes;
    public IReadOnlySet<ulong> Loaded => _loaded;
    public RestartPhase Phase { get; private set; } = RestartPhase.Voting;
    public DateTimeOffset Deadline { get; private set; }
    public string? Failure { get; private set; }
    public string? NewSeed { get; private set; }

    public RestartBallot(string requestId, string runToken, IEnumerable<ulong> members,
        ulong initiator, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runToken);
        _members = members.ToHashSet();
        if (_members.Count == 0 || !_members.Contains(initiator))
            throw new ArgumentException("Initiator must belong to a nonempty membership.");
        RequestId = requestId;
        RunToken = runToken;
        Initiator = initiator;
        Deadline = now.AddSeconds(30);
        _votes.Add(initiator);
    }

    public bool Vote(string request, ulong sender, bool approve, DateTimeOffset now)
    {
        if (!Accept(request, sender, RestartPhase.Voting, now)) return false;
        if (!approve) { Cancel("玩家拒绝了重开请求"); return true; }
        return _votes.Add(sender);
    }

    public bool BeginPrepare(string seed, string oldSeed, DateTimeOffset now)
    {
        if (Phase != RestartPhase.Voting || now >= Deadline || !_votes.SetEquals(_members)) return false;
        if (string.IsNullOrWhiteSpace(seed) || seed == oldSeed) return false;
        NewSeed = seed;
        Phase = RestartPhase.Preparing;
        Deadline = now.AddSeconds(15);
        return true;
    }

    public bool Ready(string request, ulong sender, DateTimeOffset now) =>
        Accept(request, sender, RestartPhase.Preparing, now) && _ready.Add(sender);

    public bool Commit(DateTimeOffset now)
    {
        if (Phase != RestartPhase.Preparing || now >= Deadline || !_ready.SetEquals(_members)) return false;
        Phase = RestartPhase.Committed;
        Deadline = now.AddSeconds(60);
        return true;
    }

    public bool MarkLoaded(string request, ulong sender) =>
        request == RequestId && Phase == RestartPhase.Committed && _members.Contains(sender) && _loaded.Add(sender);

    public bool AllLoaded => Phase == RestartPhase.Committed && _loaded.SetEquals(_members);

    public bool CheckMembers(IEnumerable<ulong> members)
    {
        if (_members.SetEquals(members)) return true;
        Cancel("玩家成员发生变化，已取消重开");
        return false;
    }

    public bool Tick(DateTimeOffset now)
    {
        if (Phase is RestartPhase.Failed or RestartPhase.Idle || now < Deadline || AllLoaded) return false;
        Cancel(Phase == RestartPhase.Committed ? "等待新局加载超时" : "重开请求超时");
        return true;
    }

    public void Cancel(string reason)
    {
        Phase = RestartPhase.Failed;
        Failure = reason;
    }

    private bool Accept(string request, ulong sender, RestartPhase phase, DateTimeOffset now) =>
        request == RequestId && Phase == phase && now < Deadline && _members.Contains(sender);
}
