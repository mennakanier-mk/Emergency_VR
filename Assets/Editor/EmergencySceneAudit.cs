using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// Read-only inspection of the loaded scene; output stays in Library.
[InitializeOnLoad]
public static class EmergencySceneAudit
{
    static EmergencySceneAudit() { EditorApplication.delayCall += Write; }

    [MenuItem("Tools/Emergency VR/Write Scene Audit")]
    public static void Write()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/SampleScene.unity") return;
        var objects = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray();
        var text = new StringBuilder();
        text.AppendLine($"Scene={scene.path}, dirty={scene.isDirty}, quality={QualitySettings.names[QualitySettings.GetQualityLevel()]}, pipeline={QualitySettings.renderPipeline}");
        foreach (var t in objects)
        {
            var camera = t.GetComponent<Camera>();
            var controller = t.GetComponent<CharacterController>();
            var light = t.GetComponent<Light>();
            var move = t.GetComponent<SimpleVRMovement>();
            if (camera || controller || light || move)
                text.AppendLine($"OBJECT {t.name}: position={t.position}, rotation={t.eulerAngles}, scale={t.lossyScale}, components={string.Join(",", t.GetComponents<Component>().Where(c => c).Select(c => c.GetType().Name))}");
            if (controller) text.AppendLine($"CONTROLLER enabled={controller.enabled}, center={controller.center}, height={controller.height}, radius={controller.radius}");
            if (camera) text.AppendLine($"CAMERA enabled={camera.enabled}, HDR={camera.allowHDR}, MSAA={camera.allowMSAA}, dynamic={camera.allowDynamicResolution}, clip={camera.nearClipPlane}/{camera.farClipPlane}");
            if (camera) for (var p = t; p; p = p.parent) text.AppendLine($"CAMERA PARENT {p.name}: local={p.localPosition}, world={p.position}");
            if (light) text.AppendLine($"LIGHT enabled={light.enabled}, type={light.type}, color={light.color}, intensity={light.intensity}, shadows={light.shadows}");
            var collider = t.GetComponent<Collider>();
            if (collider && collider.enabled && !collider.isTrigger)
                text.AppendLine($"COLLIDER {t.name}, type={collider.GetType().Name}, center={collider.bounds.center}, size={collider.bounds.size}");
            var renderer = t.GetComponent<MeshRenderer>();
            if (renderer && renderer.bounds.size.x > 10 && renderer.bounds.size.z > 10)
                text.AppendLine($"SURFACE {t.name}: center={renderer.bounds.center}, size={renderer.bounds.size}, material={string.Join(",",renderer.sharedMaterials.Select(m => m ? m.name : "null"))}");
        }
        File.WriteAllText("Library/EmergencySceneAudit.txt", text.ToString());
    }
}
