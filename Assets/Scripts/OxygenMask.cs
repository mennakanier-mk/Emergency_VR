using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The paramedic's oxygen mask, built from settings you can change in the Inspector - the mesh
/// rebuilds live, in the Editor and in Play.
///
/// The mask is fitted into the box of the original FA_mask mesh from em.fbx (same size, same
/// axes), so it still lands where the clip animates it. Use Size / Depth to make it bigger or
/// smaller than that box, Flip Outward if the dome faces into the face, Flip Nose if the narrow
/// end is at the chin. Or drop your own mesh into Custom Mesh (made in Blender, say) and it is
/// fitted the same way.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class OxygenMask : MonoBehaviour
{
    [Header("Shape")]
    [Tooltip("Overall size relative to the original mask's box.")]
    [Range(0.4f, 2f)] public float size = 1.17f;
    [Tooltip("How far the dome sticks out of the face, relative to the original box.")]
    [Range(0.3f, 2.5f)] public float depth = 1.53f;
    [Tooltip("Width relative to height.")]
    [Range(0.5f, 1.5f)] public float width = 1f;
    [Tooltip("How much narrower it is over the nose than at the chin. 0 = an oval.")]
    [Range(0f, 0.8f)] public float noseNarrowing = 0.38f;
    [Tooltip("0 = pointed cone, 1 = round dome.")]
    [Range(0f, 1f)] public float roundness = 1f;
    [Tooltip("Thickness of the soft rim that touches the face (fraction of the mask height).")]
    [Range(0f, 0.2f)] public float rim = 0.08f;

    [Header("Parts")]
    public bool connector = true;
    [Tooltip("Oxygen tube length (fraction of the mask height). 0 = no tube.")]
    [Range(0f, 3f)] public float tube = 0.9f;
    public bool strap = true;
    [Tooltip("How far back the elastic strap goes (fraction of the mask height).")]
    [Range(0.2f, 3f)] public float strapLength = 1.3f;

    [Header("Look")]
    public Color plastic = new Color(0.86f, 0.97f, 0.92f, 0.45f);
    public Color strapColor = new Color(0.30f, 0.72f, 0.48f, 1f);
    [Range(0f, 1f)] public float shine = 1f;

    [Header("Orientation")]
    public bool flipOutward;
    public bool flipNose;

    [Header("On the boy's face")]
    [Tooltip("Extra turn while the mask is on the boy's face, degrees (mask's own axes), on top of " +
             "the automatic fit. Only used on the face - not in the bag or in the paramedic's hand.")]
    [UnityEngine.Serialization.FormerlySerializedAs("rotationOffset")]
    public Vector3 onFaceRotation = new Vector3(40f, 0f, -10f);
    [Tooltip("Extra shift while on the face, metres (mask's own axes, after the turn).")]
    [UnityEngine.Serialization.FormerlySerializedAs("positionOffset")]
    public Vector3 onFacePosition = new Vector3(0.1f, -0.5f, -0.23f);

    [Tooltip("While on the face, put the mask exactly at the pose below (local to its parent, the rig) " +
             "instead of fitting it to the head. These are the values chosen in Play on 4 Oct 2026.")]
    public bool useFacePose = true;
    public Vector3 faceLocalPosition = new Vector3(1.36f, 0.178f, 5.102f);
    public Vector3 faceLocalRotation = new Vector3(-42.565f, 90.296f, -1.52f);
    public Vector3 faceLocalScale = new Vector3(116.6f, 116.6f, 116.6f);

    // Settings tuned in Play on 4 Oct 2026, written once into masks that already existed.
    [SerializeField, HideInInspector] int _preset;
    const int kPreset = 1;

    [Header("Your own model (optional)")]
    [Tooltip("Use this mesh instead of the generated one. It is fitted into the same box.")]
    public Mesh customMesh;

    // Fit box, from the original em.fbx mesh (kept so the box survives the mesh being replaced).
    [SerializeField, HideInInspector] Mesh _source;
    [SerializeField, HideInInspector] Bounds _fit;
    [SerializeField, HideInInspector] int _dAx = -1, _hAx;
    [SerializeField, HideInInspector] float _outSign = 1f;

    /// <summary>Local axis pointing out of the face, and towards the nose (for fitting on a face).</summary>
    public Vector3 OutLocal { get; private set; } = Vector3.forward;
    public Vector3 NoseLocal { get; private set; } = Vector3.up;

    Mesh _mesh;
    Material _clear, _strap;
    bool _dirty = true;

    void OnEnable() { ApplyPreset(); _dirty = true; Rebuild(); }

    void ApplyPreset()
    {
        if (_preset >= kPreset) return;
        size = 1.17f; depth = 1.53f; width = 1f; noseNarrowing = 0.38f; roundness = 1f; rim = 0.08f;
        connector = true; tube = 0.9f; strap = true; strapLength = 1.3f; shine = 1f;
        onFaceRotation = new Vector3(40f, 0f, -10f);
        onFacePosition = new Vector3(0.1f, -0.5f, -0.23f);
        _preset = kPreset;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(this);
            if (gameObject.scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
#endif
    }

    /// <summary>Blend the mask to the chosen on-face pose (0 = as animated, 1 = fully on).</summary>
    public void ApplyFacePose(float weight)
    {
        if (weight <= 0f) return;
        transform.localPosition = Vector3.Lerp(transform.localPosition, faceLocalPosition, weight);
        transform.localRotation = Quaternion.Slerp(transform.localRotation, Quaternion.Euler(faceLocalRotation), weight);
        transform.localScale = Vector3.Lerp(transform.localScale, faceLocalScale, weight);
    }

    /// <summary>Apply the on-face offset, weighted (0 = off the face, 1 = fully on it).</summary>
    public void ApplyOnFaceOffset(float weight)
    {
        if (weight <= 0f) return;
        transform.rotation = transform.rotation * Quaternion.Slerp(Quaternion.identity, Quaternion.Euler(onFaceRotation), weight);
        Vector3 ls = transform.lossyScale;
        Vector3 inv = new Vector3(1f / Mathf.Max(1e-6f, Mathf.Abs(ls.x)), 1f / Mathf.Max(1e-6f, Mathf.Abs(ls.y)), 1f / Mathf.Max(1e-6f, Mathf.Abs(ls.z)));
        transform.position += transform.TransformVector(Vector3.Scale(onFacePosition, inv)) * weight;
    }

    void OnValidate() { _dirty = true; }
    void Update() { if (_dirty) Rebuild(); }

    void OnDestroy()
    {
        var mf = GetComponent<MeshFilter>();
        if (mf != null && _source != null && mf.sharedMesh == _mesh) mf.sharedMesh = _source;
        Kill(_mesh); Kill(_clear); Kill(_strap);
    }

    static void Kill(Object o) { if (o == null) return; if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }

    void CaptureSource()
    {
        var mf = GetComponent<MeshFilter>();
        if (_dAx >= 0 && _source != null) return;
        Mesh m = mf.sharedMesh;
        if (m == null || (m.hideFlags & HideFlags.DontSave) != 0) return;
        _source = m;
        _fit = m.bounds;
        Vector3 e = _fit.extents;
        _dAx = e.x <= e.y && e.x <= e.z ? 0 : (e.y <= e.z ? 1 : 2);
        _hAx = e.x >= e.y && e.x >= e.z ? 0 : (e.y >= e.z ? 1 : 2);
        if (_hAx == _dAx) _hAx = (_dAx + 1) % 3;
        float sum = 0f; var nrm = m.normals;
        if (nrm != null) foreach (var n in nrm) sum += n[_dAx];
        _outSign = sum >= 0f ? 1f : -1f;
    }

    public void Rebuild()
    {
        _dirty = false;
        var mf = GetComponent<MeshFilter>();
        var mr = GetComponent<MeshRenderer>();
        CaptureSource();
        if (_dAx < 0) return;

        int dAx = _dAx, hAx = _hAx, wAx = 3 - _dAx - _hAx;
        float outS = _outSign * (flipOutward ? -1f : 1f), noseS = flipNose ? -1f : 1f;
        Vector3 e = _fit.extents;

        Mesh canon = customMesh != null ? Instantiate(customMesh) : Build();
        canon.RecalculateBounds();
        Bounds cb = canon.bounds;
        Vector3 ce = new Vector3(Mathf.Max(cb.extents.x, 1e-5f), Mathf.Max(cb.extents.y, 1e-5f), Mathf.Max(cb.extents.z, 1e-5f));

        // Target half-sizes along the original box axes.
        float tw = e[wAx] * size * width, th = e[hAx] * size, td = e[dAx] * size * depth;

        var verts = canon.vertices; var norms = canon.normals;
        if (norms == null || norms.Length != verts.Length) { canon.RecalculateNormals(); norms = canon.normals; }
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 c = verts[i] - cb.center, n = norms[i];
            Vector3 v = Vector3.zero, vn = Vector3.zero;
            v[wAx] = c.x / ce.x * tw;
            v[hAx] = c.y / ce.y * th * noseS;
            v[dAx] = c.z / ce.z * td * outS;
            vn[wAx] = n.x * ce.x / Mathf.Max(1e-6f, tw);
            vn[hAx] = n.y * ce.y / Mathf.Max(1e-6f, th) * noseS;
            vn[dAx] = n.z * ce.z / Mathf.Max(1e-6f, td) * outS;
            verts[i] = _fit.center + v;
            norms[i] = vn.normalized;
        }
        canon.vertices = verts; canon.normals = norms;
        // Mirroring an axis turns triangles inside out: flip them back.
        if (noseS * outS < 0f)
            for (int s = 0; s < canon.subMeshCount; s++)
            {
                var t = canon.GetTriangles(s);
                for (int k = 0; k < t.Length; k += 3) { int a = t[k]; t[k] = t[k + 1]; t[k + 1] = a; }
                canon.SetTriangles(t, s);
            }
        canon.RecalculateBounds();
        canon.name = "OxygenMask";
        canon.hideFlags = HideFlags.DontSave;

        Kill(_mesh);
        _mesh = canon;
        mf.sharedMesh = _mesh;

        Vector3 o = Vector3.zero; o[dAx] = outS;
        Vector3 up = Vector3.zero; up[hAx] = noseS;
        OutLocal = o; NoseLocal = up;
        OxygenMaskModel.Axes[transform] = (o, up);

        // Materials.
        Material baseMat = _source != null && mr.sharedMaterial != null && mr.sharedMaterial != _clear ? mr.sharedMaterial : null;
        if (_clear == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            _clear = baseMat != null ? new Material(baseMat) : new Material(shader);
            _strap = baseMat != null ? new Material(baseMat) : new Material(shader);
            _clear.hideFlags = _strap.hideFlags = HideFlags.DontSave;
            _clear.name = "OxygenMask Clear"; _strap.name = "OxygenMask Strap";
        }
        MakeTransparent(_clear, plastic, shine);
        SetColor(_strap, strapColor, 0.35f);
        mr.sharedMaterials = customMesh != null && customMesh.subMeshCount < 2 ? new[] { _clear } : new[] { _clear, _strap };
    }

    // ------------------------------------------------------------------ generated mesh

    Mesh Build()
    {
        var v = new List<Vector3>(); var n = new List<Vector3>();
        var shell = new List<int>(); var parts = new List<int>();

        const float W = 0.065f, H = 0.075f, D = 0.05f;
        const int U = 12, V = 40;
        float narrow = noseNarrowing;

        Vector2 RimAt(float a)
        {
            float x = Mathf.Sin(a), y = Mathf.Cos(a);
            return new Vector2(x * W * (1f - narrow * Mathf.Max(0f, y)), y * H);
        }

        int baseIdx = v.Count;
        for (int iu = 0; iu <= U; iu++)
        {
            float u = iu / (float)U;
            float round = Mathf.Cos(u * Mathf.PI * 0.5f), cone = 1f - u;
            float s = Mathf.Lerp(cone, round, roundness);
            float z = D * Mathf.Lerp(u, Mathf.Sin(u * Mathf.PI * 0.5f), roundness);
            for (int iv = 0; iv <= V; iv++)
            {
                float a = iv / (float)V * Mathf.PI * 2f;
                Vector2 r = RimAt(a) * Mathf.Lerp(0.30f, 1f, s);
                v.Add(new Vector3(r.x, r.y - 0.012f * (1f - s), z));
                n.Add(new Vector3(r.x / W, r.y / H, 1.2f * (1f - s) + 0.2f).normalized);
            }
        }
        int row = V + 1;
        for (int iu = 0; iu < U; iu++)
            for (int iv = 0; iv < V; iv++)
            {
                int a = baseIdx + iu * row + iv, b = a + 1, c = a + row, d = c + 1;
                shell.AddRange(new[] { a, c, b, b, c, d });
                shell.AddRange(new[] { a, b, c, b, d, c });
            }
        int tip = v.Count; v.Add(new Vector3(0f, -0.012f, D)); n.Add(Vector3.forward);
        int last = baseIdx + U * row;
        for (int iv = 0; iv < V; iv++) { shell.AddRange(new[] { last + iv, tip, last + iv + 1 }); shell.AddRange(new[] { last + iv, last + iv + 1, tip }); }

        if (rim > 0.001f)
            Tube(v, n, parts, a => { var r = RimAt(a); return new Vector3(r.x, r.y, 0.002f); }, rim * H, V, 6);
        if (connector)
            Cylinder(v, n, parts, new Vector3(0f, -0.012f, D - 0.004f), Vector3.forward, 0.011f, 0.022f, 12);
        if (tube > 0.01f)
            Cylinder(v, n, parts, new Vector3(0f, -0.016f, D + 0.012f), Vector3.down, 0.006f, tube * H, 10);
        if (strap)
            Tube(v, n, parts, a =>
            {
                float x = Mathf.Cos(a) * W * 1.25f, z = -Mathf.Abs(Mathf.Sin(a)) * strapLength * H + 0.004f;
                return new Vector3(x, 0.01f, z);
            }, 0.0025f, 24, 4);

        var m = new Mesh { name = "OxygenMask" };
        m.SetVertices(v); m.SetNormals(n);
        m.subMeshCount = 2;
        m.SetTriangles(shell, 0);
        m.SetTriangles(parts, 1);
        m.RecalculateBounds();
        return m;
    }

    static void Tube(List<Vector3> v, List<Vector3> n, List<int> tri, System.Func<float, Vector3> path, float radius, int segs, int sides)
    {
        int start = v.Count;
        for (int i = 0; i <= segs; i++)
        {
            float a = i / (float)segs * Mathf.PI * 2f;
            Vector3 p = path(a), t = (path(a + 0.01f) - p).normalized;
            Vector3 b1 = Vector3.Cross(t, Vector3.forward); if (b1.sqrMagnitude < 1e-6f) b1 = Vector3.Cross(t, Vector3.up);
            b1.Normalize(); Vector3 b2 = Vector3.Cross(t, b1);
            for (int k = 0; k <= sides; k++)
            {
                float c = k / (float)sides * Mathf.PI * 2f;
                Vector3 o = b1 * Mathf.Cos(c) + b2 * Mathf.Sin(c);
                v.Add(p + o * radius); n.Add(o);
            }
        }
        int r = sides + 1;
        for (int i = 0; i < segs; i++)
            for (int k = 0; k < sides; k++)
            {
                int a = start + i * r + k, b = a + 1, c = a + r, d = c + 1;
                tri.AddRange(new[] { a, b, c, b, d, c });
            }
    }

    static void Cylinder(List<Vector3> v, List<Vector3> n, List<int> tri, Vector3 from, Vector3 dir, float radius, float length, int sides)
    {
        dir.Normalize();
        Vector3 b1 = Vector3.Cross(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.forward : Vector3.up).normalized;
        Vector3 b2 = Vector3.Cross(dir, b1);
        int start = v.Count;
        for (int k = 0; k <= sides; k++)
        {
            float c = k / (float)sides * Mathf.PI * 2f;
            Vector3 o = b1 * Mathf.Cos(c) + b2 * Mathf.Sin(c);
            v.Add(from + o * radius); n.Add(o);
            v.Add(from + dir * length + o * radius); n.Add(o);
        }
        for (int k = 0; k < sides; k++)
        {
            int a = start + k * 2, b = a + 1, c = a + 2, d = a + 3;
            tri.AddRange(new[] { a, c, b, b, c, d });
        }
    }

    // ------------------------------------------------------------------ materials (URP Lit)

    static void SetColor(Material m, Color c, float smooth)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
    }

    static void MakeTransparent(Material m, Color c, float smooth)
    {
        SetColor(m, c, smooth);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }
}
