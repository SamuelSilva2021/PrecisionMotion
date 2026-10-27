using System.Globalization;

namespace PrecisionMotion.Core.Vision.Visualization;

public readonly record struct OfflinePosePoint(int Frame, int PersonId, string Landmark, double X, double Y, double Confidence);
public readonly record struct OfflinePoseMetrics(int GroundTruthPoints, int MatchedPoints, int MissingPoints, int ExtraPoints, double MeanErrorPixels, double MedianErrorPixels, double P95ErrorPixels, double Recall, double Precision);

public static class OfflinePoseEvaluator
{
    public static OfflinePoseMetrics Evaluate(
        IReadOnlyCollection<OfflinePosePoint> groundTruth,
        IReadOnlyCollection<OfflinePosePoint> predictions,
        double tolerancePixels = 20,
        double minimumConfidence = 0.2)
    {
        ArgumentNullException.ThrowIfNull(groundTruth);
        ArgumentNullException.ThrowIfNull(predictions);
        if (tolerancePixels <= 0) throw new ArgumentOutOfRangeException(nameof(tolerancePixels));
        if (minimumConfidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minimumConfidence));

        var expected = groundTruth.ToDictionary(p => (p.Frame, p.PersonId, p.Landmark));
        var predicted = predictions.Where(p => p.Confidence >= minimumConfidence)
            .ToDictionary(p => (p.Frame, p.PersonId, p.Landmark));
        var errors = new List<double>();
        int matches = 0;
        foreach (var (key, actual) in expected)
        {
            if (!predicted.TryGetValue(key, out var prediction)) continue;
            double dx = actual.X - prediction.X;
            double dy = actual.Y - prediction.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            errors.Add(distance);
            if (distance <= tolerancePixels) matches++;
        }

        errors.Sort();
        int extras = predicted.Keys.Count(key => !expected.ContainsKey(key));
        int misses = expected.Count - matches;
        int incorrect = errors.Count - matches;
        double precision = predicted.Count == 0 ? 0 : (double)matches / (matches + extras + incorrect);
        double recall = expected.Count == 0 ? 0 : (double)matches / expected.Count;
        return new OfflinePoseMetrics(expected.Count, matches, misses, extras,
            errors.Count == 0 ? 0 : errors.Average(), Percentile(errors, 0.5), Percentile(errors, 0.95), recall, precision);
    }

    public static IReadOnlyList<OfflinePosePoint> ReadCsv(string path)
    {
        using var reader = new StreamReader(path);
        string? header = reader.ReadLine();
        if (!string.Equals(header?.TrimStart('\uFEFF'), "frame,person_id,landmark,x,y,confidence", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected CSV header: frame,person_id,landmark,x,y,confidence");

        var points = new List<OfflinePosePoint>();
        string? line;
        int lineNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] fields = line.Split(',');
            if (fields.Length != 6) throw new InvalidDataException($"Invalid CSV row {lineNumber}");
            var point = new OfflinePosePoint(
                int.Parse(fields[0], CultureInfo.InvariantCulture),
                int.Parse(fields[1], CultureInfo.InvariantCulture),
                fields[2].Trim(),
                double.Parse(fields[3], CultureInfo.InvariantCulture),
                double.Parse(fields[4], CultureInfo.InvariantCulture),
                double.Parse(fields[5], CultureInfo.InvariantCulture));
            if (point.Frame < 0 || point.PersonId < 0 || point.Landmark.Length == 0 ||
                !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                !double.IsFinite(point.Confidence) || point.Confidence is < 0 or > 1)
                throw new InvalidDataException($"Invalid values on CSV row {lineNumber}");
            points.Add(point);
        }
        return points;
    }

    public static OfflinePoseMetrics EvaluateCsv(string groundTruthPath, string predictionsPath,
        double tolerancePixels = 20, double minimumConfidence = 0.2) =>
        Evaluate(ReadCsv(groundTruthPath), ReadCsv(predictionsPath), tolerancePixels, minimumConfidence);

    public static void WriteSummary(string path, OfflinePoseMetrics result)
    {
        File.WriteAllLines(path,
        [
            $"Ground truth points: {result.GroundTruthPoints}",
            $"Matched within tolerance: {result.MatchedPoints}",
            $"Missing or outside tolerance: {result.MissingPoints}",
            $"Extra predictions: {result.ExtraPoints}",
            FormattableString.Invariant($"Mean localization error (px): {result.MeanErrorPixels:F2}"),
            FormattableString.Invariant($"Median localization error (px): {result.MedianErrorPixels:F2}"),
            FormattableString.Invariant($"P95 localization error (px): {result.P95ErrorPixels:F2}"),
            FormattableString.Invariant($"Recall: {result.Recall:P2}"),
            FormattableString.Invariant($"Precision: {result.Precision:P2}"),
            "Identity labels must be manually aligned across files. This evaluates prerecorded data only."
        ]);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0) return 0;
        double position = (sorted.Count - 1) * fraction;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
