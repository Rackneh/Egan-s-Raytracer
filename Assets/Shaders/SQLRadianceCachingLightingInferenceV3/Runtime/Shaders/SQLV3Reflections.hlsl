// View-dependent specular from the strongest cached, shadowed light. This keeps
// metals lit without a per-light or shadow-ray loop inside every mirror bounce.
float3 SQL3CachedSpecular(SQL3Lighting lighting,float3 normal,float3 view,float roughness,float3 f0)
{
    float3 dir=lighting.dominantDirection.xyz;
    if(dot(dir,dir)<1e-6 || dot(lighting.dominantRadiance.rgb,1)<1e-6)return 0;
    dir=normalize(dir); float3 h=normalize(view+dir);
    float nl=saturate(dot(normal,dir)),nv=max(saturate(dot(normal,view)),1e-4),nh=saturate(dot(normal,h)),vh=saturate(dot(view,h));
    float a=max(.04,roughness*roughness),a2=a*a,d=(nh*a2-nh)*nh+1;
    float distribution=a2/max(PI*d*d,1e-7);
    float visibility=.5/max(nl*sqrt(nv*(nv-a2*nv)+a2)+nv*sqrt(nl*(nl-a2*nl)+a2),1e-7);
    return lighting.dominantRadiance.rgb*(distribution*visibility*nl)*(f0+(1-f0)*pow(1-vh,5));
}
float3 EvaluateSQLInfinityMirrorReflection(
    float3 positionWS,
    float3 normalWS,
    float3 viewDirWS,
    float roughness,
    float3 f0,
    float3 lightDir,
    float3 lightColor,
    int maxBouncesAllowed,
    bool isInfiniteClosedForm)
{
    float3 rayOrigin = positionWS + normalWS * 0.015;
    float3 rayDir = reflect(-viewDirWS, normalWS);

    float3 accumulatedRadiance = float3(0, 0, 0);
    float3 throughput = float3(1, 1, 1);
    float feedbackGain = clamp(_SQL3Bounce.y, 0.0, 0.96);
    float3 lastSurfaceDiffuse = float3(0.01, 0.01, 0.01);

    // Limit ray intersection loop steps for GPU performance (each step calculates an actual mirror bounce)
    int loopLimit = isInfiniteClosedForm ? 32 : clamp(maxBouncesAllowed, 1, 32);

    bool exhausted = true;
    [loop]
    for (int bounce = 0; bounce < loopLimit; bounce++)
    {
        int hitIdx = -1;
        float hitDist = 100.0;
        float3 hitPoint = float3(0, 0, 0);
        float3 hitNormal = float3(0, 1, 0);

        SQL3Hit h;
        bool hit = SQL3Trace(rayOrigin, rayDir, 100.0, false, h);
        hitIdx = h.entity; hitDist = h.distance; hitPoint = h.position; hitNormal = h.normal;

        if (!hit)
        {
            float3 env = SampleSQLReflection(rayDir, roughness);
            accumulatedRadiance += throughput * env;
            exhausted = false;
            break;
        }

        float4 d0 = _SQL3Entities[hitIdx * 6 + 0];
        float4 d1 = _SQL3Entities[hitIdx * 6 + 1];
        float4 d2 = _SQL3Entities[hitIdx * 6 + 2];
        float4 d3 = _SQL3Entities[hitIdx * 6 + 3];

        int patternId = (int)d1.w;
        float3 baseCol = d2.rgb;
        float rough2 = d2.w;
        float met2 = d3.x;
        float3 emissive2 = d3.yzw;

        float3 hitAlbedo, emission; SQL3Material(h, hitAlbedo, emission);

        float3 f02 = lerp(float3(0.04, 0.04, 0.04), saturate(hitAlbedo), met2);
        float vDotN2 = saturate(dot(-rayDir, hitNormal));
        float fresnel2 = f02.r + (1.0 - f02.r) * pow(1.0 - vDotN2, 5.0);
        float smoothness2 = saturate(1.0 - rough2);
        float reflLobe2 = max(fresnel2, smoothness2 * smoothness2 * 0.85);
        float3 reflWeight2 = lerp(f02, float3(1, 1, 1), reflLobe2);

        SQL3Lighting lighting = SQL3SampleLighting(h);
        float3 directSpecular=SQL3CachedSpecular(lighting,hitNormal,-rayDir,rough2,f02);
        float3 surfaceColor = hitAlbedo * (1.0 - met2) * 0.96 *
            (lighting.direct.rgb + lighting.indirect.rgb * _SQL3GIEnabled);
        lastSurfaceDiffuse = surfaceColor * saturate(1.0 - reflWeight2 * smoothness2);
        accumulatedRadiance += throughput * (directSpecular + emission);

        // If hit object is diffuse (rough), add surface color and stop bouncing
        if (smoothness2 < 0.4)
        {
            accumulatedRadiance += throughput * surfaceColor;
            exhausted = false;
            break;
        }

        // Energy conservation: add the diffuse portion of the surface
        accumulatedRadiance += throughput * lastSurfaceDiffuse;

        // Update throughput for next reflection bounce
        throughput *= reflWeight2 * feedbackGain;

        if (max(throughput.r, max(throughput.g, throughput.b)) < 0.003)
        {
            exhausted = false;
            break; // Converged
        }

        // Advance ray for the next bounce
        rayOrigin = hitPoint + hitNormal * 0.015;
        rayDir = reflect(rayDir, hitNormal);
    }

    // Closed-form Neumann transfer for infinite bounces / high bounces (> 32)
    if (exhausted && (isInfiniteClosedForm || maxBouncesAllowed > 32))
    {
        float maxT = max(throughput.r, max(throughput.g, throughput.b));
        if (maxT > 0.003)
        {
            // Neumann tail factor: K / (1 - K) for remaining diffuse bounces between mirrors
            float k = feedbackGain;
            float tail = k / max(1.0 - k, 0.04);
            accumulatedRadiance += throughput * tail * lastSurfaceDiffuse;
            accumulatedRadiance += throughput * SampleSQLReflection(rayDir, roughness);
        }
    }

    // Apply primary surface reflection weight and roughness blur
    float blurFactor = saturate(roughness * 2.2);
    float3 blurredRadiance = SampleSQLReflection(reflect(-viewDirWS, normalWS), roughness);
    float3 finalReflected = lerp(accumulatedRadiance, blurredRadiance, blurFactor);

    float vDotN = saturate(dot(viewDirWS, normalWS));
    float fresnelFactor = f0.r + (1.0 - f0.r) * pow(1.0 - vDotN, 5.0);
    float smoothness = saturate(1.0 - roughness);
    float reflLobe = max(fresnelFactor, smoothness * smoothness * 0.85);
    float3 reflectionWeight = lerp(f0, float3(1.0, 1.0, 1.0), reflLobe);

    return finalReflected * reflectionWeight;
}

