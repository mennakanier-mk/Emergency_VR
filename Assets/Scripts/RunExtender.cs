using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Makes a baked one-shot clip run further: repeats one stride of the run and carries the
/// character (and the ball) forward by that stride's distance each time, so he ends up - and
/// falls - further out in the road. Arms, legs and the fall still play exactly as animated.
///
/// How: when the clip reaches Loop End, the Animator is sent back to Loop Start (exactly one
/// stride earlier), and every tracked part is shifted forward by how far it travelled across
/// that stride. The pose at Loop Start is the same as at Loop End, so the jump is invisible and
/// the run just keeps going.
///
/// Defaults are measured from accident_scene.fbx: the run is Blender frames 68-104 at 24 fps,
/// one full stride whose start and end poses match (feet and hip height) is frames 86-102, which is 3.5 s - 4.1667 s
/// in the clip. Each extra stride is about 0.67 s and roughly 3 m at this object's scale.
///
/// Tracks the same parts as Ground Follow Animated (the Hips, plus any direct-child mesh such as
/// the ball) and runs just before it, so the ground-follow sees the extended positions.
/// </summary>
[DefaultExecutionOrder(900)]    // after the Animator, before GroundFollowAnimated (1000)
[DisallowMultipleComponent]
public class RunExtender : MonoBehaviour
{
    [Tooltip("Times one stride is repeated. Each = ~0.67 s and ~3 m more running. Keep it at 0-1: " +
             "more and the repetition starts to show.")]
    [Range(0, 4)] public int repeatStrides = 1;

    [Tooltip("Longer strides during the run (1 = as animated). Adds distance without repeating " +
             "anything: 1.3 = every stride covers 30% more ground.")]
    [Range(1f, 1.6f)] public float strideLength = 1.3f;

    [Tooltip("Clip time (s) where the run starts / where the fall starts. Strides are lengthened " +
             "only between these, so the walk before and the fall itself stay as animated.")]
    public float runStart = 2.83f;
    public float runEnd = 4.33f;

    [Tooltip("Clip time (s) where the repeated stride starts.")]
    public float loopStart = 3.5f;

    [Tooltip("Clip time (s) where it ends - exactly one stride after Loop Start.")]
    public float loopEnd = 4.1667f;

    [Tooltip("Also carry loose props (the ball) forward with the boy. Off: the ball rolls away on its own path as animated.")]
    public bool includeProps = false;

    [Tooltip("More parts to carry forward.")]
    public List<Transform> extraTargets = new List<Transform>();

    class Part
    {
        public Transform t;
        public Vector3 startRaw;     // raw animated position at Loop Start
        public Vector3 stride;       // horizontal distance over one stride
        public Vector3 carried;      // accumulated offset applied every frame
        public Vector3 stretch;      // extra distance from the longer strides
        public Vector3 prevRaw;
        public bool havePrev;
    }

    Animator _anim;
    readonly List<Part> _parts = new List<Part>();
    int _done;
    bool _haveStart, _haveStride, _rewound;

    /// <summary>Seconds the repeats add to the clip - AccidentCar adds this to its impact time.</summary>
    public float ExtraTime => enabled ? repeatStrides * Mathf.Max(0f, loopEnd - loopStart) : 0f;

    void Start()
    {
        _anim = GetComponentInChildren<Animator>();

        var set = new HashSet<Transform>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
            if (t.name.ToLowerInvariant().EndsWith("hips")) { set.Add(t); break; }

        if (includeProps)
            foreach (Transform child in transform)
            {
                var r = child.GetComponent<Renderer>();
                if (r != null && !(r is SkinnedMeshRenderer)) set.Add(child);
            }

        foreach (var t in extraTargets) if (t != null) set.Add(t);
        foreach (var t in set) _parts.Add(new Part { t = t });

        if (_anim == null || _parts.Count == 0)
        {
            Debug.LogWarning($"[RunExtender] '{name}': no Animator or nothing to move.", this);
            enabled = false;
            return;
        }

        MeasureStrideExactly();
    }

