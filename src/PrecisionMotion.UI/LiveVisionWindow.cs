using System.Buffers;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PrecisionMotion.Core.Vision.Detection;
using Microsoft.ML.OnnxRuntime;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.UI;

public sealed class LiveVisionWindow : Window
{
    private readonly string _modelPath;
    private readonly float _threshold;
    private readonly float _nms;
    private readonly TextBlock _status;
    private readonly bool _useCuda;
    private readonly Image _preview;
    private readonly Canvas _annotations;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public LiveVisionWindow(string modelPath, float threshold, float nms, bool useCuda = false)
    {
        _modelPath = modelPath;
        _threshold = threshold;
        _nms = nms;
        _useCuda = useCuda;
        Title = "PrecisionMotion - Diagnóstico ao vivo (somente leitura)";
        Width = 1000;
        Height = 720;
        Background = Brushes.Black;

        var root = new DockPanel();
        Content = root;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var stop = new Button { Content = "Parar", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 12, 0) };
        stop.Click += (_, _) => _cts?.Cancel();
        header.Children.Add(stop);

        _status = new TextBlock { Text = "Iniciando captura...", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(_status);

        var viewbox = new Viewbox { Stretch = Stretch.Uniform };
        root.Children.Add(viewbox);
        var layer = new Grid();
        viewbox.Child = layer;
        _preview = new Image { Stretch = Stretch.Fill };
        _annotations = new Canvas { IsHitTestVisible = false };
        layer.Children.Add(_preview);
        layer.Children.Add(_annotations);

        Loaded += (_, _) => Start();
        Closed += (_, _) => _cts?.Cancel();
    }

    private void Start()
    {
        if (_worker is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var source = new DesktopFrameSource();
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            string provider = "CPU";
            string? cudaFailure = null;
            if (_useCuda)
            {
                try
                {
                    options.AppendExecutionProvider_CUDA(0);
                    provider = "CUDA";
                }
                catch (Exception ex)
                {
                    cudaFailure = ex.ToString();
                    WriteCudaDiagnostic(cudaFailure);
                }
            }

            OnnxTargetDetector detector;
            try
            {
                detector = new OnnxTargetDetector(_modelPath, confidenceThreshold: _threshold, iouThreshold: _nms,
                    allowedClassIds: System.IO.Path.GetFileName(_modelPath).Equals("yolo11n.onnx", StringComparison.OrdinalIgnoreCase) ? new[] { 0 } : null,
                    options: options);
            }
            catch (Exception ex) when (_useCuda && provider == "CUDA")
            {
                provider = "CPU";
                cudaFailure = ex.ToString();
                WriteCudaDiagnostic(cudaFailure);
                detector = new OnnxTargetDetector(_modelPath, confidenceThreshold: _threshold, iouThreshold: _nms,
                    allowedClassIds: System.IO.Path.GetFileName(_modelPath).Equals("yolo11n.onnx", StringComparison.OrdinalIgnoreCase) ? new[] { 0 } : null);
            }
            string providerStatus = cudaFailure is null ? provider : "CPU (falha CUDA; consulte cuda-diagnostic.log)";
            using (detector)
            {

            int width = source.Width;
            int height = source.Height;
            int byteCount = checked(width * height * 3);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            var watch = Stopwatch.StartNew();
            int frames = 0;

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    layerSize(width, height);
                    _preview.Source = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
                });

                while (!token.IsCancellationRequested)
                {
                    long started = Stopwatch.GetTimestamp();
                    if (!source.TryGetNextFrame(buffer, out _, out _, token)) break;
                    var targets = await detector.DetectAsync(buffer.AsMemory(0, byteCount), width, height, token);
                    double latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    frames++;
                    double fps = frames / Math.Max(0.001, watch.Elapsed.TotalSeconds);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_preview.Source is WriteableBitmap bitmap)
                            bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, width * 3, 0);

                        _annotations.Children.Clear();
                        foreach (var target in targets)
                        {
                            var box = target.Box;
                            var rect = new System.Windows.Shapes.Rectangle
                            {
                                Width = Math.Max(1, box.Width),
                                Height = Math.Max(1, box.Height),
                                Stroke = Brushes.Lime,
                                StrokeThickness = 2
                            };
                            Canvas.SetLeft(rect, box.X);
                            Canvas.SetTop(rect, box.Y);
                            _annotations.Children.Add(rect);

                            var label = new TextBlock
                            {
                                Text = $"Classe {target.ClassId}  {target.Confidence:P0}",
                                Foreground = Brushes.White,
                                Background = Brushes.DarkGreen,
                                FontSize = 16
                            };
                            Canvas.SetLeft(label, box.X);
                            Canvas.SetTop(label, Math.Max(0, box.Y - 22));
                            _annotations.Children.Add(label);
                        }
                        _status.Text = $"Provider: {providerStatus} | FPS: {fps:F1} | Latência total: {latency:F1} ms | Detecções: {targets.Count} | {System.IO.Path.GetFileName(_modelPath)}";
                    });
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => _status.Text = $"Erro: {ex.Message}");
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => _status.Text += " | Captura encerrada");
        }
    }

    private static void WriteCudaDiagnostic(string error)
    {
        try
        {
            string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrecisionMotion");
            System.IO.Directory.CreateDirectory(directory);
            string file = System.IO.Path.Combine(directory, "cuda-diagnostic.log");
            System.IO.File.AppendAllText(file, $"[{DateTimeOffset.Now:O}] CUDA initialization failed:{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to write CUDA diagnostic: {ex}");
        }
    }

    private void layerSize(int width, int height)
    {
        _preview.Width = width;
        _preview.Height = height;
        _annotations.Width = width;
        _annotations.Height = height;
    }
}
