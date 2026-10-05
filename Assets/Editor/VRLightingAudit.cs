using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Quality > Audit Lighting And Render  (read-only)
///
/// Reports what the phone is ACTUALLY rendering with, which is not always what the settings
/// windows appear to say.
///
/// Two things make that gap easy to fall into in URP. First, the quality levels in
/// Project Settings no longer own anti-aliasing, shadows or shadow distance - the URP asset does,
/// and the ones still shown in the Quality window are ignored. Second, a project can hold several
/// URP assets and only one of them is live; editing the others changes nothing at all and leaves
/// no trace that it did nothing. So this resolves the active asset the same way the runtime does -
/// the quality level's own override if it has one, otherwise the Graphics Settings default - and
/// reports that one.
///
/// The rest is what a lighting pass needs before it can decide anything: which lights exist and
/// in what mode, whether anything is marked static, whether the meshes even have the second UV
/// set that baking requires, and what it would all cost.
/// </summary>
public static class VRLightingAudit
{
    [MenuItem("Tools/Emergency VR/Quality/Audit Lighting And Render")]
    public static void Audit()
    {
        var sb = new StringBuilder("[Quality] audit\n\n");

        ReportPipeline(sb);
        ReportAmbient(sb);
        ReportLights(sb);
        ReportBakeReadiness(sb);
        ReportProbes(sb);
        ReportCost(sb);
        ReportVolumes(sb);

        Debug.Log(sb.ToString());
    }

    // ------------------------------------------------------------------ pipeline

    static UniversalRenderPipelineAsset ActiveAsset(out string how)
    {
        // This is the resolution order the runtime uses. Asking for it rather than assuming is
        // the whole point of the audit.
        var perLevel = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;

        if (perLevel != null)
        {
            how = $"quality level '{QualitySettings.names[QualitySettings.GetQualityLevel()]}' overrides it";
            return perLevel;
        }

        how = "Graphics Settings default (no per-level override)";
        return GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
    }

    static void ReportPipeline(StringBuilder sb)
    {
        sb.AppendLine("RENDER PIPELINE");

        int level = QualitySettings.GetQualityLevel();
        sb.AppendLine($"  quality level in editor   {QualitySettings.names[level]}  (index {level})");
        sb.AppendLine("  android default level     see Project Settings > Quality > the green tick on Android");

        string how;
        var asset = ActiveAsset(out how);

        if (asset == null)
        {
            sb.AppendLine("  ACTIVE ASSET              none - the project is not on URP?");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"  ACTIVE ASSET              {asset.name}");
        sb.AppendLine($"                            ({how})");

        // List the others so it is obvious which ones are being edited for nothing.
        var all = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset", new[] { "Assets" })
                               .Select(AssetDatabase.GUIDToAssetPath)
                               .Select(AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>)
                               .Where(a => a != null)
                               .ToList();

        foreach (var other in all)
        {
            if (other == asset)
                sb.AppendLine($"    * {other.name}   <-- live");
            else
                sb.AppendLine($"      {other.name}       (not used - editing this changes nothing)");
        }

        var so = new SerializedObject(asset);

        // Read through the typed properties, not SerializedObject.
        //
        // m_MSAA and the shadowmap resolutions are serialized as ENUMS whose values are 1/2/4/8
        // and 256/512/1024/2048, but whose SerializedProperty reports the enum's POSITION in the
        // list - so 4x MSAA reads back as "2" and a 2048 map reads back as "3". A report that is
        // off by that much is worse than no report, because it looks authoritative.
        sb.AppendLine();
        sb.AppendLine($"    MSAA                    {asset.msaaSampleCount}x");
        sb.AppendLine($"    render scale            {asset.renderScale:0.00}");
        sb.AppendLine($"    HDR                     {OnOff(asset.supportsHDR)}");
        sb.AppendLine($"    depth texture           {OnOff(asset.supportsCameraDepthTexture)}");
        sb.AppendLine($"    opaque texture          {OnOff(asset.supportsCameraOpaqueTexture)}");
        sb.AppendLine();
        sb.AppendLine($"    main light shadows      {OnOff(asset.supportsMainLightShadows)}" +
                      $"   map {asset.mainLightShadowmapResolution}");
        sb.AppendLine($"    shadow distance         {asset.shadowDistance:0} m");
        sb.AppendLine($"    cascades                {asset.shadowCascadeCount}" +
                      "        <- each one is another full pass over the scene, per eye");
        sb.AppendLine($"    soft shadow quality     {SoftName(Read(so, "m_SoftShadowQuality"))}");
        sb.AppendLine($"    extra light shadows     {OnOff(asset.supportsAdditionalLightShadows)}");
        sb.AppendLine($"    extra lights per object {asset.maxAdditionalLightsCount}");
        sb.AppendLine($"    LOD cross fade          {OnOff(asset.enableLODCrossFade)}");
        sb.AppendLine();
        sb.AppendLine($"    texture mipmap limit    {QualitySettings.globalTextureMipmapLimit}   (0 = full res)");
        sb.AppendLine($"    anisotropic             {QualitySettings.anisotropicFiltering}");
        sb.AppendLine($"    LOD bias                {QualitySettings.lodBias:0.0}");
        sb.AppendLine($"    realtime reflections    {QualitySettings.realtimeReflectionProbes}");
        sb.AppendLine();
    }

