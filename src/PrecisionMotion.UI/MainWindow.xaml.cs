using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PrecisionMotion.Core.Configuration;
using PrecisionMotion.Core.Models;
using PrecisionMotion.Core.Stabilization;
using PrecisionMotion.Core.Vision.Detection;
using PrecisionMotion.Core.Vision.Pipeline;
using PrecisionMotion.Core.Vision.Visualization;

namespace PrecisionMotion.UI;

public partial class MainWindow : Window
{
    private static readonly string[] ProfileNames = {
        "M416", "AKM", "BERYL", "AUG", "SCARL", "GROZA", "ACE32", "QBZ",
        "G36C", "K2", "FAMAS", "MUTANT", "M16A4", "SLR", "SKS", "MINI14",
        "MK12", "QBU", "VSS", "DRAGUNOV", "UMP45", "VECTOR", "TOMMYGUN",
        "MP5K", "PP19", "P90", "JS9", "M249", "DP28", "MG3", "AWM", "KAR98K"
    };

    private static readonly Dictionary<string, string> DataNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "SCARL", "SCAR" },
        { "VECTOR", "VECTR" },
        { "TOMMYGUN", "TOMMYG" }
    };

    private readonly ConfigProvider _config;
    private StabilizationEngine? _engine;
    private string _currentProfile = "M416";
    private bool _suppressSliderEvents;

    private float _visionConfidence = 0.40f;
    private float _visionNms = 0.45f;
    private CancellationTokenSource? _auditCts;
    private string? _lastGeneratedVideoPath;
    private string _modelPath = "";

    public MainWindow()
    {
        InitializeComponent();

        _config = new ConfigProvider();
        InitializeControls();
        RestoreLastUiState();

        Closing += OnWindowClosing;
    }

    private void InitializeControls()
    {
        // Populate profile list
        ProfileListBox.ItemsSource = ProfileNames;
        ProfileListBox.SelectedItem = "M416";

        // Prevent mouse wheel on all sliders
        var sliders = new[] {
            BigSlider, SliderArma, SliderEmPe, SliderAgachado, SliderFerro,
            Slider1x, SliderHolo, Slider2x, Slider3x, Slider4x, Slider6x, Slider8x, SliderTermica
        };
        foreach (var s in sliders)
        {
            s.PreviewMouseWheel += (sender, e) => e.Handled = true;
        }

        // Apply loaded configs
        foreach (var child in ResolutionsWrap.Children)
        {
            if (child is RadioButton rb && rb.Tag?.ToString() == _config.GlobalConfig.ActiveResolution)
            {
                rb.IsChecked = true;
                break;
            }
        }

        if (_config.GlobalConfig.AimMode == "alternar")
            AimToggleRadio.IsChecked = true;
        else
            AimHoldRadio.IsChecked = true;

        CheckSlot2.IsChecked = _config.GlobalConfig.SecondarySlotEnabled;
        CheckDmrs.IsChecked = _config.GlobalConfig.DmrsEnabled;
        CheckInvertSlots.IsChecked = _config.GlobalConfig.InvertSlots;
        CheckSounds.IsChecked = _config.GlobalConfig.SoundsEnabled;

        BigSlider.Value = _config.GlobalConfig.MasterCalibration;
        BigSliderLabel.Text = $"{_config.GlobalConfig.MasterCalibration}%";

        InitializeVisionControls();
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (ViewMain == null || ViewWp == null || ViewVision == null) return;

        if (TabMainBtn.IsChecked == true)
        {
            ViewMain.Visibility = Visibility.Visible;
            ViewWp.Visibility = Visibility.Collapsed;
            ViewVision.Visibility = Visibility.Collapsed;
        }
        else if (TabWpBtn.IsChecked == true)
        {
            ViewMain.Visibility = Visibility.Collapsed;
            ViewWp.Visibility = Visibility.Visible;
            ViewVision.Visibility = Visibility.Collapsed;
        }
        else if (TabVisionBtn.IsChecked == true)
        {
            ViewMain.Visibility = Visibility.Collapsed;
            ViewWp.Visibility = Visibility.Collapsed;
            ViewVision.Visibility = Visibility.Visible;
        }
    }

    private void OnResolutionSelected(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag != null)
        {
            string res = rb.Tag.ToString()!;
            _config.GlobalConfig.ActiveResolution = res;
            _config.SaveGlobalConfig();
            _config.ReloadAll();
            LoadProfileValues(_currentProfile);
        }
    }

    private void OnAimModeChanged(object sender, RoutedEventArgs e)
    {
        // Disparado durante InitializeComponent, antes de os controles/config existirem.
        if (_config == null || AimToggleRadio == null)
            return;

        _config.GlobalConfig.AimMode = AimToggleRadio.IsChecked == true ? "alternar" : "segurar";
        _config.SaveGlobalConfig();
    }

    private void OnBigSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)e.NewValue;
        if (BigSliderLabel != null)
            BigSliderLabel.Text = $"{val}%";
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileListBox.SelectedItem is string profile)
        {
            _currentProfile = profile;
            SelectedProfileHeader.Text = $"Perfil Selecionado: {profile}";
            LoadProfileValues(profile);
        }
    }

    private string GetDataKey(string displayName) =>
        DataNameMap.TryGetValue(displayName, out var mapped) ? mapped : displayName;

    private void LoadProfileValues(string profileName)
    {
        _suppressSliderEvents = true;
        try
        {
            string dataKey = GetDataKey(profileName).ToUpperInvariant();
            string resPath = Path.Combine(_config.BaseDirectory, "Configs", _config.GlobalConfig.ActiveResolution, "parameter1.json");

            double arma = 1.0;
            double empe = -0.02;
            double agachado = -0.15;

            var scopeDict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(resPath))
            {
                try
                {
                    string json = File.ReadAllText(resPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("weapons", out var wArray))
                    {
                        foreach (var w in wArray.EnumerateArray())
                        {
                            if (string.Equals(w.GetProperty("name").GetString(), dataKey, StringComparison.OrdinalIgnoreCase))
                            {
                                arma = w.TryGetProperty("multiplicador_individual", out var mi) ? mi.GetDouble() : 1.0;
                                empe = w.TryGetProperty("multiplicadorempe", out var me) ? me.GetDouble() : -0.02;
                                agachado = w.TryGetProperty("multiplicadoragachado", out var ma) ? ma.GetDouble() : -0.15;
                                break;
                            }
                        }
                    }

                    if (root.TryGetProperty("scopes", out var sArray))
                    {
                        foreach (var s in sArray.EnumerateArray())
                        {
                            string img = s.TryGetProperty("check_image", out var ci) ? ci.GetString() ?? "" : "";
                            string pKey = $"pattern_{dataKey}";
                            if (s.TryGetProperty(pKey, out var pv))
                            {
                                scopeDict[img] = pv.GetDouble();
                            }
                        }
                    }
                }
                catch { }
            }

            SliderArma.Value = arma;
            ValArma.Text = arma.ToString("F2");

            SliderEmPe.Value = empe;
            ValEmPe.Text = empe.ToString("F2");

            SliderAgachado.Value = agachado;
            ValAgachado.Text = agachado.ToString("F2");

            SetScopeSlider(SliderFerro, ValFerro, scopeDict.GetValueOrDefault("", 1.0));
            SetScopeSlider(Slider1x, Val1x, scopeDict.GetValueOrDefault("shape1x.png", 1.0));
            SetScopeSlider(SliderHolo, ValHolo, scopeDict.GetValueOrDefault("shapeholo.png", 1.0));
            SetScopeSlider(Slider2x, Val2x, scopeDict.GetValueOrDefault("shape2x.png", 1.75));
            SetScopeSlider(Slider3x, Val3x, scopeDict.GetValueOrDefault("shape3x.png", 2.10));
            SetScopeSlider(Slider4x, Val4x, scopeDict.GetValueOrDefault("shape4x.png", 2.70));
            SetScopeSlider(Slider6x, Val6x, scopeDict.GetValueOrDefault("shape6x.png", 3.60));
            SetScopeSlider(Slider8x, Val8x, scopeDict.GetValueOrDefault("shape8x.png", 4.20));
            SetScopeSlider(SliderTermica, ValTermica, scopeDict.GetValueOrDefault("shapetermica.png", 1.0));
        }
        finally
        {
            _suppressSliderEvents = false;
        }
    }

    private static void SetScopeSlider(Slider slider, TextBlock label, double val)
    {
        slider.Value = val;
        label.Text = val.ToString("F2");
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents || _config == null || ValTermica == null) return;

        if (sender == SliderArma) ValArma.Text = e.NewValue.ToString("F2");
        else if (sender == SliderEmPe) ValEmPe.Text = e.NewValue.ToString("F2");
        else if (sender == SliderAgachado) ValAgachado.Text = e.NewValue.ToString("F2");
        else if (sender == SliderFerro) ValFerro.Text = e.NewValue.ToString("F2");
        else if (sender == Slider1x) Val1x.Text = e.NewValue.ToString("F2");
        else if (sender == SliderHolo) ValHolo.Text = e.NewValue.ToString("F2");
        else if (sender == Slider2x) Val2x.Text = e.NewValue.ToString("F2");
        else if (sender == Slider3x) Val3x.Text = e.NewValue.ToString("F2");
        else if (sender == Slider4x) Val4x.Text = e.NewValue.ToString("F2");
        else if (sender == Slider6x) Val6x.Text = e.NewValue.ToString("F2");
        else if (sender == Slider8x) Val8x.Text = e.NewValue.ToString("F2");
        else if (sender == SliderTermica) ValTermica.Text = e.NewValue.ToString("F2");

        SaveCurrentProfileSliders();
    }

    private void SaveCurrentProfileSliders()
    {
        string dataKey = GetDataKey(_currentProfile).ToUpperInvariant();
        string resPath = Path.Combine(_config.BaseDirectory, "Configs", _config.GlobalConfig.ActiveResolution, "parameter1.json");
        if (!File.Exists(resPath)) return;

        try
        {
            string json = File.ReadAllText(resPath);
            var node = JsonNode.Parse(json);
            if (node == null) return;

            if (node["weapons"] is JsonArray wArray)
            {
                foreach (var w in wArray)
                {
                    if (w is JsonObject wObj && string.Equals(wObj["name"]?.ToString(), dataKey, StringComparison.OrdinalIgnoreCase))
                    {
                        wObj["multiplicador_individual"] = Math.Round(SliderArma.Value, 4);
                        wObj["multiplicadorempe"] = Math.Round(SliderEmPe.Value, 4);
                        wObj["multiplicadoragachado"] = Math.Round(SliderAgachado.Value, 4);
                        break;
                    }
                }
            }

            if (node["scopes"] is JsonArray sArray)
            {
                var map = new Dictionary<string, double>
                {
                    { "", SliderFerro.Value },
                    { "shape1x.png", Slider1x.Value },
                    { "shapeholo.png", SliderHolo.Value },
                    { "shape2x.png", Slider2x.Value },
                    { "shape3x.png", Slider3x.Value },
                    { "shape4x.png", Slider4x.Value },
                    { "shape6x.png", Slider6x.Value },
                    { "shape8x.png", Slider8x.Value },
                    { "shapetermica.png", SliderTermica.Value }
                };

                foreach (var s in sArray)
                {
                    if (s is JsonObject sObj)
                    {
                        string img = sObj["check_image"]?.ToString() ?? "";
                        if (map.TryGetValue(img, out double v))
                        {
                            sObj[$"pattern_{dataKey}"] = Math.Round(v, 4);
                        }
                    }
                }
            }

            File.WriteAllText(resPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _config.ReloadAll();
        }
        catch { }
    }

    private void OnStartEngineClicked(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Deseja confirmar as configurações e ativar o motor de estabilização em segundo plano?\n\n(A interface será minimizada e poderá ser reaberta com a tecla F6)",
            "Confirmação",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        // Apply bigslider if changed
        int calib = (int)BigSlider.Value;
        if (calib != _config.GlobalConfig.MasterCalibration)
        {
            _config.ApplyMasterCalibration(calib);
        }

        // Apply options
        _config.GlobalConfig.SecondarySlotEnabled = CheckSlot2.IsChecked == true;
        _config.GlobalConfig.DmrsEnabled = CheckDmrs.IsChecked == true;
        _config.GlobalConfig.InvertSlots = CheckInvertSlots.IsChecked == true;
        _config.GlobalConfig.SoundsEnabled = CheckSounds.IsChecked == true;
        _config.SaveGlobalConfig();

        StartOrRestartEngine();

        Hide();
    }

    private void StartOrRestartEngine()
    {
        if (_engine != null)
        {
            _engine.Stop();
            _engine.Dispose();
            _engine = null;
        }

        _engine = new StabilizationEngine(_config)
        {
            OnRestoreUIRequested = () =>
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                });
            },
            OnShutdownRequested = () =>
            {
                Dispatcher.Invoke(() =>
                {
                    SaveUiState();
                    Application.Current.Shutdown();
                });
            }
        };

        _engine.Start();

        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
        StatusLabel.Text = "Motor: Ativo";
    }

    private void SaveUiState()
    {
        try
        {
            string statePath = Path.Combine(_config.BaseDirectory, "Configs", "geral", "ui_state.json");
            var state = new
            {
                pagina = TabWpBtn.IsChecked == true ? "wp" : "main",
                ultima_arma = _currentProfile
            };
            File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void RestoreLastUiState()
    {
        try
        {
            string statePath = Path.Combine(_config.BaseDirectory, "Configs", "geral", "ui_state.json");
            if (!File.Exists(statePath)) return;

            string json = File.ReadAllText(statePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("pagina", out var p) && p.GetString() == "wp")
            {
                TabWpBtn.IsChecked = true;
            }

            if (root.TryGetProperty("ultima_arma", out var ua))
            {
                string? arma = ua.GetString();
                if (!string.IsNullOrEmpty(arma) && ProfileNames.Contains(arma))
                {
                    ProfileListBox.SelectedItem = arma;
                }
            }
        }
        catch { }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveUiState();
        MessageBox.Show(
            "Depois de fechar essa interface, aperte F10 se quiser encerrar o motor completamente!",
            "Atenção",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    #region Visão & Telemetria Handlers

    private void InitializeVisionControls()
    {
        // Localiza modelo pubg_trained.onnx
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidatePaths = {
            Path.Combine(basePath, "models", "pubg_trained.onnx"),
            Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "..", "models", "pubg_trained.onnx")),
            Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "models", "pubg_trained.onnx")),
            Path.GetFullPath("models/pubg_trained.onnx")
        };

        foreach (var p in candidatePaths)
        {
            if (File.Exists(p))
            {
                _modelPath = p;
                break;
            }
        }

        if (string.IsNullOrEmpty(_modelPath) && File.Exists("models/pubg_trained.onnx"))
        {
            _modelPath = Path.GetFullPath("models/pubg_trained.onnx");
        }

        if (File.Exists(_modelPath))
        {
            ModelStatusBadge.Text = "● ONNX Conectado";
            ModelFileLabel.Text = Path.GetFileName(_modelPath);
            LogVision($"[SISTEMA] Modelo neural carregado: {_modelPath}");
        }
        else
        {
            ModelStatusBadge.Text = "○ Modelo ausente";
            ModelStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            ModelFileLabel.Text = "pubg_trained.onnx não encontrado";
            LogVision("[AVISO] Arquivo de modelo pubg_trained.onnx não foi encontrado na pasta models/.");
        }

        PopulateVideoList();
    }

    private void PopulateVideoList()
    {
        string defaultDir = @"C:\Users\samue\Videos\NVIDIAPLAYERUNKNOWN'S BATTLEGROUNDS";
        VideoClipsCombo.Items.Clear();

        if (Directory.Exists(defaultDir))
        {
            var files = Directory.GetFiles(defaultDir, "*.mp4")
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            foreach (var f in files)
            {
                VideoClipsCombo.Items.Add(f);
            }

            if (VideoClipsCombo.Items.Count > 0)
            {
                VideoClipsCombo.SelectedIndex = 0;
                AuditProgressStatus.Text = $"{VideoClipsCombo.Items.Count} gravações encontradas.";
                LogVision($"[SISTEMA] {VideoClipsCombo.Items.Count} clipes de combate carregados da pasta NVIDIA.");
            }
        }
        else
        {
            AuditProgressStatus.Text = "Pasta padrão de vídeos não encontrada. Use 'Procurar Outro...'.";
        }
    }

    private void OnVisionConfidenceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VisionConfidenceLabel == null) return;
        _visionConfidence = (float)(e.NewValue / 100.0);
        VisionConfidenceLabel.Text = $"{e.NewValue:F0}%";
    }

    private void OnVisionNmsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VisionNmsLabel == null) return;
        _visionNms = (float)(e.NewValue / 100.0);
        VisionNmsLabel.Text = $"{e.NewValue:F0}%";
    }

    private void OnBrowseVideoClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecionar Gravação de Combate PUBG",
            Filter = "Vídeos (*.mp4;*.avi;*.mkv)|*.mp4;*.avi;*.mkv|Todos os Arquivos (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            if (!VideoClipsCombo.Items.Contains(dialog.FileName))
            {
                VideoClipsCombo.Items.Insert(0, dialog.FileName);
            }
            VideoClipsCombo.SelectedItem = dialog.FileName;
            AuditProgressStatus.Text = $"Selecionado: {Path.GetFileName(dialog.FileName)}";
            LogVision($"[ARQUIVO] Vídeo selecionado: {dialog.FileName}");
        }
    }

    private async void OnStartAuditClicked(object sender, RoutedEventArgs e)
    {
        string? videoPath = VideoClipsCombo.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            MessageBox.Show("Selecione um arquivo de vídeo válido para auditar.", "Vídeo Inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
        {
            MessageBox.Show($"O modelo pubg_trained.onnx não foi encontrado em:\n{_modelPath}\n\nVerifique o arquivo na pasta models/", "Modelo ausente", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        StartAuditBtn.IsEnabled = false;
        CancelAuditBtn.IsEnabled = true;
        OpenOutputDirBtn.IsEnabled = false;
        AuditProgressBar.Value = 0;
        AuditProgressPercent.Text = "0%";

        string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "auditorias");
        Directory.CreateDirectory(outputDir);
        string outputFileName = $"audit_{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileNameWithoutExtension(videoPath)}.avi";
        string outputVideoPath = Path.Combine(outputDir, outputFileName);
        _lastGeneratedVideoPath = outputVideoPath;

        _auditCts = new CancellationTokenSource();
        LogVision($"==================================================");
        LogVision($"[INICIANDO AUDITORIA] {Path.GetFileName(videoPath)}");
        LogVision($"[CONFIG] Limiar Confiança: {_visionConfidence:P0} | NMS IoU: {_visionNms:P0}");
        LogVision($"[DESTINO] {outputVideoPath}");
        AuditProgressStatus.Text = "Inicializando rede neural ONNX...";

        try
        {
            var detector = new OnnxTargetDetector(
                _modelPath,
                confidenceThreshold: _visionConfidence,
                iouThreshold: _visionNms,
                allowedClassIds: new[] { 0, 1 });

            var config = new VisionPipelineConfig
            {
                MinimumConfidence = _visionConfidence,
                UseRoi = false
            };

            var annotator = new VideoAnnotatorService(detector, config);

            var progress = new Progress<VideoAnnotatorProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    AuditProgressBar.Value = p.ProgressPercentage * 100.0;
                    AuditProgressPercent.Text = $"{p.ProgressPercentage * 100.0:F0}%";
                    AuditProgressStatus.Text = $"Processando: {p.ProcessedFrames}/{p.TotalFrames} frames | {p.CurrentFps:F1} FPS";

                    MetricLatencyLabel.Text = $"{p.AverageLatencyMs:F1} ms";
                    MetricFpsLabel.Text = $"{p.CurrentFps:F1} FPS";
                    MetricTargetsLabel.Text = $"{p.TotalDetections}";
                    MetricHeadRatioLabel.Text = $"{p.BodyDetections} / {p.HeadDetections}";
                });
            });

            var result = await Task.Run(() => annotator.RenderAnnotatedVideoDetailedAsync(
                videoPath,
                outputVideoPath,
                maxFrames: 450, // Processa até ~15s de clipe
                progress: progress,
                cancellationToken: _auditCts.Token));

            LogVision($"[CONCLUÍDO] {result.ProcessedFrames} quadros renderizados em {result.ElapsedTime.TotalSeconds:F1} segundos.");
            LogVision($"[TELEMETRIA] FPS Médio: {result.AverageFps:F1} | Latência Média: {result.AverageLatencyMs:F1} ms");
            LogVision($"[DETECÇÕES] Total: {result.TotalDetections} alvos (Corpo: {result.BodyDetections}, Cabeça: {result.HeadDetections})");
            LogVision($"[ARQUIVO SALVO] {result.OutputPath}");

            AuditProgressStatus.Text = $"Auditoria concluída com sucesso! ({result.ProcessedFrames} quadros anotados)";
            AuditProgressBar.Value = 100;
            AuditProgressPercent.Text = "100%";
            OpenOutputDirBtn.IsEnabled = true;

            if (_config.GlobalConfig.SoundsEnabled)
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        }
        catch (OperationCanceledException)
        {
            LogVision("[CANCELADO] Auditoria interrompida pelo usuário.");
            AuditProgressStatus.Text = "Processamento cancelado.";
        }
        catch (Exception ex)
        {
            LogVision($"[FALHA] Erro durante o processamento: {ex.Message}");
            AuditProgressStatus.Text = "Erro na execução da auditoria.";
            MessageBox.Show($"Erro durante a auditoria do vídeo:\n{ex.Message}", "Erro de Visão", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartAuditBtn.IsEnabled = true;
            CancelAuditBtn.IsEnabled = false;
            _auditCts?.Dispose();
            _auditCts = null;
        }
    }

    private void OnCancelAuditClicked(object sender, RoutedEventArgs e)
    {
        _auditCts?.Cancel();
    }

    private void OnOpenOutputDirClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastGeneratedVideoPath) && File.Exists(_lastGeneratedVideoPath))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastGeneratedVideoPath}\"");
        }
        else
        {
            string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "auditorias");
            if (Directory.Exists(outputDir))
            {
                System.Diagnostics.Process.Start("explorer.exe", outputDir);
            }
        }
    }

    private void LogVision(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        AuditLogBox.AppendText($"[{timestamp}] {message}\r\n");
        AuditLogBox.ScrollToEnd();
    }

    #endregion
}