# Arquitetura e Engenharia do Módulo de Visão Computacional (C# / .NET)

Este documento especifica o design técnico, a estrutura de projetos, as abstrações e a implementação do módulo de detecção e análise visual de alvos desenvolvido em **C# / .NET 8+**.

---

## 1. Visão Geral e Padrões de Alta Performance em .NET

A abordagem em C# prioriza **latência previsível e mínima sobrecarga de CPU**, aplicando técnicas modernas de sistemas de tempo real:

* **Zero Alocação no Caminho Crítico:** Uso de `readonly record struct` para dados efêmeros por frame e `ArrayPool<byte>.Shared` / `MemoryPool<T>` para reutilização de tensores e buffers de imagem sem sobrecarregar o Garbage Collector (GC).
* **Processamento com Span e Memória Contígua:** Utilização de `ReadOnlySpan<byte>` para transformações e normalizações de tensores.
* **Assincronismo e Cancelamento:** Suporte nativo a `CancellationToken` cooperativo em todas as etapas demoradas e descarte explícito via `IDisposable` e `IAsyncDisposable`.
* **Isolamento de Segurança:** Arquitetura desacoplada que emite exclusivamente relatórios de telemetria e análise visual para frames gravados ou sintéticos, sem dependências de injeção de periféricos (`user32.dll` / `SendInput`).

---

## 2. Estrutura da Solução e Projetos

```
TargetVision/
│
├── TargetVision.sln
│
├── src/
│   └── TargetVision.Core/
│       ├── TargetVision.Core.csproj
│       ├── Abstractions/
│       │   ├── IFrameSource.cs          # Interface de obtenção de frames (vídeo/sintético)
│       │   ├── ITargetDetector.cs       # Abstração de inferência (ONNX / Mock)
│       │   ├── ITargetSelector.cs       # Estratégia de priorização e filtro de alvos
│       │   └── ISmoother.cs             # Suavização temporal (EMA / Filtro)
│       ├── Models/
│       │   ├── BoundingBox.cs           # readonly record struct (X, Y, Width, Height)
│       │   ├── DetectedTarget.cs        # readonly record struct (Box, Confidence, ClassId)
│       │   ├── TargetSpatialData.cs     # readonly record struct (Head, Center, Delta, Dist)
│       │   └── VisionMetrics.cs         # Métricas de latência e throughput
│       ├── Detection/
│       │   ├── OnnxTargetDetector.cs    # Implementação via Microsoft.ML.OnnxRuntime
│       │   └── MockTargetDetector.cs    # Implementação determinística para testes
│       ├── Sources/
│       │   ├── SyntheticFrameSource.cs  # Gerador procedural de alvos geométricos
│       │   └── PrerecordedImageSource.cs# Leitor de imagens/vídeos de benchmark
│       ├── Mathematics/
│       │   ├── CoordinateCalculator.cs  # Transformações de coordenadas e estimativa de cabeça
│       │   └── VectorExtensions.cs      # Operações vetoriais de distância euclidiana
│       ├── Selection/
│       │   ├── ClosestToCrosshairSelector.cs
│       │   └── HighestConfidenceSelector.cs
│       ├── Smoothing/
│       │   └── ExponentialMovingAverageSmoother.cs
│       └── Pipeline/
│           ├── VisionPipeline.cs        # Orquestrador assíncrono do ciclo de vida
│           └── VisionPipelineConfig.cs  # DTO de configuração (resoluções, limites, ratios)
│
└── tests/
    └── TargetVision.Tests/
        ├── TargetVision.Tests.csproj    # xUnit + FluentAssertions
        ├── CoordinateTransformTests.cs  # Validação geométrica e de cabeça
        ├── TargetSelectorTests.cs       # Validação das regras de desempate
        ├── SmoothingTests.cs            # Teste numérico da convergência do EMA
        └── PipelineIntegrationTests.cs # Execução com fontes sintéticas
```

---

## 3. Especificação dos Modelos e Abstrações (`Abstractions/` e `Models/`)

### 3.1. Estruturas de Dados Livres de Alocação (`Models/`)
```csharp
namespace TargetVision.Core.Models;

public readonly record struct BoundingBox(float X, float Y, float Width, float Height)
{
    public (float X, float Y) Center => (X + Width / 2f, Y + Height / 2f);
    public (float X, float Y) TopCenter => (X + Width / 2f, Y);
}

public readonly record struct DetectedTarget(
    BoundingBox Box,
    float Confidence,
    int ClassId);

public readonly record struct TargetSpatialData(
    DetectedTarget Target,
    (float X, float Y) EstimatedHead,
    (float X, float Y) Center,
    (float X, float Y) RelativeDisplacement,
    (float X, float Y) NormalizedDisplacement,
    float DistanceToReference);

public readonly record struct VisionMetrics(
    double CaptureLatencyMs,
    double InferenceLatencyMs,
    double ProcessingLatencyMs,
    double TotalLatencyMs,
    double FramesPerSecond);
```

### 3.2. Interfaces Abstratas (`Abstractions/`)
```csharp
namespace TargetVision.Core.Abstractions;

using TargetVision.Core.Models;

public interface IFrameSource : IDisposable
{
    /// <summary>
    /// Copia o próximo frame para o buffer fornecido sem alocações no heap.
    /// Retorna false quando não houver mais frames ou caso seja cancelado.
    /// </summary>
    bool TryGetNextFrame(Span<byte> destinationBuffer, out int width, out int height, CancellationToken cancellationToken);
}

public interface ITargetDetector : IDisposable
{
    /// <summary>
    /// Executa a inferência sobre os bytes do frame fornecido.
    /// </summary>
    ValueTask<IReadOnlyList<DetectedTarget>> DetectAsync(
        ReadOnlyMemory<byte> frameData, 
        int width, 
        int height, 
        CancellationToken cancellationToken);
}

public interface ITargetSelector
{
    TargetSpatialData? SelectTarget(
        ReadOnlySpan<TargetSpatialData> targets, 
        (float X, float Y) referencePoint, 
        float minimumConfidence);
}

public interface ISmoother
{
    (float X, float Y) Smooth((float X, float Y) currentPoint, double timestampSeconds);
    void Reset();
}
```

