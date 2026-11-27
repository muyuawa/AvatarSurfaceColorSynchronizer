using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AvatarSurfaceSync
{
    internal sealed class SurfacePreview : IDisposable
    {
        private readonly SurfaceModel model;
        private readonly Scene scene;
        private readonly Camera camera;
        private readonly Light key, fill;
        private readonly Dictionary<SurfaceMesh, MeshRenderer> copies = new Dictionary<SurfaceMesh, MeshRenderer>();
        private readonly Dictionary<Transform, Transform> transforms = new Dictionary<Transform, Transform>();
        private readonly List<Material> proxies = new List<Material>();
        private readonly Material hidden;
        public Bounds Bounds { get; private set; }
        public Matrix4x4 ViewProjection => camera.projectionMatrix * camera.worldToCameraMatrix;

        public SurfacePreview(SurfaceModel model)
        {
            this.model = model;
            scene = EditorSceneManager.NewPreviewScene();
            try
            {
                camera = NewObject("Surface preview camera").AddComponent<Camera>();
                camera.cameraType = CameraType.Preview;
                camera.scene = scene;
                camera.enabled = false;
                camera.allowMSAA = false;
                camera.allowHDR = true;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.orthographic = true;
                camera.renderingPath = RenderingPath.Forward;
                key = NewLight("Key");
                fill = NewLight("Fill");
                var composite = Shader.Find("Hidden/AvatarSurfaceSync/Composite");
                if (!composite || !composite.isSupported) throw new InvalidOperationException("无法加载 GPU 合成 Shader。");
                hidden = new Material(composite) { hideFlags = HideFlags.HideAndDontSave };
                hidden.SetFloat("_ColorMask", 0);
                hidden.SetFloat("_ZWrite", 0);
                foreach (var mesh in model.Meshes)
                {
                    var go = CopyTransform(mesh.Renderer.transform).gameObject;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh.RenderMesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sortingLayerID = mesh.Renderer.sortingLayerID;
                    renderer.sortingOrder = mesh.Renderer.sortingOrder;
                    renderer.shadowCastingMode = mesh.Renderer.shadowCastingMode;
                    renderer.receiveShadows = mesh.Renderer.receiveShadows;
                    renderer.enabled = mesh.Regions.Any(r => r.Enabled);
                    copies.Add(mesh, renderer);
                }
                SetMaterials(CaptureMode.Full, null, false);
                bool any = false;
                foreach (var copy in copies.Values.Where(r => r.enabled))
                {
                    if (!any) { Bounds = copy.bounds; any = true; }
                    else { var b = Bounds; b.Encapsulate(copy.bounds); Bounds = b; }
                }
                if (!any) Bounds = new Bounds(Vector3.zero, Vector3.one);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(go, scene);
            return go;
        }

        private Transform CopyTransform(Transform original)
        {
            if (transforms.TryGetValue(original, out var existing)) return existing;
            var copy = NewObject(original.name).transform;
            transforms.Add(original, copy);
            if (original.parent != null)
            {
                copy.SetParent(CopyTransform(original.parent), false);
                copy.localPosition = original.localPosition;
            }
            else copy.localPosition = original.localPosition - model.Origin;
            copy.localRotation = original.localRotation;
            copy.localScale = original.localScale;
            return copy;
        }

        private Light NewLight(string name)
        {
            var light = NewObject(name).AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            return light;
        }

        public void SetMaterials(CaptureMode mode, IReadOnlyDictionary<Region, Material> changes, bool omitOverlays,
            ISet<Region> skin = null)
        {
            foreach (var proxy in proxies) Object.DestroyImmediate(proxy);
            proxies.Clear();
            var skinMeshes = skin == null ? null : new HashSet<SurfaceMesh>(skin.Select(r => r.Mesh));
            bool Visible(Region r) => r.Enabled && (skin == null || skin.Contains(r) ||
                r.Effective == RegionRole.Overlay && (skinMeshes.Contains(r.Mesh) || r.Role == RegionRole.Overlay));
            foreach (var pair in copies)
            {
                var mesh = pair.Key;
                var copy = pair.Value;
                copy.enabled = mesh.Regions.Any(Visible);
                if (!copy.enabled) continue;
                var materials = new Material[mesh.Renderer.sharedMaterials.Length];
                foreach (var region in mesh.Regions)
                {
                    if (!Visible(region)) continue;
                    var source = changes != null && changes.TryGetValue(region, out var edited) ? edited : region.Original;
                    bool over = region.Adapter.IsTransparent(source) || region.Effective == RegionRole.Overlay;
                    Material used = source;
                    if (over && (omitOverlays || mode == CaptureMode.Identity)) used = hidden;
                    else if (mode == CaptureMode.Identity || mode == CaptureMode.Composite)
                    {
                        used = region.Adapter.Proxy(region, source, mode == CaptureMode.Identity);
                        proxies.Add(used);
                    }
                    materials[region.Slot] = used;
                }
                for (int i = 0; i < materials.Length; i++) if (!materials[i]) materials[i] = hidden;
                copy.sharedMaterials = materials;
                foreach (var region in mesh.Regions)
                    copy.SetPropertyBlock(materials[region.Slot] != hidden && (mode == CaptureMode.Full || mode == CaptureMode.Neutral)
                        ? region.Block : null, region.Slot);
            }
        }

        private void Setup(Bounds frame, Vector3 direction, int environment, CaptureMode mode,
            float aspect = 1f, float? displayZoom = null)
        {
            float radius = Mathf.Max(.035f, frame.extents.magnitude);
            camera.aspect = aspect;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = radius * 5 + .06f;
            camera.transform.position = frame.center + direction.normalized * (radius * 2 + .04f);
            var up = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > .94f ? Vector3.forward : Vector3.up;
            camera.transform.rotation = Quaternion.LookRotation(-direction.normalized, up);
            float size = radius * 1.055f;
            if (displayZoom.HasValue)
            {
                float vertical = ProjectedExtent(camera.transform.up, frame.extents);
                float horizontal = ProjectedExtent(camera.transform.right, frame.extents);
                size = Mathf.Max(.035f, Mathf.Max(vertical, horizontal / aspect)) * 1.10f * displayZoom.Value;
            }
            camera.orthographicSize = size;
            camera.backgroundColor = mode == CaptureMode.Identity || mode == CaptureMode.Composite
                ? Color.clear : new Color(.12f, .13f, .15f, 1);
            key.enabled = fill.enabled = mode == CaptureMode.Full || mode == CaptureMode.Neutral;
            if (!key.enabled) return;
            key.intensity = environment == 2 ? .56f : .88f;
            fill.intensity = environment == 2 ? .70f : .47f;
            key.color = environment == 1 ? new Color(1, .77f, .58f) : Color.white;
            fill.color = environment == 2 ? new Color(.58f, .74f, 1) : Color.white;
            key.transform.rotation = Quaternion.Euler(environment == 2 ? 12 : 28, environment == 2 ? 97 : -26, 0);
            fill.transform.rotation = Quaternion.Euler(10, 155, 0);
        }

        private static float ProjectedExtent(Vector3 axis, Vector3 extents) =>
            Mathf.Abs(axis.x) * extents.x + Mathf.Abs(axis.y) * extents.y + Mathf.Abs(axis.z) * extents.z;

        public void RenderDisplay(RenderTexture target, Vector3 direction, Bounds frame, float zoom, CaptureMode mode, int lights,
            IReadOnlyDictionary<Region, Material> changes, ISet<Region> skin)
        {
            SetMaterials(mode, changes, false, skin);
            Setup(frame, direction, lights, mode, (float)target.width / target.height, zoom);
            Render(target);
        }

        public Capture CaptureNeck(Bounds frame, IReadOnlyList<Vector3> directions, int size,
            IReadOnlyDictionary<Region, Material> changes, ISet<Region> skin, int lights = 0, Capture geometry = null)
        {
            var result = new Capture(size);
            var color = CreateTarget(size, false);
            var ids = geometry == null ? CreateTarget(size, true) : null;
            try
            {
                if (ids) SetMaterials(CaptureMode.Identity, null, true, skin);
                for (int i = 0; i < directions.Count; i++)
                {
                    Setup(frame, directions[i], lights, CaptureMode.Identity);
                    if (ids) Render(ids);
                    result.Views.Add(new Capture.View
                    {
                        Direction = directions[i],
                        Projection = ViewProjection,
                        Id = geometry == null ? Read(ids, true) : geometry.Views[i].Id
                    });
                }
                SetMaterials(CaptureMode.Neutral, changes, false, skin);
                for (int i = 0; i < directions.Count; i++)
                {
                    Setup(frame, directions[i], lights, CaptureMode.Neutral);
                    Render(color);
                    result.Views[i].Color = Read(color, false);
                }
                return result;
            }
            finally
            {
                Object.DestroyImmediate(color);
                if (ids) Object.DestroyImmediate(ids);
            }
        }

        private void Render(RenderTexture target)
        {
            var previousTarget = RenderTexture.active;
            bool srgbWrite = GL.sRGBWrite;
            camera.targetTexture = target;
            try { camera.Render(); }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = srgbWrite;
            }
        }

        internal static RenderTexture CreateTarget(int resolution, bool identity, int height = 0, int depth = 24)
        {
            var format = identity ? GraphicsFormat.R32G32B32A32_SFloat : GraphicsFormat.R16G16B16A16_SFloat;
            var target = new RenderTexture(new RenderTextureDescriptor(resolution, height > 0 ? height : resolution, format, depth))
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            if (!target.Create())
            {
                Object.DestroyImmediate(target);
                throw new InvalidOperationException("无法分配 GPU 渲染目标。");
            }
            return target;
        }

        internal static Color[] Read(RenderTexture target, bool floats)
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(target.width, target.height, floats ? TextureFormat.RGBAFloat : TextureFormat.RGBAHalf, false, true)
                { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                return texture.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(texture);
            }
        }

        public void Dispose()
        {
            foreach (var proxy in proxies) if (proxy) Object.DestroyImmediate(proxy);
            proxies.Clear();
            if (hidden) Object.DestroyImmediate(hidden);
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    internal sealed class Capture
    {
        internal sealed class View
        {
            public Matrix4x4 Projection;
            public Vector3 Direction;
            public Color[] Id, Color;
        }

        public readonly int Size;
        public readonly List<View> Views = new List<View>();
        public Capture(int size) { Size = size; }
    }
}
