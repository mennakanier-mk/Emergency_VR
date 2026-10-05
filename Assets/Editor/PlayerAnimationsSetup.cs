using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Tools > Emergency > Setup Player Animations
///
/// Hooks Mixamo animations up to the player:
///   1. Download from mixamo.com (Format FBX, Skin: "Without Skin" is enough):
///        - an Idle  (e.g. "Idle" / "Breathing Idle")   -> file name containing "idle"
///        - a Jump   (e.g. "Jump")                      -> file name containing "jump"
///        - optional a Run, to replace the current one  -> file name containing "run"
///   2. Drop the .fbx files into Assets/player (any sub-folder).
///   3. Run this menu.
///
/// Everything is switched to Humanoid, so Unity retargets the Mixamo motion onto this
/// character even though its skeleton came from a different file. Loops are set on Idle and Run.
/// </summary>
public static class PlayerAnimationsSetup
{
    const string kFolder = "Assets/player";
    const string kPlayerModel = "Assets/player/player.fbx";

    [MenuItem("Tools/Emergency/Setup Player Animations")]
    static void Setup()
    {
        if (Application.isPlaying)
        {
            EditorUtility.DisplayDialog("Player animations", "Stop Play mode first.", "OK");
            return;
        }

        var log = new System.Text.StringBuilder("[PlayerAnimations]\n");

        // .anim clips already in Assets/player (Generic, paths like "mixamorig:Hips/...")
        // are used directly - no Humanoid conversion needed.
        if (SetupFromAnimFiles(log)) return;

        // ---------------------------------------------------------- 1. the character itself
        Avatar playerAvatar = MakeHumanoid(kPlayerModel, loop: true, log);
        if (playerAvatar == null)
        {
            Debug.LogError("[PlayerAnimations] player.fbx could not be made Humanoid, so Mixamo " +
                           "animations cannot be retargeted onto it. Check the Rig tab of player.fbx.\n" + log);
            return;
        }

        // ---------------------------------------------------------- 2. the Mixamo files
        AnimationClip idle = null, jump = null, run = null;

        var files = Directory.GetFiles(kFolder, "*.fbx", SearchOption.AllDirectories)
                             .Select(p => p.Replace('\\', '/'))
                             .Where(p => p != kPlayerModel);

        foreach (var path in files)
        {
            string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            string kind = n.Contains("jump") ? "jump"
                        : (n.Contains("idle") || n.Contains("stand") || n.Contains("breath")) ? "idle"
                        : (n.Contains("run") || n.Contains("jog") || n.Contains("sprint")) ? "run"
                        : null;
            if (kind == null) { log.AppendLine($"  skipped {path} (name has no idle / jump / run)"); continue; }

            if (MakeHumanoid(path, loop: kind != "jump", log) == null)
            {
                log.AppendLine($"  {path}: could not be made Humanoid - skipped");
                continue;
            }

            var clip = FirstClip(path);
            if (clip == null) { log.AppendLine($"  {path}: no animation inside"); continue; }

            if (kind == "idle") idle = clip;
            else if (kind == "jump") jump = clip;
            else run = clip;
            log.AppendLine($"  {kind,-5} <- {path} ('{clip.name}', {clip.length:0.00}s)");
        }

        // ---------------------------------------------------------- 3. hook up the Player
        var player = Object.FindFirstObjectByType<ThirdPersonPlayer>(FindObjectsInactive.Include);
        if (player == null)
        {
            Debug.LogWarning("[PlayerAnimations] No Player in the open scene. Run " +
                             "Tools > Emergency > Use Character Instead Of VR first.\n" + log);
            return;
        }

        Undo.RecordObject(player, "Player animations");
        if (run != null) player.moveClip = run;
        else player.moveClip = FirstClip(kPlayerModel);
        if (idle != null) player.idleClip = idle;
        if (jump != null) player.jumpClip = jump;
        EditorUtility.SetDirty(player);

        if (player.model != null)
        {
            var anim = player.model.GetComponentInChildren<Animator>();
            if (anim == null) anim = Undo.AddComponent<Animator>(player.model.gameObject);
            Undo.RecordObject(anim, "Player avatar");
            anim.avatar = playerAvatar;
            anim.applyRootMotion = false;
            EditorUtility.SetDirty(anim);
        }

        EditorSceneManager.MarkSceneDirty(player.gameObject.scene);

        log.AppendLine($"  Player: run '{N(player.moveClip)}', idle '{N(player.idleClip)}', jump '{N(player.jumpClip)}'");
        if (idle == null) log.AppendLine("  (no idle file found - built-in standing pose is used)");
        if (jump == null) log.AppendLine("  (no jump file found - the character jumps in its run pose)");
        log.AppendLine("  Save the scene (Ctrl+S).");
        Debug.Log(log.ToString());
    }

