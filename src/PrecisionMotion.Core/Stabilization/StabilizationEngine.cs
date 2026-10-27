using System.Diagnostics;
using System.Text.Json;
using PrecisionMotion.Core.Configuration;
using PrecisionMotion.Core.Input;
using PrecisionMotion.Core.Interop;
using PrecisionMotion.Core.Models;
using PrecisionMotion.Core.Vision;

namespace PrecisionMotion.Core.Stabilization;

public sealed class StabilizationEngine : IDisposable
{
    private const double PadraoArma = 1.0;
    private const double PadraoEmPe = -0.02;
    private const double PadraoAgachado = -0.15;
    private const double GainMin = 10.0;
    private const double GainMax = 2000.0;
    private const int StableReadsRequired = 3;
    private const int InventoryPollMs = 50;
    private const double InventoryMaxSeconds = 120.0;

    private readonly record struct SlotAttachments(string? Optic, string? Component1, string? Component2);
    private readonly record struct InventoryReading(string? Profile1, string? Profile2, SlotAttachments Slot1, SlotAttachments Slot2);

    private static readonly int[] ResetKeys = {
        NativeMethods.VK_1, NativeMethods.VK_2, NativeMethods.VK_3, NativeMethods.VK_4, NativeMethods.VK_5,
        NativeMethods.VK_R, NativeMethods.VK_TAB, NativeMethods.VK_SHIFT
    };

    public ConfigProvider Config { get; }
    public PatternMatcher Recognizer { get; }
    public RecognitionDiagnostics Diagnostics { get; }
    public DirectInputService InputService { get; }

    public bool IsRunning { get; private set; }
    public int ActiveSlot { get; private set; } = 1;
    public string Slot1Profile { get; private set; } = "M4";
    public string Slot2Profile { get; private set; } = "UNASSIGNED";
    public string Slot1Optic { get; private set; } = "";
    public string Slot2Optic { get; private set; } = "";
    public string? Slot1Component1 { get; private set; }
    public string? Slot2Component1 { get; private set; }
    public string? Slot1Component2 { get; private set; }
    public string? Slot2Component2 { get; private set; }

    public StanceType CurrentStance { get; private set; } = StanceType.Standard;
    public string AimMode { get; private set; } = "segurar";
    public bool IsAimEngaged { get; private set; }
    public double DynamicGain { get; private set; } = 120.0;

    public Action? OnRestoreUIRequested { get; set; }
    public Action? OnShutdownRequested { get; set; }
    public Action<string>? OnLogMessage { get; set; }

    private int _tabPressCount;
    private bool _initialReadCompleted;
    private double _subPixelAccumulator;
    private readonly HashSet<(string, string)> _loggedWarnings = new();

    private Thread? _stabilizationThread;
    private Thread? _commandThread;
    private Thread? _keyWatcherThread;
    private Thread? _inventoryThread;
    private Thread? _stanceThread;

    public StabilizationEngine(ConfigProvider config)
    {
        Config = config;
        Recognizer = new PatternMatcher(
            config.BaseDirectory,
            config.GlobalConfig.ActiveResolution,
            config.GlobalConfig.NormalizeRecognition);
        Diagnostics = new RecognitionDiagnostics(config.BaseDirectory, config.GlobalConfig);
        InputService = new DirectInputService();

        AimMode = config.GlobalConfig.AimMode;
        LoadSessionState();
    }

