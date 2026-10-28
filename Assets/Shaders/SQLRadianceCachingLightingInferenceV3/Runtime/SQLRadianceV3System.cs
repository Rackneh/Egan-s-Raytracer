using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Profiling;

namespace Rackneh.InferenceLighting.SQLRadianceLiveV3
{
    [Serializable]
    public sealed class SQLRadianceV3Settings
    {
        public bool reflections = true, globalIllumination = true, limitedBounces;
        [Range(1,4096)] public int maxBounces = 4096;
        [Range(0,.96f)] public float reflectionFeedback = .92f;
        [Header("Surface lighting budget (independent of mirror depth)")]
        [Range(64,16384)] public int samplesPerFrame = 2048;
        [Range(1,32)] public int giRaysPerSample = 4;
        [Range(0,.95f)] public float historyWeight = .65f;
        [Range(.001f,.05f)] public float shadowBias = .015f;
        [ColorUsage(false,true)] public Color environment = Color.black;
        [Range(1024,131072)] public int meshVertexBudget = 65536;
        public LayerMask inclusionMask = ~0;
        [Tooltip("0: full lighting; 1: indirect only; 2: reflections only; 3: direct shadows and lighting")]
        [Range(0,3)] public int debugView;
    }

    [ExecuteAlways, DefaultExecutionOrder(10000), DisallowMultipleComponent]
    public sealed class SQLRadianceV3System : MonoBehaviour
    {
        [StructLayout(LayoutKind.Sequential)] public struct LightingSample { public Vector4 direct, indirect, area, dominantDirection, dominantRadiance; }
        public SQLRadianceV3Settings settings = new SQLRadianceV3Settings();
        public SQLRadianceV3Geometry Geometry { get; private set; }
        public ComputeBuffer Cache => cache;
        public int LightCount { get; private set; }
        public int UpdatedSamples { get; private set; }
        public int Iteration { get; private set; }
        public float FullRefreshFrames => Geometry == null ? 0 : Mathf.Ceil(Geometry.SampleCount/(float)Mathf.Max(64,settings.samplesPerFrame));
        ComputeShader lighting;
        ComputeBuffer cache, scratch;
        int copyKernel, updateKernel, cursor, registryHash;
        double nextDiscovery;
        Light[] lights = Array.Empty<Light>();
        readonly Vector4[] lightData = new Vector4[192], baseST = new Vector4[32], emissionST = new Vector4[32];
        readonly Texture[] baseSources = new Texture[32], emissionSources = new Texture[32];
        readonly uint[] baseVersions = new uint[32], emissionVersions = new uint[32];
        RenderTexture albedoMaps, emissionMaps, transfer, lightingAtlas;
        bool warnedLights;
        static SQLRadianceV3System owner;
        public bool IsOwner => owner == this;