    static string N(Object o) => o != null ? o.name : "none";

    const string kFixedFolder = "Assets/player/Fixed";

    /// <summary>A copy of the clip with every path re-rooted at 'root', found by bone name.</summary>
    static AnimationClip Retarget(AnimationClip src, Transform root, Dictionary<string, Transform> byName,
                                  bool loop, out int moved, out int missing)
    {
        moved = 0; missing = 0;
        var dst = new AnimationClip { name = src.name, frameRate = src.frameRate };

        // Where the motion really ends. Exported clips are often padded: run.anim moves for
        // 0.63 s and then every bone holds still until 8.3 s, so a loop over the full length
        // ran once and then froze. Cut at the last moment any bone still changes.
        var bindings = AnimationUtility.GetCurveBindings(src);
        var curves = bindings.Select(b => AnimationUtility.GetEditorCurve(src, b)).ToArray();

        float activeEnd = 0f;
        foreach (var c in curves) activeEnd = Mathf.Max(activeEnd, LastChange(c));
        if (activeEnd < 1f / 60f) activeEnd = src.length;

        // A looping clip is cut at the end of its first full cycle: the first moment after the
        // start where every bone is back in its starting pose. That is a seamless loop.
        // Only for run/walk: an idle barely moves, so its pose "returns" almost immediately.
        string lower = src.name.ToLowerInvariant();
        bool isGait = lower.Contains("run") || lower.Contains("walk") || lower.Contains("jog") || lower.Contains("sprint");
        if (loop && isGait)
        {
            float cycle = FindCycleEnd(curves, bindings, activeEnd);
            if (cycle > 0f) activeEnd = cycle;
        }

        for (int ci = 0; ci < bindings.Length; ci++)
        {
            var b = bindings[ci];
            var curve = Trim(curves[ci], activeEnd);
            var nb = b;
            if (!string.IsNullOrEmpty(b.path))
            {
                string last = b.path.Substring(b.path.LastIndexOf('/') + 1);
                if (byName.TryGetValue(last, out var t))
                {
                    string p = AnimationUtility.CalculateTransformPath(t, root);
                    if (p != b.path) moved++;
                    nb.path = p;
                }
                else missing++;
            }
            AnimationUtility.SetEditorCurve(dst, nb, curve);
        }

        // The clip's play range must follow the cut, otherwise it still plays to the old end
        // (this is why the first trimmed run still lasted 8.3 s).
        var s = AnimationUtility.GetAnimationClipSettings(src);
        s.loopTime = loop;
        s.startTime = 0f;
        s.stopTime = activeEnd;
        AnimationUtility.SetAnimationClipSettings(dst, s);
        return dst;
    }

    /// <summary>
    /// First time (after a minimum) where all rotation curves are back at their start value.
    /// Only rotations are compared: the hips' position may drift forward in a run that is not
    /// "in place", and that is handled at runtime.
    /// </summary>
    static float FindCycleEnd(AnimationCurve[] curves, EditorCurveBinding[] bindings, float maxEnd)
    {
        var rot = new List<AnimationCurve>();
        for (int i = 0; i < curves.Length; i++)
            if (curves[i] != null && curves[i].length > 1 && bindings[i].propertyName.Contains("Rotation"))
                rot.Add(curves[i]);
        if (rot.Count == 0) return 0f;

        const float step = 1f / 60f;
        float best = 0f, bestErr = float.MaxValue;
        for (float t = 0.3f; t <= maxEnd + 1e-4f; t += step)
        {
            float err = 0f;
            foreach (var c in rot) err = Mathf.Max(err, Mathf.Abs(c.Evaluate(t) - c.Evaluate(0f)));
            if (err < 0.01f) return t;                       // clean loop point
            if (err < bestErr) { bestErr = err; best = t; }
        }
        return bestErr < 0.05f ? best : 0f;
    }

    /// <summary>Time of the last key after which the curve never changes again.</summary>
    static float LastChange(AnimationCurve c)
    {
        if (c == null || c.length < 2) return 0f;
        var keys = c.keys;
        float final = keys[keys.Length - 1].value;
        for (int i = keys.Length - 1; i >= 0; i--)
            if (Mathf.Abs(keys[i].value - final) > 1e-4f)
                return i + 1 < keys.Length ? keys[i + 1].time : keys[i].time;
        return 0f;                              // constant the whole way
    }

