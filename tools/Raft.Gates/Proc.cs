using System.Diagnostics;
using System.Threading.Tasks;

namespace Raft.Gates;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public override string ToString() => $"exit {ExitCode}\n{StdOut}\n{StdErr}";
}

internal static class Proc
{
    public static ProcessResult Run(string file, string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Task.WaitAll(stdout, stderr);
        return new ProcessResult(p.ExitCode, stdout.Result, stderr.Result);
    }
}
