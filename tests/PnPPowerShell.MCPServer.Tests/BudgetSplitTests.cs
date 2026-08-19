using PnPPowerShell.MCPServer.Tools;

namespace PnPPowerShell.MCPServer.Tests;

/// <summary>Covers the split that keeps analysis plus execution inside the configured timeout.</summary>
public class BudgetSplitTests
{
    [Theory]
    [InlineData(600)]
    [InlineData(60)]
    [InlineData(20)]
    [InlineData(15)]
    [InlineData(5)]
    [InlineData(1)]
    public void Analysis_plus_the_reserved_floor_never_exceeds_the_budget(int seconds)
    {
        var budget = TimeSpan.FromSeconds(seconds);

        var (analysis, floor) = PnPPowerShellTools.SplitBudget(budget);

        // The worst case is analysis using its whole cap and execution then taking the floor.
        Assert.True(analysis + floor <= budget, $"{analysis} + {floor} exceeds {budget}");
    }

    [Theory]
    [InlineData(600)]
    [InlineData(60)]
    [InlineData(20)]
    [InlineData(15)]
    [InlineData(5)]
    [InlineData(1)]
    public void Both_slices_are_usable(int seconds)
    {
        var (analysis, floor) = PnPPowerShellTools.SplitBudget(TimeSpan.FromSeconds(seconds));

        Assert.True(analysis > TimeSpan.Zero, "Analysis was given no time at all.");
        Assert.True(floor > TimeSpan.Zero, "Execution was reserved no time at all.");
    }

    [Fact]
    public void A_generous_budget_reserves_a_fixed_ten_seconds()
    {
        var (analysis, floor) = PnPPowerShellTools.SplitBudget(TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.FromSeconds(10), floor);
        Assert.Equal(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(10), analysis);
    }

    [Fact]
    public void A_short_budget_is_halved_rather_than_reserving_a_fixed_slice()
    {
        // Reserving a flat 10s out of a 15s budget would leave analysis almost nothing.
        var (analysis, floor) = PnPPowerShellTools.SplitBudget(TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(7.5), floor);
        Assert.Equal(TimeSpan.FromSeconds(7.5), analysis);
    }
}
