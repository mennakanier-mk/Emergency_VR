using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Blood from the head, only once the character is lying on the ground.
///
/// Watches the Hips and Head bones. When both are close to the ground and have stopped
/// moving for a moment (he has finished falling), it:
///   1. throws a small burst of droplets from the head, then
///   2. grows a pool on the ground under and just beyond the head, slowly, over several
///      seconds - a few overlapping irregular blobs so it is not a perfect circle.
///
/// Everything is built in code (particle systems, material, blob texture), so there is no
/// asset to set up. Sizes follow the character's own height, so it is right whatever scale
/// the model is at. Added automatically to the accident victim by AccidentCar; can also be
/// added by hand to any character.
/// </summary>
// After RunExtender (900) and GroundFollowAnimated (1000): they move the hips in LateUpdate,
// so only after them are the bones where the character is actually drawn.
[DefaultExecutionOrder(1100)]
[DisallowMultipleComponent]
public class BloodPool : MonoBehaviour
{
    [Header("When")]
    [Tooltip("Hips at most this fraction of their standing height above the ground = lying down.")]
    [Range(0.1f, 0.8f)] public float lyingHipsFraction = 0.4f;

    [Tooltip("Seconds he must stay lying still before the blood starts.")]
    public float settleTime = 0.5f;

    [Header("Pool")]
    [Tooltip("Final pool diameter, as a fraction of the character's height.")]
    [Range(0.1f, 1.5f)] public float poolSize = 0.55f;

    [Tooltip("Seconds the pool takes to spread to full size.")]
    public float spreadTime = 8f;

    [Tooltip("How many overlapping blobs make up the pool.")]
    [Range(1, 6)] public int blobs = 4;

    public Color poolColor = new Color(0.32f, 0.01f, 0.01f, 0.95f);

    [Header("Droplets")]
    [Range(0, 40)] public int droplets = 14;
    public Color dropletColor = new Color(0.45f, 0.02f, 0.02f, 1f);

    Transform _hips, _head;
    float _standingHips, _height;
    float _stillFor;
    Vector3 _lastHips;
    bool _started;

    ParticleSystem _pool;
    float _poolT;
    float[] _blobScale;
    static Material _mat;
    static Texture2D _blobTex, _dropTex;

    void Start()
    {
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            if (_hips == null && n.EndsWith("hips")) _hips = t;
            else if (_head == null && n.EndsWith("head")) _head = t;
        }

        if (_hips == null || _head == null)
        {
            Debug.LogWarning($"[BloodPool] '{name}': no Hips / Head bone found.", this);
            enabled = false;
            return;
        }

