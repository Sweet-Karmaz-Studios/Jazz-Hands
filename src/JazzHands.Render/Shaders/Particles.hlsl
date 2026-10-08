// Particle fields (Phase 29a, on the GPU since 2026-10-08): one instance per dot or streak, a quad
// around it, shaded by the distance to its segment. A dot is a segment of no length. The field is
// worked out on the CPU (ParticleField) and handed over in target texels, so the picture is the
// same whatever device draws it, to the antialiasing.
#include "Effect.hlsli"

struct ParticleDot
{
    float2 From;
    float2 To;
    float Radius;
    float Weight;
    float2 Padding;
    float4 Color;
};

StructuredBuffer<ParticleDot> Dots : register(t0);

struct ParticleVertex
{
    float4 Position : SV_Position;
    nointerpolation float2 From : FROM;
    nointerpolation float2 To : TO;
    nointerpolation float2 Shape : SHAPE;
    nointerpolation float4 Color : COLOR;
};

ParticleVertex VsParticle(uint vertexId : SV_VertexID, uint instance : SV_InstanceID)
{
    ParticleDot particle = Dots[instance];

    // A texel past the edge, for the antialiased rim.
    float reach = particle.Radius + 1.0;
    float2 low = min(particle.From, particle.To) - reach;
    float2 high = max(particle.From, particle.To) + reach;
    float2 texel = lerp(low, high, float2(vertexId & 1, vertexId >> 1));

    ParticleVertex output;
    output.Position = float4(texel.x / Resolution.x * 2.0 - 1.0, 1.0 - texel.y / Resolution.y * 2.0, 0.0, 1.0);
    output.From = particle.From;
    output.To = particle.To;
    output.Shape = float2(particle.Radius, particle.Weight);
    output.Color = particle.Color;
    return output;
}

// Premultiplied colour times the share of this texel the disc or capsule covers: a texel's width
// of ramp across the edge, as Direct2D's antialiasing gives.
float4 PsParticle(ParticleVertex input) : SV_Target
{
    float2 along = input.To - input.From;
    float length2 = dot(along, along);
    float t = length2 > 0.0 ? saturate(dot(input.Position.xy - input.From, along) / length2) : 0.0;
    float distance = length(input.Position.xy - (input.From + along * t));
    float coverage = saturate(input.Shape.x + 0.5 - distance) * input.Shape.y;
    return input.Color * coverage;
}
