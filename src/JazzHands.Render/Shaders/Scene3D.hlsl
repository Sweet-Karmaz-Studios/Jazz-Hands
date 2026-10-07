// The 3D scenes (Phases 47 and 48). Each 3D layer is its canvas, a picture drawn flat with its
// effects and masks, set in the world by one matrix and drawn as a quad through the camera. Meshes
// (text, shapes, glTF models) read their corners from a structured buffer and their materials from
// glTF's metallic and roughness model. Depth is reversed and infinite (near over distance), so the
// nearest surface has the greatest depth and precision holds at every distance. Solid meshes come
// first; layers and translucent meshes back to front; at equal depth the one drawn later (the
// higher track) wins, so coplanar layers stack as they would flat.
//
// Lighting is per pixel and shared by both: ambient and an environment picture for fill, Lambert
// diffuse and a GGX highlight from each point, spot and directional light, shadows from distance
// maps, and reflections of the environment by roughness. A layer is the metallic-free case with its
// own reflectance. Something that does not take lights, or a scene with none, shows its colour as
// it is. Colours stay premultiplied linear light.
//
// The shadow pass draws casters into a slice of a float array per light (six for a point light, a
// cube's faces), storing the distance from the light, or the depth along a directional light.
// Depth of field runs afterwards on the finished scene: a gather by circle of confusion that lets
// a blurred sample reach a pixel only when its own blur covers it, and a sample behind the pixel
// only when the pixel is blurred too, so a sharp edge is not smeared by what is out of focus
// behind it.

#include "Common.hlsli"

#define MAX_LIGHTS 8
#define MAX_SHADOW_SLICES 24
#define DOF_SAMPLES 80
#define PI 3.14159265

cbuffer LayerConstants : register(b0)
{
    row_major float4x4 World;           // canvas pixels (x, y, 0), or a mesh's own space, to the world
    row_major float4x4 ViewProjection;  // the world to clip space: the camera's, or a shadow slice's
    float4 CameraPosition;
    float4 CameraForward;
    float2 CanvasSize;
    float Opacity;
    uint Lit;                           // take the lights; 0 shows the colour as it is
    float Ambient;
    float Diffuse;
    float Specular;
    float Roughness;
    uint AcceptsShadows;
    uint LightCount;
    uint ShadowKind;                    // shadow pass: 0 distance from ShadowOrigin, 1 depth along ShadowDirection
    float AlphaCut;                     // shadow pass: coverage under this casts nothing
    float4 ShadowOrigin;
    float4 ShadowDirection;
    uint Bicubic;                       // sample near one to one with the compositor's bicubic filter
    uint HasEnvironment;                // an environment picture is bound at t11
    float2 LayerPad;
};

struct Light
{
    float4 PositionKind;    // xyz where it is; w 0 ambient, 1 point, 2 spot, 3 directional, 4 environment
    float4 DirectionCone;   // xyz the way it shines; w cosine of a spot's outer half angle
    float4 ColorInner;      // rgb colour times intensity; w cosine of a spot's inner half angle
    float4 Falloff;         // x 0 none, 1 smooth, 2 inverse square; y radius; z falloff distance
    float4 Shadow;          // x first slice or -1; y darkness; z softness in world pixels; w world pixels per texel (directional) or per texel per unit of distance
};

cbuffer LightConstants : register(b1)
{
    Light Lights[MAX_LIGHTS];
};

cbuffer ShadowConstants : register(b2)
{
    row_major float4x4 ShadowViewProjection[MAX_SHADOW_SLICES];
};

cbuffer MaterialConstants : register(b4)
{
    row_major float4x4 NormalMatrix;    // a mesh's normals to the world: the inverse transpose of World
    float4 BaseColor;
    float3 Emissive;
    float Metallic;
    float MaterialRoughness;
    float NormalScale;
    float AlphaCutoff;
    uint AlphaModeValue;                // 0 opaque, 1 mask, 2 blend
    uint Maps;                          // 1 base colour, 2 metal and roughness, 4 normal, 8 emissive, 16 occlusion
    uint DoubleSided;
    uint UvSets;                        // the maps (by the bits of Maps) that read the second texture coordinates
    float MaterialPad;
};