        _height = Mathf.Max(0.3f, (_head.position.y - Ground(_head.position)) * 1.12f);
        _standingHips = Mathf.Max(0.1f, _hips.position.y - Ground(_hips.position));
        _lastHips = _hips.position;
    }

    void LateUpdate()
    {
        if (!_started)
        {
            float hipsUp = _hips.position.y - Ground(_hips.position);
            float headUp = _head.position.y - Ground(_head.position);
            bool lying = hipsUp < _standingHips * lyingHipsFraction &&
                         headUp < _standingHips * (lyingHipsFraction + 0.15f);

            float moved = (_hips.position - _lastHips).magnitude / Mathf.Max(Time.deltaTime, 1e-4f);
            _lastHips = _hips.position;

            _stillFor = lying && moved < _height * 0.25f ? _stillFor + Time.deltaTime : 0f;
            if (_stillFor >= settleTime) Begin();
            return;
        }

        if (_pool == null || _poolT >= 1f) return;

        // Spread: fast at first, slowing down - the way a pool actually grows.
        _poolT = Mathf.Min(1f, _poolT + Time.deltaTime / Mathf.Max(0.1f, spreadTime));
        float grow = 1f - Mathf.Pow(1f - _poolT, 2.2f);

        var ps = new ParticleSystem.Particle[_pool.particleCount];
        int n = _pool.GetParticles(ps);
        float full = _height * poolSize;
        for (int i = 0; i < n; i++)
            ps[i].startSize = Mathf.Max(0.001f, full * _blobScale[i % _blobScale.Length] * grow);
        _pool.SetParticles(ps, n);
    }

    void Begin()
    {
        _started = true;

        Vector3 head = _head.position;
        Vector3 away = head - _hips.position; away.y = 0f;
        away = away.sqrMagnitude > 1e-4f ? away.normalized : transform.forward;

        float ground = Ground(head);
        Vector3 center = new Vector3(head.x, ground, head.z) + away * (_height * 0.06f);

        BuildDroplets(head, away);
        BuildPool(center, away, ground);

        Debug.Log($"[BloodPool] '{name}' is down - blood started at the head.", this);
    }

    // ------------------------------------------------------------------ pool

    void BuildPool(Vector3 center, Vector3 away, float ground)
    {
        var go = new GameObject("BloodPool");
        go.transform.position = center;
        _pool = go.AddComponent<ParticleSystem>();
        _pool.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = _pool.main;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = 100000f;
        main.startSpeed = 0f;
        main.startSize = 0.001f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 8;
        main.startColor = poolColor;
        main.gravityModifier = 0f;

        var em = _pool.emission; em.enabled = false;
        var shape = _pool.shape; shape.enabled = false;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
        r.material = Mat(BlobTexture());
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.sortingFudge = -10f;

        _pool.Play();

        // A few blobs, the main one at the head and the rest drifting the way the head points,
        // all just above the road so they never z-fight with it.
        var rnd = new System.Random(GetInstanceID());
        _blobScale = new float[blobs];
        Vector3 side = Vector3.Cross(Vector3.up, away);
        float lift = Mathf.Max(0.004f, _height * 0.004f);

        for (int i = 0; i < blobs; i++)
        {
            float s = i == 0 ? 1f : 0.45f + (float)rnd.NextDouble() * 0.35f;
            _blobScale[i] = s;

            Vector3 off = i == 0 ? Vector3.zero
                : away * (_height * poolSize * (0.15f + 0.25f * (float)rnd.NextDouble()))
                  + side * (_height * poolSize * ((float)rnd.NextDouble() - 0.5f) * 0.5f);

            var ep = new ParticleSystem.EmitParams
            {
                position = new Vector3(center.x + off.x, ground + lift * (1f + i * 0.3f), center.z + off.z),
                rotation = (float)rnd.NextDouble() * 360f,
                startSize = 0.001f,
                startColor = poolColor,
                startLifetime = 100000f,
                applyShapeToPosition = false
            };
            _pool.Emit(ep, 1);
        }
    }

    // ------------------------------------------------------------------ droplets

    void BuildDroplets(Vector3 head, Vector3 away)
    {
        if (droplets <= 0) return;

        var go = new GameObject("BloodDrops");
        go.transform.position = head;
        go.transform.rotation = Quaternion.LookRotation(Vector3.up + away * 0.6f);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        float k = _height / 1.3f;   // sizes are tuned for a 1.3 m child

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.duration = 0.2f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.7f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f * k, 1.6f * k);
        main.startSize = new ParticleSystem.MinMaxCurve(0.015f * k, 0.04f * k);
        main.gravityModifier = 1.2f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startColor = dropletColor;
        main.maxParticles = droplets;

        var em = ps.emission;
        em.rateOverTime = 0f;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)droplets) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 35f;
        shape.radius = 0.03f * k;

        var col = ps.sizeOverLifetime;
        col.enabled = true;
        col.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.4f));

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.material = Mat(DropTexture());
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        ps.Play();
        Destroy(go, 3f);
    }

    // ------------------------------------------------------------------ helpers

    float Ground(Vector3 at)
    {
        var hits = Physics.RaycastAll(at + Vector3.up * 2f, Vector3.down, 20f, ~0,
                                      QueryTriggerInteraction.Ignore);
        float best = float.NegativeInfinity;
        foreach (var h in hits)
        {
            if (h.transform == transform || h.transform.IsChildOf(transform)) continue;
            if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
            if (h.collider.GetComponentInParent<RoadDriver>() != null) continue;   // cars, people
            if (h.point.y > best) best = h.point.y;
        }
        return float.IsNegativeInfinity(best) ? transform.position.y : best;
    }

    static readonly Dictionary<Texture2D, Material> _mats = new Dictionary<Texture2D, Material>();

    static Material Mat(Texture2D tex)
    {
        if (_mats.TryGetValue(tex, out var m) && m != null) return m;

        // Sprites/Default: alpha-blended, vertex-coloured, works in URP and on mobile.
        var sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        m = new Material(sh) { mainTexture = tex, name = "Blood (runtime)" };
        m.renderQueue = 3000;
        _mats[tex] = m;
        return m;
    }

    /// <summary>Irregular soft-edged blob, darker in the middle.</summary>
    static Texture2D BlobTexture()
    {
        if (_blobTex != null) return _blobTex;
        const int N = 128;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                float a = Mathf.Atan2(v, u), r = Mathf.Sqrt(u * u + v * v);
                // Wobbly edge: a few harmonics plus a little noise.
                float edge = 0.78f + 0.07f * Mathf.Sin(a * 3f + 1.3f) + 0.05f * Mathf.Sin(a * 7f + 0.4f)
                           + 0.04f * (Mathf.PerlinNoise(Mathf.Cos(a) * 2f + 5f, Mathf.Sin(a) * 2f + 5f) - 0.5f);
                float alpha = Mathf.Clamp01((edge - r) / 0.05f);
                float shade = Mathf.Lerp(0.75f, 1f, Mathf.Clamp01(r / edge));   // darker centre
                px[y * N + x] = new Color(shade, shade, shade, alpha);
            }
        t.SetPixels(px);
        t.Apply();
        _blobTex = t;
        return t;
    }

    static Texture2D DropTexture()
    {
        if (_dropTex != null) return _dropTex;
        const int N = 32;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                float r = Mathf.Sqrt(u * u + v * v);
                px[y * N + x] = new Color(1, 1, 1, Mathf.Clamp01((0.9f - r) / 0.15f));
            }
        t.SetPixels(px);
        t.Apply();
        _dropTex = t;
        return t;
    }
}
