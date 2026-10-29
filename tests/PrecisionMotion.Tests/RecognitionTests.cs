using OpenCvSharp;
using PrecisionMotion.Core.Configuration;
using PrecisionMotion.Core.Models;
using PrecisionMotion.Core.Vision;

namespace PrecisionMotion.Tests;

public sealed class HysteresisTrackerTests
{
    private static MatchDebugResult Result(params (string Name, double Score)[] ranked)
    {
        var list = ranked.Select(r => new MatchCandidateScore(r.Name, r.Score)).OrderByDescending(c => c.Score).ToArray();
        double best = list.Length > 0 ? list[0].Score : 0;
        return new MatchDebugResult(list.Length > 0 ? list[0].Name : null, best, list)
        {
            Margin = list.Length > 1 ? best - list[1].Score : best
        };
    }

    [Fact]
    public void Switch_RequiresNConsecutiveConfirmations()
    {
        var tracker = new HysteresisTracker(3, 0.05, 0.02, "A");

        Assert.Equal(HysteresisState.Pending, tracker.Observe(Result(("B", 0.9), ("A", 0.5)), 0.6).State);
        Assert.Equal(HysteresisState.Pending, tracker.Observe(Result(("B", 0.9), ("A", 0.5)), 0.6).State);
        Assert.Equal("A", tracker.Current);

        var third = tracker.Observe(Result(("B", 0.9), ("A", 0.5)), 0.6);
        Assert.True(third.Changed);
        Assert.Equal("B", tracker.Current);
    }

    [Fact]
    public void InterleavedReading_ResetsPendingCount()
    {
        var tracker = new HysteresisTracker(3, 0.05, 0.02, "A");

        tracker.Observe(Result(("B", 0.9), ("A", 0.5)), 0.6);
        tracker.Observe(Result(("B", 0.9), ("A", 0.5)), 0.6);
        tracker.Observe(Result(("A", 0.9), ("B", 0.5)), 0.6); // volta ao atual
        tracker.Observe(Result(("B", 0.9), ("A", 0.5)), 0.6);

        Assert.Equal("A", tracker.Current);
    }

    [Fact]
    public void CurrentState_SurvivesScoreBetweenExitAndEnterThreshold()
    {
        var tracker = new HysteresisTracker(1, 0.10, 0.02);
        Assert.True(tracker.Observe(Result(("A", 0.80), ("B", 0.3)), 0.70).Changed);

        // 0.65 < limiar de entrada (0.70), mas >= saída (0.60): permanece.
        var held = tracker.Observe(Result(("A", 0.65), ("B", 0.3)), 0.70);
        Assert.Equal(HysteresisState.Held, held.State);
        Assert.Equal("A", tracker.Current);
    }

    [Fact]
    public void NewState_RequiresEnterThreshold()
    {
        var tracker = new HysteresisTracker(1, 0.10, 0.02);

        var outcome = tracker.Observe(Result(("A", 0.65), ("B", 0.3)), 0.70);

        Assert.False(outcome.Changed);
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void CloseScores_DoNotOscillateBetweenTemplates()
    {
        var tracker = new HysteresisTracker(1, 0.05, 0.05, "A");

        var outcome = tracker.Observe(Result(("B", 0.82), ("A", 0.80)), 0.60);

        Assert.Equal(HysteresisState.Ambiguous, outcome.State);
        Assert.Equal("A", tracker.Current);
    }

    [Fact]
    public void AllowNone_SettlesOnEmptyAfterConfirmations()
    {
        var tracker = new HysteresisTracker(2, 0.05, 0.02, "SCOPE");
        var noMatch = new MatchDebugResult(null, 0.2, new[] { new MatchCandidateScore("SCOPE", 0.2) });

        tracker.Observe(noMatch, 0.7, allowNone: true);
        Assert.Equal("SCOPE", tracker.Current);
        tracker.Observe(noMatch, 0.7, allowNone: true);

        Assert.Equal(HysteresisTracker.None, tracker.Current);
    }
}

public sealed class MatcherRankingTests
{
    [Fact]
    public void MatchDetailed_ReturnsRankedCandidatesWithMargin()
    {
        using var data = new TestDataDir();
        data.WriteTemplate("shapealpha.png", TestDataDir.Rectangle(10, 2));
        data.WriteTemplate("shapebeta.png", TestDataDir.Diagonal(10));
        using var matcher = new PatternMatcher(data.Root, TestDataDir.Resolution, normalizeRecognition: false);
        using var frame = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(frame, new Rect(3, 3, 6, 6), Scalar.All(255), -1);

        var result = matcher.MatchProfileDetailedFrame(frame, 0.5, topN: 5);

        Assert.Equal("ALPHA", result.BestName);
        Assert.Equal(2, result.RankedCandidates.Count);
        Assert.Equal(result.BestScore - result.RankedCandidates[1].Score, result.Margin, 6);
        Assert.Equal(0.5, result.Threshold);
    }

