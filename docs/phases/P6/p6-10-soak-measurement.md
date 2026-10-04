# P6-10 measurement: the soak with membership requests, before any change is committed

Both runs locally, in one container, one after the other, on the same 10,000 seeds, from `5844aff` with the diff at the end applied (an environment switch, RAFT_SOAK_MEMBERSHIP, that runs five nodes, three configured, with P6-08's MembershipWorkload and faults over all five; and a total of states explored). Invariant failures were collected rather than asserted, so a violation would not stop the run; there were none.

## Today's soak (three nodes, no membership requests)

```
10000 executions (seeds 1..10000), faults generated until 12000, run to 22000
invariants 1-11 (membership aside): no violation; elections 27880; 3 clients, retrying on timeout
liveness checked in 9297, no stable suffix in 703; the commit clause checked in 9297
entries committed in fact: 1649739 (164 per execution, fewest 92)
time to a command committed after the stable suffix: max 1007, mean 175 (window 3000)
time to leader after the stable suffix: max 564, mean 38 (window 3000)
linearizability (WGL, budget 32000000 states per key): 9999 checked and accepted, 0 rejected; 1 unverified: KL-1 (seed 7723, key k3). Its linearizability is unknown: the search exhausts 32000000 states without a verdict. Recorded as a known limit against the register row "A structural measure of linearizability-checking cost" (docs/phases/P5/report.md); any other undecided search fails the run (0 here); most states explored 32000124
MEASURE total states 72362123, in decided searches 40361999; membership requests 0; invariant violations 0: 
hardest histories (states explored, seed): 32000124 seed 7723, 21232721 seed 3044, 4910281 seed 7625, 2450196 seed 8260, 2183833 seed 9302
time: checking 568 s of 1323 s
client histories: 1785551 operations (178 per execution), 176174 indeterminate, 3253130 refusals left out; largest per-key sub-history 61
history contents (floor 100):
  history-indeterminate                        9552 / 10000  (95.52%)
  history-concurrent-on-a-key                  9994 / 10000  (99.94%)
  history-read-completed                      10000 / 10000  (100%)
  history-put-completed                       10000 / 10000  (100%)
  history-append-completed                    10000 / 10000  (100%)
  history-delete-completed                    10000 / 10000  (100%)
  history-cas-true                            10000 / 10000  (100%)
  history-cas-false                           10000 / 10000  (100%)
  history-write-retried                        8699 / 10000  (86.99%)
effects (floor 100: 1% of 10000, at least 3):
  split-vote                                   7508 / 10000  (75.08%)
  leader-replaced-after-crash                  5578 / 10000  (55.78%)
  vote-denied-already-voted                    5550 / 10000  (55.5%)
  requestvote-ignored                          7053 / 10000  (70.53%)
  leader-stepped-down                          7071 / 10000  (70.71%)
  candidate-stepped-down                       7759 / 10000  (77.59%)
  elected-after-restart                        4281 / 10000  (42.81%)
  elected-during-one-way-partition             3062 / 10000  (30.62%)
  follower-caught-up-by-backtracking           6981 / 10000  (69.81%)
  conflicting-suffix-truncated                 2124 / 10000  (21.24%)
  leader-crashed-with-uncommitted-entries      1830 / 10000  (18.3%)
  entry-committed-in-a-later-term              1075 / 10000  (10.75%)
  command-retried-and-duplicated               1577 / 10000  (15.77%)
  crash-with-a-log-write-in-flight             3832 / 10000  (38.32%)
  delivered-after-later-send                   8550 / 10000  (85.5%)
  lost-in-transit-to-a-live-receiver           4660 / 10000  (46.6%)
  delivered-twice                              4533 / 10000  (45.33%)
  delivered-later-than-normal-delay            8272 / 10000  (82.72%)
  one-way-reachability                         7418 / 10000  (74.18%)
  node-isolated-for-a-timeout                  1076 / 10000  (10.76%)
  majority-down                                3848 / 10000  (38.48%)
  all-down                                     2981 / 10000  (29.81%)
  unsynced-write-lost                          3730 / 10000  (37.3%)
  writes-completed-out-of-order-at-crash        165 / 10000  (1.65%)
  partial-record-left-on-disk                  1524 / 10000  (15.24%)
  write-slower-than-normal-latency             4118 / 10000  (41.18%)
  step-after-silence-longer-than-a-timeout     3681 / 10000  (36.81%)
  restarted-from-disk                          8045 / 10000  (80.45%)
  clock-rate-diverged                          5764 / 10000  (57.64%)
```

## With membership requests

```
10000 executions (seeds 1..10000), faults generated until 12000, run to 22000
invariants 1-11 (membership aside): no violation; elections 35136; 3 clients, retrying on timeout
liveness checked in 9057, no stable suffix in 943; the commit clause checked in 9057
entries committed in fact: 999463 (99 per execution, fewest 57)
time to a command committed after the stable suffix: max 2146, mean 224 (window 3000)
time to leader after the stable suffix: max 433, mean 29 (window 3000)
linearizability (WGL, budget 32000000 states per key): 9997 checked and accepted, 0 rejected; 0 unverified; any other undecided search fails the run (3 here); most states explored 32000350
MEASURE total states 116001256, in decided searches 20000702; membership requests 32289; invariant violations 0: 
hardest histories (states explored, seed): 32000350 seed 2576, 32000110 seed 2947, 32000094 seed 8741, 6056195 seed 2397, 4496557 seed 5507
time: checking 774 s of 1720 s
client histories: 1140003 operations (114 per execution), 193230 indeterminate, 3865425 refusals left out; largest per-key sub-history 48
history contents (floor 100):
  history-indeterminate                        9847 / 10000  (98.47%)
  history-concurrent-on-a-key                  9970 / 10000  (99.7%)
  history-read-completed                      10000 / 10000  (100%)
  history-put-completed                       10000 / 10000  (100%)
  history-append-completed                    10000 / 10000  (100%)
  history-delete-completed                    10000 / 10000  (100%)
  history-cas-true                             9982 / 10000  (99.82%)
  history-cas-false                           10000 / 10000  (100%)
  history-write-retried                        8915 / 10000  (89.15%)
  undecided: seed 2576 (key k4, 39 operations)
  undecided: seed 2947 (key k2, 26 operations)
  undecided: seed 8741 (key k5, 30 operations)
effects (floor 100: 1% of 10000, at least 3):
  split-vote                                   9996 / 10000  (99.96%)
  leader-replaced-after-crash                  5635 / 10000  (56.35%)
  vote-denied-already-voted                    8522 / 10000  (85.22%)
  requestvote-ignored                         10000 / 10000  (100%)
  leader-stepped-down                          8602 / 10000  (86.02%)
  candidate-stepped-down                       9072 / 10000  (90.72%)
  elected-after-restart                        4634 / 10000  (46.34%)
  elected-during-one-way-partition             7400 / 10000  (74%)
  follower-caught-up-by-backtracking           8259 / 10000  (82.59%)
  conflicting-suffix-truncated                 1717 / 10000  (17.17%)
  leader-crashed-with-uncommitted-entries      1440 / 10000  (14.4%)
  entry-committed-in-a-later-term              2101 / 10000  (21.01%)
  command-retried-and-duplicated                916 / 10000  (9.16%)
  crash-with-a-log-write-in-flight             3299 / 10000  (32.99%)
  delivered-after-later-send                   9516 / 10000  (95.16%)
  lost-in-transit-to-a-live-receiver           8227 / 10000  (82.27%)
  delivered-twice                              8099 / 10000  (80.99%)
  delivered-later-than-normal-delay            9743 / 10000  (97.43%)
  one-way-reachability                         7919 / 10000  (79.19%)
  node-isolated-for-a-timeout                    12 / 10000  (0.12%)
  majority-down                                3424 / 10000  (34.24%)
  all-down                                     2939 / 10000  (29.39%)
  unsynced-write-lost                          4444 / 10000  (44.44%)
  writes-completed-out-of-order-at-crash        156 / 10000  (1.56%)
  partial-record-left-on-disk                  1932 / 10000  (19.32%)
  write-slower-than-normal-latency             5805 / 10000  (58.05%)
  step-after-silence-longer-than-a-timeout     5363 / 10000  (53.63%)
  restarted-from-disk                          8929 / 10000  (89.29%)
  clock-rate-diverged                          7574 / 10000  (75.74%)
  FAIL split-vote: 9996 of 10000 (95% or more), not declared always-on
  FAIL requestvote-ignored: 10000 of 10000 (95% or more), not declared always-on
  FAIL delivered-after-later-send: 9516 of 10000 (95% or more), not declared always-on
  FAIL delivered-later-than-normal-delay: 9743 of 10000 (95% or more), not declared always-on
  FAIL node-isolated-for-a-timeout: 12 of 10000 (0.12%), below the floor of 100 (1% of 10000, at least 3)
  FAIL linearizability undecided in 3 of 10000 runs (budget exhausted): seed 2576 (key k4, 39 operations), seed 2947 (key k2, 26 operations), seed 8741 (key k5, 30 operations)
  FAIL KL-1: seed 7723, key k3 no longer has the recorded history (digest f8bb49f4513f, recorded 39ee32132eed): re-measure it or remove the entry
```

## The diff measured

```diff
diff --git a/tests/Raft.Scale.Tests/Raft.Scale.Tests.csproj b/tests/Raft.Scale.Tests/Raft.Scale.Tests.csproj
index ff3155c..6187d2e 100644
--- a/tests/Raft.Scale.Tests/Raft.Scale.Tests.csproj
+++ b/tests/Raft.Scale.Tests/Raft.Scale.Tests.csproj
@@ -13,6 +13,7 @@
     <PackageReference Include="xunit.v3.mtp-v2" />
   </ItemGroup>
   <ItemGroup>
+    <Compile Include="../Raft.Membership.Tests/MembershipWorkload.cs" Link="Checkers/MembershipWorkload.cs" />
     <Compile Include="../Raft.Core.Tests/ClientHistory.cs" Link="Checkers/ClientHistory.cs" />
     <Compile Include="../Raft.Core.Tests/Cluster.cs" Link="Checkers/Cluster.cs" />
     <Compile Include="../Raft.Core.Tests/CommitLiveness.cs" Link="Checkers/CommitLiveness.cs" />
diff --git a/tests/Raft.Scale.Tests/SoakTests.cs b/tests/Raft.Scale.Tests/SoakTests.cs
index ee5483a..fc9424f 100644
--- a/tests/Raft.Scale.Tests/SoakTests.cs
+++ b/tests/Raft.Scale.Tests/SoakTests.cs
@@ -248,10 +248,13 @@ public sealed class SoakTests
         var checking = new System.Diagnostics.Stopwatch();
         var total = System.Diagnostics.Stopwatch.StartNew();
         var maxPerKey = 0;
+        long totalStates = 0, totalDecided = 0, membershipOps = 0;
+        var violations = new List<string>();
         for (var seed = first; seed < first + count; seed++)
         {
-            var schedule = FaultGenerator.Generate((ulong)seed, new GeneratorConfig { Duration = FaultsUntil });
-            var (sim, h) = Cluster.Run((ulong)seed, Duration, schedule, Clients, new RaftWorkload(int.MaxValue, retry: true, think: Think));
+            var m = Environment.GetEnvironmentVariable("RAFT_SOAK_MEMBERSHIP") is not null;
+            var schedule = FaultGenerator.Generate((ulong)seed, m ? new GeneratorConfig { Duration = FaultsUntil, Nodes = 5 } : new GeneratorConfig { Duration = FaultsUntil });
+            var (sim, h) = m ? Cluster.Run((ulong)seed, Duration, schedule, Clients, new Raft.Membership.Tests.MembershipWorkload(Think), 2) : Cluster.Run((ulong)seed, Duration, schedule, Clients, new RaftWorkload(int.MaxValue, retry: true, think: Think));
             var observations = sim.Observations.ToList();
             var stable = StableFrom(schedule, observations);
             var results = Cluster.Check(h, stable, Duration);
@@ -259,10 +262,11 @@ public sealed class SoakTests
             var commit = CommitLiveness.Clause(h, observations, log, stable, Duration, Cluster.Window, 2 * Cluster.Window);
             foreach (var r in results.Concat(LogAnalysis.Names.Select(log.Result)).Append(commit))
             {
-                Assert.True(r.Holds, $"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(3))}");
+                if (!r.Holds) { violations.Add($"seed {seed}, {r.Invariant}: {string.Join("; ", r.Violations.Take(2))}"); }
             }
 
             var client = ClientHistory.From(sim.ClientLog);
+            membershipOps += client.Membership;
             Assert.True(client.Unexplained.Count == 0, $"seed {seed}: {string.Join("; ", client.Unexplained.Take(3))}");
             Assert.True(client.Accounted == sim.ClientLog.Count && client.History.Count == client.Completed + client.Indeterminate, $"seed {seed}: the adapter lost operations");
             var problems = History.Problems(client.History);
@@ -271,6 +275,8 @@ public sealed class SoakTests
             var lin = WglChecker.Check(client.History, CheckerBudget);
             checking.Stop();
             hardest.Add((lin.StatesExplored, seed));
+            totalStates += lin.StatesExplored;
+            if (lin.Verdict != Verdict.Undecided) { totalDecided += lin.StatesExplored; }
             Assert.True(lin.Verdict != Verdict.NotLinearizable, $"seed {seed}, linearizability at key {lin.Key}: no linearization past\n  {string.Join("\n  ", lin.LongestPrefix.TakeLast(5))}\nof\n  {string.Join("\n  ", lin.SubHistory.Take(40))}");
             var (recorded, limitFailure) = KnownLimits.Judge(limits, seed, client.History, lin);
             if (limitFailure is not null)
@@ -357,6 +363,7 @@ public sealed class SoakTests
             timeToCommit.Count == 0 ? "time to commit: none measured" : $"time to a command committed after the stable suffix: max {timeToCommit.Max()}, mean {timeToCommit.Average().ToString("F0", CultureInfo.InvariantCulture)} (window {Cluster.Window})",
             timeToLeader.Count == 0 ? "time to leader: none measured" : $"time to leader after the stable suffix: max {timeToLeader.Max()}, mean {timeToLeader.Average().ToString("F0", CultureInfo.InvariantCulture)} (window {Cluster.Window})",
             $"linearizability (WGL, budget {CheckerBudget} states per key): {count - undecided.Count - unverified.Count} checked and accepted, 0 rejected; {unverified.Count} unverified{(unverified.Count == 0 ? "" : ": " + string.Join("; ", unverified))}; any other undecided search fails the run ({undecided.Count} here); most states explored {maxStates}",
+            $"MEASURE total states {totalStates}, in decided searches {totalDecided}; membership requests {membershipOps}; invariant violations {violations.Count}: {string.Join(" | ", violations.Take(10))}",
             $"hardest histories (states explored, seed): {string.Join(", ", hardest.OrderByDescending(x => x.States).Take(5).Select(x => $"{x.States} seed {x.Seed}"))}",
             $"time: checking {checking.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s of {total.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s",
             $"client histories: {operations} operations ({operations / count} per execution), {indeterminate} indeterminate, {refused} refusals left out; largest per-key sub-history {maxPerKey}",
```
