using System.Globalization;
using System.Text;
using Raft.Core;
using Raft.Simulation;

namespace Raft.Membership.Tests;

/// <summary>
/// The soak's key-value workload over five nodes, three of them the initial configuration (P6-08),
/// with membership requests among the operations (P6 decision 6): one in <c>oneIn</c>, naming three of
/// the five servers, drawn afresh, to a random node like any operation. A timed-out request is sent
/// again, same bytes.
/// </summary>
internal sealed class MembershipWorkload(long think, int oneIn = 180) : IClientWorkload
{
    public const int Universe = 5;

    public ClientCall? Retry(int client, ClientCall timedOut, IRandomSource random) =>
        new(new NodeId(1 + (int)random.NextLong(Universe)), timedOut.Request);

    public ClientCall? NextCall(int client, int sequence, IRandomSource random)
    {
        var node = new NodeId(1 + (int)random.NextLong(Universe));
        if (random.NextLong(oneIn) == 0)
        {
            return new ClientCall(node, Encoding.ASCII.GetBytes("Member|" + Three(random)), think);
        }

        var key = "k" + random.NextLong(6).ToString(CultureInfo.InvariantCulture);
        var value = "c" + client.ToString(CultureInfo.InvariantCulture) + "s" + sequence.ToString(CultureInfo.InvariantCulture);
        var command = random.NextLong(5) switch
        {
            0 => "Put|" + key + "|" + value,
            1 => "Append|" + key + "|" + value,
            2 => "Get|" + key,
            3 => "Delete|" + key,
            _ => "Cas|" + key + "|-|" + value,
        };
        return new ClientCall(node, Encoding.ASCII.GetBytes(command), think);
    }

    /// <summary>Three of the five servers, in order, uniformly.</summary>
    private static string Three(IRandomSource random)
    {
        var ids = new[] { 1, 2, 3, 4, 5 };
        for (var i = ids.Length - 1; i > 0; i--)
        {
            var j = (int)random.NextLong(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }

        System.Array.Sort(ids, 0, 3);
        return string.Join(",", ids[0], ids[1], ids[2]);
    }
}
