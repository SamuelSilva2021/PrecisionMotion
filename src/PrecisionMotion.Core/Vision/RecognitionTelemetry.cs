using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

public sealed record TelemetryEntry(
    DateTime Timestamp,
    string Category,
    string? Best,
    double Score,
    double Margin,
    bool Matched);

public sealed record ClassStats(string Category, string Name, int Count, double MeanScore, double MinScore, double MaxScore);

public sealed record CategoryStats(string Category, int Reads, int NoMatch, double NoMatchRate);

public sealed record TelemetrySummary(
    DateTime GeneratedAt,
    IReadOnlyList<CategoryStats> Categories,
    IReadOnlyList<ClassStats> Classes,
    IReadOnlyList<TelemetryEntry> RecentHistory);

/// <summary>
/// Telemetria persistida dos reconhecimentos: histórico curto, score médio por classe, contagem de
/// "sem match" por categoria e crops de falha. Pensada para comparar versões/branches.
/// </summary>
public sealed class RecognitionTelemetry : IDisposable
{
    public const int HistoryCapacity = 200;
    public const int MaxFailureCrops = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureCropInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Queue<TelemetryEntry> _history = new();
    private readonly Dictionary<(string Category, string Name), Accumulator> _classes = new();
    private readonly Dictionary<string, (int Reads, int NoMatch)> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastFailureCrop = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFlush = DateTime.UtcNow;
    private bool _dirty;
    private int _failureCropsSaved;

    private struct Accumulator
    {
        public int Count;
        public double Sum;
        public double Min;
        public double Max;
    }

    public RecognitionTelemetry(string directory, bool enabled)
    {
        _directory = directory;
        IsEnabled = enabled;
    }

    public bool IsEnabled { get; }
    public string Directory => _directory;
    public int FailureCropsSaved => _failureCropsSaved;

    public static string ResolveDirectory(string baseDirectory, string configured) =>
        Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(System.IO.Directory.GetParent(baseDirectory)?.FullName ?? baseDirectory, configured);

    public void Record(string category, MatchDebugResult result, Mat? frame = null)
    {
        if (!IsEnabled)
            return;

        bool matched = result.BestName != null;
        var entry = new TelemetryEntry(DateTime.Now, category, result.TopName, result.BestScore, result.Margin, matched);
        bool saveCrop = false;
        bool flush = false;

        lock (_gate)
        {
            _history.Enqueue(entry);
            while (_history.Count > HistoryCapacity)
                _history.Dequeue();

            _categories.TryGetValue(category, out var cat);
            _categories[category] = (cat.Reads + 1, cat.NoMatch + (matched ? 0 : 1));

            if (matched)
            {
                var key = (category, result.BestName!);
                _classes.TryGetValue(key, out var acc);
                acc.Min = acc.Count == 0 ? result.BestScore : Math.Min(acc.Min, result.BestScore);
                acc.Max = acc.Count == 0 ? result.BestScore : Math.Max(acc.Max, result.BestScore);
                acc.Count++;
                acc.Sum += result.BestScore;
                _classes[key] = acc;
            }
            else if (frame != null && _failureCropsSaved < MaxFailureCrops)
            {
                var now = DateTime.UtcNow;
                if (!_lastFailureCrop.TryGetValue(category, out var last) || now - last >= FailureCropInterval)
                {
                    _lastFailureCrop[category] = now;
                    _failureCropsSaved++;
                    saveCrop = true;
                }
            }

            _dirty = true;
            if (DateTime.UtcNow - _lastFlush >= FlushInterval)
            {
                _lastFlush = DateTime.UtcNow;
                flush = true;
            }
        }

        if (saveCrop)
            SaveFailureCrop(category, result, frame!);

        if (flush)
            Flush();
    }

    public TelemetrySummary GetSummary()
    {
        lock (_gate)
        {
            var categories = _categories
                .Select(kv => new CategoryStats(kv.Key, kv.Value.Reads, kv.Value.NoMatch,
                    kv.Value.Reads == 0 ? 0.0 : (double)kv.Value.NoMatch / kv.Value.Reads))
                .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var classes = _classes
                .Select(kv => new ClassStats(kv.Key.Category, kv.Key.Name, kv.Value.Count,
                    kv.Value.Count == 0 ? 0.0 : kv.Value.Sum / kv.Value.Count, kv.Value.Min, kv.Value.Max))
                .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new TelemetrySummary(DateTime.Now, categories, classes, _history.ToArray());
        }
    }

    public void Flush()
    {
        if (!IsEnabled)
            return;

        TelemetrySummary summary;
        lock (_gate)
        {
            if (!_dirty)
                return;
            _dirty = false;
        }

        summary = GetSummary();

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            });
            string target = Path.Combine(_directory, "telemetry.json");
            string temp = target + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, target, overwrite: true);
        }
        catch (IOException)
        {
            // Telemetria é best-effort: nunca derruba o motor.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SaveFailureCrop(string category, MatchDebugResult result, Mat frame)
    {
        try
        {
            string dir = Path.Combine(_directory, "failures");
            System.IO.Directory.CreateDirectory(dir);
            string safe = string.Concat(category.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
            string top = result.TopName ?? "none";
            string file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{safe}_{top}_{result.BestScore:F2}.png");
            Cv2.ImWrite(file, frame);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => Flush();
}
