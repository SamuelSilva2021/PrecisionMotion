using System.Text.Json;
using System.Text.Json.Nodes;
using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Configuration;

public class ConfigProvider
{
    private static readonly string[] ParamFiles = { "parameter1.json", "parameter2.json", "parameter3.json", "parameter4.json" };
    private static readonly string[] AvailableResolutions = {
        "3440x1440", "2560x1440", "2560x1080", "1920x1080",
        "1728x1080", "1440x1080", "1600x900", "1366x768"
    };

    public string BaseDirectory { get; }
    public GlobalConfigData GlobalConfig { get; private set; } = new();
    public Dictionary<string, MotionProfileItem> Profiles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProfileOverride> ProfileOverrides { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ComponentItem> OpticItems { get; private set; } = new();
    public List<ComponentItem> ComponentGroup1 { get; private set; } = new();
    public List<ComponentItem> ComponentGroup2 { get; private set; } = new();

    public ScreenRegion? Slot1Area { get; private set; }
    public ScreenRegion? Slot2Area { get; private set; }

    public Dictionary<int, List<ScreenRegion>> OpticCandidateAreas { get; } = new() { { 1, new() }, { 2, new() } };
    public Dictionary<int, List<ScreenRegion>> Comp1CandidateAreas { get; } = new() { { 1, new() }, { 2, new() } };
    public Dictionary<int, List<ScreenRegion>> Comp2CandidateAreas { get; } = new() { { 1, new() }, { 2, new() } };

    public ConfigProvider(string? baseDir = null)
    {
        BaseDirectory = ResolveBaseDirectory(baseDir);
        ReloadAll();
    }

    private static double ClampThreshold(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return fallback;

        return Math.Clamp(value, 0.0, 1.0);
    }

    private static int ClampPositiveInt(int value, int fallback, int min, int max)
    {
        if (value < min || value > max)
            return fallback;

        return value;
    }

    public static string ResolveBaseDirectory(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && Directory.Exists(Path.Combine(preferred, "Configs")))
            return Path.GetFullPath(preferred);

        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            string dataDir = Path.Combine(current, "data");
            if (Directory.Exists(Path.Combine(dataDir, "Configs")))
                return Path.GetFullPath(dataDir);

            if (Directory.Exists(Path.Combine(current, "Configs")))
                return Path.GetFullPath(current);

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent)) break;
            current = parent;
        }

        string curDir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 10; i++)
        {
            string dataDir = Path.Combine(curDir, "data");
            if (Directory.Exists(Path.Combine(dataDir, "Configs")))
                return Path.GetFullPath(dataDir);

            if (Directory.Exists(Path.Combine(curDir, "Configs")))
                return Path.GetFullPath(curDir);

            string? parent = Directory.GetParent(curDir)?.FullName;
            if (string.IsNullOrEmpty(parent)) break;
            curDir = parent;
        }

        return AppContext.BaseDirectory;
    }

    public void ReloadAll()
    {
        LoadGlobalConfig();
        LoadCalibrationParameters();
        LoadStanceCoordinates();
        LoadResolutionComponents();
    }

    private void LoadGlobalConfig()
    {
        GlobalConfig = new GlobalConfigData();
        string cfgPath = Path.Combine(BaseDirectory, "Configs", "geral", "config.json");
        if (!File.Exists(cfgPath)) return;

        try
        {
            string json = File.ReadAllText(cfgPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("configs", out var configsArray) && configsArray.GetArrayLength() > 0)
            {
                var cfg = configsArray[0];

                foreach (var res in AvailableResolutions)
                {
                    if (cfg.TryGetProperty($"res{res}", out var resProp) && resProp.GetBoolean())
                    {
                        GlobalConfig.ActiveResolution = res;
                        break;
                    }
                }

                if (cfg.TryGetProperty($"scoord{GlobalConfig.ActiveResolution}", out var scoordProp) &&
                    scoordProp.ValueKind == JsonValueKind.Array && scoordProp.GetArrayLength() == 2)
                {
                    int x = scoordProp[0][0].GetInt32();
                    int y = scoordProp[0][1].GetInt32();
                    int w = scoordProp[1][0].GetInt32();
                    int h = scoordProp[1][1].GetInt32();
                    GlobalConfig.ScopeCoords = new ScreenRegion(x, y, w, h);
                }

                if (cfg.TryGetProperty("modo_mira", out var modoProp))
                    GlobalConfig.AimMode = modoProp.GetString()?.ToLowerInvariant() ?? "segurar";

                if (cfg.TryGetProperty("limiar_mira_visual", out var limiarProp))
                    GlobalConfig.VisualDetectionThreshold = ClampThreshold(limiarProp.GetDouble(), GlobalConfig.VisualDetectionThreshold);

                if (cfg.TryGetProperty("limiar_match_perfil", out var limiarPerfilProp))
                    GlobalConfig.ProfileMatchThreshold = ClampThreshold(limiarPerfilProp.GetDouble(), GlobalConfig.ProfileMatchThreshold);

                if (cfg.TryGetProperty("limiar_match_escopo", out var limiarEscopoProp))
                    GlobalConfig.OpticMatchThreshold = ClampThreshold(limiarEscopoProp.GetDouble(), GlobalConfig.OpticMatchThreshold);

                if (cfg.TryGetProperty("limiar_match_postura", out var limiarPosturaProp))
                    GlobalConfig.StanceMatchThreshold = ClampThreshold(limiarPosturaProp.GetDouble(), GlobalConfig.StanceMatchThreshold);

                if (cfg.TryGetProperty("limiar_match_componente", out var limiarComponenteProp))
                    GlobalConfig.ComponentMatchThreshold = ClampThreshold(limiarComponenteProp.GetDouble(), GlobalConfig.ComponentMatchThreshold);

                if (cfg.TryGetProperty("normalizar_reconhecimento", out var normalizarProp))
                    GlobalConfig.NormalizeRecognition = normalizarProp.GetBoolean();

                if (cfg.TryGetProperty("debug_reconhecimento", out var debugProp))
                    GlobalConfig.RecognitionDebugEnabled = debugProp.GetBoolean();

                if (cfg.TryGetProperty("debug_salvar_crops", out var saveCropsProp))
                    GlobalConfig.RecognitionDebugSaveCrops = saveCropsProp.GetBoolean();

                if (cfg.TryGetProperty("debug_top_n", out var topNProp))
                    GlobalConfig.RecognitionDebugTopN = ClampPositiveInt(topNProp.GetInt32(), GlobalConfig.RecognitionDebugTopN, 1, 10);

                if (cfg.TryGetProperty("debug_intervalo_ms", out var debugIntervalProp))
                    GlobalConfig.RecognitionDebugIntervalMs = ClampPositiveInt(debugIntervalProp.GetInt32(), GlobalConfig.RecognitionDebugIntervalMs, 100, 60000);

                if (cfg.TryGetProperty("debug_diretorio", out var debugDirProp))
                    GlobalConfig.RecognitionDebugDirectory = debugDirProp.GetString() ?? GlobalConfig.RecognitionDebugDirectory;

                if (cfg.TryGetProperty("sons", out var sonsProp))
                    GlobalConfig.SoundsEnabled = sonsProp.GetBoolean();

                if (cfg.TryGetProperty("calibragem", out var calProp))
                    GlobalConfig.MasterCalibration = calProp.GetInt32();

                if (cfg.TryGetProperty("slot2", out var s2Prop))
                    GlobalConfig.SecondarySlotEnabled = s2Prop.GetBoolean();

                if (cfg.TryGetProperty("dmrs", out var dmrProp))
                    GlobalConfig.DmrsEnabled = dmrProp.GetBoolean();

                if (cfg.TryGetProperty("invertslots", out var invProp))
                    GlobalConfig.InvertSlots = invProp.GetBoolean();
            }
        }
        catch { }
    }

    private void LoadCalibrationParameters()
    {
        Profiles.Clear();
        var seenAreas = new List<ScreenRegion>();

        foreach (var fname in ParamFiles)
        {
            string path = Path.Combine(BaseDirectory, "Configs", "calibragem", fname);
            if (!File.Exists(path)) continue;

            try
            {
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("weapons", out var wpArray)) continue;

                foreach (var wp in wpArray.EnumerateArray())
                {
                    string name = wp.GetProperty("name").GetString() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;

                    var item = new MotionProfileItem
                    {
                        Name = name,
                        IndividualMultiplier = wp.TryGetProperty("multiplicador_individual", out var mi) ? mi.GetDouble() : 1.0,
                        ProneMultiplier = wp.TryGetProperty("multiplicadordeitado", out var md) ? md.GetDouble() : 0.6
                    };

                    if (wp.TryGetProperty("pattern", out var patArray))
                    {
                        foreach (var val in patArray.EnumerateArray())
                            item.Pattern.Add(val.GetDouble());
                    }

                    string checkField1 = $"check_area_{GlobalConfig.ActiveResolution}";
                    if (wp.TryGetProperty(checkField1, out var ca1) && ca1.GetArrayLength() == 4)
                    {
                        var reg = ScreenRegion.FromCorners(ca1[0].GetInt32(), ca1[1].GetInt32(), ca1[2].GetInt32(), ca1[3].GetInt32());
                        item.CheckArea1 = reg;
                        if (!seenAreas.Contains(reg)) seenAreas.Add(reg);
                    }

                    string checkField2 = $"check_area2_{GlobalConfig.ActiveResolution}";
                    if (wp.TryGetProperty(checkField2, out var ca2) && ca2.GetArrayLength() == 4)
                    {
                        var reg = ScreenRegion.FromCorners(ca2[0].GetInt32(), ca2[1].GetInt32(), ca2[2].GetInt32(), ca2[3].GetInt32());
                        item.CheckArea2 = reg;
                        if (!seenAreas.Contains(reg)) seenAreas.Add(reg);
                    }

                    Profiles[name.ToUpperInvariant()] = item;
                }
            }
            catch { }
        }

        Slot1Area = seenAreas.Count >= 1 ? seenAreas[0] : null;
        Slot2Area = seenAreas.Count >= 2 ? seenAreas[1] : null;
    }

    private void LoadStanceCoordinates()
    {
        string cfgPath = Path.Combine(BaseDirectory, "Configs", GlobalConfig.ActiveResolution, "coord.cfg");
        if (!File.Exists(cfgPath)) return;

        try
        {
            var lines = File.ReadAllLines(cfgPath)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();

            var vals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Count - 1; i += 2)
            {
                string key = lines[i].TrimEnd(':');
                if (int.TryParse(lines[i + 1], out int val))
                    vals[key] = val;
            }

            int x = vals.GetValueOrDefault("coordempex", 0);
            int y = vals.GetValueOrDefault("coordempey", 0);
            int w = vals.GetValueOrDefault("tamanhoempex", 30);
            int h = vals.GetValueOrDefault("tamanhoempey", 50);

            GlobalConfig.PostureCoords = new ScreenRegion(x, y, w, h);
        }
        catch { }
    }

    private void LoadResolutionComponents()
    {
        ProfileOverrides.Clear();
        OpticItems.Clear();
        ComponentGroup1.Clear();
        ComponentGroup2.Clear();
        OpticCandidateAreas[1].Clear();
        OpticCandidateAreas[2].Clear();
        Comp1CandidateAreas[1].Clear();
        Comp1CandidateAreas[2].Clear();
        Comp2CandidateAreas[1].Clear();
        Comp2CandidateAreas[2].Clear();

        string path = Path.Combine(BaseDirectory, "Configs", GlobalConfig.ActiveResolution, "parameter1.json");
        if (!File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("weapons", out var wArray))
            {
                foreach (var w in wArray.EnumerateArray())
                {
                    string name = w.GetProperty("name").GetString() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    var ov = new ProfileOverride
                    {
                        Name = name,
                        IndividualMultiplier = w.TryGetProperty("multiplicador_individual", out var mi) ? mi.GetDouble() : 1.0,
                        StandardOffset = w.TryGetProperty("multiplicadorempe", out var me) ? me.GetDouble() : -0.02,
                        LowProfileOffset = w.TryGetProperty("multiplicadoragachado", out var ma) ? ma.GetDouble() : -0.15
                    };
                    ProfileOverrides.TryAdd(name.ToUpperInvariant(), ov);
                }
            }

            ParseComponentList(root, "scopes", OpticItems, OpticCandidateAreas);
            ParseComponentList(root, "atachs1", ComponentGroup1, Comp1CandidateAreas);
            ParseComponentList(root, "atachs2", ComponentGroup2, Comp2CandidateAreas);
        }
        catch { }
    }

    private static void ParseComponentList(
        JsonElement root,
        string propertyName,
        List<ComponentItem> targetList,
        Dictionary<int, List<ScreenRegion>> candidateAreas)
    {
        if (!root.TryGetProperty(propertyName, out var array)) return;

        foreach (var elem in array.EnumerateArray())
        {
            var item = new ComponentItem
            {
                CheckImage = elem.TryGetProperty("check_image", out var ci) ? ci.GetString() ?? "" : ""
            };

            if (elem.TryGetProperty("check_area", out var ca1) && ca1.GetArrayLength() == 4)
            {
                var reg = ScreenRegion.FromCorners(ca1[0].GetInt32(), ca1[1].GetInt32(), ca1[2].GetInt32(), ca1[3].GetInt32());
                item.CheckArea1 = reg;
                if (!candidateAreas[1].Contains(reg)) candidateAreas[1].Add(reg);
            }

            if (elem.TryGetProperty("check_area2", out var ca2) && ca2.GetArrayLength() == 4)
            {
                var reg = ScreenRegion.FromCorners(ca2[0].GetInt32(), ca2[1].GetInt32(), ca2[2].GetInt32(), ca2[3].GetInt32());
                item.CheckArea2 = reg;
                if (!candidateAreas[2].Contains(reg)) candidateAreas[2].Add(reg);
            }

            foreach (var prop in elem.EnumerateObject())
            {
                if (prop.Name.StartsWith("pattern_", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.TryGetDouble(out double val))
                    {
                        string profileKey = prop.Name.Substring("pattern_".Length).ToUpperInvariant();
                        item.PatternValues[profileKey] = val;
                    }
                }
            }

            targetList.Add(item);
        }
    }

    public (double Value, string Status) LookupComponent(
        List<ComponentItem> items,
        string profileName,
        string? checkImage,
        double neutral = 1.0)
    {
        string target = checkImage ?? "";
        string key = profileName.ToUpperInvariant();

        foreach (var item in items)
        {
            if (string.Equals(item.CheckImage, target, StringComparison.OrdinalIgnoreCase))
            {
                if (item.PatternValues.TryGetValue(key, out double val))
                    return (val, "ok");

                return (neutral, string.IsNullOrEmpty(target) ? "vazio" : "sem_chave");
            }
        }

        return (neutral, string.IsNullOrEmpty(target) ? "vazio" : "sem_item");
    }

    public void SaveGlobalConfig()
    {
        string cfgPath = Path.Combine(BaseDirectory, "Configs", "geral", "config.json");
        if (!File.Exists(cfgPath)) return;

        try
        {
            string json = File.ReadAllText(cfgPath);
            var node = JsonNode.Parse(json);
            if (node?["configs"] is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject cfgObj)
            {
                foreach (var res in AvailableResolutions)
                    cfgObj[$"res{res}"] = (res == GlobalConfig.ActiveResolution);

                cfgObj["modo_mira"] = GlobalConfig.AimMode;
                cfgObj["limiar_mira_visual"] = GlobalConfig.VisualDetectionThreshold;
                cfgObj["limiar_match_perfil"] = GlobalConfig.ProfileMatchThreshold;
                cfgObj["limiar_match_escopo"] = GlobalConfig.OpticMatchThreshold;
                cfgObj["limiar_match_postura"] = GlobalConfig.StanceMatchThreshold;
                cfgObj["limiar_match_componente"] = GlobalConfig.ComponentMatchThreshold;
                cfgObj["normalizar_reconhecimento"] = GlobalConfig.NormalizeRecognition;
                cfgObj["debug_reconhecimento"] = GlobalConfig.RecognitionDebugEnabled;
                cfgObj["debug_salvar_crops"] = GlobalConfig.RecognitionDebugSaveCrops;
                cfgObj["debug_top_n"] = GlobalConfig.RecognitionDebugTopN;
                cfgObj["debug_intervalo_ms"] = GlobalConfig.RecognitionDebugIntervalMs;
                cfgObj["debug_diretorio"] = GlobalConfig.RecognitionDebugDirectory;
                cfgObj["sons"] = GlobalConfig.SoundsEnabled;
                cfgObj["calibragem"] = GlobalConfig.MasterCalibration;
                cfgObj["slot2"] = GlobalConfig.SecondarySlotEnabled;
                cfgObj["dmrs"] = GlobalConfig.DmrsEnabled;
                cfgObj["invertslots"] = GlobalConfig.InvertSlots;

                File.WriteAllText(cfgPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }

    public void ApplyMasterCalibration(int sliderValue)
    {
        GlobalConfig.MasterCalibration = sliderValue;
        double factor = sliderValue / 100.0;

        foreach (var fname in ParamFiles)
        {
            string path = Path.Combine(BaseDirectory, "Configs", "calibragem", fname);
            if (!File.Exists(path)) continue;

            try
            {
                string json = File.ReadAllText(path);
                var node = JsonNode.Parse(json);
                if (node?["weapons"] is JsonArray wpArray)
                {
                    foreach (var w in wpArray)
                    {
                        if (w is JsonObject wObj)
                        {
                            wObj["multiplicador_individual"] = factor;
                            if (wObj.ContainsKey("multiplicadorempe"))
                                wObj["multiplicadorempe"] = factor;
                            if (wObj.ContainsKey("multiplicadoragachado"))
                                wObj["multiplicadoragachado"] = factor * 0.8;
                        }
                    }
                }

                if (node != null)
                {
                    File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }

        SaveGlobalConfig();
        LoadCalibrationParameters();
    }
}