struct MeshVertexData
{
    float3 Position;
    float3 Normal;
    float2 Uv;
    float4 Tangent;
    float2 Uv1;
};

Texture2D<float4> Canvas : register(t0);        // with its mipmaps, for a layer seen smaller or slantwise
Texture2DArray<float> ShadowMaps : register(t1);
StructuredBuffer<MeshVertexData> Vertices : register(t5);
Texture2D<float4> BaseColorMap : register(t6);
Texture2D<float4> MetalRoughMap : register(t7);
Texture2D<float4> NormalMap : register(t8);
Texture2D<float4> EmissiveMap : register(t9);
Texture2D<float4> OcclusionMap : register(t10);
Texture2D<float4> EnvironmentMap : register(t11); // equirectangular, linear, with mipmaps
SamplerState Anisotropic : register(s3);
SamplerState AnisotropicWrap : register(s4);

struct LayerVertex
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
    float3 WorldPosition : TEXCOORD1;
    float3 Normal : TEXCOORD2;
};

struct MeshPixel
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
    float3 WorldPosition : TEXCOORD1;
    float3 Normal : TEXCOORD2;
    float4 Tangent : TEXCOORD3;
    float2 Uv1 : TEXCOORD4;
};

// The texture coordinates a map (by its bit in Maps) reads: the second set where the material says.
float2 MapUv(uint map, MeshPixel input)
{
    return (UvSets & map) != 0 ? input.Uv1 : input.Uv;
}

struct SceneOutput
{
    float4 Color : SV_Target0;
    float Depth : SV_Target1;
};

static const float2 Poisson[16] =
{
    float2(-0.94201624, -0.39906216), float2(0.94558609, -0.76890725),
    float2(-0.09418410, -0.92938870), float2(0.34495938, 0.29387760),
    float2(-0.91588581, 0.45771432), float2(-0.81544232, -0.87912464),
    float2(-0.38277543, 0.27676845), float2(0.97484398, 0.75648379),
    float2(0.44323325, -0.97511554), float2(0.53742981, -0.47373420),
    float2(-0.26496911, -0.41893023), float2(0.79197514, 0.19090188),
    float2(-0.24188840, 0.99706507), float2(-0.81409955, 0.91437590),
    float2(0.19984126, 0.78641367), float2(0.14383161, -0.14100790),
};

// A quad of the canvas, four vertices as a strip, the corner from the id.
LayerVertex VsLayer(uint vertexId : SV_VertexID)
{
    float2 corner = float2(vertexId & 1, vertexId >> 1);
    float4 world = mul(float4(corner * CanvasSize, 0.0, 1.0), World);

    LayerVertex output;
    output.Position = mul(world, ViewProjection);
    output.Uv = corner;
    output.WorldPosition = world.xyz;

    // The plane's normal from its two edges, which holds under any scale.
    output.Normal = cross(World[0].xyz, World[1].xyz);
    return output;
}

// A mesh's corner, read from its buffer: an indexed draw gives the index as the vertex id.
MeshPixel VsMesh(uint vertexId : SV_VertexID)
{
    MeshVertexData corner = Vertices[vertexId];
    float4 world = mul(float4(corner.Position, 1.0), World);

    MeshPixel output;
    output.Position = mul(world, ViewProjection);
    output.Uv = corner.Uv;
    output.Uv1 = corner.Uv1;
    output.WorldPosition = world.xyz;
    output.Normal = mul(float4(corner.Normal, 0.0), NormalMatrix).xyz;
    output.Tangent = float4(mul(float4(corner.Tangent.xyz, 0.0), World).xyz, corner.Tangent.w);
    return output;
}

float FalloffAt(float4 falloff, float distance)
{
    uint mode = (uint)falloff.x;
    float radius = falloff.y;
    if (mode == 1)
    {
        return 1.0 - smoothstep(radius, radius + max(falloff.z, 1e-3), distance);
    }

    if (mode == 2)
    {
        return distance <= radius ? 1.0 : (radius * radius) / max(distance * distance, 1e-6);
    }

    return 1.0;
}