        void OnEnable()
        {
            if (owner && owner != this) { Debug.LogWarning("[SQL V3] Only one live system can own the global cache.",this); return; }
            owner = this;
            Rebuild();
        }
        void OnDisable()
        {
            if (owner != this) return;
            Shader.SetGlobalInt("_SQL3Enabled",0); Shader.SetGlobalInt("_SQL3EntityCount",0); Shader.SetGlobalInt("_SQL3Debug",0);
            Release(); owner = null;
        }
        void LateUpdate()
        {
            if (!IsOwner || !lighting) return;
            if(Time.realtimeSinceStartupAsDouble >= nextDiscovery)
            {
                nextDiscovery=Time.realtimeSinceStartupAsDouble+1;
                var renderers=Discover(); int hash=RegistryHash(renderers);
                lights=FindObjectsOfType<Light>();
                if(hash!=registryHash) { Rebuild(renderers); return; }
            }
            Tick();
        }
        Renderer[] Discover()
        {
            var result=new List<Renderer>();
            foreach(var r in FindObjectsOfType<Renderer>())
                if(r.gameObject.scene==gameObject.scene && r.enabled && r.gameObject.activeInHierarchy &&
                   (settings.inclusionMask.value & (1<<r.gameObject.layer))!=0 && (r is MeshRenderer || r is SkinnedMeshRenderer)) result.Add(r);
            result.Sort((a,b)=>a.GetInstanceID().CompareTo(b.GetInstanceID()));
            return result.ToArray();
        }
        static int RegistryHash(Renderer[] renderers)
        {
            unchecked
            {
                int hash=17;
                foreach(var r in renderers)
                {
                    hash=hash*31+r.GetInstanceID();
                    var mesh=r is SkinnedMeshRenderer sk ? sk.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    hash=hash*31+(mesh ? mesh.GetInstanceID() : 0);
                    foreach(var mat in r.sharedMaterials) hash=hash*31+(mat ? mat.GetInstanceID() : 0);
                }
                return hash;
            }
        }
        [ContextMenu("Rebuild live scene registry")]
        public void Rebuild() { if(IsOwner) Rebuild(Discover()); }
        void Rebuild(Renderer[] renderers)
        {
            Release();
            if(!SystemInfo.supportsComputeShaders) { Debug.LogError("[SQL V3] Compute shaders are required.",this); return; }
            var source=Resources.Load<ComputeShader>("SQLV3Lighting");
            if(!source) { Debug.LogError("[SQL V3] SQLV3Lighting compute resource is missing.",this); return; }
            lighting=Instantiate(source); lighting.hideFlags=HideFlags.HideAndDontSave;
            copyKernel=lighting.FindKernel("CopyCache"); updateKernel=lighting.FindKernel("UpdateCache");
            Geometry=new SQLRadianceV3Geometry(renderers,settings.meshVertexBudget);
            cache=new ComputeBuffer(Math.Max(1,Geometry.SampleCount),80);
            scratch=new ComputeBuffer(Math.Max(1,Geometry.SampleCount),80);
            var zero=new LightingSample[Math.Max(1,Geometry.SampleCount)]; cache.SetData(zero); scratch.SetData(zero);
            albedoMaps=CreateAtlas("SQL V3 albedo"); emissionMaps=CreateAtlas("SQL V3 emission");
            lightingAtlas=new RenderTexture(48,256,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear) {
                name="SQL V3 filtered surface lighting",dimension=TextureDimension.Tex2DArray,volumeDepth=5,
                enableRandomWrite=true,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave };
            lightingAtlas.Create();
            var active=RenderTexture.active;
            for(int slice=0;slice<5;slice++) {Graphics.SetRenderTarget(lightingAtlas,0,CubemapFace.Unknown,slice);GL.Clear(false,true,Color.clear);}
            RenderTexture.active=active;
            transfer=new RenderTexture(128,128,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear) { hideFlags=HideFlags.HideAndDontSave }; transfer.Create();
            Array.Clear(baseSources,0,32); Array.Clear(emissionSources,0,32);
            registryHash=RegistryHash(renderers); lights=FindObjectsOfType<Light>(); cursor=0; Iteration=0;
            nextDiscovery=Time.realtimeSinceStartupAsDouble+1;
            Tick();
        }
        static RenderTexture CreateAtlas(string name)
        {
            var rt=new RenderTexture(128,128,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear) {
                name=name,dimension=TextureDimension.Tex2DArray,volumeDepth=32,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Repeat,hideFlags=HideFlags.HideAndDontSave };
            rt.Create(); return rt;
        }
        static Vector4 TextureST(Material material,string property)
        {
            if(!material || !material.HasProperty(property)) return new Vector4(1,1,0,0);
            Vector2 scale=material.GetTextureScale(property),offset=material.GetTextureOffset(property);
            return new Vector4(scale.x,scale.y,offset.x,offset.y);
        }
        void CopyMap(Material material,string property,int slice,RenderTexture atlas,Texture[] sources,uint[] versions)
        {
            Texture source=material && material.HasProperty(property) ? material.GetTexture(property) : null;
            if(!source || source.dimension!=TextureDimension.Tex2D) source=Texture2D.whiteTexture;
            if(sources[slice]==source && versions[slice]==source.updateCount && !(source is RenderTexture)) return;
            var previous=RenderTexture.active;
            Graphics.Blit(source,transfer); Graphics.CopyTexture(transfer,0,0,atlas,slice,0);
            RenderTexture.active=previous; sources[slice]=source; versions[slice]=source.updateCount;
        }
        void SyncMaterials()
        {
            for(int i=0;i<Geometry.Count;i++)
            {
                var m=Geometry.Entries[i].material;
                string baseName=m && m.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                CopyMap(m,baseName,i,albedoMaps,baseSources,baseVersions);
                CopyMap(m,"_EmissionMap",i,emissionMaps,emissionSources,emissionVersions);
                baseST[i]=TextureST(m,baseName); emissionST[i]=TextureST(m,"_EmissionMap");
                var flags=Geometry.SurfaceFlags[i];
                flags.w=(baseSources[i]!=Texture2D.whiteTexture ? 1:0)+(emissionSources[i]!=Texture2D.whiteTexture ? 2:0);
                Geometry.SurfaceFlags[i]=flags;
            }
        }
        [StructLayout(LayoutKind.Explicit)] struct MaskBits { [FieldOffset(0)] public int integer; [FieldOffset(0)] public float floating; }
        void SyncLights()
        {
            int count=0;
            foreach(var light in lights)
            {
                if(!light || !light.enabled || !light.gameObject.activeInHierarchy || light.gameObject.scene!=gameObject.scene) continue;
                if(count==32) { if(!warnedLights) Debug.LogWarning("[SQL V3] 32-light cache budget reached. Additional lights still use URP direct lighting.",this); warnedLights=true; break; }
                int type=light.type==LightType.Directional ? 0 : light.type==LightType.Point ? 1 : light.type==LightType.Spot ? 2 : light.type==LightType.Disc ? 4 : 3;
                var tr=light.transform; Color c=light.color.linear*light.intensity;
                if(light.useColorTemperature) c*=Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
                Vector2 size=light.areaSize;
                // areaSize is preserved by Unity for rectangle/disc Light components.
                lightData[count*6]=SQLRadianceV3Geometry.V(tr.position,type);
                lightData[count*6+1]=new Vector4(c.r,c.g,c.b,light.range);
                lightData[count*6+2]=SQLRadianceV3Geometry.V(tr.forward,Mathf.Cos(light.spotAngle*Mathf.Deg2Rad*.5f));
                lightData[count*6+3]=SQLRadianceV3Geometry.V(tr.right,Mathf.Max(.001f,size.x));
                lightData[count*6+4]=SQLRadianceV3Geometry.V(tr.up,Mathf.Max(.001f,size.y));
                lightData[count*6+5]=new Vector4(Mathf.Cos(light.innerSpotAngle*Mathf.Deg2Rad*.5f),
                    light.shadows==LightShadows.None ? 0 : light.shadowStrength,new MaskBits {integer=light.cullingMask}.floating,light.bounceIntensity);
                count++;
            }
            LightCount=count;
        }
        public void Tick()
        {
            if(!IsOwner || !lighting || Geometry==null) return;
            Profiler.BeginSample("SQL V3: live scene and skin refit");
            Geometry.Sync(); SyncMaterials(); SyncLights(); Geometry.Bind();
            Shader.SetGlobalInt("_SQL3Enabled",1); Shader.SetGlobalInt("_SQL3Debug",settings.debugView);
            Shader.SetGlobalVector("_SQL3Bounce",new Vector4(settings.maxBounces,settings.reflectionFeedback,settings.reflections ? 1:0,settings.limitedBounces ? 1:0));
            Shader.SetGlobalFloat("_SQL3GIEnabled",settings.globalIllumination ? 1:0);
            Shader.SetGlobalFloat("_SQL3Bias",settings.shadowBias); Shader.SetGlobalVector("_SQL3Sky",settings.environment.linear);
            Shader.SetGlobalTexture("_SQL3AlbedoMaps",albedoMaps); Shader.SetGlobalTexture("_SQL3EmissionMaps",emissionMaps);
            Shader.SetGlobalTexture("_SQL3LightingAtlas",lightingAtlas);
            Shader.SetGlobalVectorArray("_SQL3BaseST",baseST); Shader.SetGlobalVectorArray("_SQL3EmissionST",emissionST);
            Profiler.EndSample();
            Profiler.BeginSample("SQL V3: budgeted lighting cache");
            UpdatedSamples=Math.Min(Geometry.SampleCount,Mathf.Clamp(settings.samplesPerFrame,64,16384));
            if(UpdatedSamples>0)
            {
                lighting.SetInt("_SQL3SampleCount",Geometry.SampleCount);
                lighting.SetBuffer(copyKernel,"_SQL3Cache",cache); lighting.SetBuffer(copyKernel,"_SQL3WriteCache",scratch);
                lighting.Dispatch(copyKernel,(Geometry.SampleCount+63)/64,1,1);
                Geometry.Bind(lighting,updateKernel);
                lighting.SetBuffer(updateKernel,"_SQL3Cache",cache); lighting.SetBuffer(updateKernel,"_SQL3WriteCache",scratch);
                lighting.SetTexture(updateKernel,"_SQL3WriteAtlas",lightingAtlas);
                lighting.SetTexture(updateKernel,"_SQL3AlbedoMaps",albedoMaps); lighting.SetTexture(updateKernel,"_SQL3EmissionMaps",emissionMaps);
                lighting.SetVectorArray("_SQL3BaseST",baseST); lighting.SetVectorArray("_SQL3EmissionST",emissionST);
                lighting.SetVectorArray("_SQL3Lights",lightData); lighting.SetInt("_SQL3LightCount",LightCount);
                lighting.SetInt("_SQL3UpdateStart",cursor); lighting.SetInt("_SQL3UpdateCount",UpdatedSamples);
                lighting.SetInt("_SQL3Iteration",Iteration++); lighting.SetInt("_SQL3GISamples",Mathf.Clamp(settings.giRaysPerSample,1,32));
                lighting.SetFloat("_SQL3GIEnabled",settings.globalIllumination ? 1:0); lighting.SetFloat("_SQL3HistoryWeight",settings.historyWeight);
                lighting.SetFloat("_SQL3Bias",settings.shadowBias); lighting.SetVector("_SQL3Sky",settings.environment.linear);
                lighting.Dispatch(updateKernel,(UpdatedSamples+63)/64,1,1);
                var swap=cache; cache=scratch; scratch=swap; cursor=(cursor+UpdatedSamples)%Geometry.SampleCount;
            }
            Shader.SetGlobalBuffer("_SQL3Cache",cache);
            Profiler.EndSample();
        }
        void Release()
        {
            Geometry?.Dispose(); Geometry=null; cache?.Release(); scratch?.Release(); cache=scratch=null;
            if(lighting) DestroyImmediate(lighting);
            if(albedoMaps) DestroyImmediate(albedoMaps); if(emissionMaps) DestroyImmediate(emissionMaps); if(transfer) DestroyImmediate(transfer);
            if(lightingAtlas)DestroyImmediate(lightingAtlas);
        }
    }
}
