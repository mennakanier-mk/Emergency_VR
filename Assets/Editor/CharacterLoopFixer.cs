using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Fix Character Walk Loop (Selection)
///   Tools > Emergency VR > Fix Character Walk Loop (All In Scene)
///
/// Fixes "walks one step, then glides along the path with no animation".
///
/// Every character imported so far arrived the same way: the Animator Controller plays a
/// standalone "Scene.anim" that was duplicated out of the FBX before its range was trimmed. That
/// copy is the whole Blender timeline (8.3 s) - one stride, then several seconds of standing
/// still - so it loops, but only after a long freeze. Editing Loop Time / Start / End on the FBX
/// changes nothing, because the controller is not using the FBX clip at all.
///
/// For each Animator on the selected objects (or in the scene) this:
///   1. finds the character's own model file (the FBX its prefab instance comes from);
///   2. turns on Loop Time + Loop Pose for that FBX's clip (keeping your Start/End frames);
///   3. points every controller state that plays a duplicated .anim at the FBX clip instead.
/// Clips that already come from the FBX just get Loop Time / Loop Pose switched on.
///
/// The stale .anim files are left where they are - delete them once you are happy.
/// </summary>
public static class CharacterLoopFixer
{
    const string MenuSel = "Tools/Emergency VR/Fix Character Walk Loop (Selection)";
    const string MenuAll = "Tools/Emergency VR/Fix Character Walk Loop (All In Scene)";

    [MenuItem(MenuSel)]
    static void FixSelection()
    {
        var animators = Selection.gameObjects
            .SelectMany(g => g.GetComponentsInChildren<Animator>(true))
            .Distinct().ToList();
        Run(animators);
    }

    [MenuItem(MenuSel, true)]
    static bool FixSelectionValidate() => Selection.gameObjects.Length > 0;

    [MenuItem(MenuAll)]
    static void FixAll()
    {
        var animators = new List<Animator>();
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            animators.AddRange(root.GetComponentsInChildren<Animator>(true));
        Run(animators);
    }

    static void Run(List<Animator> animators)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[WalkLoop] Exit Play mode first.");
            return;
        }

        var log = new StringBuilder("[WalkLoop]\n");
        var doneControllers = new HashSet<AnimatorController>();
        int fixedStates = 0, loopedClips = 0;

        foreach (var animator in animators)
        {
            if (animator == null) continue;
            var controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller == null)
            {
                log.AppendLine($"  {animator.name}: no Animator Controller - skipped");
                continue;
            }
            if (!doneControllers.Add(controller)) continue;

            string modelPath = FindModelPath(animator.gameObject);
            var modelClips = modelPath != null ? LoadModelClips(modelPath) : new List<AnimationClip>();

            foreach (var state in AllStates(controller))
            {
                var clip = state.motion as AnimationClip;
                if (clip == null) continue;

                string clipPath = AssetDatabase.GetAssetPath(clip);
                bool fromModel = AssetImporter.GetAtPath(clipPath) is ModelImporter;

                if (fromModel)
                {
                    if (EnableLoop(clipPath, clip.name)) loopedClips++;
                    log.AppendLine($"  {controller.name} / {state.name}: FBX clip '{clip.name}' - loop on");
                    continue;
                }

                // A standalone .anim. Swap it for the model's own clip if there is one.
                if (modelClips.Count == 0)
                {
                    SetStandaloneLoop(clip);
                    log.AppendLine($"  {controller.name} / {state.name}: '{clip.name}.anim' has no FBX " +
                                   $"to switch to - turned its loop on ({clip.length:0.##} s). If it " +
                                   "still pauses, the clip itself is too long.");
                    continue;
                }

                var target = modelClips.FirstOrDefault(c => c.name == clip.name) ?? modelClips[0];
                if (EnableLoop(modelPath, target.name)) loopedClips++;

                // Reload after reimport - the old reference is stale once the FBX reimports.
                target = LoadModelClips(modelPath).FirstOrDefault(c => c.name == target.name) ?? target;

                Undo.RecordObject(state, "Fix Walk Loop");
                state.motion = target;
                EditorUtility.SetDirty(state);
                fixedStates++;

                log.AppendLine($"  {controller.name} / {state.name}: '{clipPath}' ({clip.length:0.##} s) " +
                               $"-> FBX clip '{target.name}' ({target.length:0.##} s), loop on");

                if (target.length > 3f)
                    log.AppendLine($"      ! {target.length:0.#} s is long for a walk cycle - set Start/End " +
                                   "on the FBX Animation tab to just one stride.");
            }

            EditorUtility.SetDirty(controller);
        }

        AssetDatabase.SaveAssets();
        log.AppendLine($"\n  controllers {doneControllers.Count}, states repointed {fixedStates}, " +
                       $"FBX clips set to loop {loopedClips}");
        Debug.Log(log.ToString());
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The FBX this character's prefab instance comes from, if any.</summary>
    static string FindModelPath(GameObject go)
    {
        var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
        var source = root != null ? PrefabUtility.GetCorrespondingObjectFromOriginalSource(root) : null;
        string path = source != null ? AssetDatabase.GetAssetPath(source) : null;

        if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is ModelImporter)
            return path;

        // Fallback: an FBX in the same folder as the controller.
        var anim = go.GetComponent<Animator>();
        if (anim != null && anim.runtimeAnimatorController != null)
        {
            string dir = System.IO.Path.GetDirectoryName(
                AssetDatabase.GetAssetPath(anim.runtimeAnimatorController)).Replace('\\', '/');
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { dir }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (LoadModelClips(p).Count > 0) return p;
            }
        }
        return null;
    }

    static List<AnimationClip> LoadModelClips(string modelPath) =>
        AssetDatabase.LoadAllAssetsAtPath(modelPath)
            .OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__"))
            .ToList();

    /// <summary>Loop Time + Loop Pose on one clip of a model. True if something changed.</summary>
    static bool EnableLoop(string modelPath, string clipName)
    {
        var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null) return false;

        // Until clipAnimations is populated the importer uses the defaults and ignores edits.
        var clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
        if (clips == null || clips.Length == 0) return false;

        bool changed = false;
        foreach (var c in clips)
        {
            if (c.name != clipName && clips.Length > 1) continue;
            if (!c.loopTime || !c.loopPose) { c.loopTime = true; c.loopPose = true; changed = true; }
        }

        if (!changed && importer.clipAnimations.Length > 0) return false;

        importer.clipAnimations = clips;
        importer.SaveAndReimport();
        return true;
    }

    static void SetStandaloneLoop(AnimationClip clip)
    {
        var s = AnimationUtility.GetAnimationClipSettings(clip);
        if (s.loopTime && s.loopBlend) return;
        Undo.RecordObject(clip, "Fix Walk Loop");
        s.loopTime = true;
        s.loopBlend = true;
        AnimationUtility.SetAnimationClipSettings(clip, s);
        EditorUtility.SetDirty(clip);
    }

    static IEnumerable<AnimatorState> AllStates(AnimatorController c)
    {
        foreach (var layer in c.layers)
            foreach (var s in AllStates(layer.stateMachine))
                yield return s;
    }

    static IEnumerable<AnimatorState> AllStates(AnimatorStateMachine sm)
    {
        foreach (var cs in sm.states) yield return cs.state;
        foreach (var sub in sm.stateMachines)
            foreach (var s in AllStates(sub.stateMachine))
                yield return s;
    }
}
