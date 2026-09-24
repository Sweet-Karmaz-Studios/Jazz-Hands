// The built-in transitions, one pixel shader each, all reading Transition.hlsli's constants. The
// classes in Effects/Transitions fill them; what each member means is written above each shader.
// Distances are in texels: the classes multiply sequence pixels by the quality before they come
// here, so half quality looks like full quality, smaller.

#include "Transition.hlsli"

static const float Pi = 3.14159265;
static const float TwoPi = 6.28318531;

// Mode 0 cross (in perceptual values), 1 film (in linear light).
float4 PsCrossfade(FullScreenVertex input) : SV_TARGET
{
    float4 a = LoadOutgoing(input.Position.xy);
    float4 b = LoadIncoming(input.Position.xy);
    return Mode == 1 ? lerp(a, b, Progress) : MixPerceptual(a, b, Progress);
}

// Tint the colour dipped to; Values.x the share of the transition held on the colour, 0 to 0.9.
float4 PsDip(FullScreenVertex input) : SV_TARGET
{
    float hold = saturate(Values.x);
    float fade = max((1.0 - hold) * 0.5, 1e-4);
    float4 a = LoadOutgoing(input.Position.xy);
    float4 b = LoadIncoming(input.Position.xy);

    if (Progress < 0.5)
    {
        return MixPerceptual(a, Tint, saturate(Progress / fade));
    }

    return MixPerceptual(Tint, b, saturate((Progress - (1.0 - fade)) / fade));
}

// Values: x the direction the edge travels in radians (0 left to right, clockwise, y down),
// y softness, z border width, both in texels. The incoming picture is behind the edge.
float4 PsWipeLinear(FullScreenVertex input) : SV_TARGET
{
    float2 direction = float2(cos(Values.x), sin(Values.x));
    float2 fromCentre = input.Position.xy - Resolution * 0.5;
    float along = dot(fromCentre, direction);

    // How far the frame reaches along the direction, so the edge starts and ends just off it.
    float reach = abs(direction.x) * Resolution.x * 0.5 + abs(direction.y) * Resolution.y * 0.5;
    float margin = Values.y + Values.z;
    float edge = lerp(-reach - margin, reach + margin, Progress);

    return Wipe(LoadOutgoing(input.Position.xy), LoadIncoming(input.Position.xy), edge - along, Values.y, Values.z);
}

// How much of a turn a sweep has made at a pixel, and the texels to its moving edge: the shared
// arithmetic of the clock and radial wipes. angle is the pixel's, 0 to span; the sweep covers
// span at progress 1, with a margin so a soft edge is off the frame at both ends.
float SweepDistance(float angle, float span, float radius, float softness)
{
    float margin = softness > 1e-3 ? 0.05 * span : 0.0;
    float edge = lerp(-margin, span + margin, Progress);
    return (edge - angle) * max(radius, 1.0);
}

// Values: x the angle it starts from in radians (0 at twelve o'clock), y softness, z border,
// w unused; Flag 1 counterclockwise. A hand sweeping round the frame centre.
float4 PsWipeClock(FullScreenVertex input) : SV_TARGET
{
    float2 v = input.Position.xy - Resolution * 0.5;
    float turn = atan2(v.x, -v.y) - Values.x;
    turn = Flag == 1 ? -turn : turn;
    float angle = turn - floor(turn / TwoPi) * TwoPi;

    float distance = SweepDistance(angle, TwoPi, length(v), Values.y);
    return Wipe(LoadOutgoing(input.Position.xy), LoadIncoming(input.Position.xy), distance, Values.y, Values.z);
}

// Mode the corner it pivots on: 0 top left, 1 top right, 2 bottom left, 3 bottom right. Values:
// y softness, z border; Flag 1 counterclockwise. A line swinging a quarter turn from one edge of
// the frame to the other.
float4 PsWipeRadial(FullScreenVertex input) : SV_TARGET
{
    float2 corner = float2(Mode == 1 || Mode == 3 ? Resolution.x : 0.0, Mode >= 2 ? Resolution.y : 0.0);
    float2 v = abs(input.Position.xy - corner);
    float angle = atan2(v.y, v.x);
    angle = Flag == 1 ? Pi * 0.5 - angle : angle;

    float distance = SweepDistance(angle, Pi * 0.5, length(v), Values.y);
    return Wipe(LoadOutgoing(input.Position.xy), LoadIncoming(input.Position.xy), distance, Values.y, Values.z);
}

// The distance of a point from the iris centre in the shape's own measure, in texels: round, a
// diamond, or a box with the frame's proportions.
float IrisMeasure(float2 position, float2 centre)
{
    float2 v = abs(position - centre);
    return Mode == 1 ? v.x + v.y : Mode == 2 ? max(v.x, v.y * Resolution.x / Resolution.y) : length(v);
}

// Mode the shape: 0 circle, 1 diamond, 2 box (the frame's own shape). Values: xy the centre in
// texels from the top left, z softness, w border; Flag 1 closes onto the incoming picture rather
// than opening from it.
float4 PsIris(FullScreenVertex input) : SV_TARGET
{
    float2 centre = Values.xy;

    // Far enough to uncover the corner furthest from the centre.
    float reach = max(
        max(IrisMeasure(float2(0.0, 0.0), centre), IrisMeasure(float2(Resolution.x, 0.0), centre)),
        max(IrisMeasure(float2(0.0, Resolution.y), centre), IrisMeasure(Resolution, centre)));
    float here = IrisMeasure(input.Position.xy, centre);

    float margin = Values.z + Values.w;
    float4 a = LoadOutgoing(input.Position.xy);
    float4 b = LoadIncoming(input.Position.xy);

    if (Flag == 1)
    {
        // Closing: the outgoing picture shrinks into the centre, the incoming one outside it.
        float radius = lerp(reach + margin, -margin, Progress);
        return Wipe(a, b, here - radius, Values.z, Values.w);
    }

    float opening = lerp(-margin, reach + margin, Progress);
    return Wipe(a, b, opening - here, Values.z, Values.w);
}

