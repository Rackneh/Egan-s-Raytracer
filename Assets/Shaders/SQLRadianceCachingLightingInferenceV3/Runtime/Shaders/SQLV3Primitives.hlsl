bool SQL3IntersectPrimitives(
    float3 rayOrigin,
    float3 rayDir,
    float maxDist,
    bool shadow,
    out int outHitIndex,
    out float outHitDist,
    out float3 outHitPoint,
    out float3 outHitNormal)
{
    outHitIndex = -1;
    outHitDist = maxDist;
    outHitPoint = rayOrigin;
    outHitNormal = float3(0, 1, 0);

    int count = min(_SQL3EntityCount, 32);
    [loop]
    for (int i = 0; i < count; i++)
    {
        if (shadow && _SQL3SurfaceFlags[i].y == 0) continue;
        float4 d0 = _SQL3Entities[i * 6 + 0]; // pos.xyz, shapeType
        float4 d1 = _SQL3Entities[i * 6 + 1]; // extents.xyz, patternId
        float4 d4 = _SQL3Entities[i * 6 + 4]; // rotRight.xyz
        float4 d5 = _SQL3Entities[i * 6 + 5]; // rotUp.xyz

        float3 center = d0.xyz;
        int shape = (int)d0.w;
        float3 ext = max(d1.xyz, 1e-4);

        // Reconstruct local orthonormal basis
        float3 rRight = length(d4.xyz) > 0.01 ? normalize(d4.xyz) : float3(1, 0, 0);
        float3 rUp = length(d5.xyz) > 0.01 ? normalize(d5.xyz) : float3(0, 1, 0);
        float3 rFwd = normalize(cross(rRight, rUp));

        // Transform ray from world to entity's local coordinate frame
        float3 delta = rayOrigin - center;
        float3 oLoc = float3(dot(delta, rRight), dot(delta, rUp), dot(delta, rFwd));
        float3 dLoc = float3(dot(rayDir, rRight), dot(rayDir, rUp), dot(rayDir, rFwd));

        // Shape 0: Sphere (Rotation invariant)
        if (shape == 0)
        {
            float radius = ext.x;
            float b = dot(oLoc, dLoc);
            float c = dot(oLoc, oLoc) - radius * radius;
            float disc = b * b - c;
            if (disc >= 0.0)
            {
                float sqrtd = sqrt(disc);
                float t = -b - sqrtd;
                if (t < 0.02) t = -b + sqrtd;
                if (t > 0.02 && t < outHitDist)
                {
                    outHitDist = t;
                    outHitIndex = i;
                    outHitPoint = rayOrigin + rayDir * t;
                    outHitNormal = normalize(outHitPoint - center);
                }
            }
        }
        // Shape 1: Oriented Box / Cube / Thin Mirror Wall
        else if (shape == 1)
        {
            float3 safeDir = abs(dLoc) > 1e-5 ? dLoc : (dLoc >= 0 ? 1e-5 : -1e-5);
            float3 invD = 1.0 / safeDir;
            float3 t0 = (-ext - oLoc) * invD;
            float3 t1 = (ext - oLoc) * invD;
            float3 tmin = min(t0, t1);
            float3 tmax = max(t0, t1);

            float enter = max(max(tmin.x, tmin.y), tmin.z);
            float exit = min(min(tmax.x, tmax.y), tmax.z);

            if (exit >= max(enter, 0.02) && enter < outHitDist && enter > 0.02)
            {
                outHitDist = enter;
                outHitIndex = i;
                outHitPoint = rayOrigin + rayDir * enter;
                float3 pLoc = oLoc + dLoc * enter;
                float3 normScale = pLoc / ext;
                float3 absN = abs(normScale);
                float3 nLoc = float3(0, 0, 0);
                if (absN.x > absN.y && absN.x > absN.z) nLoc = float3(sign(pLoc.x), 0, 0);
                else if (absN.y > absN.z) nLoc = float3(0, sign(pLoc.y), 0);
                else nLoc = float3(0, 0, sign(pLoc.z));
                outHitNormal = normalize(rRight * nLoc.x + rUp * nLoc.y + rFwd * nLoc.z);
            }
        }
        // Shape 2: Oriented Plane / Quad / Wall
        else if (shape == 2)
        {
            if (abs(dLoc.y) > 1e-5)
            {
                float t = -oLoc.y / dLoc.y;
                if (t > 0.02 && t < outHitDist)
                {
                    float3 pLoc = oLoc + dLoc * t;
                    if (abs(pLoc.x) <= ext.x && abs(pLoc.z) <= ext.z)
                    {
                        outHitDist = t;
                        outHitIndex = i;
                        outHitPoint = rayOrigin + rayDir * t;
                        float signY = sign(-dLoc.y);
                        outHitNormal = rUp * signY;
                    }
                }
            }
        }
        // Shape 3: Capsule (along local Y axis)
        else if (shape == 3)
        {
            float radius = ext.x;
            float halfH = max(ext.y - radius, 0.01);
            float3 pa = float3(0, -halfH, 0);
            float3 pb = float3(0, halfH, 0);
            float3 ba = pb - pa;
            float3 oa = oLoc - pa;
            float baba = dot(ba, ba);
            float bard = dot(ba, dLoc);
            float baoa = dot(ba, oa);
            float rdoa = dot(dLoc, oa);
            float oaoa = dot(oa, oa);

            float a = baba - bard * bard;
            float b = baba * rdoa - baoa * bard;
            float c = baba * oaoa - baoa * baoa - radius * radius * baba;
            float h = b * b - a * c;
            if (h >= 0.0)
            {
                float t = (-b - sqrt(h)) / max(a, 1e-5);
                float y = baoa + t * bard;
                if (y > 0.0 && y < baba)
                {
                    if (t > 0.02 && t < outHitDist)
                    {
                        outHitDist = t;
                        outHitIndex = i;
                        outHitPoint = rayOrigin + rayDir * t;
                        float3 pLoc = oLoc + dLoc * t;
                        float3 nLoc = normalize((pLoc - pa) - ba * (y / baba));
                        outHitNormal = normalize(rRight * nLoc.x + rUp * nLoc.y + rFwd * nLoc.z);
                    }
                }
                else
                {
                    float3 ocCap = oLoc - (y <= 0.0 ? pa : pb);
                    float bCap = dot(ocCap, dLoc);
                    float cCap = dot(ocCap, ocCap) - radius * radius;
                    float discCap = bCap * bCap - cCap;
                    if (discCap >= 0.0)
                    {
                        float tCap = -bCap - sqrt(discCap);
                        if (tCap > 0.02 && tCap < outHitDist)
                        {
                            outHitDist = tCap;
                            outHitIndex = i;
                            outHitPoint = rayOrigin + rayDir * tCap;
                            float3 pLoc = oLoc + dLoc * tCap;
                            float3 nLoc = normalize(pLoc - (y <= 0.0 ? pa : pb));
                            outHitNormal = normalize(rRight * nLoc.x + rUp * nLoc.y + rFwd * nLoc.z);
                        }
                    }
                }
            }
        }
    }

    if (outHitIndex >= 0 && dot(outHitNormal, rayDir) > 0.0)
    {
        outHitNormal = -outHitNormal;
    }

    return outHitIndex >= 0;
}