    static string SoftName(string v)
    {
        switch (v)
        {
            case "0": return "off / per-light";
            case "1": return "Low";
            case "2": return "Medium";
            case "3": return "High";
            default:  return v;
        }
    }

    // ------------------------------------------------------------------ ambient

    static void ReportAmbient(StringBuilder sb)
    {
        sb.AppendLine("AMBIENT AND SKY");
        sb.AppendLine($"  ambient source            {RenderSettings.ambientMode}");

        if (RenderSettings.ambientMode == AmbientMode.Trilight)
        {
            sb.AppendLine($"    sky / equator / ground  {Hex(RenderSettings.ambientSkyColor)} / " +
                          $"{Hex(RenderSettings.ambientEquatorColor)} / {Hex(RenderSettings.ambientGroundColor)}");
        }
        else if (RenderSettings.ambientMode == AmbientMode.Flat)
        {
            sb.AppendLine($"    colour                  {Hex(RenderSettings.ambientLight)}");
        }

        sb.AppendLine($"  ambient intensity         {RenderSettings.ambientIntensity:0.00}");
        sb.AppendLine($"  reflection source         {RenderSettings.defaultReflectionMode}");
        sb.AppendLine($"  reflection intensity      {RenderSettings.reflectionIntensity:0.00}");
        sb.AppendLine($"  skybox material           {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "none")}");
        sb.AppendLine($"  fog                       {(RenderSettings.fog ? $"on, {RenderSettings.fogMode}, {Hex(RenderSettings.fogColor)}" : "off")}");
        sb.AppendLine();
    }

    static string Hex(Color c)
    {
        return "#" + ColorUtility.ToHtmlStringRGB(c);
    }

    // ------------------------------------------------------------------ lights

    static void ReportLights(StringBuilder sb)
    {
        var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include,
                                                     FindObjectsSortMode.None);

        sb.AppendLine($"LIGHTS  ({lights.Length} in the scene)");

        int realtimeOn = 0, shadowCasters = 0;

        foreach (var l in lights.OrderByDescending(l => l.type == LightType.Directional)
                                .ThenBy(l => l.name))
        {
            bool live = l.isActiveAndEnabled;
            bool realtime = l.lightmapBakeType == LightmapBakeType.Realtime;

            if (live && realtime) realtimeOn++;
            if (live && l.shadows != LightShadows.None) shadowCasters++;

            string range = l.type == LightType.Directional
                ? $"angle {l.transform.eulerAngles.x:0}/{l.transform.eulerAngles.y:0}"
                : $"range {l.range:0.0}";

            sb.AppendLine($"  {(live ? " " : "-")} {l.name,-26} {l.type,-11} {l.lightmapBakeType,-8} " +
                          $"int {l.intensity,5:0.00}  {range,-18} shadows {l.shadows}");
        }

