using UnityEngine;

/// <summary>
/// Pulls a model's geometry back onto its own pivot before RoadDriver starts moving it.
///
/// Some FBX exports keep the Blender world position baked into the child objects. "moto .fbx"
/// is one: the bike and rider sit about 45 m away from the prefab root. RoadDriver puts the ROOT
/// on the path, so the bike rode 45 m off to the side (up on the kerb), and on every bend it
/// swung round the path in a 45 m circle instead of turning on the spot.
///
/// This shifts the root's direct children horizontally so the centre of the renderers lands on
/// the root. Only positions of the root's direct children are touched; clips that animate
/// rotations (the moto's lean, wheel spin) or bones below them keep working.
///
/// Applied automatically at runtime to every RoadDriver whose geometry is more than
/// <see cref="AutoThreshold"/> metres from its pivot, after Awake and before any Start, so
/// RoadDriver's ground snap and pivot-height measurement already see the corrected model.
/// Models that are already centred (all the cars, pedestrians and bicycles) are left alone.
/// </summary>
[DisallowMultipleComponent]
public class ModelRecenter : MonoBehaviour
{
    public const float AutoThreshold = 3f;

    [Tooltip("Offset that was removed, in world metres. Read-only, for reference.")]
    public Vector3 appliedOffset;

    bool _done;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoApply()
    {
        foreach (var driver in Object.FindObjectsByType<RoadDriver>(FindObjectsSortMode.None))
        {
            Vector3 offset = HorizontalOffset(driver.transform);
            if (offset.magnitude < AutoThreshold) continue;

            var r = driver.GetComponent<ModelRecenter>();
            if (r == null) r = driver.gameObject.AddComponent<ModelRecenter>();
            r.Apply();
        }
    }

    void Awake() => Apply();

    public void Apply()
    {
        if (_done) return;
        _done = true;

        Vector3 offset = HorizontalOffset(transform);
        if (offset.sqrMagnitude < 0.0001f) return;

        foreach (Transform child in transform)
            child.position -= offset;

        appliedOffset = offset;
        Debug.Log($"[ModelRecenter] '{name}': geometry was {offset.magnitude:0.0} m off its " +
                  "pivot - moved back onto it.", this);
    }

    /// <summary>World-space XZ distance from the root to the centre of its renderers.</summary>
    static Vector3 HorizontalOffset(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return Vector3.zero;

        bool any = false;
        Bounds b = default;

        foreach (var r in renderers)
        {
            if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer)
                continue;

            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }

        if (!any) return Vector3.zero;

        Vector3 d = b.center - root.position;
        d.y = 0f;
        return d;
    }
}
