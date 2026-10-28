Shader "InferenceLighting/SQLRadianceLiveV3Lit"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo",2D)="white"{}
        [MainColor] _BaseColor("Color",Color)=(1,1,1,1)
        _Smoothness("Smoothness",Range(0,1))=.5
        _Metallic("Metallic",Range(0,1))=0
        [Normal] _BumpMap("Normal map",2D)="bump"{}
        _BumpScale("Normal strength",Float)=1
        [HDR] _EmissionColor("Emission",Color)=(0,0,0,1)
        _EmissionMap("Emission map",2D)="white"{}
        _TexturePattern("Procedural pattern",Int)=0
        [HideInInspector] _SQL3EntityIndex("Surface slot",Float)=0
        [HideInInspector] _SQL3VertexOffset("Mesh vertex offset",Float)=0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" "DisableBatching"="True" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST,_BaseColor,_EmissionMap_ST,_EmissionColor;
            float _Smoothness,_Metallic,_BumpScale,_SQL3EntityIndex,_SQL3VertexOffset;
            int _TexturePattern;
        CBUFFER_END
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_EmissionMap); SAMPLER(sampler_EmissionMap);
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags {"LightMode"="UniversalForwardOnly"}
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "SQLV3Scene.hlsl"
            #include "SQLV3Reflections.hlsl"
            int _SQL3Enabled,_SQL3Debug;
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float4 tangentOS:TANGENT; float2 uv:TEXCOORD0; uint id:SV_VertexID; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; float4 tangentWS:TEXCOORD2; float2 uv:TEXCOORD3; float3 gi:TEXCOORD4; float3 area:TEXCOORD5; float fog:TEXCOORD6; };
            Varyings Vert(Attributes i)
            {
                Varyings o=(Varyings)0;
                VertexPositionInputs p=GetVertexPositionInputs(i.positionOS.xyz);
                o.positionCS=p.positionCS; o.positionWS=p.positionWS;
                o.normalWS=TransformObjectToWorldNormal(i.normalOS);
                o.tangentWS=float4(TransformObjectToWorldDir(i.tangentOS.xyz),i.tangentOS.w*GetOddNegativeScale());
                o.uv=i.uv; o.fog=ComputeFogFactor(p.positionCS.z);
                int entity=(int)_SQL3EntityIndex-1;
                if(_SQL3Enabled!=0 && entity>=0 && entity<_SQL3EntityCount && _SQL3Entities[entity*6].w==4)
                {
                    SQL3Lighting c=_SQL3Cache[(int)_SQL3VertexOffset+i.id]; o.gi=c.indirect.rgb; o.area=c.area.rgb;
                }
                return o;
            }
            float3 Direct(BRDFData brdf,Light light,float3 normal,float3 view)
            {
                #ifdef _LIGHT_LAYERS
                if(!IsMatchingLightLayer(light.layerMask,GetMeshRenderingLayer())) return 0;
                #endif
                return LightingPhysicallyBased(brdf,light,normal,view);
            }
            float4 Frag(Varyings i):SV_Target
            {
                float3 geometric=normalize(i.normalWS);
                float3 tangent=normalize(i.tangentWS.xyz),bitangent=cross(geometric,tangent)*i.tangentWS.w;
                float3 normalTS=UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap,sampler_BumpMap,TRANSFORM_TEX(i.uv,_BaseMap)),_BumpScale);
                float3 normal=normalize(tangent*normalTS.x+bitangent*normalTS.y+geometric*normalTS.z);
                float3 view=GetWorldSpaceNormalizeViewDir(i.positionWS);
                float3 albedo=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,TRANSFORM_TEX(i.uv,_BaseMap)).rgb*_BaseColor.rgb;
                if(_TexturePattern>0) albedo=EvaluateProceduralTexture(_TexturePattern,i.uv,i.positionWS,_BaseColor.rgb,geometric);
                float3 emission=SAMPLE_TEXTURE2D(_EmissionMap,sampler_EmissionMap,TRANSFORM_TEX(i.uv,_EmissionMap)).rgb*_EmissionColor.rgb;
                BRDFData brdf; half alpha=1;
                InitializeBRDFData(albedo,_Metallic,half3(0,0,0),_Smoothness,alpha,brdf);
                float4 shadowCoord=TransformWorldToShadowCoord(i.positionWS);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                shadowCoord=ComputeScreenPos(TransformWorldToHClip(i.positionWS));
                #endif
                Light main=GetMainLight(shadowCoord,i.positionWS,half4(1,1,1,1));
                float3 direct=Direct(brdf,main,normal,view);
                InputData inputData=(InputData)0; inputData.positionWS=i.positionWS;
                inputData.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(i.positionCS);
                #if USE_FORWARD_PLUS
                UNITY_LOOP for(uint lightIndex=0;lightIndex<min(URP_FP_DIRECTIONAL_LIGHTS_COUNT,MAX_VISIBLE_LIGHTS);lightIndex++)
                { Light l=GetAdditionalLight(lightIndex,i.positionWS,half4(1,1,1,1)); direct+=Direct(brdf,l,normal,view); }
                #endif
                #if defined(_ADDITIONAL_LIGHTS)
                uint count=GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(count)
                    Light l=GetAdditionalLight(lightIndex,i.positionWS,half4(1,1,1,1)); direct+=Direct(brdf,l,normal,view);
                LIGHT_LOOP_END
                #endif
                int entity=(int)_SQL3EntityIndex-1;
                float3 indirect=0,area=0,reflection=0;
                if(_SQL3Enabled!=0 && entity>=0 && entity<_SQL3EntityCount)
                {
                    if(_SQL3Entities[entity*6].w==4) { indirect=i.gi; area=i.area; }
                    else
                    {
                        SQL3Hit h=(SQL3Hit)0; h.entity=entity; h.triangleIndex=-1; h.position=i.positionWS; h.normal=geometric;
                        SQL3Lighting c=SQL3SampleLighting(h); indirect=c.indirect.rgb; area=c.area.rgb;
                    }
                    if(_SQL3GIEnabled==0) indirect=0;
                    float roughness=max(.04,1-_Smoothness);
                    if(_SQL3Bounce.z>0 && roughness<.455)
                        reflection=EvaluateSQLInfinityMirrorReflection(i.positionWS,geometric,view,roughness,brdf.specular,
                            main.direction,main.color,(int)_SQL3Bounce.x,_SQL3Bounce.w==0);
                }
                indirect*=albedo*(1-_Metallic)*.96;
                direct+=area*albedo*(1-_Metallic)*.96;
                float3 color=direct+indirect+reflection+emission;
                if(_SQL3Debug==1) color=indirect;
                if(_SQL3Debug==2) color=reflection;
                if(_SQL3Debug==3) color=direct;
                return float4(MixFog(color,i.fog),1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags {"LightMode"="ShadowCaster"}
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection,_LightPosition;
            struct A {float4 position:POSITION;float3 normal:NORMAL;};
            float4 ShadowVert(A i):SV_POSITION
            {
                float3 p=TransformObjectToWorld(i.position.xyz),n=TransformObjectToWorldNormal(i.normal);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 d=normalize(_LightPosition-p);
                #else
                float3 d=_LightDirection;
                #endif
                float4 clip=TransformWorldToHClip(ApplyShadowBias(p,n,d));
                #if UNITY_REVERSED_Z
                clip.z=min(clip.z,clip.w*UNITY_NEAR_CLIP_VALUE);
                #else
                clip.z=max(clip.z,clip.w*UNITY_NEAR_CLIP_VALUE);
                #endif
                return clip;
            }
            float4 ShadowFrag():SV_Target {return 0;}
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags {"LightMode"="DepthOnly"}
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            float4 DepthVert(float4 p:POSITION):SV_POSITION {return TransformObjectToHClip(p.xyz);}
            float4 DepthFrag():SV_Target {return 0;}
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags {"LightMode"="DepthNormalsOnly"}
            ZWrite On
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex NormalVert
            #pragma fragment NormalFrag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            struct A {float4 p:POSITION;float3 n:NORMAL;};
            struct V {float4 p:SV_POSITION;float3 n:TEXCOORD0;};
            V NormalVert(A i) {V o;o.p=TransformObjectToHClip(i.p.xyz);o.n=TransformObjectToWorldNormal(i.n);return o;}
            float4 NormalFrag(V i):SV_Target
            {
                float3 n=normalize(i.n);
                #if defined(_GBUFFER_NORMALS_OCT)
                return float4(PackFloat2To888(saturate(PackNormalOctQuadEncode(n)*.5+.5)),0);
                #else
                return float4(n,0);
                #endif
            }
            ENDHLSL
        }
    }
}

