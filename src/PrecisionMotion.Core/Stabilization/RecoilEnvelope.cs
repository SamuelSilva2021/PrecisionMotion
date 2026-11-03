namespace PrecisionMotion.Core.Stabilization;

/// <summary>
/// Intensidade de recuo ao longo da rajada. Os valores de <c>pattern</c> da arma são distribuídos em janelas
/// iguais sobre a duração da rajada (<c>total_time</c>) e interpolados linearmente entre os centros, para a
/// velocidade não dar degraus. Antes do 1º centro vale o 1º valor; depois do último, o último.
/// A taxa nunca passa do nível do trecho final da rajada (o início não compensa mais que o trecho estável).
/// Padrões com valor negativo (sentido desconhecido) caem no pico constante, o comportamento anterior.
/// </summary>
public static class RecoilEnvelope
{
    public const double DefaultBurstSeconds = 2.975;

    /// <summary>Achatamento calibrado no campo de treino para a BERYL.</summary>
    public const double BerylFlatten = 0.5;

    /// <summary>Só a BERYL usa a curva temporal; as demais armas mantêm o pico constante.</summary>
    public static bool UsesTimeShape(string? profileName) =>
        string.Equals(profileName, "BERYL", StringComparison.OrdinalIgnoreCase);

    public static double Peak(IReadOnlyList<double> pattern) =>
        pattern.Count == 0 ? 1.0 : pattern.Max(Math.Abs);

    public static bool IsTimeShaped(IReadOnlyList<double> pattern) =>
        pattern.Count > 1 && pattern.All(v => v >= 0);

    /// <param name="flatten">0 = curva do pattern; 1 = pico constante. Valores intermediários aproximam a curva do pico.</param>
    public static double Rate(IReadOnlyList<double> pattern, IReadOnlyList<double> totalTime, double seconds, double flatten = 0.0)
    {
        if (!IsTimeShaped(pattern))
            return Peak(pattern);

        double peak = Peak(pattern);
        double f = Math.Clamp(flatten, 0.0, 1.0);
        double raw = Shape(pattern, totalTime, seconds);
        double sustained = pattern[^1] + (peak - pattern[^1]) * f;

        // O início da rajada não passa do nível do trecho estável: o recuo inicial já é coberto por esse nível.
        return Math.Min(raw + (peak - raw) * f, sustained);
    }

    private static double Shape(IReadOnlyList<double> pattern, IReadOnlyList<double> totalTime, double seconds)
    {
        double duration = totalTime.Count > 0 && totalTime[^1] > 0 ? totalTime[^1] : DefaultBurstSeconds;
        double window = duration / pattern.Count;
        double t = Math.Max(0.0, seconds);

        double firstCenter = window * 0.5;
        double lastCenter = window * (pattern.Count - 0.5);
        if (t <= firstCenter) return pattern[0];
        if (t >= lastCenter) return pattern[^1];

        int i = (int)((t - firstCenter) / window);
        double a = firstCenter + i * window;
        double fraction = (t - a) / window;
        return pattern[i] + (pattern[i + 1] - pattern[i]) * fraction;
    }
}