    private void Log(string msg)
    {
        string formatted = $"{DateTime.Now:HH:mm:ss} {msg}";
        OnLogMessage?.Invoke(formatted);
        Debug.WriteLine(formatted);
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;

        _stabilizationThread = new Thread(StabilizationLoop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _commandThread = new Thread(CommandDispatchLoop) { IsBackground = true };
        _keyWatcherThread = new Thread(KeyMonitorLoop) { IsBackground = true };
        _inventoryThread = new Thread(InventorySessionLoop) { IsBackground = true };
        _stanceThread = new Thread(StanceLoop) { IsBackground = true };

        _stabilizationThread.Start();
        _commandThread.Start();
        _keyWatcherThread.Start();
        _inventoryThread.Start();
        _stanceThread.Start();

        Log($"[Engine] Inicializado com resolução {Config.GlobalConfig.ActiveResolution} | Modo mira: {AimMode}");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        SaveSessionState();
        Log("[Engine] Finalizado.");
    }

    public CalculationResult CalculateCurrentStep()
    {
        string profileName = ActiveSlot == 1 ? Slot1Profile : Slot2Profile;
        Config.Profiles.TryGetValue(profileName, out var profileData);

        double baseRate = 1.0;
        if (profileData?.Pattern != null && profileData.Pattern.Count > 0)
            baseRate = profileData.Pattern.Max(p => Math.Abs(p));

        double globalFactor = profileData?.IndividualMultiplier ?? 1.0;

        Config.ProfileOverrides.TryGetValue(profileName, out var ov);
        double arma = ov?.IndividualMultiplier ?? (profileData?.IndividualMultiplier ?? PadraoArma);
        double offPe = ov?.StandardOffset ?? PadraoEmPe;
        double offAg = ov?.LowProfileOffset ?? PadraoAgachado;

        double gainPe = arma + offPe;
        double gainAg = arma + offAg;

        if (!(gainPe >= 0.1 && gainAg >= 0.1 && gainAg < gainPe))
        {
            gainPe = arma + PadraoEmPe;
            gainAg = arma + PadraoAgachado;
        }

        double stanceFactor = CurrentStance switch
        {
            StanceType.LowProfile => gainAg,
            StanceType.Prone => gainPe * (profileData?.ProneMultiplier ?? 0.6),
            _ => gainPe
        };

        string opticImg = ActiveSlot == 1 ? Slot1Optic : Slot2Optic;
        var (opticFactor, _) = Config.LookupComponent(Config.OpticItems, profileName, opticImg, 1.0);

        string? comp1 = ActiveSlot == 1 ? Slot1Component1 : Slot2Component1;
        string? comp2 = ActiveSlot == 1 ? Slot1Component2 : Slot2Component2;

        var (red1, _) = Config.LookupComponent(Config.ComponentGroup1, profileName, comp1, 0.0);
        var (red2, _) = Config.LookupComponent(Config.ComponentGroup2, profileName, comp2, 0.0);

        double c1Factor = Math.Max(0.1, 1.0 - red1);
        double c2Factor = Math.Max(0.1, 1.0 - red2);

        double velocity = baseRate * globalFactor * stanceFactor * opticFactor * c1Factor * c2Factor * DynamicGain;

        return new CalculationResult(
            BaseRate: baseRate,
            ProfileFactor: globalFactor,
            StanceFactor: stanceFactor,
            OpticFactor: opticFactor,
            OpticImage: string.IsNullOrEmpty(opticImg) ? "padrao" : opticImg,
            Component1Factor: c1Factor,
            Component1Image: comp1 ?? "-",
            Component2Factor: c2Factor,
            Component2Image: comp2 ?? "-",
            DynamicGain: DynamicGain,
            VelocityPxPerSec: velocity
        );
    }

    private void StabilizationLoop()
    {
        bool wasRightDown = false;
        bool isActivelyCompensating = false;
        long lastVisualCheckTicks = 0;
        const double tickSeconds = 0.015;
        int tickMs = (int)(tickSeconds * 1000);

        while (IsRunning)
        {
            bool leftDown = DirectInputService.IsKeyPressed(NativeMethods.VK_LBUTTON);
            bool rightDown = DirectInputService.IsKeyPressed(NativeMethods.VK_RBUTTON);

            // Update Aim State
            if (AimMode == "segurar")
            {
                IsAimEngaged = rightDown;
            }
            else
            {
                if (rightDown && !wasRightDown)
                    IsAimEngaged = !IsAimEngaged;

                if (IsAimEngaged && ResetKeys.Any(k => DirectInputService.IsKeyPressed(k)))
                    IsAimEngaged = false;

                if (leftDown && !IsAimEngaged)
                {
                    long now = Stopwatch.GetTimestamp();
                    if ((now - lastVisualCheckTicks) / (double)Stopwatch.Frequency >= 0.3)
                    {
                        lastVisualCheckTicks = now;
                        double? darkness = Recognizer.CalculateCenterDarkness();
                        if (darkness >= Config.GlobalConfig.VisualDetectionThreshold)
                            IsAimEngaged = true;
                    }
                }
            }
            wasRightDown = rightDown;

            if (leftDown && IsAimEngaged)
            {
                var calc = CalculateCurrentStep();
                if (!isActivelyCompensating)
                {
                    Log($"[Motion] Ativo -> Slot {ActiveSlot}: {(ActiveSlot == 1 ? Slot1Profile : Slot2Profile)} | Postura: {CurrentStance} | Velocidade: {calc.VelocityPxPerSec:F0} px/s");
                    isActivelyCompensating = true;
                }

                double step = calc.VelocityPxPerSec * tickSeconds;
                _subPixelAccumulator += step;
                int pixels = (int)_subPixelAccumulator;

                if (pixels != 0)
                {
                    DirectInputService.MoveRelative(0, pixels);
                    _subPixelAccumulator -= pixels;
                }

                Thread.Sleep(tickMs);
            }
            else
            {
                isActivelyCompensating = false;
                _subPixelAccumulator = 0.0;
                Thread.Sleep(5);
            }
        }
    }

    private void KeyMonitorLoop()
    {
        bool wasPressed = false;
        while (IsRunning)
        {
            bool hasChanged = DirectInputService.WasKeyPressedSinceLastQuery(NativeMethods.VK_TAB, out bool isDown);
            if ((isDown && !wasPressed) || (hasChanged && !isDown && !wasPressed))
            {
                Interlocked.Increment(ref _tabPressCount);
            }
            wasPressed = isDown;
            Thread.Sleep(2);
        }
    }

    private void InventorySessionLoop()
    {
        int lastSeenTab = _tabPressCount;
        InspectInventory(0.3, 0.5); // Initial check

        while (IsRunning)
        {
            if (_tabPressCount == lastSeenTab)
            {
                Thread.Sleep(2);
                continue;
            }

            lastSeenTab = _tabPressCount;
            InspectInventory();
            lastSeenTab = _tabPressCount;
        }
    }

    private void InspectInventory(double waitOpen = 2.0, double waitClose = 0.5)
    {
        var areas = new List<ScreenRegion>();
        if (Config.Slot1Area.HasValue) areas.Add(Config.Slot1Area.Value);
        if (Config.Slot2Area.HasValue) areas.Add(Config.Slot2Area.Value);

        foreach (var list in Config.OpticCandidateAreas.Values) areas.AddRange(list);
        foreach (var list in Config.Comp1CandidateAreas.Values) areas.AddRange(list);
        foreach (var list in Config.Comp2CandidateAreas.Values) areas.AddRange(list);

        if (areas.Count == 0) return;

        int pad = PatternMatcher.IconPadding;
        int minX = areas.Min(a => a.X) - pad;
        int minY = areas.Min(a => a.Y) - pad;
        int maxX = areas.Max(a => a.X + a.Width) + pad;
        int maxY = areas.Max(a => a.Y + a.Height) + pad;

        var bounding = new ScreenRegion(Math.Max(minX, 0), Math.Max(minY, 0), maxX - minX, maxY - minY);

        var stopwatch = Stopwatch.StartNew();
        InventoryReading? last = null;
        InventoryReading? applied = null;
        int stableReads = 0;
        bool inventorySeen = false;
        double lastVisibleAt = 0.0;

        // A sessão acompanha o inventário enquanto ele estiver aberto: trocas de arma/anexo feitas
        // depois da primeira leitura também são aplicadas, sem depender de um novo TAB.
        while (IsRunning && stopwatch.Elapsed.TotalSeconds < InventoryMaxSeconds)
        {
            double elapsed = stopwatch.Elapsed.TotalSeconds;

            Recognizer.StartCache(bounding);
            try
            {
                var reading = ReadInventory();

                if (reading.Profile1 == null && reading.Profile2 == null)
                {
                    last = null;
                    stableReads = 0;
                    if (elapsed - lastVisibleAt >= (inventorySeen ? waitClose : waitOpen))
                        break;
                }
                else
                {
                    inventorySeen = true;
                    lastVisibleAt = elapsed;
                    stableReads = reading == last ? stableReads + 1 : 1;
                    last = reading;

                    if (stableReads >= StableReadsRequired && reading != applied)
                    {
                        ApplyInventoryState(reading);
                        applied = reading;
                    }
                }
            }
            finally
            {
                Recognizer.ClearCache();
            }

            Thread.Sleep(InventoryPollMs);
        }
    }

    private InventoryReading ReadInventory()
    {
        string? p1 = null;
        string? p2 = null;

        if (Config.Slot1Area.HasValue)
        {
            using var frame = Recognizer.CaptureFrame(Config.Slot1Area.Value);
            var result = Recognizer.MatchProfileDetailedFrame(
                frame,
                Config.GlobalConfig.ProfileMatchThreshold,
                Config.GlobalConfig.RecognitionDebugTopN);
            p1 = result.BestName;
            MaybeLogRecognitionDebug("profile_slot1", "Perfil slot 1", result, frame);
        }

        if (Config.Slot2Area.HasValue)
        {
            using var frame = Recognizer.CaptureFrame(Config.Slot2Area.Value);
            var result = Recognizer.MatchProfileDetailedFrame(
                frame,
                Config.GlobalConfig.ProfileMatchThreshold,
                Config.GlobalConfig.RecognitionDebugTopN);
            p2 = result.BestName;
            MaybeLogRecognitionDebug("profile_slot2", "Perfil slot 2", result, frame);
        }

        // Anexos só entram na leitura de um slot cuja arma foi reconhecida, para não misturar
        // anexos novos com o perfil antigo.
        var s1 = p1 != null ? DetectAttachmentsForSlot(1) : default;
        var s2 = p2 != null ? DetectAttachmentsForSlot(2) : default;
        return new InventoryReading(p1, p2, s1, s2);
    }

    private void ApplyInventoryState(InventoryReading reading)
    {
        bool changed = false;

        if (!string.IsNullOrEmpty(reading.Profile1))
        {
            if (reading.Profile1 != Slot1Profile)
            {
                if (_initialReadCompleted && (Slot1Profile == "UNASSIGNED" || string.IsNullOrEmpty(Slot1Profile)))
                    ActiveSlot = 1;

                changed = true;
            }

            Slot1Optic = reading.Slot1.Optic ?? "";
            Slot1Component1 = reading.Slot1.Component1;
            Slot1Component2 = reading.Slot1.Component2;
            Slot1Profile = reading.Profile1;
        }

        if (!string.IsNullOrEmpty(reading.Profile2))
        {
            if (reading.Profile2 != Slot2Profile)
            {
                if (_initialReadCompleted && (Slot2Profile == "UNASSIGNED" || string.IsNullOrEmpty(Slot2Profile)))
                    ActiveSlot = 2;

                changed = true;
            }

            Slot2Optic = reading.Slot2.Optic ?? "";
            Slot2Component1 = reading.Slot2.Component1;
            Slot2Component2 = reading.Slot2.Component2;
            Slot2Profile = reading.Profile2;
        }

        _initialReadCompleted = true;
        SaveSessionState();
        if (changed)
            Log($"[Inventory] Atualizado -> Slot 1: {Slot1Profile} ({Slot1Optic}) | Slot 2: {Slot2Profile} ({Slot2Optic}) | Ativo: Slot {ActiveSlot}");
    }

    private SlotAttachments DetectAttachmentsForSlot(int slot)
    {
        string? optic = null, comp1 = null, comp2 = null;

        if (Config.OpticCandidateAreas.TryGetValue(slot, out var opticAreas) && opticAreas.Count > 0)
        {
            var images = Config.OpticItems.Select(o => o.CheckImage).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var result = Recognizer.MatchComponentDetailed(
                opticAreas,
                images,
                Config.GlobalConfig.OpticMatchThreshold,
                Config.GlobalConfig.RecognitionDebugTopN);
            optic = result.BestName;
            MaybeLogRecognitionDebug($"optic_slot{slot}", $"Escopo slot {slot}", result, null);
        }

        if (Config.Comp1CandidateAreas.TryGetValue(slot, out var comp1Areas) && comp1Areas.Count > 0)
        {
            var images = Config.ComponentGroup1.Select(c => c.CheckImage).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var result = Recognizer.MatchComponentDetailed(
                comp1Areas,
                images,
                Config.GlobalConfig.ComponentMatchThreshold,
                Config.GlobalConfig.RecognitionDebugTopN);
            comp1 = result.BestName;
            MaybeLogRecognitionDebug($"component1_slot{slot}", $"Componente 1 slot {slot}", result, null);
        }

        if (Config.Comp2CandidateAreas.TryGetValue(slot, out var comp2Areas) && comp2Areas.Count > 0)
        {
            var images = Config.ComponentGroup2.Select(c => c.CheckImage).Where(s => !string.IsNullOrEmpty(s)).ToList();
            var result = Recognizer.MatchComponentDetailed(
                comp2Areas,
                images,
                Config.GlobalConfig.ComponentMatchThreshold,
                Config.GlobalConfig.RecognitionDebugTopN);
            comp2 = result.BestName;
            MaybeLogRecognitionDebug($"component2_slot{slot}", $"Componente 2 slot {slot}", result, null);
        }

        return new SlotAttachments(optic, comp1, comp2);
    }

    private void StanceLoop()
    {
        var coords = Config.GlobalConfig.PostureCoords;
        StanceType? candidate = null;

        while (IsRunning)
        {
            try
            {
                using var frame = Recognizer.CaptureFrame(coords);
                var result = Recognizer.MatchStanceFrame(
                    frame,
                    Config.GlobalConfig.StanceMatchThreshold,
                    Config.GlobalConfig.RecognitionDebugTopN);
                MaybeLogRecognitionDebug("stance", "Postura", result, frame);
                StanceType? detected = Enum.TryParse<StanceType>(result.BestName, out var parsed)
                    ? parsed
                    : null;
                if (detected.HasValue && detected.Value != CurrentStance)
                {
                    if (detected.Value == candidate)
                    {
                        CurrentStance = detected.Value;
                        Log($"[Stance] Atualizado: {CurrentStance}");
                        candidate = null;
                    }
                    else
                    {
                        candidate = detected.Value;
                    }
                }
                else
                {
                    candidate = null;
                }
            }
            catch { }

            Thread.Sleep(200);
        }
    }

    private void MaybeLogRecognitionDebug(string key, string label, MatchDebugResult result, OpenCvSharp.Mat? frame)
    {
        if (!Diagnostics.ShouldEmit(key))
            return;

        string best = result.BestName ?? "nenhum";
        string candidates = Diagnostics.FormatCandidates(result);
        Log($"[RecognitionDebug] {label} -> best={best} score={result.BestScore:F3} top=[{candidates}]");
        Diagnostics.SaveCrop(label, frame);
    }

    private void CommandDispatchLoop()
    {
        while (IsRunning)
        {
            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F10))
            {
                IsRunning = false;
                OnShutdownRequested?.Invoke();
                break;
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F6))
            {
                OnRestoreUIRequested?.Invoke();
                Thread.Sleep(300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F7))
            {
                AimMode = AimMode == "segurar" ? "alternar" : "segurar";
                IsAimEngaged = false;
                Log($"[AimMode] Alternado para: {AimMode}");
                Thread.Sleep(300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F8))
            {
                double prev = DynamicGain;
                DynamicGain = Math.Max(GainMin, DynamicGain * 0.8);
                SaveSessionState();
                Log($"[Gain] Reduzido: {prev:F0} -> {DynamicGain:F0}");
                Thread.Sleep(300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F9))
            {
                double prev = DynamicGain;
                DynamicGain = Math.Min(GainMax, DynamicGain * 1.25);
                SaveSessionState();
                Log($"[Gain] Aumentado: {prev:F0} -> {DynamicGain:F0}");
                Thread.Sleep(300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F5))
            {
                Slot1Profile = "UNASSIGNED";
                Slot2Profile = "UNASSIGNED";
                Log("[Profiles] Reset manual realizado (F5)");
                Thread.Sleep(300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_1))
            {
                if (ActiveSlot != 1)
                {
                    ActiveSlot = 1;
                    Log($"[Slot] Ativo: Slot 1 ({Slot1Profile})");
                    SaveSessionState();
                }
                Thread.Sleep(150);
            }
            else if (DirectInputService.IsKeyPressed(NativeMethods.VK_2))
            {
                if (ActiveSlot != 2)
                {
                    ActiveSlot = 2;
                    Log($"[Slot] Ativo: Slot 2 ({Slot2Profile})");
                    SaveSessionState();
                }
                Thread.Sleep(150);
            }

