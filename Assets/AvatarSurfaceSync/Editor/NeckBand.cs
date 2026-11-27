using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AvatarSurfaceSync
{
    internal sealed class NeckSample
    {
        public Region A, B;
        public Vector3 PointA, PointB, Direction, Difference;
        public Vector3 Position => (PointA + PointB) * .5f;
        public int View;
        public int[] PixelsA, PixelsB;
        public float Weight, OriginalError;
        public bool Measured;
    }

    internal static class NeckBand
    {
        public static Bounds Frame(SurfaceModel model, IEnumerable<Seam> topology)
        {
            var animator = model.Root.GetComponentInChildren<Animator>();
            if (animator && animator.isHuman)
            {
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                var neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                if (head && neck)
                {
                    var span = head.position - neck.position;
                    var length = Mathf.Max(.035f, span.magnitude);
                    var center = head.position - model.Origin;
                    return new Bounds(center, new Vector3(3.75f, 2.8f, 3.75f) * length);
                }
            }

            var points = topology.SelectMany(s => s.Samples).Select(p => p.Position).ToArray();
            if (points.Length == 0) return default;
            var frame = new Bounds(points[0], new Vector3(.10f, .08f, .10f));
            foreach (var p in points) frame.Encapsulate(p);
            frame.Expand(new Vector3(.055f, .035f, .055f));
            return frame;
        }

        public static Vector3[] Directions(Transform root)
        {
            return Enumerable.Range(0, 8)
                .Select(i => (Quaternion.AngleAxis(i * 45f, root.up) * root.forward - root.up * .13f).normalized)
                .ToArray();
        }

        public static List<NeckSample> Find(Capture capture, Bounds frame, HashSet<Region> original, HashSet<Region> target)
        {
            var aById = original.ToDictionary(r => r.Id);
            var bById = target.ToDictionary(r => r.Id);
            var cells = new Dictionary<Vector3Int, NeckSample>();
            float extent = frame.size.y;
            for (int v = 0; v < capture.Views.Count; v++)
            {
                var view = capture.Views[v];
                int size = capture.Size;
                var inverse = view.Projection.inverse;
                float step = 2f / (Mathf.Abs(view.Projection.m11) * size);
                int stride = Mathf.Max(2, Mathf.RoundToInt(.0025f / step));
                int band = Mathf.Max(2, Mathf.RoundToInt(.0020f / step));
                for (int x = stride; x < size - stride; x += stride)
                {
                    for (int y = band * 3; y < size - band * 3; y++)
                    {
                        int lower = y * size + x, upper = lower + size;
                        int lowId = Mathf.RoundToInt(view.Id[lower].r), highId = Mathf.RoundToInt(view.Id[upper].r);
                        Region low, high;
                        bool aBelow = aById.TryGetValue(lowId, out low);
                        if (aBelow)
                        {
                            if (!bById.TryGetValue(highId, out high)) continue;
                        }
                        else
                        {
                            if (!bById.TryGetValue(lowId, out low) || !aById.TryGetValue(highId, out high)) continue;
                        }
                        if (IsFace(low) && !IsFace(high)) continue;

                        var pa = Unproject(inverse, size, lower, view.Id[lower].g);
                        var pb = Unproject(inverse, size, upper, view.Id[upper].g);
                        var position = (pa + pb) * .5f;
                        // A chin can occlude the neck several centimetres behind it. Both sides
                        // must still be inside the neck region and adjacent in the rendered view.
                        float depthGap = Vector3.Distance(pa, pb);
                        if (!frame.Contains(pa) || !frame.Contains(pb) ||
                            depthGap > Mathf.Clamp(extent * .33f, .022f, .075f)) continue;

                        var below = Pixels(lower, -size, lowId);
                        var above = Pixels(upper, size, highId);
                        if (below.Length < 2 || above.Length < 2) continue;
                        float confidence = Mathf.Lerp(1f, .58f, Mathf.InverseLerp(.008f, extent * .33f, depthGap));
                        float front = Mathf.Max(0, Vector3.Dot(view.Direction, capture.Views[0].Direction));
                        confidence *= 1f + .6f * front;
                        float cell = Mathf.Clamp(extent * .019f, .0025f, .0060f);
                        var key = Vector3Int.RoundToInt((position - frame.center) / cell);
                        if (cells.TryGetValue(key, out var prior) && prior.Weight >= confidence) continue;
                        cells[key] = new NeckSample
                        {
                            A = aBelow ? low : high, B = aBelow ? high : low,
                            PointA = aBelow ? pa : pb, PointB = aBelow ? pb : pa,
                            PixelsA = aBelow ? below : above, PixelsB = aBelow ? above : below,
                            Direction = view.Direction, View = v, Weight = confidence
                        };
                    }
                }

                int[] Pixels(int edge, int axis, int id)
                {
                    var pixels = new List<int>(3);
                    for (int i = 1; i <= band * 3; i++)
                    {
                        int index = edge + axis * i;
                        if (Mathf.RoundToInt(view.Id[index].r) != id) break;
                        if (i % band == 0) pixels.Add(index);
                    }
                    return pixels.ToArray();
                }

            }
            return cells.Values.ToList();
        }

        private static Vector3 Unproject(Matrix4x4 inverse, int size, int index, float depth)
        {
            if (SystemInfo.usesReversedZBuffer) depth = 1f - depth;
            return inverse.MultiplyPoint(new Vector3(
                2f * (index % size + .5f) / size - 1f,
                2f * (index / size + .5f) / size - 1f, 2f * depth - 1f));
        }

        public static Bounds SkinFrame(IEnumerable<Region> skin)
        {
            var bounds = new Bounds();
            bool any = false;
            foreach (var region in skin)
                foreach (int index in region.Triangles)
                {
                    var point = region.Mesh.World[index];
                    if (any) bounds.Encapsulate(point);
                    else { bounds.center = point; any = true; }
                }
            return bounds;
        }

        public static float OutsideDifference(Capture baseline, Capture candidate, IReadOnlyDictionary<Region, Bounds> bands)
        {
            var byId = bands.ToDictionary(p => p.Key.Id, p => p.Value);
            float max = 0;
            int size = baseline.Size;
            for (int view = 0; view < baseline.Views.Count; view++)
            {
                var original = baseline.Views[view];
                var edited = candidate.Views[view].Color;
                var inverse = original.Projection.inverse;
                // Allow up to two screen pixels of filtering at the band edge.
                float margin = 8f / (Mathf.Abs(original.Projection.m11) * size);
                for (int i = 0; i < edited.Length; i++)
                {
                    var a = original.Color[i];
                    var b = edited[i];
                    if (Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)) < .001f) continue;
                    int id = Mathf.RoundToInt(original.Id[i].r);
                    if (!byId.TryGetValue(id, out var band)) continue;
                    band.Expand(margin);
                    if (band.Contains(Unproject(inverse, size, i, original.Id[i].g))) continue;
                    max = Mathf.Max(max, (ColorSolver.ToLab(a) - ColorSolver.ToLab(b)).magnitude);
                }
            }
            return max;
        }

        private static bool IsFace(Region r) => r.Effective == RegionRole.Face || r.Suggested == RegionRole.Face;

        public static ErrorStats Measure(Capture capture, IReadOnlyList<NeckSample> samples, bool storeBaseline = false)
        {
            var errors = new List<float>(samples.Count);
            foreach (var sample in samples)
            {
                sample.Measured = TryColors(capture, sample, out var a, out var b);
                if (!sample.Measured) continue;
                sample.Difference = a - b;
                float error = sample.Difference.magnitude;
                if (storeBaseline) sample.OriginalError = error;
                errors.Add(error);
            }
            return new ErrorStats(errors);
        }

        public static bool TryColors(Capture capture, NeckSample sample, out Vector3 a, out Vector3 b)
        {
            var image = capture.Views[sample.View].Color;
            b = Vector3.zero;
            return TryBand(image, sample.PixelsA, out a) && TryBand(image, sample.PixelsB, out b);
        }

        private static bool TryBand(Color[] image, int[] indices, out Vector3 result)
        {
            result = Vector3.zero;
            foreach (int i in indices)
            {
                if (image[i].a < .42f || !float.IsFinite(image[i].r + image[i].g + image[i].b)) return false;
                result += ColorSolver.ToLab(image[i]);
            }
            result /= indices.Length;
            var near = ColorSolver.ToLab(image[indices[0]]);
            result = near + Vector3.ClampMagnitude(near - result, .018f) * .6f;
            return true;
        }
    }
}
