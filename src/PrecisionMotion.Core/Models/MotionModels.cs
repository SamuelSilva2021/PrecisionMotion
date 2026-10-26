using System.Text.Json.Serialization;

namespace PrecisionMotion.Core.Models;

public enum StanceType
{
    Standard,   // Em pé
    LowProfile, // Agachado
    Prone       // Deitado
}

public readonly record struct ScreenRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static ScreenRegion FromCorners(int x1, int y1, int x2, int y2)
    {
        return new ScreenRegion(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }
}

public class MotionProfileItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("pattern")]
    public List<double> Pattern { get; set; } = new();

    [JsonPropertyName("multiplicador_individual")]
    public double IndividualMultiplier { get; set; } = 1.0;

    [JsonPropertyName("multiplicadordeitado")]
    public double ProneMultiplier { get; set; } = 0.6;

    public ScreenRegion? CheckArea1 { get; set; }
    public ScreenRegion? CheckArea2 { get; set; }
}

public class ProfileOverride
{
    public string Name { get; set; } = string.Empty;
    public double IndividualMultiplier { get; set; } = 1.0;
    public double StandardOffset { get; set; } = -0.02;
    public double LowProfileOffset { get; set; } = -0.15;
}

public class ComponentItem
{
    public string CheckImage { get; set; } = string.Empty;
    public ScreenRegion? CheckArea1 { get; set; }
    public ScreenRegion? CheckArea2 { get; set; }
    public Dictionary<string, double> PatternValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class GlobalConfigData
{
    public string ActiveResolution { get; set; } = "1920x1080";
    public string AimMode { get; set; } = "segurar"; // "segurar" or "alternar"
    public double VisualDetectionThreshold { get; set; } = 0.75;
    public bool SoundsEnabled { get; set; } = true;
    public int MasterCalibration { get; set; } = 100;
    public bool SecondarySlotEnabled { get; set; } = false;
    public bool DmrsEnabled { get; set; } = false;
    public bool InvertSlots { get; set; } = false;
    public ScreenRegion ScopeCoords { get; set; } = new(710, 990, 23, 34);
    public ScreenRegion PostureCoords { get; set; } = new(0, 0, 30, 50);
}

public class MotionSessionState
{
    [JsonPropertyName("active_slot")]
    public int ActiveSlot { get; set; } = 1;

    [JsonPropertyName("slot1_weapon")]
    public string Slot1Profile { get; set; } = "M4";

    [JsonPropertyName("slot2_weapon")]
    public string Slot2Profile { get; set; } = "UNASSIGNED";

    [JsonPropertyName("slot1_scope")]
    public string Slot1Optic { get; set; } = "";

    [JsonPropertyName("slot2_scope")]
    public string Slot2Optic { get; set; } = "";

    [JsonPropertyName("slot1_atach1")]
    public string? Slot1Component1 { get; set; }

    [JsonPropertyName("slot2_atach1")]
    public string? Slot2Component1 { get; set; }

    [JsonPropertyName("slot1_atach2")]
    public string? Slot1Component2 { get; set; }

    [JsonPropertyName("slot2_atach2")]
    public string? Slot2Component2 { get; set; }

    [JsonPropertyName("ganho_recuo")]
    public double DynamicGain { get; set; } = 120.0;

    [JsonPropertyName("salvo_em")]
    public double SavedTimestamp { get; set; }
}

public readonly record struct CalculationResult(
    double BaseRate,
    double ProfileFactor,
    double StanceFactor,
    double OpticFactor,
    string OpticImage,
    double Component1Factor,
    string Component1Image,
    double Component2Factor,
    string Component2Image,
    double DynamicGain,
    double VelocityPxPerSec
);
