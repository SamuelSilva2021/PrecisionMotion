using System.Text;

namespace PrecisionMotion.Core.Vision.Evaluation;

public sealed class EvaluationMetrics
{
    public int TotalFramesEvaluated { get; set; }
    public int TotalGroundTruthTargets { get; set; }
    public int TruePositives { get; set; }
    public int FalsePositives { get; set; }
    public int FalseNegatives { get; set; }

    public double Precision => (TruePositives + FalsePositives) > 0 
        ? (double)TruePositives / (TruePositives + FalsePositives) 
        : 0.0;

    public double Recall => (TruePositives + FalseNegatives) > 0 
        ? (double)TruePositives / (TruePositives + FalseNegatives) 
        : 0.0;

    public double F1Score => (Precision + Recall) > 0 
        ? 2.0 * (Precision * Recall) / (Precision + Recall) 
        : 0.0;

    public double MeanIoU { get; set; }
    public double MeanHeadErrorPixels { get; set; }
    public double AverageInferenceLatencyMs { get; set; }
    public double P95InferenceLatencyMs { get; set; }

    public string GenerateMarkdownReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Relatório de Avaliação do Detector de Visão (Benchmark Offline)");
        sb.AppendLine();
        sb.AppendLine($"* **Total de Quadros Avaliados:** {TotalFramesEvaluated}");
        sb.AppendLine($"* **Total de Alvos de Referência (Ground Truth):** {TotalGroundTruthTargets}");
        sb.AppendLine();
        sb.AppendLine("### 1. Métricas de Detecção e Acurácia");
        sb.AppendLine("| Métrica | Valor | Descrição |");
        sb.AppendLine("| :--- | :--- | :--- |");
        sb.AppendLine($"| **Precisão (Precision)** | {Precision:P2} | Proporção de detecções corretas |");
        sb.AppendLine($"| **Revocação (Recall)** | {Recall:P2} | Proporção de alvos reais detectados |");
        sb.AppendLine($"| **F1-Score** | {F1Score:P2} | Média harmônica de precisão e revocação |");
        sb.AppendLine($"| **Mean IoU** | {MeanIoU:P2} | Sobreposição média com caixas reais |");
        sb.AppendLine($"| **Erro Médio da Cabeça** | {MeanHeadErrorPixels:F2} px | Desvio euclidiano médio da posição da cabeça |");
        sb.AppendLine();
        sb.AppendLine("### 2. Matriz de Confusão");
        sb.AppendLine($"* **Verdadeiros Positivos (TP):** {TruePositives}");
        sb.AppendLine($"* **Falsos Positivos (FP - Alarmes Falsos):** {FalsePositives}");
        sb.AppendLine($"* **Falsos Negativos (FN - Alvos Omitidos):** {FalseNegatives}");
        sb.AppendLine();
        sb.AppendLine("### 3. Métricas de Desempenho Computacional");
        sb.AppendLine($"* **Latência Média de Inferência:** {AverageInferenceLatencyMs:F2} ms");
        sb.AppendLine($"* **Latência P95 de Inferência:** {P95InferenceLatencyMs:F2} ms");
        sb.AppendLine();
        return sb.ToString();
    }
}
