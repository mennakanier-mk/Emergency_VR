using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A box the player is allowed to walk inside. Put one (or several) in the scene, size them over
/// the streets, and PhoneVRLocomotion will not let the wearer leave them.
///
/// Why a zone and not colliders: the city is a single imported mesh with a MeshCollider, so there
/// is nothing to stop you walking off the map, through a shopfront, or out over the edge of the
/// world - and adding a collider to every building is both a lot of work and a lot of physics. A
/// walkable box is the same idea as a level designer's play area: cheap, visible in the Scene
/// view, and easy to reshape while you look at the street.
///
/// Several zones form a network. The player may be in any one of them, so an L-shaped junction is
/// two overlapping boxes. Outside all of them, the nearest one pulls you back - you slide along
/// its wall rather than stopping dead, which is what a corridor should feel like.
///
/// Only X and Z are constrained. Height is left to gravity and the ground.
/// </summary>
[DisallowMultipleComponent]
public class VRSafeZone : MonoBehaviour
{
    [Tooltip("Centre of the walkable box, relative to this object.")]
    public Vector3 center = Vector3.zero;

    [Tooltip("Size of the walkable box in metres. Y is ignored - only the footprint matters.")]
    public Vector3 size = new Vector3(40f, 4f, 40f);

    [Tooltip("Drawn in the Scene view so you can shape it against the road.")]
    public Color gizmoColor = new Color(0.2f, 0.9f, 0.4f, 1f);

    [Tooltip("Turn off to disable this zone without deleting it.")]
    public bool active = true;

    static readonly List<VRSafeZone> _all = new List<VRSafeZone>();
    public static IReadOnlyList<VRSafeZone> All => _all;

    void OnEnable() { if (!_all.Contains(this)) _all.Add(this); }
    void OnDisable() { _all.Remove(this); }

    /// <summary>World-space footprint. Rotation is ignored; these are axis-aligned on purpose,
    /// because a rotated box makes the clamp maths worse for no gain on a street grid.</summary>
    public Bounds WorldBounds
    {
        get
        {
            Vector3 c = transform.position + center;
            Vector3 s = new Vector3(Mathf.Abs(size.x), Mathf.Max(1f, Mathf.Abs(size.y)),
                                    Mathf.Abs(size.z));
            return new Bounds(c, s);
        }
    }

    public bool ContainsXZ(Vector3 p)
    {
        if (!active) return false;
        Bounds b = WorldBounds;
        return p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z;
    }

    public Vector3 ClampXZ(Vector3 p)
    {
        Bounds b = WorldBounds;
        p.x = Mathf.Clamp(p.x, b.min.x, b.max.x);
        p.z = Mathf.Clamp(p.z, b.min.z, b.max.z);
        return p;
    }

    /// <summary>Squared distance from the point to this box's footprint. 0 when inside.</summary>
    public float SqrDistanceXZ(Vector3 p)
    {
        Vector3 c = ClampXZ(p);
        float dx = c.x - p.x, dz = c.z - p.z;
        return dx * dx + dz * dz;
    }

    // ---------------------------------------------------------------- static API

    public static bool AnyZoneExists()
    {
        for (int i = 0; i < _all.Count; i++)
            if (_all[i] != null && _all[i].active) return true;
        return false;
    }

    public static bool IsInside(Vector3 p)
    {
        for (int i = 0; i < _all.Count; i++)
            if (_all[i] != null && _all[i].ContainsXZ(p)) return true;
        return false;
    }

    /// <summary>
    /// Pulls a point back into the closest zone. Clamping per axis is what makes it slide: walk
    /// into the side wall of a street and your forward motion survives while the sideways part
    /// is cancelled, instead of the whole step being thrown away.
    /// </summary>
    public static Vector3 Clamp(Vector3 p)
    {
        if (IsInside(p)) return p;

        VRSafeZone nearest = null;
        float best = float.MaxValue;

        for (int i = 0; i < _all.Count; i++)
        {
            var z = _all[i];
            if (z == null || !z.active) continue;

            float d = z.SqrDistanceXZ(p);
            if (d >= best) continue;

            best = d;
            nearest = z;
        }

        return nearest == null ? p : nearest.ClampXZ(p);
    }

    // ---------------------------------------------------------------- gizmos

    void OnDrawGizmos()
    {
        Bounds b = WorldBounds;

        Gizmos.color = active ? gizmoColor : new Color(0.5f, 0.5f, 0.5f, 1f);
        Gizmos.DrawWireCube(b.center, new Vector3(b.size.x, 0.05f, b.size.z));

        // A low translucent slab reads as a floor you may stand on, which is what it is.
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, active ? 0.12f : 0.05f);
        Gizmos.DrawCube(b.center, new Vector3(b.size.x, 0.05f, b.size.z));

        Gizmos.color = active ? gizmoColor : Color.gray;
        Gizmos.DrawWireCube(b.center, b.size);
    }
}
