using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCvSharp;
using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

public sealed class RecognitionDiagnostics
{
    private readonly GlobalConfigData _config;
    private readonly string _outputDirectory;
    private readonly ConcurrentDictionary<string, long> _lastEmissionTicks = new(StringComparer.OrdinalIgnoreCase);

    public RecognitionDiagnostics(string baseDirectory, GlobalConfigData config)
    {
        _config = config;
        _outputDirectory = Path.IsPathRooted(config.RecognitionDebugDirectory)
            ? config.RecognitionDebugDirectory
            : Path.Combine(Directory.GetParent(baseDirectory)?.FullName ?? baseDirectory, config.RecognitionDebugDirectory);
    }

    public bool IsEnabled => _config.RecognitionDebugEnabled;

    public string OutputDirectory => _outputDirectory;

    public bool ShouldEmit(string key)
    {
        if (!IsEnabled)
            return false;

        long now = Stopwatch.GetTimestamp();
        long minDelta = (long)(Stopwatch.Frequency * (_config.RecognitionDebugIntervalMs / 1000.0));
        long last = _lastEmissionTicks.GetOrAdd(key, 0);
        if (now - last < minDelta)
            return false;

        _lastEmissionTicks[key] = now;
        return true;
    }

    public string FormatCandidates(MatchDebugResult result)
    {
        if (result.RankedCandidates.Count == 0)
            return "sem candidatos";

        return string.Join(", ", result.RankedCandidates.Select(c => $"{c.Name}:{c.Score:F3}"));
    }

    public void SaveCrop(string label, Mat? frame)
    {
        if (!IsEnabled || !_config.RecognitionDebugSaveCrops || frame == null || frame.Empty())
            return;

        Directory.CreateDirectory(_outputDirectory);
        string safeLabel = string.Concat(label.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_'));
        string file = Path.Combine(_outputDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{safeLabel}.png");
        Cv2.ImWrite(file, frame);
    }
}