            Thread.Sleep(20);
        }
    }

    private string GetStateFilePath() =>
        Path.Combine(Config.BaseDirectory, "Configs", "geral", "estado_motor.json");

    private void SaveSessionState()
    {
        try
        {
            var state = new MotionSessionState
            {
                ActiveSlot = ActiveSlot,
                Slot1Profile = Slot1Profile,
                Slot2Profile = Slot2Profile,
                Slot1Optic = Slot1Optic,
                Slot2Optic = Slot2Optic,
                Slot1Component1 = Slot1Component1,
                Slot2Component1 = Slot2Component1,
                Slot1Component2 = Slot1Component2,
                Slot2Component2 = Slot2Component2,
                DynamicGain = DynamicGain,
                SavedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetStateFilePath(), json);
        }
        catch { }
    }

    private void LoadSessionState()
    {
        string path = GetStateFilePath();
        if (!File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<MotionSessionState>(json);
            if (state == null) return;

            ActiveSlot = state.ActiveSlot is 1 or 2 ? state.ActiveSlot : 1;
            if (!string.IsNullOrEmpty(state.Slot1Profile)) Slot1Profile = state.Slot1Profile;
            if (!string.IsNullOrEmpty(state.Slot2Profile)) Slot2Profile = state.Slot2Profile;
            Slot1Optic = state.Slot1Optic ?? "";
            Slot2Optic = state.Slot2Optic ?? "";
            Slot1Component1 = state.Slot1Component1;
            Slot2Component1 = state.Slot2Component1;
            Slot1Component2 = state.Slot1Component2;
            Slot2Component2 = state.Slot2Component2;

            if (state.DynamicGain >= GainMin && state.DynamicGain <= GainMax)
                DynamicGain = state.DynamicGain;

            _initialReadCompleted = true;
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        Recognizer.Dispose();
        InputService.Dispose();
    }
}
