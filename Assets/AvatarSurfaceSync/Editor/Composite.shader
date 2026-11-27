Shader "Hidden/AvatarSurfaceSync/Composite"
{
    Properties
    {

        _MainTex ("Base", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Tone ("HSVG", Vector) = (0,1,1,1)
        _MainColorAdjustMask ("Tone mask", 2D) = "white" {}
        _MainGradationTex ("Gradation", 2D) = "white" {}
        _MainGradationStrength ("Gradation strength", Float) = 0
        _AlphaMask ("Alpha mask", 2D) = "white" {}
        _AlphaParams ("Mode, scale, offset, cutoff", Vector) = (0,1,0,0.5)
        _DetailAlbedoMap ("Standard detail", 2D) = "gray" {}
        _DetailMask ("Standard detail mask", 2D) = "white" {}
        _DetailParams ("Enabled and UV", Vector) = (0,0,0,0)
        _Skin ("Skin mask", 2D) = "white" {}
        _Protect ("Protection mask", 2D) = "black" {}
        _Protection ("Protection strength", Float) = 0
        _Width ("Width", Float) = 0.025
        _NeckCoverage ("Neck coverage", 2D) = "black" {}
        _NeckCount ("Neck anchors", Int) = 0
        _CheckNeckOverlap ("Check overlap", Float) = 0
        _Global ("Global Lab", Vector) = (0,0,0,0)
        _DirectBase ("Adjust raw main texture", Float) = 0
        _SeamOnly ("Isolate neck texels", Float) = 0
        _BakeSecond ("Bake the second main texture", Float) = 0
        _Identity ("Region code", Float) = 0
        _RenderMode ("Mode", Float) = 0
        _Premultiply ("Premultiply", Float) = 0
        _Cull ("Cull", Float) = 2
        _Src ("Src", Float) = 1
        _Dst ("Dst", Float) = 0
        _SrcA ("Src alpha", Float) = 1
        _DstA ("Dst alpha", Float) = 10
        _Op ("Blend op", Float) = 0
        _OpA ("Alpha blend op", Float) = 0
        _ZTest ("ZTest", Float) = 4
        _ZWrite ("ZWrite", Float) = 1
        _ColorMask ("ColorMask", Float) = 15
        _StencilRef ("Stencil", Float) = 0
        _StencilComp ("Stencil compare", Float) = 8
        _StencilReadMask ("Stencil read", Float) = 255
        _StencilWriteMask ("Stencil write", Float) = 255
        _StencilPass ("Stencil pass", Float) = 0
        _StencilFail ("Stencil fail", Float) = 0
        _StencilZFail ("Stencil zfail", Float) = 0
        _OffsetFactor ("Depth factor", Float) = 0
        _OffsetUnits ("Depth units", Float) = 0
        _Layer2 ("Layer 2", 2D) = "white" {}
        _LayerMask2 ("Layer mask 2", 2D) = "white" {}
        _LayerColor2 ("Layer tint 2", Color) = (1,1,1,1)
        _LayerParams2 ("Enabled, blend, UV, angle", Vector) = (0,0,0,0)
        _LayerDecal2 ("Decal flags", Vector) = (0,0,0,0)
        _LayerFlip2 ("Mirror flags", Vector) = (0,0,0,0)
        _LayerAlpha2 ("Alpha and lighting", Vector) = (0,1,0,0)
        _Layer3 ("Layer 3", 2D) = "white" {}
        _LayerMask3 ("Layer mask 3", 2D) = "white" {}
        _LayerColor3 ("Layer tint 3", Color) = (1,1,1,1)
        _LayerParams3 ("Enabled, blend, UV, angle", Vector) = (0,0,0,0)
        _LayerDecal3 ("Decal flags", Vector) = (0,0,0,0)
        _LayerFlip3 ("Mirror flags", Vector) = (0,0,0,0)
        _LayerAlpha3 ("Alpha and lighting", Vector) = (0,1,0,0)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        HLSLINCLUDE
        #include "UnityCG.cginc"
        #include "SurfaceColor.hlsl"
        ENDHLSL

        Pass
        {
            Name "SURFACE"
            Cull [_Cull]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Blend [_Src] [_Dst], [_SrcA] [_DstA]
            BlendOp [_Op], [_OpA]
            ColorMask [_ColorMask]
            Offset [_OffsetFactor], [_OffsetUnits]
            Stencil
            {
                Ref [_StencilRef]
                Comp [_StencilComp]
                ReadMask [_StencilReadMask]
                WriteMask [_StencilWriteMask]
                Pass [_StencilPass]
                Fail [_StencilFail]
                ZFail [_StencilZFail]
            }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SurfaceVert
            #pragma fragment SurfaceFrag
            ENDHLSL
        }

        Pass
        {
            Name "BAKE"
            Tags { "LightMode"="Meta" }
            Cull Off
            ZTest Always
            ZWrite Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex BakeVert
            #pragma fragment BakeFrag
            ENDHLSL
        }

        Pass
        {
            Name "OVERLAP"
            Tags { "LightMode"="Meta" }
            Cull Off
            ZTest Always
            ZWrite Off
            Blend One One
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex BakeVert
            #pragma fragment OverlapFrag
            ENDHLSL
        }
        Pass
        {
            Name "LOCAL_MIP"
            Tags { "LightMode"="Meta" }
            Cull Off
            ZTest Always
            ZWrite Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment LocalMipFrag
            ENDHLSL
        }
    }
}
