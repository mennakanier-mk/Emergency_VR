using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Emergency > Use Character Instead Of VR
///
/// Puts the character from Assets/player/player.fbx in front of the camera, hides the VR hands,
/// switches the stereo VR view off, and leaves the camera at exactly the angle it has now.
/// Everything is undoable (Ctrl+Z).
/// </summary>
public static class ThirdPersonPlayerSetup
{
    const string kModelPath = "Assets/player/player.fbx";
    const float kCharacterHeight = 1.75f;

    [MenuItem("Tools/Emergency/Use Character Instead Of VR")]
    static void Setup()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Character setup", "Stop Play mode first.", "OK");
            return;
        }

        var existing = Object.FindFirstObjectByType<ThirdPersonPlayer>(FindObjectsInactive.Include);
        if (existing != null)
        {
            Selection.activeGameObject = existing.gameObject;
            EditorUtility.DisplayDialog("Character setup",
                "A ThirdPersonPlayer is already in the scene (selected).", "OK");
            return;
        }

        var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(kModelPath);
        if (modelAsset == null)
        {
            EditorUtility.DisplayDialog("Character setup", "Not found: " + kModelPath, "OK");
            return;
        }

        AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(kModelPath)
            .OfType<AnimationClip>()
            .FirstOrDefault(c => !c.name.StartsWith("__preview__"));

        // ---------------------------------------------------------- the camera (unchanged)
        Camera cam = null;
        var vr = Object.FindFirstObjectByType<SimpleStereoVR>(FindObjectsInactive.Include);
        if (vr != null) cam = vr.headCamera != null ? vr.headCamera : vr.GetComponentInChildren<Camera>(true);
        if (cam == null) cam = Camera.main;
        if (cam == null) cam = Object.FindFirstObjectByType<Camera>();
        if (cam == null)
        {
            EditorUtility.DisplayDialog("Character setup", "No camera in the scene.", "OK");
            return;
        }

        Undo.SetCurrentGroupName("Use Character Instead Of VR");
        int group = Undo.GetCurrentGroup();

        Transform rig = cam.transform;
        while (rig.parent != null) rig = rig.parent;

        // ---------------------------------------------------------- VR off
        if (vr != null) { Undo.RecordObject(vr, "VR off"); vr.enabled = false; }
        foreach (var l in Object.FindObjectsByType<PhoneVRLocomotion>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        { Undo.RecordObject(l, "VR off"); l.enabled = false; }
        foreach (var r in Object.FindObjectsByType<VRLinkReceiver>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        { Undo.RecordObject(r, "VR off"); r.enabled = false; }

        // ---------------------------------------------------------- hide the hands
        int hidden = 0;
        foreach (var rend in rig.GetComponentsInChildren<Renderer>(true))
        {
            if (!IsHand(rend.transform, rig)) continue;
            Transform t = rend.transform;
            while (t.parent != null && t.parent != rig && t.parent != cam.transform &&
                   !t.parent.name.ToLowerInvariant().Contains("offset") &&
                   t.parent.name.ToLowerInvariant().Contains("hand"))
                t = t.parent;
            if (t == cam.transform || t == rig) continue;
            if (!t.gameObject.activeSelf) continue;
            Undo.RecordObject(t.gameObject, "Hide hands");
            t.gameObject.SetActive(false);
            hidden++;
        }

        // ---------------------------------------------------------- the character
        var player = new GameObject("Player");
        Undo.RegisterCreatedObjectUndo(player, "Create player");

        var modelGo = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
        Undo.RegisterCreatedObjectUndo(modelGo, "Create player model");
        modelGo.name = "player";
        modelGo.transform.SetParent(player.transform, false);

        // Anything the FBX brought along that is not the character itself.
        foreach (var c in modelGo.GetComponentsInChildren<Camera>(true)) c.enabled = false;
        foreach (var l in modelGo.GetComponentsInChildren<Light>(true)) l.enabled = false;
        foreach (var col in modelGo.GetComponentsInChildren<Collider>(true)) col.enabled = false;

        // Normal human size, standing on its feet at the Player's origin.
        Bounds b = WorldBounds(modelGo);
        if (b.size.y > 0.001f && (b.size.y < 1.2f || b.size.y > 2.4f))
        {
            modelGo.transform.localScale *= kCharacterHeight / b.size.y;
            b = WorldBounds(modelGo);
        }
        modelGo.transform.position += new Vector3(-b.center.x, -b.min.y, -b.center.z);
        float height = Mathf.Max(1f, b.size.y);

        // Face away from the camera, like the hands did.
        Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        fwd.Normalize();
        player.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);

        // Far enough ahead that the whole body is in the camera's view, on the ground.
        Vector3 pos = cam.transform.position + fwd * 3f;
        for (float d = 2f; d <= 12f; d += 0.1f)
        {
            Vector3 p = GroundUnder(cam.transform.position + fwd * d, rig, cam.transform.position.y);
            if (cam.WorldToViewportPoint(p).y >= 0.12f) { pos = p; break; }
            pos = p;
        }
        player.transform.position = pos;

        var cc = player.AddComponent<CharacterController>();
        cc.height = height;
        cc.radius = Mathf.Clamp(height * 0.17f, 0.2f, 0.45f);
        cc.center = new Vector3(0f, height * 0.5f + cc.skinWidth, 0f);
        cc.stepOffset = Mathf.Min(0.35f, height * 0.3f);

        var tpp = player.AddComponent<ThirdPersonPlayer>();
        tpp.followCamera = cam;
        tpp.model = modelGo.transform;
        tpp.moveClip = clip;

        EditorSceneManager.MarkSceneDirty(player.scene);
        Undo.CollapseUndoOperations(group);
        Selection.activeGameObject = player;

        Debug.Log($"[ThirdPersonPlayerSetup] Player placed at {pos}, height {height:F2} m, " +
                  $"animation '{(clip != null ? clip.name : "NONE")}', {hidden} VR hand object(s) hidden, " +
                  "VR view switched off. Camera not changed. Save the scene (Ctrl+S).");

        if (clip == null)
            Debug.LogWarning("[ThirdPersonPlayerSetup] No animation clip found inside player.fbx. " +
                             "Drag one into ThirdPersonPlayer > Move Clip.");
    }

    static bool IsHand(Transform t, Transform rig)
    {
        for (; t != null && t != rig; t = t.parent)
            if (t.name.ToLowerInvariant().Contains("hand")) return true;
        return false;
    }

    static Bounds WorldBounds(GameObject go)
    {
        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        return b;
    }

    static Vector3 GroundUnder(Vector3 p, Transform ignoreRig, float fromY)
    {
        Vector3 origin = new Vector3(p.x, fromY + 2f, p.z);
        var hits = Physics.RaycastAll(origin, Vector3.down, 50f, ~0, QueryTriggerInteraction.Ignore)
                          .OrderBy(h => h.distance);
        foreach (var h in hits)
            if (!h.transform.IsChildOf(ignoreRig)) return h.point;

        // No collider below: use the rig's feet height.
        return new Vector3(p.x, ignoreRig.position.y, p.z);
    }
}
