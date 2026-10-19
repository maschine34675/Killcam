using System;
using System.Linq;
using EFT;
using Killcam;
using Killcam.Recording;
using Killcam.Replay;
using UnityEngine;
using UnityEngine.Rendering;
using U = UnityEngine.Object;

internal static class Program
{
    private static int _checks;
    private static void Assert(bool value,string message) { if (!value) throw new Exception(message); }
    private static void Check(string name,Action body)
    {
        GameObject.RejectAdd=null; Renderer.RejectEnabled=null;
        try { body(); _checks++; Console.WriteLine("PASS " + name); }
        finally { GameObject.RejectAdd=null; Renderer.RejectEnabled=null; }
    }
    private static bool Near(Vector3 a,Vector3 b)=>(a-b).sqrMagnitude<0.00001f;
    private static GameObject Child(GameObject parent,string name)
    {
        var go=new GameObject(name); go.transform.SetParent(parent.transform,false); return go;
    }
    private static MeshRenderer Static(GameObject go,Mesh mesh=null)
    {
        go.AddComponent<MeshFilter>().sharedMesh=mesh??new Mesh();
        return go.AddComponent<MeshRenderer>();
    }
    private static GameObject Holder()
    {
        var holder=new GameObject("test holder"); holder.SetActive(false); return holder;
    }
    private static int Live()=>GameObject.All.Count(x=>!x.destroyed);
    private static BoneWindow Window()=>new()
    {
        Times=new[]{0f,1f}, RootPos=new[]{new Vector3(10,0,0),new Vector3(20,0,0)},
        RootRot=new[]{Quaternion.identity,Quaternion.identity},BoneCount=1,Paths=new[]{"skeleton"},
        BonePos=new[]{Vector3.zero,new Vector3(0,2,0)},BoneRot=new[]{Quaternion.identity,Quaternion.identity}
    };
    private static GameObject Source()
    {
        var source=new GameObject("source body"); source.AddComponent<NativeScript>();
        source.AddComponent<Animator>(); source.AddComponent<Collider>();
        Static(Child(source,"skeleton")); return source;
    }

