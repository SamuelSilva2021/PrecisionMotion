using PrecisionMotion.Core.Configuration;
using OpenCvSharp;
using PrecisionMotion.Core.Vision;

// Uso:
//   benchmark [--dataset <dir>] [--synthetic] [--out <dir>] [--data <pasta com Configs>] [--resolution <LxA>]
//   calibrate [--data <pasta com Configs>] [--resolution <LxA>] [--no-selftest]
// Layout do dataset: <dir>/<profile|optic|stance>/<Classe>/*.png

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Uso: PrecisionMotion.Benchmark <benchmark|calibrate> [opções]");
    Console.WriteLine("  benchmark: --dataset <dir> | --synthetic, --out <dir>, --data <dir>, --resolution <LxA>");
    Console.WriteLine("  calibrate: --data <dir>, --resolution <LxA>, --no-selftest");
    return 0;
}

string command = args[0];
string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Flag(string name) => Array.IndexOf(args, name) >= 0;

var config = new ConfigProvider(Opt("--data"));
string? resolution = Opt("--resolution");
if (resolution != null)
{
    config.ResolutionOverride = resolution;
    config.ReloadAll();
}

switch (command)
{
    case "calibrate":
    {
        var report = CalibrationRunner.Run(config, selfTest: !Flag("--no-selftest"));
        Console.WriteLine(report.ToText());
        return report.IsValid ? 0 : 1;
    }

    case "perf":
    {
        var g = config.GlobalConfig;
        using var matcher = new PatternMatcher(config.BaseDirectory, g.ActiveResolution, g.NormalizeRecognition);
        Console.WriteLine($"Resolução {g.ActiveResolution} | perfis={matcher.ProfileTemplates.Count} escopos={matcher.OpticTemplates.Count} postura={matcher.StanceTemplates.Count}");
        Console.WriteLine($"Áreas: escopo={config.OpticCandidateAreas.Sum(k => k.Value.Count)} comp1={config.Comp1CandidateAreas.Sum(k => k.Value.Count)} comp2={config.Comp2CandidateAreas.Sum(k => k.Value.Count)}");
        Console.WriteLine($"OpenCV threads: {Cv2.GetNumThreads()}");

        static void Measure(string label, int iterations, Action action)
        {
            action(); // aquecimento
            long alloc0 = GC.GetAllocatedBytesForCurrentThread();
            int gc0 = GC.CollectionCount(0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            sw.Stop();
            Console.WriteLine($"{label,-34} {sw.Elapsed.TotalMilliseconds / iterations,8:F2} ms/leitura | {(GC.GetAllocatedBytesForCurrentThread() - alloc0) / iterations / 1024.0,8:F0} KB/leitura | GC0={GC.CollectionCount(0) - gc0}");
        }

        // Captura real da tela (GDI) — custo sem o jogo; com o jogo em tela cheia pode ser maior.
        var stance = g.PostureCoords;
        Measure("captura GDI postura", 100, () => { using var f = PrecisionMotion.Core.Vision.ScreenCaptureService.CaptureRegion(stance); });
        var big = new PrecisionMotion.Core.Models.ScreenRegion(0, 0, 900, 700);
        Measure("captura GDI 900x700 (inventário)", 50, () => { using var f = PrecisionMotion.Core.Vision.ScreenCaptureService.CaptureRegion(big); });

        // Casamento puro (sem GDI): tela sintética com ruído.
        using var screen = new Mat(2160, 3840, MatType.CV_8UC1);
        Cv2.Randu(screen, Scalar.All(0), Scalar.All(255));
        matcher.FrameProvider = r =>
        {
            var rect = new Rect(Math.Clamp(r.X, 0, 3000), Math.Clamp(r.Y, 0, 1500), Math.Max(1, r.Width), Math.Max(1, r.Height));
            rect = new Rect(rect.X, rect.Y, Math.Min(rect.Width, screen.Width - rect.X), Math.Min(rect.Height, screen.Height - rect.Y));
            return new Mat(screen, rect).Clone();
        };
        var diagnostics = new RecognitionDiagnostics(config.BaseDirectory, g);
        using var service = new RecognitionService(config, matcher, diagnostics, new RecognitionTelemetry("perf_tmp", false));
        service.BeginInventorySession();
        Measure("PollStance (casamento)", 100, () => service.PollStance());
        Measure("PollInventory (casamento+cache)", 20, () => service.PollInventory());
        return 0;
    }

    case "benchmark":
    {
        var g = config.GlobalConfig;
        using var matcher = new PatternMatcher(config.BaseDirectory, g.ActiveResolution, g.NormalizeRecognition);
        string? dataset = Opt("--dataset");
        var samples = dataset != null
            ? RecognitionBenchmark.LoadDataset(dataset)
            : RecognitionBenchmark.BuildSyntheticSamples(matcher);

        if (samples.Count == 0)
        {
            Console.Error.WriteLine("Nenhuma amostra encontrada (esperado <dataset>/<profile|optic|stance>/<Classe>/*.png).");
            return 2;
        }

        try
        {
            var report = RecognitionBenchmark.Run(matcher, samples, g.ProfileMatchThreshold, g.OpticMatchThreshold, g.StanceMatchThreshold);
            string outDir = Opt("--out") ?? Path.Combine(Directory.GetParent(config.BaseDirectory)?.FullName ?? ".", "benchmark_output", "offline");
            report.WriteTo(outDir);
            Console.WriteLine(report.ToMarkdown());
            Console.WriteLine($"Relatórios gravados em {outDir}");
            return 0;
        }
        finally
        {
            foreach (var s in samples) s.Frame.Dispose();
        }
    }

    default:
        Console.Error.WriteLine($"Comando desconhecido: {command}");
        return 2;
}
