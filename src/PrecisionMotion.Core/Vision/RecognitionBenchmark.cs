using System.Text;
using System.Text.Json;
using OpenCvSharp;
using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

public static class BenchmarkCategory
{
    public const string Profile = "profile";
    public const string Optic = "optic";
    public const string Stance = "stance";

    public static readonly string[] All = { Profile, Optic, Stance };
}

public sealed record BenchmarkSample(string Category, string Expected, string Source, Mat Frame);

public sealed record BenchmarkPrediction(
    string Category,
    string Expected,
    string Source,
    string? Predicted,
    double Score,
    int ExpectedRank,
    bool Accepted,
    IReadOnlyList<MatchCandidateScore> Top)
{
    public bool Top1Correct => ExpectedRank == 1;
    public bool Top3Correct => ExpectedRank is >= 1 and <= 3;
    /// <summary>Aceito pelo limiar mas com classe errada: o pior tipo de falha em produção.</summary>
    public bool ConfidentWrong => Accepted && !Top1Correct;
}

public sealed record ClassAccuracy(string Category, string Class, int Samples, int Top1, int Top3, int Rejected)
{
    public double Accuracy => Samples == 0 ? 0.0 : (double)Top1 / Samples;
}

public sealed record CategorySummary(string Category, int Samples, double Top1, double Top3, double RejectedRate, int ConfidentWrong);

public sealed class BenchmarkReport
{
    public double HighScoreWrongMinScore { get; init; }
    public List<BenchmarkPrediction> Predictions { get; init; } = new();
    public List<CategorySummary> Categories { get; init; } = new();
    public List<ClassAccuracy> Classes { get; init; } = new();

    /// <summary>Predições erradas com score alto (aceitas pelo limiar ou acima de <see cref="HighScoreWrongMinScore"/>).</summary>
    public List<BenchmarkPrediction> HighScoreWrong { get; init; } = new();

