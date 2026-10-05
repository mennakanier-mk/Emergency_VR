#if UNITY_EDITOR
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>Dumps geometry the ambulance sequence needs to Logs/Probe.txt.</summary>
[InitializeOnLoad]
static class SceneProbe
{
    static int _next;
    static readonly float[] Times = { 3f, 6f, 10f, 15f, 20f, 30f, 40f, 44f, 46f, 48f, 50f, 60f, 70f, 76f, 77f, 78f, 79f, 80f, 81f, 82f, 84f, 90f, 100f, 110f, 120f };

    static SceneProbe()
    {
        EditorApplication.delayCall += () => { if (!Application.isPlaying) Write("Logs/Probe.txt"); };
        EditorApplication.update += () =>
        {
            if (!Application.isPlaying) { _next = 0; _lookChecked = false; return; }
            LookAtWork();
            if (_next < Times.Length && Time.timeSinceLevelLoad > Times[_next])
            {
                QuickProbe($"Logs/Probe_{Times[_next]:0}.txt");
                _next++;
            }
        };
    }

    // Debug view (only while Logs/look.txt says a side number): the Scene view follows the
    // paramedic and the boy, so the work can be looked at from a fixed side.
    static bool _lookChecked; static float _lookSide;
    static void LookAtWork()
    {
        if (!_lookChecked)
        {
            _lookChecked = true; _lookSide = 0f;
            try { if (System.IO.File.Exists("Logs/look.txt")) float.TryParse(System.IO.File.ReadAllText("Logs/look.txt").Trim(), out _lookSide); } catch { }
        }
        if (_lookSide == 0f) return;
        var sv = SceneView.lastActiveSceneView; if (sv == null) return;
        var amb = Object.FindFirstObjectByType<AmbulanceResponse>(); if (amb == null || amb.rig == null) return;
        var boy = GameObject.Find("accident_scene"); if (boy == null) return;
        Transform mh = RootSpace.Find(amb.rig, "hips"), bh = RootSpace.Find(boy.transform, "hips"), hd = RootSpace.Find(boy.transform, ":head");
        if (mh == null || bh == null || hd == null) return;
        Vector3 c = (mh.position + bh.position) * 0.5f;
        Vector3 along = hd.position - bh.position; along.y = 0; along.Normalize();
        Vector3 viewDir = Quaternion.AngleAxis(_lookSide, Vector3.up) * along;          // degrees round the boy
        Quaternion rot = Quaternion.LookRotation(Quaternion.AngleAxis(25f, Vector3.Cross(Vector3.up, viewDir)) * viewDir, Vector3.up);
        sv.LookAt(c, rot, 2.2f, false, true);
        sv.Repaint();
    }

    [MenuItem("Tools/Emergency VR/Debug/Probe Ambulance Scene")]
    static void Probe() => Write(Application.isPlaying ? "Logs/ProbePlay.txt" : "Logs/Probe.txt");