    [Fact]
    public void TemplateLargerThanRegion_IsSkippedAndReported()
    {
        using var data = new TestDataDir();
        data.WriteTemplate("shapealpha.png", TestDataDir.Rectangle(20, 4));
        data.WriteTemplate("shapebeta.png", TestDataDir.Rectangle(8, 1));
        using var matcher = new PatternMatcher(data.Root, TestDataDir.Resolution, normalizeRecognition: false);
        using var frame = TestDataDir.Rectangle(12, 2);

        var result = matcher.MatchProfileDetailedFrame(frame, 0.1);

        Assert.Equal(1, result.SkippedCandidates);
        Assert.DoesNotContain(result.RankedCandidates, c => c.Name == "ALPHA");
    }

    [Fact]
    public void AllTemplatesLargerThanRegion_YieldsNoMatchWithoutThrowing()
    {
        using var data = new TestDataDir();
        data.WriteTemplate("shapealpha.png", TestDataDir.Rectangle(20, 4));
        using var matcher = new PatternMatcher(data.Root, TestDataDir.Resolution, normalizeRecognition: false);
        using var frame = TestDataDir.Rectangle(6, 1);

        var result = matcher.MatchProfileDetailedFrame(frame, 0.1);

        Assert.Null(result.BestName);
        Assert.Equal(0.0, result.BestScore);
        Assert.Equal(1, result.SkippedCandidates);
    }

    [Fact]
    public void FlatFrame_ScoreIsNeverNaN()
    {
        using var data = new TestDataDir();
        data.WriteTemplate("shapealpha.png", TestDataDir.Rectangle(10, 2));
        using var matcher = new PatternMatcher(data.Root, TestDataDir.Resolution, normalizeRecognition: false);
        using var frame = new Mat(20, 20, MatType.CV_8UC1, Scalar.All(40));

        var result = matcher.MatchProfileDetailedFrame(frame, 0.5);

        Assert.False(double.IsNaN(result.BestScore));
        Assert.Null(result.BestName);
    }
}

public sealed class ConfigValidationTests
{
    [Fact]
    public void OutOfRangeThreshold_IsClampedAndReported()
    {
        using var data = new TestDataDir();
        data.WriteConfig("\"limiar_match_perfil\":1.7,\"histerese_confirmacoes\":0");
        data.WriteCalibration();
        data.WriteResolutionParameters();

        var config = new ConfigProvider(data.Root);

        Assert.Equal(1.0, config.GlobalConfig.ProfileMatchThreshold);
        Assert.Equal(3, config.GlobalConfig.StableConfirmations);
        Assert.Contains(config.LoadIssues, i => i.Code == "threshold_out_of_range");
        Assert.Contains(config.LoadIssues, i => i.Code == "value_out_of_range");
    }

    [Fact]
    public void MalformedJson_IsReportedInsteadOfSwallowed()
    {
        using var data = new TestDataDir();
        File.WriteAllText(Path.Combine(data.GeneralDir, "config.json"), "{ not json");
        File.WriteAllText(Path.Combine(data.CalibrationDir, "parameter1.json"), "[[[");
        data.WriteResolutionParameters();

        var config = new ConfigProvider(data.Root);

        Assert.Contains(config.LoadIssues, i => i.Code == "parse_error" && i.Message.Contains("config.json"));
        Assert.Contains(config.LoadIssues, i => i.Code == "parse_error" && i.Message.Contains("parameter1.json"));
    }

