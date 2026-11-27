using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AvatarSurfaceSync
{
    internal sealed class SurfaceSyncWindow : EditorWindow
    {
        private const string Title = "Avatar Surface Color Synchronizer";
        private static readonly string[] Scopes = { "仅融合颈线", "整体校色（会改全身）" };
        private static readonly string[] Modes = { "原材质向目标同步", "两组材质统一至中间色" };
        private static readonly string[] RoleNames = { "自动", "脸部", "身体", "目标 / 参考", "原材质 / 调整", "覆盖层", "忽略" };

        [SerializeField] private GameObject root;
        [SerializeField] private SurfaceSyncManifest manifest;
        [SerializeField] private SyncSettings settings = new SyncSettings();
        [SerializeField] private string output = "Assets/AvatarSurfaceSyncGenerated";
        private SurfaceModel model;
        private SurfacePreview preview;
        private List<Seam> seams = new List<Seam>();
        private SyncResult result;
        private readonly HashSet<Region> originals = new HashSet<Region>(), targets = new HashSet<Region>();
        private static readonly string[] Pages = { "调色", "颈线", "材质", "输出" };
        private int page, materialId;
        private Vector2Int frameSize;
        private bool frameQueued;
        private string query = "", message = "";
        private bool heat, split = true, showResult = true, frameSeams, isolateSkin;
        private float yaw, pitch = 8, zoom = 1f;
        private int environment, viewMode;
        private bool displayDirty = true;
        private string reusableExport;
        private RenderTexture originalImage, correctedImage;
        private Matrix4x4 displayProjection;
        private Vector3 displayDirection;

        [MenuItem("Tools/Avatar Surface Color Synchronizer")]
        private static void Open() => GetWindow<SurfaceSyncWindow>(Title);

        private void OnEnable()
        {
            titleContent = new GUIContent("Avatar Surface Sync");
            minSize = new Vector2(750, 460);
            if (!root && Selection.activeGameObject) root = Selection.activeGameObject;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= RenderPendingFrame;
            frameQueued = false;
            DisposeAnalysis();
            FreeImages();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                var selected = (GameObject)EditorGUILayout.ObjectField("Avatar 根节点", root, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck()) { DisposeAnalysis(); root = selected; message = ""; }
                using (new EditorGUI.DisabledScope(!root))
                    if (GUILayout.Button("分析 Avatar", GUILayout.Width(110))) Execute(Analyze);
            }

            float sidebar = Mathf.Clamp(position.width * .42f, 310, 380);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(sidebar), GUILayout.ExpandHeight(true)))
                {
                    float previousLabelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 104;
                    try { DrawControls(); }
                    finally { EditorGUIUtility.labelWidth = previousLabelWidth; }
                }
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
                    DrawPreview();
            }
        }

        private void DrawControls()
        {
            page = GUILayout.Toolbar(page, Pages);
            GUILayout.Space(8);
            switch (page)
            {
                case 0: DrawColorSettings(); break;
                case 1: DrawSeamSettings(); break;
                case 2: DrawMaterials(); break;
                case 3: DrawOutput(); break;
            }
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(preview == null || originals.Count == 0 || targets.Count == 0))
                if (GUILayout.Button("计算并预览", GUILayout.Height(28))) Execute(() => Evaluate(false));
            DrawStatus();
        }

        private void DrawColorSettings()
        {
            EditorGUI.BeginChangeCheck();
            settings.AllowWholeSkin = EditorGUILayout.Popup("处理范围", settings.AllowWholeSkin ? 1 : 0, Scopes) == 1;
            settings.Mode = (SyncMode)EditorGUILayout.Popup("模式", (int)settings.Mode, Modes);
            if (EditorGUI.EndChangeCheck()) Invalidate();
            if (model != null)
            {
                GroupPicker("原材质", originals, targets);
                GroupPicker("目标材质", targets, originals);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("按头部与身体自动选择")) Execute(SelectSkinContacts);
                    using (new EditorGUI.DisabledScope(settings.Mode != SyncMode.SourceToTarget || originals.Count == 0 || targets.Count == 0))
                        if (GUILayout.Button("交换两组")) Execute(SwapGroups);
                }
            }
            Heading("校正强度");
            EditorGUI.BeginChangeCheck();
            settings.Strength = EditorGUILayout.Slider("同步强度", settings.Strength, 0f, 1f);
            settings.Lightness = EditorGUILayout.Slider("明度", settings.Lightness, 0f, 1.5f);
            settings.Chroma = EditorGUILayout.Slider("色彩", settings.Chroma, 0f, 1.5f);
            settings.Protect = EditorGUILayout.Slider("妆容保护", settings.Protect, 0f, 1f);
            if (EditorGUI.EndChangeCheck()) Invalidate();
        }

        private void DrawSeamSettings()
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("处理范围", Scopes[settings.AllowWholeSkin ? 1 : 0]);
            settings.Seam = EditorGUILayout.Slider("局部强度", settings.Seam, 0f, 1.5f);
            bool wasAuto = settings.WidthMm <= 0;
            bool useAuto = EditorGUILayout.Toggle(new GUIContent("自动融合宽度", "开启时根据颈线色差计算；关闭后可以手动选较窄的过渡。"), wasAuto);
            if (useAuto != wasAuto) settings.WidthMm = useAuto ? 0 : 20;
            if (settings.NeckWidthTooNarrow)
            {
                EditorGUILayout.LabelField("融合宽度 (mm)", $"{settings.WidthMm:0.##}（过窄）");
                if (GUILayout.Button($"改用 {SyncSettings.MinManualNeckWidthMm:G} mm"))
                {
                    settings.WidthMm = SyncSettings.MinManualNeckWidthMm;
                    GUI.changed = true;
                }
            }
            else if (!useAuto)
                settings.WidthMm = EditorGUILayout.Slider(new GUIContent("融合宽度 (mm)", "手动指定从颈线向外衰减的距离。"), settings.WidthMm,
                    settings.SeamOnly ? SyncSettings.MinManualNeckWidthMm : 1f, 80f);
            settings.Screened = EditorGUILayout.Toggle("平滑颈线", settings.Screened);
            if (EditorGUI.EndChangeCheck()) Invalidate();
            if (result != null)
                EditorGUILayout.LabelField("颈线采样  ·  " + result.Before.Count + " 对", EditorStyles.wordWrappedMiniLabel);
            if (settings.SeamOnly)
                EditorGUILayout.HelpBox("保留下巴和脸色：选「原材质向目标同步」，原材质选身体、目标选脸。", MessageType.Info);
            Heading("采集设置");
            EditorGUI.BeginChangeCheck();
            settings.MatchMm = EditorGUILayout.Slider("选材距离 (mm)", settings.MatchMm, .5f, 16f);
            if (EditorGUI.EndChangeCheck() && model != null) Execute(Detect);
            EditorGUI.BeginChangeCheck();
            settings.PreviewSize = EditorGUILayout.IntPopup("采集分辨率", settings.PreviewSize, new[] { "256", "384", "512", "768", "1024" },
                new[] { 256, 384, 512, 768, 1024 });
            settings.Iterations = EditorGUILayout.IntSlider("最大迭代", settings.Iterations, 1, 5);
            if (EditorGUI.EndChangeCheck()) Invalidate();
        }

        private void DrawStatus()
        {
            GUILayout.Space(5);
            if (result != null)
            {
                EditorGUILayout.LabelField("颈线 · 隔离皮肤实渲染  ｜  " + result.Before.Label,
                    EditorStyles.wordWrappedMiniLabel);
                if (result.Trial != null)
                    EditorGUILayout.LabelField("调整后  ·  " + result.After.Label, EditorStyles.wordWrappedMiniLabel);
            }

            var details = new List<string>();
            if (result != null)
            {
                details.AddRange(result.Warnings);
                if (result.Trial != null)
                {
                    details.Add($"冷光下的颈线平均色差 {result.CoolBefore.Mean:F4} → {result.CoolAfter.Mean:F4}");
                }
                details.AddRange(result.Diagnostics);
            }
            if (model != null) details.AddRange(model.Warnings);
            if (!string.IsNullOrEmpty(message)) details.Add(message);
            if (details.Count == 0) return;
            var summary = details[0];
            if (summary.Length > 72) summary = summary.Substring(0, 69) + "…";
            EditorGUILayout.LabelField(summary, EditorStyles.wordWrappedMiniLabel);
            if (details.Count > 1 || summary != details[0])
                if (GUILayout.Button("查看详情", EditorStyles.miniButton)) Execute(() =>
                    EditorUtility.DisplayDialog("Avatar Surface Sync", string.Join("\n", details), "关闭"));
        }

        private void DrawOutput()
        {
            output = EditorGUILayout.TextField("输出目录", output);
            EditorGUI.BeginChangeCheck();
            if (settings.SeamOnly) EditorGUILayout.LabelField("贴图分辨率", "保留源贴图大小");
            else settings.OutputSize = EditorGUILayout.IntPopup("贴图分辨率", settings.OutputSize,
                new[] { "源贴图大小", "256", "512", "1024", "2048", "4096" }, new[] { 0, 256, 512, 1024, 2048, 4096 });
            if (EditorGUI.EndChangeCheck()) Invalidate();

            using (new EditorGUI.DisabledScope(result?.Trial == null))
            {
                if (GUILayout.Button("生成资源")) Execute(() => Export(false));
                using (new EditorGUI.DisabledScope(root && EditorUtility.IsPersistent(root)))
                    if (GUILayout.Button("生成并替换材质")) Execute(() => Export(true));
            }
            Heading("恢复");
            manifest = (SurfaceSyncManifest)EditorGUILayout.ObjectField("生成记录", manifest, typeof(SurfaceSyncManifest), false);
            using (new EditorGUI.DisabledScope(!manifest || !root || EditorUtility.IsPersistent(root)))
            {
                if (GUILayout.Button("应用本记录")) Execute(() => Apply(false));
                if (GUILayout.Button("恢复原材质")) Execute(() => Apply(true));
            }
        }

        private static void Heading(string label)
        {
            GUILayout.Space(8);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
        }

        private void GroupPicker(string label, HashSet<Region> group, HashSet<Region> other)
        {
            var regions = model.Regions.Where(r => r.Enabled && r.Triangles.Length != 0 && r.Effective != RegionRole.Overlay).ToArray();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                string text = group.Count == 0 ? "选择材质槽" : group.Count == 1 ? group.First().Label : $"已选 {group.Count} 个材质槽";
                if (!EditorGUILayout.DropdownButton(new GUIContent(text, text), FocusType.Keyboard)) return;
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("清空"), false, () => { group.Clear(); Invalidate(); Repaint(); });
                menu.AddSeparator("");
                foreach (var r in regions)
                {
                    string item = r.Source.name.Replace('/', '／') + "/" + r.Slot + "  " + r.Original.name.Replace('/', '／') + "  #" + r.Id;
                    menu.AddItem(new GUIContent(item), group.Contains(r), () =>
                    {
                        bool adding = !group.Remove(r);
                        if (adding)
                        {
                            group.Add(r);
                            other.Remove(r);
                            if (!r.SkinRegion) r.Role = group == originals ? RegionRole.Target : RegionRole.Reference;
                        }
                        Execute(Detect);
                        Repaint();
                    });
                }
                menu.ShowAsContext();
            }
        }

        private void DrawMaterials()
        {
            if (model == null)
            {
                EditorGUILayout.LabelField("分析 Avatar 后显示材质槽。", EditorStyles.wordWrappedMiniLabel);
                return;
            }
            query = EditorGUILayout.TextField("筛选", query);
            var rows = model.Regions.Where(r => r.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (rows.Length == 0)
            {
                EditorGUILayout.LabelField("没有匹配的材质槽。", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            int index = Mathf.Max(0, Array.FindIndex(rows, r => r.Id == materialId));
            index = EditorGUILayout.Popup("材质槽", index, rows.Select(r => r.Label).ToArray());
            var region = rows[index];
            materialId = region.Id;
            EditorGUILayout.LabelField(region.Label, EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(!region.Enabled))
            {
                EditorGUI.BeginChangeCheck();
                region.Role = (RegionRole)EditorGUILayout.Popup("区域", (int)region.Role, RoleNames);
                if (EditorGUI.EndChangeCheck())
                {
                    if (!region.SkinRegion) { originals.Remove(region); targets.Remove(region); }
                    else if (region.Role == RegionRole.Reference) { targets.Add(region); originals.Remove(region); }
                    else if (region.Role == RegionRole.Target) { originals.Add(region); targets.Remove(region); }
                    Execute(Detect);
                }
                var canBake = region.Adapter.CanBake(region, out var detail);
                EditorGUILayout.LabelField(region.Adapter.Name + " · " + detail, EditorStyles.wordWrappedMiniLabel);
                if (region.Effective == RegionRole.Overlay)
                    EditorGUILayout.LabelField("覆盖层 · 队列 " + region.Original.renderQueue, EditorStyles.miniLabel);
                if (!region.SkinRegion || !canBake) return;
                EditorGUI.BeginChangeCheck();
                region.Skin = (Texture)EditorGUILayout.ObjectField("皮肤遮罩 (R)", region.Skin, typeof(Texture2D), false);
                region.Protection = (Texture)EditorGUILayout.ObjectField("保护遮罩 (R)", region.Protection, typeof(Texture2D), false);
                if (EditorGUI.EndChangeCheck()) Invalidate();
            }
        }

        private void DrawPreview()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                viewMode = EditorGUILayout.Popup(viewMode, new[] { "完整材质", "中性渲染", "合成底色" }, EditorStyles.toolbarPopup, GUILayout.Width(80));
                environment = EditorGUILayout.Popup(environment, new[] { "中性光", "暖光", "冷光" }, EditorStyles.toolbarPopup, GUILayout.Width(62));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("适合窗口", EditorStyles.toolbarButton)) ResetView();
                if (EditorGUI.EndChangeCheck()) displayDirty = true;
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                if (result?.Trial != null)
                {
                    split = GUILayout.Toggle(split, "对比", EditorStyles.toolbarButton);
                    if (!split) showResult = GUILayout.Toggle(showResult, "调整后", EditorStyles.toolbarButton);
                }
                heat = GUILayout.Toggle(heat, "颈线热力", EditorStyles.toolbarButton);
                using (new EditorGUI.DisabledScope(originals.Count == 0 || targets.Count == 0))
                    isolateSkin = GUILayout.Toggle(isolateSkin, "隔离皮肤", EditorStyles.toolbarButton);
                if (GUILayout.Toggle(frameSeams, "颈线特写", EditorStyles.toolbarButton) != frameSeams)
                {
                    frameSeams = !frameSeams;
                    zoom = 1;
                    displayDirty = true;
                }
                if (EditorGUI.EndChangeCheck()) displayDirty = true;
                GUILayout.FlexibleSpace();
            }
            var area = GUILayoutUtility.GetRect(100f, 100f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(area, new Color(.10f, .11f, .13f));
            if (preview == null)
            {
                if (Event.current.type == EventType.Repaint)
                    GUI.Label(area, "选择 Avatar 后点击「分析 Avatar」", EditorStyles.centeredGreyMiniLabel);
                return;
            }
            HandleOrbit(area);
            if (Event.current.type != EventType.Repaint) return;
            bool compare = split && result?.Trial != null;
            float width = compare ? area.width * .5f : area.width;
            float height = Mathf.Max(1f, area.height);
            int longest = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(width, height) * EditorGUIUtility.pixelsPerPoint), 256, 1024);
            var requested = new Vector2Int(Mathf.Max(32, Mathf.RoundToInt(longest * width / Mathf.Max(width, height))),
                Mathf.Max(32, Mathf.RoundToInt(longest * height / Mathf.Max(width, height))));
            if (requested != frameSize) { frameSize = requested; displayDirty = true; }
            if (displayDirty && !frameQueued)
            {
                frameQueued = true;
                EditorApplication.delayCall += RenderPendingFrame;
            }
            if (!originalImage) return;
            if (compare && correctedImage)
            {
                DrawImage(new Rect(area.x, area.y, width, area.height), originalImage, "原始", true);
                DrawImage(new Rect(area.x + width, area.y, width, area.height), correctedImage, "调整后", false);
            }
            else
            {
                bool after = showResult && correctedImage && result?.Trial != null;
                DrawImage(area, after ? correctedImage : originalImage, after ? "调整后" : "原始", !after);
            }
        }

        private void ResetView()
        {
            var forward = root ? root.transform.forward : Vector3.forward;
            yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            pitch = -5;
            zoom = 1f;
            frameSeams = false;
            displayDirty = true;
        }

        private void RenderPendingFrame()
        {
            frameQueued = false;
            if (!this || preview == null) return;
            try { DrawFrames(); }
            catch (Exception e) { message = e.Message; Debug.LogException(e); }
            finally { displayDirty = false; Repaint(); }
        }

        private void HandleOrbit(Rect rect)
        {
            var current = Event.current;
            if (!rect.Contains(current.mousePosition)) return;
            if (current.type == EventType.MouseDrag && (current.button == 0 || current.button == 1))
            {
                yaw += current.delta.x * .5f;
                pitch = Mathf.Clamp(pitch - current.delta.y * .5f, -88, 88);
                displayDirty = true;
                current.Use();
                Repaint();
            }
            if (current.type == EventType.ScrollWheel)
            {
                zoom = Mathf.Clamp(zoom * Mathf.Exp(current.delta.y * .055f), .06f, 2.8f);
                displayDirty = true;
                current.Use();
                Repaint();
            }
        }

        private void DrawFrames()
        {
            var direction = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
            var frame = preview.Bounds;
            if (frameSeams)
            {
                var measured = result?.Neck;
                if (measured != null && measured.Count != 0)
                {
                    frame = new Bounds(measured[0].Position, Vector3.one * .055f);
                    foreach (var p in measured) frame.Encapsulate(p.Position);
                    frame.Expand(.033f);
                }
                else
                {
                    var neck = NeckFrame();
                    if (neck.size.sqrMagnitude > 0) frame = neck;
                }
            }
            var mode = viewMode == 2 ? CaptureMode.Composite : viewMode == 1 ? CaptureMode.Neutral : CaptureMode.Full;
            var skin = isolateSkin && originals.Count > 0 && targets.Count > 0
                ? new HashSet<Region>(originals.Concat(targets)) : null;
            EnsureFrame(ref originalImage);
            preview.RenderDisplay(originalImage, direction, frame, zoom, mode, environment, null, skin);
            if (result?.Trial != null)
            {
                EnsureFrame(ref correctedImage);
                preview.RenderDisplay(correctedImage, direction, frame, zoom, mode, environment, result.Trial.Materials, skin);
            }
            displayProjection = preview.ViewProjection;
            displayDirection = direction;
        }

        private void EnsureFrame(ref RenderTexture image)
        {
            if (image && image.width == frameSize.x && image.height == frameSize.y) return;
            if (image) DestroyImmediate(image);
            image = SurfacePreview.CreateTarget(frameSize.x, false, frameSize.y);
        }

        private void DrawImage(Rect area, RenderTexture image, string label, bool before)
        {
            GUI.DrawTexture(area, image, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(area.x + 7, area.y + 5, 100, 20), label, EditorStyles.whiteMiniLabel);
            if (!heat || result == null) return;
            foreach (var sample in result.Neck)
            {
                if (!sample.Measured || Vector3.Dot(displayDirection, sample.Direction) < .48f) continue;
                var p = displayProjection.MultiplyPoint(sample.Position);
                float error = before ? sample.OriginalError : sample.Difference.magnitude;
                var tint = Color.Lerp(new Color(.2f, .9f, .45f), new Color(1f, .22f, .16f), Mathf.InverseLerp(.005f, .05f, error));
                if (Mathf.Abs(p.x) <= 1 && Mathf.Abs(p.y) <= 1 && Mathf.Abs(p.z) <= 1)
                    EditorGUI.DrawRect(new Rect(area.x + (p.x + 1) * area.width * .5f, area.y + (1 - p.y) * area.height * .5f, 3, 3), tint);
            }
        }

        private Bounds NeckFrame() =>
            NeckBand.Frame(model, seams.Where(s => SyncSession.BetweenGroups(s, originals, targets)));

        private void Analyze()
        {
            DisposeAnalysis();
            model = new SurfaceModel(root);
            preview = new SurfacePreview(model);
            ResetView();
            Detect();
            SelectSkinContacts();
        }

        private void Detect()
        {
            Invalidate();
            if (model != null) seams = SeamDetector.Detect(model, settings.MatchMm * .001f);
        }

        private void SelectSkinContacts()
        {
            Invalidate();
            SeamDetector.SelectSkinContacts(seams, originals, targets);
            message = originals.Count > 0 ? $"已选中 {originals.Count} 个原材质槽和 {targets.Count} 个目标材质槽。" :
                "未能自动选中头部和身体材质，请手动选择两组。";
        }

        private void SwapGroups()
        {
            var originalGroup = originals.ToArray();
            originals.Clear();
            originals.UnionWith(targets);
            targets.Clear();
            targets.UnionWith(originalGroup);
            foreach (var region in originals)
                if (region.Role == RegionRole.Reference) region.Role = RegionRole.Target;
            foreach (var region in targets)
                if (region.Role == RegionRole.Target) region.Role = RegionRole.Reference;
            Invalidate();
            message = "已交换原材质和目标；重新计算预览即可检查新的调整方向。";
        }

        private void Evaluate(bool highResolution)
        {
            Invalidate();
            result = SyncSession.Optimize(preview, NeckFrame(), NeckBand.Directions(root.transform), originals, targets, settings.Copy(), highResolution,
                (operation, progress) =>
                {
                    if (EditorUtility.DisplayCancelableProgressBar(Title, operation, progress))
                        throw new OperationCanceledException();
                });
            displayDirty = true;
        }

        private void Export(bool apply)
        {
            if (!manifest || reusableExport != output)
            {
                Evaluate(true);
                if (result?.Trial == null) return;
                manifest = SurfaceSyncManifest.Export(root, result, output);
                reusableExport = output;
            }
            message = "已生成 " + manifest.Materials.Count + " 张贴图。";
            if (apply) Apply(false);
            else EditorGUIUtility.PingObject(manifest);
        }

        private void Apply(bool restore)
        {
            var changed = manifest.Assign(root, restore);
            message = changed == 0 ? "本记录没有可更新的材质槽。" :
                (restore ? "已恢复 " : "已替换 ") + changed + " 个材质引用，可使用 Unity Undo。";
            if (changed > 0)
            {
                EditorGUIUtility.PingObject(manifest);
                displayDirty = true;
            }
        }

        private void Execute(Action action)
        {
            var selectedRoot = root;
            EditorApplication.delayCall += () =>
            {
                if (!this || selectedRoot != root) return;
                try { action(); }
                catch (OperationCanceledException) { message = "已取消计算。"; }
                catch (Exception error) { message = error.Message; Debug.LogException(error); }
                finally { EditorUtility.ClearProgressBar(); Repaint(); }
            };
        }

        private void Invalidate()
        {
            reusableExport = null;
            result?.Dispose();
            result = null;
            displayDirty = true;
        }

        private void DisposeAnalysis()
        {
            preview?.Dispose();
            preview = null;
            Invalidate();
            model?.Dispose();
            model = null;
            seams.Clear();
            originals.Clear();
            targets.Clear();
        }

        private void FreeImages()
        {
            if (originalImage) DestroyImmediate(originalImage);
            if (correctedImage) DestroyImmediate(correctedImage);
            originalImage = correctedImage = null;
        }
    }
}
