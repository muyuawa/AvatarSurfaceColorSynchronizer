sampler2D _MainTex, _MainColorAdjustMask, _MainGradationTex, _AlphaMask, _DetailAlbedoMap, _DetailMask;
sampler2D _Layer2, _Layer3, _LayerMask2, _LayerMask3, _Skin, _Protect, _NeckCoverage;
float4 _MainTex_ST, _AlphaMask_ST, _DetailAlbedoMap_ST, _Layer2_ST, _Layer3_ST, _Color, _Tone, _AlphaParams;
float4 _LayerParams2, _LayerParams3, _LayerDecal2, _LayerDecal3, _LayerFlip2, _LayerFlip3;
float4 _LayerColor2, _LayerColor3, _LayerAlpha2, _LayerAlpha3, _DetailParams;
float _Premultiply, _Identity, _RenderMode, _MainGradationStrength, _Protection, _Width;
float4 _Global;
float4 _NeckMin, _NeckMax;
float4 _NeckAnchors[64], _NeckDeltas[64];
int _NeckCount;
float _CheckNeckOverlap, _DirectBase, _SeamOnly, _BakeSecond;
float4 _NeckCoverage_TexelSize;
static const float OutsideBand = 64;

struct Vertex
{
    float4 position : POSITION;
    float4 tangent : TANGENT;
    float2 uv : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
    float2 uv2 : TEXCOORD2;
    float2 uv3 : TEXCOORD3;
    float3 world : TEXCOORD4;
};
struct Varyings
{
    float4 position : SV_POSITION;
    float4 uv01 : TEXCOORD0;
    float4 uv23 : TEXCOORD1;
    float3 world : TEXCOORD2;
    float2 sampleUv : TEXCOORD3;
    float hand : TEXCOORD4;
    float2 bakeUv : TEXCOORD5;
};

Varyings SurfaceVert(Vertex v)
{
    Varyings o;
    o.position = UnityObjectToClipPos(v.position);
    o.uv01 = float4(v.uv, v.uv1);
    o.uv23 = float4(v.uv2, v.uv3);
    o.world = 0;
    o.sampleUv = TRANSFORM_TEX(v.uv, _MainTex);
    o.hand = v.tangent.w * unity_WorldTransformParams.w;
    o.bakeUv = o.sampleUv;
    return o;
}

Varyings BakeVert(Vertex v)
{
    Varyings o = SurfaceVert(v);
    o.world = v.world;
    if (_BakeSecond > .5) o.bakeUv = TRANSFORM_TEX(v.uv, _Layer2);
    o.position = float4(2 * o.bakeUv.x - 1, (2 * o.bakeUv.y - 1) * (UNITY_UV_STARTS_AT_TOP ? -1 : 1), .5, 1);
    return o;
}

float3 Tone(float3 c)
{
    if (all(abs(_Tone - float4(0,1,1,1)) < 1e-6)) return c;
    c = pow(max(c, 0), _Tone.w);
    float peak = max(c.r, max(c.g, c.b));
    float span = peak - min(c.r, min(c.g, c.b));
    float hue = peak == c.r ? (c.g - c.b) / max(span, 1e-9) :
                peak == c.g ? 2 + (c.b - c.r) / max(span, 1e-9) : 4 + (c.r - c.g) / max(span, 1e-9);
    float sat = saturate(span / max(peak, 1e-9) * _Tone.y);
    float v = saturate(peak * _Tone.z);
    float3 rgb = saturate(abs(frac(hue / 6 + _Tone.x + float3(0, 2.0/3, 1.0/3)) * 6 - 3) - 1);
    return v * lerp(1, rgb, sat);
}

float3 Linear(float3 c)
{
    #ifdef UNITY_COLORSPACE_GAMMA
    return GammaToLinearSpace(c);
    #else
    return c;
    #endif
}

float3 ToLab(float3 rgb)
{
    float3 c = pow(max(mul(float3x3(
        .41222147,.53633254,.05144599,
        .21190350,.68069955,.10739696,
        .08830246,.28171884,.62997870), Linear(rgb)), 0), 1.0 / 3.0);
    return mul(float3x3(.21045426,.79361779,-.00407205,1.97799850,-2.42859221,.45059371,
        .02590404,.78277177,-.80867577), c);
}

float3 FromLab(float3 lab)
{
    float3 c = mul(float3x3(1,.39633778,.21580376,1,-.10556135,-.06385417,1,-.08948418,-1.29148555), lab);
    c = saturate(mul(float3x3(4.07674166,-3.30771159,.23096993,-1.26843800,2.60975740,-.34131940,
        -.00419609,-.70341861,1.70761470), c * c * c));
    #ifdef UNITY_COLORSPACE_GAMMA
    return LinearToGammaSpace(c);
    #else
    return c;
    #endif
}

