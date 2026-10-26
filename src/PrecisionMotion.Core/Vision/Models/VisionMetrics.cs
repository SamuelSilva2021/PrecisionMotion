namespace PrecisionMotion.Core.Vision.Models;

public readonly record struct VisionMetrics(
    double CaptureLatencyMs,
    double InferenceLatencyMs,
    double ProcessingLatencyMs,
    double TotalLatencyMs,
    double FramesPerSecond);
