using Silk.NET.OpenGL;
using System;

namespace LimitlessSquareEngine
{
    internal partial class Graphics
    {
        private uint _smaaEdgeDetectionProgram = 0;
        private uint _smaaWeightProgram = 0;
        private int _smaaEdgeColorTextureLoc = -1;
        private int _smaaEdgeRtMetricsLoc = -1;
        private int _smaaWeightEdgesTextureLoc = -1;
        private int _smaaWeightAreaTextureLoc = -1;
        private int _smaaWeightSearchTextureLoc = -1;
        private int _smaaWeightRtMetricsLoc = -1;
        private int _smaaWeightSubsampleLoc = -1;

        private uint _smaaAreaTexture = 0;
        private uint _smaaSearchTexture = 0;
        private bool _smaaLookupTexturesCreated = false;

        private uint _smaaEdgesTexture = 0;
        private uint _smaaEdgesFramebuffer = 0;
        private uint _smaaBlendTexture = 0;
        private uint _smaaBlendFramebuffer = 0;
        private int _smaaTargetWidth = 0;
        private int _smaaTargetHeight = 0;

        private const string _smaaCommonSource = @"
#define SMAA_RT_METRICS uSmaaRtMetrics
#define SMAA_THRESHOLD 0.1
#define SMAA_MAX_SEARCH_STEPS 16
#define SMAA_MAX_SEARCH_STEPS_DIAG 8
#define SMAA_CORNER_ROUNDING 25
#define SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR 2.0

#define SMAATexture2D(tex) sampler2D tex
#define SMAATexturePass2D(tex) tex
#define SMAASampleLevelZero(tex, coord) textureLod(tex, coord, 0.0)
#define SMAASampleLevelZeroPoint(tex, coord) textureLod(tex, coord, 0.0)
#define SMAASampleLevelZeroOffset(tex, coord, offset) textureLodOffset(tex, coord, 0.0, offset)
#define SMAASample(tex, coord) texture(tex, coord)
#define SMAASamplePoint(tex, coord) texture(tex, coord)
#define SMAASampleOffset(tex, coord, offset) texture(tex, coord, offset)
#define SMAA_FLATTEN
#define SMAA_BRANCH
#define lerp(a, b, t) mix(a, b, t)
#define saturate(a) clamp(a, 0.0, 1.0)
#define mad(a, b, c) fma(a, b, c)
#define float2 vec2
#define float3 vec3
#define float4 vec4
#define int2 ivec2
#define int3 ivec3
#define int4 ivec4
#define bool2 bvec2
#define bool3 bvec3
#define bool4 bvec4

#define SMAA_AREATEX_SELECT(sample) sample.rg
#define SMAA_SEARCHTEX_SELECT(sample) sample.r
#define SMAA_AREATEX_MAX_DISTANCE 16
#define SMAA_AREATEX_MAX_DISTANCE_DIAG 20
#define SMAA_AREATEX_PIXEL_SIZE (1.0 / float2(160.0, 560.0))
#define SMAA_AREATEX_SUBTEX_SIZE (1.0 / 7.0)
#define SMAA_SEARCHTEX_SIZE float2(66.0, 33.0)
#define SMAA_SEARCHTEX_PACKED_SIZE float2(64.0, 16.0)
#define SMAA_CORNER_ROUNDING_NORM (float(SMAA_CORNER_ROUNDING) / 100.0)

void SMAAMovc(bool2 cond, inout float2 variable, float2 value) {
    SMAA_FLATTEN if (cond.x) variable.x = value.x;
    SMAA_FLATTEN if (cond.y) variable.y = value.y;
}

void SMAAMovc(bool4 cond, inout float4 variable, float4 value) {
    SMAAMovc(cond.xy, variable.xy, value.xy);
    SMAAMovc(cond.zw, variable.zw, value.zw);
}

void SMAAEdgeDetectionVS(float2 texcoord,
                         out float4 offset[3]) {
    offset[0] = mad(SMAA_RT_METRICS.xyxy, float4(-1.0, 0.0, 0.0, -1.0), texcoord.xyxy);
    offset[1] = mad(SMAA_RT_METRICS.xyxy, float4( 1.0, 0.0, 0.0,  1.0), texcoord.xyxy);
    offset[2] = mad(SMAA_RT_METRICS.xyxy, float4(-2.0, 0.0, 0.0, -2.0), texcoord.xyxy);
}

void SMAABlendingWeightCalculationVS(float2 texcoord,
                                     out float2 pixcoord,
                                     out float4 offset[3]) {
    pixcoord = texcoord * SMAA_RT_METRICS.zw;

    offset[0] = mad(SMAA_RT_METRICS.xyxy, float4(-0.25, -0.125,  1.25, -0.125), texcoord.xyxy);
    offset[1] = mad(SMAA_RT_METRICS.xyxy, float4(-0.125, -0.25, -0.125,  1.25), texcoord.xyxy);

    offset[2] = mad(SMAA_RT_METRICS.xxyy,
                    float4(-2.0, 2.0, -2.0, 2.0) * float(SMAA_MAX_SEARCH_STEPS),
                    float4(offset[0].xz, offset[1].yw));
}

void SMAANeighborhoodBlendingVS(float2 texcoord,
                                out float4 offset) {
    offset = mad(SMAA_RT_METRICS.xyxy, float4( 1.0, 0.0, 0.0,  1.0), texcoord.xyxy);
}

float2 SMAALumaEdgeDetectionPS(float2 texcoord,
                               float4 offset[3],
                               SMAATexture2D(colorTex)) {
    float2 threshold = float2(SMAA_THRESHOLD, SMAA_THRESHOLD);

    float3 weights = float3(0.2126, 0.7152, 0.0722);
    float L = dot(SMAASamplePoint(colorTex, texcoord).rgb, weights);

    float Lleft = dot(SMAASamplePoint(colorTex, offset[0].xy).rgb, weights);
    float Ltop  = dot(SMAASamplePoint(colorTex, offset[0].zw).rgb, weights);

    float4 delta;
    delta.xy = abs(L - float2(Lleft, Ltop));
    float2 edges = step(threshold, delta.xy);

    if (dot(edges, float2(1.0, 1.0)) == 0.0)
        discard;

    float Lright = dot(SMAASamplePoint(colorTex, offset[1].xy).rgb, weights);
    float Lbottom = dot(SMAASamplePoint(colorTex, offset[1].zw).rgb, weights);
    delta.zw = abs(L - float2(Lright, Lbottom));

    float2 maxDelta = max(delta.xy, delta.zw);

    float Lleftleft = dot(SMAASamplePoint(colorTex, offset[2].xy).rgb, weights);
    float Ltoptop = dot(SMAASamplePoint(colorTex, offset[2].zw).rgb, weights);
    delta.zw = abs(float2(Lleft, Ltop) - float2(Lleftleft, Ltoptop));

    maxDelta = max(maxDelta.xy, delta.zw);
    float finalDelta = max(maxDelta.x, maxDelta.y);

    edges.xy *= step(finalDelta, SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR * delta.xy);

    return edges;
}

float2 SMAADecodeDiagBilinearAccess(float2 e) {
    e.r = e.r * abs(5.0 * e.r - 5.0 * 0.75);
    return round(e);
}

float4 SMAADecodeDiagBilinearAccess(float4 e) {
    e.rb = e.rb * abs(5.0 * e.rb - 5.0 * 0.75);
    return round(e);
}

float2 SMAASearchDiag1(SMAATexture2D(edgesTex), float2 texcoord, float2 dir, out float2 e) {
    float4 coord = float4(texcoord, -1.0, 1.0);
    float3 t = float3(SMAA_RT_METRICS.xy, 1.0);
    while (coord.z < float(SMAA_MAX_SEARCH_STEPS_DIAG - 1) &&
           coord.w > 0.9) {
        coord.xyz = mad(t, float3(dir, 1.0), coord.xyz);
        e = SMAASampleLevelZero(edgesTex, coord.xy).rg;
        coord.w = dot(e, float2(0.5, 0.5));
    }
    return coord.zw;
}

float2 SMAASearchDiag2(SMAATexture2D(edgesTex), float2 texcoord, float2 dir, out float2 e) {
    float4 coord = float4(texcoord, -1.0, 1.0);
    coord.x += 0.25 * SMAA_RT_METRICS.x;
    float3 t = float3(SMAA_RT_METRICS.xy, 1.0);
    while (coord.z < float(SMAA_MAX_SEARCH_STEPS_DIAG - 1) &&
           coord.w > 0.9) {
        coord.xyz = mad(t, float3(dir, 1.0), coord.xyz);
        e = SMAASampleLevelZero(edgesTex, coord.xy).rg;
        e = SMAADecodeDiagBilinearAccess(e);
        coord.w = dot(e, float2(0.5, 0.5));
    }
    return coord.zw;
}

float2 SMAAAreaDiag(SMAATexture2D(areaTex), float2 dist, float2 e, float offset) {
    float2 texcoord = mad(float2(SMAA_AREATEX_MAX_DISTANCE_DIAG, SMAA_AREATEX_MAX_DISTANCE_DIAG), e, dist);

    texcoord = mad(SMAA_AREATEX_PIXEL_SIZE, texcoord, 0.5 * SMAA_AREATEX_PIXEL_SIZE);

    texcoord.x += 0.5;

    texcoord.y += SMAA_AREATEX_SUBTEX_SIZE * offset;

    return SMAA_AREATEX_SELECT(SMAASampleLevelZero(areaTex, texcoord));
}

float2 SMAACalculateDiagWeights(SMAATexture2D(edgesTex), SMAATexture2D(areaTex), float2 texcoord, float2 e, float4 subsampleIndices) {
    float2 weights = float2(0.0, 0.0);

    float4 d;
    float2 end;
    if (e.r > 0.0) {
        d.xz = SMAASearchDiag1(SMAATexturePass2D(edgesTex), texcoord, float2(-1.0,  1.0), end);
        d.x += float(end.y > 0.9);
    } else
        d.xz = float2(0.0, 0.0);
    d.yw = SMAASearchDiag1(SMAATexturePass2D(edgesTex), texcoord, float2(1.0, -1.0), end);

    SMAA_BRANCH
    if (d.x + d.y > 2.0) {
        float4 coords = mad(float4(-d.x + 0.25, d.x, d.y, -d.y - 0.25), SMAA_RT_METRICS.xyxy, texcoord.xyxy);
        float4 c;
        c.xy = SMAASampleLevelZeroOffset(edgesTex, coords.xy, int2(-1,  0)).rg;
        c.zw = SMAASampleLevelZeroOffset(edgesTex, coords.zw, int2( 1,  0)).rg;
        c.yxwz = SMAADecodeDiagBilinearAccess(c.xyzw);

        float2 cc = mad(float2(2.0, 2.0), c.xz, c.yw);

        SMAAMovc(bool2(step(0.9, d.zw)), cc, float2(0.0, 0.0));

        weights += SMAAAreaDiag(SMAATexturePass2D(areaTex), d.xy, cc, subsampleIndices.z);
    }

    d.xz = SMAASearchDiag2(SMAATexturePass2D(edgesTex), texcoord, float2(-1.0, -1.0), end);
    if (SMAASampleLevelZeroOffset(edgesTex, texcoord, int2(1, 0)).r > 0.0) {
        d.yw = SMAASearchDiag2(SMAATexturePass2D(edgesTex), texcoord, float2(1.0, 1.0), end);
        d.y += float(end.y > 0.9);
    } else
        d.yw = float2(0.0, 0.0);

    SMAA_BRANCH
    if (d.x + d.y > 2.0) {
        float4 coords = mad(float4(-d.x, -d.x, d.y, d.y), SMAA_RT_METRICS.xyxy, texcoord.xyxy);
        float4 c;
        c.x  = SMAASampleLevelZeroOffset(edgesTex, coords.xy, int2(-1,  0)).g;
        c.y  = SMAASampleLevelZeroOffset(edgesTex, coords.xy, int2( 0, -1)).r;
        c.zw = SMAASampleLevelZeroOffset(edgesTex, coords.zw, int2( 1,  0)).gr;
        float2 cc = mad(float2(2.0, 2.0), c.xz, c.yw);

        SMAAMovc(bool2(step(0.9, d.zw)), cc, float2(0.0, 0.0));

        weights += SMAAAreaDiag(SMAATexturePass2D(areaTex), d.xy, cc, subsampleIndices.w).gr;
    }

    return weights;
}

float SMAASearchLength(SMAATexture2D(searchTex), float2 e, float offset) {
    float2 scale = SMAA_SEARCHTEX_SIZE * float2(0.5, -1.0);
    float2 bias = SMAA_SEARCHTEX_SIZE * float2(offset, 1.0);

    scale += float2(-1.0,  1.0);
    bias  += float2( 0.5, -0.5);

    scale *= 1.0 / SMAA_SEARCHTEX_PACKED_SIZE;
    bias *= 1.0 / SMAA_SEARCHTEX_PACKED_SIZE;

    return SMAA_SEARCHTEX_SELECT(SMAASampleLevelZero(searchTex, mad(scale, e, bias)));
}

float SMAASearchXLeft(SMAATexture2D(edgesTex), SMAATexture2D(searchTex), float2 texcoord, float end) {
    float2 e = float2(0.0, 1.0);
    while (texcoord.x > end &&
           e.g > 0.8281 &&
           e.r == 0.0) {
        e = SMAASampleLevelZero(edgesTex, texcoord).rg;
        texcoord = mad(-float2(2.0, 0.0), SMAA_RT_METRICS.xy, texcoord);
    }

    float offset = mad(-(255.0 / 127.0), SMAASearchLength(SMAATexturePass2D(searchTex), e, 0.0), 3.25);
    return mad(SMAA_RT_METRICS.x, offset, texcoord.x);
}

float SMAASearchXRight(SMAATexture2D(edgesTex), SMAATexture2D(searchTex), float2 texcoord, float end) {
    float2 e = float2(0.0, 1.0);
    while (texcoord.x < end &&
           e.g > 0.8281 &&
           e.r == 0.0) {
        e = SMAASampleLevelZero(edgesTex, texcoord).rg;
        texcoord = mad(float2(2.0, 0.0), SMAA_RT_METRICS.xy, texcoord);
    }
    float offset = mad(-(255.0 / 127.0), SMAASearchLength(SMAATexturePass2D(searchTex), e, 0.5), 3.25);
    return mad(-SMAA_RT_METRICS.x, offset, texcoord.x);
}

float SMAASearchYUp(SMAATexture2D(edgesTex), SMAATexture2D(searchTex), float2 texcoord, float end) {
    float2 e = float2(1.0, 0.0);
    while (texcoord.y > end &&
           e.r > 0.8281 &&
           e.g == 0.0) {
        e = SMAASampleLevelZero(edgesTex, texcoord).rg;
        texcoord = mad(-float2(0.0, 2.0), SMAA_RT_METRICS.xy, texcoord);
    }
    float offset = mad(-(255.0 / 127.0), SMAASearchLength(SMAATexturePass2D(searchTex), e.gr, 0.0), 3.25);
    return mad(SMAA_RT_METRICS.y, offset, texcoord.y);
}

float SMAASearchYDown(SMAATexture2D(edgesTex), SMAATexture2D(searchTex), float2 texcoord, float end) {
    float2 e = float2(1.0, 0.0);
    while (texcoord.y < end &&
           e.r > 0.8281 &&
           e.g == 0.0) {
        e = SMAASampleLevelZero(edgesTex, texcoord).rg;
        texcoord = mad(float2(0.0, 2.0), SMAA_RT_METRICS.xy, texcoord);
    }
    float offset = mad(-(255.0 / 127.0), SMAASearchLength(SMAATexturePass2D(searchTex), e.gr, 0.5), 3.25);
    return mad(-SMAA_RT_METRICS.y, offset, texcoord.y);
}

float2 SMAAArea(SMAATexture2D(areaTex), float2 dist, float e1, float e2, float offset) {
    float2 texcoord = mad(float2(SMAA_AREATEX_MAX_DISTANCE, SMAA_AREATEX_MAX_DISTANCE), round(4.0 * float2(e1, e2)), dist);

    texcoord = mad(SMAA_AREATEX_PIXEL_SIZE, texcoord, 0.5 * SMAA_AREATEX_PIXEL_SIZE);

    texcoord.y = mad(SMAA_AREATEX_SUBTEX_SIZE, offset, texcoord.y);

    return SMAA_AREATEX_SELECT(SMAASampleLevelZero(areaTex, texcoord));
}

void SMAADetectHorizontalCornerPattern(SMAATexture2D(edgesTex), inout float2 weights, float4 texcoord, float2 d) {
    float2 leftRight = step(d.xy, d.yx);
    float2 rounding = (1.0 - SMAA_CORNER_ROUNDING_NORM) * leftRight;

    rounding /= leftRight.x + leftRight.y;

    float2 factor = float2(1.0, 1.0);
    factor.x -= rounding.x * SMAASampleLevelZeroOffset(edgesTex, texcoord.xy, int2(0,  1)).r;
    factor.x -= rounding.y * SMAASampleLevelZeroOffset(edgesTex, texcoord.zw, int2(1,  1)).r;
    factor.y -= rounding.x * SMAASampleLevelZeroOffset(edgesTex, texcoord.xy, int2(0, -2)).r;
    factor.y -= rounding.y * SMAASampleLevelZeroOffset(edgesTex, texcoord.zw, int2(1, -2)).r;

    weights *= saturate(factor);
}

void SMAADetectVerticalCornerPattern(SMAATexture2D(edgesTex), inout float2 weights, float4 texcoord, float2 d) {
    float2 leftRight = step(d.xy, d.yx);
    float2 rounding = (1.0 - SMAA_CORNER_ROUNDING_NORM) * leftRight;

    rounding /= leftRight.x + leftRight.y;

    float2 factor = float2(1.0, 1.0);
    factor.x -= rounding.x * SMAASampleLevelZeroOffset(edgesTex, texcoord.xy, int2( 1, 0)).g;
    factor.x -= rounding.y * SMAASampleLevelZeroOffset(edgesTex, texcoord.zw, int2( 1, 1)).g;
    factor.y -= rounding.x * SMAASampleLevelZeroOffset(edgesTex, texcoord.xy, int2(-2, 0)).g;
    factor.y -= rounding.y * SMAASampleLevelZeroOffset(edgesTex, texcoord.zw, int2(-2, 1)).g;

    weights *= saturate(factor);
}

float4 SMAABlendingWeightCalculationPS(float2 texcoord,
                                       float2 pixcoord,
                                       float4 offset[3],
                                       SMAATexture2D(edgesTex),
                                       SMAATexture2D(areaTex),
                                       SMAATexture2D(searchTex),
                                       float4 subsampleIndices) {
    float4 weights = float4(0.0, 0.0, 0.0, 0.0);

    float2 e = SMAASample(edgesTex, texcoord).rg;

    SMAA_BRANCH
    if (e.g > 0.0) {
        weights.rg = SMAACalculateDiagWeights(SMAATexturePass2D(edgesTex), SMAATexturePass2D(areaTex), texcoord, e, subsampleIndices);

        SMAA_BRANCH
        if (weights.r == -weights.g) {

        float2 d;

        float3 coords;
        coords.x = SMAASearchXLeft(SMAATexturePass2D(edgesTex), SMAATexturePass2D(searchTex), offset[0].xy, offset[2].x);
        coords.y = offset[1].y;
        d.x = coords.x;

        float e1 = SMAASampleLevelZero(edgesTex, coords.xy).r;

        coords.z = SMAASearchXRight(SMAATexturePass2D(edgesTex), SMAATexturePass2D(searchTex), offset[0].zw, offset[2].y);
        d.y = coords.z;

        d = abs(round(mad(SMAA_RT_METRICS.zz, d, -pixcoord.xx)));

        float2 sqrt_d = sqrt(d);

        float e2 = SMAASampleLevelZeroOffset(edgesTex, coords.zy, int2(1, 0)).r;

        weights.rg = SMAAArea(SMAATexturePass2D(areaTex), sqrt_d, e1, e2, subsampleIndices.y);

        coords.y = texcoord.y;
        SMAADetectHorizontalCornerPattern(SMAATexturePass2D(edgesTex), weights.rg, coords.xyzy, d);

        } else
            e.r = 0.0;
    }

    SMAA_BRANCH
    if (e.r > 0.0) {
        float2 d;

        float3 coords;
        coords.y = SMAASearchYUp(SMAATexturePass2D(edgesTex), SMAATexturePass2D(searchTex), offset[1].xy, offset[2].z);
        coords.x = offset[0].x;
        d.x = coords.y;

        float e1 = SMAASampleLevelZero(edgesTex, coords.xy).g;

        coords.z = SMAASearchYDown(SMAATexturePass2D(edgesTex), SMAATexturePass2D(searchTex), offset[1].zw, offset[2].w);
        d.y = coords.z;

        d = abs(round(mad(SMAA_RT_METRICS.ww, d, -pixcoord.yy)));

        float2 sqrt_d = sqrt(d);

        float e2 = SMAASampleLevelZeroOffset(edgesTex, coords.xz, int2(0, 1)).g;

        weights.ba = SMAAArea(SMAATexturePass2D(areaTex), sqrt_d, e1, e2, subsampleIndices.x);

        coords.x = texcoord.x;
        SMAADetectVerticalCornerPattern(SMAATexturePass2D(edgesTex), weights.ba, coords.xyxz, d);
    }

    return weights;
}

float4 SMAANeighborhoodBlendingPS(float2 texcoord,
                                  float4 offset,
                                  SMAATexture2D(colorTex),
                                  SMAATexture2D(blendTex)) {
    float4 a;
    a.x = SMAASample(blendTex, offset.xy).a;
    a.y = SMAASample(blendTex, offset.zw).g;
    a.wz = SMAASample(blendTex, texcoord).xz;

    SMAA_BRANCH
    if (dot(a, float4(1.0, 1.0, 1.0, 1.0)) < 1e-5) {
        float4 color = SMAASampleLevelZero(colorTex, texcoord);

        return color;
    } else {
        bool h = max(a.x, a.z) > max(a.y, a.w);

        float4 blendingOffset = float4(0.0, a.y, 0.0, a.w);
        float2 blendingWeight = a.yw;
        SMAAMovc(bool4(h, h, h, h), blendingOffset, float4(a.x, 0.0, a.z, 0.0));
        SMAAMovc(bool2(h, h), blendingWeight, a.xz);
        blendingWeight /= dot(blendingWeight, float2(1.0, 1.0));

        float4 blendingCoord = mad(blendingOffset, float4(SMAA_RT_METRICS.xy, -SMAA_RT_METRICS.xy), texcoord.xyxy);

        float4 color = blendingWeight.x * SMAASampleLevelZero(colorTex, blendingCoord.xy);
        color += blendingWeight.y * SMAASampleLevelZero(colorTex, blendingCoord.zw);

        return color;
    }
}
";