    [Fact]
    public void MissingFiles_AreReported()
    {
        using var data = new TestDataDir();
        data.WriteConfig();

        var config = new ConfigProvider(data.Root);

        Assert.Contains(config.LoadIssues, i => i.Code == "file_missing" && i.Message.Contains("parameter1.json"));
        Assert.Contains(config.LoadIssues, i => i.Code == "file_missing" && i.Message.Contains("coord.cfg"));
    }

    [Fact]
    public void CalibrationRunner_FlagsInvalidAreasMissingTemplatesAndOverlap()
    {
        using var data = new TestDataDir();
        data.WriteConfig();
        data.WriteCalibration(slot1: "[10,10,50,40]", slot2: "[30,20,70,60]"); // sobrepostas
        data.WriteCoord();
        data.WriteResolutionParameters(
            "[{\"check_image\":\"ausente.png\",\"check_area\":[0,0,0,0],\"check_area2\":[2000,10,2040,40]}]");
        data.WriteTemplate("shapealpha.png", TestDataDir.Rectangle(10, 2));

        var report = CalibrationRunner.Run(new ConfigProvider(data.Root), selfTest: false);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, i => i.Code == "area_overlap");
        Assert.Contains(report.Issues, i => i.Code == "area_invalid");
        Assert.Contains(report.Issues, i => i.Code == "area_out_of_screen");
        Assert.Contains(report.Issues, i => i.Code == "template_missing" && i.Message.Contains("ausente.png"));
        Assert.Contains(report.Issues, i => i.Code == "template_missing" && i.Message.Contains("empe.png"));
    }

    [Fact]
    public void CalibrationRunner_SelfTestRecognizesOwnTemplates()
    {
        using var data = new TestDataDir();
        data.WriteConfig();
        data.WriteCalibration();
        data.WriteCoord();
        data.WriteResolutionParameters();
        data.WriteTemplate("shapealpha.png", TestDataDir.Rectangle(12, 2));
        data.WriteTemplate("shapebeta.png", TestDataDir.Diagonal(12));

        var report = CalibrationRunner.Run(new ConfigProvider(data.Root));

        Assert.Equal(2, report.TemplateChecks.Count(t => t.Kind == "perfil"));
        Assert.All(report.TemplateChecks, t => Assert.True(t.SelfMatched, t.Template));
    }
}

public sealed class RecognitionServiceTests
{
    private sealed class Rig : IDisposable
    {
        public TestDataDir Data { get; } = new();
        public PatternMatcher Matcher { get; private set; } = null!;
        public RecognitionService Service { get; private set; } = null!;
        public Mat Screen { get; } = new(300, 200, MatType.CV_8UC1, Scalar.All(0));
        public ConfigProvider Config { get; private set; } = null!;

        public Rig(string extraConfig = "")
        {
            Data.WriteConfig(extraConfig);
            Data.WriteCalibration(slot1: "[10,10,40,40]", slot2: "[10,100,40,130]");
            Data.WriteCoord();
            Data.WriteResolutionParameters();
            Data.WriteTemplate("shapealpha.png", TestDataDir.Rectangle(30, 4));
            Data.WriteTemplate("shapebeta.png", TestDataDir.Diagonal(30));

            Config = new ConfigProvider(Data.Root);
            Matcher = new PatternMatcher(Data.Root, TestDataDir.Resolution, Config.GlobalConfig.NormalizeRecognition)
            {
                FrameProvider = region =>
                {
                    var rect = new Rect(
                        Math.Clamp(region.X, 0, Screen.Width), Math.Clamp(region.Y, 0, Screen.Height), 0, 0);
                    rect = new Rect(rect.X, rect.Y,
                        Math.Min(region.Width, Screen.Width - rect.X), Math.Min(region.Height, Screen.Height - rect.Y));
                    return rect.Width <= 0 || rect.Height <= 0 ? null : new Mat(Screen, rect).Clone();
                }
            };
            var diagnostics = new RecognitionDiagnostics(Data.Root, Config.GlobalConfig);
            Service = new RecognitionService(Config, Matcher, diagnostics,
                new RecognitionTelemetry(Path.Combine(Data.Root, "telemetry"), enabled: true));
        }