// The face of a point light's cube a direction falls on: +x, -x, +y, -y, +z, -z.
uint CubeFace(float3 v)
{
    float3 a = abs(v);
    if (a.x >= a.y && a.x >= a.z)
    {
        return v.x > 0.0 ? 0 : 1;
    }

    if (a.y >= a.z)
    {
        return v.y > 0.0 ? 2 : 3;
    }

    return v.z > 0.0 ? 4 : 5;
}

// How much of a light reaches a point past the shadows: 1 lit, 1 - darkness in full shadow.
float ShadowVisibility(Light light, uint kind, float3 world, float3 normal, float3 toLight)
{
    uint slice = (uint)light.Shadow.x;
    float metric;
    if (kind == 3)
    {
        metric = dot(world - light.PositionKind.xyz, light.DirectionCone.xyz);
    }
    else
    {
        float3 fromLight = world - light.PositionKind.xyz;
        metric = length(fromLight);
        if (kind == 1)
        {
            slice += CubeFace(fromLight);
        }
    }

    float4 clip = mul(float4(world, 1.0), ShadowViewProjection[slice]);
    if (clip.w <= 0.0)
    {
        return 1.0;
    }

    float2 ndc = clip.xy / clip.w;
    float2 uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    if (any(uv < 0.0) || any(uv > 1.0))
    {
        return 1.0;
    }

    uint width, height, slices;
    ShadowMaps.GetDimensions(width, height, slices);

    // A texel covers more of the world further from a point or spot light; the bias grows with it
    // and with how slantwise the light falls, which keeps a surface from shadowing itself.
    float texel = kind == 3 ? light.Shadow.w : metric * light.Shadow.w;
    float facing = saturate(dot(normal, toLight));
    float slope = sqrt(saturate(1.0 - facing * facing)) / max(facing, 0.05);
    float bias = 0.5 + texel * (1.5 + 2.0 * min(slope, 10.0));
    float radius = 1.0 + light.Shadow.z / max(texel, 1e-3);

    float lit = 0.0;
    [unroll]
    for (int i = 0; i < 16; i++)
    {
        float2 at = uv + Poisson[i] * radius / float2(width, height);
        float stored = ShadowMaps.SampleLevel(PointClamp, float3(at, slice), 0);
        lit += metric - bias <= stored ? 1.0 : 0.0;
    }

    return 1.0 - light.Shadow.y * (1.0 - lit / 16.0);
}

// Where a direction lands on an equirectangular picture: across from the back round, up at the top.
float2 Equirectangular(float3 direction)
{
    float3 d = normalize(direction);
    float u = atan2(d.x, d.z) / (2.0 * PI) + 0.5;
    float v = acos(clamp(-d.y, -1.0, 1.0)) / PI;
    return float2(u, v);
}

float3 EnvironmentAt(float3 direction, float blur)
{
    uint width, height, levels;
    EnvironmentMap.GetDimensions(0, width, height, levels);
    float3 colour = EnvironmentMap.SampleLevel(LinearWrap, Equirectangular(direction), blur * (levels - 1)).rgb;
    return max(colour, 0.0);
}

