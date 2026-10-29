using PrecisionMotion.Core.Models;

namespace PrecisionMotion.Core.Vision;

public enum HysteresisState
{
    /// <summary>Nenhuma proposta nova: leitura sem candidato claro.</summary>
    Idle,
    /// <summary>O estado atual foi reafirmado.</summary>
    Held,
    /// <summary>Candidato diferente aguardando mais confirmações.</summary>
    Pending,
    /// <summary>Dois candidatos com score parecido: leitura descartada.</summary>
    Ambiguous,
    /// <summary>O estado mudou nesta leitura.</summary>
    Changed
}

public readonly record struct HysteresisOutcome(
    HysteresisState State,
    string? Current,
    string? Pending,
    int PendingCount)
{
    public bool Changed => State == HysteresisState.Changed;
}

/// <summary>
/// Estabiliza uma classificação ao longo do tempo:
/// <list type="bullet">
/// <item>só troca de estado após <c>confirmations</c> leituras consecutivas do mesmo candidato;</item>
/// <item>entrar num estado exige score &gt;= limiar; permanecer nele exige apenas score &gt;= limiar - <c>exitMargin</c>;</item>
/// <item>quando o 1º colocado não supera o atual (ou o 2º) por pelo menos <c>ambiguityMargin</c>, a leitura é ignorada.</item>
/// </list>
/// "Nenhum" é representado por <see cref="None"/> e só é considerado quando <c>allowNone</c> está ligado.
/// </summary>
public sealed class HysteresisTracker
{
    public const string None = "";

    private readonly int _confirmations;
    private readonly double _exitMargin;
    private readonly double _ambiguityMargin;

    public HysteresisTracker(int confirmations, double exitMargin, double ambiguityMargin, string? initial = null)
    {
        _confirmations = Math.Max(1, confirmations);
        _exitMargin = Math.Max(0.0, exitMargin);
        _ambiguityMargin = Math.Max(0.0, ambiguityMargin);
        Current = initial;
    }

    /// <summary>Estado estável (null = ainda indefinido, "" = estável em "nenhum").</summary>
    public string? Current { get; private set; }
    public string? Pending { get; private set; }
    public int PendingCount { get; private set; }
    public double LastAcceptedScore { get; private set; }

    public bool IsSettled => Current != null;

    public void Reset(string? initial = null)
    {
        Current = initial;
        Pending = null;
        PendingCount = 0;
        LastAcceptedScore = 0.0;
    }

    public HysteresisOutcome Observe(MatchDebugResult result, double enterThreshold, bool allowNone = false)
    {
        double exitThreshold = Math.Max(0.0, enterThreshold - _exitMargin);
        string? top = result.TopName;
        double topScore = result.BestScore;

        if (!string.IsNullOrEmpty(Current) && top != null)
        {
            double? currentScore = ScoreOf(result, Current);

            // Permanência: o estado atual continua líder com score acima do limiar de saída.
            if (string.Equals(top, Current, StringComparison.OrdinalIgnoreCase) && topScore >= exitThreshold)
            {
                LastAcceptedScore = topScore;
                return Settle(HysteresisState.Held);
            }

            // Concorrente empatado com o atual: não troca.
            if (currentScore.HasValue && currentScore.Value >= exitThreshold && topScore - currentScore.Value < _ambiguityMargin)
                return Settle(HysteresisState.Ambiguous);
        }

        string? proposal = null;
        if (top != null && topScore >= enterThreshold)
        {
            if (Current != null && result.RankedCandidates.Count > 1 && result.Margin < _ambiguityMargin
                && !string.Equals(top, Current, StringComparison.OrdinalIgnoreCase))
            {
                return Settle(HysteresisState.Ambiguous);
            }

            proposal = top;
        }
        else if (allowNone)
        {
            proposal = None;
        }

        if (proposal == null || string.Equals(proposal, Current, StringComparison.OrdinalIgnoreCase))
        {
            if (proposal != null)
                LastAcceptedScore = topScore;
            return Settle(proposal != null ? HysteresisState.Held : HysteresisState.Idle);
        }

        if (string.Equals(proposal, Pending, StringComparison.OrdinalIgnoreCase))
        {
            PendingCount++;
        }
        else
        {
            Pending = proposal;
            PendingCount = 1;
        }

        if (PendingCount >= _confirmations)
        {
            Current = proposal;
            LastAcceptedScore = topScore;
            Pending = null;
            PendingCount = 0;
            return new HysteresisOutcome(HysteresisState.Changed, Current, null, 0);
        }

        return new HysteresisOutcome(HysteresisState.Pending, Current, Pending, PendingCount);
    }

    private HysteresisOutcome Settle(HysteresisState state)
    {
        // Qualquer leitura que não reforça o candidato pendente quebra a sequência de confirmações.
        Pending = null;
        PendingCount = 0;
        return new HysteresisOutcome(state, Current, null, 0);
    }

    private static double? ScoreOf(MatchDebugResult result, string name)
    {
        foreach (var candidate in result.RankedCandidates)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                return candidate.Score;
        }

        return null;
    }
}
