using OpenCvSharp;
using PrecisionMotion.Core.Configuration;
using PrecisionMotion.Core.Vision;

namespace PrecisionMotion.Tests;

public sealed class PatternMatcherTests
{
    [Fact]
    public void ConfigProvider_ShouldLoadRecognitionTunables()
    {
        var config = new ConfigProvider();

        Assert.Equal(0.60, config.GlobalConfig.ProfileMatchThreshold, 3);
        Assert.Equal(0.55, config.GlobalConfig.OpticMatchThreshold, 3);
        Assert.Equal(0.55, config.GlobalConfig.StanceMatchThreshold, 3);
        Assert.Equal(0.70, config.GlobalConfig.ComponentMatchThreshold, 3);
        Assert.True(config.GlobalConfig.NormalizeRecognition);
        Assert.False(config.GlobalConfig.RecognitionDebugEnabled);
        Assert.False(config.GlobalConfig.RecognitionDebugSaveCrops);
        Assert.Equal(3, config.GlobalConfig.RecognitionDebugTopN);
        Assert.Equal(1000, config.GlobalConfig.RecognitionDebugIntervalMs);
    }

    [Fact]
    public void PreprocessForMatch_ShouldConvertColorImageToNormalizedGray()
    {
        using var source = new Mat(6, 6, MatType.CV_8UC3, new Scalar(120, 120, 120));
        using (var roi = new Mat(source, new Rect(0, 0, 3, 6)))
        {
            roi.SetTo(new Scalar(130, 130, 130));
        }

        using var processed = PatternMatcher.PreprocessForMatch(source, normalizeRecognition: true);

        Cv2.MinMaxLoc(processed, out double minVal, out double maxVal);

        Assert.Equal(1, processed.Channels());
        Assert.Equal(source.Rows, processed.Rows);
        Assert.Equal(source.Cols, processed.Cols);
        Assert.True(minVal <= 10, $"Expected normalized minimum near black, got {minVal}");
        Assert.True(maxVal >= 245, $"Expected normalized maximum near white, got {maxVal}");
    }

    [Fact]
    public void MatchProfileFrame_ShouldUseLoadedProfileTemplates()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"precisionmotion-tests-{Guid.NewGuid():N}");
        string configDir = Path.Combine(tempRoot, "Configs", "testres");
        Directory.CreateDirectory(configDir);

        try
        {
            using (var alpha = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(0)))
            using (var beta = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(0)))
            {
                Cv2.Rectangle(alpha, new Rect(2, 2, 6, 6), Scalar.All(255), -1);
                Cv2.Line(beta, new Point(0, 0), new Point(9, 9), Scalar.All(255), 1);
                Cv2.ImWrite(Path.Combine(configDir, "shapealpha.png"), alpha);
                Cv2.ImWrite(Path.Combine(configDir, "shapebeta.png"), beta);
            }

            using var matcher = new PatternMatcher(tempRoot, "testres", normalizeRecognition: false);
            using var frame = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(0));
            Cv2.Rectangle(frame, new Rect(3, 3, 6, 6), Scalar.All(255), -1);

            var result = matcher.MatchProfileFrame(frame, 0.50);

            Assert.Equal("ALPHA", result.BestName);
            Assert.True(result.BestScore >= 0.99, $"Expected strong template score, got {result.BestScore}");

            var detailed = matcher.MatchProfileDetailedFrame(frame, 0.50, topN: 2);
            Assert.Equal("ALPHA", detailed.BestName);
            Assert.Equal(2, detailed.RankedCandidates.Count);
            Assert.True(detailed.RankedCandidates[0].Score >= detailed.RankedCandidates[1].Score);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
