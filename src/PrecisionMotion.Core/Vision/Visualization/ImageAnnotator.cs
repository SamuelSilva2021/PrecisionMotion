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

        // 1. Desenha região ROI se fornecida (Campo de alta resolução)
        if (roiRect.HasValue)
        {
            Cv2.Rectangle(canvas, roiRect.Value, RoiColor, 2, LineTypes.Link8);
            Cv2.PutText(canvas, "CAMPO DE VISAO IA (ROI 1:1)", new Point(roiRect.Value.X + 8, roiRect.Value.Y + 22),
                HersheyFonts.HersheySimplex, 0.50, RoiColor, 1);
        }

        // 2. Desenha caixas detectadas
        foreach (var target in targets)
        {
            var box = target.Box;
            var rect = new Rect((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
            Cv2.Rectangle(canvas, rect, BoxColor, 2);

            string classLabel = target.ClassId == 1 ? "CABECA" : "CORPO";
            string label = $"{classLabel} {target.Confidence:P0}";

            // Fundo escuro atrás do texto para legibilidade máxima
            int textY = Math.Max(22, (int)box.Y - 6);
            Cv2.Rectangle(canvas, new Rect((int)box.X, textY - 16, 120, 18), new Scalar(0, 0, 0), -1);
            Cv2.PutText(canvas, label, new Point((int)box.X + 4, textY - 3),
                HersheyFonts.HersheySimplex, 0.45, BoxColor, 1);
        }

        // 3. Desenha ponto da cabeça estimado
        if (estimatedHead.HasValue)
        {
            var headPt = new Point((int)estimatedHead.Value.X, (int)estimatedHead.Value.Y);
            Cv2.Circle(canvas, headPt, 4, HeadColor, -1);
            Cv2.Circle(canvas, headPt, 8, HeadColor, 2);
        }

        // 4. Desenha retículo de referência (mira central)
        if (referencePoint.HasValue)
        {
            int rx = (int)referencePoint.Value.X;
            int ry = (int)referencePoint.Value.Y;
            Cv2.Circle(canvas, new Point(rx, ry), 5, CrosshairColor, 1);
            Cv2.Line(canvas, new Point(rx - 15, ry), new Point(rx + 15, ry), CrosshairColor, 2);
            Cv2.Line(canvas, new Point(rx, ry - 15), new Point(rx, ry + 15), CrosshairColor, 2);

            // 5. Desenha Linha de Rastreamento (Vetor da Mira até o Alvo)
            if (estimatedHead.HasValue)
            {
                var headPt = new Point((int)estimatedHead.Value.X, (int)estimatedHead.Value.Y);
                Cv2.Line(canvas, new Point(rx, ry), headPt, new Scalar(0, 255, 255), 2, LineTypes.AntiAlias);

                int dX = headPt.X - rx;
                int dY = headPt.Y - ry;
                string deltaMsg = $"Delta: dX={dX:+0;-0;0} dY={dY:+0;-0;0}";
                int midX = (rx + headPt.X) / 2;
                int midY = (ry + headPt.Y) / 2 - 8;
                Cv2.PutText(canvas, deltaMsg, new Point(midX, midY), HersheyFonts.HersheySimplex, 0.45, new Scalar(0, 255, 255), 1);
            }
        }

        // 6. Desenha banners de status e telemetria (posicionados abaixo da barra de janela do player)
        string statusText;
        Scalar statusBg;
        if (targets.Count > 0 && estimatedHead.HasValue)
        {
            var best = targets[0];
            string cls = best.ClassId == 1 ? "CABECA" : "CORPO";
            int dX = referencePoint.HasValue ? (int)(estimatedHead.Value.X - referencePoint.Value.X) : 0;
            int dY = referencePoint.HasValue ? (int)(estimatedHead.Value.Y - referencePoint.Value.Y) : 0;
            statusText = $"[ALVO TRAVADO] {targets.Count} Alvo(s) | {cls}: {best.Confidence:P0} | Delta: ({dX:+0;-0}, {dY:+0;-0})";
            statusBg = new Scalar(0, 120, 0); // Verde vibrante
        }
        else
        {
            statusText = "[BUSCANDO] Nenhum alvo detectado no campo de visao.";
            statusBg = new Scalar(35, 35, 35); // Cinza neutro
        }

        // Banner Status
        Cv2.Rectangle(canvas, new Rect(15, 45, 540, 26), statusBg, -1);
        Cv2.Rectangle(canvas, new Rect(15, 45, 540, 26), new Scalar(120, 120, 120), 1);
        Cv2.PutText(canvas, statusText, new Point(22, 63), HersheyFonts.HersheySimplex, 0.45, TextColor, 1);

        // Banner Telemetria
        if (metrics.HasValue)
        {
            var m = metrics.Value;
            string telemetry = $"FPS: {m.FramesPerSecond:F1} | Infer: {m.InferenceLatencyMs:F1}ms | Proc: {m.ProcessingLatencyMs:F2}ms | Total: {m.TotalLatencyMs:F1}ms";
            Cv2.Rectangle(canvas, new Rect(15, 75, 540, 24), new Scalar(0, 0, 0), -1);
            Cv2.Rectangle(canvas, new Rect(15, 75, 540, 24), new Scalar(80, 80, 80), 1);
            Cv2.PutText(canvas, telemetry, new Point(22, 92), HersheyFonts.HersheySimplex, 0.42, TextColor, 1);
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
