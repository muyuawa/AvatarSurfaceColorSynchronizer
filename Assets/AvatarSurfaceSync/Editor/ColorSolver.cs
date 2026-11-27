using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AvatarSurfaceSync
{
    internal readonly struct ErrorStats
    {
        public readonly float Mean, P95;
        public readonly int Count;
        public ErrorStats(IEnumerable<float> values)
        {
            var sorted = values.OrderBy(x => x).ToArray();
            Count = sorted.Length;
            Mean = Count > 0 ? sorted.Average() : float.PositiveInfinity;
            P95 = Count > 0 ? sorted[Mathf.Max(0, Mathf.CeilToInt(Count * .95f) - 1)] : float.PositiveInfinity;
        }
        public string Label => Count == 0 ? "没有可信采样" : $"平均 {Mean:F4}  ·  P95 {P95:F4}  ·  {Count} 对";
        public float Loss => Mean + P95 * .45f;
    }

    internal static class ColorSolver
    {
        internal static Vector3 ToLab(Color c)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Gamma) c = c.linear;
            float l = CubeRoot(.41222147f * c.r + .53633254f * c.g + .05144599f * c.b);
            float m = CubeRoot(.21190350f * c.r + .68069955f * c.g + .10739696f * c.b);
            float s = CubeRoot(.08830246f * c.r + .28171884f * c.g + .62997870f * c.b);
            return new Vector3(.21045426f * l + .79361779f * m - .00407205f * s,
                1.97799850f * l - 2.42859221f * m + .45059371f * s,
                .02590404f * l + .78277177f * m - .80867577f * s);
        }

        private static float CubeRoot(float x) => Mathf.Pow(Mathf.Max(0, x), 1f / 3f);

        internal static Vector3 Robust(IReadOnlyList<NeckSample> samples)
        {
            var center = new Vector3(Median(samples.Select(s => s.Difference.x)),
                Median(samples.Select(s => s.Difference.y)), Median(samples.Select(s => s.Difference.z)));
            float deviation = Mathf.Max(.006f, Median(samples.Select(s => (s.Difference - center).magnitude)) * 1.48f);
            for (int i = 0; i < 4; i++)
            {
                float total = 0;
                Vector3 sum = Vector3.zero;
                foreach (var p in samples)
                {
                    float w = p.Weight * Mathf.Min(1, 1.5f * deviation / Mathf.Max(1e-6f, (p.Difference - center).magnitude));
                    sum += p.Difference * w;
                    total += w;
                }
                if (total > 0) center = sum / total;
            }
            return center;
        }

        private static float Median(IEnumerable<float> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) * .5f;
        }
    }

    internal sealed class Correction
    {
        public const int MaxAnchors = 64;
        public Vector3 Global;
        public bool SecondMain;
        public Vector4[] Anchors, Deltas;
        public Bounds Area;
        public float Width;
        public bool HasLocal => Anchors != null && Anchors.Length != 0;
    }

    internal static class CorrectionSolver
    {
        public static Dictionary<Region, Correction> Solve(IReadOnlyList<NeckSample> samples,
            HashSet<Region> originals, HashSet<Region> targets, SyncSettings settings, float gain)
        {
            var usable = samples.Where(p => p.Measured).ToArray();
            var movable = new HashSet<Region>(originals);
            if (settings.Mode == SyncMode.Midpoint) movable.UnionWith(targets);
            movable.RemoveWhere(r => !r.CanBake);
            var regions = usable.SelectMany(p => new[] { p.A, p.B }).Distinct().ToArray();
            movable.IntersectWith(regions);
            var overall = regions.ToDictionary(r => r, _ => Vector3.zero);
            var scale = new Vector3(settings.Lightness, settings.Chroma, settings.Chroma) * (settings.Strength * gain);

            if (!settings.SeamOnly)
            {
                var pairs = usable.GroupBy(p => (p.A, p.B)).Select(g => (g.Key.A, g.Key.B,
                    Shift: ColorSolver.Robust(g.ToArray()), Weight: g.Sum(p => p.Weight))).ToArray();
                var next = new Dictionary<Region, Vector3>();
                for (int i = 0; i < 35; i++)
                {
                    foreach (var region in movable)
                    {
                        float w = .015f;
                        Vector3 sum = Vector3.zero;
                        foreach (var pair in pairs)
                        {
                            if (pair.A == region) { sum += (overall[pair.B] - pair.Shift) * pair.Weight; w += pair.Weight; }
                            if (pair.B == region) { sum += (overall[pair.A] + pair.Shift) * pair.Weight; w += pair.Weight; }
                        }
                        next[region] = Vector3.Lerp(overall[region], sum / w, .5f);
                    }
                    foreach (var cell in next) overall[cell.Key] = cell.Value;
                }
            }

            var result = new Dictionary<Region, Correction>();
            foreach (var region in movable)
            {
                var change = new Correction { Global = Vector3.ClampMagnitude(Vector3.Scale(overall[region], scale), .23f) };
                result.Add(region, change);
                if (settings.Seam <= 0 || !region.CanBake) continue;
                var local = usable.Where(p => p.A == region || p.B == region).ToArray();
                float residual = local.Average(p => (p.Difference + overall[p.A] - overall[p.B]).magnitude);
                change.Width = settings.WidthMm > 0 ? settings.WidthMm * .001f : settings.SeamOnly
                    ? Mathf.Clamp(.025f + residual * .15f, .025f, .045f)
                    : Mathf.Clamp(.017f + residual * .15f, .016f, .038f);
                var smoothed = settings.SeamOnly && settings.Screened
                    ? local.GroupBy(p => (p.A, p.B)).ToDictionary(g => g.Key, g => ColorSolver.Robust(g.ToArray()))
                    : null;
                var flow = local.Select(p =>
                {
                    // Preserve local skin detail rather than fitting each pixel's lighting or freckles.
                    var diff = smoothed == null ? p.Difference : Vector3.Lerp(p.Difference, smoothed[(p.A, p.B)], .8f);
                    var err = Vector3.Scale(diff + overall[p.A] - overall[p.B], scale);
                    float share = movable.Contains(p.A) && movable.Contains(p.B) && p.A.CanBake && p.B.CanBake ? .5f : 1f;
                    var delta = Vector3.ClampMagnitude(err * (settings.Seam * share), .20f);
                    return (Position: p.A == region ? p.PointA : p.PointB, Delta: p.A == region ? -delta : delta, Weight: p.Weight);
                }).ToArray();
                if (flow.Length == 0 || flow.All(x => x.Delta.sqrMagnitude < 1e-8f)) continue;
                Pack(change, flow, settings.Screened);
            }
            return result;
        }

        private static void Pack(Correction correction, (Vector3 Position, Vector3 Delta, float Weight)[] flow, bool smooth)
        {
            float spacing = Mathf.Clamp(correction.Width * .23f, .0037f, .008f);
            var cells = flow.GroupBy(x => Vector3Int.RoundToInt(x.Position / spacing)).Select(group =>
            {
                float weight = group.Sum(x => x.Weight);
                var position = group.Aggregate(Vector3.zero, (v, x) => v + x.Position * x.Weight) / weight;
                var delta = group.Aggregate(Vector3.zero, (v, x) => v + x.Delta * x.Weight) / weight;
                return (Position: position, Delta: delta, Weight: weight);
            }).ToArray();
            var picked = new List<int> { 0 };
            var distance = Enumerable.Repeat(float.MaxValue, cells.Length).ToArray();
            while (picked.Count < Mathf.Min(Correction.MaxAnchors, cells.Length))
            {
                var last = cells[picked[picked.Count - 1]].Position;
                int far = 0;
                for (int i = 0; i < cells.Length; i++)
                {
                    distance[i] = Mathf.Min(distance[i], (cells[i].Position - last).sqrMagnitude);
                    if (distance[i] > distance[far]) far = i;
                }
                if (distance[far] <= 0) break;
                picked.Add(far);
            }
            correction.Anchors = new Vector4[picked.Count];
            correction.Deltas = new Vector4[picked.Count];
            var bounds = new Bounds(cells[picked[0]].Position, Vector3.zero);
            for (int i = 0; i < picked.Count; i++)
            {
                var c = cells[picked[i]];
                var delta = c.Delta;
                if (smooth)
                {
                    float weight = 0;
                    var sum = Vector3.zero;
                    foreach (var nearby in cells)
                    {
                        float w = 1 - Mathf.Clamp01((nearby.Position - c.Position).magnitude / (spacing * 2.5f));
                        weight += w;
                        sum += nearby.Delta * w;
                    }
                    if (weight > 0) delta = Vector3.Lerp(delta, sum / weight, .6f);
                }
                correction.Anchors[i] = c.Position;
                correction.Deltas[i] = delta;
                bounds.Encapsulate(c.Position);
            }
            bounds.Expand(correction.Width * 2f);
            correction.Area = bounds;
        }
    }
}
