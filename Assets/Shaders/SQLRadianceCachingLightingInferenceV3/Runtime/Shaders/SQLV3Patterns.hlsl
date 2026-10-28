float3 EvaluateProceduralTexture(int patternId, float2 uv, float3 worldPos, float3 baseColor, float3 normal)
{
    // Surface-aligned 2D coordinates
    float3 absN = abs(normal);
    float2 pCoord = absN.y > 0.5 ? worldPos.xz : (absN.x > 0.5 ? worldPos.zy : worldPos.xy);

    if (patternId == 1) // Luxury Polished Checkerboard Tiles (for floor / pedestals / walls)
    {
        float2 c = floor(pCoord * 1.5);
        float check = fmod(abs(c.x + c.y), 2.0);
        float3 darkTone = baseColor * 0.2;
        float3 lightTone = baseColor * 1.35;
        // Subtle mortar border line
        float2 gridDist = abs(frac(pCoord * 1.5) - 0.5);
        float border = step(0.47, max(gridDist.x, gridDist.y));
        float3 tileCol = check > 0.5 ? lightTone : darkTone;
        return lerp(tileCol, float3(0.05, 0.05, 0.06), border);
    }
    else if (patternId == 2) // Jade / Marble Swirled Mineral Veins
    {
        float noise = sin(pCoord.x * 3.5 + sin(pCoord.y * 5.0) * 2.0 + cos(pCoord.x * 2.0));
        float vein = saturate(pow(abs(noise), 3.0));
        float3 veinColor = baseColor * 1.5 + float3(0.12, 0.15, 0.1);
        float3 baseTone = baseColor * 0.65;
        return lerp(baseTone, veinColor, vein);
    }
    else if (patternId == 3) // Gold Brushed Metal / Cyber Grid
    {
        float gx = saturate(cos(pCoord.x * 16.0) * 8.0);
        float gz = saturate(cos(pCoord.y * 16.0) * 8.0);
        float wire = max(gx, gz);
        return lerp(baseColor * 0.85, baseColor * 1.3 + float3(0.2, 0.16, 0.05), wire * 0.6);
    }
    else if (patternId == 4) // Terracotta Staggered Brick Grid
    {
        float2 b = float2(pCoord.x * 2.5, pCoord.y * 5.0);
        b.x += floor(b.y) * 0.5;
        float2 f = abs(frac(b) - 0.5);
        float mortar = step(0.42, max(f.x, f.y));
        float variation = frac(sin(floor(b.y) * 17.0 + floor(b.x) * 31.0) * 43758.5453);
        float3 brick = baseColor * (0.8 + 0.35 * variation);
        return lerp(brick, float3(0.25, 0.23, 0.22), mortar);
    }
    else if (patternId == 5) // Hexagonal Honeycomb Matrix
    {
        float2 p = pCoord * 2.5;
        float2 h = abs(frac(p) - 0.5);
        float d = max(h.x * 1.5 + h.y, h.y * 2.0);
        float edge = smoothstep(0.4, 0.48, d);
        return lerp(baseColor, baseColor * 0.3, edge);
    }
    return baseColor;
}

float3 EvaluateProceduralTexture(int patternId, float2 uv, float3 worldPos, float3 baseColor)
{
    return EvaluateProceduralTexture(patternId, uv, worldPos, baseColor, float3(0, 1, 0));
}

