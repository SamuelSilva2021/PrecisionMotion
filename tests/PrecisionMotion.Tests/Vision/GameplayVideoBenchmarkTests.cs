using System.Diagnostics;
using FluentAssertions;
using PrecisionMotion.Core.Vision.Detection;
using PrecisionMotion.Core.Vision.Evaluation;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Pipeline;
using PrecisionMotion.Core.Vision.Selection;
using PrecisionMotion.Core.Vision.Smoothing;
using PrecisionMotion.Core.Vision.Sources;
using PrecisionMotion.Core.Vision.Visualization;
using Xunit;
using Xunit.Abstractions;

namespace PrecisionMotion.Tests.Vision;

public class GameplayVideoBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public GameplayVideoBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Benchmark_GameplayHighlightVideo_WithYolo11nAndRoi()
    {
        // 1. Localiza o modelo YOLO11n
        string solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string modelPath = Path.Combine(solutionRoot, "models", "yolo11n.onnx");

        if (!File.Exists(modelPath))
        {
            _output.WriteLine($"Modelo ONNX não encontrado em: {modelPath}. Pulando benchmark.");
            return;
        }

        // 2. Localiza um vídeo de highlight gravado pelo usuário
        string videoDir = @"C:\Users\samue\Videos\NVIDIAPLAYERUNKNOWN'S BATTLEGROUNDS";
        if (!Directory.Exists(videoDir))
        {
            _output.WriteLine($"Diretório de vídeos não encontrado: {videoDir}. Pulando benchmark.");
            return;
        }

        string? videoPath = Directory.GetFiles(videoDir, "*.mp4").FirstOrDefault();
        if (string.IsNullOrEmpty(videoPath))
        {
            _output.WriteLine("Nenhum vídeo .mp4 encontrado no diretório. Pulando benchmark.");
            return;
        }

        _output.WriteLine($"Iniciando Benchmark no vídeo: {Path.GetFileName(videoPath)}");
        _output.WriteLine($"Modelo: YOLO11n (ONNX Runtime, filtro: pessoa [classe 0])");

        // 3. Inicializa fontes e detector (apenas classe 0 = person em COCO)
        using var videoSource = new VideoFileFrameSource(videoPath, loop: false);
        using var detector = new OnnxTargetDetector(
            modelPath: modelPath,
            modelWidth: 640,
            modelHeight: 640,
            confidenceThreshold: 0.35f,
            iouThreshold: 0.45f,
            allowedClassIds: new[] { 0 }); // Apenas pessoas

        var config = new VisionPipelineConfig
        {
            FrameWidth = videoSource.Width,
            FrameHeight = videoSource.Height,
            ReferencePoint = (videoSource.Width / 2f, videoSource.Height / 2f),
            UseRoi = true,
            RoiWidth = 640,
            RoiHeight = 640,
            HeadRatio = 0.16f,
            MinimumConfidence = 0.35f
        };

        var selector = new ClosestToCrosshairSelector();
        var smoother = new ExponentialMovingAverageSmoother(0.45f, 0.25);
        var calculator = new CoordinateCalculator(config.FrameWidth, config.FrameHeight, config.HeadRatio);

        await using var pipeline = new VisionPipeline(videoSource, detector, selector, smoother, calculator, config);

        string outputDir = Path.Combine(solutionRoot, "benchmark_output");
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, true);
        }
        Directory.CreateDirectory(outputDir);

        int totalProcessed = 0;
        int targetsDetected = 0;
        double totalInferMs = 0;

        using var cts = new CancellationTokenSource();
        int maxFramesToAnalyze = 100; // Analisa os primeiros 100 frames do clipe

        for (int frameIdx = 0; frameIdx < maxFramesToAnalyze; frameIdx++)
        {
            var result = await pipeline.ProcessSingleFrameAsync(cts.Token);
            if (result.Metrics.TotalLatencyMs <= 0 && result.SelectedTarget == null)
            {
                // Fim do vídeo
                break;
            }

            totalProcessed++;
            totalInferMs += result.Metrics.InferenceLatencyMs;

            if (result.SelectedTarget.HasValue)
            {
                targetsDetected++;
                var t = result.SelectedTarget.Value;
                _output.WriteLine($"[Frame {frameIdx:D3}] Alvo Detectado! Confiança: {t.Target.Confidence:P0} | Cabeça: ({t.EstimatedHead.X:F1}, {t.EstimatedHead.Y:F1}) | Inferência: {result.Metrics.InferenceLatencyMs:F1}ms");
            }
        }

        double avgInferMs = totalProcessed > 0 ? totalInferMs / totalProcessed : 0;
        double fps = avgInferMs > 0 ? 1000.0 / avgInferMs : 0;

        _output.WriteLine("========================================");
        _output.WriteLine($"Total de Quadros Analisados: {totalProcessed}");
        _output.WriteLine($"Quadros com Alvo Identificado: {targetsDetected}");
        _output.WriteLine($"Latência Média de Inferência: {avgInferMs:F2} ms");
        _output.WriteLine($"Throughput Estimado: {fps:F1} FPS");
        _output.WriteLine("========================================");

        totalProcessed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExtractDataset_FromUserHighlightVideos()
    {
        string solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string modelPath = Path.Combine(solutionRoot, "models", "yolo11n.onnx");

        if (!File.Exists(modelPath))
        {
            _output.WriteLine("Modelo ONNX não encontrado. Pulando extração.");
            return;
        }

        string videoDir = @"C:\Users\samue\Videos\NVIDIAPLAYERUNKNOWN'S BATTLEGROUNDS";
        if (!Directory.Exists(videoDir))
        {
            _output.WriteLine("Diretório de vídeos não encontrado. Pulando extração.");
            return;
        }

        var videoFiles = Directory.GetFiles(videoDir, "*.mp4");
        if (videoFiles.Length == 0)
        {
            _output.WriteLine("Nenhum arquivo de vídeo encontrado.");
            return;
        }

        string outputDatasetDir = Path.Combine(solutionRoot, "dataset_pubg");
        _output.WriteLine($"Iniciando extração do dataset para: {outputDatasetDir}");

        using var detector = new OnnxTargetDetector(
            modelPath: modelPath,
            modelWidth: 640,
            modelHeight: 640,
            confidenceThreshold: 0.35f,
            allowedClassIds: new[] { 0 });

        var config = new VisionPipelineConfig
        {
            UseRoi = true,
            RoiWidth = 640,
            RoiHeight = 640,
            MinimumConfidence = 0.35f
        };

        var extractor = new DatasetExtractor(detector, config);
        int totalSaved = 0;
        int maxTotalFrames = 100;

        foreach (var video in videoFiles)
        {
            if (totalSaved >= maxTotalFrames) break;

            _output.WriteLine($"Processando vídeo: {Path.GetFileName(video)}...");

            int remaining = maxTotalFrames - totalSaved;
            int savedFromVideo = await extractor.ExtractDatasetFromVideoAsync(
                videoPath: video,
                outputDatasetDir: outputDatasetDir,
                sampleIntervalFrames: 10,
                onlySaveFramesWithDetections: true,
                maxFramesToSave: remaining);

            totalSaved += savedFromVideo;
            _output.WriteLine($"  -> Extraídos {savedFromVideo} quadros deste vídeo. (Total acumulado: {totalSaved})");
        }

        _output.WriteLine("====================================================");
        _output.WriteLine($"Extração concluída com sucesso!");
        _output.WriteLine($"Total de Imagens e Rótulos salvos: {totalSaved}");
        _output.WriteLine($"Diretório: {outputDatasetDir}");
        _output.WriteLine($"Imagens: {Path.Combine(outputDatasetDir, "images", "train")}");
        _output.WriteLine($"Rótulos: {Path.Combine(outputDatasetDir, "labels", "train")}");
        _output.WriteLine($"Visualização Anotada: {Path.Combine(outputDatasetDir, "debug_annotated")}");
        _output.WriteLine("====================================================");

        totalSaved.Should().BeGreaterThan(0);
    }
}
