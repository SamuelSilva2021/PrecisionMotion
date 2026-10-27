using Microsoft.ML.OnnxRuntime;
using Xunit;
using Xunit.Abstractions;

namespace PrecisionMotion.Tests.Vision;

public sealed class OnnxModelContractTests
{
    private readonly ITestOutputHelper _output;

    public OnnxModelContractTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("yolo11n.onnx")]
    [InlineData("pubg_trained.onnx")]
    public void Model_HasExpectedInputAndRawDetectionOutput(string modelFile)
    {
        string root = FindRepositoryRoot();
        string modelPath = Path.Combine(root, "models", modelFile);
        Assert.True(File.Exists(modelPath), $"Modelo ausente: {modelPath}");

        using var session = new InferenceSession(modelPath);
        var input = Assert.Single(session.InputMetadata);
        var inputShape = input.Value.Dimensions.ToArray();

        _output.WriteLine($"Model: {modelFile}");
        _output.WriteLine($"Input: {input.Key} [{string.Join(", ", inputShape)}]");
        Assert.Equal(4, inputShape.Length);
        Assert.Equal(3, inputShape[1]);
        Assert.True(inputShape[2] == 640 || inputShape[2] == -1);
        Assert.True(inputShape[3] == 640 || inputShape[3] == -1);

        var output = Assert.Single(session.OutputMetadata);
        var shape = output.Value.Dimensions.ToArray();
        _output.WriteLine($"Output: {output.Key} [{string.Join(", ", shape)}]");
        Assert.Equal(3, shape.Length);
        Assert.True(shape[1] >= 5 || shape[2] >= 5, "Saída não corresponde ao parser de detecções 3D.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PrecisionMotion.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Raiz da solução PrecisionMotion não encontrada.");
    }
}
