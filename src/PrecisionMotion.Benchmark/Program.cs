using PrecisionMotion.Core.Configuration;
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