float2 LayerUv(Varyings i, float n)
{
    return n < .5 ? i.uv01.xy : n < 1.5 ? i.uv01.zw : n < 2.5 ? i.uv23.xy : i.uv23.zw;
}
float2 Rotate(float2 uv, float angle)
{
    float s, c;
    sincos(angle, s, c);
    uv -= .5;
    return float2(c * uv.x - s * uv.y, s * uv.x + c * uv.y) + .5;
}
float4 ReadLayer(sampler2D tex, sampler2D mask, Varyings i, float4 st, float4 param, float4 decal, float4 flip, float4 tint)
{
    if (param.x < .5) return 0;
    float2 uv = LayerUv(i, param.z);
    float right = i.hand > 0;
    float visible = 1;
    if (decal.x > .5)
    {
        bool copied = uv.x < .5;
        if (decal.w > .5) uv.x = abs(uv.x - .5) + .5;
        uv = uv * st.xy + st.zw;
        if (flip.x > .5 && right > .5) uv.x = 1 - uv.x;
        if (flip.y > .5 && copied) uv.x = 1 - uv.x;
        if (decal.y > .5 && right > .5 || decal.z > .5 && right < .5) visible = 0;
        uv = Rotate((uv - st.zw) / st.xy, param.w) * st.xy + st.zw;
        visible *= all(uv >= 0) && all(uv <= 1);
    }
    else uv = Rotate(uv * st.xy + st.zw, param.w);
    float4 c = tex2D(tex, uv);
    if (flip.z > .5)
    {
        float d = max(min(c.r,c.g), min(max(c.r,c.g),c.b)) - .5;
        c = float4(1,1,1,saturate(d / max(fwidth(d), 1e-4) + .5));
    }
    c *= tint;
    c.a *= visible * tex2D(mask, i.sampleUv).r;
    return c;
}

float3 ApplyLayer(float3 original, float4 c, float blend)
{
    float3 result = c.rgb;
    if (blend > 2.5) result = original * c.rgb;
    else if (blend > 1.5) result = max(original, original + c.rgb - original * c.rgb);
    else if (blend > .5) result = original + c.rgb;
    return lerp(original, result, c.a);
}
void ProcessLayer(inout float4 baseColor, float4 layer, float blend, float4 alphaMode)
{
    if (_RenderMode > .5 && alphaMode.x > .5)
    {
        if (alphaMode.x < 1.5) baseColor.a = layer.a;
        else if (alphaMode.x < 2.5) baseColor.a *= layer.a;
        else if (alphaMode.x < 3.5) baseColor.a = saturate(baseColor.a + layer.a);
        else baseColor.a = saturate(baseColor.a - layer.a);
        layer.a = 1;
    }
    baseColor.rgb = ApplyLayer(baseColor.rgb, layer, blend);
}

float4 Compose(float4 raw, float mask, float4 layer2, float4 layer3, float3 detail)
{
    float3 corrected = Tone(raw.rgb);
    if (_MainGradationStrength > 0)
    {
        float3 coord = corrected;
        #ifndef UNITY_COLORSPACE_GAMMA
        coord = LinearToGammaSpace(coord);
        #endif
        float3 c = float3(tex2Dlod(_MainGradationTex, float4(coord.r,.5,0,0)).r,
            tex2Dlod(_MainGradationTex, float4(coord.g,.5,0,0)).g, tex2Dlod(_MainGradationTex, float4(coord.b,.5,0,0)).b);
        corrected = lerp(corrected,c,_MainGradationStrength);
    }
    float4 result = float4(lerp(raw.rgb, corrected, mask), raw.a) * _Color;
    result.rgb *= detail;
    if (_LayerParams2.x > .5) ProcessLayer(result, layer2, _LayerParams2.y, _LayerAlpha2);
    if (_LayerParams3.x > .5) ProcessLayer(result, layer3, _LayerParams3.y, _LayerAlpha3);
    return result;
}

float3 Detail(Varyings i)
{
    if (_DetailParams.x < .5) return 1;
    float2 uv = _DetailParams.y > .5 ? i.uv01.zw : i.uv01.xy;
    return lerp(1, tex2D(_DetailAlbedoMap, TRANSFORM_TEX(uv, _DetailAlbedoMap)).rgb * unity_ColorSpaceDouble.rgb,
        tex2D(_DetailMask, i.sampleUv).a);
}

