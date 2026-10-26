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
    private readonly Dictionary<StanceType, Mat> _stanceTemplates = new();
    private readonly Dictionary<string, Mat> _allTemplates = new(StringComparer.OrdinalIgnoreCase);

    private Mat? _cachedFrame;
    private int _cacheX0;
    private int _cacheY0;

    public PatternMatcher(string baseDirectory, string resolution)
    {
        _resDirectory = Path.Combine(baseDirectory, "Configs", resolution);
        LoadTemplates();
    }

    private void LoadTemplates()
    {
        if (!Directory.Exists(_resDirectory)) return;

        foreach (var file in Directory.GetFiles(_resDirectory, "*.png"))
        {
            string fname = Path.GetFileName(file);
            var img = Cv2.ImRead(file, ImreadModes.Grayscale);
            if (img.Empty()) continue;

            _allTemplates[fname] = img;

            if (fname.StartsWith("shape", StringComparison.OrdinalIgnoreCase) && fname.Length > 9)
            {
                string tag = fname.Substring(5, fname.Length - 9).ToUpperInvariant();
                _templates[tag] = img;
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

    public (string? BestName, double BestScore) Match(Mat? screenCrop, double threshold, Dictionary<string, Mat> candidates)
    {
        if (screenCrop == null || screenCrop.Empty())
            return (null, 0.0);

        string? bestName = null;
        double bestScore = -1.0;

        foreach (var (name, tmpl) in candidates)
        {
            if (screenCrop.Height < tmpl.Height || screenCrop.Width < tmpl.Width)
                continue;

            using var result = new Mat();
            Cv2.MatchTemplate(screenCrop, tmpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

            if (maxVal > bestScore && maxVal >= threshold)
            {
                bestScore = maxVal;
                bestName = name;
            }
        }

        return (bestName, bestScore >= 0 ? bestScore : 0.0);
    }

    public string? MatchProfile(ScreenRegion region, double threshold = 0.60)
    {
        using var frame = Grab(region);
        var profileTemplates = _templates
            .Where(kv => !OpticNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        return Match(frame, threshold, profileTemplates).BestName;
    }

    public string? MatchOptic(ScreenRegion region, double threshold = 0.55)
    {
        using var frame = ScreenCaptureService.CaptureRegion(region);
        var opticTemplates = _templates
            .Where(kv => OpticNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        return Match(frame, threshold, opticTemplates).BestName;
    }

    public StanceType? MatchStance(ScreenRegion region, double threshold = 0.55)
    {
        using var frame = ScreenCaptureService.CaptureRegion(region);
        if (frame == null || frame.Empty()) return null;

        StanceType? bestStance = null;
        double bestVal = threshold;

        foreach (var (stance, tmpl) in _stanceTemplates)
        {
            if (frame.Height < tmpl.Height || frame.Width < tmpl.Width)
                continue;

            using var result = new Mat();
            Cv2.MatchTemplate(frame, tmpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

            if (maxVal > bestVal)
            {
                bestVal = maxVal;
                bestStance = stance;
            }
        }

        return bestStance;
    }

    public (string? BestName, double BestScore) MatchComponentWithScore(
        List<ScreenRegion> areas,
        List<string> candidateImages,
        double threshold = DefaultIconThreshold)
    {
        var candidates = candidateImages
            .Where(img => _allTemplates.ContainsKey(img))
            .ToDictionary(img => img, img => _allTemplates[img], StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0) return (null, 0.0);

        string? bestName = null;
        double bestScore = threshold;

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

            foreach (var (name, tmpl) in candidates)
            {
                if (frame.Height < tmpl.Height || frame.Width < tmpl.Width)
                    continue;

                using var result = new Mat();
                Cv2.MatchTemplate(frame, tmpl, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

                if (maxVal > bestScore)
                {
                    bestScore = maxVal;
                    bestName = name;
                }
            }
        }

        return (bestName, bestName != null ? bestScore : 0.0);
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
