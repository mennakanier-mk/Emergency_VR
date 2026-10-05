using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ReplaceVRHandsTool
{
    [MenuItem("Tools/Emergency VR/Replace VR Hands with Custom hands Model")]
    public static void ReplaceHands()
    {
        var scene = EditorSceneManager.GetActiveScene();
        var handModelAsset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/hands/Untitled.fbx");

        if (handModelAsset == null)
        {
            Debug.LogError("[ReplaceVRHandsTool] Could not find Assets/hands/Untitled.fbx!");
            return;
        }

        var transforms = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray();
        
        // Find Left & Right controller/hand objects under XR Origin
        var leftHand = transforms.FirstOrDefault(t => t.name.StartsWith("LeftHand") || t.name.Contains("Left Controller") || t.name.Contains("LeftHand Direct"));
        var rightHand = transforms.FirstOrDefault(t => t.name.StartsWith("RightHand") || t.name.Contains("Right Controller") || t.name.Contains("RightHand Direct"));

        if (leftHand == null)
        {
            leftHand = transforms.FirstOrDefault(t => t.name.ToLower().Contains("left"));
        }
        if (rightHand == null)
        {
            rightHand = transforms.FirstOrDefault(t => t.name.ToLower().Contains("right"));
        }

        if (leftHand == null || rightHand == null)
        {
            Debug.LogError("[ReplaceVRHandsTool] Could not locate Left Hand or Right Hand transform under XR Origin!");
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Replace VR Hands Model");

        // Clear previous custom hand instances if any exist
        foreach (Transform child in leftHand.GetComponentsInChildren<Transform>())
        {
            if (child != leftHand && child.name.StartsWith("CustomVRHand"))
            {
                Undo.DestroyObjectImmediate(child.gameObject);
            }
        }
        foreach (Transform child in rightHand.GetComponentsInChildren<Transform>())
        {
            if (child != rightHand && child.name.StartsWith("CustomVRHand"))
            {
                Undo.DestroyObjectImmediate(child.gameObject);
            }
        }

        // Instantiate Left Hand model
        GameObject leftInstance = (GameObject)PrefabUtility.InstantiatePrefab(handModelAsset, leftHand);
        leftInstance.name = "CustomVRHand_Left";
        Undo.RegisterCreatedObjectUndo(leftInstance, "Create Left Hand Instance");
        leftInstance.transform.localPosition = Vector3.zero;
        leftInstance.transform.localRotation = Quaternion.identity;

        // Instantiate Right Hand model
        GameObject rightInstance = (GameObject)PrefabUtility.InstantiatePrefab(handModelAsset, rightHand);
        rightInstance.name = "CustomVRHand_Right";
        Undo.RegisterCreatedObjectUndo(rightInstance, "Create Right Hand Instance");
        rightInstance.transform.localPosition = Vector3.zero;
        rightInstance.transform.localRotation = Quaternion.identity;

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[ReplaceVRHandsTool] Successfully attached Assets/hands/Untitled.fbx to Left Hand ({leftHand.name}) and Right Hand ({rightHand.name})!");
    }
}
