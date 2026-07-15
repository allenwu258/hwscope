namespace HwScope.Core.Tests.Cli;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_DoesNotTreatOptionValueAsPciCommand()
    {
        var options = CliOptions.Parse(["benchmark", "storage", "--drive", "C:", "--workload", "pci"]);

        Assert.True(options.StorageBenchmark);
        Assert.False(options.PciMode);
    }

    [Fact]
    public void Parse_AllowsGlobalJsonOptionBeforePciCommand()
    {
        var options = CliOptions.Parse(["--json", "pcie"]);

        Assert.True(options.PciMode);
        Assert.True(options.Json);
        Assert.False(options.IncludeSensitiveIds);
    }

    [Fact]
    public void Parse_RequiresExplicitFlagToIncludeSensitiveIds()
    {
        var options = CliOptions.Parse(["pcie", "--json", "--include-sensitive-ids"]);

        Assert.True(options.PciMode);
        Assert.True(options.IncludeSensitiveIds);
    }
}
