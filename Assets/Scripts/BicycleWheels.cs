using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Makes a bicycle's wheels roll, driven by how far the bike actually travels.
///
/// The wheels in "boy with bicycle .fbx" were separated in Blender but kept the bike's origin,
/// so their pivot sits at the rear hub - spinning the front wheel around it would swing it in a
/// circle. This builds a pivot at each wheel's true centre (read from its mesh bounds), reparents
/// the wheel under it keeping its world pose, and puts a WheelSpin on the pivot with the axle and
/// radius already measured.
///
/// Wheels are found automatically: any MeshFilter child whose mesh is a disc (one thin axis, the
/// other two roughly equal). The frame, rider and skinned meshes never match.
///
/// Added automatically at runtime to every RoadDriver whose name contains "bicycle", so the three
/// bikes in the scene need no manual setup. It can also be added by hand to anything else.
/// </summary>
[DisallowMultipleComponent]
public class BicycleWheels : MonoBehaviour
{
    [Tooltip("Thinnest side / widest side must be below this to count as a wheel.")]
    [Range(0.05f, 0.5f)] public float maxThickness = 0.3f;

    [Tooltip("The two wide sides must be at least this close to equal (a circle).")]
    [Range(0.5f, 1f)] public float minRoundness = 0.85f;

    [Tooltip("Flip if the wheels roll backwards.")]
    public bool invert = false;

    [Range(0.1f, 5f)] public float speedMultiplier = 1f;

    public bool logSetup = true;

    readonly List<WheelSpin> _spins = new List<WheelSpin>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoAttach()
    {
        foreach (var driver in Object.FindObjectsByType<RoadDriver>(FindObjectsSortMode.None))
        {
            if (driver.GetComponent<BicycleWheels>() != null) continue;

            // Two-wheelers only, by name. A model whose clip already spins its wheels (an
            // Animator with a controller, like "moto") is left to its clip so the wheels are not
            // driven twice - bicycles are the exception because their stale wheel curves were
            // stripped from the clip.
            string n = driver.name.ToLowerInvariant();
            bool twoWheeler = n.Contains("bicycle") || n.Contains("moto") || n.Contains("scooter");
            if (!twoWheeler) continue;

            var anim = driver.GetComponentInChildren<Animator>();
            bool animated = anim != null && anim.runtimeAnimatorController != null;

            if (n.Contains("bicycle") || !animated)
                driver.gameObject.AddComponent<BicycleWheels>();
        }
    }

    void Start()
    {
        foreach (var mf in GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            if (mf.GetComponentInParent<WheelSpin>() != null) continue;   // already set up

            if (!IsDisc(mf.sharedMesh.bounds.size, out int thinAxis, out float localRadius))
                continue;

            Transform wheel = mf.transform;

            // Pivot at the disc's centre, same orientation as the wheel, so the axle is still the
            // wheel's thin local axis.
            var pivot = new GameObject(wheel.name + "_Pivot").transform;
            pivot.SetParent(wheel.parent, false);
            pivot.position = wheel.TransformPoint(mf.sharedMesh.bounds.center);
            pivot.rotation = wheel.rotation;
            pivot.localScale = Vector3.one;
            wheel.SetParent(pivot, true);

            var spin = pivot.gameObject.AddComponent<WheelSpin>();
            spin.axle = thinAxis == 0 ? WheelSpin.AxleAxis.LocalX
                      : thinAxis == 1 ? WheelSpin.AxleAxis.LocalY
                                      : WheelSpin.AxleAxis.LocalZ;

            Vector3 s = wheel.lossyScale;
            float scale = thinAxis == 0 ? Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))
                        : thinAxis == 1 ? Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z))
                                        : Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
            spin.radius = localRadius * scale;
            spin.invert = invert;
            spin.speedMultiplier = speedMultiplier;
            _spins.Add(spin);

            if (logSetup)
                Debug.Log($"[BicycleWheels] '{name}': wheel '{wheel.name}' axle {spin.axle}, " +
                          $"radius {spin.radius:0.000} m", this);
        }

        if (_spins.Count == 0)
            Debug.Log($"[BicycleWheels] '{name}': no disc-shaped meshes found.", this);
    }

    void OnValidate()
    {
        foreach (var s in _spins)
        {
            if (s == null) continue;
            s.invert = invert;
            s.speedMultiplier = speedMultiplier;
        }
    }

    bool IsDisc(Vector3 size, out int thinAxis, out float radius)
    {
        thinAxis = 0;
        radius = 0f;

        float x = size.x, y = size.y, z = size.z;
        float a, b, c; // a = thin, b and c = wide

        if (x <= y && x <= z)      { thinAxis = 0; a = x; b = y; c = z; }
        else if (y <= x && y <= z) { thinAxis = 1; a = y; b = x; c = z; }
        else                       { thinAxis = 2; a = z; b = x; c = y; }

        float wide = Mathf.Max(b, c);
        if (wide <= 0.0001f) return false;
        if (a / wide > maxThickness) return false;
        if (Mathf.Min(b, c) / wide < minRoundness) return false;

        radius = wide * 0.5f;
        return true;
    }
}
