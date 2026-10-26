using System.Diagnostics;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Pipeline;
using PrecisionMotion.Core.Vision.Selection;
using PrecisionMotion.Core.Vision.Smoothing;
using PrecisionMotion.Core.Vision.Sources;

namespace PrecisionMotion.Core.Vision.Visualization;

public sealed class VideoAnnotatorService
{
    private readonly ITargetDetector _detector;
    private readonly VisionPipelineConfig _config;

    public VideoAnnotatorService(ITargetDetector detector, VisionPipelineConfig? config = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _config = config ?? VisionPipelineConfig.Default;
    }

    public async Task<int> RenderAnnotatedVideoAsync(
        string inputVideoPath,
        string outputVideoPath,
        int maxFrames = int.MaxValue,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputVideoPath))
        {
            throw new FileNotFoundException("Vídeo de entrada não encontrado", inputVideoPath);
        }

        using var videoSource = new VideoFileFrameSource(inputVideoPath, loop: false);

        int width = videoSource.Width;
        int height = videoSource.Height;
        double fps = videoSource.Fps > 0 ? videoSource.Fps : 30.0;
        int totalVideoFrames = videoSource.FrameCount;
        int framesToProcess = Math.Min(totalVideoFrames, maxFrames);

        string? outDir = Path.GetDirectoryName(outputVideoPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var fourcc = FourCC.FromString("MJPG"); // Compatibilidade universal
        using var writer = new VideoWriter(outputVideoPath, fourcc, fps, new Size(width, height));

        if (!writer.IsOpened())
        {
            throw new InvalidOperationException($"Não foi possível inicializar o VideoWriter para o arquivo: {outputVideoPath}");
        }

        var selector = new ClosestToCrosshairSelector();
        var smoother = new ExponentialMovingAverageSmoother(_config.SmoothingAlpha, _config.SmoothingTimeoutSeconds);
        var calculator = new CoordinateCalculator(_config.FrameWidth, _config.FrameHeight, _config.HeadRatio);

        await using var pipeline = new VisionPipeline(videoSource, _detector, selector, smoother, calculator, _config);

        int processedFrames = 0;
        byte[] frameBuffer = new byte[width * height * 3];

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        for (int i = 0; i < framesToProcess; i++)
        {
            if (cts.Token.IsCancellationRequested) break;

            var result = await pipeline.ProcessSingleFrameAsync(cts.Token).ConfigureAwait(false);
            if (result.Metrics.TotalLatencyMs <= 0 && result.SelectedTarget == null)
            {
                // Fim do vídeo
                break;
            }

            // Lê o frame atual correspondente para renderização
            // Cria imagem do frame para anotação
            using var frameMat = new Mat(height, width, MatType.CV_8UC3);

            var targetsToDraw = result.SelectedTarget.HasValue 
                ? new[] { result.SelectedTarget.Value.Target } 
                : Array.Empty<DetectedTarget>();

            var headToDraw = result.SmoothedPoint ?? result.SelectedTarget?.EstimatedHead;

            Rect? roiRect = _config.UseRoi 
                ? new Rect(
                    (int)(_config.ReferencePoint.X - (_config.RoiWidth / 2f)),
                    (int)(_config.ReferencePoint.Y - (_config.RoiHeight / 2f)),
                    _config.RoiWidth,
                    _config.RoiHeight)
                : null;

            // Renderiza sobre o canvas
            using var annotated = ImageAnnotator.AnnotateFrame(
                frameMat,
                targetsToDraw,
                headToDraw,
                _config.ReferencePoint,
                result.Metrics,
                roiRect);

            writer.Write(annotated);
            processedFrames++;

            if (framesToProcess > 0)
            {
                progress?.Report((double)processedFrames / framesToProcess);
            }
        }

        return processedFrames;
    }
}
