using Xunit;

namespace Raft.Gates.Tests;

public sealed class SabotageTests
{
    [Fact]
    public void SabotageFails() => Assert.Equal(1, 2);
}