        private const string _smaaFullscreenVertexSource = @"#version 430 core
layout(location = 0) in vec3 aPos;
layout(location = 2) in vec2 aUv;
out vec2 vUv;
void main()
{
    vUv = aUv;
    gl_Position = vec4(aPos.xy, 0.0, 1.0);
}";

        private const string _smaaEdgeFragmentSourcePrefix = @"#version 430 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uColorTexture;
uniform vec4 uSmaaRtMetrics;
";

        private const string _smaaEdgeFragmentSourceSuffix = @"
void main()
{
    float4 offset[3];
    SMAAEdgeDetectionVS(vUv, offset);
    FragColor = float4(SMAALumaEdgeDetectionPS(vUv, offset, uColorTexture), 0.0, 0.0);
}";

        private const string _smaaEdgeFragmentSource = _smaaEdgeFragmentSourcePrefix + _smaaCommonSource + _smaaEdgeFragmentSourceSuffix;

        private const string _smaaWeightFragmentSourcePrefix = @"#version 430 core
in vec2 vUv;
out vec4 FragColor;
uniform sampler2D uEdgesTexture;
uniform sampler2D uAreaTexture;
uniform sampler2D uSearchTexture;
uniform vec4 uSmaaRtMetrics;
uniform vec4 uSubsampleIndices;
";