    /// <summary>Matriz por categoria: esperado → previsto ("(nenhum)" quando não há candidato) → contagem.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> Confusion { get; init; } = new();

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark de reconhecimento");
        sb.AppendLine();
        sb.AppendLine("| Categoria | Amostras | Top-1 | Top-3 | Rejeitadas | Erro com score alto |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var c in Categories)
            sb.AppendLine($"| {c.Category} | {c.Samples} | {c.Top1:P1} | {c.Top3:P1} | {c.RejectedRate:P1} | {c.ConfidentWrong} |");

        foreach (var cat in Categories)
        {
            sb.AppendLine();
            sb.AppendLine($"## Acurácia por classe — {cat.Category}");
            sb.AppendLine("| Classe | Amostras | Top-1 | Top-3 | Rejeitadas |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var c in Classes.Where(c => c.Category == cat.Category).OrderBy(c => c.Accuracy).ThenBy(c => c.Class))
                sb.AppendLine($"| {c.Class} | {c.Samples} | {c.Accuracy:P0} | {(c.Samples == 0 ? 0 : (double)c.Top3 / c.Samples):P0} | {c.Rejected} |");
        }

        sb.AppendLine();
        sb.AppendLine($"## Erros com score alto (>= {HighScoreWrongMinScore:F2} ou aceitos pelo limiar)");
        if (HighScoreWrong.Count == 0)
            sb.AppendLine("Nenhum.");
        foreach (var p in HighScoreWrong.OrderByDescending(p => p.Score))
            sb.AppendLine($"- [{p.Category}] esperado **{p.Expected}**, previsto **{p.Predicted}** ({p.Score:F3}) — {p.Source}");

        return sb.ToString();
    }

    public string ConfusionCsv(string category)
    {
        var sb = new StringBuilder();
        if (!Confusion.TryGetValue(category, out var matrix))
            return string.Empty;

        var predicted = matrix.Values.SelectMany(r => r.Keys).Distinct().OrderBy(s => s).ToList();
        sb.AppendLine("esperado;" + string.Join(';', predicted));
        foreach (var (expected, row) in matrix.OrderBy(r => r.Key))
            sb.AppendLine(expected + ";" + string.Join(';', predicted.Select(p => row.GetValueOrDefault(p, 0))));
        return sb.ToString();
    }

    public void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "benchmark.md"), ToMarkdown());
        foreach (var category in Confusion.Keys)
            File.WriteAllText(Path.Combine(directory, $"confusion_{category}.csv"), ConfusionCsv(category));

        var json = new
        {
            categories = Categories,
            classes = Classes,
            highScoreWrong = HighScoreWrong.Select(p => new { p.Category, p.Expected, p.Predicted, p.Score, p.Source })
        };
        File.WriteAllText(Path.Combine(directory, "benchmark.json"),
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Avalia o matcher offline sobre crops rotulados. Layout do dataset: <c>&lt;raiz&gt;/&lt;categoria&gt;/&lt;Classe&gt;/*.png</c>
/// com categoria em {profile, optic, stance}.
/// </summary>
public static class RecognitionBenchmark
{
    public const string NoCandidate = "(nenhum)";

    public static BenchmarkReport Run(
        PatternMatcher matcher,
        IEnumerable<BenchmarkSample> samples,
        double profileThreshold,
        double opticThreshold,
        double stanceThreshold,
        double highScoreWrongMinScore = 0.80)
    {
        var predictions = new List<BenchmarkPrediction>();

        foreach (var sample in samples)
        {
            var (candidates, threshold) = sample.Category switch
            {
                BenchmarkCategory.Profile => (matcher.ProfileTemplates, profileThreshold),
                BenchmarkCategory.Optic => (matcher.OpticTemplates, opticThreshold),
                BenchmarkCategory.Stance => (matcher.StanceTemplates, stanceThreshold),
                _ => throw new ArgumentException($"Categoria desconhecida: {sample.Category}")
            };

            // Ranking completo: top-3 e posição da classe esperada exigem mais que o placar do 1º colocado.
            var result = matcher.MatchDetailed(sample.Frame, threshold, candidates, candidates.Count);
            int rank = 0;
            for (int i = 0; i < result.RankedCandidates.Count; i++)
            {
                if (string.Equals(result.RankedCandidates[i].Name, sample.Expected, StringComparison.OrdinalIgnoreCase))
                {
                    rank = i + 1;
                    break;
                }
            }

            predictions.Add(new BenchmarkPrediction(
                sample.Category, sample.Expected, sample.Source, result.TopName, result.BestScore,
                rank, result.BestName != null, result.RankedCandidates.Take(3).ToArray()));
        }

        return Summarize(predictions, highScoreWrongMinScore);
    }

    public static BenchmarkReport Summarize(IReadOnlyList<BenchmarkPrediction> predictions, double highScoreWrongMinScore = 0.80)
    {
        var report = new BenchmarkReport
        {
            HighScoreWrongMinScore = highScoreWrongMinScore,
            Predictions = predictions.ToList()
        };

        foreach (var byCategory in predictions.GroupBy(p => p.Category).OrderBy(g => g.Key))
        {
            var items = byCategory.ToList();
            report.Categories.Add(new CategorySummary(
                byCategory.Key,
                items.Count,
                (double)items.Count(p => p.Top1Correct) / items.Count,
                (double)items.Count(p => p.Top3Correct) / items.Count,
                (double)items.Count(p => !p.Accepted) / items.Count,
                items.Count(p => p.ConfidentWrong)));

            foreach (var byClass in items.GroupBy(p => p.Expected, StringComparer.OrdinalIgnoreCase))
            {
                report.Classes.Add(new ClassAccuracy(
                    byCategory.Key, byClass.Key, byClass.Count(),
                    byClass.Count(p => p.Top1Correct), byClass.Count(p => p.Top3Correct),
                    byClass.Count(p => !p.Accepted)));
            }

            var matrix = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in items)
            {
                if (!matrix.TryGetValue(p.Expected, out var row))
                    matrix[p.Expected] = row = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                string predicted = p.Predicted ?? NoCandidate;
                row[predicted] = row.GetValueOrDefault(predicted) + 1;
            }
            report.Confusion[byCategory.Key] = matrix;
        }

        report.HighScoreWrong.AddRange(predictions.Where(p => !p.Top1Correct && (p.Accepted || p.Score >= highScoreWrongMinScore)));
        return report;
    }

    /// <summary>Carrega crops de <c>&lt;raiz&gt;/&lt;categoria&gt;/&lt;Classe&gt;/*.png|jpg|bmp</c>. O chamador descarta os Mats.</summary>
    public static List<BenchmarkSample> LoadDataset(string root)
    {
        var samples = new List<BenchmarkSample>();
        foreach (var category in BenchmarkCategory.All)
        {
            string categoryDir = Path.Combine(root, category);
            if (!Directory.Exists(categoryDir)) continue;

            foreach (var classDir in Directory.GetDirectories(categoryDir))
            {
                string expected = Path.GetFileName(classDir);
                foreach (var file in Directory.EnumerateFiles(classDir)
                             .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)))
                {
                    var frame = Cv2.ImRead(file, ImreadModes.Grayscale);
                    if (frame.Empty())
                    {
                        frame.Dispose();
                        continue;
                    }

                    samples.Add(new BenchmarkSample(category, expected, file, frame));
                }
            }
        }

        return samples;
    }

    /// <summary>
    /// Amostras sintéticas derivadas dos próprios templates (borda, variação de brilho e ruído gaussiano).
    /// Serve como teste de regressão quando não há crops reais; não substitui um dataset capturado do jogo.
    /// </summary>
    public static List<BenchmarkSample> BuildSyntheticSamples(PatternMatcher matcher, int variantsPerTemplate = 3, int seed = 42)
    {
        var rng = new Random(seed);
        var samples = new List<BenchmarkSample>();

        void AddAll(string category, IReadOnlyDictionary<string, Mat> set)
        {
            foreach (var (name, template) in set)
            {
                for (int v = 0; v < variantsPerTemplate; v++)
                {
                    var padded = new Mat();
                    Cv2.CopyMakeBorder(template, padded, 4, 4, 4, 4, BorderTypes.Replicate);

                    if (v > 0)
                    {
                        using var noise = new Mat(padded.Size(), MatType.CV_8UC1);
                        Cv2.Randn(noise, Scalar.All(0), Scalar.All(6 * v));
                        Cv2.Add(padded, noise, padded);
                        padded.ConvertTo(padded, -1, 1.0, rng.Next(-20, 21));
                    }

                    samples.Add(new BenchmarkSample(category, name, $"synthetic:{name}#{v}", padded));
                }
            }
        }

        AddAll(BenchmarkCategory.Profile, matcher.ProfileTemplates);
        AddAll(BenchmarkCategory.Optic, matcher.OpticTemplates);
        AddAll(BenchmarkCategory.Stance, matcher.StanceTemplates);
        return samples;
    }
}
