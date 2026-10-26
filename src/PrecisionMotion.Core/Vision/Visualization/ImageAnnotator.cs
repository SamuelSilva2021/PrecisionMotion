using OpenCvSharp;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Visualization;

public static class ImageAnnotator
{
    private static readonly Scalar BoxColor = new(0, 255, 0);       // Verde BGR
    private static readonly Scalar HeadColor = new(0, 0, 255);      // Vermelho BGR
    private static readonly Scalar CrosshairColor = new(255, 255, 0);// Ciano BGR
    private static readonly Scalar RoiColor = new(255, 128, 0);     // Laranja BGR
    private static readonly Scalar TextColor = new(255, 255, 255);  // Branco BGR

    public static Mat AnnotateFrame(
        Mat frame,
        IReadOnlyList<DetectedTarget> targets,
        (float X, float Y)? estimatedHead = null,
        (float X, float Y)? referencePoint = null,
        VisionMetrics? metrics = null,
        Rect? roiRect = null)
    {
        var canvas = frame.Clone();

        // 1. Desenha região ROI se fornecida
        if (roiRect.HasValue)
        {
            Cv2.Rectangle(canvas, roiRect.Value, RoiColor, 1, LineTypes.Link8);
            Cv2.PutText(canvas, "ROI 1:1", new Point(roiRect.Value.X + 5, roiRect.Value.Y + 15),
                HersheyFonts.HersheySimplex, 0.45, RoiColor, 1);
        }

        // 2. Desenha caixas detectadas
        foreach (var target in targets)
        {
            var box = target.Box;
            var rect = new Rect((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
            Cv2.Rectangle(canvas, rect, BoxColor, 2);

            string label = $"ID:{target.ClassId} {target.Confidence:P0}";
            Cv2.PutText(canvas, label, new Point((int)box.X, Math.Max(15, (int)box.Y - 5)),
                HersheyFonts.HersheySimplex, 0.45, BoxColor, 1);
        }

        // 3. Desenha ponto da cabeça estimado
        if (estimatedHead.HasValue)
        {
            var headPt = new Point((int)estimatedHead.Value.X, (int)estimatedHead.Value.Y);
            Cv2.Circle(canvas, headPt, 4, HeadColor, -1);
            Cv2.Circle(canvas, headPt, 7, HeadColor, 1);
        }

        // 4. Desenha retículo de referência (mira)
        if (referencePoint.HasValue)
        {
            int rx = (int)referencePoint.Value.X;
            int ry = (int)referencePoint.Value.Y;
            Cv2.Line(canvas, new Point(rx - 10, ry), new Point(rx + 10, ry), CrosshairColor, 1);
            Cv2.Line(canvas, new Point(rx, ry - 10), new Point(rx, ry + 10), CrosshairColor, 1);
        }

        // 5. Desenha banner de telemetria
        if (metrics.HasValue)
        {
            var m = metrics.Value;
            string telemetry = $"FPS: {m.FramesPerSecond:F1} | Infer: {m.InferenceLatencyMs:F1}ms | Proc: {m.ProcessingLatencyMs:F2}ms | Total: {m.TotalLatencyMs:F1}ms";

            Cv2.Rectangle(canvas, new Rect(10, 10, 480, 26), new Scalar(0, 0, 0), -1);
            Cv2.PutText(canvas, telemetry, new Point(15, 28),
                HersheyFonts.HersheySimplex, 0.45, TextColor, 1);
        }

        return canvas;
    }

    public static bool SaveAnnotatedImage(
        string sourceImagePath,
        string destinationPath,
        IReadOnlyList<DetectedTarget> targets,
        (float X, float Y)? estimatedHead = null,
        (float X, float Y)? referencePoint = null,
        VisionMetrics? metrics = null,
        Rect? roiRect = null)
    {
        using var original = Cv2.ImRead(sourceImagePath, ImreadModes.Color);
        if (original.Empty()) return false;

        using var annotated = AnnotateFrame(original, targets, estimatedHead, referencePoint, metrics, roiRect);

        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return Cv2.ImWrite(destinationPath, annotated);
    }
}
