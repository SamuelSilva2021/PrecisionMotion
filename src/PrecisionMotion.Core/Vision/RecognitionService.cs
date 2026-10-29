using System.Collections.Concurrent;
using OpenCvSharp;
using PrecisionMotion.Core.Configuration;
using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

public interface IRecognitionService : IDisposable
{
    Action<string>? OnLog { get; set; }

    /// <summary>Zera a histerese do inventário; chamar ao abrir uma nova sessão de inspeção.</summary>
    void BeginInventorySession();

    /// <summary>Uma leitura do inventário (perfis a cada chamada, anexos conforme a cadência configurada).</summary>
    InventoryRecognitionResult PollInventory();

    void SeedStance(StanceType stance);
    StanceRecognitionResult PollStance();

    RecognitionSnapshot GetSnapshot();
    void FlushTelemetry();
}

public sealed class RecognitionService : IRecognitionService
{
    private readonly ConfigProvider _config;
    private readonly PatternMatcher _matcher;
    private readonly RecognitionDiagnostics _diagnostics;
    private readonly RecognitionTelemetry _telemetry;
    private readonly ConcurrentDictionary<string, RecognitionObservation> _observations = new(StringComparer.OrdinalIgnoreCase);

    private readonly HysteresisTracker _profile1;
    private readonly HysteresisTracker _profile2;
    private readonly HysteresisTracker _stance;
    private readonly Dictionary<(int Slot, string Kind), HysteresisTracker> _attachments = new();
    private int _pollIndex;

    public RecognitionService(
        ConfigProvider config,
        PatternMatcher matcher,
        RecognitionDiagnostics diagnostics,
        RecognitionTelemetry telemetry)
    {
        _config = config;
        _matcher = matcher;
        _diagnostics = diagnostics;
        _telemetry = telemetry;

        var g = config.GlobalConfig;
        _profile1 = NewTracker();
        _profile2 = NewTracker();
        _stance = new HysteresisTracker(g.StableConfirmations, g.ExitThresholdMargin, g.AmbiguityMargin, nameof(StanceType.Standard));
        foreach (int slot in new[] { 1, 2 })
            foreach (var kind in new[] { "optic", "component1", "component2" })
                _attachments[(slot, kind)] = NewTracker();
    }

    public Action<string>? OnLog { get; set; }

    private HysteresisTracker NewTracker()
    {
        var g = _config.GlobalConfig;
        return new HysteresisTracker(g.StableConfirmations, g.ExitThresholdMargin, g.AmbiguityMargin);
    }

    public void BeginInventorySession()
    {
        _profile1.Reset();
        _profile2.Reset();
        foreach (var tracker in _attachments.Values)
            tracker.Reset();
        _pollIndex = 0;
    }

    public void SeedStance(StanceType stance) => _stance.Reset(stance.ToString());

    public InventoryRecognitionResult PollInventory()
    {
        var bounding = ComputeInventoryBounds();
        if (bounding == null)
            return InventoryRecognitionResult.Hidden;

        var g = _config.GlobalConfig;
        _matcher.StartCache(bounding.Value);
        try
        {
            var raw1 = ReadProfile(1, _config.Slot1Area);
            var raw2 = ReadProfile(2, _config.Slot2Area);

            if (raw1?.BestName == null && raw2?.BestName == null)
                return InventoryRecognitionResult.Hidden;

            bool readAttachments = _pollIndex % Math.Max(1, g.AttachmentCadence) == 0;
            _pollIndex++;

            if (_config.Slot1Area.HasValue) _profile1.Observe(raw1!, g.ProfileMatchThreshold);
            if (_config.Slot2Area.HasValue) _profile2.Observe(raw2!, g.ProfileMatchThreshold);

            if (readAttachments)
            {
                if (raw1?.BestName != null) ReadAttachments(1);
                if (raw2?.BestName != null) ReadAttachments(2);
            }

            var slot1 = StableAttachments(1);
            var slot2 = StableAttachments(2);
            bool ready = (_profile1.IsSettled || _profile2.IsSettled)
                && SlotSettled(1, _profile1) && SlotSettled(2, _profile2);

            PublishStable("profile_slot1", _profile1.Current);
            PublishStable("profile_slot2", _profile2.Current);

            return new InventoryRecognitionResult(true, _profile1.Current, _profile2.Current, slot1, slot2, ready);
        }
        finally
        {
            _matcher.ClearCache();
        }
    }

    public StanceRecognitionResult PollStance()
    {
        var g = _config.GlobalConfig;
        var region = g.PostureCoords;
        using var frame = _matcher.CaptureFrame(region);
        var raw = _matcher.MatchStanceFrame(frame, g.StanceMatchThreshold, g.RecognitionDebugTopN);
        var outcome = _stance.Observe(raw, g.StanceMatchThreshold);

        Publish("stance", "Postura", raw, g.StanceMatchThreshold, region, _stance.Current, frame);

        StanceType? stable = Enum.TryParse<StanceType>(_stance.Current, out var parsed) ? parsed : null;
        return new StanceRecognitionResult(stable, outcome.Changed, raw, outcome.State == HysteresisState.Pending);
    }

    public RecognitionSnapshot GetSnapshot() => new(
        _observations.Values.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
        _matcher.NormalizeRecognition,
        _diagnostics.OutputDirectory,
        _telemetry.FailureCropsSaved,
        DateTime.Now,
        ScreenCaptureService.CaptureCount,
        ScreenCaptureService.MeanCaptureMs);