        sb.AppendLine();
        sb.AppendLine($"  active realtime lights    {realtimeOn}");
        sb.AppendLine($"  casting shadows           {shadowCasters}");
        sb.AppendLine();
    }

    // ------------------------------------------------------------------ bake readiness

    static void ReportBakeReadiness(StringBuilder sb)
    {
        sb.AppendLine("BAKE READINESS");

        sb.AppendLine($"  lightmaps in scene        {LightmapSettings.lightmaps.Length}" +
                      $"   mode {LightmapSettings.lightmapsMode}");

        var renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude,
                                                               FindObjectsSortMode.None);

        int contributeGI = 0, lacksUv2 = 0, importerCanGenerate = 0, importerCannot = 0;

        var modelsMissingUv2 = new HashSet<string>();

        foreach (var r in renderers)
        {
            var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
            bool gi = (flags & StaticEditorFlags.ContributeGI) != 0;
            if (gi) contributeGI++;

            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;

            // uv2 is the lightmap UV set. Without it a renderer cannot receive a lightmap at all,
            // however carefully the bake is configured - it is the single reason a bake comes back
            // with half the city black.
            if (mesh.uv2 != null && mesh.uv2.Length > 0) continue;

            lacksUv2++;

            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path) || modelsMissingUv2.Contains(path)) continue;

            modelsMissingUv2.Add(path);

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null) importerCanGenerate++;
            else importerCannot++;
        }

        sb.AppendLine($"  mesh renderers            {renderers.Length}");
        sb.AppendLine($"  marked Contribute GI      {contributeGI}");
        sb.AppendLine($"  WITHOUT lightmap UVs      {lacksUv2}   (across {modelsMissingUv2.Count} source files)");
        sb.AppendLine($"    of those, model files   {importerCanGenerate}  - Generate Lightmap UVs can be switched on");
        sb.AppendLine($"    not model files         {importerCannot}  - would need UVs authored");

        if (modelsMissingUv2.Count > 0)
        {
            sb.AppendLine("    first few:");
            foreach (var p in modelsMissingUv2.Take(8))
                sb.AppendLine($"      {p}");
        }

        sb.AppendLine();
    }

    // ------------------------------------------------------------------ probes

    static void ReportProbes(StringBuilder sb)
    {
        sb.AppendLine("PROBES");

        var groups = Object.FindObjectsByType<LightProbeGroup>(FindObjectsInactive.Include,
                                                               FindObjectsSortMode.None);

        int probes = groups.Sum(g => g.probePositions != null ? g.probePositions.Length : 0);

        sb.AppendLine($"  light probe groups        {groups.Length}, {probes} probes total");

        if (probes == 0)
        {
            sb.AppendLine("    none - so every moving thing (cars, people, animals) is lit by the");
            sb.AppendLine("    sun and flat ambient only, identically wherever it stands. That is");
            sb.AppendLine("    what makes moving objects look pasted onto a scene rather than in it.");
        }

        var reflections = Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include,
                                                                    FindObjectsSortMode.None);

        sb.AppendLine($"  reflection probes         {reflections.Length}");

        foreach (var p in reflections.Take(12))
            sb.AppendLine($"      {p.name,-26} {p.mode,-8} res {p.resolution}  " +
                          $"refresh {p.refreshMode}");

        sb.AppendLine();
    }

    // ------------------------------------------------------------------ cost

    static void ReportCost(StringBuilder sb)
    {
        sb.AppendLine("COST");

        var mrs = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude,
                                                         FindObjectsSortMode.None);

        var skinned = Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude,
                                                                     FindObjectsSortMode.None);

        long tris = 0;
        int submeshes = 0, casting = 0;
        var materials = new HashSet<Material>();

        foreach (var r in mrs)
        {
            if (r.shadowCastingMode != ShadowCastingMode.Off) casting++;

            foreach (var m in r.sharedMaterials) if (m != null) materials.Add(m);

            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;

            submeshes += mesh.subMeshCount;

            // GetIndexCount rather than mesh.triangles: the second one allocates a copy of every
            // index in the city just to count them.
            for (int i = 0; i < mesh.subMeshCount; i++) tris += (long)mesh.GetIndexCount(i) / 3;
        }

        foreach (var r in skinned)
        {
            foreach (var m in r.sharedMaterials) if (m != null) materials.Add(m);
            if (r.sharedMesh == null) continue;

            submeshes += r.sharedMesh.subMeshCount;
            for (int i = 0; i < r.sharedMesh.subMeshCount; i++)
                tris += (long)r.sharedMesh.GetIndexCount(i) / 3;
        }

        sb.AppendLine($"  mesh renderers            {mrs.Length}");
        sb.AppendLine($"  skinned renderers         {skinned.Length}   (each one animates on the CPU every frame)");
        sb.AppendLine($"  submeshes (draw calls)    {submeshes}");
        sb.AppendLine($"  unique materials          {materials.Count}");
        sb.AppendLine($"  triangles                 {tris:N0}");
        sb.AppendLine($"  casting shadows           {casting} of {mrs.Length}");
        sb.AppendLine();
        sb.AppendLine("  Everything above is drawn TWICE - once per eye. Read every number as double.");
        sb.AppendLine();
    }

    // ------------------------------------------------------------------ post

    static void ReportVolumes(StringBuilder sb)
    {
        sb.AppendLine("POST PROCESSING");

        var volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include,
                                                       FindObjectsSortMode.None);

        if (volumes.Length == 0)
        {
            sb.AppendLine("  no volumes in the scene");
            sb.AppendLine();
            return;
        }

        foreach (var v in volumes)
        {
            sb.AppendLine($"  {v.name}   global {v.isGlobal}, weight {v.weight:0.00}, " +
                          $"priority {v.priority:0}, {(v.isActiveAndEnabled ? "on" : "off")}");

            if (v.sharedProfile == null) { sb.AppendLine("      no profile"); continue; }

            foreach (var c in v.sharedProfile.components)
                sb.AppendLine($"      {c.GetType().Name,-28} {(c.active ? "active" : "off")}");
        }

        sb.AppendLine();
    }

    // ------------------------------------------------------------------ helpers

    static string Read(SerializedObject so, string prop)
    {
        var p = so.FindProperty(prop);
        if (p == null) return "?";

        switch (p.propertyType)
        {
            case SerializedPropertyType.Integer: return p.intValue.ToString();
            case SerializedPropertyType.Boolean: return p.boolValue ? "1" : "0";
            case SerializedPropertyType.Float:   return p.floatValue.ToString("0.00");
            case SerializedPropertyType.Enum:    return p.enumValueIndex.ToString();
            default:                             return p.propertyType.ToString();
        }
    }

    static float ReadFloat(SerializedObject so, string prop)
    {
        var p = so.FindProperty(prop);
        return p != null ? p.floatValue : 0f;
    }

    static string Bool(string v)
    {
        return v == "1" ? "on" : v == "0" ? "off" : v;
    }

    static string OnOff(bool v)
    {
        return v ? "on" : "off";
    }
}
