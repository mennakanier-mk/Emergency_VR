using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The XR Origin's 3D hands are made for headsets that track your real hands: in Play their
/// tracking scripts move them to wherever the (missing) hand data says - the floor under the
/// rig - and hide them. A phone in a viewer has no hand tracking, so they vanished.
///
/// This keeps them where they are placed in the scene (in front of the camera, as seen in the
/// Game view before Play): their tracking scripts are switched off and they are attached to
/// the head, so they stay in view and turn with it.
///
/// Installs itself whenever a scene loads; nothing to add by hand.
/// </summary>
public static class HandsInView
{
    static readonly string[] kTrackingTypes =
    {
        "TrackedPoseDriver", "HandSkeletonDriver", "HandTrackingEvents", "HandVisualizer",
        "HandMeshController", "XRHandSkeleton", "HandPoseDriver", "Smoothing", "ControllerInputActionManager",
        "XRInputModalityManager", "Interactor", "PokeGesture", "HandShape", "XRHand"
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        SceneManager.sceneLoaded -= OnLoaded;
        SceneManager.sceneLoaded += OnLoaded;
        Apply();
    }

    static void OnLoaded(Scene s, LoadSceneMode m) => Apply();

    static void Apply()
    {
        // The head: the phone-VR rig's camera, else the main camera.
        Transform head = null;
        var vr = Object.FindFirstObjectByType<SimpleStereoVR>();
        if (vr != null && vr.headCamera != null) head = vr.headCamera.transform;
        if (head == null && Camera.main != null) head = Camera.main.transform;
        if (head == null) { Debug.Log("[HandsInView] no head camera in this scene."); return; }

        // The rig: the top of the head's hierarchy (XR Origin).
        Transform rig = head;
        while (rig.parent != null) rig = rig.parent;

        // The XR modality manager switches the hand objects off when no hands are tracked.
        foreach (var mb in rig.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb.GetType().Name.Contains("InputModalityManager")) mb.enabled = false;

        var roots = new HashSet<Transform>();
        foreach (var r in rig.GetComponentsInChildren<Renderer>(true))
        {
            if (r.transform.IsChildOf(head)) continue;                     // already on the head (eyes, chrome)
            if (!LooksLikeHand(r, rig)) continue;
            // Its own branch: the child of the rig's top-level object (or of 'Camera Offset').
            Transform t = r.transform;
            while (t.parent != null && t.parent != rig && !IsOffset(t.parent)) t = t.parent;
            if (t.parent == null || t == head || head.IsChildOf(t)) continue;
            roots.Add(t);
        }

        if (roots.Count == 0)
        {
            var sb = new System.Text.StringBuilder($"[HandsInView] no hands under '{rig.name}' (head '{head.name}'). Hand renderers in the scene:");
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string path = r.name; for (var p = r.transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
                if (path.ToLowerInvariant().Contains("hand")) sb.Append($"\n  {path} [{r.GetType().Name}] active={r.gameObject.activeInHierarchy} enabled={r.enabled}");
            }
            Debug.Log(sb.ToString());
        }

        // A character is used instead of the VR hands: hide the hands and stop here.
        if (Object.FindFirstObjectByType<ThirdPersonPlayer>() != null)
        {
            foreach (var root in roots) root.gameObject.SetActive(false);
            Debug.Log($"[HandsInView] ThirdPersonPlayer in scene - {roots.Count} VR hand(s) hidden.");
            return;
        }

        foreach (var root in roots)
        {
            int off = 0;
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                string n = mb.GetType().Name;
                foreach (var k in kTrackingTypes)
                    if (n.Contains(k)) { mb.enabled = false; off++; break; }
            }
            // Keep exactly the pose it has in the scene, now relative to the head.
            root.SetParent(head, true);
            root.gameObject.SetActive(true);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                r.gameObject.SetActive(true);
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }
            RaiseIntoView(root, head);
            Debug.Log($"[HandsInView] '{root.name}' kept in view on '{head.name}' ({off} tracking scripts off).");
        }
    }

    /// <summary>
    /// The eyes see a narrower picture than the Game view did, which left only the fingertips at
    /// the bottom edge. Lift each hand along the head's up until its middle sits a little above
    /// the bottom of an eye's view.
    /// </summary>
    static void RaiseIntoView(Transform root, Transform head)
    {
        Camera eye = null;
        foreach (var c in head.GetComponentsInChildren<Camera>(true))
            if (c.transform != head) { eye = c; break; }
        if (eye == null) eye = head.GetComponent<Camera>();
        if (eye == null) return;

        Bounds b = default; bool any = false;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
        if (!any) return;

        const float targetY = 0.24f;
        Vector3 up = head.up;
        float step = Mathf.Max(0.02f, b.extents.magnitude * 0.25f);
        float y0 = eye.WorldToViewportPoint(b.center).y;
        float y1 = eye.WorldToViewportPoint(b.center + up * step).y;
        if (y0 >= targetY || y1 - y0 < 1e-4f) return;
        float k = Mathf.Clamp((targetY - y0) / (y1 - y0), 0f, 20f) * step;
        root.position += up * k;
    }

    static bool IsOffset(Transform t) => t.name.ToLowerInvariant().Contains("offset");

    static bool LooksLikeHand(Renderer r, Transform rig)
    {
        if (r is LineRenderer || r is TrailRenderer || r is ParticleSystemRenderer) return false;   // rays, reticles
        for (Transform t = r.transform; t != null && t != rig; t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("smoothing")) return false;
            if (n.Contains("hand")) return true;
        }
        if (r is SkinnedMeshRenderer smr)
            foreach (var b in smr.bones)
                if (b != null && b.name.ToLowerInvariant().Contains("wrist")) return true;
        return false;
    }
}
