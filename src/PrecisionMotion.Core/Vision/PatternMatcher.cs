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
    private readonly Dictionary<StanceType, Mat> _stanceTemplates = new();
    private readonly Dictionary<string, Mat> _allTemplates = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _normalizeRecognition;

    private Mat? _cachedFrame;
    private int _cacheX0;
    private int _cacheY0;

    public PatternMatcher(string baseDirectory, string resolution, bool normalizeRecognition = true)
    {
        _resDirectory = Path.Combine(baseDirectory, "Configs", resolution);
        _normalizeRecognition = normalizeRecognition;
        LoadTemplates();
    }

    public static Mat PreprocessForMatch(Mat source, bool normalizeRecognition)
    {
        if (source.Empty())
            return source.Clone();

        Mat grayscale;
        if (source.Channels() == 1)
        {
            grayscale = source.Clone();
        }
        else
        {
            grayscale = new Mat();
            var colorCode = source.Channels() switch
            {
                3 => ColorConversionCodes.BGR2GRAY,
                4 => ColorConversionCodes.BGRA2GRAY,
                _ => ColorConversionCodes.BGR2GRAY
            };
            Cv2.CvtColor(source, grayscale, colorCode);
        }

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
            _stanceTemplates[StanceType.Standard] = empe;

        if (_allTemplates.TryGetValue("agachado.png", out var agachado))
            _stanceTemplates[StanceType.LowProfile] = agachado;

        if (_allTemplates.TryGetValue("deitado.png", out var deitado))
            _stanceTemplates[StanceType.Prone] = deitado;
    }

    public void StartCache(ScreenRegion boundingRegion)
    {
        ClearCache();
        _cachedFrame = ScreenCaptureService.CaptureRegion(boundingRegion);
        if (_cachedFrame != null)
        {
            _cacheX0 = boundingRegion.X;
            _cacheY0 = boundingRegion.Y;
        }
    }

    public void ClearCache()
    {
        _cachedFrame?.Dispose();
        _cachedFrame = null;
    }

    private Mat? Grab(ScreenRegion region)
    {
        if (_cachedFrame != null)
        {
            int fx = region.X - _cacheX0;
            int fy = region.Y - _cacheY0;
            if (fx >= 0 && fy >= 0 && fx + region.Width <= _cachedFrame.Width && fy + region.Height <= _cachedFrame.Height)
            {
                var rect = new Rect(fx, fy, region.Width, region.Height);
                return new Mat(_cachedFrame, rect);
            }
        }

        return ScreenCaptureService.CaptureRegion(region);
    }

    public Mat? CaptureFrame(ScreenRegion region)
    {
        using var frame = Grab(region);
        return frame?.Clone();
    }

    public MatchDebugResult MatchDetailed(Mat? screenCrop, double threshold, Dictionary<string, Mat> candidates, int topN = 3)
    {
        if (screenCrop == null || screenCrop.Empty())
            return new MatchDebugResult(null, 0.0, Array.Empty<MatchCandidateScore>());

        using var preparedFrame = PreprocessForMatch(screenCrop, _normalizeRecognition);
        var ranked = new List<MatchCandidateScore>(candidates.Count);

        foreach (var (name, tmpl) in candidates)
        {
            if (preparedFrame.Height < tmpl.Height || preparedFrame.Width < tmpl.Width)
                continue;

            using var result = new Mat();
            Cv2.MatchTemplate(preparedFrame, tmpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ranked.Add(new MatchCandidateScore(name, maxVal));
        }

        if (ranked.Count == 0)
            return new MatchDebugResult(null, 0.0, Array.Empty<MatchCandidateScore>());

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = ranked.Take(Math.Max(1, topN)).ToArray();
        var best = ranked[0];
        return best.Score >= threshold
            ? new MatchDebugResult(best.Name, best.Score, top)
            : new MatchDebugResult(null, best.Score, top);
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
        using var frame = ScreenCaptureService.CaptureRegion(region);
        return MatchOpticFrame(frame, threshold).BestName;
    }

    public (string? BestName, double BestScore) MatchOpticFrame(Mat? screenCrop, double threshold = 0.55) =>
        Match(screenCrop, threshold, _opticTemplates);

    public MatchDebugResult MatchOpticDetailedFrame(Mat? screenCrop, double threshold = 0.55, int topN = 3) =>
        MatchDetailed(screenCrop, threshold, _opticTemplates, topN);

    public StanceType? MatchStance(ScreenRegion region, double threshold = 0.55)
    {
        using var frame = ScreenCaptureService.CaptureRegion(region);
        var result = MatchStanceFrame(frame, threshold);
        return Enum.TryParse<StanceType>(result.BestName, out var stance) ? stance : null;
    }

    public MatchDebugResult MatchStanceFrame(Mat? screenCrop, double threshold = 0.55, int topN = 3)
    {
        if (screenCrop == null || screenCrop.Empty())
            return new MatchDebugResult(null, 0.0, Array.Empty<MatchCandidateScore>());

        using var preparedFrame = PreprocessForMatch(screenCrop, _normalizeRecognition);
        var ranked = new List<MatchCandidateScore>(_stanceTemplates.Count);

        foreach (var (stance, tmpl) in _stanceTemplates)
        {
            if (preparedFrame.Height < tmpl.Height || preparedFrame.Width < tmpl.Width)
                continue;

            using var result = new Mat();
            Cv2.MatchTemplate(preparedFrame, tmpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
            ranked.Add(new MatchCandidateScore(stance.ToString(), maxVal));
        }

        if (ranked.Count == 0)
            return new MatchDebugResult(null, 0.0, Array.Empty<MatchCandidateScore>());

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = ranked.Take(Math.Max(1, topN)).ToArray();
        var best = ranked[0];
        return best.Score >= threshold
            ? new MatchDebugResult(best.Name, best.Score, top)
            : new MatchDebugResult(null, best.Score, top);
    }

    public (string? BestName, double BestScore) MatchComponentWithScore(
        List<ScreenRegion> areas,
        List<string> candidateImages,
        double threshold = DefaultIconThreshold)
    {
        var debug = MatchComponentDetailed(areas, candidateImages, threshold, 1);
        return (debug.BestName, debug.BestScore);
    }

    public MatchDebugResult MatchComponentDetailed(
        List<ScreenRegion> areas,
        List<string> candidateImages,
        double threshold = DefaultIconThreshold,
        int topN = 3)
    {
        var candidates = candidateImages
            .Where(img => _allTemplates.ContainsKey(img))
            .ToDictionary(img => img, img => _allTemplates[img], StringComparer.OrdinalIgnoreCase);

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
        _stanceTemplates.Clear();
    }
}