// Light reaching the eye from a surface: diffuse colour, reflectance at normal incidence (f0) and
// at grazing (f90), roughness, its normal and the way to the eye.
float3 Shade(float3 diffuseColour, float3 f0, float f90, float roughness, float ambientShare, float diffuseShare, float occlusion, float3 n, float3 v, float3 world)
{
    float alpha = max(roughness * roughness, 0.002);
    float alpha2 = alpha * alpha;
    float nv = saturate(dot(n, v)) + 1e-4;

    float3 ambient = 0.0;
    float3 diffuse = 0.0;
    float3 highlight = 0.0;
    float3 reflection = 0.0;

    [loop]
    for (uint i = 0; i < LightCount; i++)
    {
        Light light = Lights[i];
        uint kind = (uint)light.PositionKind.w;
        float3 radiance = light.ColorInner.rgb;
        if (kind == 0)
        {
            ambient += radiance;
            continue;
        }

        if (kind == 4)
        {
            if (HasEnvironment != 0)
            {
                // Fill from the picture's colours round the normal, and its reflection, blurred by
                // roughness, weighed by how much a surface reflects at this angle.
                ambient += EnvironmentAt(n, 1.0) * radiance;
                float3 fresnel = f0 + (max(f90 * (1.0 - roughness), f0) - f0) * pow(1.0 - nv, 5.0);
                reflection += EnvironmentAt(reflect(-v, n), roughness) * radiance * fresnel;
            }

            continue;
        }

        float3 l;
        float attenuation = 1.0;
        if (kind == 3)
        {
            l = -light.DirectionCone.xyz;
        }
        else
        {
            float3 toLight = light.PositionKind.xyz - world;
            float distance = length(toLight);
            l = toLight / max(distance, 1e-4);
            attenuation = FalloffAt(light.Falloff, distance);
            if (kind == 2)
            {
                attenuation *= smoothstep(light.DirectionCone.w, light.ColorInner.w, dot(-l, light.DirectionCone.xyz));
            }
        }

        float nl = saturate(dot(n, l));
        if (nl <= 0.0 || attenuation <= 0.0)
        {
            continue;
        }

        if (AcceptsShadows != 0 && light.Shadow.x >= 0.0)
        {
            attenuation *= ShadowVisibility(light, kind, world, n, l);
        }

        float3 h = normalize(l + v);
        float nh = saturate(dot(n, h));
        float vh = saturate(dot(v, h));
        float denominator = nh * nh * (alpha2 - 1.0) + 1.0;
        float d = alpha2 / (PI * denominator * denominator);
        float k = alpha / 2.0;
        float visibility = 0.25 / ((nl * (1.0 - k) + k) * (nv * (1.0 - k) + k));
        float3 f = f0 + (f90 - f0) * pow(1.0 - vh, 5.0);

        float3 arriving = radiance * nl * attenuation;
        diffuse += arriving;
        highlight += arriving * d * visibility * f;
    }

    return diffuseColour * (ambient * ambientShare * occlusion + diffuse * diffuseShare) + highlight + reflection * occlusion;
}

// The canvas at a pixel. Seen at about its own size, the compositor's filter, so a 3D layer at
// rest looks as it would flat; seen smaller, or slantwise, trilinear and anisotropic through its
// mipmaps, so a layer going off into the distance does not shimmer.
float4 SampleCanvas(float2 uv)
{
    // Derivatives first, outside any branch.
    float2 across = ddx(uv);
    float2 down = ddy(uv);
    float footprint = max(length(across * CanvasSize), length(down * CanvasSize));
    float4 filtered = Canvas.SampleGrad(Anisotropic, uv, across, down);
    if (footprint > 1.25)
    {
        return filtered;
    }

    return Bicubic != 0 ? SampleBicubic(Canvas, uv, CanvasSize) : Canvas.SampleLevel(LinearClamp, uv, 0);
}

SceneOutput PsLayer(LayerVertex input)
{
    float4 picture = max(SampleCanvas(input.Uv), 0.0);
    if (picture.a <= 1.0 / 1024.0)
    {
        discard;
    }

    float3 color = picture.rgb;
    if (Lit != 0)
    {
        float3 straight = picture.rgb / picture.a;
        float3 n = normalize(input.Normal);
        float3 v = normalize(CameraPosition.xyz - input.WorldPosition);

        // Both faces take light: the one the camera sees.
        if (dot(n, v) < 0.0)
        {
            n = -n;
        }

        float f0 = 0.16 * Specular * Specular;
        color = Shade(straight, f0.xxx, saturate(50.0 * f0), Roughness, Ambient, Diffuse, 1.0, n, v, input.WorldPosition) * picture.a;
    }

    SceneOutput output;
    output.Color = float4(color, picture.a) * Opacity;
    output.Depth = dot(input.WorldPosition - CameraPosition.xyz, CameraForward.xyz);
    return output;
}

