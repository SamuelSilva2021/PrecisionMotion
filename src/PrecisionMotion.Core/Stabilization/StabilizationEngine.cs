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
    private const int InventoryPollMs = 50;
    private const int StancePollMs = 200;
    private const double InventoryMaxSeconds = 120.0;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private static readonly int[] ResetKeys = {
        NativeMethods.VK_1, NativeMethods.VK_2, NativeMethods.VK_3, NativeMethods.VK_4, NativeMethods.VK_5,
        NativeMethods.VK_R, NativeMethods.VK_TAB, NativeMethods.VK_SHIFT
    };

    public ConfigProvider Config { get; }
    public PatternMatcher Recognizer { get; }
    public RecognitionDiagnostics Diagnostics { get; }
    public IRecognitionService Recognition { get; }
    public DirectInputService InputService { get; }

    public bool IsRunning => _cts is { IsCancellationRequested: false };
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

    // Protege o estado de slots/perfis compartilhado entre os loops de inventário, comandos e persistência.
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task[] _tasks = Array.Empty<Task>();

    public StabilizationEngine(ConfigProvider config)
    {
        Config = config;
        Recognizer = new PatternMatcher(
            config.BaseDirectory,
            config.GlobalConfig.ActiveResolution,
            config.GlobalConfig.NormalizeRecognition);
        Diagnostics = new RecognitionDiagnostics(config.BaseDirectory, config.GlobalConfig);
        var telemetry = new RecognitionTelemetry(
            RecognitionTelemetry.ResolveDirectory(config.BaseDirectory, config.GlobalConfig.TelemetryDirectory),
            config.GlobalConfig.TelemetryEnabled);
        Recognition = new RecognitionService(config, Recognizer, Diagnostics, telemetry);
        Recognition.OnLog = msg => OnLogMessage?.Invoke(msg);
        InputService = new DirectInputService();

        AimMode = config.GlobalConfig.AimMode;
        LoadSessionState();
        Recognition.SeedStance(CurrentStance);
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

        foreach (var issue in Config.LoadIssues.Where(i => i.Severity != ConfigIssueSeverity.Info))
            Log($"[Config] {issue.Severity}: {issue.Message}");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _tasks = new[]
        {
            RunLoop(StabilizationLoop, token, ThreadPriority.Highest),
            RunLoop(CommandDispatchLoop, token),
            RunLoop(KeyMonitorLoop, token),
            RunLoop(InventorySessionLoop, token),
            RunLoop(StanceLoop, token)
        };

        Log($"[Engine] Inicializado com resolução {Config.GlobalConfig.ActiveResolution} | Modo mira: {AimMode}");
    }

    // Cada loop roda numa thread dedicada (LongRunning); exceções inesperadas são logadas sem derrubar os demais.
    private Task RunLoop(Action<CancellationToken> loop, CancellationToken token, ThreadPriority priority = ThreadPriority.Normal) =>
        Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = priority;
            try
            {
                loop(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log($"[Engine] Loop {loop.Method.Name} encerrado por erro: {ex.Message}");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Stop()
    {
        var cts = _cts;
        if (cts == null || cts.IsCancellationRequested) return;

        cts.Cancel();

        // F10 chama Stop de dentro do loop de comandos; esperar a si mesmo só gastaria o timeout.
        var self = Task.CurrentId;
        var others = _tasks.Where(t => t.Id != self).ToArray();
        try { Task.WaitAll(others, ShutdownTimeout); } catch (AggregateException) { }

        SaveSessionState();
        Recognition.FlushTelemetry();
        Log("[Engine] Finalizado.");
    }

    private static bool Sleep(CancellationToken token, int ms) => token.WaitHandle.WaitOne(ms);

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


    private void StabilizationLoop(CancellationToken token)
    {
        bool wasRightDown = false;
        bool isActivelyCompensating = false;
        long lastVisualCheckTicks = 0;
        const double tickSeconds = 0.015;
        int tickMs = (int)(tickSeconds * 1000);

        while (!token.IsCancellationRequested)
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

                Sleep(token, tickMs);
            }
            else
            {
                isActivelyCompensating = false;
                _subPixelAccumulator = 0.0;
                Sleep(token, 5);
            }
        }
    }

    private void KeyMonitorLoop(CancellationToken token)
    {
        bool wasPressed = false;
        while (!token.IsCancellationRequested)
        {
            bool hasChanged = DirectInputService.WasKeyPressedSinceLastQuery(NativeMethods.VK_TAB, out bool isDown);
            if ((isDown && !wasPressed) || (hasChanged && !isDown && !wasPressed))
            {
                Interlocked.Increment(ref _tabPressCount);
            }
            wasPressed = isDown;
            Sleep(token, 2);
        }
    }

    private void InventorySessionLoop(CancellationToken token)
    {
        int lastSeenTab = Volatile.Read(ref _tabPressCount);
        InspectInventory(token, 0.3, 0.5); // Initial check

        while (!token.IsCancellationRequested)
        {
            if (Volatile.Read(ref _tabPressCount) == lastSeenTab)
            {
                Sleep(token, 2);
                continue;
            }

            lastSeenTab = Volatile.Read(ref _tabPressCount);
            InspectInventory(token);
            lastSeenTab = Volatile.Read(ref _tabPressCount);
        }
    }

    private void InspectInventory(CancellationToken token, double waitOpen = 2.0, double waitClose = 0.5)
    {
        var stopwatch = Stopwatch.StartNew();
        InventoryRecognitionResult? applied = null;
        bool inventorySeen = false;
        double lastVisibleAt = 0.0;

        Recognition.BeginInventorySession();

        // A sessão acompanha o inventário enquanto ele estiver aberto: trocas de arma/anexo feitas
        // depois da primeira leitura também são aplicadas, sem depender de um novo TAB.
        while (!token.IsCancellationRequested && stopwatch.Elapsed.TotalSeconds < InventoryMaxSeconds)
        {
            double elapsed = stopwatch.Elapsed.TotalSeconds;
            var reading = Recognition.PollInventory();

            if (!reading.Visible)
            {
                if (elapsed - lastVisibleAt >= (inventorySeen ? waitClose : waitOpen))
                    break;
            }
            else
            {
                inventorySeen = true;
                lastVisibleAt = elapsed;

                // A histerese (N confirmações, limiares de entrada/saída, margem de ambiguidade) já foi aplicada pelo serviço.
                if (reading.Ready && !reading.SameState(applied))
                {
                    ApplyInventoryState(reading);
                    applied = reading;
                }
            }

            Sleep(token, InventoryPollMs);
        }
    }

    private void ApplyInventoryState(InventoryRecognitionResult reading)
    {
        lock (_stateLock)
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
    }

    private void StanceLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = Recognition.PollStance();
                if (result.Changed && result.Stable.HasValue)
                {
                    CurrentStance = result.Stable.Value;
                    Log($"[Stance] Atualizado: {CurrentStance}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"[Stance] Falha na leitura: {ex.Message}");
            }

            Sleep(token, StancePollMs);
        }
    }

    private void CommandDispatchLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F10))
            {
                Stop();
                OnShutdownRequested?.Invoke();
                break;
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F6))
            {
                OnRestoreUIRequested?.Invoke();
                Sleep(token, 300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F7))
            {
                AimMode = AimMode == "segurar" ? "alternar" : "segurar";
                IsAimEngaged = false;
                Log($"[AimMode] Alternado para: {AimMode}");
                Sleep(token, 300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F8))
            {
                lock (_stateLock)
                {
                    double prev = DynamicGain;
                    DynamicGain = Math.Max(GainMin, DynamicGain * 0.8);
                    SaveSessionState();
                    Log($"[Gain] Reduzido: {prev:F0} -> {DynamicGain:F0}");
                }
                Sleep(token, 300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F9))
            {
                lock (_stateLock)
                {
                    double prev = DynamicGain;
                    DynamicGain = Math.Min(GainMax, DynamicGain * 1.25);
                    SaveSessionState();
                    Log($"[Gain] Aumentado: {prev:F0} -> {DynamicGain:F0}");
                }
                Sleep(token, 300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_F5))
            {
                lock (_stateLock)
                {
                    Slot1Profile = "UNASSIGNED";
                    Slot2Profile = "UNASSIGNED";
                }
                Log("[Profiles] Reset manual realizado (F5)");
                Sleep(token, 300);
            }

            if (DirectInputService.IsKeyPressed(NativeMethods.VK_1))
            {
                SetActiveSlot(1, Slot1Profile);
                Sleep(token, 150);
            }
            else if (DirectInputService.IsKeyPressed(NativeMethods.VK_2))
            {
                SetActiveSlot(2, Slot2Profile);
                Sleep(token, 150);
            }

            Sleep(token, 20);
        }
    }

    private void SetActiveSlot(int slot, string profile)
    {
        lock (_stateLock)
        {
            if (ActiveSlot == slot) return;
            ActiveSlot = slot;
            Log($"[Slot] Ativo: Slot {slot} ({profile})");
            SaveSessionState();
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
            lock (_stateLock)
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
        Recognition.Dispose();
        Recognizer.Dispose();
        InputService.Dispose();
        _cts?.Dispose();
    }
}
