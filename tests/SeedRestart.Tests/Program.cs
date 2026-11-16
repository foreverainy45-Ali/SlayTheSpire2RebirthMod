using SeedRestart.Core;

var now = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
RestartBallot New(params ulong[] members) => new("request", "run", members, members[0], now);

var b = New(1, 2, 3, 4);
Check(!b.BeginPrepare("new", "old", now), "initiator alone cannot restart a four-player run");
Check(!b.Vote("other", 2, true, now), "stale request ignored");
Check(!b.Vote("request", 99, true, now), "outsider cannot vote");
Check(b.Vote("request", 2, true, now), "member consent accepted");
Check(!b.Vote("request", 2, true, now), "duplicate consent not counted");
b.Vote("request", 3, true, now);
Check(!b.BeginPrepare("new", "old", now), "three of four is insufficient");
b.Vote("request", 4, true, now);
Check(!b.BeginPrepare("old", "old", now), "same seed rejected");
Check(b.BeginPrepare("new", "old", now), "unanimous approval begins preparation");
Check(!b.Commit(now), "cannot commit before ready barrier");
foreach (ulong id in new ulong[] {1, 2, 3}) b.Ready("request", id, now);
Check(!b.Commit(now), "one unready peer prevents commit");
b.Ready("request", 4, now);
Check(b.Commit(now), "all ready allows exactly one commit");
Check(!b.Commit(now), "duplicate commit ignored");
foreach (ulong id in new ulong[] {1, 2, 3}) b.MarkLoaded("request", id);
Check(!b.AllLoaded, "all clients must acknowledge loading");
b.MarkLoaded("request", 4);
Check(b.AllLoaded && !b.Tick(now.AddMinutes(2)), "completed barrier does not time out");

b = New(1, 2);
b.Vote("request", 2, false, now);
Check(b.Phase == RestartPhase.Failed && !b.BeginPrepare("new", "old", now), "rejection cancels request");
b = New(1, 2);
Check(b.Tick(now.AddSeconds(30)) && b.Phase == RestartPhase.Failed, "vote timeout cancels request");
b = New(1, 2);
Check(!b.CheckMembers([1]) && b.Phase == RestartPhase.Failed, "disconnect cannot shrink voting quorum");
b = New(1, 2);
b.Vote("request", 2, true, now);
b.BeginPrepare("new", "old", now);
Check(b.Tick(now.AddSeconds(15)), "preparation timeout aborts before commit");
b = New(1, 2);
Check(!b.Vote("request", 2, true, now.AddSeconds(30)), "late consent rejected without relying on timer tick");
b.Cancel("QuickSL started");
Check(!b.BeginPrepare("new", "old", now), "SL cancellation prevents subsequent restart commit");
Console.WriteLine($"{passed} checks passed.");