// A mesh's surface colour and alpha, with its base colour picture.
float4 MeshBase(float2 uv)
{
    float4 base = BaseColor;
    if ((Maps & 1) != 0)
    {
        base *= BaseColorMap.Sample(AnisotropicWrap, uv);
    }

    return base;
}

SceneOutput PsMesh(MeshPixel input, bool front : SV_IsFrontFace)
{
    float4 base = MeshBase(MapUv(1, input));
    if (AlphaModeValue == 1 && base.a < AlphaCutoff)
    {
        discard;
    }

    float alpha = AlphaModeValue == 0 ? 1.0 : base.a;
    float3 colour = base.rgb;
    float3 emissive = Emissive;
    if ((Maps & 8) != 0)
    {
        emissive *= EmissiveMap.Sample(AnisotropicWrap, MapUv(8, input)).rgb;
    }

    if (Lit != 0)
    {
        float3 v = normalize(CameraPosition.xyz - input.WorldPosition);
        float3 n = normalize(input.Normal);

        // A double-sided material seen from behind faces the eye.
        if (DoubleSided != 0 && !front)
        {
            n = -n;
        }

        if ((Maps & 4) != 0)
        {
            float3 t = input.Tangent.xyz - n * dot(n, input.Tangent.xyz);
            if (dot(t, t) > 1e-12)
            {
                t = normalize(t);
                float3 b = cross(n, t) * (input.Tangent.w < 0.0 ? -1.0 : 1.0);
                float3 bent = NormalMap.Sample(AnisotropicWrap, MapUv(4, input)).xyz * 2.0 - 1.0;
                bent.xy *= NormalScale;
                n = normalize(bent.x * t + bent.y * b + bent.z * n);
            }
        }

        float metallic = Metallic;
        float roughness = MaterialRoughness;
        if ((Maps & 2) != 0)
        {
            float4 mr = MetalRoughMap.Sample(AnisotropicWrap, MapUv(2, input));
            roughness *= mr.g;
            metallic *= mr.b;
        }

        float occlusion = (Maps & 16) != 0 ? OcclusionMap.Sample(AnisotropicWrap, MapUv(16, input)).r : 1.0;
        float3 f0 = lerp(float3(0.04, 0.04, 0.04), colour, metallic);
        colour = Shade(colour * (1.0 - metallic), f0, 1.0, roughness, 1.0, 1.0, occlusion, n, v, input.WorldPosition);
    }

    SceneOutput output;
    output.Color = float4((colour + emissive) * alpha, alpha) * Opacity;
    output.Depth = dot(input.WorldPosition - CameraPosition.xyz, CameraForward.xyz);
    return output;
}

struct ShadowVertex
{
    float4 Position : SV_POSITION;
    float2 Uv : TEXCOORD0;
    float3 WorldPosition : TEXCOORD1;
};

ShadowVertex VsShadow(uint vertexId : SV_VertexID)
{
    float2 corner = float2(vertexId & 1, vertexId >> 1);
    float4 world = mul(float4(corner * CanvasSize, 0.0, 1.0), World);

    ShadowVertex output;
    output.Position = mul(world, ViewProjection);
    output.Uv = corner;
    output.WorldPosition = world.xyz;
    return output;
}

ShadowVertex VsMeshShadow(uint vertexId : SV_VertexID)
{
    MeshVertexData corner = Vertices[vertexId];
    float4 world = mul(float4(corner.Position, 1.0), World);

    ShadowVertex output;
    output.Position = mul(world, ViewProjection);
    output.Uv = (UvSets & 1) != 0 ? corner.Uv1 : corner.Uv;   // what the base colour reads, for its alpha
    output.WorldPosition = world.xyz;
    return output;
}

float ShadowMetric(float3 world)
{
    return ShadowKind == 1
        ? dot(world - ShadowOrigin.xyz, ShadowDirection.xyz)
        : length(world - ShadowOrigin.xyz);
}