    /// <summary>The curve with every key after 'end' removed (and one key exactly at 'end').</summary>
    static AnimationCurve Trim(AnimationCurve c, float end)
    {
        if (c == null) return null;
        var keep = new List<Keyframe>();
        foreach (var k in c.keys)
            if (k.time <= end + 1e-4f) keep.Add(k);
        if (keep.Count == 0 || Mathf.Abs(keep[keep.Count - 1].time - end) > 1e-4f)
        {
            var k = new Keyframe(end, c.Evaluate(end));
            keep.Add(k);
        }
        var r = new AnimationCurve(keep.ToArray()) { preWrapMode = c.preWrapMode, postWrapMode = c.postWrapMode };
        return r;
    }

    /// <summary>
    /// Reads when the jump clip leaves the ground and lands, from the hips height: after the
    /// crouch the hips rise past their starting height (takeoff), and later drop back below it
    /// (landing).
    /// </summary>
    static bool FindJumpTiming(AnimationClip clip, Transform root, out float takeoff, out float land)
    {
        takeoff = 0f; land = clip.length;
        var hips = root.GetComponentsInChildren<Transform>(true)
                       .FirstOrDefault(t => t.name == "Hips" || t.name.EndsWith(":Hips"));
        if (hips == null) return false;

        var b = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(hips, root),
                                              typeof(Transform), "m_LocalPosition.y");
        var c = AnimationUtility.GetEditorCurve(clip, b);
        if (c == null || c.length < 2) return false;

        const float step = 1f / 60f;
        float start = c.Evaluate(0f), peak = start, peakT = 0f;
        for (float t = 0; t <= clip.length; t += step)
        {
            float v = c.Evaluate(t);
            if (v > peak) { peak = v; peakT = t; }
        }
        if (peak - start < 0.05f) return false;          // no real lift in this clip

        float threshold = start + (peak - start) * 0.1f;
        takeoff = -1f; land = -1f;
        for (float t = peakT; t >= 0f; t -= step)
            if (c.Evaluate(t) <= threshold) { takeoff = t; break; }
        for (float t = peakT; t <= clip.length; t += step)
            if (c.Evaluate(t) <= threshold) { land = t; break; }

