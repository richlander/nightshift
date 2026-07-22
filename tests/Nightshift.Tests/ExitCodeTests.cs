namespace Nightshift.Tests;

using Nightshift;
using Xunit;

/// <summary>Locks the exit-code contract the agent loop scripts against — values must stay stable + distinct.</summary>
public class ExitCodeTests
{
    [Fact]
    public void Values_AreStableAndDistinct()
    {
        int[] codes =
        [
            ExitCode.Ok, ExitCode.Usage, ExitCode.NoClaim, ExitCode.NoWork,
            ExitCode.Draining, ExitCode.Query, ExitCode.Halt, ExitCode.FenceStale,
            ExitCode.Coordinate, ExitCode.NoCoordinate, ExitCode.Interrupted,
        ];

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, c => Assert.InRange(c, 0, 130));

        Assert.Equal(0, ExitCode.Ok);
        Assert.Equal(2, ExitCode.Usage);
        Assert.Equal(3, ExitCode.NoClaim);
        Assert.Equal(10, ExitCode.NoWork);
        Assert.Equal(11, ExitCode.Draining);
        Assert.Equal(12, ExitCode.Query);
        Assert.Equal(13, ExitCode.Halt);
        Assert.Equal(14, ExitCode.FenceStale);
        Assert.Equal(20, ExitCode.Coordinate);
        Assert.Equal(21, ExitCode.NoCoordinate);
        Assert.Equal(130, ExitCode.Interrupted);
    }
}