        public void Draw(int slot, Mat template)
        {
            var area = slot == 1 ? Config.Slot1Area!.Value : Config.Slot2Area!.Value;
            var roi = new Mat(Screen, new Rect(area.X, area.Y, area.Width, area.Height));
            roi.SetTo(Scalar.All(0));
            using var resized = new Mat();
            Cv2.Resize(template, resized, new Size(area.Width, area.Height));
            resized.CopyTo(roi);
        }

        public void Dispose()
        {
            Service.Dispose();
            Matcher.Dispose();
            Screen.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void Inventory_BecomesReadyOnlyAfterRequiredStableReads()
    {
        using var rig = new Rig();
        rig.Draw(1, TestDataDir.Rectangle(30, 4));
        rig.Service.BeginInventorySession();

        var first = rig.Service.PollInventory();
        var second = rig.Service.PollInventory();
        var third = rig.Service.PollInventory();

        Assert.True(first.Visible);
        Assert.Null(first.Profile1);
        Assert.False(first.Ready);
        Assert.Null(second.Profile1);
        Assert.Equal("ALPHA", third.Profile1);
        Assert.True(third.Ready);
    }

    [Fact]
    public void Inventory_OneNoisyFrameDoesNotChangeWeapon()
    {
        using var rig = new Rig();
        rig.Service.BeginInventorySession();
        rig.Draw(1, TestDataDir.Rectangle(30, 4));
        for (int i = 0; i < 3; i++) rig.Service.PollInventory();

        rig.Draw(1, TestDataDir.Diagonal(30));
        var glitch = rig.Service.PollInventory();
        rig.Draw(1, TestDataDir.Rectangle(30, 4));
        var recovered = rig.Service.PollInventory();

        Assert.Equal("ALPHA", glitch.Profile1);
        Assert.Equal("ALPHA", recovered.Profile1);
    }

    [Fact]
    public void Inventory_SwitchesAfterConsecutiveConfirmations()
    {
        using var rig = new Rig();
        rig.Service.BeginInventorySession();
        rig.Draw(1, TestDataDir.Rectangle(30, 4));
        for (int i = 0; i < 3; i++) rig.Service.PollInventory();

        rig.Draw(1, TestDataDir.Diagonal(30));
        InventoryRecognitionResult last = rig.Service.PollInventory();
        for (int i = 0; i < 2; i++) last = rig.Service.PollInventory();

        Assert.Equal("BETA", last.Profile1);
    }

    [Fact]
    public void Inventory_BlankScreenIsHidden()
    {
        using var rig = new Rig();
        rig.Service.BeginInventorySession();

        var result = rig.Service.PollInventory();

        Assert.False(result.Visible);
        Assert.False(result.Ready);
    }

    [Fact]
    public void Snapshot_ExposesTopCandidatesThresholdAndRegion()
    {
        using var rig = new Rig();
        rig.Service.BeginInventorySession();
        rig.Draw(1, TestDataDir.Rectangle(30, 4));
        rig.Service.PollInventory();

        var snapshot = rig.Service.GetSnapshot();
        var slot1 = snapshot.Observations.Single(o => o.Key == "profile_slot1");

        Assert.Equal("ALPHA", slot1.RawBest);
        Assert.Equal(rig.Config.GlobalConfig.ProfileMatchThreshold, slot1.Threshold);
        Assert.NotNull(slot1.Region);
        Assert.NotEmpty(slot1.TopCandidates);
    }
}

public sealed class BenchmarkAndTelemetryTests
{
    [Fact]
    public void Summarize_ComputesAccuracyConfusionAndHighScoreErrors()
    {
        var top = new[] { new MatchCandidateScore("B", 0.9), new MatchCandidateScore("A", 0.5) };
        var predictions = new List<BenchmarkPrediction>
        {
            new("profile", "A", "s1", "A", 0.95, 1, true, top),
            new("profile", "A", "s2", "B", 0.90, 2, true, top),   // errado com score alto
            new("profile", "B", "s3", "B", 0.40, 1, false, top),  // certo mas rejeitado
        };

        var report = RecognitionBenchmark.Summarize(predictions);

        var summary = Assert.Single(report.Categories);
        Assert.Equal(2.0 / 3, summary.Top1, 6);
        Assert.Equal(1.0, summary.Top3, 6);
        Assert.Equal(1, summary.ConfidentWrong);
        Assert.Equal(0.5, report.Classes.Single(c => c.Class == "A").Accuracy);
        Assert.Equal(1, report.Confusion["profile"]["A"]["B"]);
        Assert.Single(report.HighScoreWrong);
        Assert.Contains("A;B", report.ConfusionCsv("profile"));
    }