    /// <summary>
    /// The stride is measured from the clip itself at exactly Loop Start and Loop End, not from
    /// whichever two frames happened to land past those times. Frame-to-frame measuring was off
    /// by up to one frame of running (~10-15 cm), and that error was the hop seen every time the
    /// stride repeated.
    /// </summary>
    void MeasureStrideExactly()
    {
        var ctrl = _anim.runtimeAnimatorController;
        if (ctrl == null || ctrl.animationClips.Length == 0) return;
        var clip = ctrl.animationClips[0];

        var go = _anim.gameObject;
        clip.SampleAnimation(go, loopStart);
        var a = new Vector3[_parts.Count];
        for (int i = 0; i < _parts.Count; i++) a[i] = _parts[i].t.position;

        clip.SampleAnimation(go, loopEnd);
        for (int i = 0; i < _parts.Count; i++)
        {
            Vector3 d = _parts[i].t.position - a[i];
            d.y = 0f;
            _parts[i].stride = d;
        }

        clip.SampleAnimation(go, 0f);   // back to the first frame
        _haveStride = true;
        _haveStart = true;
    }

    void LateUpdate()
    {
        var info = _anim.GetCurrentAnimatorStateInfo(0);
        float length = info.length;
        float time = info.normalizedTime * length;

        // 1. Measure on the raw positions the Animator just wrote.
        bool rewind = false;
        if (_done < repeatStrides)
        {
            if (!_haveStart && time >= loopStart)
            {
                foreach (var p in _parts) p.startRaw = p.t.position;
                _haveStart = true;
            }

            if (_haveStart && time >= loopEnd)
            {
                if (!_haveStride)
                {
                    foreach (var p in _parts)
                    {
                        Vector3 d = p.t.position - p.startRaw;
                        d.y = 0f;
                        p.stride = d;
                    }
                    _haveStride = true;
                }
                rewind = true;
            }
        }

        // 2. Longer strides: add a share of this frame's forward movement, eased in and out at
        //    the edges of the run so the speed never pops.
        float k = (strideLength - 1f) * Ramp(time);
        foreach (var p in _parts)
        {
            Vector3 raw = p.t.position;
            if (p.havePrev)
            {
                Vector3 d = raw - p.prevRaw;
                if (_rewound) d += p.stride;     // the clip just jumped back one stride
                d.y = 0f;
                if (d.sqrMagnitude < 1f) p.stretch += d * k;
            }
            p.prevRaw = raw;
            p.havePrev = true;
        }
        _rewound = false;

        // 3. This frame still shows the end of the stride with the offset carried so far.
        foreach (var p in _parts)
            p.t.position += p.carried + p.stretch;

        // 4. From next frame: back one stride, carried one stride further. Same pose, same spot.
        if (rewind)
        {
            foreach (var p in _parts) p.carried += p.stride;
            _done++;
            _rewound = true;

            float back = loopStart + (time - loopEnd);   // keep the overshoot - no hitch
            _anim.Play(info.fullPathHash, 0, back / Mathf.Max(0.0001f, length));
        }
    }

    float Ramp(float t)
    {
        const float edge = 0.25f;
        float a = Mathf.Clamp01((t - runStart) / edge);
        // Still repeats to come: the clip will jump back before the run ends, so no ease-out yet.
        float b = _done < repeatStrides ? 1f : Mathf.Clamp01((runEnd - t) / edge);
        return Mathf.SmoothStep(0f, 1f, Mathf.Min(a, b));
    }

    /// <summary>Restart from the beginning (e.g. when the scene event is retriggered).</summary>
    public void ResetRun()
    {
        _done = 0;
        foreach (var p in _parts) { p.carried = Vector3.zero; p.stretch = Vector3.zero; p.havePrev = false; }
    }
}
