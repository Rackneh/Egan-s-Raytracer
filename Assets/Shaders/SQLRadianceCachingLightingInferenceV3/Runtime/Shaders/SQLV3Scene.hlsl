#ifndef SQL_LIVE_V3_SCENE
#define SQL_LIVE_V3_SCENE
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
float4 _SQL3Entities[192], _SQL3Metadata[32], _SQL3SurfaceFlags[32];
int _SQL3EntityCount,_SQL3HasMeshes;
struct SQL3Vertex { float4 position, normal, uv; };
struct SQL3Node { float4 minimum, maximum; };
struct SQL3Lighting { float4 direct, indirect, area, dominantDirection, dominantRadiance; };
StructuredBuffer<SQL3Vertex> _SQL3Vertices;
StructuredBuffer<SQL3Node> _SQL3Nodes;
StructuredBuffer<float4> _SQL3Triangles;
StructuredBuffer<SQL3Lighting> _SQL3Cache;
Texture2DArray<float4> _SQL3LightingAtlas;
SamplerState sampler_SQL3LightingAtlas;
Texture2DArray<float4> _SQL3AlbedoMaps, _SQL3EmissionMaps;
SamplerState sampler_SQL3AlbedoMaps;
float4 _SQL3BaseST[32], _SQL3EmissionST[32];
float4 _SQL3Sky;
float4 _SQL3Bounce;
float _SQL3Bias;
float _SQL3GIEnabled;
#include "SQLV3Patterns.hlsl"
#include "SQLV3Primitives.hlsl"
struct SQL3Hit { int entity, triangleIndex; float distance; float3 position, normal; float2 uv, bary; };

