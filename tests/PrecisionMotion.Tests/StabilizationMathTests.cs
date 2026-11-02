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

public class MasterCalibrationTests
{
    [Fact]
    public void MasterCalibration_ScalesEveryWeaponProportionallyAndKeepsTheirDifferences()
    {
        var config = new ConfigProvider();
        using var engine = new StabilizationEngine(config);
        int original = config.GlobalConfig.MasterCalibration;
        try
        {
            string[] weapons = { "M4", "AKM" };
            var baseline = new Dictionary<string, double>();
            var scaled = new Dictionary<string, double>();

            foreach (var w in weapons)
            {
                typeof(StabilizationEngine).GetProperty("Slot1Profile")!.SetValue(engine, w);
                config.GlobalConfig.MasterCalibration = 100;
                baseline[w] = engine.CalculateCurrentStep().VelocityPxPerSec;
                config.GlobalConfig.MasterCalibration = 200;
                scaled[w] = engine.CalculateCurrentStep().VelocityPxPerSec;
            }

            foreach (var w in weapons)
                Assert.InRange(scaled[w] / baseline[w], 1.99, 2.01);

            // A diferença de calibragem entre as armas se mantém (a razão M4/AKM não muda com o slider).
            Assert.InRange((scaled["M4"] / scaled["AKM"]) / (baseline["M4"] / baseline["AKM"]), 0.99, 1.01);
        }
        finally
        {
            config.GlobalConfig.MasterCalibration = original;
        }
    }

    [Fact]
    public void ApplyMasterCalibration_DoesNotRewriteWeaponFilesAndClampsRange()
    {
        using var data = new TestDataDir();
        data.WriteConfig();
        data.WriteCalibration();
        data.WriteResolutionParameters();
        string path = Path.Combine(data.CalibrationDir, "parameter1.json");
        string before = File.ReadAllText(path);

        var config = new ConfigProvider(data.Root);
        config.ApplyMasterCalibration(999);

        Assert.Equal(ConfigProvider.MasterCalibrationMax, config.GlobalConfig.MasterCalibration);
        Assert.Equal(before, File.ReadAllText(path));
    }
}