float4 SurfaceFrag(Varyings i) : SV_Target
{
    float4 a = ReadLayer(_Layer2, _LayerMask2, i, _Layer2_ST, _LayerParams2, _LayerDecal2, _LayerFlip2, _LayerColor2);
    float4 b = ReadLayer(_Layer3, _LayerMask3, i, _Layer3_ST, _LayerParams3, _LayerDecal3, _LayerFlip3, _LayerColor3);
    float4 c = Compose(tex2D(_MainTex, i.sampleUv), tex2D(_MainColorAdjustMask, i.sampleUv).r, a, b, Detail(i));
    if (_RenderMode < .5) c.a = 1;
    else
    {
        if (_AlphaParams.x > .5)
        {
            float m = saturate(tex2D(_AlphaMask, TRANSFORM_TEX(i.uv01.xy, _AlphaMask)).r * _AlphaParams.y + _AlphaParams.z);
            if (_AlphaParams.x < 1.5) c.a = m;
            else if (_AlphaParams.x < 2.5) c.a *= m;
            else if (_AlphaParams.x < 3.5) c.a = saturate(c.a + m);
            else c.a = saturate(c.a - m);
        }
        if (_RenderMode < 1.5) clip(c.a - _AlphaParams.w);
        else clip(c.a - _AlphaParams.w - .0001);
    }
    if (_Identity > .5) return float4(_Identity, i.position.z, 0, 1);
    if (_Premultiply > .5) c.rgb *= c.a;
    return c;
}

float3 NeckOffset(float3 position)
{
    if (_NeckCount == 0 || any(position < _NeckMin.xyz) || any(position > _NeckMax.xyz)) return 0;
    float width = max(_Width, .0001);
    float closest = width * width;
    float total = 0;
    float3 shift = 0;
    [loop] for (int i = 0; i < _NeckCount; i++)
    {
        float3 offset = position - _NeckAnchors[i].xyz;
        float distance = dot(offset, offset);
        closest = min(closest, distance);
        if (distance >= width * width) continue;
        float similarity = saturate(1 - distance / (width * width));
        float w = similarity * similarity / (distance + .000012);
        shift += _NeckDeltas[i].xyz * w;
        total += w;
    }
    float falloff = 1 - smoothstep(0, width, sqrt(closest));
    return shift * (falloff / max(total, 1e-6));
}

float Protection(Varyings i, float alpha2, float alpha3)
{
    float protect = max(max(alpha2, alpha3) * _Protection, tex2D(_Protect, i.uv01.xy).r);
    return saturate(tex2D(_Skin, i.uv01.xy).r * (1 - protect));
}

float4 OverlapFrag(Varyings i) : SV_Target
{
    float3 local = NeckOffset(i.world);
    if (_SeamOnly < .5) return float4(abs(local), 1);
    if (dot(local, local) > 1e-10)
    {
        float a = ReadLayer(_Layer2, _LayerMask2, i, _Layer2_ST, _LayerParams2, _LayerDecal2, _LayerFlip2, _LayerColor2).a;
        float b = ReadLayer(_Layer3, _LayerMask3, i, _Layer3_ST, _LayerParams3, _LayerDecal3, _LayerFlip3, _LayerColor3).a;
        // When editing layer 2, keep its alpha/coverage and protect the other layer, not layer 2 from itself.
        if (_BakeSecond > .5 && a <= .001) return float4(0, 0, 0, OutsideBand);
        local *= Protection(i, _BakeSecond > .5 ? 0 : a, b);
        if (dot(local, local) > 1e-10) return float4(local, 1);
    }
    // Additive atlas: any outside/protected surface vetoes the entire texel, even if the neck was drawn later.
    return float4(0, 0, 0, OutsideBand);
}

float UnsafeUv(float2 uv)
{
    return step(OutsideBand, tex2Dlod(_NeckCoverage, float4(uv, 0, 0)).a);
}

