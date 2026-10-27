using PrecisionMotion.Core.Vision.Models;

namespace PrecisionMotion.Core.Vision.Visualization;

public readonly record struct TrackedDetection(int Id, DetectedTarget Detection, int ConsecutiveMisses, int ObservedFrames);

public sealed class OfflineDetectionTracker
{
    private sealed class Track
    {
        public required int Id { get; init; }
        public required DetectedTarget LastDetection { get; set; }
        public int Misses { get; set; }
        public int Observations { get; set; } = 1;
    }

    private readonly List<Track> _tracks = [];
    private readonly float _minimumIou;
    private readonly int _maximumMisses;
    private int _nextId = 1;

    public OfflineDetectionTracker(float minimumIou = 0.3f, int maximumMisses = 8)
    {
        if (minimumIou is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(minimumIou));
        if (maximumMisses < 0) throw new ArgumentOutOfRangeException(nameof(maximumMisses));
        _minimumIou = minimumIou;
        _maximumMisses = maximumMisses;
    }

    public IReadOnlyList<TrackedDetection> Update(IReadOnlyList<DetectedTarget> detections)
    {
        var candidates = new List<(int Track, int Detection, float Iou)>();
        for (int t = 0; t < _tracks.Count; t++)
        {
            for (int d = 0; d < detections.Count; d++)
            {
                if (_tracks[t].LastDetection.ClassId != detections[d].ClassId) continue;
                float iou = IntersectionOverUnion(_tracks[t].LastDetection.Box, detections[d].Box);
                if (iou >= _minimumIou) candidates.Add((t, d, iou));
            }
        }

        candidates.Sort((a, b) => b.Iou.CompareTo(a.Iou));
        var assignedTracks = new HashSet<int>();
        var assignedDetections = new HashSet<int>();
        var result = new List<TrackedDetection>(detections.Count);

        foreach (var match in candidates)
        {
            if (!assignedTracks.Add(match.Track)) continue;
            if (!assignedDetections.Add(match.Detection))
            {
                assignedTracks.Remove(match.Track);
                continue;
            }

            var track = _tracks[match.Track];
            track.LastDetection = detections[match.Detection];
            track.Misses = 0;
            track.Observations++;
            result.Add(new TrackedDetection(track.Id, track.LastDetection, 0, track.Observations));
        }

        for (int t = 0; t < _tracks.Count; t++)
            if (!assignedTracks.Contains(t)) _tracks[t].Misses++;

        for (int d = 0; d < detections.Count; d++)
        {
            if (assignedDetections.Contains(d)) continue;
            var track = new Track { Id = _nextId++, LastDetection = detections[d] };
            _tracks.Add(track);
            result.Add(new TrackedDetection(track.Id, track.LastDetection, 0, 1));
        }

        _tracks.RemoveAll(t => t.Misses > _maximumMisses);
        return result;
    }

    private static float IntersectionOverUnion(BoundingBox a, BoundingBox b)
    {
        float left = Math.Max(a.X, b.X);
        float top = Math.Max(a.Y, b.Y);
        float right = Math.Min(a.X + a.Width, b.X + b.Width);
        float bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        float intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        float union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }
}
