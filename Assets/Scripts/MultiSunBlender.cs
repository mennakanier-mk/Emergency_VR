using UnityEngine;

/// <summary>
/// Simple script to blend multiple Directional Lights (Sun Sources) into RenderSettings.sun.
/// Fixes lighting shifts by disabling continuous environment updates during Play mode by default.
/// </summary>
public class MultiSunBlender : MonoBehaviour
{
    public enum BlendMode
    {
        WeightedCombine,    // Combines color, intensity, and direction of all active directional lights
        LerpBetweenSuns     // Smoothly blends between Sun A and Sun B using a slider (0 to 1)
    }

    [Header("Blending Mode")]
    [Tooltip("Choose whether to combine all directional lights or lerp between Sun A and Sun B.")]
    public BlendMode blendMode = BlendMode.WeightedCombine;

    [Header("Sun Sources (Directional Lights)")]
    [Tooltip("Drag your Directional Lights here (Sun 1, Sun 2, Fill Light, etc.).")]
    public Light[] directionalLights;

    [Header("Lerp Slider (For Lerp Mode)")]
    [Range(0f, 1f)]
    [Tooltip("0 = 100% First Light, 1 = 100% Second Light.")]
    public float blendFactor = 0.5f;

    [Header("Master Sun Target (RenderSettings.sun)")]
    [Tooltip("The main Directional Light that will receive the blended result and serve as RenderSettings.sun.")]
    public Light masterSunLight;

    [Header("Play Mode Lighting Locks")]
    [Tooltip("Keep false to lock lighting in Play mode and prevent unwanted brightness/color shifts.")]
    public bool enableRealtimeUpdatesInPlayMode = false;

    void Start()
    {
        if (masterSunLight == null)
        {
            masterSunLight = GetComponent<Light>();
        }

        if (masterSunLight != null)
        {
            RenderSettings.sun = masterSunLight;
        }

        // Apply blend once on start
        ApplySunBlending();
    }

    void Update()
    {
        // Only run continuous updates if explicitly enabled
        if (!enableRealtimeUpdatesInPlayMode) return;

        ApplySunBlending();
    }

    private void ApplySunBlending()
    {
        if (directionalLights == null || directionalLights.Length == 0) return;

        switch (blendMode)
        {
            case BlendMode.LerpBetweenSuns:
                if (directionalLights.Length >= 2)
                {
                    BlendTwoLights(directionalLights[0], directionalLights[1], blendFactor);
                }
                break;

            case BlendMode.WeightedCombine:
                CombineAllLights();
                break;
        }

        if (masterSunLight != null && RenderSettings.sun != masterSunLight)
        {
            RenderSettings.sun = masterSunLight;
        }
    }

    private void BlendTwoLights(Light lightA, Light lightB, float t)
    {
        if (lightA == null || lightB == null || masterSunLight == null) return;

        // 1. Lerp Color & Intensity
        masterSunLight.color = Color.Lerp(lightA.color, lightB.color, t);
        masterSunLight.intensity = Mathf.Lerp(lightA.intensity, lightB.intensity, t);

        // 2. Lerp Rotation / Direction
        masterSunLight.transform.rotation = Quaternion.Slerp(lightA.transform.rotation, lightB.transform.rotation, t);
    }

    private void CombineAllLights()
    {
        if (masterSunLight == null) return;

        Color blendedColor = Color.black;
        float totalIntensity = 0f;
        Vector3 blendedForward = Vector3.zero;

        int activeCount = 0;
        foreach (var light in directionalLights)
        {
            if (light == null || !light.enabled) continue;

            float weight = light.intensity;
            blendedColor += light.color * weight;
            totalIntensity += light.intensity;
            blendedForward += light.transform.forward * weight;
            activeCount++;
        }

        if (activeCount == 0 || totalIntensity <= 0.0001f) return;

        // Calculate average color and intensity
        blendedColor /= totalIntensity;
        float averageIntensity = totalIntensity / activeCount;

        // Apply blended parameters to Master Sun Light
        masterSunLight.color = blendedColor;
        masterSunLight.intensity = averageIntensity;

        if (blendedForward.sqrMagnitude > 0.001f)
        {
            masterSunLight.transform.rotation = Quaternion.LookRotation(blendedForward.normalized);
        }
    }
}
