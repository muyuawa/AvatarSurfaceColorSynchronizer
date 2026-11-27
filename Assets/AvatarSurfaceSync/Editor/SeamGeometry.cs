using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AvatarSurfaceSync
{
    internal sealed class Boundary
    {
        public Region Region;
        public int V0;
        public Vector3 A, B, Normal, Inward;
        public float Length;
        public Vector3 At(float t) => Vector3.Lerp(A, B, t);
        public float Closest(Vector3 p) => Mathf.Clamp01(Vector3.Dot(p - A, B - A) / (Length * Length));
    }

    internal sealed class SeamSample
    {
        public Boundary A, B;
        public float T, U, Weight;
        public Vector3 Position => (A.At(T) + B.At(U)) * .5f;
    }

    internal sealed class Seam
    {
        public Region A, B;
        public readonly List<SeamSample> Samples = new List<SeamSample>();
        public float Length;
    }

    internal static class SeamDetector
    {
        internal static void SelectSkinContacts(IEnumerable<Seam> seams, HashSet<Region> originals, HashSet<Region> targets)
        {
            originals.Clear();
            targets.Clear();
            var contacts = seams.GroupBy(s => (s.A, s.B))
                .Select(g => (g.Key.A, g.Key.B, Length: g.Sum(s => s.Length),
                    Samples: g.Sum(s => s.Samples.Count)))
                .Where(c => c.Length >= .0045f && c.Samples >= 5)
                .ToArray();
            var headContacts = contacts.Where(c => c.A.Effective == RegionRole.Face && c.B.Effective == RegionRole.Body ||
                                                 c.B.Effective == RegionRole.Face && c.A.Effective == RegionRole.Body).ToArray();
            if (headContacts.Length == 0) return;
            float required = Mathf.Max(.0045f, headContacts.Max(c => c.Length) * .32f);
            foreach (var contact in headContacts.Where(c => c.Length >= required))
            {
                var face = contact.A.Effective == RegionRole.Face ? contact.A : contact.B;
                var body = face == contact.A ? contact.B : contact.A;
                targets.Add(face);
                originals.Add(body);
            }
        }

        internal static List<Seam> Detect(SurfaceModel model, float tolerance)
        {
            var edges = model.Regions.Where(r => r.SkinRegion && r.Enabled).SelectMany(Boundaries).ToArray();
            if (edges.Length == 0) return new List<Seam>();
            var tree = new BoundaryTree(edges, 0, edges.Length);
            var groups = new Dictionary<(int, int), List<SeamSample>>();
            foreach (var a in edges)
            {
                var count = Mathf.Clamp(Mathf.CeilToInt(a.Length / Mathf.Clamp(tolerance * .52f, .0012f, .003f)), 1, 96);
                for (var i = 0; i < count; i++)
                {
                    var t = (i + .5f) / count;
                    var position = a.At(t);
                    var score = tolerance;
                    Boundary b = null;
                    tree.Match(a, position, tolerance, ref b, ref score);
                    if (b == null) continue;
                    var key = (a.Region.Id, b.Region.Id);
                    if (!groups.TryGetValue(key, out var samples)) groups.Add(key, samples = new List<SeamSample>());
                    samples.Add(new SeamSample { A = a, B = b, T = t, U = b.Closest(position),
                        Weight = a.Length / count * Mathf.Clamp01(1f - .5f * score / tolerance) });
                }
            }
            return groups.Values.SelectMany(s => BuildCurves(s, tolerance))
                .OrderByDescending(s => s.Samples.Sum(p => p.Weight)).ToList();
        }

        private static IEnumerable<Seam> BuildCurves(List<SeamSample> samples, float tolerance)
        {
            var byEdge = samples.GroupBy(s => s.A).ToDictionary(g => g.Key, g => g.OrderBy(s => s.T).ToList());
            var joins = new Dictionary<Vector3Int, List<Boundary>>();
            float resolution = Mathf.Max(tolerance * .6f, .0003f);
            Vector3Int Key(Vector3 v) => Vector3Int.RoundToInt(v / resolution);
            foreach (var edge in byEdge.Keys)
            {
                foreach (var v in new[] { edge.A, edge.B })
                {
                    var key = Key(v);
                    if (!joins.TryGetValue(key, out var list)) joins.Add(key, list = new List<Boundary>());
                    list.Add(edge);
                }
            }
            var visited = new HashSet<Boundary>();
            foreach (var edge in byEdge.Keys)
            {
                if (!visited.Add(edge)) continue;
                var seam = new Seam { A = edge.Region, B = byEdge[edge][0].B.Region };
                var queue = new Queue<Boundary>();
                queue.Enqueue(edge);
                while (queue.Count != 0)
                {
                    var current = queue.Dequeue();
                    seam.Samples.AddRange(byEdge[current]);
                    foreach (var end in new[] { current.A, current.B })
                        foreach (var adjacent in joins[Key(end)])
                            if (visited.Add(adjacent)) queue.Enqueue(adjacent);
                }
                float length = seam.Samples.Sum(s => s.Weight);
                if (seam.Samples.Count < 3 || length < .003f) continue;
                seam.Length = length;
                if (seam.Samples.Count > 512)
                {
                    var original = seam.Samples.ToArray();
                    seam.Samples.Clear();
                    int j = 0;
                    float position = original[0].Weight;
                    for (int i = 0; i < 512; i++)
                    {
                        float next = (i + .5f) * length / 512;
                        while (j + 1 < original.Length && position < next) position += original[++j].Weight;
                        if (seam.Samples.Count != 0 && seam.Samples[seam.Samples.Count - 1] == original[j]) continue;
                        seam.Samples.Add(original[j]);
                    }
                }
                yield return seam;
            }
        }

        private static IEnumerable<Boundary> Boundaries(Region region)
        {
            var triangles = region.Triangles;
            var index = new Dictionary<(int, int), (int triangle, int corner, int count)>();
            for (int t = 0; t < triangles.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[t + k], b = triangles[t + (k + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (index.TryGetValue(key, out var previous)) index[key] = (previous.triangle, previous.corner, previous.count + 1);
                    else index.Add(key, (t, k, 1));
                }
            var world = region.Mesh.World;
            foreach (var item in index.Values)
            {
                if (item.count != 1) continue;
                int t = item.triangle, k = item.corner;
                int ia = triangles[t + k], ib = triangles[t + (k + 1) % 3], ic = triangles[t + (k + 2) % 3];
                var a = world[ia]; var b = world[ib]; var c = world[ic];
                float length = (b - a).magnitude;
                if (length < .00003f) continue;
                var along = (b - a) / length;
                var inward = (c - a) - along * Vector3.Dot(c - a, along);
                var height = inward.magnitude;
                if (height < .00003f) continue;
                var normal = (region.Mesh.Normal[ia] + region.Mesh.Normal[ib]).normalized;
                if (normal.sqrMagnitude < .1f) normal = Vector3.Cross(along, inward).normalized;
                yield return new Boundary { Region = region, V0 = ia,
                    A = a, B = b, Length = length, Inward = inward / height, Normal = normal };
            }
        }

        private sealed class BoundaryTree
        {
            private readonly Boundary[] edges;
            private readonly int start, count;
            private readonly Bounds bounds;
            private readonly BoundaryTree left, right;

            public BoundaryTree(Boundary[] edges, int start, int count)
            {
                this.edges = edges;
                this.start = start;
                this.count = count;
                bounds = new Bounds(edges[start].A, Vector3.zero);
                for (int i = start; i < start + count; i++)
                {
                    bounds.Encapsulate(edges[i].A);
                    bounds.Encapsulate(edges[i].B);
                }
                if (count <= 12) return;
                var s = bounds.size;
                int axis = s.x > s.y ? (s.x > s.z ? 0 : 2) : (s.y > s.z ? 1 : 2);
                Array.Sort(edges, start, count, Comparer<Boundary>.Create((a, b) =>
                    (a.A[axis] + a.B[axis]).CompareTo(b.A[axis] + b.B[axis])));
                left = new BoundaryTree(edges, start, count / 2);
                right = new BoundaryTree(edges, start + count / 2, count - count / 2);
            }

            public void Match(Boundary a, Vector3 p, float tolerance, ref Boundary best, ref float score)
            {
                if (bounds.SqrDistance(p) > score * score) return;
                if (left != null)
                {
                    left.Match(a, p, tolerance, ref best, ref score);
                    right.Match(a, p, tolerance, ref best, ref score);
                    return;
                }
                for (int i = start; i < start + count; i++)
                {
                    var b = edges[i];
                    if (b.Region.Id <= a.Region.Id) continue;
                    float alignment = Mathf.Abs(Vector3.Dot((a.B - a.A) / a.Length, (b.B - b.A) / b.Length));
                    if (alignment < .53f || Vector3.Dot(a.Inward, b.Inward) > .34f ||
                        Vector3.Dot(a.Normal, b.Normal) < .15f) continue;
                    float t = b.Closest(p);
                    float distance = Vector3.Distance(p, b.At(t));
                    float ranked = distance + tolerance * (.22f * (1 - alignment) + SkinPenalty(a, b));
                    if (ranked >= score) continue;
                    score = ranked;
                    best = b;
                }
            }

            private static float SkinPenalty(Boundary a, Boundary b)
            {
                var am = a.Region.Mesh; var bm = b.Region.Mesh;
                if (am.Weights == null || bm.Weights == null || am.Weights.Length <= a.V0 ||
                    bm.Weights.Length <= b.V0) return 0f;
                int i = am.Weights[a.V0].boneIndex0, j = bm.Weights[b.V0].boneIndex0;
                if (i < am.Bones.Length && j < bm.Bones.Length && am.Bones[i] == bm.Bones[j]) return 0f;
                return .08f;
            }
        }
    }
}
