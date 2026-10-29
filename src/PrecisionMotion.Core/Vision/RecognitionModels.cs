using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

public sealed record SlotAttachments(string? Optic, string? Component1, string? Component2);

/// <summary>Leitura de inventário já filtrada pela histerese.</summary>
public sealed record InventoryRecognitionResult(
    bool Visible,
    string? Profile1,
    string? Profile2,
    SlotAttachments Slot1,
    SlotAttachments Slot2,
    bool Ready)
{
    public static InventoryRecognitionResult Hidden { get; } =
        new(false, null, null, new SlotAttachments(null, null, null), new SlotAttachments(null, null, null), false);

    /// <summary>Mesmo conteúdo estável, independente de visibilidade/prontidão.</summary>
    public bool SameState(InventoryRecognitionResult? other) =>
        other != null && Profile1 == other.Profile1 && Profile2 == other.Profile2
        && Slot1 == other.Slot1 && Slot2 == other.Slot2;
}

public sealed record StanceRecognitionResult(
    StanceType? Stable,
    bool Changed,
    MatchDebugResult Raw);

/// <summary>Última observação de um tipo de reconhecimento (para diagnóstico na UI).</summary>
public sealed record RecognitionObservation(
    string Key,
    string Label,
    string? RawBest,
    double RawScore,
    double Threshold,
    string? StableValue,
    IReadOnlyList<MatchCandidateScore> TopCandidates,
    ScreenRegion? Region,
    DateTime Timestamp);

public sealed record RecognitionSnapshot(
    IReadOnlyList<RecognitionObservation> Observations,
    bool NormalizeRecognition,
    string DiagnosticsDirectory,
    int SavedCrops,
    DateTime CapturedAt)
{
    public static RecognitionSnapshot Empty { get; } =
        new(Array.Empty<RecognitionObservation>(), true, "", 0, DateTime.MinValue);
}
