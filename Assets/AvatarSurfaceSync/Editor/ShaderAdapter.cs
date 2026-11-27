using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarSurfaceSync
{
    internal class ShaderAdapter
    {
        protected enum Family { Unknown, Standard, Unlit, Lil }
        private readonly Family family;
        public bool Known => family != Family.Unknown;
        public bool Lil => family == Family.Lil;
        public string Name { get; }

        protected ShaderAdapter(Family family, string name)
        {
            this.family = family;
            Name = name;
        }

        private static readonly ShaderAdapter LilAdapter = new ShaderAdapter(Family.Lil, "lilToon");
        private static readonly ShaderAdapter DewAdapter = new ShaderAdapter(Family.Lil, "DewSkin / lilToon");
        private static readonly ShaderAdapter StandardAdapter = new ShaderAdapter(Family.Standard, "Unity Standard");
        private static readonly ShaderAdapter UnlitAdapter = new ShaderAdapter(Family.Unlit, "Unity Unlit");
        private static readonly ShaderAdapter Fallback = new ShaderAdapter(Family.Unknown, "渲染反馈");

        public static ShaderAdapter Resolve(Material material)
        {
            var name = material.shader ? material.shader.name : "";
            if (name.StartsWith("DewSkin/", StringComparison.Ordinal) && material.HasProperty("_UseMain2ndTex")) return DewAdapter;
            if (name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0 &&
                name.IndexOf("Fur", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("Refraction", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("Gem", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("Multi", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("TwoPass", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("FakeShadow", StringComparison.OrdinalIgnoreCase) < 0)
                return LilAdapter;
            if (name == "Standard" || name == "Standard (Specular setup)") return StandardAdapter;
            if (name == "Unlit/Color" || name == "Unlit/Texture" || name == "Unlit/Transparent" ||
                name == "Unlit/Transparent Cutout") return UnlitAdapter;
            return Fallback;
        }

        public bool IsTransparent(Material m) => RenderMode(m) == 2;

        private int RenderMode(Material m)
        {
            var type = m.GetTag("RenderType", false, "");
            var shaderName = m.shader ? m.shader.name : "";
            if (type == "TransparentCutout" || m.IsKeywordEnabled("_ALPHATEST_ON") ||
                Lil && shaderName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (type == "Transparent" || m.IsKeywordEnabled("_ALPHABLEND_ON") ||
                m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") || m.renderQueue >= 3000 ||
                Lil && shaderName.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 0;
        }

        public static string TextureProperty(bool secondMain) => secondMain ? "_Main2ndTex" : "_MainTex";

        public bool CanBake(Region r, out string reason)
        {
            var m = r.Original;
            if (!Known) { reason = "未知 Shader，当前无法安全烘焙主贴图"; return false; }
            if (!r.Block.isEmpty) { reason = "存在材质属性块，输出会被覆盖"; return false; }
            if (r.Mesh.Uv == null || r.Mesh.Uv.Length != r.Mesh.World?.Length || r.Triangles.Length == 0)
            { reason = "没有可烘焙的 UV0 三角面"; return false; }
            if (!m.HasProperty("_MainTex"))
            { reason = "材质没有主贴图槽"; return false; }
            if (m.GetTexture("_MainTex") && !(m.GetTexture("_MainTex") is Texture2D))
            { reason = "主纹理为动态或非二维资源"; return false; }
            if (Lil && (Vector(m, "_MainTex_ScrollRotate", Vector4.zero).sqrMagnitude > 1e-8f ||
                Float(m, "_UseParallax", 0) > .5f))
            { reason = "主 UV 带旋转、动画或视差"; return false; }
            if (family == Family.Standard && m.IsKeywordEnabled("_PARALLAXMAP"))
            { reason = "Standard 视差会移动取样坐标"; return false; }
            if (!FitsAtlas(r, "_MainTex")) { reason = "主纹理 UV 退化或跨图块，无法安全烘焙"; return false; }
            reason = CompositeExact(m) ? "支持合成与局部烘焙" : "动态层以原 Shader 渲染验证";
            return true;
        }

        public bool CanBakeSecond(Region r)
        {
            const string layer = "_Main2ndTex";
            var m = r.Original;
            return Lil && Float(m, "_UseMain2ndTex", 0) > .5f && m.HasProperty(layer) &&
                m.GetTexture(layer) is Texture2D && CanBake(r, out _) &&
                Float(m, layer + "_UVMode", 0) == 0 && Float(m, layer + "BlendMode", 0) == 0 &&
                Float(m, layer + "AlphaMode", 0) == 0 && Float(m, layer + "_Cull", 0) == 0 &&
                Float(m, layer + "IsDecal", 0) == 0 && Float(m, layer + "IsMSDF", 0) == 0 &&
                Float(m, layer + "IsLeftOnly", 0) == 0 && Float(m, layer + "IsRightOnly", 0) == 0 &&
                Float(m, layer + "ShouldCopy", 0) == 0 && Float(m, layer + "ShouldFlipCopy", 0) == 0 &&
                Float(m, layer + "ShouldFlipMirror", 0) == 0 && Float(m, layer + "Angle", 0) == 0 &&
                Vector(m, layer + "_ScrollRotate", Vector4.zero).sqrMagnitude < 1e-8f &&
                Vector(m, "_Main2ndDissolveParams", Vector4.zero).x == 0 &&
                Vector(m, "_Main2ndDistanceFade", Vector4.zero).z == 0 &&
                Float(m, "_AudioLink2Main2nd", 0) == 0 && FitsAtlas(r, layer);
        }

        private static bool FitsAtlas(Region r, string property)
        {
            var scale = r.Original.GetTextureScale(property);
            var offset = r.Original.GetTextureOffset(property);
            if (Mathf.Abs(scale.x * scale.y) < .00001f) return false;
            foreach (int i in r.Triangles)
            {
                var p = Vector2.Scale(r.Mesh.Uv[i], scale) + offset;
                if (p.x < -.001f || p.y < -.001f || p.x > 1.001f || p.y > 1.001f) return false;
            }
            return true;
        }

        public bool CompositeExact(Material m)
        {
            if (!Known) return false;
            if (family == Family.Standard) return !m.IsKeywordEnabled("_PARALLAXMAP");
            if (!Lil) return true;
            if (Vector(m, "_MainTex_ScrollRotate", Vector4.zero).sqrMagnitude > 1e-8f ||
                Vector(m, "_DissolveParams", Vector4.zero).x > .5f ||
                Float(m, "_UseParallax", 0) > .5f || Float(m, "_UseDither", 0) > .5f) return false;
            for (int n = 2; n <= 3; n++)
            {
                string ordinal = n == 2 ? "2nd" : "3rd";
                string main = "_Main" + ordinal;
                if (Float(m, "_UseMain" + ordinal + "Tex", 0) < .5f) continue;
                var anim = Vector(m, main + "TexDecalAnimation", Vector4.one);
                var scroll = Vector(m, main + "Tex_ScrollRotate", Vector4.zero);
                if (Float(m, main + "Tex_UVMode", 0) > 3.5f || Float(m, main + "Tex_Cull", 0) > .5f ||
                    Float(m, main + "EnableLighting", 1) < .99f || anim.x * anim.y > 1.01f ||
                    Mathf.Abs(scroll.x) + Mathf.Abs(scroll.y) + Mathf.Abs(scroll.w) > 1e-6f ||
                    Vector(m, main + "DistanceFade", Vector4.zero).z > .001f ||
                    Vector(m, main + "DissolveParams", Vector4.zero).x > .5f ||
                    Float(m, "_AudioLink2Main" + ordinal, 0) > .5f)
                    return false;
            }
            return true;
        }

        public Material Proxy(Region region, Material source, bool identity)
        {
            var shader = Shader.Find("Hidden/AvatarSurfaceSync/Composite");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("合成 Shader 未导入或当前图形后端不支持它。");
            var pass = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = source.renderQueue };
            var mode = RenderMode(source);
            if (source.HasProperty("_MainTex")) CopyTexture(source, "_MainTex", pass, "_MainTex");
            else if (source.HasProperty("_BaseMap")) CopyTexture(source, "_BaseMap", pass, "_MainTex");
            pass.SetColor("_Color", source.HasProperty("_Color") ? source.GetColor("_Color") :
                source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : Color.white);
            pass.SetFloat("_RenderMode", mode);
            pass.SetFloat("_Cull", Float(source, "_Cull", family == Family.Unlit && mode == 2 ? 0 : 2));
            pass.SetFloat("_Src", identity ? 1 : Float(source, "_SrcBlend", mode == 2 ? 5 : 1));
            pass.SetFloat("_Dst", identity ? 0 : Float(source, "_DstBlend", mode == 2 ? 10 : 0));
            pass.SetFloat("_SrcA", identity ? 1 : Float(source, "_SrcBlendAlpha", 1));
            pass.SetFloat("_DstA", identity ? 0 : Float(source, "_DstBlendAlpha", 10));
            pass.SetFloat("_Op", identity ? 0 : Float(source, "_BlendOp", 0));
            pass.SetFloat("_OpA", identity ? 0 : Float(source, "_BlendOpAlpha", 0));
            pass.SetFloat("_ZTest", Float(source, "_ZTest", 4));
            pass.SetFloat("_ZWrite", identity ? 1 : Float(source, "_ZWrite", mode == 2 ? 0 : 1));
            pass.SetFloat("_ColorMask", identity ? 15 : Float(source, "_ColorMask", 15));
            pass.SetFloat("_OffsetFactor", Float(source, "_OffsetFactor", 0));
            pass.SetFloat("_OffsetUnits", Float(source, "_OffsetUnits", 0));
            foreach (string property in StencilProperties)
                if (source.HasProperty(property)) pass.SetFloat(property, source.GetFloat(property));
            pass.SetFloat("_Identity", identity ? region.Id : 0);
            pass.SetFloat("_Premultiply", source.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ? 1 : 0);
            pass.SetVector("_AlphaParams", new Vector4(Float(source, "_AlphaMaskMode", 0), Float(source, "_AlphaMaskScale", 1),
                Float(source, "_AlphaMaskValue", 0), Float(source, "_Cutoff", mode == 1 ? .5f : 0f)));
            if (family == Family.Standard)
            {
                CopyTexture(source, "_DetailAlbedoMap", pass, "_DetailAlbedoMap");
                CopyTexture(source, "_DetailMask", pass, "_DetailMask");
                pass.SetVector("_DetailParams", new Vector4(source.IsKeywordEnabled("_DETAIL_MULX2") ? 1 : 0, Float(source, "_UVSec", 0)));
            }
            if (Lil)
            {
                pass.SetVector("_Tone", Vector(source, "_MainTexHSVG", new Vector4(0,1,1,1)));
                pass.SetFloat("_MainGradationStrength", Float(source, "_MainGradationStrength", 0));
                foreach (var property in LilTextures) CopyTexture(source, property, pass, property);
                ConfigureLayer(source, pass, 2, "2nd");
                ConfigureLayer(source, pass, 3, "3rd");
            }
            if (region.Skin) pass.SetTexture("_Skin", region.Skin);
            if (region.Protection) pass.SetTexture("_Protect", region.Protection);
            return pass;
        }

        private static readonly string[] StencilProperties =
        {
            "_StencilRef", "_StencilComp", "_StencilReadMask", "_StencilWriteMask", "_StencilPass", "_StencilFail", "_StencilZFail"
        };
        private static readonly string[] LilTextures = { "_MainColorAdjustMask", "_MainGradationTex", "_AlphaMask" };

        private static void ConfigureLayer(Material source, Material proxy, int number, string ordinal)
        {
            string prop = "_Main" + ordinal;
            string tex = prop + "Tex";
            string key = "_Layer";
            CopyTexture(source, tex, proxy, key + number);
            CopyTexture(source, prop + "BlendMask", proxy, key + "Mask" + number);
            if (source.HasProperty("_Color" + ordinal)) proxy.SetColor(key + "Color" + number, source.GetColor("_Color" + ordinal));
            proxy.SetVector(key + "Params" + number, new Vector4(Float(source, "_UseMain" + ordinal + "Tex", 0),
                Float(source, tex + "BlendMode", 0), Float(source, tex + "_UVMode", 0),
                Vector(source, tex + "_ScrollRotate", new Vector4(0, 0, Float(source, tex + "Angle", 0), 0)).z));
            proxy.SetVector(key + "Decal" + number, new Vector4(Float(source, tex + "IsDecal", 0), Float(source, tex + "IsLeftOnly", 0),
                Float(source, tex + "IsRightOnly", 0), Float(source, tex + "ShouldCopy", 0)));
            proxy.SetVector(key + "Flip" + number, new Vector4(Float(source, tex + "ShouldFlipMirror", 0),
                Float(source, tex + "ShouldFlipCopy", 0), Float(source, tex + "IsMSDF", 0)));
            proxy.SetVector(key + "Alpha" + number, new Vector4(Float(source, tex + "AlphaMode", 0), Float(source, prop + "EnableLighting", 1)));
        }

        internal static float Float(Material m, string name, float fallback) => m.HasProperty(name) ? m.GetFloat(name) : fallback;
        private static Vector4 Vector(Material m, string name, Vector4 fallback) => m.HasProperty(name) ? m.GetVector(name) : fallback;
        private static void CopyTexture(Material source, string name, Material target, string property)
        {
            if (!source.HasProperty(name)) return;
            var texture = source.GetTexture(name);
            if (texture && texture.dimension == TextureDimension.Tex2D) target.SetTexture(property, texture);
            target.SetTextureScale(property, source.GetTextureScale(name));
            target.SetTextureOffset(property, source.GetTextureOffset(name));
        }
    }
}
