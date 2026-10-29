using System.Text;
using OpenCvSharp;
using PrecisionMotion.Core.Models;
using PrecisionMotion.Core.Vision;

namespace PrecisionMotion.Core.Configuration;

public sealed record TemplateCheck(string Template, string Kind, bool SelfMatched, double SelfScore, string? Rival, double RivalScore);

public sealed class CalibrationReport
{
    public string Resolution { get; init; } = "";
    public List<ConfigIssue> Issues { get; } = new();
    public List<TemplateCheck> TemplateChecks { get; } = new();

    public int Errors => Issues.Count(i => i.Severity == ConfigIssueSeverity.Error);
    public int Warnings => Issues.Count(i => i.Severity == ConfigIssueSeverity.Warning);
    public bool IsValid => Errors == 0;

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Calibração {Resolution}: {Errors} erro(s), {Warnings} aviso(s)");
        foreach (var issue in Issues.OrderByDescending(i => i.Severity))
            sb.AppendLine($"[{issue.Severity}] {issue.Code}: {issue.Message}");

        var weak = TemplateChecks.Where(t => !t.SelfMatched || t.RivalScore >= 0.9).ToList();
        sb.AppendLine($"Templates testados: {TemplateChecks.Count} | suspeitos: {weak.Count}");
        foreach (var t in weak)
            sb.AppendLine($"  {t.Kind} {t.Template}: self={t.SelfScore:F3} rival={t.Rival}:{t.RivalScore:F3}");
        return sb.ToString();
    }
}

/// <summary>
/// Valida a configuração de uma resolução sem depender do jogo: arquivos, thresholds, áreas
/// (tamanho, limites, sobreposição), templates ausentes e auto-reconhecimento dos templates principais.
/// </summary>
public static class CalibrationRunner
{
    private static readonly string[] RequiredStanceTemplates = { "empe.png", "agachado.png", "deitado.png" };

    public static CalibrationReport Run(ConfigProvider config, bool selfTest = true)
    {
        var g = config.GlobalConfig;
        var report = new CalibrationReport { Resolution = g.ActiveResolution };
        report.Issues.AddRange(config.LoadIssues);

        string resDir = Path.Combine(config.BaseDirectory, "Configs", g.ActiveResolution);
        if (!Directory.Exists(resDir))
        {
            report.Issues.Add(new(ConfigIssueSeverity.Error, "resolution_missing", $"Pasta da resolução ausente: {resDir}"));
            return report;
        }

        if (!TryParseResolution(g.ActiveResolution, out int screenW, out int screenH))
            report.Issues.Add(new(ConfigIssueSeverity.Error, "resolution_invalid", $"Resolução '{g.ActiveResolution}' fora do formato LxA."));

        CheckThresholds(g, report);
        CheckAreas(config, screenW, screenH, report);
        CheckTemplates(config, resDir, report);

        if (selfTest && !report.Issues.Any(i => i.Code == "resolution_missing"))
            RunSelfTest(config, report);

        return report;
    }

