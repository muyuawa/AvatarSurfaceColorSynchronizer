using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AvatarSurfaceSync
{
    internal sealed class SyncResult : IDisposable
    {
        public Trial Trial;
        public ErrorStats Before, After, CoolBefore, CoolAfter;
        public readonly List<NeckSample> Neck = new List<NeckSample>();
        public readonly HashSet<string> Warnings = new HashSet<string>();
        public readonly List<string> Diagnostics = new List<string>();
        public void Dispose() => Trial?.Dispose();
    }

    internal static class SyncSession
    {
        public static bool BetweenGroups(Seam s, HashSet<Region> originals, HashSet<Region> targets) =>
            originals.Contains(s.A) && targets.Contains(s.B) || originals.Contains(s.B) && targets.Contains(s.A);

        public static SyncResult Optimize(SurfacePreview scene, Bounds frame, Vector3[] directions, HashSet<Region> originals,
            HashSet<Region> targets, SyncSettings settings, bool output, Action<string, float> status)
        {
            var result = new SyncResult();
            if (settings.NeckWidthTooNarrow)
            {
                result.Warnings.Add(SyncSettings.NarrowNeckWarning);
                return result;
            }
            if (frame.size.sqrMagnitude <= 0)
            {
                result.Warnings.Add("无法定位头部和脖子。请选择相接的皮肤材质槽。");
                return result;
            }
            try
            {
                var skin = new HashSet<Region>(originals);
                skin.UnionWith(targets);
                int size = settings.PreviewSize;
                status?.Invoke("从实际渲染定位头部与脖子的颜色交界", .1f);
                var baseline = scene.CaptureNeck(frame, directions, size, null, skin);
                result.Neck.AddRange(NeckBand.Find(baseline, frame, originals, targets));
                result.Before = result.After = NeckBand.Measure(baseline, result.Neck, true);
                if (result.Before.Count < 6)
                {
                    result.Warnings.Add("选中的材质之间只有 " + result.Before.Count + " 对可信颈线采样，无法可靠调色。");
                    return result;
                }

                int textureSize = output ? settings.OutputSize : Mathf.Clamp(size * 2, 512, 1536);
                Capture Render(Trial trial) => scene.CaptureNeck(frame, directions, size, trial.Materials, skin, geometry: baseline);
                Capture body = null;
                var bodyFrame = settings.SeamOnly ? NeckBand.SkinFrame(skin) : default;
                var bodyDirections = settings.SeamOnly ? directions.Where((_, i) => i % 2 == 0).ToArray() : null;
                int bodySize = Mathf.Clamp(size, 256, 512);

                bool Check(Dictionary<Region, Correction> correction, string label, bool directBase = false)
                {
                    var trial = TextureBaker.Apply(correction, result.Trial, settings, textureSize, result.Warnings, directBase);
                    try
                    {
                        if (trial.Materials.Count == 0)
                        {
                            result.Diagnostics.Add(label + "：没有可烘焙的修正。");
                            return false;
                        }
                        var score = NeckBand.Measure(Render(trial), result.Neck);
                        bool covered = score.Count >= result.Before.Count * .95f;
                        bool accepted = covered && score.Loss < result.After.Loss - .0001f;
                        if (accepted && settings.SeamOnly)
                        {
                            body ??= scene.CaptureNeck(bodyFrame, bodyDirections, bodySize, null, skin);
                            var full = scene.CaptureNeck(bodyFrame, bodyDirections, bodySize, trial.Materials, skin, geometry: body);
                            float outside = NeckBand.OutsideDifference(body, full, trial.LocalBands);
                            if (outside > .02f)
                            {
                                result.Diagnostics.Add($"{label}：{score.Label}（存在融合带外的变化，拒绝输出；最大色差 {outside:F4}）");
                                return false;
                            }
                        }
                        result.Diagnostics.Add($"{label}：{score.Label}（{(accepted ? "通过" : covered ? "未优于当前" : "有效样本不足")}）");
                        if (!accepted) return false;
                        result.Trial?.Dispose();
                        result.Trial = trial;
                        trial = null;
                        result.After = score;
                        return true;
                    }
                    finally { trial?.Dispose(); }
                }

                void Iterate(HashSet<Region> second = null)
                {
                    float gain = 1f;
                    string label = second != null ? "颈线融合 · 第二主贴图" : settings.SeamOnly ? "颈线融合" : "合成校色";
                    for (int i = 0; i < settings.Iterations; i++)
                    {
                        status?.Invoke($"{label}（{i + 1}/{settings.Iterations}）", (second != null ? .55f : .24f) + .30f * i / settings.Iterations);
                        var correction = CorrectionSolver.Solve(result.Neck, originals, targets, settings, gain);
                        if (second != null)
                        {
                            if (!correction.Any(p => second.Contains(p.Key) && p.Value.HasLocal)) break;
                            foreach (var pair in correction) pair.Value.SecondMain = second.Contains(pair.Key);
                        }
                        if (correction.Values.All(c => c.Global.sqrMagnitude < 1e-8f && !c.HasLocal)) break;
                        if (!Check(correction, $"{label} #{i + 1}"))
                        {
                            gain *= .5f;
                            NeckBand.Measure(result.Trial == null ? baseline : Render(result.Trial), result.Neck);
                            continue;
                        }
                        gain = 1f;
                        if (result.After.Mean <= .0055f && result.After.P95 <= .015f || settings.Strength < .999f) break;
                    }
                }

                Iterate();
                if (result.Trial == null && settings.SeamOnly && settings.Seam > 0)
                {
                    var adjustable = settings.Mode == SyncMode.Midpoint ? skin : originals;
                    var second = new HashSet<Region>(adjustable.Where(r => r.Adapter.CanBakeSecond(r)));
                    if (second.Count > 0)
                    {
                        NeckBand.Measure(baseline, result.Neck);
                        Iterate(second);
                    }
                }
                if (result.Trial == null && !settings.SeamOnly)
                {
                    NeckBand.Measure(baseline, result.Neck);
                    var global = settings.Copy();
                    global.Seam = 0;
                    var requested = CorrectionSolver.Solve(result.Neck, originals, targets, global, 1);
                    // Probes are temporary; only bake the validated output at the full requested resolution.
                    int probeSize = textureSize > 0 ? Mathf.Min(textureSize, 1024) : 1024;
                    var changes = CalibrateGlobal(requested, result, Render, settings, probeSize, status);
                    for (int i = 0; i < settings.Iterations && changes.Count > 0; i++)
                    {
                        status?.Invoke($"复核主贴图校色（{i + 1}/{settings.Iterations}）", .74f + .12f * i / settings.Iterations);
                        if (Check(changes, $"实渲染整体校色 #{i + 1}", directBase: true)) break;
                        foreach (var change in changes.Values) change.Global *= .5f;
                    }
                }

                if (result.Trial == null)
                {
                    result.Warnings.Add(settings.SeamOnly
                        ? "没有找到通过复核的颈线融合，不会改全身。共用到融合带外的 UV 和保护区域会保留原色。"
                        : "重渲染未确认颈线色差改善，未生成输出。");
                    NeckBand.Measure(baseline, result.Neck);
                    return result;
                }
                status?.Invoke("检查冷光下的颈线颜色", .9f);
                var cool = scene.CaptureNeck(frame, directions, size, null, skin, lights: 2, geometry: baseline);
                result.CoolBefore = NeckBand.Measure(cool, result.Neck);
                result.CoolAfter = NeckBand.Measure(scene.CaptureNeck(frame, directions, size, result.Trial.Materials, skin,
                    lights: 2, geometry: baseline), result.Neck);
                NeckBand.Measure(scene.CaptureNeck(frame, directions, size, result.Trial.Materials, skin, geometry: baseline), result.Neck);
                if (result.After.P95 > .025f)
                    result.Warnings.Add("颈线仍有局部色差，切换到颈线特写可检查残余位置。");
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static Dictionary<Region, Correction> CalibrateGlobal(Dictionary<Region, Correction> requested, SyncResult result,
            Func<Trial, Capture> render, SyncSettings settings, int textureSize, Action<string, float> status)
        {
            var regions = requested.Where(p => p.Value.Global.sqrMagnitude >= 1e-8f).Select(p => p.Key).ToArray();
            var changes = new Dictionary<Region, Correction>();
            if (regions.Length == 0) return changes;

            // Use identical bakes for the control and probe so resampling isn't counted as a response.
            var probes = regions.ToDictionary(r => r, _ => new Correction());
            var control = new Dictionary<NeckSample, (Vector3 A, Vector3 B)>();
            status?.Invoke("测量主贴图对颈线的影响（对照）", .55f);
            using (var zero = TextureBaker.Apply(probes, null, settings, textureSize, result.Warnings, directBase: true))
            {
                var capture = render(zero);
                foreach (var sample in result.Neck)
                    if (sample.Measured && NeckBand.TryColors(capture, sample, out var a, out var b)) control.Add(sample, (a, b));
            }
            if (control.Count < result.Before.Count * .95f)
            {
                result.Diagnostics.Add("主贴图响应：对照重渲染的有效样本不足。");
                return changes;
            }

            var response = regions.ToDictionary(r => r, _ => new Vector3[3]);
            var coverage = regions.ToDictionary(r => r, _ => new bool[3]);
            var weights = regions.ToDictionary(r => r, _ => 0f);
            foreach (var sample in control.Keys)
            {
                if (weights.ContainsKey(sample.A)) weights[sample.A] += sample.Weight;
                if (weights.ContainsKey(sample.B)) weights[sample.B] += sample.Weight;
            }
            const float step = .028f;
            for (int axis = 0; axis < 3; axis++)
            {
                status?.Invoke($"测量主贴图对颈线的影响（{axis + 1}/3）", .59f + .045f * axis);
                foreach (var region in regions)
                {
                    var shift = Vector3.zero;
                    shift[axis] = requested[region].Global[axis] < 0 ? -step : step;
                    probes[region].Global = shift;
                }
                var seen = regions.ToDictionary(r => r, _ => 0f);
                using (var probe = TextureBaker.Apply(probes, null, settings, textureSize, result.Warnings, directBase: true))
                {
                    var capture = render(probe);
                    foreach (var pair in control)
                    {
                        var sample = pair.Key;
                        if (!NeckBand.TryColors(capture, sample, out var a, out var b)) continue;
                        Add(sample.A, a - pair.Value.A, sample.Weight);
                        Add(sample.B, b - pair.Value.B, sample.Weight);
                    }
                }
                foreach (var region in regions)
                {
                    coverage[region][axis] = seen[region] > 0 && seen[region] >= weights[region] * .95f;
                    response[region][axis] = coverage[region][axis] ? response[region][axis] / seen[region] : Vector3.zero;
                }

                void Add(Region region, Vector3 delta, float weight)
                {
                    if (!response.TryGetValue(region, out var columns)) return;
                    columns[axis] += delta * (weight / probes[region].Global[axis]);
                    seen[region] += weight;
                }
            }

            foreach (var region in regions)
            {
                var columns = response[region];
                string Axis(int i) => coverage[region][i] ? columns[i].magnitude.ToString("F2") : "样本不足";
                result.Diagnostics.Add($"{region.Label}：主贴图颈线响应 L {Axis(0)} / a {Axis(1)} / b {Axis(2)}");
                if (!coverage[region].Any(x => x)) continue;
                if (columns.All(c => c.sqrMagnitude < .04f * .04f))
                {
                    result.Diagnostics.Add("当前保护设置下，所测主贴图改动几乎没有传到这处颈线。");
                    continue;
                }
                var desired = requested[region].Global;
                var shift = Vector3.ClampMagnitude(FitResponse(columns, desired), Mathf.Min(.23f, 3 * desired.magnitude));
                if (shift.sqrMagnitude >= 1e-8f) changes.Add(region, new Correction { Global = shift });
            }
            return changes;
        }

        private static Vector3 FitResponse(Vector3[] response, Vector3 desired)
        {
            // (JᵀJ + λ²I)⁻¹ Jᵀd. Damping avoids a large correction along poorly observed axes.
            const float damping = .12f * .12f;
            var a = response[0]; var b = response[1]; var c = response[2];
            float ab = Vector3.Dot(a, b), ac = Vector3.Dot(a, c), bc = Vector3.Dot(b, c);
            var x = new Vector3(a.sqrMagnitude + damping, ab, ac);
            var y = new Vector3(ab, b.sqrMagnitude + damping, bc);
            var z = new Vector3(ac, bc, c.sqrMagnitude + damping);
            var rhs = new Vector3(Vector3.Dot(a, desired), Vector3.Dot(b, desired), Vector3.Dot(c, desired));
            var p = Vector3.Cross(y, z);
            float determinant = Vector3.Dot(x, p);
            if (!(determinant > 1e-10f)) return Vector3.zero;
            return new Vector3(Vector3.Dot(rhs, p), Vector3.Dot(rhs, Vector3.Cross(z, x)),
                Vector3.Dot(rhs, Vector3.Cross(x, y))) / determinant;
        }
    }
}
