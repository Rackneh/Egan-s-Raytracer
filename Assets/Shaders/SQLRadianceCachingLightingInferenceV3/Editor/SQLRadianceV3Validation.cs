using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace Rackneh.InferenceLighting.SQLRadianceLiveV3.Editor
{
    public static class SQLRadianceV3Validation
    {
        const string Reports="Tools/sql-live-v3-validation";
        static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
        static SQLRadianceV3System.LightingSample[] Read(SQLRadianceV3System system)
        {
            var data=new SQLRadianceV3System.LightingSample[system.Geometry.SampleCount]; system.Cache.GetData(data); return data;
        }
        static int Sample(SQLRadianceV3System system,Renderer renderer,int face=2,int x=4,int y=4)
        {
            return system.Geometry.Entries.First(e=>e.renderer==renderer).start+face*64+y*8+x;
        }
        static float Energy(Vector4 v) => v.x+v.y+v.z;
        static void Steps(SQLRadianceV3System system,int count) { for(int i=0;i<count;i++) system.Tick(); }
        static Vector4 Trace(SQLRadianceV3System system,Vector3 origin,Vector3 direction)
        {
            var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>(SQLRadianceV3Scene.Root+"/Editor/SQLV3Validation.compute");
            int kernel=shader.FindKernel("Trace");
            using(var rays=new ComputeBuffer(2,16)) using(var results=new ComputeBuffer(1,16))
            {
                rays.SetData(new[]{SQLRadianceV3Geometry.V(origin,0),SQLRadianceV3Geometry.V(direction,0)});
                system.Geometry.Bind(shader,kernel); shader.SetFloat("_SQL3Bias",.015f); shader.SetInt("RayCount",1);
                shader.SetBuffer(kernel,"Rays",rays); shader.SetBuffer(kernel,"Results",results); shader.Dispatch(kernel,1,1,1);
                var result=new Vector4[1]; results.GetData(result); return result[0];
            }
        }
        static double FrameCost(Camera camera,RenderTexture target,Texture2D pixel,Action update)
        {
            var samples=new double[12];
            for(int frame=0;frame<18;frame++)
            {
                var watch=System.Diagnostics.Stopwatch.StartNew(); update?.Invoke(); camera.Render();
                RenderTexture.active=target; pixel.ReadPixels(new Rect(0,0,1,1),0,0); pixel.Apply(); watch.Stop();
                if(frame>=6)samples[frame-6]=watch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(samples); return (samples[5]+samples[6])*.5;
        }
        static void Capture(Camera camera,RenderTexture target,string name)
        {
            camera.Render(); RenderTexture.active=target;
            var pixels=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);
            pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0); pixels.Apply();
            File.WriteAllBytes(Reports+"/"+name+".png",pixels.EncodeToPNG()); Object.DestroyImmediate(pixels);
        }
        [MenuItem("Tools/Inference Lighting/SQL V3 Live/Capture and benchmark feature demo")]
        public static void CaptureAndBenchmark()
        {
            Directory.CreateDirectory(Reports); var report=new StringBuilder();
            var previous=SceneManager.GetActiveScene(); var active=RenderTexture.active;
            var priorRenderers=Object.FindObjectsOfType<Renderer>().Where(r=>r.enabled).ToArray();
            var priorLights=Object.FindObjectsOfType<Light>().Where(l=>l.enabled).ToArray();
            var priorSystems=Object.FindObjectsOfType<SQLRadianceV3System>().Where(s=>s.enabled).ToArray();
            foreach(var r in priorRenderers)r.enabled=false; foreach(var l in priorLights)l.enabled=false; foreach(var s in priorSystems)s.enabled=false;
            Scene scene=default; RenderTexture rt=null; Texture2D pixel=null; Material[] originals=null,baseline=null;
            try
            {
                scene=EditorSceneManager.OpenScene(SQLRadianceV3Scene.Root+"/Demo/SQLV3Features.unity",OpenSceneMode.Additive);
                var camera=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<Camera>()).First();
                var system=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<SQLRadianceV3System>()).First();
                var renderers=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<Renderer>()).ToArray();
                var skin=renderers.OfType<SkinnedMeshRenderer>().First();
                originals=renderers.Select(r=>r.sharedMaterial).ToArray(); baseline=new Material[renderers.Length];
                rt=new RenderTexture(960,540,24,RenderTextureFormat.ARGBHalf); rt.Create(); camera.targetTexture=rt;
                pixel=new Texture2D(1,1,TextureFormat.RGBA32,false);
                system.settings.samplesPerFrame=2048; Steps(system,48); Capture(camera,rt,"v3-features");
                system.settings.debugView=1; system.Tick(); Capture(camera,rt,"v3-indirect"); system.settings.debugView=0;
                var bone=skin.bones[2]; bone.localRotation=Quaternion.Euler(0,0,55);
                var moving=renderers.First(r=>r.name=="Moving blue cube"); moving.transform.position+=Vector3.right*1.8f;
                Steps(system,16); Capture(camera,rt,"v3-moved");
                // Compare the unchanged analytic baseline on the same rigid scene.
                skin.enabled=false; system.enabled=false;
                for(int i=0;i<renderers.Length;i++)
                {
                    baseline[i]=new Material(originals[i]); baseline[i].shader=Shader.Find("InferenceLighting/SQLRadianceLit");
                    baseline[i].SetInt("_SubsurfaceProfile",0); renderers[i].sharedMaterial=baseline[i];
                }
                var oldRoot=SQLRadianceV3Scene.Object(scene,"Baseline system");
                var old=oldRoot.AddComponent<Rackneh.InferenceLighting.SQLRadiance.SQLRadianceSystem>();
                old.Settings.limitedBounces=false; old.Settings.maxBounces=4096; old.BindGlobalShaderParameters();
                double baselineMs=FrameCost(camera,rt,pixel,null); Capture(camera,rt,"baseline-rigid");
                Object.DestroyImmediate(oldRoot);
                for(int i=0;i<renderers.Length;i++) renderers[i].sharedMaterial=originals[i];
                system.enabled=true; Steps(system,32);
                double rigidMs=FrameCost(camera,rt,pixel,system.Tick);
                skin.enabled=true; system.Rebuild(); Steps(system,32);
                double fullMs=FrameCost(camera,rt,pixel,system.Tick);
                system.settings.maxBounces=32; double depth32=FrameCost(camera,rt,pixel,system.Tick);
                system.settings.maxBounces=4096; double depth4096=FrameCost(camera,rt,pixel,system.Tick);
                report.AppendLine("960x540, median of 12 synchronized Camera.Render + one-pixel readback frames after 6 warmups.");
                report.AppendLine("These are CPU+GPU wall times in the open editor, not isolated GPU timestamps or a player benchmark.");
                report.AppendLine($"Working baseline, rigid scene, infinite mirrors: {baselineMs:F3} ms");
                report.AppendLine($"Live V3, same rigid scene + GI/shadows/emission: {rigidMs:F3} ms ({rigidMs/baselineMs:F2}x baseline)");
                report.AppendLine($"Live V3, plus skinned character: {fullMs:F3} ms");
                report.AppendLine($"Live V3 infinite mode, maxBounces field 32 / 4096: {depth32:F3} / {depth4096:F3} ms");
                report.AppendLine($"Lighting budget: {system.UpdatedSamples} samples/frame, {system.settings.giRaysPerSample} GI rays/sample, {system.Geometry.MeshTriangleCount} mesh triangles.");
                var errors=ShaderUtil.GetShaderMessages(Shader.Find(SQLRadianceV3Scene.ShaderName)).Where(m=>m.severity==UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToArray();
                Require(errors.Length==0,string.Join("\n",errors.Select(e=>e.message)));
                report.AppendLine("RESULT: captures rendered without shader errors.");
            }
            catch(Exception ex) {report.AppendLine("FAIL: "+ex);throw;}
            finally
            {
                RenderTexture.active=active;
                if(scene.IsValid())EditorSceneManager.CloseScene(scene,true); SceneManager.SetActiveScene(previous);
                if(rt)Object.DestroyImmediate(rt); if(pixel)Object.DestroyImmediate(pixel);
                if(baseline!=null) foreach(var m in baseline)if(m)Object.DestroyImmediate(m);
                foreach(var r in priorRenderers)if(r)r.enabled=true; foreach(var l in priorLights)if(l)l.enabled=true; foreach(var s in priorSystems)if(s)s.enabled=true;
                // Original SQL variants retain independent globals; restore their existing bindings.
                foreach(var s in Object.FindObjectsOfType<Rackneh.InferenceLighting.SQLRadiance.SQLRadianceSystem>())s.BindGlobalShaderParameters();
                File.WriteAllText(Reports+"/performance.txt",report.ToString()); Debug.Log(report.ToString());
            }
        }
        [MenuItem("Tools/Inference Lighting/SQL V3 Live/Run GPU validation")]
        public static void Run()
        {
            Directory.CreateDirectory(Reports); var report=new StringBuilder();
            var previous=SceneManager.GetActiveScene(); var existing=Object.FindObjectsOfType<SQLRadianceV3System>().Where(s=>s.enabled).ToArray();
            foreach(var e in existing)e.enabled=false;
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            Material white=null,emit=null,skinMat=null; Texture2D texture=null; Mesh skinMesh=null;
            try
            {
                white=SQLRadianceV3Scene.Material(Color.white); emit=SQLRadianceV3Scene.Material(Color.black); skinMat=SQLRadianceV3Scene.Material(Color.red);
                var floor=SQLRadianceV3Scene.Primitive(scene,"Receiver",PrimitiveType.Cube,new Vector3(0,-.1f,0),new Vector3(8,.2f,8),white).GetComponent<Renderer>();
                var blocker=SQLRadianceV3Scene.Primitive(scene,"Moving blocker",PrimitiveType.Cube,new Vector3(20,1.5f,0),new Vector3(2,1,2),white).GetComponent<Renderer>();
                var emitter=SQLRadianceV3Scene.Primitive(scene,"Textured emitter",PrimitiveType.Cube,new Vector3(0,3.5f,0),new Vector3(6,.1f,6),emit).GetComponent<Renderer>();
                var light=SQLRadianceV3Scene.Light(scene,"Test light",LightType.Point,new Vector3(0,3,0),Quaternion.Euler(90,0,0),Color.white,0);
                var skin=SQLRadianceV3Scene.Character(scene,skinMat,out var left,out var right); skin.transform.position=new Vector3(6,0,0); skinMesh=skin.sharedMesh;
                var root=SQLRadianceV3Scene.Object(scene,"Test system"); root.SetActive(false);
                var system=root.AddComponent<SQLRadianceV3System>(); system.settings.samplesPerFrame=16384; system.settings.giRaysPerSample=16; system.settings.historyWeight=0; root.SetActive(true);
                Require(system.Geometry!=null,"System initialization failed");
                int floorSample=Sample(system,floor); Steps(system,2);
                Require(Energy(Read(system)[floorSample].direct)<1e-6f && Energy(Read(system)[floorSample].indirect)<1e-6f,"Lights-off scene is not black");
                report.AppendLine("PASS: no fabricated directional light or ambient floor in a dark scene.");
                emitter.enabled=false; system.Tick();
                foreach(var type in new[]{LightType.Directional,LightType.Point,LightType.Spot,LightType.Rectangle,LightType.Disc})
                {
                    light.type=type; light.intensity=4; light.range=12; light.spotAngle=100; light.innerSpotAngle=80; light.areaSize=new Vector2(2,2);
                    system.Tick(); float lit=Energy(Read(system)[floorSample].direct);
                    Require(lit>.01f,type+" contributes no light: "+lit);
                    report.AppendLine($"PASS: {type} irradiance = {lit:F5}.");
                }
                light.type=LightType.Point; light.intensity=8; system.Tick(); float unblocked=Energy(Read(system)[floorSample].direct);
                blocker.transform.position=new Vector3(0,1.5f,0); system.Tick(); float blocked=Energy(Read(system)[floorSample].direct);
                Require(blocked<unblocked*.05f,"Moving occluder does not shadow cached direct light");
                blocker.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off; system.Tick();
                Require(Energy(Read(system)[floorSample].direct)>unblocked*.9f,"ShadowCastingMode.Off was ignored");
                blocker.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.On;
                report.AppendLine($"PASS: moving shadow blocker {unblocked:F5} -> {blocked:F5}; shadow flags respected.");
                // A downward ray must see the moved rigid object at its current location.
                var hit=Trace(system,new Vector3(0,4,0),Vector3.down);
                Require(Mathf.Abs(hit.y-2)<.001f,"Rigid reflection geometry is stale");
                blocker.transform.position=new Vector3(20,1.5f,0); system.Tick(); hit=Trace(system,new Vector3(0,4,0),Vector3.down);
                Require(Mathf.Abs(hit.y)<.001f,"Rigid object left a ghost reflection");
                report.AppendLine("PASS: rigid movement updates reflection intersections without rebaking.");
                // Trace a head-on arm ray before/after a bone translation. This exercises BakeMesh and BVH refit.
                var before=Trace(system,new Vector3(5.45f,1.25f,-3),Vector3.forward);
                Require(before.w>0,"Skinned arm was not intersected");
                left.localPosition+=new Vector3(0,1,0); system.Tick();
                var after=Trace(system,new Vector3(5.45f,1.25f,-3),Vector3.forward);
                var moved=Trace(system,new Vector3(5.45f,2.25f,-3),Vector3.forward);
                Require(after.w==0 && moved.w>0,"Bone deformation did not refit reflection geometry");
                report.AppendLine("PASS: offscreen skinned bone deformation moves exact mesh intersections; no old silhouette remains.");
                // Textured emission alone must illuminate another object, through transport.
                light.intensity=0; emitter.enabled=true;
                texture=new Texture2D(4,4,TextureFormat.RGBA32,false,true);
                texture.SetPixels(Enumerable.Repeat(Color.black,16).ToArray()); texture.Apply();
                emit.SetTexture("_EmissionMap",texture); emit.SetColor("_EmissionColor",new Color(4,0,0));
                Steps(system,4); float dark=Energy(Read(system)[floorSample].indirect);
                texture.SetPixels(Enumerable.Repeat(Color.white,16).ToArray()); texture.Apply();
                Steps(system,12); var litGI=Read(system)[floorSample].indirect;
                Require(litGI.x>.05f && litGI.x>dark+.04f && litGI.y<litGI.x*.1f,"Textured emission failed to bounce red light: "+litGI);
                report.AppendLine($"PASS: emission texture black -> white changes receiver GI {dark:F5} -> {litGI.x:F5} red.");
                // With emission disabled, punctual lighting must also bounce into an occluded receiver.
                emit.SetColor("_EmissionColor",Color.black); light.intensity=12; light.transform.position=new Vector3(2,2,0);
                blocker.transform.position=new Vector3(0,1,0); blocker.transform.localScale=new Vector3(2,.2f,2);
                Steps(system,32); var indirect=Read(system)[floorSample].indirect;
                Require(Energy(indirect)>.0001f,"Punctual lighting has no spatial diffuse bounce");
                report.AppendLine($"PASS: punctual light produces spatial diffuse bounce = {Energy(indirect):F5}.");
                system.settings.globalIllumination=false; Steps(system,2);
                Require(Energy(Read(system)[floorSample].indirect)<1e-6f,"GI toggle retained indirect light");
                report.AppendLine("PASS: GI can be disabled without retaining stale bounce light.");
                system.settings.samplesPerFrame=64; system.settings.maxBounces=1; system.Tick(); int small=system.UpdatedSamples;
                system.settings.maxBounces=4096; system.Tick(); Require(system.UpdatedSamples==small && small==64,"Lighting budget depends on mirror depth");
                report.AppendLine("PASS: lighting dispatch stays at 64 samples for 1 and 4096 mirror bounces.");
                var errors=ShaderUtil.GetShaderMessages(Shader.Find(SQLRadianceV3Scene.ShaderName)).Where(m=>m.severity==UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToArray();
                Require(errors.Length==0,string.Join("\n",errors.Select(e=>e.message)));
                report.AppendLine("PASS: Unity reports no render shader errors.");
                report.AppendLine("RESULT: PASS");
            }
            catch(Exception ex) { report.AppendLine("FAIL: "+ex); throw; }
            finally
            {
                EditorSceneManager.CloseScene(scene,true); SceneManager.SetActiveScene(previous);
                foreach(var e in existing) if(e)e.enabled=true;
                if(white)Object.DestroyImmediate(white); if(emit)Object.DestroyImmediate(emit); if(skinMat)Object.DestroyImmediate(skinMat);
                if(texture)Object.DestroyImmediate(texture); if(skinMesh)Object.DestroyImmediate(skinMesh);
                File.WriteAllText(Reports+"/gpu-validation.txt",report.ToString()); Debug.Log(report.ToString());
            }
        }
        public static void CaptureSceneCopy()
        {
            var previous=SceneManager.GetActiveScene(); var active=RenderTexture.active;
            var others=Object.FindObjectsOfType<Renderer>().Where(r=>r.enabled).ToArray();
            var lights=Object.FindObjectsOfType<Light>().Where(l=>l.enabled).ToArray();
            foreach(var r in others)r.enabled=false;foreach(var l in lights)l.enabled=false;
            Scene scene=default;RenderTexture rt=null;Texture2D pixel=null;
            try
            {
                scene=EditorSceneManager.OpenScene(SQLRadianceV3Scene.Root+"/SQLTestV3.unity",OpenSceneMode.Additive);
                var roots=scene.GetRootGameObjects();
                var camera=roots.SelectMany(r=>r.GetComponentsInChildren<Camera>()).First();
                var system=roots.SelectMany(r=>r.GetComponentsInChildren<SQLRadianceV3System>()).First(s=>s.enabled);
                rt=new RenderTexture(960,540,24,RenderTextureFormat.ARGBHalf);rt.Create();camera.targetTexture=rt;
                pixel=new Texture2D(1,1,TextureFormat.RGBA32,false);
                Steps(system,48);Capture(camera,rt,"sql-test-v3");
                double elapsed=FrameCost(camera,rt,pixel,system.Tick);
                double frozen=FrameCost(camera,rt,pixel,null);
                string stats=$"V3 copy of user's active SQLTest scene.\n{system.Geometry.Count} surface slots; {system.Geometry.MeshTriangleCount} mesh triangles; {system.LightCount} lights.\n{system.UpdatedSamples} lighting samples/frame; {system.FullRefreshFrames} frames per refresh.\n960x540 synchronized editor frame median: {elapsed:F3} ms; frozen cache: {frozen:F3} ms.\n";
                system.enabled=false;
                var renderers=roots.SelectMany(r=>r.GetComponentsInChildren<Renderer>()).ToArray();
                var temporary=new Material[renderers.Length];
                for(int i=0;i<renderers.Length;i++) {temporary[i]=new Material(renderers[i].sharedMaterial) {shader=Shader.Find("InferenceLighting/SQLRadianceLit")};temporary[i].SetInt("_SubsurfaceProfile",0);renderers[i].sharedMaterial=temporary[i];}
                var baseline=SQLRadianceV3Scene.Object(scene,"Baseline").AddComponent<Rackneh.InferenceLighting.SQLRadiance.SQLRadianceSystem>();
                baseline.Settings.limitedBounces=false;baseline.BindGlobalShaderParameters();
                double old=FrameCost(camera,rt,pixel,null);Capture(camera,rt,"sql-test-baseline");
                Object.DestroyImmediate(baseline.gameObject);foreach(var m in temporary)Object.DestroyImmediate(m);
                File.WriteAllText(Reports+"/scene-copy.txt",stats+$"Working analytic baseline on the same scene: {old:F3} ms.\n");
            }
            finally
            {
                RenderTexture.active=active;if(scene.IsValid())EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(previous);
                if(rt)Object.DestroyImmediate(rt);if(pixel)Object.DestroyImmediate(pixel);
                foreach(var r in others)if(r)r.enabled=true;foreach(var l in lights)if(l)l.enabled=true;
            }
        }
    }
}
