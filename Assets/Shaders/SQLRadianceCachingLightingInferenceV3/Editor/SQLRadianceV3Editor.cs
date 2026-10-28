using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Rackneh.InferenceLighting.SQLRadianceLiveV3.Editor
{
    [CustomEditor(typeof(SQLRadianceV3System))]
    public sealed class SQLRadianceV3SystemEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector(); var system=(SQLRadianceV3System)target;
            if(system.Geometry!=null)
                EditorGUILayout.HelpBox($"{system.Geometry.Count}/32 surface slots, {system.Geometry.MeshTriangleCount} mesh triangles, {system.LightCount}/32 lights.\n{system.UpdatedSamples} lighting samples/frame; full cache refresh in {system.FullRefreshFrames} frames. Mirror tracing is capped at 32 steps.",MessageType.Info);
            if(GUILayout.Button("Rebuild scene registry")) system.Rebuild();
        }
    }
    public static class SQLRadianceV3Scene
    {
        public const string Root="Assets/InferenceLighting/SQLRadianceCachingLightingInferenceV3";
        public const string ShaderName="InferenceLighting/SQLRadianceLiveV3Lit";
        public static GameObject Object(Scene scene,string name)
        {
            var go=new GameObject(name); SceneManager.MoveGameObjectToScene(go,scene); return go;
        }
        public static Material Material(Color color,float smoothness=0,float metallic=0)
        {
            var m=new Material(Shader.Find(ShaderName)); m.SetColor("_BaseColor",color); m.SetFloat("_Smoothness",smoothness); m.SetFloat("_Metallic",metallic); return m;
        }
        public static GameObject Primitive(Scene scene,string name,PrimitiveType type,Vector3 position,Vector3 scale,Material material)
        {
            var go=GameObject.CreatePrimitive(type); go.name=name; SceneManager.MoveGameObjectToScene(go,scene);
            go.transform.position=position; go.transform.localScale=scale; go.GetComponent<Renderer>().sharedMaterial=material;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>()); return go;
        }
        public static SkinnedMeshRenderer Character(Scene scene,Material material,out Transform leftArm,out Transform rightArm)
        {
            var go=Object(scene,"Skinned character"); var skin=go.AddComponent<SkinnedMeshRenderer>();
            var positions=new List<Vector3>(); var normals=new List<Vector3>(); var uvs=new List<Vector2>(); var indices=new List<int>();
            var weights=new List<BoneWeight>(); var bones=new List<Transform>(); var bindposes=new List<Matrix4x4>();
            Vector3[] centers={new Vector3(0,1.15f,0),new Vector3(0,1.9f,0),new Vector3(-.55f,1.25f,0),new Vector3(.55f,1.25f,0),new Vector3(-.2f,.4f,0),new Vector3(.2f,.4f,0)};
            Vector3[] sizes={new Vector3(.65f,.9f,.35f),new Vector3(.4f,.4f,.4f),new Vector3(.25f,.8f,.25f),new Vector3(.25f,.8f,.25f),new Vector3(.25f,.8f,.3f),new Vector3(.25f,.8f,.3f)};
            var cube=GameObject.CreatePrimitive(PrimitiveType.Cube); var source=cube.GetComponent<MeshFilter>().sharedMesh;
            var vertices=source.vertices; var ns=source.normals; var uv=source.uv; var tris=source.triangles;
            for(int part=0;part<centers.Length;part++)
            {
                var bone=Object(scene,"Bone "+part).transform; bone.SetParent(go.transform,false); bone.localPosition=centers[part]; bones.Add(bone);
                bindposes.Add(bone.worldToLocalMatrix*go.transform.localToWorldMatrix);
                int start=positions.Count;
                for(int v=0;v<vertices.Length;v++) { positions.Add(Vector3.Scale(vertices[v],sizes[part])+centers[part]); normals.Add(ns[v]); uvs.Add(uv[v]); weights.Add(new BoneWeight {boneIndex0=part,weight0=1}); }
                foreach(int t in tris) indices.Add(start+t);
            }
            UnityEngine.Object.DestroyImmediate(cube);
            var mesh=new Mesh {name="SQL V3 skinned validation character"}; mesh.SetVertices(positions); mesh.SetNormals(normals); mesh.SetUVs(0,uvs); mesh.SetTriangles(indices,0);
            mesh.boneWeights=weights.ToArray(); mesh.bindposes=bindposes.ToArray(); mesh.RecalculateBounds();
            skin.sharedMesh=mesh; skin.bones=bones.ToArray(); skin.rootBone=go.transform; skin.sharedMaterial=material; skin.updateWhenOffscreen=true;
            leftArm=bones[2]; rightArm=bones[3]; return skin;
        }
        public static Light Light(Scene scene,string name,LightType type,Vector3 position,Quaternion rotation,Color color,float intensity)
        {
            var go=Object(scene,name); go.transform.SetPositionAndRotation(position,rotation); var light=go.AddComponent<Light>();
            light.type=type; light.color=color; light.intensity=intensity; light.range=12; light.shadows=LightShadows.Soft; return light;
        }
        [MenuItem("Tools/Inference Lighting/SQL V3 Live/Create feature demo")]
        public static void CreateDemo()
        {
            var previous=SceneManager.GetActiveScene(); var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            string folder=Root+"/Demo"; if(!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(Root,"Demo");
            string path=AssetDatabase.GenerateUniqueAssetPath(folder+"/SQLV3Features.unity");
            var materials=new List<Material>();
            try
            {
                var white=Material(new Color(.7f,.7f,.7f)); var red=Material(new Color(.8f,.05f,.03f)); var blue=Material(new Color(.04f,.2f,.9f));
                var mirror=Material(new Color(.96f,.96f,.96f),1,1); var emit=Material(Color.white);
                var texture=new Texture2D(16,16,TextureFormat.RGBA32,false,true); texture.name="Emission checker";
                for(int y=0;y<16;y++) for(int x=0;x<16;x++) texture.SetPixel(x,y,((x/4+y/4)%2)==0 ? Color.white : Color.black);
                texture.Apply(); AssetDatabase.CreateAsset(texture,AssetDatabase.GenerateUniqueAssetPath(folder+"/EmissionChecker.asset"));
                emit.SetColor("_EmissionColor",new Color(3,.8f,.1f)); emit.SetTexture("_EmissionMap",texture);
                materials.AddRange(new[]{white,red,blue,mirror,emit});
                for(int i=0;i<materials.Count;i++) AssetDatabase.CreateAsset(materials[i],AssetDatabase.GenerateUniqueAssetPath(folder+"/Material"+i+".mat"));
                Primitive(scene,"Floor",PrimitiveType.Cube,new Vector3(0,-.1f,0),new Vector3(8,.2f,8),white);
                Primitive(scene,"Left mirror",PrimitiveType.Cube,new Vector3(-4,1.5f,0),new Vector3(.1f,3,8),mirror);
                Primitive(scene,"Right mirror",PrimitiveType.Cube,new Vector3(4,1.5f,0),new Vector3(.1f,3,8),mirror);
                Primitive(scene,"Back wall",PrimitiveType.Cube,new Vector3(0,1.5f,4),new Vector3(8,3,.1f),white);
                Primitive(scene,"Red bounce wall",PrimitiveType.Cube,new Vector3(-2,1,2),new Vector3(.3f,2,2),red);
                Primitive(scene,"Emissive texture panel",PrimitiveType.Cube,new Vector3(1,1.5f,3.8f),new Vector3(2,1,.1f),emit);
                var moving=Primitive(scene,"Moving blue cube",PrimitiveType.Cube,new Vector3(0,.6f,1),Vector3.one,blue);
                var skin=Character(scene,red,out var left,out var right); skin.transform.position=new Vector3(1.5f,0,0);
                AssetDatabase.CreateAsset(skin.sharedMesh,AssetDatabase.GenerateUniqueAssetPath(folder+"/Character.asset"));
                Light(scene,"Directional",LightType.Directional,Vector3.zero,Quaternion.Euler(45,-30,0),Color.white,.5f);
                Light(scene,"Point",LightType.Point,new Vector3(-1,2,-1),Quaternion.identity,new Color(1,.8f,.6f),5);
                var spot=Light(scene,"Spot",LightType.Spot,new Vector3(2,2,-1),Quaternion.Euler(40,180,0),new Color(.4f,.6f,1),8); spot.spotAngle=70; spot.innerSpotAngle=45;
                var area=Light(scene,"Rectangle",LightType.Rectangle,new Vector3(0,2,3.5f),Quaternion.Euler(0,180,0),Color.white,2); area.areaSize=new Vector2(2,1);
                var disc=Light(scene,"Disc",LightType.Disc,new Vector3(-2,2,1),Quaternion.Euler(90,0,0),new Color(.4f,1,.6f),1); disc.areaSize=Vector2.one;
                var camera=Object(scene,"Main Camera").AddComponent<Camera>(); camera.tag="MainCamera"; camera.transform.position=new Vector3(0,2,-6);
                camera.transform.LookAt(new Vector3(0,1,1)); camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.black;
                var motion=Object(scene,"Demo motion").AddComponent<SQLV3DemoMotion>(); motion.movingObject=moving.transform; motion.leftArm=left; motion.rightArm=right;
                Object(scene,"SQL V3 Live").AddComponent<SQLRadianceV3System>();
                EditorSceneManager.SaveScene(scene,path); AssetDatabase.SaveAssets();
                Debug.Log("[SQL V3] Feature demo saved: "+path);
            }
            finally { EditorSceneManager.CloseScene(scene,true); SceneManager.SetActiveScene(previous); }
        }
        [MenuItem("Tools/Inference Lighting/SQL V3 Live/Create V3 copy of active scene")]
        public static void CreateSceneCopy()
        {
            var source=SceneManager.GetActiveScene();
            string path=AssetDatabase.GenerateUniqueAssetPath(Root+"/SQLTestV3.unity");
            // saveAsCopy preserves the open scene and its unsaved edits.
            if(!EditorSceneManager.SaveScene(source,path,true)) throw new System.IO.IOException("Could not copy active scene");
            var copy=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
            string folder=Root+"/ConvertedMaterials";
            if(!AssetDatabase.IsValidFolder(folder))AssetDatabase.CreateFolder(Root,"ConvertedMaterials");
            var converted=new Dictionary<Material,Material>();
            try
            {
                foreach(var root in copy.GetRootGameObjects())
                {
                    foreach(var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                        if(component && component.GetType().Name.Contains("SQLRadiance") && component.GetType().Name.EndsWith("System")) component.enabled=false;
                    foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        var materials=renderer.sharedMaterials;
                        for(int i=0;i<materials.Length;i++)
                        {
                            var original=materials[i]; if(!original)continue;
                            if(!converted.TryGetValue(original,out var replacement))
                            {
                                replacement=new Material(original) {shader=Shader.Find(ShaderName),name=original.name+" V3"};
                                replacement.shaderKeywords=System.Array.Empty<string>();
                                string materialPath=AssetDatabase.GenerateUniqueAssetPath(folder+"/Material"+converted.Count+".mat");
                                AssetDatabase.CreateAsset(replacement,materialPath); converted.Add(original,replacement);
                            }
                            materials[i]=replacement;
                        }
                        renderer.sharedMaterials=materials;
                    }
                }
                Object(copy,"SQL V3 Live").AddComponent<SQLRadianceV3System>();
                EditorSceneManager.SaveScene(copy,path); AssetDatabase.SaveAssets();
                Debug.Log($"[SQL V3] Created {path} with {converted.Count} separate materials. Source scene preserved.");
            }
            finally {EditorSceneManager.CloseScene(copy,true);SceneManager.SetActiveScene(source);}
        }
    }
}