float PsShadow(ShadowVertex input) : SV_Target
{
    if (Canvas.SampleLevel(LinearClamp, input.Uv, 0).a * Opacity < AlphaCut)
    {
        discard;
    }

    return ShadowMetric(input.WorldPosition);
}

float PsMeshShadow(ShadowVertex input) : SV_Target
{
    if (AlphaModeValue != 0 && MeshBase(input.Uv).a * Opacity < AlphaCut)
    {
        discard;
    }

    return ShadowMetric(input.WorldPosition);
}

cbuffer DepthOfFieldConstants : register(b3)
{
    float2 TexelSize;
    float Focus;            // world pixels from the camera
    float Aperture;         // output pixels of blur at infinity
    float MaxRadius;        // output pixels
    uint TileSize;          // pixels per side of a tile of the largest blur
    uint2 FrameSize;        // the output, in pixels
};

Texture2D<float4> SceneColor : register(t2);
Texture2D<float> SceneDepth : register(t3);
Texture2D<float> CocTiles : register(t4);

FullScreenVertex VsFullScreen(uint vertexId : SV_VertexID)
{
    return FullScreenTriangle(vertexId);
}

// The circle of confusion's radius, in output pixels: nothing at the focus, the aperture at infinity.
float Coc(float depth)
{
    return min(Aperture * abs(depth - Focus) / max(depth, 1.0), MaxRadius);
}

// The largest blur in each tile of the frame, so a pixel gathers only as wide as anything near it
// can reach, and its samples are spent where they count.
float PsCocTiles(FullScreenVertex input) : SV_Target
{
    uint2 tile = (uint2)input.Position.xy;
    uint2 first = tile * TileSize;
    float largest = 0.0;
    for (uint y = 0; y < TileSize; y++)
    {
        for (uint x = 0; x < TileSize; x++)
        {
            uint2 at = min(first + uint2(x, y), FrameSize - 1);
            largest = max(largest, Coc(SceneDepth.Load(int3(at, 0))));
        }
    }

    return largest;
}

float4 PsDepthOfField(FullScreenVertex input) : SV_Target
{
    // How far anything round here can reach: the largest blur in this tile and those beside it.
    int2 tile = (int2)((uint2)input.Position.xy / TileSize);
    uint tilesWide, tilesHigh;
    CocTiles.GetDimensions(tilesWide, tilesHigh);
    float reachable = 0.0;
    for (int dy = -1; dy <= 1; dy++)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            int2 at = clamp(tile + int2(dx, dy), int2(0, 0), int2(tilesWide - 1, tilesHigh - 1));
            reachable = max(reachable, CocTiles.Load(int3(at, 0)));
        }
    }

    float centreDepth = SceneDepth.SampleLevel(PointClamp, input.Uv, 0);
    float4 centre = SceneColor.SampleLevel(PointClamp, input.Uv, 0);
    if (reachable < 0.5)
    {
        return centre;
    }

    float centreCoc = Coc(centreDepth);
    float centreWeight = 1.0 / max(centreCoc * centreCoc, 1.0);
    float4 sum = centre * centreWeight;
    float total = centreWeight;

    // Each sample stands for this much of the disc's area, so its weight is that over the area
    // its own blur spreads it across.
    float share = reachable * reachable / DOF_SAMPLES;

    [loop]
    for (uint i = 0; i < DOF_SAMPLES; i++)
    {
        float r = reachable * sqrt((i + 0.5) / DOF_SAMPLES);
        float theta = i * 2.39996323;
        float2 uv = input.Uv + float2(cos(theta), sin(theta)) * r * TexelSize;

        float depth = SceneDepth.SampleLevel(PointClamp, uv, 0);
        float coc = Coc(depth);
        float reach = saturate(coc - r + 1.0);
        if (depth > centreDepth)
        {
            reach = min(reach, saturate(centreCoc - r + 1.0));
        }

        float w = reach * share / max(coc * coc, 1.0);
        sum += SceneColor.SampleLevel(LinearClamp, uv, 0) * w;
        total += w;
    }

    return sum / total;
}