float4 BakeLocal(Varyings i)
{
    float2 uv = i.bakeUv;
    float4 coverage = tex2Dlod(_NeckCoverage, float4(uv, 0, 0));
    clip(coverage.a - .5);
    clip(OutsideBand - .5 - coverage.a);
    float2 texel = _NeckCoverage_TexelSize.xy;
    // Leave a one-texel guard, including diagonals: filtering can sample outside the UV polygon.
    [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
            if (x != 0 || y != 0) clip(.5 - UnsafeUv(uv + float2(x, y) * texel));

    float feather = 1;
    [unroll] for (int radius = 2; radius <= 3; radius++)
    {
        float2 xy = radius * texel;
        float nearOutside = max(max(UnsafeUv(uv + float2(xy.x, 0)), UnsafeUv(uv - float2(xy.x, 0))),
                                max(UnsafeUv(uv + float2(0, xy.y)), UnsafeUv(uv - float2(0, xy.y))));
        feather = min(feather, 1 - nearOutside * (radius == 2 ? .65 : .25));
    }
    float4 at = float4(uv, 0, 0);
    float4 raw = _BakeSecond > .5 ? tex2Dlod(_Layer2, at) : tex2Dlod(_MainTex, at);
    float3 delta = coverage.rgb * (feather / coverage.a);
    clip(dot(delta, delta) - 1e-10);
    return float4(FromLab(ToLab(raw.rgb) + delta), raw.a);
}

float4 BakeFrag(Varyings i) : SV_Target
{
    if (_SeamOnly > .5) return BakeLocal(i);
    float4 raw = tex2D(_MainTex, i.sampleUv);
    float4 a = ReadLayer(_Layer2, _LayerMask2, i, _Layer2_ST, _LayerParams2, _LayerDecal2, _LayerFlip2, _LayerColor2);
    float4 b = ReadLayer(_Layer3, _LayerMask3, i, _Layer3_ST, _LayerParams3, _LayerDecal3, _LayerFlip3, _LayerColor3);
    float3 detail = Detail(i);
    float toneMask = tex2D(_MainColorAdjustMask, i.sampleUv).r;
    float4 composite = Compose(raw, toneMask, a, b, detail);
    float3 local = NeckOffset(i.world);
    if (_CheckNeckOverlap > .5)
    {
        float4 coverage = tex2D(_NeckCoverage, i.sampleUv);
        if (coverage.a > 1.5 && length(abs(local) - coverage.rgb / coverage.a) > .0025)
            local = 0;
    }
    float3 delta = (_Global.xyz + local) * Protection(i, a.a, b.a);
    if (_DirectBase > .5)
        return float4(dot(delta, delta) < 1e-12 ? raw.rgb : FromLab(ToLab(raw.rgb) + delta), raw.a);
    float3 before = ToLab(composite.rgb);
    float3 desired = before + delta;
    float3 trial = raw.rgb;
    float3 best = trial;
    float bestCost = dot(delta, delta);
    if (bestCost < 1e-12) return float4(best, raw.a);
    // Damped local inverse. Evaluate the same GPU composite as the measurement pass.
    [loop] for (int iteration=0; iteration<5; iteration++)
    {
        float3 d = float3(.008,.008,.008);
        float3 current = ToLab(Compose(float4(trial,raw.a),toneMask,a,b,detail).rgb);
        float3 ex = ToLab(Compose(float4(trial+float3(d.x,0,0),raw.a),toneMask,a,b,detail).rgb);
        float3 ey = ToLab(Compose(float4(trial+float3(0,d.y,0),raw.a),toneMask,a,b,detail).rgb);
        float3 ez = ToLab(Compose(float4(trial+float3(0,0,d.z),raw.a),toneMask,a,b,detail).rgb);
        float3 x = (ex-current)/d.x, y = (ey-current)/d.y, z = (ez-current)/d.z;
        float3 error = desired-current;
        float3 p = cross(y,z), q = cross(z,x), r = cross(x,y);
        float det = dot(x,p);
        if (abs(det) < 1e-5) break;
        float3 move = float3(dot(error,p),dot(error,q),dot(error,r)) / det;
        // Project each channel into gamut independently. A saturated red must not freeze green and blue.
        trial = saturate(trial + move * .85);
        float3 errorAfter = desired - ToLab(Compose(float4(trial,raw.a),toneMask,a,b,detail).rgb);
        float cost = dot(errorAfter,errorAfter);
        if (cost >= bestCost) break;
        bestCost = cost;
        best = trial;
    }
    return float4(best, raw.a);
}

// Preserve the source's authored/compressed mip chain. Only the difference between
// the two temporary chains (generated identically from mip 0) belongs in each mip.
sampler2D _MipSource, _MipBefore, _MipAfter;
float _LocalMip;
float4 LocalMipFrag(v2f_img i) : SV_Target
{
    float4 uv = float4(i.uv, 0, _LocalMip);
    float4 source = tex2Dlod(_MipSource, uv);
    if (_LocalMip > .5)
        source.rgb = saturate(source.rgb + (tex2Dlod(_MipAfter, uv).rgb - tex2Dlod(_MipBefore, uv).rgb));
    return source;
}
