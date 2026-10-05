using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
///   Tools > Emergency VR > Center Pivot On Model (Selection)
///
/// Moves the selected object's pivot onto its geometry: horizontally to the centre of its
/// renderers, vertically to their lowest point (the wheels / feet). The model does not move on
/// screen - the root moves and its direct children move back by the same amount.
///
/// For FBX files exported with the Blender world position baked in ("moto .fbx" sits ~45 m to
/// the side of its origin and ~2.5 m above it). Works on prefab instances: it only writes
/// position overrides on the root and its direct children, the FBX itself is not touched.
/// Animation keeps working as long as the clip does not key the POSITION of those direct
/// children (the moto's clip only keys their rotation).
/// </summary>
public static class CenterPivotTool
{
    const string Menu = "Tools/Emergency VR/Center Pivot On Model (Selection)";

    [MenuItem(Menu)]
    static void CenterSelection()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[CenterPivot] Exit Play mode first.");
            return;
        }

        foreach (var go in Selection.gameObjects)
            Center(go.transform);
    }

    [MenuItem(Menu, true)]
    static bool CenterSelectionValidate() => Selection.gameObjects.Length > 0;

    public static void Center(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        bool any = false;
        Bounds b = default;

        foreach (var r in renderers)
        {
            if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer)
                continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }

        if (!any)
        {
            Debug.LogWarning($"[CenterPivot] '{root.name}': no renderers, nothing to centre on.", root);
            return;
        }

        Vector3 target = new Vector3(b.center.x, b.min.y, b.center.z);
        Vector3 offset = target - root.position;

        if (offset.sqrMagnitude < 0.000001f)
        {
            Debug.Log($"[CenterPivot] '{root.name}': pivot is already on the model.", root);
            return;
        }

        var children = new Transform[root.childCount];
        for (int i = 0; i < children.Length; i++) children[i] = root.GetChild(i);

        var toRecord = new Object[children.Length + 1];
        toRecord[0] = root;
        for (int i = 0; i < children.Length; i++) toRecord[i + 1] = children[i];
        Undo.RecordObjects(toRecord, "Center Pivot On Model");

        // Record world positions first, move the root, then put every child back where it was.
        var worldPos = new Vector3[children.Length];
        for (int i = 0; i < children.Length; i++) worldPos[i] = children[i].position;

        root.position = target;

        for (int i = 0; i < children.Length; i++)
        {
            children[i].position = worldPos[i];
            PrefabUtility.RecordPrefabInstancePropertyModifications(children[i]);
        }
        PrefabUtility.RecordPrefabInstancePropertyModifications(root);

        // Let RoadDriver re-measure where it stands on its path.
        var driver = root.GetComponent<RoadDriver>();
        if (driver != null)
        {
            Undo.RecordObject(driver, "Center Pivot On Model");
            driver.groundOffset = 0f;
            driver.SnapToNearest();
            EditorUtility.SetDirty(driver);
        }

        EditorSceneManager.MarkSceneDirty(root.gameObject.scene);

        Debug.Log($"[CenterPivot] '{root.name}': pivot moved {offset.magnitude:0.00} m " +
                  $"({offset.x:0.00}, {offset.y:0.00}, {offset.z:0.00}) onto the model.", root);
    }
}
