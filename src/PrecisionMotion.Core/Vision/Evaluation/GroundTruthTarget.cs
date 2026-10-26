using System.Globalization;
using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Evaluation;

public readonly record struct GroundTruthTarget(int ClassId, BoundingBox Box, (float X, float Y)? HeadPoint = null)
{
    public static List<GroundTruthTarget> ParseYoloFormat(string content, int imageWidth, int imageHeight)
    {
        var targets = new List<GroundTruthTarget>();
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;

            if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out int classId)) continue;
            if (!float.TryParse(parts[1], CultureInfo.InvariantCulture, out float xNorm)) continue;
            if (!float.TryParse(parts[2], CultureInfo.InvariantCulture, out float yNorm)) continue;
            if (!float.TryParse(parts[3], CultureInfo.InvariantCulture, out float wNorm)) continue;
            if (!float.TryParse(parts[4], CultureInfo.InvariantCulture, out float hNorm)) continue;

            float width = wNorm * imageWidth;
            float height = hNorm * imageHeight;
            float x = (xNorm * imageWidth) - (width / 2f);
            float y = (yNorm * imageHeight) - (height / 2f);

            targets.Add(new GroundTruthTarget(classId, new BoundingBox(x, y, width, height)));
        }

        return targets;
    }
}
