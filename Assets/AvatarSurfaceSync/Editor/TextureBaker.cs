using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace AvatarSurfaceSync
{
    internal sealed class Trial : IDisposable
    {
        public readonly Dictionary<Region, Material> Materials = new Dictionary<Region, Material>();
        public readonly Dictionary<Region, Texture2D> Textures = new Dictionary<Region, Texture2D>();
        public readonly HashSet<Region> SecondMain = new HashSet<Region>();
        public readonly Dictionary<Region, Bounds> LocalBands = new Dictionary<Region, Bounds>();
        public void Dispose()
        {
            foreach (var material in Materials.Values) if (material) Object.DestroyImmediate(material);
            foreach (var texture in Textures.Values) if (texture) Object.DestroyImmediate(texture);
            Materials.Clear();
            Textures.Clear();
            SecondMain.Clear();
            LocalBands.Clear();
        }
    }

    internal static class TextureBaker
    {
        public static Trial Apply(IReadOnlyDictionary<Region, Correction> changes, Trial previous, SyncSettings settings,
            int requestedSize, HashSet<string> warnings, bool directBase = false)
        {
            var current = new Trial();
            try
            {
                if (previous != null)
                {
                    current.SecondMain.UnionWith(previous.SecondMain);
                    foreach (var band in previous.LocalBands) current.LocalBands.Add(band.Key, band.Value);
                    foreach (var material in previous.Materials)
                    {
                        current.Materials.Add(material.Key, Clone(material.Value));
                        if (!previous.Textures.TryGetValue(material.Key, out var texture)) continue;
                        var copy = Object.Instantiate(texture);
                        copy.hideFlags = HideFlags.HideAndDontSave;
                        current.Textures.Add(material.Key, copy);
                        current.Materials[material.Key].SetTexture(ShaderAdapter.TextureProperty(current.SecondMain.Contains(material.Key)), copy);
                    }
                }
                foreach (var pair in changes)
                {
                    var region = pair.Key;
                    var correction = pair.Value;
                    // Direct-base calibration includes an unshifted control bake at the same resolution.
                    if (settings.SeamOnly ? !correction.HasLocal : !directBase && correction.Global.sqrMagnitude < 1e-8f && !correction.HasLocal) continue;
                    if (correction.SecondMain && !settings.SeamOnly) continue;
                    string property = ShaderAdapter.TextureProperty(correction.SecondMain);
                    bool existing = current.Materials.TryGetValue(region, out var material);
                    if (existing && current.SecondMain.Contains(region) != correction.SecondMain) continue;
                    if (!existing)
                    {
                        material = Clone(region.Original);
                        current.Materials.Add(region, material);
                    }
                    var input = material.GetTexture(property) as Texture2D;
                    bool native = settings.SeamOnly || requestedSize == 0;
                    int width = native ? Mathf.Min(8192, input ? input.width : 1024) : requestedSize;
                    int height = native ? Mathf.Min(8192, input ? input.height : 1024) : requestedSize;
                    bool oversize = settings.SeamOnly && input && (input.width != width || input.height != height);
                    if (oversize) warnings.Add(region.Label + "：源贴图过大，无法在不缩小贴图的情况下仅融合颈线。");
                    var texture = oversize ? null : Bake(region, material, correction, width, height, settings, warnings, directBase);
                    if (!texture)
                    {
                        if (!existing) { current.Materials.Remove(region); Object.DestroyImmediate(material); }
                        continue;
                    }
                    if (current.Textures.TryGetValue(region, out var old)) Object.DestroyImmediate(old);
                    current.Textures[region] = texture;
                    material.SetTexture(property, texture);
                    if (correction.SecondMain) current.SecondMain.Add(region);
                    if (settings.SeamOnly)
                    {
                        var area = correction.Area;
                        if (current.LocalBands.TryGetValue(region, out var before)) area.Encapsulate(before);
                        current.LocalBands[region] = area;
                    }
                }
                return current;
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }

        private static Material Clone(Material source) => new Material(source)
        { name = source.name + " · Surface Sync", hideFlags = HideFlags.HideAndDontSave };

        private static Texture2D Bake(Region region, Material material, Correction correction, int w, int h,
            SyncSettings settings, HashSet<string> warnings, bool directBase)
        {
            var source = material.GetTexture(ShaderAdapter.TextureProperty(correction.SecondMain)) as Texture2D;
            var mesh = region.Mesh.RenderMesh;
            Mesh atlas = null;
            Material pass = null;
            RenderTexture raster = null, overlapRaster = null;
            var old = RenderTexture.active;
            bool srgb = GL.sRGBWrite;
            try
            {
                atlas = new Mesh { indexFormat = mesh.indexFormat, hideFlags = HideFlags.HideAndDontSave };
                atlas.vertices = mesh.vertices;
                var tangents = mesh.tangents;
                if (tangents.Length == atlas.vertexCount)
                {
                    if (region.Source.localToWorldMatrix.determinant < 0)
                        for (int i = 0; i < tangents.Length; i++) tangents[i].w *= -1;
                    atlas.tangents = tangents;
                }
                var uvs = new List<Vector4>();
                for (int i = 0; i < 4; i++)
                {
                    mesh.GetUVs(i, uvs);
                    if (uvs.Count == atlas.vertexCount) atlas.SetUVs(i, uvs);
                }
                atlas.SetIndices(region.Triangles, MeshTopology.Triangles, 0, false);
                if (correction.HasLocal) atlas.SetUVs(4, region.Mesh.World);
                pass = region.Adapter.Proxy(region, material, false);
                pass.SetVector("_Global", correction.Global);
                pass.SetFloat("_DirectBase", directBase ? 1 : 0);
                pass.SetFloat("_Width", correction.Width);
                pass.SetFloat("_Protection", settings.Protect);
                pass.SetFloat("_SeamOnly", settings.SeamOnly ? 1 : 0);
                pass.SetFloat("_BakeSecond", correction.SecondMain ? 1 : 0);
                if (correction.HasLocal)
                {
                    pass.SetInt("_NeckCount", correction.Anchors.Length);
                    pass.SetVectorArray("_NeckAnchors", correction.Anchors);
                    pass.SetVectorArray("_NeckDeltas", correction.Deltas);
                    pass.SetVector("_NeckMin", correction.Area.min);
                    pass.SetVector("_NeckMax", correction.Area.max);
                }
                raster = SurfacePreview.CreateTarget(w, false, h, 0);
                GL.sRGBWrite = false;
                RenderTexture.active = raster;
                GL.Clear(false, true, Color.clear);
                Draw(pass, atlas, 2);
                if (settings.SeamOnly)
                {
                    raster.filterMode = FilterMode.Point;
                    raster.wrapMode = TextureWrapMode.Clamp;
                    pass.SetTexture("_NeckCoverage", raster);
                    return BakeLocal(source, pass, atlas, w, h, region.Original.name);
                }
                var coverage = SurfacePreview.Read(raster, false);
                var mask = new byte[coverage.Length];
                bool localOverlap = false;
                for (int i = 0; i < mask.Length; i++)
                {
                    var px = coverage[i];
                    mask[i] = px.a > .5f ? (byte)1 : (byte)0;
                    localOverlap |= px.a > 1.5f && px.r + px.g + px.b > .003f;
                }
                if (localOverlap)
                {
                    warnings.Add(region.Label + "：重叠 UV 处仅保留可共享的局部修正。");
                    overlapRaster = raster;
                    overlapRaster.filterMode = FilterMode.Point;
                    pass.SetTexture("_NeckCoverage", overlapRaster);
                    pass.SetFloat("_CheckNeckOverlap", 1);
                    raster = SurfacePreview.CreateTarget(w, false, h, 0);
                }
                coverage = null;
                Graphics.Blit(source ? source : Texture2D.whiteTexture, raster);
                RenderTexture.active = raster;
                GL.sRGBWrite = false;
                Draw(pass, atlas, 1);
                var result = SurfacePreview.Read(raster, false);
                var pixels = new Color32[result.Length];
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                for (int i = 0; i < pixels.Length; i++) pixels[i] = linear ? result[i].gamma : result[i];
                Pad(pixels, mask, w, h, Mathf.Clamp(Mathf.Min(w, h) / 128, 4, 24));
                var texture = new Texture2D(w, h, TextureFormat.RGBA32, true, false)
                {
                    name = region.Original.name + " Sync Base",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapModeU = source ? source.wrapModeU : TextureWrapMode.Repeat,
                    wrapModeV = source ? source.wrapModeV : TextureWrapMode.Repeat,
                    filterMode = source ? source.filterMode : FilterMode.Bilinear,
                    anisoLevel = source ? source.anisoLevel : 1
                };
                texture.SetPixels32(pixels);
                texture.Apply(true, false);
                return texture;
            }
            finally
            {
                GL.sRGBWrite = srgb;
                RenderTexture.active = old;
                if (raster) Object.DestroyImmediate(raster);
                if (overlapRaster) Object.DestroyImmediate(overlapRaster);
                if (atlas) Object.DestroyImmediate(atlas);
                if (pass) Object.DestroyImmediate(pass);
            }
        }

        private static Texture2D BakeLocal(Texture2D source, Material pass, Mesh atlas, int w, int h, string name)
        {
            bool srgb = !source || source.isDataSRGB;
            bool encode = srgb && QualitySettings.activeColorSpace == ColorSpace.Linear;
            int mipCount = source ? source.mipmapCount : Texture2D.whiteTexture.mipmapCount;
            var format = encode ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            var previousTarget = RenderTexture.active;
            var target = new RenderTexture(new RenderTextureDescriptor(w, h, format, 0)) { hideFlags = HideFlags.HideAndDontSave };
            Texture2D output = null, before = null;
            try
            {
                if (!target.Create()) throw new InvalidOperationException("无法分配原尺寸的局部烘焙贴图。");
                output = new Texture2D(w, h, TextureFormat.RGBA32, mipCount, !srgb)
                {
                    name = name + " Sync Base", hideFlags = HideFlags.HideAndDontSave,
                    wrapModeU = source ? source.wrapModeU : TextureWrapMode.Repeat,
                    wrapModeV = source ? source.wrapModeV : TextureWrapMode.Repeat,
                    filterMode = source ? source.filterMode : FilterMode.Bilinear,
                    anisoLevel = source ? source.anisoLevel : 1,
                    mipMapBias = source ? source.mipMapBias : 0
                };
                var rect = new Rect(0, 0, w, h);
                int mipPass = pass.FindPass("LOCAL_MIP");
                pass.SetTexture("_MipSource", source ? source : Texture2D.whiteTexture);
                pass.SetFloat("_LocalMip", 0);
                GL.sRGBWrite = encode;
                Graphics.Blit(null, target, pass, mipPass);
                RenderTexture.active = target;
                if (mipCount > 1)
                {
                    before = new Texture2D(w, h, TextureFormat.RGBA32, mipCount, !srgb) { hideFlags = HideFlags.HideAndDontSave };
                    before.ReadPixels(rect, 0, 0, false);
                    before.Apply(true, false);
                }
                // Only inside-band fragments may write. No resampling or padding of other UV islands.
                GL.sRGBWrite = encode;
                Draw(pass, atlas, 1);
                output.ReadPixels(rect, 0, 0, false);
                output.Apply(mipCount > 1, false);
                if (before) RestoreSourceMips(output, before, pass, mipPass, format, encode);
                return output;
            }
            catch
            {
                if (output) Object.DestroyImmediate(output);
                throw;
            }
            finally
            {
                RenderTexture.active = previousTarget;
                Object.DestroyImmediate(target);
                if (before) Object.DestroyImmediate(before);
            }
        }

        private static void RestoreSourceMips(Texture2D output, Texture2D before, Material pass, int mipPass, GraphicsFormat format, bool encode)
        {
            pass.SetTexture("_MipBefore", before);
            pass.SetTexture("_MipAfter", output);
            var previousTarget = RenderTexture.active;
            for (int mip = 1; mip < output.mipmapCount; mip++)
            {
                int w = Mathf.Max(1, output.width >> mip), h = Mathf.Max(1, output.height >> mip);
                var target = RenderTexture.GetTemporary(new RenderTextureDescriptor(w, h, format, 0));
                var readback = new Texture2D(w, h, TextureFormat.RGBA32, false, !output.isDataSRGB) { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    pass.SetFloat("_LocalMip", mip);
                    GL.sRGBWrite = encode;
                    Graphics.Blit(null, target, pass, mipPass);
                    RenderTexture.active = target;
                    readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                    output.SetPixelData(readback.GetRawTextureData<Color32>(), mip);
                }
                finally
                {
                    RenderTexture.active = previousTarget;
                    RenderTexture.ReleaseTemporary(target);
                    Object.DestroyImmediate(readback);
                }
            }
            output.Apply(false, false);
        }

        private static void Draw(Material material, Mesh mesh, int pass)
        {
            if (!material.SetPass(pass)) throw new InvalidOperationException("GPU 烘焙 Pass 不可用。");
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
        }

        private static void Pad(Color32[] colors, byte[] coverage, int width, int height, int passes)
        {
            var frontier = new List<int>();
            for (int i = 0; i < coverage.Length; i++)
            {
                if (coverage[i] != 1) continue;
                int x = i % width;
                if ((x > 0 && coverage[i - 1] == 0) || (x + 1 < width && coverage[i + 1] == 0) ||
                    (i >= width && coverage[i - width] == 0) || (i + width < coverage.Length && coverage[i + width] == 0)) frontier.Add(i);
            }
            var next = new List<int>();
            for (int step = 0; step < passes && frontier.Count != 0; step++)
            {
                foreach (var index in frontier)
                {
                    int x = index % width;
                    if (x > 0) Visit(index, index - 1);
                    if (x + 1 < width) Visit(index, index + 1);
                    if (index >= width) Visit(index, index - width);
                    if (index + width < coverage.Length) Visit(index, index + width);
                }
                frontier.Clear();
                var previous = frontier;
                frontier = next;
                next = previous;
            }

            void Visit(int src, int dst)
            {
                if (coverage[dst] != 0) return;
                coverage[dst] = 1;
                colors[dst] = colors[src];
                next.Add(dst);
            }
        }
    }
}
