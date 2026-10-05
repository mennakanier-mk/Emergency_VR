using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Sounds put on the traffic (cars, the donkey and horse carts, motorbikes, bicycles):
///  - a vehicle's sound plays (once, not on a loop) when it comes close to you - just behind or
///    in front of the camera - and can play again the next time it comes close;
///  - the ambulance's sound plays all the time, on a loop;
///  - all of them are 3D: a little louder right next to you, very quiet further away.
///
/// Added by itself to every vehicle (any RoadDriver that is not a pedestrian) that has an
/// AudioSource, when a scene loads. The numbers can be changed on the component in Play.
/// Distances are world units: the people here are about 3 units tall (1 unit = about 60 cm).
/// </summary>
[DisallowMultipleComponent]
public class VehicleSounds : MonoBehaviour
{
    [Tooltip("Plays all the time on a loop (the ambulance). Others play when they come close.")]
    public bool alwaysOn;
    [Tooltip("Starts its sound when it comes this close to you, world units.")]
    public float hearDistance = 16f;
    [Tooltip("It counts as gone (and may play again next time) beyond this, world units.")]
    public float leaveDistance = 24f;
    [Tooltip("Full volume up to this distance, world units.")]
    public float nearDistance = 6f;
    [Tooltip("Very quiet beyond this, world units.")]
    public float farDistance = 70f;
    [Range(0f, 0.3f)] public float farVolume = 0.04f;

    [Header("Waiting for you")]
    [Tooltip("When it stops because you are in front of it, its sound plays again and gets louder.")]
    public bool louderWhenBlocked = true;
    [Tooltip("Seconds between repeats while it keeps waiting.")]
    public float blockedRepeat = 1.2f;
    [Tooltip("How fast it gets louder (per second).")]
    public float louderSpeed = 1.5f;

    AudioSource[] _sources;
    float[] _baseVolume, _baseBlend;
    bool _near;
    RoadDriver _driver;
    float _blockedLevel, _nextBlockedPlay;

    // ------------------------------------------------------------------ installer

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
        EnsureListener();
        int n = 0;
        foreach (var d in FindObjectsByType<RoadDriver>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (d is PedestrianWalker) continue;
            if (d.path != null && d.path.kind != TrafficPath.PathKind.Vehicles) continue;
            bool any = false;
            foreach (var a in d.GetComponentsInChildren<AudioSource>(true)) if (a.clip != null) { any = true; break; }
            if (!any) continue;
            var vs = d.GetComponent<VehicleSounds>();
            if (vs == null) vs = d.gameObject.AddComponent<VehicleSounds>();
            vs.alwaysOn = d.GetComponent<AmbulanceResponse>() != null;
            n++;
        }
        if (n > 0) Debug.Log($"[VehicleSounds] {n} vehicles with sound (ambulance always on, others when close).");
    }

    // ------------------------------------------------------------------ per vehicle

    void Start()
    {
        var list = new List<AudioSource>();
        foreach (var a in GetComponentsInChildren<AudioSource>(true))
            if (a.clip != null) list.Add(a);
        _sources = list.ToArray();
        _driver = GetComponent<RoadDriver>();
        _baseVolume = new float[_sources.Length];
        _baseBlend = new float[_sources.Length];
        for (int i = 0; i < _sources.Length; i++) _baseVolume[i] = _sources[i].volume;

        var curve = Curve();
        foreach (var a in _sources)
        {
            a.spatialBlend = 1f;                     // 3D: depends on where you are
            a.dopplerLevel = 0f;                     // no pitch wobble as it passes
            a.spread = 0f;
            a.rolloffMode = AudioRolloffMode.Custom;
            a.minDistance = 1f;
            a.maxDistance = farDistance;
            a.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
            a.loop = alwaysOn;
            a.playOnAwake = alwaysOn;
            if (alwaysOn) { if (!a.isPlaying) a.Play(); }
            else a.Stop();
        }
        for (int i = 0; i < _sources.Length; i++) _baseBlend[i] = _sources[i].spatialBlend;
    }

    void Update()
    {
        if (_sources == null || _sources.Length == 0) return;

        // Stopped (or braking) because you are in front of it: sound off, rising, repeating.
        bool blocked = louderWhenBlocked && _driver != null && _driver.WaitingForPlayer;
        _blockedLevel = Mathf.MoveTowards(_blockedLevel, blocked ? 1f : 0f,
                                          Time.deltaTime * (blocked ? louderSpeed : 2f));
        for (int i = 0; i < _sources.Length; i++)
        {
            var a = _sources[i];
            if (a == null) continue;
            a.volume = Mathf.Lerp(_baseVolume[i], 1f, _blockedLevel);
            // Less "far away" as it gets louder, so it is clearly heard in both ears.
            a.spatialBlend = Mathf.Lerp(_baseBlend[i], 0.5f, _blockedLevel);
        }
        if (blocked && Time.time >= _nextBlockedPlay)
        {
            _nextBlockedPlay = Time.time + blockedRepeat;
            foreach (var a in _sources)
                if (a != null && a.isActiveAndEnabled && !a.isPlaying) a.Play();
        }

        if (alwaysOn) return;
        if (!RoadDriver.PlayerPosition(out Vector3 p)) return;

        float d = Vector3.Distance(p, transform.position);
        if (!_near && d < hearDistance)
        {
            _near = true;
            foreach (var a in _sources) if (a.isActiveAndEnabled) { a.loop = false; a.Play(); }
        }
        else if (_near && d > leaveDistance)
        {
            _near = false;                           // gone: it may play again when it comes back
        }
    }

    AnimationCurve Curve()
    {
        float n = Mathf.Clamp01(nearDistance / Mathf.Max(1f, farDistance));
        var c = new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(n, 1f),
            new Keyframe(Mathf.Lerp(n, 1f, 0.25f), 0.35f),
            new Keyframe(Mathf.Lerp(n, 1f, 0.55f), 0.12f),
            new Keyframe(1f, farVolume));
        for (int i = 0; i < c.length; i++) c.SmoothTangents(i, 0f);
        return c;
    }

    /// <summary>Sound is heard from the VR head. The phone rig keeps its head camera switched off,
    /// so make sure there is an enabled AudioListener there.</summary>
    static void EnsureListener()
    {
        foreach (var l in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            if (l.isActiveAndEnabled) return;
        var vr = FindFirstObjectByType<SimpleStereoVR>();
        Transform head = vr != null && vr.headCamera != null ? vr.headCamera.transform
                       : (Camera.main != null ? Camera.main.transform : null);
        if (head == null) return;
        var listener = head.GetComponent<AudioListener>();
        if (listener == null) listener = head.gameObject.AddComponent<AudioListener>();
        listener.enabled = true;
    }
}
