using System;
using System.Collections.Generic;
using System.Linq;
using N = System.Numerics;

// Deterministic behavioral stand-ins, not a Unity renderer. Native API compatibility
// is checked by the separate real plugin build; visual acceptance requires EFT.
namespace UnityEngine
{
    public class Object
    {
        public bool destroyed;
        public static int Instantiations;
        public static void Destroy(Object obj)
        {
            if (obj == null || obj.destroyed) return;
            obj.destroyed = true;
            if (obj is GameObject go)
            {
                foreach (var child in go.transform.Children.ToArray()) Destroy(child.gameObject);
                foreach (var component in go.Components) component.destroyed = true;
                go.transform.SetParent(null, false);
            }
        }
        public static GameObject Instantiate(GameObject source)
        {
            Instantiations++;
            throw new InvalidOperationException("Forbidden native/script-bearing clone");
        }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public string name => gameObject.name;
        public T GetComponent<T>() where T : class => gameObject.GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool inactive) where T : class => gameObject.GetComponentsInChildren<T>(inactive);
    }
    public class GameObject : Object
    {
        public static readonly List<GameObject> All = new();
        public static Func<GameObject, Type, bool> RejectAdd;
        internal readonly List<Component> Components = new();
        public string name;
        public int layer;
        public Transform transform;
        public bool activeSelf = true;
        public bool activeInHierarchy => !destroyed && activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
        public GameObject(string name)
        {
            this.name = name;
            transform = new Transform { gameObject = this };
            Components.Add(transform); All.Add(this);
        }
        public void SetActive(bool value) => activeSelf = value;
        public T AddComponent<T>() where T : Component, new()
        {
            if (RejectAdd?.Invoke(this, typeof(T)) == true) throw new InvalidOperationException("Injected component creation failure");
            var result = new T { gameObject = this }; Components.Add(result); return result;
        }
        public T GetComponent<T>() where T : class => Components.OfType<T>().FirstOrDefault();
        public T[] GetComponentsInChildren<T>(bool inactive) where T : class
        {
            var result = new List<T>();
            void Walk(Transform node)
            {
                if (node.gameObject.destroyed || (!inactive && !node.gameObject.activeInHierarchy)) return;
                result.AddRange(node.gameObject.Components.OfType<T>());
                foreach (var child in node.Children) Walk(child);
            }
            Walk(transform); return result.ToArray();
        }
    }
    public class Transform : Component
    {
        internal readonly List<Transform> Children = new();
        public Transform parent;
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = Vector3.one;
        public int childCount => Children.Count;
        public Transform GetChild(int i) => Children[i];
        public Vector3 position => parent == null ? localPosition : parent.TransformPoint(localPosition);
        public Quaternion rotation => parent == null ? localRotation : parent.rotation * localRotation;
        public Vector3 lossyScale => parent == null ? localScale : Vector3.Mul(parent.lossyScale, localScale);
        public void SetParent(Transform next, bool worldPositionStays)
        {
            var p = position; var r = rotation; var s = lossyScale;
            parent?.Children.Remove(this); parent = next; parent?.Children.Add(this);
            if (worldPositionStays) { SetPositionAndRotation(p, r); localScale = parent == null ? s : Vector3.Div(s, parent.lossyScale); }
        }
        public void SetPositionAndRotation(Vector3 p, Quaternion r)
        {
            localPosition = parent == null ? p : parent.InverseTransformPoint(p);
            localRotation = parent == null ? r : Quaternion.Inverse(parent.rotation) * r;
        }
        public Vector3 TransformPoint(Vector3 point) => position + rotation * Vector3.Mul(lossyScale, point);
        public Vector3 InverseTransformPoint(Vector3 point) => Vector3.Div(Quaternion.Inverse(rotation) * (point - position), lossyScale);
        public Transform Find(string path)
        {
            Transform result = this;
            foreach (string name in path.Split('/')) { result = result.Children.FirstOrDefault(x => x.name == name); if (result == null) return null; }
            return result;
        }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public static Vector3 zero => new(); public static Vector3 one => new(1,1,1);
        public float sqrMagnitude => x*x+y*y+z*z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 Mul(Vector3 a, Vector3 b) => new(a.x*b.x,a.y*b.y,a.z*b.z);
        public static Vector3 Div(Vector3 a, Vector3 b) => new(a.x/b.x,a.y/b.y,a.z/b.z);
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => new(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t);
    }
    public struct Quaternion
    {
        internal N.Quaternion Value;
        public Quaternion(float x,float y,float z,float w) { Value=new(x,y,z,w); }
        public static Quaternion identity => new(0,0,0,1);
        public static Quaternion Inverse(Quaternion q) => new() { Value=N.Quaternion.Inverse(q.Value) };
        public static Quaternion Slerp(Quaternion a,Quaternion b,float t) => new() { Value=N.Quaternion.Slerp(a.Value,b.Value,t) };
        public static Quaternion operator *(Quaternion a,Quaternion b) => new() { Value=a.Value*b.Value };
        public static Vector3 operator *(Quaternion q,Vector3 v) { var r=N.Vector3.Transform(new N.Vector3(v.x,v.y,v.z),q.Value); return new(r.X,r.Y,r.Z); }
    }
    public struct Bounds { public Vector3 center, extents; }
    public static class Mathf
    {
        public static int Min(int a,int b)=>Math.Min(a,b);
        public static float Clamp01(float v)=>Math.Clamp(v,0f,1f);
        public static float Sqrt(float v)=>MathF.Sqrt(v);
    }
    public class Mesh : Object { public int blendShapeCount; }
    public class Material : Object { }
    public class MaterialPropertyBlock
    {
        internal Dictionary<string,float> Values=new();
        public bool isEmpty => Values.Count==0;
        public void Clear()=>Values.Clear();
        public void SetFloat(string key,float value)=>Values[key]=value;
        public float GetFloat(string key)=>Values.TryGetValue(key,out var value)?value:0;
        internal void Copy(MaterialPropertyBlock other) { Values=other == null?new():new(other.Values); }
    }
    public class Renderer : Component
    {
        public static Func<Renderer,bool,bool> RejectEnabled;
        private bool _enabled=true;
        public bool enabled { get=>_enabled; set { if (RejectEnabled?.Invoke(this,value)==true) throw new InvalidOperationException("Injected source hide failure"); _enabled=value; } }
        public bool forceRenderingOff,receiveShadows,allowOcclusionWhenDynamic;
        public uint renderingLayerMask;
        public int sortingLayerID,sortingOrder;
        public int lightProbeUsage,reflectionProbeUsage,motionVectorGenerationMode;
        public Rendering.ShadowCastingMode shadowCastingMode;
        public Material[] sharedMaterials=Array.Empty<Material>();
        public Transform probeAnchor;
        private readonly Dictionary<int,MaterialPropertyBlock> _blocks=new();
        public void GetPropertyBlock(MaterialPropertyBlock value,int materialIndex=-1) => value.Copy(_blocks.TryGetValue(materialIndex,out var block)?block:null);
        public bool HasMaterialOverride(int materialIndex) => _blocks.ContainsKey(materialIndex);
        public void SetPropertyBlock(MaterialPropertyBlock value,int materialIndex=-1) { if(value==null) { _blocks.Remove(materialIndex); return; } var copy=new MaterialPropertyBlock(); copy.Copy(value); _blocks[materialIndex]=copy; }
    }
    public class MeshRenderer : Renderer { }
    public class TrailRenderer : Renderer { }
    public class MeshFilter : Component { public Mesh sharedMesh; }
    public class SkinnedMeshRenderer : Renderer
    {
        public Mesh sharedMesh;
        public Transform[] bones=Array.Empty<Transform>();
        public Transform rootBone;
        public Bounds localBounds;
        public int quality;
        public bool updateWhenOffscreen,skinnedMotionVectors;
        private readonly Dictionary<int,float> _weights=new();
        public float GetBlendShapeWeight(int i)=>_weights.TryGetValue(i,out var value)?value:0;
        public void SetBlendShapeWeight(int i,float value)=>_weights[i]=value;
    }
    public class LODGroup : Component
    {
        public bool enabled=true,animateCrossFading;
        public int fadeMode;
        public Vector3 localReferencePoint;
        public float size;
        private LOD[] _lods=Array.Empty<LOD>();
        public LOD[] GetLODs()=>_lods;
        public void SetLODs(LOD[] lods)=>_lods=lods;
    }
    public struct LOD
    {
        public float screenRelativeTransitionHeight,fadeTransitionWidth;
        public Renderer[] renderers;
        public LOD(float height,Renderer[] renderers) { screenRelativeTransitionHeight=height; this.renderers=renderers; fadeTransitionWidth=0; }
    }
    public class Light : Component { public bool enabled=true; }
    public class NativeScript : Component { }
    public class Animator : Component { }
    public class Collider : Component { }
    public static class Debug { public static Logger unityLogger=new(); }
    public class Logger { public bool logEnabled=true; }
}
namespace UnityEngine.Rendering { public enum ShadowCastingMode { Off,On,TwoSided,ShadowsOnly } }
namespace EFT
{
    public class Bone { public UnityEngine.Transform Original; }
    public class PlayerBones { public Bone WeaponRoot; }
    public class Player { public PlayerBones PlayerBones; }
}
namespace Killcam
{
    public class Log
    {
        public readonly List<string> Errors=new();
        public void LogDebug(string value) { }
        public void LogError(string value)=>Errors.Add(value);
        public void LogWarning(string value)=>Errors.Add(value);
    }
    public class Plugin { public static readonly Plugin Instance=new(); public Log Logger=new(); }
}
namespace Killcam.Recording
{
    // Data boundary only; recording code is unchanged and not under this harness.
    public class BoneWindow
    {
        public float[] Times; public UnityEngine.Vector3[] RootPos,BonePos,WeaponPos,WeaponAnimPos;
        public UnityEngine.Quaternion[] RootRot,BoneRot,WeaponRot,WeaponAnimRot;
        public int BoneCount; public string[] Paths; public bool[] WeaponValid,WeaponAnimValid;
    }
}
namespace Killcam.Replay
{
    // Unchanged muzzle math is outside this test; real API/build retains its source.
    public static class WeaponMuzzle
    {
        public static void Encapsulate(ref UnityEngine.Bounds bounds,ref bool hasBounds,UnityEngine.Transform t,UnityEngine.Mesh mesh)=>hasBounds=true;
        public static bool TryGetMuzzle(UnityEngine.Transform pivot,UnityEngine.Bounds bounds,bool valid,UnityEngine.Quaternion aim,
            out UnityEngine.Vector3 p,out UnityEngine.Quaternion q) { p=default;q=default;return pivot!=null&&valid; }
    }
}
