using System.Globalization;
using System.Text;
using OpenCvSharp;
using PrecisionMotion.Core.Vision.Abstractions;
using PrecisionMotion.Core.Vision.Mathematics;
using PrecisionMotion.Core.Vision.Models;
using PrecisionMotion.Core.Vision.Pipeline;
using PrecisionMotion.Core.Vision.Sources;
using PrecisionMotion.Core.Vision.Visualization;

namespace PrecisionMotion.Core.Vision.Evaluation;

public sealed class DatasetExtractor
{
    private readonly ITargetDetector _detector;
    private readonly VisionPipelineConfig _config;

    public DatasetExtractor(ITargetDetector detector, VisionPipelineConfig? config = null)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _config = config ?? VisionPipelineConfig.Default;
    }

    public async Task<int> ExtractDatasetFromVideoAsync(
        string videoPath,
        string outputDatasetDir,
        int sampleIntervalFrames = 6,
        bool onlySaveFramesWithDetections = true,
        int maxFramesToSave = 100,
        int lastSecondsOnly = 12, // Analisa apenas os últimos 12 segundos onde o combate/kill acontece
        bool excludePlayerWeaponZone = true,
        bool generateHeadAnnotations = true,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Vídeo de entrada não encontrado", videoPath);
        }

        string imagesDir = Path.Combine(outputDatasetDir, "images", "train");
        string labelsDir = Path.Combine(outputDatasetDir, "labels", "train");
        string debugDir = Path.Combine(outputDatasetDir, "debug_annotated");

        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(labelsDir);
        Directory.CreateDirectory(debugDir);

        using var videoSource = new VideoFileFrameSource(videoPath, loop: false);

        int width = videoSource.Width;
        int height = videoSource.Height;
        double fps = videoSource.Fps > 0 ? videoSource.Fps : 60.0;
        int totalFrames = videoSource.FrameCount;

        // Foca a extração nos últimos segundos do clipe (momento da troca de tiro / kill)
        int combatStartFrame = 0;
        if (lastSecondsOnly > 0 && totalFrames > 0)
        {
            int framesInCombat = (int)(lastSecondsOnly * fps);
            combatStartFrame = Math.Max(0, totalFrames - framesInCombat);
        }

        int savedCount = 0;
        int currentFrame = 0;

        byte[] frameBuffer = new byte[width * height * 3];
        var roi = _config.UseRoi 
            ? RoiTransformer.CreateCentered(width, height, _config.RoiWidth, _config.RoiHeight) 
            : null;

        while (videoSource.TryGetNextFrame(frameBuffer, out int w, out int h, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentFrame++;

            // Pula o início do clipe (looting, corrida, veículos) e foca na eliminação
            if (currentFrame < combatStartFrame)
            {
                continue;
            }

            if ((currentFrame - combatStartFrame) % sampleIntervalFrames != 0)
            {
                continue;
            }

            // Executa detecção
            IReadOnlyList<DetectedTarget> detections;

            if (roi != null)
            {
                int roiBytes = roi.Bounds.Width * roi.Bounds.Height * 3;
                byte[] roiBuffer = new byte[roiBytes];
                roi.ExtractRoi(frameBuffer, width, height, 3, roiBuffer);

                var roiDetections = await _detector.DetectAsync(roiBuffer, roi.Bounds.Width, roi.Bounds.Height, cancellationToken).ConfigureAwait(false);
                var globalDetections = new List<DetectedTarget>(roiDetections.Count);
                for (int i = 0; i < roiDetections.Count; i++)
                {
                    globalDetections.Add(roi.ToGlobal(roiDetections[i]));
                }
                detections = globalDetections;
            }
            else
            {
                detections = await _detector.DetectAsync(frameBuffer, width, height, cancellationToken).ConfigureAwait(false);
            }

            // Filtra por confiança e remove alarmes falsos na própria arma do jogador (canto inferior direito)
            var validDetections = new List<DetectedTarget>();
            foreach (var d in detections)
            {
                if (d.Confidence < _config.MinimumConfidence) continue;

                if (excludePlayerWeaponZone)
                {
                    // Se a caixa está colada no fundo da tela à direita (região da própria arma/mãos)
                    bool isWeaponZone = (d.Box.Y + d.Box.Height > height * 0.72f) && (d.Box.X > width * 0.45f);
                    if (isWeaponZone) continue;
                }

                validDetections.Add(d);
            }

            if (onlySaveFramesWithDetections && validDetections.Count == 0)
            {
                continue;
            }

            string baseName = $"pubg_combat_f{currentFrame:D5}";
            string imgOutPath = Path.Combine(imagesDir, baseName + ".jpg");
            string labelOutPath = Path.Combine(labelsDir, baseName + ".txt");
            string debugOutPath = Path.Combine(debugDir, baseName + ".jpg");

            // 1. Salva imagem em alta resolução
            using (var mat = new Mat(height, width, MatType.CV_8UC3))
            {
                unsafe
                {
                    new ReadOnlySpan<byte>(frameBuffer).CopyTo(new Span<byte>(mat.DataPointer, frameBuffer.Length));
                }
                Cv2.ImWrite(imgOutPath, mat);

                // Salva cópia visual anotada para validação rápida humana
                var roiRect = roi != null ? new Rect(roi.Bounds.X, roi.Bounds.Y, roi.Bounds.Width, roi.Bounds.Height) : (Rect?)null;
                using var annotated = ImageAnnotator.AnnotateFrame(mat, validDetections, null, _config.ReferencePoint, null, roiRect);
                Cv2.ImWrite(debugOutPath, annotated);
            }

            // 2. Salva anotação no formato YOLO normalizado (0: body, 1: head)
            var sb = new StringBuilder();
            foreach (var det in validDetections)
            {
                var box = det.Box;
                float xCenter = (box.X + (box.Width / 2f)) / width;
                float yCenter = (box.Y + (box.Height / 2f)) / height;
                float wNorm = box.Width / width;
                float hNorm = box.Height / height;

                // Classe 0: Corpo do Alvo (Body)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1:F6} {2:F6} {3:F6} {4:F6}",
                    0, xCenter, yCenter, wNorm, hNorm));

                if (generateHeadAnnotations)
                {
                    // Classe 1: Cabeça (Head) - terço superior centralizado
                    float headW = (box.Width * 0.45f) / width;
                    float headH = (box.Height * 0.22f) / height;
                    float headYCenter = (box.Y + (box.Height * 0.12f)) / height;

                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1:F6} {2:F6} {3:F6} {4:F6}",
                        1, xCenter, headYCenter, headW, headH));
                }
            }

            await File.WriteAllTextAsync(labelOutPath, sb.ToString(), cancellationToken).ConfigureAwait(false);

            savedCount++;
            if (savedCount >= maxFramesToSave)
            {
                break;
            }
        }

        // 3. Gera arquivo data.yaml pronto para treino
        string yamlContent = GenerateDataYaml(outputDatasetDir, generateHeadAnnotations);
        await File.WriteAllTextAsync(Path.Combine(outputDatasetDir, "data.yaml"), yamlContent, cancellationToken).ConfigureAwait(false);

        return savedCount;
    }

    private static string GenerateDataYaml(string datasetRoot, bool hasHeadClass)
    {
        string classes = hasHeadClass 
            ? "  0: body\n  1: head\n" 
            : "  0: target\n";

        return $@"# Configuração do Dataset PUBG para YOLO (Treinamento / Fine-Tuning)
path: {datasetRoot.Replace('\\', '/')}
train: images/train
val: images/train

names:
{classes}";
    }
}