        if (takeoff < 0f) takeoff = 0f;
        if (land < 0f) land = clip.length;
        return land > takeoff;
    }

    static string Kind(string fileName)
    {
        string n = fileName.ToLowerInvariant();
        if (n.Contains("jump")) return "jump";
        if (n.Contains("walk")) return "walk";
        if (n.Contains("run") || n.Contains("jog") || n.Contains("sprint")) return "run";
        if (n.Contains("idle") || n.Contains("stand") || n.Contains("breath")) return "idle";
        return null;
    }

    /// <summary>
    /// Generic .anim files (Mixamo clips saved in Unity). Their curves are addressed by path,
    /// e.g. "mixamorig:Hips/mixamorig:Spine", so the Animator must sit on the object whose
    /// child is mixamorig:Hips - found here and stored on the Player as animatorRoot.
    /// </summary>
    static bool SetupFromAnimFiles(System.Text.StringBuilder log)
    {
        var paths = Directory.GetFiles(kFolder, "*.anim", SearchOption.AllDirectories)
                             .Select(p => p.Replace('\\', '/'))
                             .Where(p => !p.StartsWith(kFixedFolder + "/"))
                             .ToList();
        if (paths.Count == 0) return false;

        var player = Object.FindFirstObjectByType<ThirdPersonPlayer>(FindObjectsInactive.Include);
        if (player == null || player.model == null)
        {
            Debug.LogWarning("[PlayerAnimations] No Player in the open scene. Run " +
                             "Tools > Emergency > Use Character Instead Of VR first.");
            return true;
        }

        AnimationClip idle = null, jump = null, run = null, walk = null;

        // Every clip is rewritten so its paths start at the MODEL root. The originals did not
        // agree (run: "Armature/mixamorig:Hips/...", stand/jump: "mixamorig:Hips/..."), so with
        // one Animator one of them always failed to bind - the character fell into T-pose.
        Transform root = player.model;
        var byName = new Dictionary<string, Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (!byName.ContainsKey(t.name)) byName[t.name] = t;

        Directory.CreateDirectory(kFixedFolder);

        foreach (var path in paths)
        {
            var src = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (src == null) continue;
            string kind = Kind(Path.GetFileNameWithoutExtension(path));
            if (kind == null) { log.AppendLine($"  skipped {path} (name has no idle/stand/walk/run/jump)"); continue; }

            bool loop = kind != "jump";
            var clip = Retarget(src, root, byName, loop, out int moved, out int missing);
            string outPath = $"{kFixedFolder}/{Path.GetFileNameWithoutExtension(path)}.anim";
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(outPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(clip, existing);
                Object.DestroyImmediate(clip);
                clip = existing;
            }
            else AssetDatabase.CreateAsset(clip, outPath);
            EditorUtility.SetDirty(clip);

            if (kind == "idle") idle = clip;
            else if (kind == "jump") jump = clip;
            else if (kind == "walk") walk = clip;
            else run = clip;
            log.AppendLine($"  {kind,-5} <- {path} ({clip.length:0.00}s{(loop ? ", loop" : "")}, " +
                           $"{moved} paths fixed{(missing > 0 ? $", {missing} bones not in character" : "")})");
        }
        AssetDatabase.SaveAssets();

        Undo.RecordObject(player, "Player animations");
        if (run != null) player.moveClip = run;
        if (walk != null) player.walkClip = walk;
        if (idle != null) player.idleClip = idle;
        if (jump != null)
        {
            player.jumpClip = jump;
            if (FindJumpTiming(jump, root, out float takeoff, out float land))
            {
                player.jumpTakeoffTime = takeoff;
                player.jumpLandTime = land;
                player.jumpAnimationSpeed = 1f;
                // Physics air time matched to the clip's: h = g * (T/2)^2 / 2
                float air = Mathf.Max(0.3f, land - takeoff);
                player.jumpHeight = Mathf.Clamp(Mathf.Abs(player.gravity) * (air * 0.5f) * (air * 0.5f) * 0.5f, 0.4f, 2f);
                log.AppendLine($"  jump: leaves the ground at {takeoff:0.00}s, lands at {land:0.00}s -> height {player.jumpHeight:0.00} m");
            }
            else log.AppendLine("  jump: could not read takeoff from the clip - using defaults");
        }
        player.animatorRoot = root;
        EditorUtility.SetDirty(player);
        EditorSceneManager.MarkSceneDirty(player.gameObject.scene);

        log.AppendLine($"  Animator on '{root.name}'");
        log.AppendLine($"  Player: run '{N(player.moveClip)}', walk '{N(player.walkClip)}', " +
                       $"idle '{N(player.idleClip)}', jump '{N(player.jumpClip)}'");
        log.AppendLine("  Save the scene (Ctrl+S).");
        Debug.Log(log.ToString());
        Selection.activeGameObject = player.gameObject;
        return true;
    }

    /// <summary>Sets the file to Humanoid with its own avatar, loops its clips if asked.</summary>
    static Avatar MakeHumanoid(string path, bool loop, System.Text.StringBuilder log)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null) return null;

        bool changed = false;
        var originalType = importer.animationType;
        var originalSetup = importer.avatarSetup;

        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            changed = true;
        }

        // Loop + keep the clip in place relative to the character.
        var clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
        foreach (var c in clips)
        {
            if (c.loopTime != loop) { c.loopTime = loop; changed = true; }
            if (!c.lockRootRotation) { c.lockRootRotation = true; changed = true; }       // bake rotation
            if (!c.lockRootPositionXZ) { c.lockRootPositionXZ = true; changed = true; }   // bake XZ
            if (!c.keepOriginalOrientation) { c.keepOriginalOrientation = true; changed = true; }
            if (!c.keepOriginalPositionY) { c.keepOriginalPositionY = true; changed = true; }
            if (!c.keepOriginalPositionXZ) { c.keepOriginalPositionXZ = true; changed = true; }
        }
        if (clips.Length > 0) importer.clipAnimations = clips;

        if (changed)
        {
            importer.SaveAndReimport();
            log.AppendLine($"  {path}: Humanoid{(loop ? ", loop" : "")}");
        }

        var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
        {
            log.AppendLine($"  {path}: Humanoid avatar is NOT valid - import settings put back");
            if (originalType != ModelImporterAnimationType.Human)
            {
                importer.animationType = originalType;
                importer.avatarSetup = originalSetup;
                importer.SaveAndReimport();
            }
            return null;
        }
        return avatar;
    }

    static AnimationClip FirstClip(string path)
    {
        return AssetDatabase.LoadAllAssetsAtPath(path)
            .OfType<AnimationClip>()
            .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
    }
}
