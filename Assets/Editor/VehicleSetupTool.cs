using UnityEditor;
using UnityEngine;

public static class VehicleSetupTool
{
    [MenuItem("Tools/Emergency VR/Setup All Vehicles (Colliders + Movement)")]
    public static void SetupVehicles()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var rootObjects = scene.GetRootGameObjects();
        int processedCount = 0;

        foreach (var root in rootObjects)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                if (t.name.StartsWith("VEH_"))
                {
                    Undo.RegisterCompleteObjectUndo(t.gameObject, "Setup Vehicle");

                    // 1. Add BoxCollider to Empty Parent if missing
                    var boxCollider = t.GetComponent<BoxCollider>();
                    if (boxCollider == null)
                    {
                        boxCollider = Undo.AddComponent<BoxCollider>(t.gameObject);
                        var renderers = t.GetComponentsInChildren<Renderer>();
                        if (renderers.Length > 0)
                        {
                            Bounds bounds = renderers[0].bounds;
                            for (int i = 1; i < renderers.Length; i++)
                            {
                                bounds.Encapsulate(renderers[i].bounds);
                            }
                            boxCollider.center = t.InverseTransformPoint(bounds.center);
                            Vector3 worldSize = bounds.size;
                            Vector3 localSize = t.InverseTransformVector(worldSize);
                            boxCollider.size = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));
                        }
                    }

                    // 2. Add VehicleController script if missing
                    var controller = t.GetComponent<VehicleController>();
                    if (controller == null)
                    {
                        controller = Undo.AddComponent<VehicleController>(t.gameObject);
                    }
                    controller.navigationMode = VehicleController.NavigationType.AutoDrive;

                    EditorUtility.SetDirty(t.gameObject);
                    processedCount++;
                }
            }
        }

        Debug.Log($"[VehicleSetupTool] Successfully set up {processedCount} vehicle parents with Colliders and VehicleController (AutoDrive Mode)!");
    }
}
