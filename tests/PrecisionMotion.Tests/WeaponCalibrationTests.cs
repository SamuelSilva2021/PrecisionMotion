using PrecisionMotion.Core.Configuration;
using PrecisionMotion.Core.Models;
using PrecisionMotion.Core.Stabilization;
using PrecisionMotion.Core.Vision;

namespace PrecisionMotion.Tests;

public class WeaponCalibrationTests
{
    private static ConfigProvider NewConfig(TestDataDir data, string extra = "")
    {
        data.WriteConfig(extra);
        data.WriteCalibration();
        data.WriteResolutionParameters();
        return new ConfigProvider(data.Root);
    }

    [Fact]
    public void Default_OnlyM4StartsBelowFullStrength()
    {
        using var data = new TestDataDir();
        var config = NewConfig(data);

        Assert.Equal(75, config.WeaponCalibrationPercent("M4"));
        Assert.Equal(100, config.WeaponCalibrationPercent("AUG"));
        Assert.Equal(100, config.WeaponCalibrationPercent("BERYL"));
    }

    [Fact]
    public void Adjust_ChangesOnlyThatWeaponAndSurvivesReload()
    {
        using var data = new TestDataDir();
        var config = NewConfig(data);

        Assert.Equal(70, config.AdjustWeaponCalibration("M4", -5));

        Assert.Equal(100, config.WeaponCalibrationPercent("AUG"));
        Assert.Equal(70, new ConfigProvider(data.Root).WeaponCalibrationPercent("M4"));
    }

    [Fact]
    public void Adjust_ClampsRange()
    {
        using var data = new TestDataDir();
        var config = NewConfig(data, "\"calibragem_armas\":{\"AUG\":295}");

        Assert.Equal(ConfigProvider.MasterCalibrationMax, config.AdjustWeaponCalibration("AUG", 20));
    }

    [Fact]
    public void RefreshTuning_PicksUpEditedWeaponCalibration()
    {
        using var data = new TestDataDir();
        var config = NewConfig(data, "\"calibragem_armas\":{\"AUG\":90}");
        config.RefreshTuningIfChanged();

        data.WriteConfig("\"calibragem_armas\":{\"M4\":60}");
        File.SetLastWriteTimeUtc(Path.Combine(data.GeneralDir, "config.json"), DateTime.UtcNow.AddSeconds(5));

        Assert.True(config.RefreshTuningIfChanged());
        Assert.Equal(60, config.WeaponCalibrationPercent("M4"));
        Assert.Equal(100, config.WeaponCalibrationPercent("AUG"));
    }

    [Fact]
    public void Engine_WeaponFactorScalesOnlyThatWeapon()
    {
        using var data = new TestDataDir();
        var config = NewConfig(data, "\"calibragem_armas\":{\"ALPHA\":50}");
        using var engine = new StabilizationEngine(config);
        var slot1 = typeof(StabilizationEngine).GetProperty("Slot1Profile")!;

        slot1.SetValue(engine, "ALPHA");
        double alpha = engine.CalculateCurrentStep().VelocityPxPerSec;
        slot1.SetValue(engine, "BETA");
        double beta = engine.CalculateCurrentStep().VelocityPxPerSec;

        Assert.InRange(alpha / beta, 0.49, 0.51);
    }
}

public class BurstTimingStatsTests
{
    [Fact]
    public void SlowerLoopThanNominal_DeliversLessThanRequested()
    {
        var timing = new BurstTimingStats();
        // 600 px/s a 15 ms nominais são 9 px por passo; com passos reais de 20 ms saem 450 px/s.
        for (int i = 0; i < 100; i++)
            timing.AddInterval(0.020, 600.0, 9);

        Assert.Equal(0.020, timing.MeanIntervalSeconds, 6);
        Assert.Equal(600.0, timing.RequestedPxPerSec, 6);
        Assert.Equal(450.0, timing.ActualPxPerSec, 6);
        Assert.Equal(0.75, timing.DeliveredRatio, 6);
    }

    [Fact]
    public void TracksLongestIntervalAndResets()
    {
        var timing = new BurstTimingStats();
        timing.AddInterval(0.015, 600.0, 9);
        timing.AddInterval(0.040, 600.0, 9);
        Assert.Equal(0.040, timing.MaxIntervalSeconds, 6);

        timing.Reset();
        Assert.Equal(0, timing.Intervals);
        Assert.Equal(0.0, timing.DeliveredRatio);
    }
}

public class ComponentThresholdTests
{
    private static MatchDebugResult Read(double score, double margin) =>
        new(null, score, new[] { new MatchCandidateScore("compensador.png", score), new MatchCandidateScore("outro.png", score - margin) })
        {
            Margin = margin
        };

    private static string? Observe(params (double Score, double Margin)[] reads)
    {
        var tracker = new HysteresisTracker(3, 0.05, 0.02);
        foreach (var (score, margin) in reads)
        {
            var read = Read(score, margin);
            tracker.Observe(read, RecognitionService.ComponentThreshold(read, 0.70), allowNone: true);
        }

        return tracker.Current;
    }

    [Fact]
    public void DecisiveCandidateAroundThreshold_IsRecognized() =>
        Assert.Equal("compensador.png", Observe((0.687, 0.2), (0.701, 0.2), (0.688, 0.2)));

    [Fact]
    public void EmptySlotWithoutMargin_StaysNone() =>
        Assert.Equal(HysteresisTracker.None, Observe((0.454, 0.005), (0.454, 0.005), (0.454, 0.005)));

    [Fact]
    public void LowScoreEvenWithMargin_StaysNone() =>
        Assert.Equal(HysteresisTracker.None, Observe((0.50, 0.2), (0.50, 0.2), (0.50, 0.2)));
}