    [Fact]
    public void Run_OnSyntheticSamples_ClassifiesEveryTemplate()
    {
        using var data = new TestDataDir();
        data.WriteTemplate("shapealpha.png", TestDataDir.Rectangle(20, 3));
        data.WriteTemplate("shapebeta.png", TestDataDir.Diagonal(20));
        data.WriteTemplate("empe.png", TestDataDir.Rectangle(16, 2));
        data.WriteTemplate("agachado.png", TestDataDir.Diagonal(16));
        using var matcher = new PatternMatcher(data.Root, TestDataDir.Resolution, normalizeRecognition: true);
        var samples = RecognitionBenchmark.BuildSyntheticSamples(matcher, variantsPerTemplate: 2);

        try
        {
            var report = RecognitionBenchmark.Run(matcher, samples, 0.6, 0.55, 0.55);

            Assert.All(report.Categories, c => Assert.Equal(1.0, c.Top1));
            Assert.Equal(4 * 2, report.Predictions.Count);
            Assert.Empty(report.HighScoreWrong);
        }
        finally
        {
            foreach (var s in samples) s.Frame.Dispose();
        }
    }

    [Fact]
    public void Telemetry_TracksMeanScoreNoMatchAndPersistsJson()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"pm-telemetry-{Guid.NewGuid():N}");
        try
        {
            using var telemetry = new RecognitionTelemetry(dir, enabled: true);
            var hit1 = new MatchDebugResult("M4", 0.8, new[] { new MatchCandidateScore("M4", 0.8) });
            var hit2 = new MatchDebugResult("M4", 0.6, new[] { new MatchCandidateScore("M4", 0.6) });
            var miss = new MatchDebugResult(null, 0.3, new[] { new MatchCandidateScore("M4", 0.3) });

            telemetry.Record("profile_slot1", hit1);
            telemetry.Record("profile_slot1", hit2);
            using var crop = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(10));
            telemetry.Record("profile_slot1", miss, crop);
            telemetry.Flush();

            var summary = telemetry.GetSummary();
            Assert.Equal(0.7, summary.Classes.Single().MeanScore, 6);
            Assert.Equal(1, summary.Categories.Single().NoMatch);
            Assert.Equal(1, telemetry.FailureCropsSaved);
            Assert.True(File.Exists(Path.Combine(dir, "telemetry.json")));
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "failures")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Telemetry_HistoryIsBounded()
    {
        using var telemetry = new RecognitionTelemetry(Path.Combine(Path.GetTempPath(), $"pm-telemetry-{Guid.NewGuid():N}"), enabled: true);
        var hit = new MatchDebugResult("X", 0.9, new[] { new MatchCandidateScore("X", 0.9) });

        for (int i = 0; i < RecognitionTelemetry.HistoryCapacity + 50; i++)
            telemetry.Record("stance", hit);

        Assert.Equal(RecognitionTelemetry.HistoryCapacity, telemetry.GetSummary().RecentHistory.Count);
    }
}
