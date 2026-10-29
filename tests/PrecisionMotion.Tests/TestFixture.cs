using OpenCvSharp;

namespace PrecisionMotion.Tests;

/// <summary>Monta uma pasta de dados mínima (Configs/geral, calibragem e uma resolução) para testes isolados.</summary>
internal sealed class TestDataDir : IDisposable
{
    public const string Resolution = "1366x768";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"precisionmotion-tests-{Guid.NewGuid():N}");
    public string ResDir => Path.Combine(Root, "Configs", Resolution);
    public string GeneralDir => Path.Combine(Root, "Configs", "geral");
    public string CalibrationDir => Path.Combine(Root, "Configs", "calibragem");

    public TestDataDir()
    {
        Directory.CreateDirectory(ResDir);
        Directory.CreateDirectory(GeneralDir);
        Directory.CreateDirectory(CalibrationDir);
    }

    public void WriteConfig(string extraProperties = "") =>
        File.WriteAllText(Path.Combine(GeneralDir, "config.json"),
            $"{{\"configs\":[{{\"res{Resolution}\":true{(extraProperties.Length > 0 ? "," + extraProperties : "")}}}]}}");

    public void WriteCalibration(string slot1 = "[10,10,50,40]", string slot2 = "[10,100,50,130]") =>
        File.WriteAllText(Path.Combine(CalibrationDir, "parameter1.json"),
            "{\"weapons\":[" +
            $"{{\"name\":\"ALPHA\",\"pattern\":[1.0],\"check_area_{Resolution}\":{slot1},\"check_area2_{Resolution}\":{slot2}}}," +
            $"{{\"name\":\"BETA\",\"pattern\":[1.0],\"check_area_{Resolution}\":{slot1},\"check_area2_{Resolution}\":{slot2}}}]}}");

    public void WriteResolutionParameters(string scopes = "[]") =>
        File.WriteAllText(Path.Combine(ResDir, "parameter1.json"), $"{{\"weapons\":[],\"scopes\":{scopes},\"atachs1\":[],\"atachs2\":[]}}");

    public void WriteCoord() =>
        File.WriteAllText(Path.Combine(ResDir, "coord.cfg"),
            "coordempex:\n100\ncoordempey:\n200\ntamanhoempex:\n30\ntamanhoempey:\n50\n");

    public static Mat Rectangle(int size, int margin)
    {
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mat, new Rect(margin, margin, size - 2 * margin, size - 2 * margin), Scalar.All(255), -1);
        return mat;
    }

    public static Mat Diagonal(int size)
    {
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Line(mat, new Point(0, 0), new Point(size - 1, size - 1), Scalar.All(255), 2);
        return mat;
    }

    public void WriteTemplate(string fileName, Mat template) => Cv2.ImWrite(Path.Combine(ResDir, fileName), template);

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
