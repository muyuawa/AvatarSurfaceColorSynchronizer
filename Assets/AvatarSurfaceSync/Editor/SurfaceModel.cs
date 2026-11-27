using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace AvatarSurfaceSync
{
    internal enum RegionRole { Auto, Face, Body, Reference, Target, Overlay, Ignore }
    internal enum SyncMode { SourceToTarget, Midpoint }
    internal enum CaptureMode { Composite, Neutral, Full, Identity }

    [Serializable]
    internal sealed class SyncSettings
    {
        public SyncMode Mode;
        public float Strength = 1f, Lightness = 1f, Chroma = 1f, Seam = 1f, Protect = .92f;
        public float WidthMm;
        public float MatchMm = 4f;
        public bool Screened = true;
        public bool AllowWholeSkin;
        public const float MinManualNeckWidthMm = 15f;
        public bool NeckWidthTooNarrow => SeamOnly && WidthMm > 0 && WidthMm < MinManualNeckWidthMm;
        public static string NarrowNeckWarning => $"手动颈线宽度小于 {MinManualNeckWidthMm:G} mm 时容易形成断开的色斑，未生成结果。请选择更宽的过渡或使用自动宽度。";
        // Older editor scripts can still set SeamOnly. Keep the serialized UI scope as the single source of truth.
        public bool SeamOnly
        {
            get => !AllowWholeSkin;
            set => AllowWholeSkin = !value;
        }
        public int PreviewSize = 512, OutputSize = 1024, Iterations = 3;

        public SyncSettings Copy() => (SyncSettings)MemberwiseClone();
    }

    internal sealed class Region
    {
        public int Id, Slot, Submesh;
        public Renderer Source;
        public SurfaceMesh Mesh;
        public Material Original;
        public MaterialPropertyBlock Block;
        public ShaderAdapter Adapter;
        public RegionRole Role, Suggested;
        public Texture Protection, Skin;
        public int[] Triangles;
        public Transform HeadBone;
        public RegionRole Effective => Role == RegionRole.Auto ? Suggested : Role;
        public bool SkinRegion => Effective == RegionRole.Face || Effective == RegionRole.Body ||
                                  Effective == RegionRole.Reference || Effective == RegionRole.Target;
        public bool Enabled => Source.enabled && Source.gameObject.activeInHierarchy && !Source.forceRenderingOff;
        public bool CanBake => Adapter.CanBake(this, out _);
        public string Label => Source.name + " [" + Slot + "]  " + Original.name;
    }

    internal sealed class SurfaceMesh
    {
        public Renderer Renderer;
        public Mesh RenderMesh;
        public bool OwnsMesh;
        public Vector3[] World, Normal;
        public Vector2[] Uv;
        public BoneWeight[] Weights;
        public Transform[] Bones;
        public readonly List<Region> Regions = new List<Region>();
    }

    internal sealed class SurfaceModel : IDisposable
    {
        public readonly GameObject Root;
        public readonly Vector3 Origin;
        public readonly List<SurfaceMesh> Meshes = new List<SurfaceMesh>();
        public readonly List<Region> Regions = new List<Region>();
        public readonly List<string> Warnings = new List<string>();
        public readonly Matrix4x4 ToModel;
        public SurfaceModel(GameObject root)
        {
            Root = root;
            Origin = root.transform.position;
            ToModel = root.transform.worldToLocalMatrix;
            try
            {
                var animator = root.GetComponentInChildren<Animator>(true);
                var head = animator && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var skin = renderer as SkinnedMeshRenderer;
                    var source = skin ? skin.sharedMesh : (renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>()?.sharedMesh : null);
                    if (!source || source.subMeshCount == 0) continue;
                    var mesh = source;
                    if (skin)
                    {
                        mesh = new Mesh { name = source.name + " (surface snapshot)", hideFlags = HideFlags.HideAndDontSave };
                        skin.BakeMesh(mesh, false);
                    }
                    var data = new SurfaceMesh { Renderer = renderer, RenderMesh = mesh, OwnsMesh = skin, Bones = skin ? skin.bones : null };
                    Meshes.Add(data);
                    var matrix = renderer.localToWorldMatrix;
                    var normalMatrix = matrix.inverse.transpose;
                    if (!mesh.isReadable)
                    {
                        Warnings.Add(renderer.name + "：无法读取网格。仍用于完整渲染。");
                    }
                    else
                    {
                        data.World = mesh.vertices.Select(v => matrix.MultiplyPoint3x4(v) - Origin).ToArray();
                        var normals = mesh.normals;
                        data.Normal = normals.Length == data.World.Length
                            ? normals.Select(n => normalMatrix.MultiplyVector(n).normalized).ToArray()
                            : new Vector3[data.World.Length];
                        data.Uv = mesh.uv;
                        if (skin && source.isReadable) data.Weights = source.boneWeights;
                    }
                    var materials = renderer.sharedMaterials;
                    for (var slot = 0; slot < materials.Length; slot++)
                    {
                        if (!materials[slot]) continue;
                        var submesh = Mathf.Min(slot, mesh.subMeshCount - 1);
                        var triangles = data.World != null && mesh.GetTopology(submesh) == MeshTopology.Triangles
                            ? mesh.GetTriangles(submesh) : Array.Empty<int>();
                        var propertyBlock = new MaterialPropertyBlock();
                        renderer.GetPropertyBlock(propertyBlock, slot);
                        if (propertyBlock.isEmpty) renderer.GetPropertyBlock(propertyBlock);
                        var region = new Region
                        {
                            Id = Regions.Count + 1, Source = renderer, Slot = slot, Submesh = submesh,
                            Mesh = data, Original = materials[slot], Adapter = ShaderAdapter.Resolve(materials[slot]),
                            Triangles = triangles, HeadBone = head, Block = propertyBlock
                        };
                        region.Suggested = Suggest(region);
                        if (region.Original.HasProperty("_DewSkinMask")) region.Skin = region.Original.GetTexture("_DewSkinMask");
                        Regions.Add(region);
                        data.Regions.Add(region);
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static RegionRole Suggest(Region r)
        {
            var name = (r.Source.name + " " + r.Original.name).ToLowerInvariant();
            if (r.Slot >= r.Mesh.RenderMesh.subMeshCount || r.Adapter.IsTransparent(r.Original))
                return RegionRole.Overlay;
            if (r.Triangles.Length == 0) return RegionRole.Ignore;
            if (name.Contains("eyelash") || name.Contains("eyebrow") || name.Contains("teeth") ||
                name.Contains("pupil") || name.Contains("mouth") || name.Contains("nail") ||
                name.Contains("necklace") || name.Contains("watch") || name.Contains("jewel") ||
                name.Contains("earring") || name.Contains("metal") || name.Contains("cloth") ||
                name.Contains("eyelens") || name.Contains("hair"))
                return RegionRole.Ignore;
            if (name.Contains("face") || name.Contains("head") || name.Contains("顔")) return RegionRole.Face;
            if (name.Contains("skin") || name.Contains("body") || name.Contains("neck") ||
                name.Contains("wrist") || name.Contains("肌")) return RegionRole.Body;
            if (r.Original.GetTag("RenderType", false, "") == "TransparentCutout") return RegionRole.Ignore;
            if (!r.HeadBone || r.Triangles.Length < 60) return RegionRole.Ignore;
            var used = new HashSet<int>(r.Triangles);
            var mesh = r.Mesh;
            if (mesh.Weights == null || mesh.Weights.Length != mesh.World.Length || mesh.Bones == null)
                return RegionRole.Ignore;
            var bone = Array.IndexOf(mesh.Bones, r.HeadBone);
            if (bone < 0) return RegionRole.Ignore;
            int count = used.Count(i => mesh.Weights[i].boneIndex0 == bone && mesh.Weights[i].weight0 > .65f);
            return count > used.Count * .60f ? RegionRole.Face : RegionRole.Ignore;
        }

        public void Dispose()
        {
            foreach (var mesh in Meshes)
                if (mesh.OwnsMesh && mesh.RenderMesh) Object.DestroyImmediate(mesh.RenderMesh);
            Meshes.Clear();
        }
    }
}
