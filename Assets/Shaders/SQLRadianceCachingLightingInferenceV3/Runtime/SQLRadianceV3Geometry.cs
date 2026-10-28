using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rackneh.InferenceLighting.SQLRadianceLiveV3
{
    // Analytic room geometry stays on the original inexpensive path. Only mesh
    // entities use a BVH. Its topology is built once; animation refits bounds.
    public sealed class SQLRadianceV3Geometry : IDisposable
    {
        public const int Capacity = 3200, Resolution = 8, PatchCount = 6 * Resolution * Resolution;
        [StructLayout(LayoutKind.Sequential)] public struct Vertex { public Vector4 position, normal, uv; }
        [StructLayout(LayoutKind.Sequential)] public struct Node { public Vector4 min, max; }
        public sealed class Entry
        {
            public Renderer renderer;
            public Material material;
            public Mesh mesh, baked;
            public int submesh, shape, start, count, root = -1, end;
            public Matrix4x4 matrix;
            public Bounds bounds;
            public readonly List<Vector3> positions = new List<Vector3>(), normals = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
        }
        public readonly List<Entry> Entries = new List<Entry>();
        public readonly Vector4[] Entities = new Vector4[Capacity * 6];
        public readonly Vector4[] Metadata = new Vector4[Capacity]; // sample start, root, end, layer
        public readonly Vector4[] SurfaceFlags = new Vector4[Capacity]; // enabled, casts, receives, unused
        public Vertex[] Vertices { get; private set; }
        public int Count => Entries.Count;
        public int SampleCount => Vertices.Length;
        public int MeshTriangleCount => triangles.Count;
        public ComputeBuffer VertexBuffer { get; private set; }
        public ComputeBuffer NodeBuffer { get; private set; }
        public ComputeBuffer TriangleBuffer { get; private set; }
        readonly List<Vector4> triangles = new List<Vector4>();
        readonly List<Node> nodes = new List<Node>();
        readonly List<int> escapes = new List<int>();
        readonly List<Vertex> staging = new List<Vertex>();
        readonly Dictionary<SkinnedMeshRenderer,Mesh> skinSnapshots = new Dictionary<SkinnedMeshRenderer,Mesh>();
        readonly HashSet<SkinnedMeshRenderer> updatedSkins = new HashSet<SkinnedMeshRenderer>();

        public SQLRadianceV3Geometry(Renderer[] renderers, int maxMeshVertices = 65536)
        {
            foreach (var renderer in renderers)
            {
                if (!renderer || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var skin = renderer as SkinnedMeshRenderer;
                Mesh mesh = skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (!mesh) continue;
                var materials = renderer.sharedMaterials;
                for (int sub = 0; sub < Math.Min(mesh.subMeshCount, materials.Length); sub++)
                {
                    if (Entries.Count == Capacity)
                    {
                        Debug.LogWarning("[SQL V3] 32 surface slots reached. Split the scene or reduce submeshes; additional surfaces are excluded.");
                        break;
                    }
                    int shape = skin ? 4 : Shape(mesh, renderer.transform.lossyScale);
                    if (shape == 4 && staging.Count + mesh.vertexCount > maxMeshVertices + Capacity * PatchCount)
                    {
                        Debug.LogWarning($"[SQL V3] Mesh vertex budget exceeded: {renderer.name} is excluded.", renderer); continue;
                    }
                    var e = new Entry { renderer = renderer, mesh = mesh, material = materials[sub], submesh = sub,
                        shape = shape, start = staging.Count, bounds = mesh.bounds };
                    if (skin)
                    {
                        if(!skinSnapshots.TryGetValue(skin,out e.baked))
                        {
                            e.baked=new Mesh { name="SQL V3 skin snapshot", hideFlags=HideFlags.HideAndDontSave };
                            skin.BakeMesh(e.baked); skinSnapshots.Add(skin,e.baked);
                        }
                    }
                    try
                    {
                        if (shape == 4)
                        {
                            var source = e.baked ? e.baked : mesh;
                            source.GetVertices(e.positions); source.GetNormals(e.normals); mesh.GetUVs(0, e.uvs);
                        }
                        else BuildPatches(e);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[SQL V3] Cannot read {renderer.name}. Enable Read/Write on this mesh. {ex.Message}", renderer);
                        continue;
                    }
                    int index = Entries.Count;
                    e.count = e.positions.Count;
                    for (int v = 0; v < e.count; v++) staging.Add(default);
                    Entries.Add(e);
                    UpdateVertices(e, index);
                    if (shape == 4)
                    {
                        int first = triangles.Count;
                        var indices = (e.baked ? e.baked : mesh).GetTriangles(sub);
                        for (int t = 0; t < indices.Length; t += 3)
                            triangles.Add(new Vector4(e.start + indices[t], e.start + indices[t+1], e.start + indices[t+2], index));
                        if (triangles.Count > first) { e.root = BuildNode(first, triangles.Count - first); e.end = nodes.Count; }
                    }
                    renderer.GetPropertyBlock(e.properties, sub);
                    e.properties.SetFloat("_SQL3EntityIndex", index + 1);
                    e.properties.SetFloat("_SQL3VertexOffset", e.start);
                    renderer.SetPropertyBlock(e.properties, sub);
                }
            }
            Vertices = staging.ToArray();
            VertexBuffer = new ComputeBuffer(Math.Max(1, SampleCount), 48);
            NodeBuffer = new ComputeBuffer(Math.Max(1, nodes.Count), 32);
            TriangleBuffer = new ComputeBuffer(Math.Max(1, triangles.Count), 16);
            VertexBuffer.SetData(SampleCount > 0 ? Vertices : new Vertex[1]);
            TriangleBuffer.SetData(triangles.Count > 0 ? triangles.ToArray() : new Vector4[1]);
            Sync(true);
        }

        static int Shape(Mesh mesh, Vector3 scale)
        {
            string name = mesh.name;
            if (name == "Cube") return 1;
            if (name == "Plane" || name == "Quad") return 2;
            if (name == "Sphere" && Mathf.Abs(Mathf.Abs(scale.x)-Mathf.Abs(scale.y)) < .001f && Mathf.Abs(Mathf.Abs(scale.x)-Mathf.Abs(scale.z)) < .001f) return 0;
            if (name == "Capsule" && Mathf.Abs(Mathf.Abs(scale.x)-Mathf.Abs(scale.z)) < .001f) return 3;
            // Skinned and custom meshes retain their actual silhouette and UVs.
            return 4;
        }

        static void BuildPatches(Entry e)
        {
            var meshPositions=e.mesh.vertices; var meshUV=e.mesh.uv; var meshIndices=e.mesh.GetTriangles(e.submesh);
            for (int face = 0; face < 6; face++)
            for (int y = 0; y < Resolution; y++)
            for (int x = 0; x < Resolution; x++)
            {
                Vector2 uv = new Vector2((x+.5f)/Resolution, (y+.5f)/Resolution);
                Vector3 q = FacePoint(face, uv * 2 - Vector2.one), n = FaceNormal(face);
                Vector3 p;
                if (e.shape == 0) { n = q.normalized; p = n * e.bounds.extents.x; }
                else if(e.shape==3)
                {
                    float angle=(uv.x-.5f)*Mathf.PI*2, height=(uv.y*2-1)*e.bounds.extents.y;
                    float radius=e.bounds.extents.x, halfCylinder=Mathf.Max(0,e.bounds.extents.y-radius);
                    float cap=Mathf.Max(0,Mathf.Abs(height)-halfCylinder), radial=Mathf.Sqrt(Mathf.Max(0,radius*radius-cap*cap));
                    p=new Vector3(Mathf.Cos(angle)*radial,height,Mathf.Sin(angle)*radial);
                    n=new Vector3(p.x,Mathf.Sign(height)*cap,p.z).normalized;
                }
                else if (e.shape == 2)
                {
                    // Quad uses its local XY frame; Plane uses local XZ.
                    bool quad = e.mesh.name == "Quad";
                    p = quad ? new Vector3((uv.x*2-1)*e.bounds.extents.x, (uv.y*2-1)*e.bounds.extents.y, 0)
                        : new Vector3((uv.x*2-1)*e.bounds.extents.x, 0, (uv.y*2-1)*e.bounds.extents.z);
                    n = quad ? Vector3.back : Vector3.up;
                    if (face == 3) n = -n;
                }
                else p = Vector3.Scale(q, e.bounds.extents);
                p+=e.bounds.center;
                e.positions.Add(p); e.normals.Add(n);
                e.uvs.Add(ProjectUV(p,n,uv,meshPositions,meshUV,meshIndices));
            }
        }
        static Vector2 ProjectUV(Vector3 position,Vector3 normal,Vector2 fallback,Vector3[] positions,Vector2[] uv,int[] indices)
        {
            if(uv.Length!=positions.Length)return fallback;
            Vector3 origin=position+normal*.02f, direction=-normal; float nearest=float.PositiveInfinity;
            Vector2 result=fallback;
            for(int t=0;t<indices.Length;t+=3)
            {
                int ia=indices[t],ib=indices[t+1],ic=indices[t+2];
                Vector3 edge1=positions[ib]-positions[ia],edge2=positions[ic]-positions[ia],p=Vector3.Cross(direction,edge2);
                float det=Vector3.Dot(edge1,p); if(Mathf.Abs(det)<1e-9f)continue;
                Vector3 s=origin-positions[ia]; float u=Vector3.Dot(s,p)/det; Vector3 q=Vector3.Cross(s,edge1);
                float v=Vector3.Dot(direction,q)/det,d=Vector3.Dot(edge2,q)/det;
                if(u<-.0001f || v<-.0001f || u+v>1.0001f || d<0 || d>=nearest)continue;
                nearest=d; result=uv[ia]*(1-u-v)+uv[ib]*u+uv[ic]*v;
            }
            return result;
        }
        public static Vector3 FacePoint(int face, Vector2 uv)
        {
            switch (face) { case 0:return new Vector3(1,uv.y,uv.x); case 1:return new Vector3(-1,uv.y,uv.x);
                case 2:return new Vector3(uv.x,1,uv.y); case 3:return new Vector3(uv.x,-1,uv.y);
                case 4:return new Vector3(uv.x,uv.y,1); default:return new Vector3(uv.x,uv.y,-1); }
        }
        static Vector3 FaceNormal(int face) => face == 0 ? Vector3.right : face == 1 ? Vector3.left : face == 2 ? Vector3.up : face == 3 ? Vector3.down : face == 4 ? Vector3.forward : Vector3.back;

        void UpdateVertices(Entry e, int entity)
        {
            e.matrix = e.renderer.localToWorldMatrix;
            var normalMatrix = e.matrix.inverse.transpose;
            for (int v = 0; v < e.count; v++)
            {
                Vector3 normal = v < e.normals.Count ? e.normals[v] : Vector3.up;
                Vector2 uv = v < e.uvs.Count ? e.uvs[v] : Vector2.zero;
                var data = new Vertex { position = V(e.matrix.MultiplyPoint3x4(e.positions[v]), entity),
                    normal = V(normalMatrix.MultiplyVector(normal).normalized, 0), uv = new Vector4(uv.x,uv.y,0,0) };
                if (Vertices == null) staging[e.start+v] = data; else Vertices[e.start+v] = data;
            }
        }

        public bool Sync(bool force = false)
        {
            bool changed = force;
            updatedSkins.Clear();
            for (int i = 0; i < Count; i++)
            {
                var e = Entries[i];
                bool active = e.renderer && e.renderer.enabled && e.renderer.gameObject.activeInHierarchy;
                SurfaceFlags[i] = new Vector4(active ? 1 : 0, active && e.renderer.shadowCastingMode != ShadowCastingMode.Off ? 1 : 0,
                    active && e.renderer.receiveShadows ? 1 : 0, 0);
                if (!active) { Entities[i*6] = new Vector4(0,0,0,-1); continue; }
                var skin = e.renderer as SkinnedMeshRenderer;
                // A mesh swap is picked up by registry discovery. Hide this old slot
                // until then; indexing a new topology with the old BVH is invalid.
                if(skin && skin.sharedMesh!=e.mesh) { SurfaceFlags[i]=Vector4.zero; Entities[i*6]=new Vector4(0,0,0,-1); continue; }
                if (force || skin || e.matrix != e.renderer.localToWorldMatrix)
                {
                    if (skin)
                    {
                        if(updatedSkins.Add(skin))skin.BakeMesh(e.baked);
                        e.baked.GetVertices(e.positions); e.baked.GetNormals(e.normals);
                    }
                    UpdateVertices(e, i);
                    VertexBuffer.SetData(Vertices, e.start, e.start, e.count);
                    if (e.root >= 0) Refit(e.root, e.end);
                    changed = true;
                }
                var tr = e.renderer.transform;
                Vector3 scale = tr.lossyScale; scale = new Vector3(Mathf.Abs(scale.x),Mathf.Abs(scale.y),Mathf.Abs(scale.z));
                Vector3 ext = Vector3.Scale(e.bounds.extents, scale), right = tr.right, up = tr.up;
                if (e.shape == 2 && e.mesh.name == "Quad") { ext = new Vector3(ext.x, .001f, ext.y); up = -tr.forward; }
                Entities[i*6] = V(tr.TransformPoint(e.bounds.center), e.shape);
                Entities[i*6+1] = V(ext, Float(e.material,"_TexturePattern",0));
                Color color = ColorProperty(e.material,"_BaseColor",ColorProperty(e.material,"_Color",Color.white));
                Entities[i*6+2] = new Vector4(color.r,color.g,color.b,1-Float(e.material,"_Smoothness",.5f));
                Color emission = ColorProperty(e.material,"_EmissionColor",Color.black);
                Entities[i*6+3] = new Vector4(Float(e.material,"_Metallic",0),emission.r,emission.g,emission.b);
                Entities[i*6+4] = V(right,0); Entities[i*6+5] = V(up,0);
                Metadata[i] = new Vector4(e.start,e.root,e.end,e.renderer.gameObject.layer);
            }
            if (changed && nodes.Count > 0) NodeBuffer.SetData(nodes);
            return changed;
        }

        Vector3 Position(int index) => Vertices == null ? (Vector3)staging[index].position : (Vector3)Vertices[index].position;
        Bounds TriangleBounds(Vector4 t) { var b = new Bounds(Position((int)t.x), Vector3.zero); b.Encapsulate(Position((int)t.y)); b.Encapsulate(Position((int)t.z)); return b; }
        int BuildNode(int first, int count)
        {
            int index = nodes.Count; nodes.Add(default); escapes.Add(0);
            Bounds bounds = TriangleBounds(triangles[first]);
            for (int i=first+1; i<first+count; i++) bounds.Encapsulate(TriangleBounds(triangles[i]));
            if (count <= 4) nodes[index] = new Node { min=V(bounds.min,first), max=V(bounds.max,-count) };
            else
            {
                Vector3 size = bounds.size; int axis = size.x > size.y && size.x > size.z ? 0 : size.y > size.z ? 1 : 2;
                triangles.Sort(first,count,Comparer<Vector4>.Create((a,b)=>TriangleBounds(a).center[axis].CompareTo(TriangleBounds(b).center[axis])));
                int half=count/2; BuildNode(first,half); int right=BuildNode(first+half,count-half);
                nodes[index] = new Node { min=V(bounds.min,right), max=V(bounds.max,0) };
            }
            escapes[index] = nodes.Count;
            // Internal nodes jump past their entire subtree on a bounds miss.
            if (nodes[index].max.w == 0) { var n=nodes[index]; n.max.w=nodes.Count; nodes[index]=n; }
            return index;
        }
        void Refit(int start, int end)
        {
            for (int i=end-1;i>=start;i--)
            {
                var n=nodes[i]; Bounds b;
                if (n.max.w < 0)
                {
                    int first=(int)n.min.w, count=-(int)n.max.w; b=TriangleBounds(triangles[first]);
                    for(int t=1;t<count;t++) b.Encapsulate(TriangleBounds(triangles[first+t]));
                }
                else { var left=nodes[i+1]; var right=nodes[(int)n.min.w]; b=new Bounds(); b.SetMinMax(left.min,left.max); b.Encapsulate((Vector3)right.min); b.Encapsulate((Vector3)right.max); }
                n.min=V(b.min,n.min.w); n.max=V(b.max,n.max.w); nodes[i]=n;
            }
        }
        public void Bind(ComputeShader shader = null, int kernel = 0)
        {
            if (shader)
            {
                shader.SetVectorArray("_SQL3Entities",Entities); shader.SetVectorArray("_SQL3Metadata",Metadata); shader.SetVectorArray("_SQL3SurfaceFlags",SurfaceFlags);
                shader.SetInt("_SQL3EntityCount",Count); shader.SetInt("_SQL3HasMeshes",triangles.Count>0 ? 1:0); shader.SetBuffer(kernel,"_SQL3Vertices",VertexBuffer);
                shader.SetBuffer(kernel,"_SQL3Nodes",NodeBuffer); shader.SetBuffer(kernel,"_SQL3Triangles",TriangleBuffer);
            }
            else
            {
                Shader.SetGlobalVectorArray("_SQL3Entities",Entities); Shader.SetGlobalVectorArray("_SQL3Metadata",Metadata); Shader.SetGlobalVectorArray("_SQL3SurfaceFlags",SurfaceFlags);
                Shader.SetGlobalInt("_SQL3EntityCount",Count); Shader.SetGlobalInt("_SQL3HasMeshes",triangles.Count>0 ? 1:0); Shader.SetGlobalBuffer("_SQL3Vertices",VertexBuffer);
                Shader.SetGlobalBuffer("_SQL3Nodes",NodeBuffer); Shader.SetGlobalBuffer("_SQL3Triangles",TriangleBuffer);
            }
        }
        public static float Float(Material m,string name,float fallback) => m && m.HasProperty(name) ? m.GetFloat(name) : fallback;
        public static Color ColorProperty(Material m,string name,Color fallback) => m && m.HasProperty(name) ? m.GetColor(name) : fallback;
        public static Vector4 V(Vector3 v,float w) => new Vector4(v.x,v.y,v.z,w);
        public void Dispose()
        {
            foreach(var e in Entries)
            {
                if(e.renderer) { e.renderer.GetPropertyBlock(e.properties,e.submesh); e.properties.SetFloat("_SQL3EntityIndex",0); e.renderer.SetPropertyBlock(e.properties,e.submesh); }
            }
            foreach(var snapshot in skinSnapshots.Values)if(snapshot)UnityEngine.Object.DestroyImmediate(snapshot);
            VertexBuffer?.Release(); NodeBuffer?.Release(); TriangleBuffer?.Release();
        }
    }
}
