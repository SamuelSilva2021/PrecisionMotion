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

    [Fact(Skip = "Extração manual sob demanda para não sobrescrever o dataset de treino existente")]
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
        if (Directory.Exists(outputDatasetDir))
        {
            Directory.Delete(outputDatasetDir, true);
        }
        _output.WriteLine($"Iniciando extração limpa e focada em combate para: {outputDatasetDir}");

        using var detector = new OnnxTargetDetector(
            modelPath: modelPath,
            modelWidth: 640,
            modelHeight: 640,
            confidenceThreshold: 0.45f,
            allowedClassIds: new[] { 0 });

        var config = new VisionPipelineConfig
        {
            UseRoi = true,
            RoiWidth = 640,
            RoiHeight = 640,
            MinimumConfidence = 0.45f
        };

        var extractor = new DatasetExtractor(detector, config);
        int totalSaved = 0;
        int maxTotalFrames = 60; // 60 frames de alta precisão

        foreach (var video in videoFiles)
        {
            if (totalSaved >= maxTotalFrames) break;

            _output.WriteLine($"Processando combate em: {Path.GetFileName(video)}...");

            int remaining = maxTotalFrames - totalSaved;
            int savedFromVideo = await extractor.ExtractDatasetFromVideoAsync(
                videoPath: video,
                outputDatasetDir: outputDatasetDir,
                sampleIntervalFrames: 6,
                onlySaveFramesWithDetections: true,
                maxFramesToSave: remaining,
                lastSecondsOnly: 15,
                excludePlayerWeaponZone: true,
                generateHeadAnnotations: true);

            totalSaved += savedFromVideo;
            _output.WriteLine($"  -> Extraídos {savedFromVideo} quadros reais de combate. (Total acumulado: {totalSaved})");
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

    [Fact]
    public async Task Benchmark_CustomPubgModel_OnRealVideo()
    {
        string solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string modelPath = Path.Combine(solutionRoot, "models", "pubg_trained.onnx");

        if (!File.Exists(modelPath))
        {
            _output.WriteLine($"Modelo treinado não encontrado em: {modelPath}. Pulando teste.");
            return;
        }

        string videoDir = @"C:\Users\samue\Videos\NVIDIAPLAYERUNKNOWN'S BATTLEGROUNDS";
        string? videoPath = Directory.GetFiles(videoDir, "*.mp4").FirstOrDefault();

        if (string.IsNullOrEmpty(videoPath))
        {
            _output.WriteLine("Nenhum vídeo .mp4 encontrado.");
            return;
        }

        _output.WriteLine($"Iniciando Benchmark com Modelo Customizado PUBG: {Path.GetFileName(videoPath)}");
        _output.WriteLine("Modelo: pubg_trained.onnx (YOLO26s treinado com precisão 91.7%)");

        using var videoSource = new VideoFileFrameSource(videoPath, loop: false);

        // O modelo treinado possui 2 classes: 0 = body, 1 = head
        using var detector = new OnnxTargetDetector(
            modelPath: modelPath,
            modelWidth: 640,
            modelHeight: 640,
            confidenceThreshold: 0.35f,
            iouThreshold: 0.45f); // Detecta todas as classes treinadas

        var config = new VisionPipelineConfig
        {
            FrameWidth = videoSource.Width,
            FrameHeight = videoSource.Height,
            ReferencePoint = (videoSource.Width / 2f, videoSource.Height / 2f),
            UseRoi = true,
            RoiWidth = 640,
            RoiHeight = 640,
            MinimumConfidence = 0.35f
        };

        var selector = new ClosestToCrosshairSelector();
        var smoother = new ExponentialMovingAverageSmoother(0.45f, 0.25);
        var calculator = new CoordinateCalculator(config.FrameWidth, config.FrameHeight);

        await using var pipeline = new VisionPipeline(videoSource, detector, selector, smoother, calculator, config);

        int totalProcessed = 0;
        int detectedCount = 0;
        double totalInferMs = 0;

        using var cts = new CancellationTokenSource();
        int maxFrames = 100;

        for (int frameIdx = 0; frameIdx < maxFrames; frameIdx++)
        {
            var result = await pipeline.ProcessSingleFrameAsync(cts.Token);
            if (result.Metrics.TotalLatencyMs <= 0 && result.SelectedTarget == null) break;

            totalProcessed++;
            totalInferMs += result.Metrics.InferenceLatencyMs;

            if (result.SelectedTarget.HasValue)
            {
                detectedCount++;
                var t = result.SelectedTarget.Value;
                string className = t.Target.ClassId == 1 ? "HEAD" : "BODY";
                _output.WriteLine($"[Frame {frameIdx:D3}] [{className}] Confiança: {t.Target.Confidence:P0} | Posição: ({t.EstimatedHead.X:F1}, {t.EstimatedHead.Y:F1}) | Inferência: {result.Metrics.InferenceLatencyMs:F1}ms");
            }
        }

        double avgInferMs = totalProcessed > 0 ? totalInferMs / totalProcessed : 0;
        double fps = avgInferMs > 0 ? 1000.0 / avgInferMs : 0;

        _output.WriteLine("=================================================");
        _output.WriteLine($"Total de Quadros Analisados: {totalProcessed}");
        _output.WriteLine($"Detecções com Modelo Customizado: {detectedCount}");
        _output.WriteLine($"Latência Média: {avgInferMs:F2} ms");
        _output.WriteLine($"Throughput Estimado: {fps:F1} FPS");
        _output.WriteLine("=================================================");

        totalProcessed.Should().BeGreaterThan(0);
    }

    [Fact(Skip = "Diagnóstico específico de clipe sob demanda")]
    public async Task Diagnose_TrainingGroundVideo()
    {
        string videoPath = @"C:\Users\samue\Downloads\Video Project 8.mp4";
        if (!File.Exists(videoPath)) return;

        string solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string customModelPath = Path.Combine(solutionRoot, "models", "pubg_trained.onnx");
        string cocoModelPath = Path.Combine(solutionRoot, "models", "yolo11n.onnx");

        using var src = new VideoFileFrameSource(videoPath, loop: false);
        _output.WriteLine($"Video: {src.Width}x{src.Height} @ {src.Fps} FPS, Total Frames: {src.FrameCount}");

        using var detCustom = new OnnxTargetDetector(customModelPath, confidenceThreshold: 0.05f, iouThreshold: 0.45f, allowedClassIds: new[] { 0, 1 });
        using var detCoco = File.Exists(cocoModelPath) 
            ? new OnnxTargetDetector(cocoModelPath, confidenceThreshold: 0.05f, iouThreshold: 0.45f, allowedClassIds: new[] { 0 }) 
            : null;

        int w = src.Width;
        int h = src.Height;
        byte[] frameBuf = new byte[w * h * 3];

        // Avança até o segundo 2 (~frame 60) onde a mira está travada no alvo
        for (int i = 0; i < 60; i++)
        {
            if (!src.TryGetNextFrame(frameBuf, out _, out _, CancellationToken.None)) break;
        }

        // 1. Testa ROI 640x640 centrada
        var roi = RoiTransformer.CreateCentered(w, h, 640, 640);
        byte[] roiBuf = new byte[640 * 640 * 3];
        roi.ExtractRoi(frameBuf, w, h, 3, roiBuf);

        // Test with confidence 0.01 (1%)
        using var det1Pct = new OnnxTargetDetector(customModelPath, confidenceThreshold: 0.01f, iouThreshold: 0.45f, allowedClassIds: new[] { 0, 1 });
        var customRoi = await det1Pct.DetectAsync(new ReadOnlyMemory<byte>(roiBuf), 640, 640, CancellationToken.None);
        _output.WriteLine($"Frame 60 ROI [Custom Model (threshold 0.01)]: {customRoi.Count} detections");
        foreach (var d in customRoi.Take(10))
            _output.WriteLine($"   Class={d.ClassId}, Conf={d.Confidence:P2}, Box=({d.Box.X:F0},{d.Box.Y:F0},{d.Box.Width:F0},{d.Box.Height:F0})");

        if (detCoco != null)
        {
            var config = new VisionPipelineConfig
            {
                MinimumConfidence = 0.15f,
                UseRoi = true,
                RoiWidth = 640,
                RoiHeight = 640
            };

            using var det = new OnnxTargetDetector(cocoModelPath, confidenceThreshold: 0.15f, allowedClassIds: new[] { 0 });
            var service = new VideoAnnotatorService(det, config);
            string testOut = Path.Combine(solutionRoot, "auditoria_teste_campo_treino.avi");

            var result = await service.RenderAnnotatedVideoDetailedAsync(videoPath, testOut, maxFrames: 100);
            _output.WriteLine($"Rendered {result.ProcessedFrames} frames. Total Detections: {result.TotalDetections}");
            File.Exists(testOut).Should().BeTrue();
            result.TotalDetections.Should().BeGreaterThan(0);
        }
    }
}
