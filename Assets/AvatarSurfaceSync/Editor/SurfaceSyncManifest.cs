using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AvatarSurfaceSync
{
    internal sealed class SurfaceSyncManifest : ScriptableObject
    {
        [Serializable]
        internal sealed class Assignment
        {
            public string Hierarchy, RendererType;
            public int Slot;
            public Material Original, Generated;
            public Texture2D Texture;
            public Texture SourceTexture;
            public Vector4 SourceUv;
            public bool SecondMain;
            public string TextureProperty => ShaderAdapter.TextureProperty(SecondMain);
        }

        [Serializable]
        private sealed class CostumeReference
        {
            public string Hierarchy, Property;
            public Material Original, Generated;
        }

        private const string CostumeChangerType = "jp.lilxyzw.lilycalinventory.runtime.CostumeChanger";
        [SerializeField] private List<CostumeReference> costumeReferences = new List<CostumeReference>();

        public string AvatarName;
        public string AvatarId;
        public List<Assignment> Materials = new List<Assignment>();

        public static SurfaceSyncManifest Export(GameObject root, SyncResult result, string output)
        {
            if (result?.Trial == null || result.Trial.Textures.Count == 0)
                throw new InvalidOperationException("没有通过 GPU 复核的结果。");
            output = output.Replace('\\', '/').TrimEnd('/');
            if (output != "Assets" && (!output.StartsWith("Assets/", StringComparison.Ordinal) ||
                output.Split('/').Skip(1).Any(piece => piece.Length == 0 || piece == "." || piece == "..")))
                throw new InvalidOperationException("输出目录必须在 Assets 内。");
            EnsureFolders(output);
            var folderName = Path.GetFileName(AssetDatabase.GenerateUniqueAssetPath(output + "/" + Safe(root.name) + "_SurfaceSync"));
            var path = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder(output, folderName));
            if (string.IsNullOrEmpty(path)) throw new IOException("无法创建输出目录。");
            var manifest = CreateInstance<SurfaceSyncManifest>();
            try
            {
                manifest.AvatarName = root.name;
                manifest.AvatarId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString();
                foreach (var entry in result.Trial.Textures.OrderBy(x => x.Key.Id))
                {
                    var region = entry.Key;
                    var texture = entry.Value;
                    bool second = result.Trial.SecondMain.Contains(region);
                    string property = ShaderAdapter.TextureProperty(second);
                    var prefix = path + "/" + Safe(region.Source.name + "_" + region.Original.name) + "_" + region.Id + (second ? "_2nd" : "");
                    Texture2D baked;
                    if (result.Trial.LocalBands.ContainsKey(region))
                    {
                        // PNG import would regenerate the mips and visibly alter the unedited area.
                        baked = Object.Instantiate(texture);
                        baked.name = Path.GetFileName(prefix);
                        baked.hideFlags = HideFlags.None;
                        AssetDatabase.CreateAsset(baked, prefix + ".asset");
                    }
                    else
                    {
                        string image = prefix + ".png";
                        File.WriteAllBytes(image, texture.EncodeToPNG());
                        AssetDatabase.ImportAsset(image, ImportAssetOptions.ForceSynchronousImport);
                        var importer = (TextureImporter)AssetImporter.GetAtPath(image);
                        importer.sRGBTexture = texture.isDataSRGB;
                        importer.mipmapEnabled = texture.mipmapCount > 1;
                        importer.alphaSource = TextureImporterAlphaSource.FromInput;
                        importer.alphaIsTransparency = region.Adapter.IsTransparent(region.Original);
                        importer.npotScale = TextureImporterNPOTScale.None;
                        importer.wrapModeU = texture.wrapModeU;
                        importer.wrapModeV = texture.wrapModeV;
                        importer.filterMode = texture.filterMode;
                        importer.anisoLevel = texture.anisoLevel;
                        importer.maxTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(texture.width, texture.height));
                        importer.textureCompression = TextureImporterCompression.Uncompressed;
                        importer.SaveAndReimport();
                        baked = AssetDatabase.LoadAssetAtPath<Texture2D>(image);
                        if (!baked) throw new IOException("导入生成贴图失败。");
                    }
                    manifest.Materials.Add(new Assignment
                    {
                        Slot = region.Slot, Original = region.Original, Texture = baked,
                        Generated = CopyMaterial(region.Original, baked, property, prefix + ".mat"),
                        SourceTexture = region.Original.GetTexture(property),
                        SourceUv = TextureUv(region.Original, property), SecondMain = second,
                        Hierarchy = Address(root.transform, region.Source.transform),
                        RendererType = region.Source.GetType().FullName
                    });
                }
                AssetDatabase.CreateAsset(manifest, path + "/Assignments.asset");
                AssetDatabase.SaveAssetIfDirty(manifest);
                return manifest;
            }
            catch
            {
                AssetDatabase.DeleteAsset(path);
                if (manifest && !AssetDatabase.Contains(manifest)) DestroyImmediate(manifest);
                throw;
            }
        }

        public int Assign(GameObject root, bool restore)
        {
            if (!root || EditorUtility.IsPersistent(root))
                throw new InvalidOperationException("请选中场景中的 Avatar 实例，以应用或恢复材质。");
            if (!string.IsNullOrEmpty(AvatarId) && GlobalObjectId.TryParse(AvatarId, out var id))
            {
                var expected = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
                if (expected && expected != root) throw new InvalidOperationException("当前 Avatar 与生成记录不匹配。");
            }
            string path = Path.GetDirectoryName(AssetDatabase.GetAssetPath(this))?.Replace('\\', '/');
            var slots = new Dictionary<Renderer, Dictionary<int, Assignment>>();
            var rendererEdits = new Dictionary<Renderer, Material[]>();
            int count = 0;
            string label = restore ? "Restore Original Avatar Materials" : "Assign Avatar Material Copies";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);
            try
            {
                foreach (var entry in Materials)
                {
                    var target = Resolve(root.transform, entry.Hierarchy);
                    if (!target || !entry.Original) continue;
                    var renderer = target.GetComponents<Renderer>().FirstOrDefault(x => x.GetType().FullName == entry.RendererType);
                    if (!renderer) continue;
                    if (!rendererEdits.TryGetValue(renderer, out var materials)) materials = renderer.sharedMaterials;
                    if (entry.Slot < 0 || entry.Slot >= materials.Length) continue;

                    var current = materials[entry.Slot];
                    if (restore)
                    {
                        if (!entry.Generated || current != entry.Generated) continue;
                        materials[entry.Slot] = entry.Original;
                    }
                    else
                    {
                        if (!current || current != entry.Original && current != entry.Generated) continue;
                        if (!entry.Generated)
                        {
                            if (!entry.Texture || string.IsNullOrEmpty(path)) continue;
                            Undo.RecordObject(this, label);
                            var name = Safe(renderer.name + "_" + entry.Original.name + "_" + entry.Slot + "_Sync");
                            entry.Generated = CopyMaterial(entry.Original, entry.Texture, entry.TextureProperty, path + "/" + name + ".mat");
                        }
                        if (!slots.TryGetValue(renderer, out var bySlot))
                            slots.Add(renderer, bySlot = new Dictionary<int, Assignment>());
                        bySlot[entry.Slot] = entry;
                        if (current == entry.Generated) continue;
                        materials[entry.Slot] = entry.Generated;
                    }
                    rendererEdits[renderer] = materials;
                    count++;
                }
                foreach (var edit in rendererEdits)
                {
                    Undo.RecordObject(edit.Key, label);
                    edit.Key.sharedMaterials = edit.Value;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(edit.Key);
                }

                count += restore ? RestoreCostumes(root) : ApplyCostumes(root, slots, path, label);
                if (!restore && slots.Count > 0)
                {
                    foreach (var legacy in root.GetComponentsInChildren<SurfaceSyncBinding>(true))
                    {
                        var clean = legacy.OwnsGameObject && legacy.gameObject != root &&
                            legacy.transform.childCount == 0 && legacy.GetComponents<Component>().Length == 2;
                        Undo.DestroyObjectImmediate(clean ? (Object)legacy.gameObject : legacy);
                    }
                }
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }
            if (count > 0 || !restore && slots.Count > 0)
            {
                AssetDatabase.SaveAssetIfDirty(this);
                EditorSceneManager.MarkSceneDirty(root.scene);
                SceneView.RepaintAll();
            }
            return count;
        }

        private int ApplyCostumes(GameObject root, Dictionary<Renderer, Dictionary<int, Assignment>> slots, string directory, string label)
        {
            if (slots.Count == 0 || string.IsNullOrEmpty(directory)) return 0;
            int count = 0;
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!component || component.GetType().FullName != CostumeChangerType) continue;
                var serialized = new SerializedObject(component);
                var costumes = serialized.FindProperty("costumes");
                if (costumes == null || !costumes.isArray) continue;
                string owner = Address(root.transform, component.transform);
                int componentEdits = 0;
                for (int i = 0; i < costumes.arraySize; i++)
                {
                    var rows = costumes.GetArrayElementAtIndex(i).FindPropertyRelative("parametersPerMenu.materialReplacers");
                    if (rows == null || !rows.isArray) continue;
                    for (int j = 0; j < rows.arraySize; j++)
                    {
                        var row = rows.GetArrayElementAtIndex(j);
                        var renderer = row.FindPropertyRelative("renderer")?.objectReferenceValue as Renderer;
                        if (!renderer || !slots.TryGetValue(renderer, out var bySlot)) continue;
                        var references = row.FindPropertyRelative("replaceTo");
                        if (references == null || !references.isArray) continue;
                        foreach (var pair in bySlot)
                        {
                            if (pair.Key >= references.arraySize) continue;
                            var property = references.GetArrayElementAtIndex(pair.Key);
                            var current = property.objectReferenceValue as Material;
                            var entry = pair.Value;
                            string textureProperty = entry.TextureProperty;
                            // A distinct costume material can give layer 2 a different UV mode or blend; don't reuse its bake.
                            if (!current || !entry.Texture || current == entry.Generated || entry.SecondMain && current != entry.Original ||
                                current.shader != entry.Original.shader || !current.HasProperty(textureProperty) ||
                                current.GetTexture(textureProperty) != (entry.SourceTexture ? entry.SourceTexture : entry.Original.GetTexture(textureProperty)) ||
                                TextureUv(current, textureProperty) != TextureUv(entry.Original, textureProperty)) continue;

                            var existing = costumeReferences.FirstOrDefault(x => x.Hierarchy == owner &&
                                x.Property == property.propertyPath && x.Original == current && x.Generated &&
                                x.Generated.GetTexture(textureProperty) == entry.Texture);
                            if (existing == null)
                            {
                                var copy = current == entry.Original ? entry.Generated :
                                    CopyMaterial(current, entry.Texture, textureProperty, directory + "/" + Safe(current.name + "_CostumeSync") + ".mat");
                                existing = new CostumeReference { Hierarchy = owner, Property = property.propertyPath,
                                    Original = current, Generated = copy };
                                Undo.RecordObject(this, label);
                                costumeReferences.Add(existing);
                            }
                            property.objectReferenceValue = existing.Generated;
                            componentEdits++;
                        }
                    }
                }
                if (componentEdits > 0)
                {
                    serialized.ApplyModifiedProperties();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                    count += componentEdits;
                }
            }
            return count;
        }

        private int RestoreCostumes(GameObject root)
        {
            int count = 0;
            foreach (var group in costumeReferences.GroupBy(x => x.Hierarchy))
            {
                var target = Resolve(root.transform, group.Key);
                if (!target) continue;
                var component = target.GetComponents<MonoBehaviour>().FirstOrDefault(c => c && c.GetType().FullName == CostumeChangerType);
                if (!component) continue;
                var serialized = new SerializedObject(component);
                foreach (var record in group)
                {
                    var property = serialized.FindProperty(record.Property);
                    if (property == null || !record.Generated || property.objectReferenceValue != record.Generated) continue;
                    property.objectReferenceValue = record.Original;
                    count++;
                }
                if (serialized.ApplyModifiedProperties())
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
            return count;
        }

        private static Vector4 TextureUv(Material material, string property)
        {
            var scale = material.GetTextureScale(property);
            var offset = material.GetTextureOffset(property);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }

        private static Material CopyMaterial(Material source, Texture texture, string property, string path)
        {
            var copy = new Material(source) { name = source.name + " · Sync" };
            copy.SetTexture(property, texture);
            AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath(path));
            AssetDatabase.SaveAssetIfDirty(copy);
            return copy;
        }

        private static string Address(Transform root, Transform child)
        {
            var nodes = new Stack<string>();
            for (var p = child; p != root; p = p.parent)
                nodes.Push(p.GetSiblingIndex() + ":" + Uri.EscapeDataString(p.name));
            return string.Join("/", nodes);
        }

        private static Transform Resolve(Transform root, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return root;
            foreach (string node in relative.Split('/'))
            {
                int colon = node.IndexOf(':');
                if (colon < 0 || !int.TryParse(node.Substring(0, colon), out var index) || index < 0) return null;
                var name = Uri.UnescapeDataString(node.Substring(colon + 1));
                if (index < root.childCount && root.GetChild(index).name == name)
                {
                    root = root.GetChild(index);
                    continue;
                }
                Transform match = null;
                foreach (Transform child in root)
                {
                    if (child.name != name) continue;
                    if (match) return null;
                    match = child;
                }
                if (!match) return null;
                root = match;
            }
            return root;
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim('.', ' ');
            if (string.IsNullOrEmpty(name)) return "Avatar";
            return name.Length > 72 ? name.Substring(0, 72) : name;
        }

        private static void EnsureFolders(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int split = path.LastIndexOf('/');
            var parent = path.Substring(0, split);
            EnsureFolders(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(split + 1));
            if (!AssetDatabase.IsValidFolder(path)) throw new IOException("无法创建 " + path);
        }
    }
}
