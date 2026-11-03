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

public class TemporalCompensationTests
{
    private static readonly double[] BerylPattern = { 6.73, 6.73, 2.588, 2.588 };
    private static readonly double[] BerylTime = { 0.425, 0.85, 1.275, 1.7, 2.125, 2.55, 2.975 };

    [Fact]
    public void Envelope_NeverExceedsSustainedLevel()
    {
        Assert.Equal(2.588, RecoilEnvelope.Rate(BerylPattern, BerylTime, 0.0), 6);
        Assert.Equal(2.588, RecoilEnvelope.Rate(BerylPattern, BerylTime, 0.9), 6);
        Assert.Equal(2.588, RecoilEnvelope.Rate(BerylPattern, BerylTime, 5.0), 6);
    }

    [Fact]
    public void Envelope_RisingPatternKeepsItsShape()
    {
        double[] rising = { 1, 2, 3, 4 };
        Assert.Equal(1.0, RecoilEnvelope.Rate(rising, BerylTime, 0.0), 6);
        Assert.Equal(4.0, RecoilEnvelope.Rate(rising, BerylTime, 5.0), 6);
    }

    [Fact]
    public void Envelope_IsMonotonicWhileDecayingAndNeverExceedsPeak()
    {
        double previous = double.MaxValue;
        for (double t = 0; t <= 3.5; t += 0.05)
        {
            double rate = RecoilEnvelope.Rate(BerylPattern, BerylTime, t);
            Assert.True(rate <= previous + 1e-9, $"subiu em t={t}");
            Assert.True(rate <= RecoilEnvelope.Peak(BerylPattern) + 1e-9);
            previous = rate;
        }
    }

    [Fact]
    public void NegativePattern_FallsBackToConstantPeak()
    {
        double[] m4 = { 1, -5, 2.513, 2.513 };

        Assert.False(RecoilEnvelope.IsTimeShaped(m4));
        Assert.Equal(5.0, RecoilEnvelope.Rate(m4, BerylTime, 0.0));
        Assert.Equal(5.0, RecoilEnvelope.Rate(m4, BerylTime, 2.0));
    }

    [Fact]
    public void Engine_OnlyBerylUsesTimeShape()
    {
        var config = new ConfigProvider();
        using var engine = new StabilizationEngine(config);
        var slot1 = typeof(StabilizationEngine).GetProperty("Slot1Profile")!;

        slot1.SetValue(engine, "BERYL");
        double berylNoBurst = engine.CalculateCurrentStep().VelocityPxPerSec; // pico constante
        double berylEarly = engine.CalculateCurrentStep(0.0).VelocityPxPerSec;
        double berylLate = engine.CalculateCurrentStep(3.0).VelocityPxPerSec;
        Assert.Equal(berylEarly, berylLate, 6);
        Assert.InRange(berylEarly / berylNoBurst, 0.68, 0.70); // (6.73 + 2.588) / 2 / 6.73

        slot1.SetValue(engine, "M4");
        Assert.Equal(engine.CalculateCurrentStep().VelocityPxPerSec, engine.CalculateCurrentStep(3.0).VelocityPxPerSec, 6);
    }
}

public class TemporalTuningTests
{
    private static readonly double[] Pattern = { 6.73, 6.73, 2.588, 2.588 };
    private static readonly double[] Time = { 0.425, 0.85, 1.275, 1.7, 2.125, 2.55, 2.975 };

    [Fact]
    public void Flatten_BlendsBetweenPatternShapeAndConstantPeak()
    {
        Assert.Equal(2.588, RecoilEnvelope.Rate(Pattern, Time, 3.0, flatten: 0.0), 6);
        Assert.Equal(6.73, RecoilEnvelope.Rate(Pattern, Time, 3.0, flatten: 1.0), 6);
        Assert.Equal((6.73 + 2.588) / 2, RecoilEnvelope.Rate(Pattern, Time, 3.0, flatten: 0.5), 6);
        Assert.Equal((6.73 + 2.588) / 2, RecoilEnvelope.Rate(Pattern, Time, 0.0, flatten: 0.5), 6); // começo limitado ao nível estável
    }

    [Fact]
    public void RefreshTuning_PicksUpEditedConfigWithoutRestart()
    {
        using var data = new TestDataDir();
        data.WriteConfig("\"calibragem\":100");
        data.WriteCalibration();
        data.WriteResolutionParameters();
        var config = new ConfigProvider(data.Root);
        Assert.False(config.RefreshTuningIfChanged()); // nada mudou

        string path = Path.Combine(data.GeneralDir, "config.json");
        data.WriteConfig("\"calibragem\":180");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.True(config.RefreshTuningIfChanged());
        Assert.Equal(180, config.GlobalConfig.MasterCalibration);
        Assert.False(config.RefreshTuningIfChanged()); // já aplicado
    }

    [Fact]
    public void RefreshTuning_IgnoresHalfWrittenFileAndRecoversNextTime()
    {
        using var data = new TestDataDir();
        data.WriteConfig("\"calibragem\":100");
        data.WriteCalibration();
        data.WriteResolutionParameters();
        var config = new ConfigProvider(data.Root);
        string path = Path.Combine(data.GeneralDir, "config.json");

        File.WriteAllText(path, "{ \"configs\": [ { \"calibragem\": ");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.False(config.RefreshTuningIfChanged());
        Assert.Equal(100, config.GlobalConfig.MasterCalibration);

        data.WriteConfig("\"calibragem\":150");
        Assert.True(config.RefreshTuningIfChanged());
        Assert.Equal(150, config.GlobalConfig.MasterCalibration);
    }
}
