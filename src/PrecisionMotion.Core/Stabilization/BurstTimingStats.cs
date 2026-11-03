namespace PrecisionMotion.Core.Stabilization;

/// <summary>
/// Mede uma rajada: tempo real entre passos do laço e movimento enviado contra o pedido. O passo assume um
/// tempo fixo, então um laço mais lento que o nominal entrega menos movimento do que a velocidade calculada.
/// </summary>
public sealed class BurstTimingStats
{
    private double _requestedPixels;

    public int Intervals { get; private set; }
    public double TotalSeconds { get; private set; }
    public double MaxIntervalSeconds { get; private set; }
    public long SentPixels { get; private set; }

    public double MeanIntervalSeconds => Intervals > 0 ? TotalSeconds / Intervals : 0.0;
    public double RequestedPxPerSec => TotalSeconds > 0 ? _requestedPixels / TotalSeconds : 0.0;
    public double ActualPxPerSec => TotalSeconds > 0 ? SentPixels / TotalSeconds : 0.0;
    public double DeliveredRatio => RequestedPxPerSec > 0 ? ActualPxPerSec / RequestedPxPerSec : 0.0;

    /// <param name="seconds">Tempo real até o próximo passo.</param>
    /// <param name="velocityPxPerSec">Velocidade pedida durante esse intervalo.</param>
    /// <param name="sentPixels">Pixels enviados no início desse intervalo.</param>
    public void AddInterval(double seconds, double velocityPxPerSec, int sentPixels)
    {
        Intervals++;
        TotalSeconds += seconds;
        MaxIntervalSeconds = Math.Max(MaxIntervalSeconds, seconds);
        SentPixels += sentPixels;
        _requestedPixels += velocityPxPerSec * seconds;
    }

    public void Reset()
    {
        _requestedPixels = 0.0;
        Intervals = 0;
        TotalSeconds = 0.0;
        MaxIntervalSeconds = 0.0;
        SentPixels = 0;
    }
}
