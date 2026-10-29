using OpenCvSharp;
using PrecisionMotion.Core.Interop;
using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

public sealed class PatternMatcher : IDisposable
{
    public const int IconPadding = 24;
    public const double DefaultIconThreshold = 0.70;

    private static readonly HashSet<string> OpticNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "1X", "15X", "2X", "3X", "4X", "6X", "8X", "HOLO", "TERMICA", "012"
    };

    private readonly string _resDirectory;
    private readonly Dictionary<string, Mat> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mat> _profileTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mat> _opticTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mat> _stanceTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Mat> _allTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _normalizeRecognition;

    // Cada thread de reconhecimento (inventário, postura, benchmark) reaproveita o próprio buffer de resultado
    // do MatchTemplate em vez de alocar um Mat por template por leitura.
    private readonly ThreadLocal<Mat> _resultBuffer = new(() => new Mat(), trackAllValues: true);

    private readonly object _cacheLock = new();
    private Mat? _cachedFrame;
    private int _cacheX0;
    private int _cacheY0;

    static PatternMatcher()
    {
        // O pool do OpenCV (um worker por núcleo) disputaria CPU com o jogo para ganhar microssegundos em crops pequenos.
        Cv2.SetNumThreads(1);
    }

    public PatternMatcher(string baseDirectory, string resolution, bool normalizeRecognition = true)
    {
        _resDirectory = Path.Combine(baseDirectory, "Configs", resolution);
        _normalizeRecognition = normalizeRecognition;
        LoadTemplates();
    }

    /// <summary>Origem dos frames (padrão: captura de tela). Substituível para benchmarks e testes offline.</summary>
    public Func<ScreenRegion, Mat?> FrameProvider { get; set; } = ScreenCaptureService.CaptureRegion;

    public bool NormalizeRecognition => _normalizeRecognition;
    public IReadOnlyDictionary<string, Mat> ProfileTemplates => _profileTemplates;
    public IReadOnlyDictionary<string, Mat> OpticTemplates => _opticTemplates;
    public IReadOnlyDictionary<string, Mat> StanceTemplates => _stanceTemplates;
    public IReadOnlyDictionary<string, Mat> AllTemplates => _allTemplates;

    public static Mat PreprocessForMatch(Mat source, bool normalizeRecognition)
    {
        if (source.Empty())
            return source.Clone();

        if (source.Channels() == 1)
        {
            if (!normalizeRecognition)
                return source.Clone();

            var equalized = new Mat();
            Cv2.EqualizeHist(source, equalized);
            return equalized;
        }

        var grayscale = new Mat();
        var colorCode = source.Channels() switch
        {
            3 => ColorConversionCodes.BGR2GRAY,
            4 => ColorConversionCodes.BGRA2GRAY,
            _ => ColorConversionCodes.BGR2GRAY
        };
        Cv2.CvtColor(source, grayscale, colorCode);

        if (!normalizeRecognition)
            return grayscale;

        var normalized = new Mat();
        Cv2.EqualizeHist(grayscale, normalized);
        grayscale.Dispose();
        return normalized;
    }

    private void LoadTemplates()
    {
        if (!Directory.Exists(_resDirectory)) return;

        foreach (var file in Directory.GetFiles(_resDirectory, "*.png"))
        {
            string fname = Path.GetFileName(file);
            using var loaded = Cv2.ImRead(file, ImreadModes.Grayscale);
            if (loaded.Empty()) continue;

            var img = PreprocessForMatch(loaded, _normalizeRecognition);

            _allTemplates[fname] = img;

            if (fname.StartsWith("shape", StringComparison.OrdinalIgnoreCase) && fname.Length > 9)
            {
                string tag = fname.Substring(5, fname.Length - 9).ToUpperInvariant();
                _templates[tag] = img;

                if (OpticNames.Contains(tag))
                    _opticTemplates[tag] = img;
                else
                    _profileTemplates[tag] = img;
            }
        }

        if (_allTemplates.TryGetValue("empe.png", out var empe))
            _stanceTemplates[nameof(StanceType.Standard)] = empe;

        if (_allTemplates.TryGetValue("agachado.png", out var agachado))
            _stanceTemplates[nameof(StanceType.LowProfile)] = agachado;

        if (_allTemplates.TryGetValue("deitado.png", out var deitado))
            _stanceTemplates[nameof(StanceType.Prone)] = deitado;
    }

    public void StartCache(ScreenRegion boundingRegion)
    {
        var frame = FrameProvider(boundingRegion);
        lock (_cacheLock)
        {
            _cachedFrame?.Dispose();
            _cachedFrame = frame;
            if (frame != null)
            {
                _cacheX0 = boundingRegion.X;
                _cacheY0 = boundingRegion.Y;
            }
        }
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cachedFrame?.Dispose();
            _cachedFrame = null;
        }
    }

    /// <summary>
    /// Devolve um Mat que o chamador possui. Se a região está dentro do frame em cache a cópia sai dele
    /// (sob lock, pois outra thread pode limpar o cache); caso contrário captura direto da tela.
    /// </summary>
    private Mat? Grab(ScreenRegion region)
    {
        lock (_cacheLock)
        {
            if (_cachedFrame != null)
            {
                int fx = region.X - _cacheX0;
                int fy = region.Y - _cacheY0;
                if (fx >= 0 && fy >= 0 && fx + region.Width <= _cachedFrame.Width && fy + region.Height <= _cachedFrame.Height)
                {
                    using var view = new Mat(_cachedFrame, new Rect(fx, fy, region.Width, region.Height));
                    return view.Clone();
                }
            }
        }

        return FrameProvider(region);
    }

    public Mat? CaptureFrame(ScreenRegion region) => Grab(region);

    /// <summary>
    /// Pontua cada candidato contra o frame já pré-processado. Candidatos maiores que o frame são
    /// ignorados (e contados em <paramref name="skipped"/>) porque o MatchTemplate não consegue avaliá-los.
    /// </summary>
    private List<MatchCandidateScore> Rank(
        Mat preparedFrame,
        IEnumerable<KeyValuePair<string, Mat>> candidates,
        out int skipped)
    {
        var ranked = new List<MatchCandidateScore>();
        var buffer = _resultBuffer.Value!;
        skipped = 0;

        foreach (var (name, tmpl) in candidates)
        {
            if (preparedFrame.Height < tmpl.Height || preparedFrame.Width < tmpl.Width)
            {
                skipped++;
                continue;
            }

            Cv2.MatchTemplate(preparedFrame, tmpl, buffer, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(buffer, out _, out double maxVal, out _, out _);
            // CCoeffNormed devolve NaN em regiões/templates sem variância (cor constante).
            ranked.Add(new MatchCandidateScore(name, double.IsNaN(maxVal) ? 0.0 : maxVal));
        }

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        return ranked;
    }

    private static MatchDebugResult BuildResult(List<MatchCandidateScore> ranked, int skipped, double threshold, int topN)
    {
        if (ranked.Count == 0)
            return new MatchDebugResult(null, 0.0, Array.Empty<MatchCandidateScore>()) { SkippedCandidates = skipped };

        var top = ranked.Take(Math.Max(1, topN)).ToArray();
        var best = ranked[0];
        return new MatchDebugResult(best.Score >= threshold ? best.Name : null, best.Score, top)
        {
            SkippedCandidates = skipped,
            Threshold = threshold,
            Margin = ranked.Count > 1 ? best.Score - ranked[1].Score : best.Score
        };
    }

    public MatchDebugResult MatchDetailed(Mat? screenCrop, double threshold, IEnumerable<KeyValuePair<string, Mat>> candidates, int topN = 3)
    {
        if (screenCrop == null || screenCrop.Empty())
            return new MatchDebugResult(null, 0.0, Array.Empty<MatchCandidateScore>());

        using var preparedFrame = PreprocessForMatch(screenCrop, _normalizeRecognition);
        var ranked = Rank(preparedFrame, candidates, out int skipped);
        return BuildResult(ranked, skipped, threshold, topN);
    }

    public (string? BestName, double BestScore) Match(Mat? screenCrop, double threshold, Dictionary<string, Mat> candidates)
    {
        var debug = MatchDetailed(screenCrop, threshold, candidates, 1);
        return (debug.BestName, debug.BestScore);
    }

    public string? MatchProfile(ScreenRegion region, double threshold = 0.60)
    {
        using var frame = Grab(region);
        return MatchProfileFrame(frame, threshold).BestName;
    }

    public (string? BestName, double BestScore) MatchProfileFrame(Mat? screenCrop, double threshold = 0.60) =>
        Match(screenCrop, threshold, _profileTemplates);

    public MatchDebugResult MatchProfileDetailedFrame(Mat? screenCrop, double threshold = 0.60, int topN = 3) =>
        MatchDetailed(screenCrop, threshold, _profileTemplates, topN);

    public string? MatchOptic(ScreenRegion region, double threshold = 0.55)
    {
        using var frame = FrameProvider(region);
        return MatchOpticFrame(frame, threshold).BestName;
    }

    public (string? BestName, double BestScore) MatchOpticFrame(Mat? screenCrop, double threshold = 0.55) =>
        Match(screenCrop, threshold, _opticTemplates);

    public MatchDebugResult MatchOpticDetailedFrame(Mat? screenCrop, double threshold = 0.55, int topN = 3) =>
        MatchDetailed(screenCrop, threshold, _opticTemplates, topN);

    public StanceType? MatchStance(ScreenRegion region, double threshold = 0.55)
    {
        using var frame = FrameProvider(region);
        var result = MatchStanceFrame(frame, threshold);
        return Enum.TryParse<StanceType>(result.BestName, out var stance) ? stance : null;
    }

    public MatchDebugResult MatchStanceFrame(Mat? screenCrop, double threshold = 0.55, int topN = 3) =>
        MatchDetailed(screenCrop, threshold, _stanceTemplates, topN);

    public (string? BestName, double BestScore) MatchComponentWithScore(
        List<ScreenRegion> areas,
        List<string> candidateImages,
        double threshold = DefaultIconThreshold)
    {
        var debug = MatchComponentDetailed(areas, candidateImages, threshold, 1);
        return (debug.BestName, debug.BestScore);
    }

    public Dictionary<string, Mat> ResolveComponentCandidates(IEnumerable<string> candidateImages) =>
        candidateImages
            .Where(img => _allTemplates.ContainsKey(img))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(img => img, img => _allTemplates[img], StringComparer.OrdinalIgnoreCase);

    public MatchDebugResult MatchComponentDetailed(
        List<ScreenRegion> areas,
        List<string> candidateImages,
        double threshold = DefaultIconThreshold,
        int topN = 3)
    {
        var candidates = ResolveComponentCandidates(candidateImages);
        if (candidates.Count == 0)
            return new MatchDebugResult(null, 0.0, Array.Empty<MatchCandidateScore>());

        MatchDebugResult? best = null;

        foreach (var area in areas)
        {
            int pad = IconPadding;
            var expanded = new ScreenRegion(
                Math.Max(area.X - pad, 0),
                Math.Max(area.Y - pad, 0),
                area.Width + (2 * pad),
                area.Height + (2 * pad));

            using var frame = Grab(expanded);
            if (frame == null || frame.Empty()) continue;
            var match = MatchDetailed(frame, threshold, candidates, topN);
            if (best == null || match.BestScore > best.BestScore)
                best = match;
        }

        return best ?? new MatchDebugResult(null, 0.0, Array.Empty<MatchCandidateScore>());
    }

    public double? CalculateCenterDarkness()
    {
        int screenW = NativeMethods.GetSystemMetrics(0);
        int screenH = NativeMethods.GetSystemMetrics(1);
        if (screenW <= 0 || screenH <= 0) return null;

        // ADS_REGIAO: (0.458, 0.574, 0.542, 0.722)
        int x0 = (int)(0.458 * screenW);
        int y0 = (int)(0.574 * screenH);
        int x1 = (int)(0.542 * screenW);
        int y1 = (int)(0.722 * screenH);

        var region = new ScreenRegion(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        using var frame = ScreenCaptureService.CaptureRegion(region);
        if (frame == null || frame.Empty()) return null;

        using var darkMask = new Mat();
        Cv2.Compare(frame, 70, darkMask, CmpTypes.LT);
        int darkPixels = Cv2.CountNonZero(darkMask);
        int totalPixels = frame.Rows * frame.Cols;

        return totalPixels > 0 ? (double)darkPixels / totalPixels : null;
    }

    public void Dispose()
    {
        ClearCache();
        foreach (var mat in _allTemplates.Values)
            mat.Dispose();
        _allTemplates.Clear();
        _templates.Clear();
        _profileTemplates.Clear();
        _opticTemplates.Clear();
        _stanceTemplates.Clear();

        foreach (var buffer in _resultBuffer.Values)
            buffer.Dispose();
        _resultBuffer.Dispose();
    }
}