        private const string _smaaWeightFragmentSourceSuffix = @"
void main()
{
    float2 pixcoord;
    float4 offset[3];
    SMAABlendingWeightCalculationVS(vUv, pixcoord, offset);
    FragColor = SMAABlendingWeightCalculationPS(vUv, pixcoord, offset, uEdgesTexture, uAreaTexture, uSearchTexture, uSubsampleIndices);
}";

        private const string _smaaWeightFragmentSource = _smaaWeightFragmentSourcePrefix + _smaaCommonSource + _smaaWeightFragmentSourceSuffix;

        private uint CreateSmaaEdgeDetectionProgram()
        {
            uint vs = CompileShader(ShaderType.VertexShader, _smaaFullscreenVertexSource);
            uint fs = CompileShader(ShaderType.FragmentShader, _smaaEdgeFragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"[X] SMAA edge detection shader link failed: {infoLog}");
            }

            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, fs);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);

            return program;
        }

        private uint CreateSmaaWeightProgram()
        {
            uint vs = CompileShader(ShaderType.VertexShader, _smaaFullscreenVertexSource);
            uint fs = CompileShader(ShaderType.FragmentShader, _smaaWeightFragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"[X] SMAA blending weight shader link failed: {infoLog}");
            }

            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, fs);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);

            return program;
        }

        private uint CreateSmaaAreaTexture()
        {
            byte[] data = SmaaTextures.GetAreaTexBytes();

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);

            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.RG8,
                (uint)SmaaTextures.AreaTexWidth,
                (uint)SmaaTextures.AreaTexHeight,
                0,
                PixelFormat.RG,
                PixelType.UnsignedByte,
                (ReadOnlySpan<byte>)data);

            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            return texture;
        }

        private uint CreateSmaaSearchTexture()
        {
            byte[] data = SmaaTextures.GetSearchTexBytes();

            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);

            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.R8,
                (uint)SmaaTextures.SearchTexWidth,
                (uint)SmaaTextures.SearchTexHeight,
                0,
                PixelFormat.Red,
                PixelType.UnsignedByte,
                (ReadOnlySpan<byte>)data);

            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            return texture;
        }

        private void InitializeSmaaResources()
        {
            if (_smaaEdgeDetectionProgram == 0)
            {
                _smaaEdgeDetectionProgram = CreateSmaaEdgeDetectionProgram();
                _smaaEdgeColorTextureLoc = _gl.GetUniformLocation(_smaaEdgeDetectionProgram, "uColorTexture");
                _smaaEdgeRtMetricsLoc = _gl.GetUniformLocation(_smaaEdgeDetectionProgram, "uSmaaRtMetrics");
            }

            if (_smaaWeightProgram == 0)
            {
                _smaaWeightProgram = CreateSmaaWeightProgram();
                _smaaWeightEdgesTextureLoc = _gl.GetUniformLocation(_smaaWeightProgram, "uEdgesTexture");
                _smaaWeightAreaTextureLoc = _gl.GetUniformLocation(_smaaWeightProgram, "uAreaTexture");
                _smaaWeightSearchTextureLoc = _gl.GetUniformLocation(_smaaWeightProgram, "uSearchTexture");
                _smaaWeightRtMetricsLoc = _gl.GetUniformLocation(_smaaWeightProgram, "uSmaaRtMetrics");
                _smaaWeightSubsampleLoc = _gl.GetUniformLocation(_smaaWeightProgram, "uSubsampleIndices");
            }

            if (!_smaaLookupTexturesCreated)
            {
                _smaaAreaTexture = CreateSmaaAreaTexture();
                _smaaSearchTexture = CreateSmaaSearchTexture();
                _smaaLookupTexturesCreated = true;
            }
        }

        private uint CreateSmaaTargetTexture(int width, int height, InternalFormat internalFormat, PixelFormat pixelFormat, int bytesPerPixel)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);

            byte[] emptyData = new byte[Math.Max(1, width) * Math.Max(1, height) * bytesPerPixel];

            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                internalFormat,
                (uint)Math.Max(1, width),
                (uint)Math.Max(1, height),
                0,
                pixelFormat,
                PixelType.UnsignedByte,
                (ReadOnlySpan<byte>)emptyData);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            return texture;
        }

        private void EnsureSmaaTargets(int width, int height)
        {
            if (_smaaEdgesFramebuffer != 0 &&
                _smaaBlendFramebuffer != 0 &&
                _smaaTargetWidth == width &&
                _smaaTargetHeight == height)
            {
                return;
            }

            DeleteSmaaTargets();

            _smaaTargetWidth = width;
            _smaaTargetHeight = height;

            _smaaEdgesTexture = CreateSmaaTargetTexture(width, height, InternalFormat.RG8, PixelFormat.RG, 2);
            _smaaEdgesFramebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _smaaEdgesFramebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _smaaEdgesTexture, 0);

            GLEnum edgesStatus = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (edgesStatus != GLEnum.FramebufferComplete)
                throw new Exception($"[X] SMAA edges framebuffer incomplete: {edgesStatus}");

            _smaaBlendTexture = CreateSmaaTargetTexture(width, height, InternalFormat.Rgba8, PixelFormat.Rgba, 4);
            _smaaBlendFramebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _smaaBlendFramebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _smaaBlendTexture, 0);

            GLEnum blendStatus = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (blendStatus != GLEnum.FramebufferComplete)
                throw new Exception($"[X] SMAA blend framebuffer incomplete: {blendStatus}");

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private void DeleteSmaaTargets()
        {
            if (_smaaEdgesTexture != 0)
            {
                _gl.DeleteTexture(_smaaEdgesTexture);
                _smaaEdgesTexture = 0;
            }

            if (_smaaEdgesFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_smaaEdgesFramebuffer);
                _smaaEdgesFramebuffer = 0;
            }

            if (_smaaBlendTexture != 0)
            {
                _gl.DeleteTexture(_smaaBlendTexture);
                _smaaBlendTexture = 0;
            }

            if (_smaaBlendFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_smaaBlendFramebuffer);
                _smaaBlendFramebuffer = 0;
            }

            _smaaTargetWidth = 0;
            _smaaTargetHeight = 0;
        }

        private void DeleteSmaaResources()
        {
            DeleteSmaaTargets();

            if (_smaaAreaTexture != 0)
            {
                _gl.DeleteTexture(_smaaAreaTexture);
                _smaaAreaTexture = 0;
            }

            if (_smaaSearchTexture != 0)
            {
                _gl.DeleteTexture(_smaaSearchTexture);
                _smaaSearchTexture = 0;
            }

            _smaaLookupTexturesCreated = false;

            if (_smaaEdgeDetectionProgram != 0)
            {
                _gl.DeleteProgram(_smaaEdgeDetectionProgram);
                _smaaEdgeDetectionProgram = 0;
            }

            if (_smaaWeightProgram != 0)
            {
                _gl.DeleteProgram(_smaaWeightProgram);
                _smaaWeightProgram = 0;
            }

            _smaaEdgeColorTextureLoc = -1;
            _smaaEdgeRtMetricsLoc = -1;
            _smaaWeightEdgesTextureLoc = -1;
            _smaaWeightAreaTextureLoc = -1;
            _smaaWeightSearchTextureLoc = -1;
            _smaaWeightRtMetricsLoc = -1;
            _smaaWeightSubsampleLoc = -1;
        }

        private void ExecuteSmaaPasses()
        {
            InitializeSmaaResources();

            if (_smaaEdgeDetectionProgram == 0 || _smaaWeightProgram == 0)
                return;

            int width = Math.Max(1, _postProcessSceneWidth);
            int height = Math.Max(1, _postProcessSceneHeight);

            EnsureSmaaTargets(width, height);

            if (_smaaEdgesFramebuffer == 0 || _smaaBlendFramebuffer == 0)
                return;

            _gl.Disable(GLEnum.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(GLEnum.ScissorTest);
            _gl.Disable(GLEnum.Blend);
            _gl.Disable(GLEnum.CullFace);

            float rtX = 1f / width;
            float rtY = 1f / height;

            _currentProgram = _smaaEdgeDetectionProgram;
            _gl.UseProgram(_smaaEdgeDetectionProgram);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _smaaEdgesFramebuffer);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _postProcessSceneColorTexture);
            if (_smaaEdgeColorTextureLoc != -1)
                _gl.Uniform1(_smaaEdgeColorTextureLoc, 0);
            if (_smaaEdgeRtMetricsLoc != -1)
                _gl.Uniform4(_smaaEdgeRtMetricsLoc, rtX, rtY, (float)width, (float)height);

            DrawFullscreenQuad();

            _currentProgram = _smaaWeightProgram;
            _gl.UseProgram(_smaaWeightProgram);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _smaaBlendFramebuffer);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _smaaEdgesTexture);
            if (_smaaWeightEdgesTextureLoc != -1)
                _gl.Uniform1(_smaaWeightEdgesTextureLoc, 0);

            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 1));
            _gl.BindTexture(TextureTarget.Texture2D, _smaaAreaTexture);
            if (_smaaWeightAreaTextureLoc != -1)
                _gl.Uniform1(_smaaWeightAreaTextureLoc, 1);

            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 2));
            _gl.BindTexture(TextureTarget.Texture2D, _smaaSearchTexture);
            if (_smaaWeightSearchTextureLoc != -1)
                _gl.Uniform1(_smaaWeightSearchTextureLoc, 2);

            if (_smaaWeightRtMetricsLoc != -1)
                _gl.Uniform4(_smaaWeightRtMetricsLoc, rtX, rtY, (float)width, (float)height);
            if (_smaaWeightSubsampleLoc != -1)
                _gl.Uniform4(_smaaWeightSubsampleLoc, 0f, 0f, 0f, 0f);

            DrawFullscreenQuad();

            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 2));
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + 1));
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }
    }
}