// Which way the pictures move, as a step in texture coordinates: left, right, up, down.
float2 Movement()
{
    return Mode == 0 ? float2(-1.0, 0.0) : Mode == 1 ? float2(1.0, 0.0) : Mode == 2 ? float2(0.0, -1.0) : float2(0.0, 1.0);
}

// Mode the direction the pictures move (0 left, 1 right, 2 up, 3 down). The incoming picture
// pushes the outgoing one off the frame.
float4 PsPush(FullScreenVertex input) : SV_TARGET
{
    float2 move = Movement();
    float2 fromOutgoing = input.Uv - move * Progress;
    return OnFrame(fromOutgoing) ? SampleOutgoing(fromOutgoing) : SampleIncoming(fromOutgoing + move);
}

// Mode the direction (0 left, 1 right, 2 up, 3 down); Flag 0 the incoming picture slides in over
// the outgoing one, 1 the outgoing one slides off and uncovers the incoming.
float4 PsSlide(FullScreenVertex input) : SV_TARGET
{
    float2 move = Movement();

    if (Flag == 1)
    {
        float2 fromOutgoing = input.Uv - move * Progress;
        return OnFrame(fromOutgoing) ? SampleOutgoing(fromOutgoing) : LoadIncoming(input.Position.xy);
    }

    float2 fromIncoming = input.Uv - move * (Progress - 1.0);
    return OnFrame(fromIncoming) ? SampleIncoming(fromIncoming) : LoadOutgoing(input.Position.xy);
}

// Values: x how far it zooms, 1 to 10; yz the centre as a texture coordinate. The outgoing
// picture zooms in towards the centre and the incoming one arrives from as close and settles,
// crossing over in the middle.
float4 PsZoom(FullScreenVertex input) : SV_TARGET
{
    float amount = max(Values.x, 1.0);
    float2 centre = Values.yz;
    float eased = smoothstep(0.0, 1.0, Progress);

    float outScale = lerp(1.0, amount, eased);
    float inScale = lerp(amount, 1.0, eased);
    float4 a = Outgoing.SampleLevel(LinearClamp, centre + (input.Uv - centre) / outScale, 0);
    float4 b = Incoming.SampleLevel(LinearClamp, centre + (input.Uv - centre) / inScale, 0);

    return MixPerceptual(a, b, smoothstep(0.35, 0.65, Progress));
}

// Values.x the widest blur, in texels, reached in the middle. Both pictures blur as they cross.
float4 PsBlurDissolve(FullScreenVertex input) : SV_TARGET
{
    float radius = Values.x * sin(Pi * Progress);
    float4 a = 0.0;
    float4 b = 0.0;
    const int Taps = 32;

    // A golden angle spiral: even cover of the disc from few taps, with no grid to see.
    [loop]
    for (int index = 0; index < Taps; index++)
    {
        float r = sqrt((index + 0.5) / Taps) * radius;
        float theta = index * 2.39996323;
        float2 uv = input.Uv + float2(cos(theta), sin(theta)) * r * TexelSize;
        a += Outgoing.SampleLevel(LinearClamp, uv, 0);
        b += Incoming.SampleLevel(LinearClamp, uv, 0);
    }

    return MixPerceptual(a / Taps, b / Taps, Progress);
}

// Values: x intensity 0 to 1, y block size in texels, z the seed chosen. Rows of blocks tear
// sideways, the channels split, and block by block the incoming picture takes over, strongest in
// the middle. The randomness comes from the seed and the progress, so a frame is always the same.
float4 PsGlitch(FullScreenVertex input) : SV_TARGET
{
    float strength = saturate(Values.x) * sin(Pi * Progress);
    float block = max(Values.y, 2.0);
    uint chosen = PcgHash((uint)Seed ^ PcgHash((uint)max(Values.z, 0.0)));

    // The tears jump thirty times through the transition; which blocks have turned over does not.
    float jump = (float)(PcgHash(chosen + (uint)floor(Progress * 30.0)) & 0xFFFFFF);
    float still = (float)(chosen & 0xFFFFFF);

    float band = floor(input.Position.y / (block * 0.5));
    float tear = Random(float2(0.0, band), jump) > 1.0 - strength * 0.7
        ? (Random(float2(1.0, band), jump) - 0.5) * strength * 0.3
        : 0.0;

    float2 cell = floor(input.Position.xy / block);
    bool arrived = Random(cell, still) < smoothstep(0.2, 0.8, Progress);

    float2 uv = input.Uv + float2(tear, 0.0);
    float2 split = float2(strength * 0.012, 0.0);
    // Clamped at the frame edge, so a row torn past it smears rather than leaving a hole.
    float4 left = arrived ? Incoming.SampleLevel(LinearClamp, uv + split, 0) : Outgoing.SampleLevel(LinearClamp, uv + split, 0);
    float4 middle = arrived ? Incoming.SampleLevel(LinearClamp, uv, 0) : Outgoing.SampleLevel(LinearClamp, uv, 0);
    float4 right = arrived ? Incoming.SampleLevel(LinearClamp, uv - split, 0) : Outgoing.SampleLevel(LinearClamp, uv - split, 0);

    // Each channel from its own offset, kept inside the alpha so the colour stays premultiplied.
    float alpha = max(middle.a, max(left.a, right.a));
    return float4(min(float3(left.r, middle.g, right.b), alpha), alpha);
}
