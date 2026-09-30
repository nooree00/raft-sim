using System.IO;
using System.Linq;

namespace Raft.Gates;

/// <summary>
/// Every sabotage patch and control applies to the tree, checked with the harness's own tool and
/// flags (`git apply --whitespace=nowarn`), with every build (P4 acceptance). Until this gate the
/// patches were checked by hand with `patch`, which applies with fuzz: four stale patches passed that
/// check, were committed at the commits that broke them, and would have failed in the harness on
/// exactly the commits introducing the mechanisms they test. A check made with a different tool from
/// the one that enforces it is not the same check. Run on every commit by the per-commit matrix, it
/// names a stale patch at the commit that made it stale.
/// Vacuity risk: a manifest that loads nothing checks nothing; guarded by requiring entries.
/// </summary>
internal static class PatchCheck
{
    public static Findings Run(Repo repo, string[] args)
    {
        var f = new Findings();
        var specs = SabotageSpec.LoadAll(repo, f);
        f.Require(specs.Count > 0, "no sabotage entries to check");
        var checkedCount = 0;
        foreach (var spec in specs)
        {
            foreach (var path in new[] { spec.PatchPath, spec.ControlPath }.OfType<string>().Where(p => new FileInfo(p).Length > 0))
            {
                var apply = repo.Git("apply", "--check", "--whitespace=nowarn", path);
                f.Require(apply.Ok, $"{spec.Id}: {Path.GetFileName(path)} does not apply with git apply: {apply.StdErr.Trim().Split('\n')[0]}");
                checkedCount++;
            }
        }

        f.Note($"{checkedCount} patches and controls apply with git apply, over {specs.Count} entries");
        return f;
    }
}
