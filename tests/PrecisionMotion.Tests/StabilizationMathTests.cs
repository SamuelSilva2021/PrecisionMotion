using PrecisionMotion.Core.Configuration;
using PrecisionMotion.Core.Models;
using PrecisionMotion.Core.Stabilization;

namespace PrecisionMotion.Tests;

public class StabilizationMathTests
{
    [Fact]
    public void ConfigProvider_ShouldLocateAndLoadConfigurations()
    {
        var config = new ConfigProvider();
        Assert.NotNull(config.BaseDirectory);
        Assert.True(Directory.Exists(Path.Combine(config.BaseDirectory, "Configs")));

        Assert.NotEmpty(config.GlobalConfig.ActiveResolution);
        Assert.NotEmpty(config.Profiles);
        Assert.True(config.Profiles.ContainsKey("M4"));
    }

    [Fact]
    public void StanceCalculation_LowProfile_ShouldBeLowerThanStandard()
    {
        var config = new ConfigProvider();
        using var engine = new StabilizationEngine(config);

        // Standard stance
        var calcStandard = engine.CalculateCurrentStep();

        // Switch to low profile (crouched)
        var prop = typeof(StabilizationEngine).GetProperty("CurrentStance");
        prop?.SetValue(engine, StanceType.LowProfile);

        var calcLow = engine.CalculateCurrentStep();

        Assert.True(calcLow.VelocityPxPerSec < calcStandard.VelocityPxPerSec,
            $"Low profile speed ({calcLow.VelocityPxPerSec}) should be lower than standard speed ({calcStandard.VelocityPxPerSec})");
    }

    [Fact]
    public void OpticLookup_Unspecified_ShouldDefaultToNeutralOne()
    {
        var config = new ConfigProvider();
        var (val, status) = config.LookupComponent(config.OpticItems, "M4", "non_existent_scope.png", 1.0);

        Assert.Equal(1.0, val);
        Assert.Equal("sem_item", status);
    }

    [Fact]
    public void DynamicGain_Scaling_ShouldProportionallyScaleVelocity()
    {
        var config = new ConfigProvider();
        using var engine = new StabilizationEngine(config);

        var calc1 = engine.CalculateCurrentStep();

        var gainProp = typeof(StabilizationEngine).GetProperty("DynamicGain");
        gainProp?.SetValue(engine, engine.DynamicGain * 1.5);

        var calc2 = engine.CalculateCurrentStep();

        double ratio = calc2.VelocityPxPerSec / calc1.VelocityPxPerSec;
        Assert.InRange(ratio, 1.48, 1.52);
    }
}