    public static bool TryParseResolution(string value, out int width, out int height)
    {
        width = height = 0;
        var parts = value.Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height)
            && width > 0 && height > 0;
    }

    private static void CheckThresholds(GlobalConfigData g, CalibrationReport report)
    {
        void Check(string name, double v)
        {
            if (double.IsNaN(v) || v < 0 || v > 1)
                report.Issues.Add(new(ConfigIssueSeverity.Error, "threshold_out_of_range", $"{name}={v} fora de [0,1]."));
            else if (v < 0.3)
                report.Issues.Add(new(ConfigIssueSeverity.Warning, "threshold_low", $"{name}={v:F2} muito baixo: risco de falso positivo."));
        }

        Check("limiar_match_perfil", g.ProfileMatchThreshold);
        Check("limiar_match_escopo", g.OpticMatchThreshold);
        Check("limiar_match_postura", g.StanceMatchThreshold);
        Check("limiar_match_componente", g.ComponentMatchThreshold);
        Check("limiar_mira_visual", g.VisualDetectionThreshold);

        if (g.ExitThresholdMargin >= g.ProfileMatchThreshold)
            report.Issues.Add(new(ConfigIssueSeverity.Warning, "hysteresis_margin", "histerese_margem_saida >= limiar de perfil: o estado nunca sairia por score baixo."));
    }

    private static void CheckAreas(ConfigProvider config, int screenW, int screenH, CalibrationReport report)
    {
        var named = new List<(string Name, ScreenRegion Region)>();
        void Add(string name, ScreenRegion? region)
        {
            if (region.HasValue) named.Add((name, region.Value));
        }

        Add("slot1", config.Slot1Area);
        Add("slot2", config.Slot2Area);
        if (!config.Slot1Area.HasValue)
            report.Issues.Add(new(ConfigIssueSeverity.Error, "area_missing", "Área do slot 1 não definida em calibragem/parameter*.json."));
        if (!config.Slot2Area.HasValue)
            report.Issues.Add(new(ConfigIssueSeverity.Warning, "area_missing", "Área do slot 2 não definida."));

        Add("postura", config.GlobalConfig.PostureCoords);
        foreach (var (slot, list) in config.OpticCandidateAreas)
            for (int i = 0; i < list.Count; i++) Add($"escopo_s{slot}_{i}", list[i]);
        foreach (var (slot, list) in config.Comp1CandidateAreas)
            for (int i = 0; i < list.Count; i++) Add($"comp1_s{slot}_{i}", list[i]);
        foreach (var (slot, list) in config.Comp2CandidateAreas)
            for (int i = 0; i < list.Count; i++) Add($"comp2_s{slot}_{i}", list[i]);

        foreach (var (name, r) in named)
        {
            if (r.IsEmpty)
                report.Issues.Add(new(ConfigIssueSeverity.Error, "area_invalid", $"{name}: tamanho inválido ({r.Width}x{r.Height})."));
            else if (r.X < 0 || r.Y < 0)
                report.Issues.Add(new(ConfigIssueSeverity.Error, "area_invalid", $"{name}: coordenadas negativas ({r.X},{r.Y})."));
            else if (screenW > 0 && (r.X + r.Width > screenW || r.Y + r.Height > screenH))
                report.Issues.Add(new(ConfigIssueSeverity.Error, "area_out_of_screen", $"{name}: ({r.X},{r.Y},{r.Width}x{r.Height}) excede a tela {screenW}x{screenH}."));
        }

        // Sobreposição só entre categorias diferentes: candidatos da mesma categoria são alternativas.
        static string Category(string name) => name.Split('_')[0];
        for (int i = 0; i < named.Count; i++)
        {
            for (int j = i + 1; j < named.Count; j++)
            {
                var a = named[i];
                var b = named[j];
                if (a.Region.IsEmpty || b.Region.IsEmpty) continue;
                if (Category(a.Name) == Category(b.Name) && Category(a.Name) is "escopo" or "comp1" or "comp2") continue;
                if (Overlaps(a.Region, b.Region))
                    report.Issues.Add(new(ConfigIssueSeverity.Warning, "area_overlap", $"{a.Name} sobrepõe {b.Name}."));
            }
        }
    }

    private static bool Overlaps(ScreenRegion a, ScreenRegion b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static void CheckTemplates(ConfigProvider config, string resDir, CalibrationReport report)
    {
        string[] files = Directory.GetFiles(resDir, "*.png").Select(f => Path.GetFileName(f)).ToArray();
        var existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(Path.Combine(resDir, "coord.cfg")))
            report.Issues.Add(new(ConfigIssueSeverity.Error, "file_missing", "coord.cfg ausente."));

        foreach (var stance in RequiredStanceTemplates.Where(s => !existing.Contains(s)))
            report.Issues.Add(new(ConfigIssueSeverity.Error, "template_missing", $"Template de postura ausente: {stance}"));

        void CheckImages(string kind, IEnumerable<ComponentItem> items)
        {
            foreach (var item in items.Where(i => !string.IsNullOrEmpty(i.CheckImage)))
            {
                if (!existing.Contains(item.CheckImage))
                    report.Issues.Add(new(ConfigIssueSeverity.Error, "template_missing", $"{kind}: imagem '{item.CheckImage}' não existe em {Path.GetFileName(resDir)}."));
            }
        }

        CheckImages("escopo", config.OpticItems);
        CheckImages("componente1", config.ComponentGroup1);
        CheckImages("componente2", config.ComponentGroup2);

        if (config.Profiles.Count == 0)
            report.Issues.Add(new(ConfigIssueSeverity.Error, "profiles_empty", "Nenhum perfil de arma carregado."));
    }

    private static void RunSelfTest(ConfigProvider config, CalibrationReport report)
    {
        using var matcher = new PatternMatcher(config.BaseDirectory, config.GlobalConfig.ActiveResolution, config.GlobalConfig.NormalizeRecognition);

        void Test(string kind, IReadOnlyDictionary<string, Mat> set)
        {
            foreach (var (name, template) in set)
            {
                using var frame = PadForSelfTest(template);
                var result = matcher.MatchDetailed(frame, 0.0, set, 2);
                var self = result.RankedCandidates.FirstOrDefault(c => c.Name == name);
                var rival = result.RankedCandidates.FirstOrDefault(c => c.Name != name);
                report.TemplateChecks.Add(new TemplateCheck(
                    name, kind, result.TopName == name, self.Score, rival.Name, rival.Score));
            }
        }

        Test("perfil", matcher.ProfileTemplates);
        Test("escopo", matcher.OpticTemplates);
        Test("postura", matcher.StanceTemplates);
    }

    /// <summary>Borda replicada: simula o template dentro de uma região levemente maior, como na captura real.</summary>
    internal static Mat PadForSelfTest(Mat template)
    {
        var padded = new Mat();
        Cv2.CopyMakeBorder(template, padded, 3, 3, 3, 3, BorderTypes.Replicate);
        return padded;
    }
}
