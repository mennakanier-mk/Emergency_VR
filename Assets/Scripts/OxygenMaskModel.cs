using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Entry point used by AmbulanceResponse: makes sure the FA_mask object carries an OxygenMask
/// component (which builds and fits the mask, with settings in the Inspector), and keeps the
/// mask axes that ParamedicRig needs to put it on the boy's face.
/// </summary>
public static class OxygenMaskModel
{
    /// <summary>Per mask object: which local axis points out of the face, which towards the nose.</summary>
    public static readonly Dictionary<Transform, (Vector3 outLocal, Vector3 noseLocal)> Axes =
        new Dictionary<Transform, (Vector3, Vector3)>();

    public static OxygenMask Apply(Transform maskObj)
    {
        if (maskObj == null || maskObj.GetComponent<MeshFilter>() == null) return null;
        var m = maskObj.GetComponent<OxygenMask>();
        if (m == null) m = maskObj.gameObject.AddComponent<OxygenMask>();
        return m;
    }
}