    public static int Main()
    {
        try
        {
            Check("scaled root, hierarchy paths and inactive children",()=>
            {
                var parent=new GameObject("native parent"); parent.transform.localScale=new Vector3(2,3,4);
                parent.transform.localPosition=new Vector3(7,8,9);
                parent.transform.localRotation=new Quaternion(0,0,0.70710677f,0.70710677f);
                var source=Child(parent,"body"); source.layer=9; source.transform.localPosition=new Vector3(1,2,3);
                source.transform.localScale=new Vector3(0.5f,2,1); var bone=Child(source,"skeleton");
                bone.transform.localPosition=new Vector3(3,4,5); Static(bone); var hidden=Child(bone,"inactive"); hidden.SetActive(false);
                var holder=Holder(); var copy=PuppetVisualHierarchy.Create(source,holder.transform);
                Assert(Near(copy.transform.position,source.transform.position),"root world position changed");
                Assert(Near(copy.transform.lossyScale,source.transform.lossyScale),"scaled parent lost");
                Assert(Near(copy.transform.rotation*new Vector3(1,0,0),source.transform.rotation*new Vector3(1,0,0)),"root rotation changed");
                Assert(Near(copy.transform.Find("skeleton").localPosition,bone.transform.localPosition),"bone local pose changed");
                Assert(!copy.transform.Find("skeleton/inactive").gameObject.activeSelf&&copy.layer==9,"active/layer state changed");
                Assert(!copy.activeInHierarchy,"copy activated during assembly"); U.Destroy(holder); U.Destroy(parent);
            });
            Check("owned skin bones, rootBone, duplicate names and null slots",()=>
            {
                var source=Source(); var a=Child(source,"A");var b=Child(source,"B");
                var ba=Child(a,"joint");var bb=Child(b,"joint"); var skin=Child(source,"skin").AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh=new Mesh {blendShapeCount=2};skin.bones=new[]{ba.transform,null,bb.transform};skin.rootBone=a.transform;
                skin.SetBlendShapeWeight(0,17);skin.SetBlendShapeWeight(1,82);skin.quality=4;skin.updateWhenOffscreen=true;skin.skinnedMotionVectors=true;
                skin.localBounds=new Bounds{center=new Vector3(2,3,4),extents=new Vector3(5,6,7)};
                var holder=Holder();var copy=PuppetVisualHierarchy.Create(source,holder.transform);var result=copy.transform.Find("skin").GetComponent<SkinnedMeshRenderer>();
                Assert(ReferenceEquals(result.bones[0],copy.transform.Find("A/joint"))&&ReferenceEquals(result.bones[2],copy.transform.Find("B/joint")),"skin bone did not map by source identity");
                Assert(result.bones[1]==null&&ReferenceEquals(result.rootBone,copy.transform.Find("A")),"rootBone/null bone mapping wrong");
                Assert(ReferenceEquals(result.sharedMesh,skin.sharedMesh),"mesh asset copied/replaced");
                Assert(result.GetBlendShapeWeight(0)==17&&result.GetBlendShapeWeight(1)==82&&result.quality==4&&result.updateWhenOffscreen&&result.skinnedMotionVectors,"skin state lost");
                Assert(Near(result.localBounds.extents,skin.localBounds.extents),"skin bounds lost");
                Assert(ReferenceEquals(skin.bones[0],ba.transform)&&!ba.destroyed,"source skin mutated");U.Destroy(holder);
                Assert(!skin.sharedMesh.destroyed,"shared mesh destroyed");U.Destroy(source);
            });
            Check("appearance and independent renderer/material property blocks",()=>
            {
                var source=Source();var renderer=source.transform.Find("skeleton").GetComponent<MeshRenderer>();var material=new Material();
                renderer.sharedMaterials=new[]{material,material};renderer.enabled=false;renderer.forceRenderingOff=true;
                renderer.shadowCastingMode=ShadowCastingMode.ShadowsOnly;renderer.receiveShadows=true;renderer.allowOcclusionWhenDynamic=true;
                renderer.renderingLayerMask=47;renderer.sortingLayerID=12;renderer.sortingOrder=8;renderer.lightProbeUsage=2;renderer.reflectionProbeUsage=3;renderer.motionVectorGenerationMode=1;
                renderer.probeAnchor=source.transform;var block=new MaterialPropertyBlock();block.SetFloat("tint",0.25f);renderer.SetPropertyBlock(block);
                block.SetFloat("tint",0.8f);renderer.SetPropertyBlock(block,1);var holder=Holder();var copy=PuppetVisualHierarchy.Create(source,holder.transform);
                var result=copy.transform.Find("skeleton").GetComponent<MeshRenderer>();result.GetPropertyBlock(block);Assert(block.GetFloat("tint")==0.25f,"renderer property block lost");
                Assert(!result.HasMaterialOverride(0),"absent material override masked renderer property block");
                result.GetPropertyBlock(block,1);Assert(block.GetFloat("tint")==0.8f,"material property block lost");block.SetFloat("tint",9);result.SetPropertyBlock(block,1);
                renderer.GetPropertyBlock(block,1);Assert(block.GetFloat("tint")==0.8f,"source property block aliased");
                Assert(!result.enabled&&result.forceRenderingOff&&result.shadowCastingMode==ShadowCastingMode.On,"visibility/first-person shadow rules wrong");
                Assert(result.receiveShadows&&result.renderingLayerMask==47&&result.sortingLayerID==12&&result.sortingOrder==8&&result.lightProbeUsage==2&&result.reflectionProbeUsage==3&&result.motionVectorGenerationMode==1,"render flags lost");
                Assert(ReferenceEquals(result.probeAnchor,copy.transform)&&ReferenceEquals(result.sharedMaterials[0],material),"anchor/material ownership wrong");
                U.Destroy(holder);Assert(!material.destroyed&&renderer.shadowCastingMode==ShadowCastingMode.ShadowsOnly&&!renderer.enabled,"source appearance mutated");U.Destroy(source);
            });
            Check("LOD renderer ownership and intentionally empty unsupported level",()=>
            {
                var source=Source();var lod0=source.transform.Find("skeleton").GetComponent<MeshRenderer>();var lod1=Static(Child(source,"low"));
                var trail=Child(source,"effect").AddComponent<TrailRenderer>();var group=source.AddComponent<LODGroup>();
                group.SetLODs(new[]{new LOD(0.7f,new Renderer[]{lod0}){fadeTransitionWidth=0.2f},new LOD(0.2f,new Renderer[]{lod1}),new LOD(0.01f,new Renderer[]{trail})});
                group.size=5;group.localReferencePoint=new Vector3(1,2,3);group.fadeMode=2;group.animateCrossFading=true;
                var holder=Holder();var copy=PuppetVisualHierarchy.Create(source,holder.transform);var result=copy.GetComponent<LODGroup>();var levels=result.GetLODs();
                Assert(levels.Length==3&&levels[0].renderers.Length==1&&levels[1].renderers.Length==1&&levels[2].renderers.Length==0,"LOD membership/fallback changed");
                Assert(ReferenceEquals(levels[0].renderers[0],copy.transform.Find("skeleton").GetComponent<Renderer>()),"LOD0 retained native renderer");
                Assert(ReferenceEquals(levels[1].renderers[0],copy.transform.Find("low").GetComponent<Renderer>())&&levels[0].renderers[0]!=levels[1].renderers[0],"LOD1 mapping duplicates LOD0");
                Assert(levels[0].screenRelativeTransitionHeight==0.7f&&levels[0].fadeTransitionWidth==0.2f&&result.size==5&&result.fadeMode==2&&result.animateCrossFading&&Near(result.localReferencePoint,group.localReferencePoint),"LOD configuration lost");
                Assert(ReferenceEquals(group.GetLODs()[0].renderers[0],lod0),"source LOD mutated");U.Destroy(holder);U.Destroy(source);
            });
            Check("component allowlist and inactive construction without serialization",()=>
            {
                var source=Source();source.AddComponent<Light>();var holder=Holder();
                GameObject.RejectAdd=(go,type)=>go.activeInHierarchy;
                int calls=U.Instantiations;var copy=PuppetVisualHierarchy.Create(source,holder.transform);
                foreach(var component in copy.GetComponentsInChildren<Component>(true))
                    Assert(component is Transform||component is MeshFilter||component is MeshRenderer||component is SkinnedMeshRenderer||component is LODGroup,"native behavior copied");
                Assert(U.Instantiations==calls&&Debug.unityLogger.logEnabled,"serialization or global logger mutation occurred");
                U.Destroy(holder);Assert(source.GetComponent<NativeScript>()!=null&&!source.destroyed,"native source touched");U.Destroy(source);
            });
            Check("external skin bone refuses and cleans partial hierarchy",()=>
            {
                var source=Source();var external=new GameObject("native external bone");var skin=Child(source,"skin").AddComponent<SkinnedMeshRenderer>();
                skin.sharedMesh=new Mesh();skin.bones=new[]{external.transform};var holder=Holder();int before=Live();
                bool threw=false;try{PuppetVisualHierarchy.Create(source,holder.transform);}catch(InvalidOperationException){threw=true;}
                Assert(threw&&Live()==before&&holder.transform.childCount==0,"external bone failure leaked hierarchy");
                Assert(!external.destroyed&&!skin.sharedMesh.destroyed,"failure destroyed native dependency");U.Destroy(holder);U.Destroy(source);U.Destroy(external);
            });
            Check("active holder refuses before copying",()=>
            {
                var source=Source();var holder=new GameObject("active holder");int before=Live();bool threw=false;
                try{PuppetVisualHierarchy.Create(source,holder.transform);}catch(InvalidOperationException){threw=true;}
                Assert(threw&&Live()==before,"active holder admitted");U.Destroy(source);U.Destroy(holder);
            });
            Check("unsupported body falls back without an invisible puppet",()=>
            {
                var source=new GameObject("unsupported source");source.AddComponent<TrailRenderer>();var holder=Holder();int before=Live();
                bool threw=false;try{PuppetVisualHierarchy.Create(source,holder.transform);}catch(InvalidOperationException){threw=true;}
                Assert(threw&&Live()==before&&holder.transform.childCount==0,"unsupported body silently accepted or leaked");U.Destroy(source);U.Destroy(holder);
            });
            Check("component-copy failure cleans all partially created nodes",()=>
            {
                var source=Source();var holder=Holder();int before=Live();GameObject.RejectAdd=(go,type)=>type==typeof(MeshRenderer);
                bool threw=false;try{PuppetVisualHierarchy.Create(source,holder.transform);}catch(InvalidOperationException){threw=true;}
                Assert(threw&&Live()==before&&holder.transform.childCount==0,"component failure leaked own nodes");
                Assert(source.transform.Find("skeleton").GetComponent<MeshRenderer>().enabled,"component failure changed source");U.Destroy(source);U.Destroy(holder);
            });
            Check("puppet poses owned bones and idempotent disposal restores source",()=>
            {
                var source=Source();var disabled=Static(Child(source,"already hidden"));disabled.enabled=false;
                int before=Live();var original=source.transform.Find("skeleton");var puppet=GhostPuppet.TryCreate(source,null,Window());
                Assert(puppet!=null,"valid puppet failed");var holder=GameObject.All.Last(x=>x.name=="KillcamPuppet"&&!x.destroyed);
                var root=holder.transform.GetChild(0);puppet.Apply(0.5f);
                Assert(Near(root.position,new Vector3(15,0,0))&&Near(root.Find("skeleton").localPosition,new Vector3(0,1,0)),"recorded pose not applied");
                Assert(Near(original.localPosition,Vector3.zero)&&!original.GetComponent<Renderer>().enabled,"source bone moved or not hidden");
                puppet.Destroy();puppet.Destroy();puppet.Apply(0.8f);
                Assert(Live()==before&&!holder.activeSelf&&original.GetComponent<Renderer>().enabled&&!disabled.enabled,"dispose leaked/failed source restore");U.Destroy(source);
            });
            Check("failure before holder assignment leaves source unchanged",()=>
            {
                var source=Source();int before=Live();Assert(GhostPuppet.TryCreate(source,null,null)==null,"null window admitted");
                Assert(Live()==before&&source.transform.Find("skeleton").GetComponent<Renderer>().enabled,"early failure mutated source");U.Destroy(source);
            });
            Check("malformed recorded pose cleans constructed holder",()=>
            {
                var source=Source();var window=Window();window.RootPos=Array.Empty<Vector3>();int before=Live();
                Assert(GhostPuppet.TryCreate(source,null,window)==null&&Live()==before,"pose failure leaked holder");
                Assert(source.transform.Find("skeleton").GetComponent<Renderer>().enabled,"pose failure hid source");U.Destroy(source);
            });
            Check("partial source-hide failure rolls back enabled and disabled states",()=>
            {
                var source=Source();var second=Static(Child(source,"second"));var disabled=Static(Child(source,"disabled"));disabled.enabled=false;
                bool fail=true;Renderer.RejectEnabled=(r,value)=>ReferenceEquals(r,second)&&!value&&fail&&(fail=false)==false;
                int before=Live();Assert(GhostPuppet.TryCreate(source,null,Window())==null,"injected hide failure ignored");
                Assert(Live()==before&&source.transform.Find("skeleton").GetComponent<Renderer>().enabled&&second.enabled&&!disabled.enabled,"partial hide not restored");U.Destroy(source);
            });
            Check("partial weapon copy failure removes only owned weapon geometry",()=>
            {
                var source=Source();var weapon=new GameObject("native weapon");var material=new Material();var a=Static(Child(weapon,"part1"));var b=Static(Child(weapon,"part2"));a.sharedMaterials=b.sharedMaterials=new[]{material};
                var light=weapon.AddComponent<Light>();var player=new Player{PlayerBones=new PlayerBones{WeaponRoot=new Bone{Original=weapon.transform}}};
                var window=Window();window.WeaponPos=new[]{Vector3.zero,Vector3.one};window.WeaponValid=new[]{true,true};window.WeaponRot=new[]{Quaternion.identity,Quaternion.identity};
                int adds=0;GameObject.RejectAdd=(go,type)=>go.name=="PuppetWeaponMesh"&&type==typeof(MeshRenderer)&&++adds==2;
                int before=Live();var puppet=GhostPuppet.TryCreate(source,player,window);Assert(puppet!=null,"weapon copy failure should preserve unarmed body fallback");
                Assert(!GameObject.All.Any(x=>!x.destroyed&&x.name=="PuppetWeapon"),"partial weapon pivot leaked");
                Assert(!puppet.TryGetMuzzle(Quaternion.identity,out _,out _),"failed weapon retained muzzle");
                Assert(!a.enabled&&!b.enabled&&!light.enabled,"native weapon not hidden during replay");puppet.Destroy();
                Assert(Live()==before&&a.enabled&&b.enabled&&light.enabled&&!material.destroyed,"weapon cleanup damaged source/assets");U.Destroy(source);U.Destroy(weapon);
            });
            Console.WriteLine($"PUPPET_VISUAL_CHECKS_PASSED checks={_checks}");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("PUPPET_VISUAL_CHECKS_FAILED "+ex);return 1;}
    }
}