bool SQL3Bounds(float3 o,float3 d,float3 lo,float3 hi,float limit)
{
    float3 inv = rcp(abs(d)>1e-7 ? d : (d>=0 ? 1e-7 : -1e-7));
    float3 a=(lo-o)*inv,b=(hi-o)*inv;
    float3 mn=min(a,b),mx=max(a,b);
    return max(max(mn.x,mn.y),max(mn.z,0)) <= min(min(mx.x,mx.y),min(mx.z,limit));
}
float3 SQL3Local(int entity,float3 v)
{
    float3 r=normalize(_SQL3Entities[entity*6+4].xyz), u=normalize(_SQL3Entities[entity*6+5].xyz);
    return float3(dot(v,r),dot(v,u),dot(v,normalize(cross(r,u))));
}
float2 SQL3Patch(int entity,float3 p,float3 normal,out int face)
{
    float3 n=SQL3Local(entity,normal), a=abs(n);
    face=a.x>a.y && a.x>a.z ? (n.x>0 ? 0:1) : a.y>a.z ? (n.y>0 ? 2:3) : (n.z>0 ? 4:5);
    float3 q=SQL3Local(entity,p-_SQL3Entities[entity*6].xyz);
    float2 uv=0;
    if(_SQL3Entities[entity*6].w==3)
    {
        face=0;
        uv=float2(atan2(q.z,q.x)/TWO_PI+.5,saturate(q.y/(2*max(_SQL3Entities[entity*6+1].y,1e-5))+.5));
    }
    else
    {
        if((int)_SQL3Entities[entity*6].w==0) q/=max(max(abs(q.x),abs(q.y)),max(abs(q.z),1e-5));
        else q/=max(_SQL3Entities[entity*6+1].xyz,1e-5);
        uv=saturate((face<2 ? q.zy : face<4 ? q.xz : q.xy)*.5+.5);
    }
    return uv;
}
float2 SQL3PrimitiveUV(int entity,float3 p,float3 normal)
{
    int face; float2 uv=SQL3Patch(entity,p,normal,face), coord=uv*8-.5;
    // Extrapolate the affine face UV to the edge instead of clamping texture coordinates.
    int2 lo=clamp((int2)floor(coord),0,6); float2 f=coord-lo;
    int start=(int)_SQL3Metadata[entity].x+face*64+lo.y*8+lo.x;
    float2 a=_SQL3Vertices[start].uv.xy,b=_SQL3Vertices[start+1].uv.xy;
    float2 c=_SQL3Vertices[start+8].uv.xy,d=_SQL3Vertices[start+9].uv.xy;
    if(_SQL3Entities[entity*6].w==0)
    {
        b.x-=round(b.x-a.x); c.x-=round(c.x-a.x); d.x-=round(d.x-a.x);
    }
    return lerp(lerp(a,b,f.x),lerp(c,d,f.x),f.y);
}
bool SQL3Trace(float3 o,float3 d,float limit,bool shadow,out SQL3Hit hit)
{
    hit=(SQL3Hit)0; hit.entity=-1; hit.triangleIndex=-1; hit.distance=limit;
    SQL3IntersectPrimitives(o,d,limit,shadow,hit.entity,hit.distance,hit.position,hit.normal);
    [loop] for(int entity=0;entity<(_SQL3HasMeshes!=0 ? _SQL3EntityCount:0);entity++)
    {
        if(_SQL3Entities[entity*6].w!=4 || _SQL3SurfaceFlags[entity].x==0 || (shadow && _SQL3SurfaceFlags[entity].y==0)) continue;
        int node=(int)_SQL3Metadata[entity].y, end=(int)_SQL3Metadata[entity].z;
        if(node<0) continue;
        [loop] while(node<end)
        {
            SQL3Node n=_SQL3Nodes[node];
            bool inside=SQL3Bounds(o,d,n.minimum.xyz,n.maximum.xyz,hit.distance);
            if(n.maximum.w>0) { node=inside ? node+1 : (int)n.maximum.w; continue; }
            if(inside)
            {
                int first=(int)n.minimum.w,count=-(int)n.maximum.w;
                [loop] for(int t=0;t<count;t++)
                {
                    float4 indices=_SQL3Triangles[first+t];
                    SQL3Vertex va=_SQL3Vertices[(int)indices.x], vb=_SQL3Vertices[(int)indices.y], vc=_SQL3Vertices[(int)indices.z];
                    float3 e1=vb.position.xyz-va.position.xyz,e2=vc.position.xyz-va.position.xyz;
                    float3 p=cross(d,e2); float det=dot(e1,p);
                    if(abs(det)<1e-9) continue;
                    float inv=rcp(det); float3 s=o-va.position.xyz;
                    float u=dot(s,p)*inv; float3 q=cross(s,e1); float v=dot(d,q)*inv, dist=dot(e2,q)*inv;
                    if(u<0 || v<0 || u+v>1 || dist<=_SQL3Bias*.5 || dist>=hit.distance) continue;
                    hit.entity=entity; hit.triangleIndex=first+t; hit.distance=dist; hit.bary=float2(u,v);
                    hit.position=o+d*dist;
                    hit.normal=normalize(va.normal.xyz*(1-u-v)+vb.normal.xyz*u+vc.normal.xyz*v);
                    hit.uv=va.uv.xy*(1-u-v)+vb.uv.xy*u+vc.uv.xy*v;
                    if(dot(hit.normal,d)>0) hit.normal=-hit.normal;
                }
            }
            node++;
        }
    }
    if(hit.entity>=0 && hit.triangleIndex<0 && !shadow && _SQL3SurfaceFlags[hit.entity].w>0)
    {
        hit.uv=SQL3PrimitiveUV(hit.entity,hit.position,hit.normal);
    }
    return hit.entity>=0;
}
SQL3Lighting SQL3MixLighting(SQL3Lighting a,SQL3Lighting b,float t)
{
    SQL3Lighting c=(SQL3Lighting)0; c.direct=lerp(a.direct,b.direct,t); c.indirect=lerp(a.indirect,b.indirect,t); c.area=lerp(a.area,b.area,t);
    c.dominantDirection=lerp(a.dominantDirection,b.dominantDirection,t); c.dominantRadiance=lerp(a.dominantRadiance,b.dominantRadiance,t); return c;
}
SQL3Lighting SQL3SampleLighting(SQL3Hit hit)
{
    SQL3Lighting result=(SQL3Lighting)0;
    if(hit.triangleIndex>=0)
    {
        float3 ids=_SQL3Triangles[hit.triangleIndex].xyz; float u=hit.bary.x,v=hit.bary.y;
        SQL3Lighting a=_SQL3Cache[(int)ids.x],b=_SQL3Cache[(int)ids.y],c=_SQL3Cache[(int)ids.z];
        SQL3Lighting r=(SQL3Lighting)0;
        r.direct=a.direct*(1-u-v)+b.direct*u+c.direct*v;
        r.indirect=a.indirect*(1-u-v)+b.indirect*u+c.indirect*v;
        r.area=a.area*(1-u-v)+b.area*u+c.area*v;
        r.dominantDirection=a.dominantDirection*(1-u-v)+b.dominantDirection*u+c.dominantDirection*v;
        r.dominantRadiance=a.dominantRadiance*(1-u-v)+b.dominantRadiance*u+c.dominantRadiance*v; result=r;
    }
    else
    {
    int face; float2 uv=SQL3Patch(hit.entity,hit.position,hit.normal,face);
    #if !defined(SQL3_BUFFER_LIGHTING)
    float2 atlasUV=float2((face*8+clamp(uv.x*8,.5,7.5))/48.0,(hit.entity*8+clamp(uv.y*8,.5,7.5))/256.0);
    SQL3Lighting filtered=(SQL3Lighting)0;
    filtered.direct=_SQL3LightingAtlas.SampleLevel(sampler_SQL3LightingAtlas,float3(atlasUV,0),0);
    filtered.indirect=_SQL3LightingAtlas.SampleLevel(sampler_SQL3LightingAtlas,float3(atlasUV,1),0);
    filtered.area=_SQL3LightingAtlas.SampleLevel(sampler_SQL3LightingAtlas,float3(atlasUV,2),0);
    filtered.dominantDirection=_SQL3LightingAtlas.SampleLevel(sampler_SQL3LightingAtlas,float3(atlasUV,3),0);
    filtered.dominantRadiance=_SQL3LightingAtlas.SampleLevel(sampler_SQL3LightingAtlas,float3(atlasUV,4),0);
    result=filtered;
    #else
    float2 coord=clamp(uv*8-.5,0,7); int2 lo=(int2)floor(coord),hi=min(lo+1,7); float2 f=frac(coord);
    int start=(int)_SQL3Metadata[hit.entity].x+face*64;
    result=SQL3MixLighting(SQL3MixLighting(_SQL3Cache[start+lo.y*8+lo.x],_SQL3Cache[start+lo.y*8+hi.x],f.x),
        SQL3MixLighting(_SQL3Cache[start+hi.y*8+lo.x],_SQL3Cache[start+hi.y*8+hi.x],f.x),f.y);
    #endif
    }
    return result;
}
void SQL3Material(SQL3Hit h,out float3 albedo,out float3 emission)
{
    float4 base=_SQL3Entities[h.entity*6+2], params=_SQL3Entities[h.entity*6+3];
    float4 st=_SQL3BaseST[h.entity],est=_SQL3EmissionST[h.entity];
    int maps=(int)_SQL3SurfaceFlags[h.entity].w;
    albedo=base.rgb;
    if((maps&1)!=0)albedo*=_SQL3AlbedoMaps.SampleLevel(sampler_SQL3AlbedoMaps,float3(frac(h.uv*st.xy+st.zw),h.entity),0).rgb;
    int pattern=(int)_SQL3Entities[h.entity*6+1].w;
    if(pattern>0) albedo=EvaluateProceduralTexture(pattern,h.uv,h.position,base.rgb,h.normal);
    emission=params.yzw;
    if((maps&2)!=0)emission*=_SQL3EmissionMaps.SampleLevel(sampler_SQL3AlbedoMaps,float3(frac(h.uv*est.xy+est.zw),h.entity),0).rgb;
}
float3 SampleSQLReflection(float3 direction,float roughness) { return _SQL3Sky.rgb; }
#endif

