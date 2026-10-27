using System.Globalization;
using System.Text;

namespace PrecisionMotion.UI;

public sealed class DiagnosticTelemetryCollector
{
    private readonly List<DiagnosticSample> _samples = [];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly string _model;
    private readonly string _provider;
    private readonly double _durationSeconds;

    public DiagnosticTelemetryCollector(string model, string provider, double durationSeconds = 60)
    {
        _model = model;
        _provider = provider;
        _durationSeconds = durationSeconds;
    }

    public double DurationSeconds => _durationSeconds;
    public int Count => _samples.Count;

    public void Add(int frame, double elapsedSeconds, double captureMs, double inferenceMs, double totalMs, int detections, double maxConfidence)
    {
        _samples.Add(new DiagnosticSample(frame, elapsedSeconds, captureMs, inferenceMs, totalMs, detections, maxConfidence));
    }

    public (string Csv, string Summary) Save()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrecisionMotion", "telemetry");
        Directory.CreateDirectory(folder);
        string stem = $"diagnostic_{_startedAt:yyyyMMdd_HHmmss}";
        string csvPath = Path.Combine(folder, stem + ".csv");
        string summaryPath = Path.Combine(folder, stem + ".summary.txt");
        var csv = new StringBuilder("frame,elapsed_seconds,capture_ms,inference_ms,total_ms,detected_count,max_confidence,provider,model\n");
        foreach (var sample in _samples)
            csv.AppendLine(FormattableString.Invariant($"{sample.Frame},{sample.ElapsedSeconds:F3},{sample.CaptureMs:F3},{sample.InferenceMs:F3},{sample.TotalMs:F3},{sample.Detections},{sample.MaxConfidence:F4},{_provider},{_model}"));
        File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);

        double[] capture = _samples.Select(s => s.CaptureMs).ToArray();
        double[] inference = _samples.Select(s => s.InferenceMs).ToArray();
        double[] total = _samples.Select(s => s.TotalMs).ToArray();
        double elapsed = _samples.Count == 0 ? 0 : _samples[^1].ElapsedSeconds;
        var lines = new[]
        {
            $"Model: {_model}",
            $"Provider: {_provider}",
            $"Started: {_startedAt:O}",
            $"Requested duration (seconds): {_durationSeconds}",
            $"Recorded duration (seconds): {elapsed.ToString("F2", CultureInfo.InvariantCulture)}",
            $"Processed frames: {_samples.Count}",
            $"Frames with detections: {_samples.Count(s => s.Detections > 0)}",
            $"Detection occurrences: {_samples.Sum(s => s.Detections)}",
            $"Effective FPS: {(_samples.Count / Math.Max(elapsed, 0.001)).ToString("F2", CultureInfo.InvariantCulture)}",
            Describe("Capture", capture),
            Describe("Inference", inference),
            Describe("Total", total),
            "Note: Detection counts do not establish precision or recall without labeled ground truth."
        };
        File.WriteAllLines(summaryPath, lines, Encoding.UTF8);
        return (csvPath, summaryPath);
    }

    private static string Describe(string name, double[] samples)
    {
        if (samples.Length == 0) return $"{name} (ms): no samples";
        Array.Sort(samples);
        return FormattableString.Invariant($"{name} (ms): mean={samples.Average():F2}; p50={Percentile(samples, 0.50):F2}; p95={Percentile(samples, 0.95):F2}; p99={Percentile(samples, 0.99):F2}; min={samples[0]:F2}; max={samples[^1]:F2}");
    }

    private static double Percentile(double[] values, double percentile)
    {
        double position = (values.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }

    private readonly record struct DiagnosticSample(int Frame, double ElapsedSeconds, double CaptureMs, double InferenceMs, double TotalMs, int Detections, double MaxConfidence);
}
