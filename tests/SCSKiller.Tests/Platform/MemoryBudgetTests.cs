using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

/// <summary>"Maximum memory for compiling: Auto": a step per common RAM size, thresholds between them.</summary>
public class MemoryBudgetTests
{
    const long GB = 1L << 30;

    [Theory]
    [InlineData(16, 2)]
    [InlineData(24, 4)]
    [InlineData(32, 6)]
    [InlineData(48, 8)]
    [InlineData(64, 16)]
    [InlineData(128, 16)]  // the maximum
    [InlineData(8, 2)]     // the minimum
    [InlineData(96, 16)]
    public void AutoFollowsTheTable(int ramGB, int budgetGB) => Assert.Equal(budgetGB, ScsKiller.AutoCompileMemoryGB(ramGB * GB));

    [Fact]
    public void ReportedSizesJustUnderACommonSizeLandOnIt()
    {
        Assert.Equal(2, ScsKiller.AutoCompileMemoryGB((long)(15.8 * GB)));   // a 16 GB PC with memory reserved by the firmware / iGPU
        Assert.Equal(6, ScsKiller.AutoCompileMemoryGB((long)(31.7 * GB)));
        Assert.Equal(16, ScsKiller.AutoCompileMemoryGB((long)(63.6 * GB)));
        Assert.InRange(ScsKiller.AutoCompileMemoryGB(), 2, 16);             // this PC
    }
}
