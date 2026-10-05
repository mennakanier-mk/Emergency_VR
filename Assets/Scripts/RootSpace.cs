using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bone maths done in the space of a character's ROOT object instead of world space.
///
/// Why: the boy (accident_scene) is mirrored with a negative scale, and the paramedic rig may be
/// too. Under a mirrored parent, setting transform.rotation in world space comes out mirrored -
/// a turn to the left lands as a turn to the right. Inside the root's own local space nothing is
/// mirrored (every bone below it has a positive scale), so all rotations are worked out there and
/// written back as localRotation. Positions go through InverseTransformPoint, which handles the
/// mirror correctly.
/// </summary>
public static class RootSpace
{
    /// <summary>Rotation of a bone relative to the root (product of localRotations below it).</summary>
    public static Quaternion Rot(Transform root, Transform t)
    {
        Quaternion q = Quaternion.identity;
        while (t != null && t != root) { q = t.localRotation * q; t = t.parent; }
        return q;
    }

    public static void SetRot(Transform root, Transform t, Quaternion rs)
    {
        Quaternion parent = t.parent == null || t.parent == root ? Quaternion.identity : Rot(root, t.parent);
        t.localRotation = Quaternion.Inverse(parent) * rs;
    }

    public static Vector3 Pos(Transform root, Transform t) => root.InverseTransformPoint(t.position);
    public static Vector3 Pos(Transform root, Vector3 world) => root.InverseTransformPoint(world);

    /// <summary>A world direction expressed in root space (mirror included), normalised.</summary>
    public static Vector3 Dir(Transform root, Vector3 world)
    {
        Vector3 v = root.InverseTransformVector(world);
        return v.sqrMagnitude > 1e-12f ? v.normalized : Vector3.forward;
    }

    /// <summary>Turn a bone so the direction to its child points along a root-space direction.</summary>
    public static void Aim(Transform root, Transform bone, Transform child, Vector3 dirRS, float weight = 1f)
    {
        if (bone == null || child == null) return;
        Vector3 cur = Pos(root, child) - Pos(root, bone);
        if (cur.sqrMagnitude < 1e-10f || dirRS.sqrMagnitude < 1e-10f) return;
        Quaternion d = Quaternion.FromToRotation(cur.normalized, dirRS.normalized);
        if (weight < 1f) d = Quaternion.Slerp(Quaternion.identity, d, weight);
        SetRot(root, bone, d * Rot(root, bone));
    }

    /// <summary>Classic analytic two-bone IK (upper arm / forearm / hand), in root space.</summary>
    public static void TwoBoneIK(Transform root, Transform a, Transform b, Transform c, Vector3 targetRS, float weight)
    {
        if (a == null || b == null || c == null || weight <= 0f) return;

        Vector3 pa = Pos(root, a), pb = Pos(root, b), pc = Pos(root, c);
        Vector3 t = Vector3.Lerp(pc, targetRS, weight);

        float lab = (pb - pa).magnitude, lcb = (pc - pb).magnitude;
        float lat = Mathf.Clamp((t - pa).magnitude, 0.001f, lab + lcb - 0.001f);

        float acab0 = Angle(pc - pa, pb - pa);
        float babc0 = Angle(pa - pb, pc - pb);
        float acat0 = Angle(pc - pa, t - pa);
        float acab1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
        float babc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

        Vector3 axis0 = Vector3.Cross(pc - pa, pb - pa);
        if (axis0.sqrMagnitude < 1e-10f) axis0 = Vector3.Cross(pc - pa, Vector3.up);
        axis0.Normalize();
        Vector3 axis1 = Vector3.Cross(pc - pa, t - pa);
        bool turn = axis1.sqrMagnitude > 1e-10f;
        if (turn) axis1.Normalize();

        Quaternion aRS = Rot(root, a), bRS = Rot(root, b);
        Quaternion r0 = Quaternion.AngleAxis((acab1 - acab0) * Mathf.Rad2Deg, Quaternion.Inverse(aRS) * axis0);
        Quaternion r1 = Quaternion.AngleAxis((babc1 - babc0) * Mathf.Rad2Deg, Quaternion.Inverse(bRS) * axis0);
        Quaternion r2 = turn ? Quaternion.AngleAxis(acat0 * Mathf.Rad2Deg, Quaternion.Inverse(aRS) * axis1)
                             : Quaternion.identity;

        a.localRotation = a.localRotation * r0 * r2;
        b.localRotation = b.localRotation * r1;
    }

    static float Angle(Vector3 u, Vector3 v)
    {
        if (u.sqrMagnitude < 1e-12f || v.sqrMagnitude < 1e-12f) return 0f;
        return Mathf.Acos(Mathf.Clamp(Vector3.Dot(u.normalized, v.normalized), -1f, 1f));
    }

    /// <summary>First transform under 'under' whose name ends with the given suffix (case-insensitive).</summary>
    public static Transform Find(Transform under, string suffix)
    {
        suffix = suffix.ToLowerInvariant();
        foreach (var t in under.GetComponentsInChildren<Transform>(true))
            if (t.name.ToLowerInvariant().EndsWith(suffix)) return t;
        return null;
    }

    public static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
}
