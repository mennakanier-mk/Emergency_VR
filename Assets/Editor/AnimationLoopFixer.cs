using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
///   Tools > Emergency VR > Fix Animation Loops
///   Tools > Emergency VR > Report Animation Loops   (read-only)
///
/// Turns on Loop Time for every walk cycle in the scene.
///
/// A clip imported with Loop Time off plays once and then holds its last frame until the state
/// machine restarts it. On a walk cycle that reads as a pause between strides - the legs finish,
/// freeze, and start again - which is the gap you are seeing. The clip itself is fine; it is the
/// import setting that decides whether the end runs straight back into the beginning.
///
/// Loop Pose is turned on with it. That one matches the first and last frames to each other, so
/// the stride does not jump at the seam even when the animator rolled the clip slightly long or
/// short.
///
/// Clips inside an FBX need their settings copied out of defaultClipAnimations into
/// clipAnimations before they can be edited at all - until you do, the importer reports the
/// defaults and silently ignores what you write.
/// </summary>
public static class AnimationLoopFixer
{
    [MenuItem("Tools/Emergency VR/Report Animation Loops")]
    public static void Report() { Run(false); }

    [MenuItem("Tools/Emergency VR/Fix Animation Loops")]
    public static void Fix() { Run(true); }

    static void Run(bool write)
    {
        var scene = SceneManager.GetActiveScene();

        // Every clip reachable from an Animator in the scene.
        var clips = new HashSet<AnimationClip>();

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || animator.runtimeAnimatorController == null) continue;

                foreach (var clip in animator.runtimeAnimatorController.animationClips)
                    if (clip != null) clips.Add(clip);
            }
        }

        if (clips.Count == 0)
        {
            Debug.LogWarning("[AnimationLoops] No Animators with clips in this scene.");
            return;
        }

        var log = new StringBuilder($"[AnimationLoops] {clips.Count} clips in use\n\n");

        int looping = 0, fixedUp = 0, notEditable = 0;

        // Group by source file: an FBX has to be re-imported once, not once per clip.
        var byPath = clips.GroupBy(c => AssetDatabase.GetAssetPath(c));

        foreach (var group in byPath)
        {
            string path = group.Key;
            if (string.IsNullOrEmpty(path)) continue;

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;

            if (importer == null)
            {
                // A standalone .anim asset - the loop flag lives on the clip itself.
                foreach (var clip in group)
                {
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);

                    if (settings.loopTime)
                    {
                        looping++;
                        log.AppendLine($"  {clip.name,-34} already loops");
                        continue;
                    }

                    log.AppendLine($"  {clip.name,-34} loop OFF -> ON");

                    if (!write) continue;

                    settings.loopTime = true;
                    settings.loopBlend = true;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                    EditorUtility.SetDirty(clip);
                    fixedUp++;
                }

                continue;
            }

            // Inside a model file. The settings have to be lifted out of the defaults before
            // any edit sticks.
            var defs = importer.clipAnimations;
            if (defs == null || defs.Length == 0) defs = importer.defaultClipAnimations;

            if (defs == null || defs.Length == 0)
            {
                notEditable += group.Count();
                foreach (var clip in group)
                    log.AppendLine($"  {clip.name,-34} no editable clip data in {System.IO.Path.GetFileName(path)}");
                continue;
            }

            bool changed = false;

            foreach (var def in defs)
            {
                if (def.loopTime)
                {
                    looping++;
                    log.AppendLine($"  {def.name,-34} already loops");
                    continue;
                }

                log.AppendLine($"  {def.name,-34} loop OFF -> ON   ({System.IO.Path.GetFileName(path)})");

                def.loopTime = true;
                def.loopPose = true;
                changed = true;
                fixedUp++;
            }

            if (!write || !changed) continue;

            importer.clipAnimations = defs;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        log.AppendLine();
        log.AppendLine($"  already looping {looping}");
        log.AppendLine($"  switched on     {fixedUp}");
        if (notEditable > 0) log.AppendLine($"  not editable    {notEditable}");

        if (!write)
        {
            log.AppendLine();
            log.AppendLine("  Report only - nothing changed. Run Fix Animation Loops to apply.");
        }

        Debug.Log(log.ToString());
    }

    /// <summary>
    /// Clears Has Exit Time on the transitions out of the walk state.
    ///
    /// The other thing that puts a gap in a cycle: a transition that waits for the clip to
    /// finish before it will move. On a looping walk that means up to a whole stride of delay
    /// before the animator reacts, which looks exactly like a stutter.
    /// </summary>
    [MenuItem("Tools/Emergency VR/Fix Animation Transitions")]
    public static void FixTransitions()
    {
        var scene = SceneManager.GetActiveScene();
        var controllers = new HashSet<UnityEditor.Animations.AnimatorController>();

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var ac = animator != null
                    ? animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController
                    : null;

                if (ac != null) controllers.Add(ac);
            }
        }

        if (controllers.Count == 0)
        {
            Debug.LogWarning("[AnimationLoops] No editable Animator Controllers in this scene. " +
                             "An Animator Override Controller cannot be edited this way.");
            return;
        }

        var log = new StringBuilder("[AnimationLoops] transitions\n");
        int cleared = 0;

        foreach (var ac in controllers)
        {
            foreach (var layer in ac.layers)
            {
                foreach (var state in layer.stateMachine.states)
                {
                    foreach (var t in state.state.transitions)
                    {
                        if (!t.hasExitTime) continue;

                        t.hasExitTime = false;
                        t.duration = Mathf.Min(t.duration, 0.15f);
                        cleared++;

                        log.AppendLine($"  {ac.name}: {state.state.name} -> " +
                                       $"{(t.destinationState != null ? t.destinationState.name : "exit")}" +
                                       "   exit time OFF");
                    }
                }
            }

            EditorUtility.SetDirty(ac);
        }

        AssetDatabase.SaveAssets();

        log.AppendLine();
        log.AppendLine($"  {cleared} transitions no longer wait for the clip to finish.");

        Debug.Log(log.ToString());
    }
}