    public void FlushTelemetry() => _telemetry.Flush();

    private MatchDebugResult? ReadProfile(int slot, ScreenRegion? area)
    {
        if (!area.HasValue)
            return null;

        var g = _config.GlobalConfig;
        using var frame = _matcher.CaptureFrame(area.Value);
        var result = _matcher.MatchProfileDetailedFrame(frame, g.ProfileMatchThreshold, g.RecognitionDebugTopN);
        Publish($"profile_slot{slot}", $"Perfil slot {slot}", result, g.ProfileMatchThreshold, area, null, frame);
        return result;
    }

    private void ReadAttachments(int slot)
    {
        var g = _config.GlobalConfig;
        ReadAttachment(slot, "optic", $"Escopo slot {slot}", _config.OpticCandidateAreas, _config.OpticItems, g.OpticMatchThreshold);
        ReadAttachment(slot, "component1", $"Componente 1 slot {slot}", _config.Comp1CandidateAreas, _config.ComponentGroup1, g.ComponentMatchThreshold);
        ReadAttachment(slot, "component2", $"Componente 2 slot {slot}", _config.Comp2CandidateAreas, _config.ComponentGroup2, g.ComponentMatchThreshold);
    }

    private void ReadAttachment(
        int slot,
        string kind,
        string label,
        Dictionary<int, List<ScreenRegion>> areasBySlot,
        List<ComponentItem> items,
        double threshold)
    {
        if (!areasBySlot.TryGetValue(slot, out var areas) || areas.Count == 0)
            return;

        var images = items.Select(i => i.CheckImage).Where(s => !string.IsNullOrEmpty(s)).ToList();
        var result = _matcher.MatchComponentDetailed(areas, images, threshold, _config.GlobalConfig.RecognitionDebugTopN);
        var tracker = _attachments[(slot, kind)];
        tracker.Observe(result, threshold, allowNone: true);
        Publish($"{kind}_slot{slot}", label, result, threshold, Bounds(areas), tracker.Current, null);
    }

    private SlotAttachments StableAttachments(int slot)
    {
        static string? Value(HysteresisTracker t) => string.IsNullOrEmpty(t.Current) ? null : t.Current;
        return new SlotAttachments(
            Value(_attachments[(slot, "optic")]),
            Value(_attachments[(slot, "component1")]),
            Value(_attachments[(slot, "component2")]));
    }

    /// <summary>Um slot sem arma estável não bloqueia; com arma, cada categoria configurada precisa ter estabilizado.</summary>
    private bool SlotSettled(int slot, HysteresisTracker profile)
    {
        if (!profile.IsSettled)
            return true;

        return SettledOrUnconfigured(slot, "optic", _config.OpticCandidateAreas)
            && SettledOrUnconfigured(slot, "component1", _config.Comp1CandidateAreas)
            && SettledOrUnconfigured(slot, "component2", _config.Comp2CandidateAreas);
    }

    private bool SettledOrUnconfigured(int slot, string kind, Dictionary<int, List<ScreenRegion>> areas) =>
        !areas.TryGetValue(slot, out var list) || list.Count == 0 || _attachments[(slot, kind)].IsSettled;

    private void PublishStable(string key, string? stable)
    {
        if (_observations.TryGetValue(key, out var obs))
            _observations[key] = obs with { StableValue = stable };
    }

    private void Publish(
        string key,
        string label,
        MatchDebugResult result,
        double threshold,
        ScreenRegion? region,
        string? stable,
        Mat? frame)
    {
        _observations[key] = new RecognitionObservation(
            key, label, result.TopName, result.BestScore, threshold, stable,
            result.RankedCandidates, region, DateTime.Now);

        _telemetry.Record(key, result, frame);

        if (_diagnostics.ShouldEmit(key))
        {
            OnLog?.Invoke($"{DateTime.Now:HH:mm:ss} [RecognitionDebug] {label} -> best={result.BestName ?? "nenhum"} score={result.BestScore:F3} top=[{_diagnostics.FormatCandidates(result)}]");
            _diagnostics.SaveCrop(label, frame);
        }
    }

    private ScreenRegion? ComputeInventoryBounds()
    {
        var areas = new List<ScreenRegion>();
        if (_config.Slot1Area.HasValue) areas.Add(_config.Slot1Area.Value);
        if (_config.Slot2Area.HasValue) areas.Add(_config.Slot2Area.Value);
        foreach (var list in _config.OpticCandidateAreas.Values) areas.AddRange(list);
        foreach (var list in _config.Comp1CandidateAreas.Values) areas.AddRange(list);
        foreach (var list in _config.Comp2CandidateAreas.Values) areas.AddRange(list);

        if (areas.Count == 0)
            return null;

        int pad = PatternMatcher.IconPadding;
        int minX = Math.Max(areas.Min(a => a.X) - pad, 0);
        int minY = Math.Max(areas.Min(a => a.Y) - pad, 0);
        int maxX = areas.Max(a => a.X + a.Width) + pad;
        int maxY = areas.Max(a => a.Y + a.Height) + pad;
        return new ScreenRegion(minX, minY, maxX - minX, maxY - minY);
    }

    private static ScreenRegion Bounds(List<ScreenRegion> areas)
    {
        int minX = areas.Min(a => a.X);
        int minY = areas.Min(a => a.Y);
        return new ScreenRegion(minX, minY, areas.Max(a => a.X + a.Width) - minX, areas.Max(a => a.Y + a.Height) - minY);
    }

    public void Dispose() => _telemetry.Dispose();
}