---

## 4. Núcleo Matemático e Algoritmos

### 4.1. Transformações e Estimativa de Cabeça (`CoordinateCalculator.cs`)
```csharp
namespace TargetVision.Core.Mathematics;

using TargetVision.Core.Models;

public sealed class CoordinateCalculator
{
    private readonly float _frameWidth;
    private readonly float _frameHeight;
    private readonly float _headRatio;

    public CoordinateCalculator(int frameWidth, int frameHeight, float headRatio = 0.16f)
    {
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        _headRatio = Math.Clamp(headRatio, 0.05f, 0.40f);
    }

    public TargetSpatialData Calculate(DetectedTarget target, (float X, float Y) referencePoint)
    {
        var box = target.Box;
        var center = box.Center;

        // Estimativa anatômica da cabeça: eixo X centralizado na caixa, eixo Y no terço superior
        float headX = box.X + (box.Width / 2f);
        float headY = box.Y + (box.Height * _headRatio);
        var headPoint = (X: headX, Y: headY);

        // Deslocamento relativo em pixels em relação à referência (ex: centro do visor)
        float deltaX = headX - referencePoint.X;
        float deltaY = headY - referencePoint.Y;

        // Normalização em escala [-1.0, 1.0]
        float normX = deltaX / (_frameWidth / 2f);
        float normY = deltaY / (_frameHeight / 2f);

        float distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);

        return new TargetSpatialData(
            Target: target,
            EstimatedHead: headPoint,
            Center: center,
            RelativeDisplacement: (deltaX, deltaY),
            NormalizedDisplacement: (normX, normY),
            DistanceToReference: distance);
    }
}
```

### 4.2. Suavizador Temporal com Resposta ao Degrau (`ExponentialMovingAverageSmoother.cs`)
```csharp
namespace TargetVision.Core.Smoothing;

using TargetVision.Core.Abstractions;

public sealed class ExponentialMovingAverageSmoother : ISmoother
{
    private readonly float _alpha;
    private readonly double _timeoutSeconds;
    private (float X, float Y)? _lastPoint;
    private double _lastTimestamp;

    public ExponentialMovingAverageSmoother(float alpha = 0.45f, double timeoutSeconds = 0.25)
    {
        _alpha = Math.Clamp(alpha, 0.01f, 1.0f);
        _timeoutSeconds = timeoutSeconds;
    }

    public (float X, float Y) Smooth((float X, float Y) currentPoint, double timestampSeconds)
    {
        if (_lastPoint is null || (timestampSeconds - _lastTimestamp) > _timeoutSeconds)
        {
            _lastPoint = currentPoint;
            _lastTimestamp = timestampSeconds;
            return currentPoint;
        }

        float smoothX = (_alpha * currentPoint.X) + ((1.0f - _alpha) * _lastPoint.Value.X);
        float smoothY = (_alpha * currentPoint.Y) + ((1.0f - _alpha) * _lastPoint.Value.Y);

        _lastPoint = (smoothX, smoothY);
        _lastTimestamp = timestampSeconds;

        return _lastPoint.Value;
    }

    public void Reset()
    {
        _lastPoint = null;
        _lastTimestamp = 0;
    }
}
```

---

## 5. Estratégia de Testes Automatizados (`tests/TargetVision.Tests/`)

Utilizando **xUnit** e **FluentAssertions**, os testes executam de forma totalmente determinística e isolada:

1. **`CoordinateTransformTests.cs`:**
   - Garante que um alvo posicionado no centro do frame resulte em `RelativeDisplacement == (0, 0)`.
   - Verifica se alvos nos 4 quadrantes produzem sinais algébricos corretos $(\pm X, \pm Y)$.
   - Valida a invariância da cabeça em relação à largura e sua dependência linear com `Height * HeadRatio`.
2. **`TargetSelectorTests.cs`:**
   - Simula 5 alvos aleatórios e valida se o `ClosestToCrosshairSelector` seleciona matematicamente o ponto com menor `DistanceToReference`.
   - Testa a exclusão completa de alvos com confiança inferior ao `minimumConfidence`.
3. **`SmoothingTests.cs`:**
   - Valida a fórmula do filtro passo a passo em uma série temporal conhecida.
   - Testa a expiração de inércia: quando o intervalo de tempo supera `timeoutSeconds`, o filtro deve descartar o histórico antigo imediatamente.

---

## 6. Prós e Contras da Abordagem em C# / .NET

### Vantagens:
* **Desempenho Extremo:** Latência de pré e pós-processamento inferior a 0.5 ms por frame.
* **Coletor de Lixo Silencioso:** Através de `readonly record struct`, `Span<T>` e *buffer pooling*, alocações são praticamente zero no ciclo contínuo de quadros.
* **Segurança e Rigor:** Tipagem forte em tempo de compilação, facilidade para refatorações seguras e ecossistema corporativo maduro.

### Desvantagens e Trade-offs:
* **Separação do Código Existente:** O restante do repositório é em Python; caso deseje interoperabilidade em tempo real com a UI em PyQt5, será necessário estabelecer uma camada de IPC (ex.: Named Pipes ou gRPC local).
* **Compilação e Toolchain:** Exige o .NET SDK instalado para compilação e execução dos testes (`dotnet test`).