    /// <summary>Just the actors of the ambulance scene.</summary>
    static void QuickProbe(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"t={Time.timeSinceLevelLoad:0.0}");
        void Line(string label, Transform t) { if (t != null) sb.AppendLine($"{label,-14} {t.position:F2} active={t.gameObject.activeInHierarchy}"); }
        var boy = GameObject.Find("accident_scene");
        if (boy != null)
        {
            foreach (var n in new[] { "hips", ":head", "leftfoot", "lefthand", "righthand" }) Line("boy" + n, RootSpace.Find(boy.transform, n));
            foreach (var c in boy.GetComponents<Behaviour>()) sb.AppendLine($"  boy comp {c.GetType().Name} enabled={c.enabled}");
            foreach (var r in boy.GetComponentsInChildren<Renderer>(true)) sb.AppendLine($"  boy renderer {r.name} enabled={r.enabled} visible={r.isVisible} bounds={r.bounds.center:F1}/{r.bounds.size:F1}");
        }
        var amb = Object.FindFirstObjectByType<AmbulanceResponse>();
        if (amb != null)
        {
            Line("ambulance", amb.transform);
            var rig = amb.rig;
            if (rig != null)
            {
                Line("rig", rig);
                foreach (var n in new[] { "hips", "lefthand", "righthand", "lefttoebase" }) Line("medic " + n, RootSpace.Find(rig, n));
                Line("STR_ROOT", rig.Find("STR_ROOT")); Line("KIT", rig.Find("KIT")); Line("FA_mask", rig.Find("FA_mask"));
                {
                    Transform kit = rig.Find("KIT"), hips = RootSpace.Find(rig, "hips"), sp2 = RootSpace.Find(rig, "spine2");
                    foreach (var side in new[] { "left", "right" })
                    {
                        Transform hd = RootSpace.Find(rig, side + "hand"), fa = RootSpace.Find(rig, side + "forearm");
                        if (hd == null || kit == null || sp2 == null) continue;
                        Vector3 rh = rig.InverseTransformPoint(hd.position), rk = rig.InverseTransformPoint(kit.position), rs = rig.InverseTransformPoint(sp2.position), re = rig.InverseTransformPoint(fa.position);
                        sb.AppendLine($"  {side}: hand-kit={(rh - rk).magnitude:0.00} handFromSpine2(flat)={new Vector2(rh.x - rs.x, rh.z - rs.z).magnitude:0.00} elbowFromSpine2(flat)={new Vector2(re.x - rs.x, re.z - rs.z).magnitude:0.00} (rig m)");
                    }
                }
                var prg = rig.GetComponent<ParamedicRig>();
                if (prg != null) sb.AppendLine($"  arms: {prg.debugArms} enabled={prg.enabled} handsOut={prg.handsOutOfBody} elbowOut={prg.elbowOut}");
                var an = rig.GetComponent<Animator>();
                if (an != null) { var st = an.GetCurrentAnimatorStateInfo(0); sb.AppendLine($"clip t={st.normalizedTime * st.length:0.00} speed={an.speed} enabled={an.enabled}"); }
                foreach (var r in rig.GetComponentsInChildren<Renderer>(true)) sb.AppendLine($"  rig renderer {r.name} enabled={r.enabled} visible={r.isVisible} bounds={r.bounds.center:F1}/{r.bounds.size:F1}");
            }
        }
        var car = Object.FindFirstObjectByType<AccidentCar>();
        if (car != null)
        {
            Line("car", car.transform);
            var rd = car.GetComponent<RoadDriver>();
            if (rd != null) sb.AppendLine($"  car driver ext={rd.externalControl} speed={rd.CurrentSpeed:0.00} d={rd.Distance:0.0} groundOffset={rd.groundOffset:0.000}");
            float lowWheel = float.MaxValue, lowAll = float.MaxValue;
            foreach (var r in car.GetComponentsInChildren<Renderer>())
            {
                bool person = r.GetComponentInParent<Animator>() != null && r.GetComponentInParent<Animator>().gameObject != car.gameObject;
                if (person) continue;
                lowAll = Mathf.Min(lowAll, r.bounds.min.y);
                if (r.name.ToLowerInvariant().Contains("wheel")) lowWheel = Mathf.Min(lowWheel, r.bounds.min.y);
            }
            float ground = float.MinValue;
            foreach (var h in Physics.RaycastAll(car.transform.position + Vector3.up * 5f, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore))
                if (!h.transform.IsChildOf(car.transform)) ground = Mathf.Max(ground, h.point.y);
            sb.AppendLine($"  car lowestWheel={lowWheel:0.000} lowestBody={lowAll:0.000} groundUnderCar={ground:0.000}");
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                sb.AppendLine($"    r {r.name} [{r.GetType().Name}] en={r.enabled} min.y={r.bounds.min.y:0.000} c={r.bounds.center:F2} s={r.bounds.size:F2} verts={(mf && mf.sharedMesh ? mf.sharedMesh.vertexCount : -1)} mats={string.Join("/", System.Array.ConvertAll(r.sharedMaterials, m => m ? m.name : "-"))}");
            }
            foreach (var h in Physics.RaycastAll(car.transform.position + Vector3.up * 5f, Vector3.down, 20f, ~0, QueryTriggerInteraction.Collide))
                sb.AppendLine($"    hit {h.collider.name} [{h.collider.GetType().Name}] trig={h.collider.isTrigger} y={h.point.y:0.000} path={(h.collider.transform.parent ? h.collider.transform.parent.name : "-")}");
            foreach (var c in car.GetComponentsInChildren<Collider>(true))
                sb.AppendLine($"    col {c.name} [{c.GetType().Name}] en={c.enabled} trig={c.isTrigger} min.y={c.bounds.min.y:0.000}");
            foreach (var p in car.GetComponentsInChildren<CarExitPassenger>()) { Line("  passenger", p.transform); Line("  passenger hips", RootSpace.Find(p.transform, "hips")); sb.AppendLine($"  passenger seated={p.Seated}"); }
        }
        if (amb != null) sb.AppendLine($"  amb loaded={amb.PatientLoaded} left={amb.HasLeft}");
        foreach (var pw in Object.FindObjectsByType<PedestrianWalker>(FindObjectsSortMode.None))
        {
            var bs = pw.GetComponent<AccidentBystander>();
            if (bs == null) continue;
            sb.AppendLine($"  bystander {pw.name} pos={pw.transform.position:F1} active={bs.enabled} roam={pw.freeRoam} speed={pw.CurrentSpeed:0.00} d={pw.Distance:0.0}");
        }
        if (RoadDriver.PlayerPosition(out Vector3 pp))
        {
            sb.AppendLine($"  player {pp:F1}");
            // The vehicles nearest the player and how they see him (for 'stop behind the player').
            var near = new System.Collections.Generic.List<RoadDriver>();
            foreach (var d in Object.FindObjectsByType<RoadDriver>(FindObjectsSortMode.None))
                if (!(d is PedestrianWalker) && d.path != null && d.path.kind == TrafficPath.PathKind.Vehicles) near.Add(d);
            near.Sort((a, b) => Vector3.Distance(a.transform.position, pp).CompareTo(Vector3.Distance(b.transform.position, pp)));
            for (int i = 0; i < Mathf.Min(3, near.Count); i++)
            {
                var d = near[i];
                d.path.Sample(d.Distance, out Vector3 sp, out Vector3 dir); dir.y = 0f; dir.Normalize();
                Vector3 off = pp - d.transform.position; float dy = off.y; off.y = 0f;
                sb.AppendLine($"  near {d.name} dist={off.magnitude:0.0} along={Vector3.Dot(off, dir):0.0} side={Vector3.Dot(off, Vector3.Cross(Vector3.up, dir)):0.0} dy={dy:0.0} half={d.HalfLength:0.0} speed={d.CurrentSpeed:0.0} ext={d.externalControl} stop={d.stopForPlayer} lane={d.playerLaneHalfWidth:0.0} pivotToPath={Vector3.Distance(new Vector3(sp.x,0,sp.z), new Vector3(d.transform.position.x,0,d.transform.position.z)):0.0}");
            }
        }

        System.IO.File.WriteAllText(file, sb.ToString());
    }

    static void Write(string file)
    {
        try { WriteInner(file); } catch (System.Exception e) { System.IO.File.WriteAllText(file, e.ToString()); }
    }

    static void WriteInner(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Probe] playing={Application.isPlaying} t={Time.timeSinceLevelLoad:0.00}");

        foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            string n = go.name;
            bool amb = n == "VEH_amb";
            bool em = go.Find("STR_ROOT") != null || n == "em";
            if (!(amb || em || n == "accident_scene" || n == "car_accident")) continue;
            Dump(sb, go, 0, amb ? 4 : 2);
        }

        // Anything holding the stretcher, wherever it is.
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t.name == "STR_ROOT" || t.name == "KIT" || t.name == "Armature.013" || t.name.StartsWith("AMB_"))
                sb.AppendLine($"  @ {Path(t)}  pos={t.position:F2} rot={t.eulerAngles:F0} lossy={t.lossyScale:F2}");

        // The boy.
        var boy = GameObject.Find("accident_scene");
        if (boy != null)
        {
            foreach (var t in boy.GetComponentsInChildren<Transform>())
            {
                string l = t.name.ToLowerInvariant();
                if (l.EndsWith("hips") || l.EndsWith(":head") || l.EndsWith("headtop_end") || l.EndsWith("leftfoot") ||
                    l.EndsWith("rightfoot") || l.EndsWith("lefthand") || l.EndsWith("righthand") || l.EndsWith("spine2"))
                    sb.AppendLine($"  boy {t.name,-28} {t.position:F2}");
            }
            var smr = boy.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr != null) sb.AppendLine($"  boy bones: {string.Join(",", smr.bones.Select(b => b ? b.name : "null"))}");
        }

        // Paths.
        foreach (var p in Object.FindObjectsByType<TrafficPath>(FindObjectsSortMode.None))
        {
            sb.Append($"  path {Path(p.transform)} len={p.Length:0.0} type={p.GetType().Name}");
            sb.AppendLine($" kind={p.kind}");
            if (boy != null)
            {
                Vector3 h = Flat(BoyHips(boy));
                for (float d = 0; d < p.Length; d += 2f)
                {
                    p.Sample(d, out Vector3 pos, out Vector3 dir);
                    float off = Vector3.Distance(Flat(pos), h);
                    if (off < 30f) sb.AppendLine($"     d={d,6:0.0} pos={pos:F1} dir={dir:F2} distToBoy={off:0.0}");
                }
            }
        }

        foreach (var r in Object.FindObjectsByType<RoadDriver>(FindObjectsSortMode.None))
            sb.AppendLine($"  driver {r.name,-24} path={(r.path ? r.path.name : "-")} d={r.Distance:0.0} pos={r.transform.position:F1} half={r.HalfLength:0.0}");

        System.IO.File.WriteAllText(file, sb.ToString());
    }

    static Vector3 Flat(Vector3 v) { v.y = 0; return v; }
    static Vector3 BoyHips(GameObject boy)
    {
        foreach (var t in boy.GetComponentsInChildren<Transform>())
            if (t.name.ToLowerInvariant().EndsWith("hips")) return t.position;
        return boy.transform.position;
    }

    static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

    static void Dump(StringBuilder sb, Transform t, int depth, int maxDepth)
    {
        var b = new Bounds(); bool has = false;
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        { if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds); }
        var comps = string.Join(",", t.GetComponents<Component>().Where(c => c && !(c is Transform)).Select(c => c.GetType().Name));
        sb.AppendLine($"{new string(' ', depth * 2)}- {t.name}  pos={t.position:F2} lpos={t.localPosition:F2} rot={t.eulerAngles:F0} " +
                      $"ls={t.localScale:F2} lossy={t.lossyScale:F2} active={t.gameObject.activeInHierarchy} " +
                      (has ? $"bounds c={b.center:F2} s={b.size:F2} " : "") + $"[{comps}]");
        if (depth >= maxDepth) return;
        foreach (Transform c in t) Dump(sb, c, depth + 1, maxDepth);
    }
}
#endif
