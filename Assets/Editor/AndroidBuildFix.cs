using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Repairs the Android Player settings for the Google Cardboard build.
///
/// Tools > Emergency VR > Fix Android Build Settings
///
/// The build was failing in CheckAarMetadataWorkAction because the generated
/// Library/Bee/Android/Prj/IL2CPP/Gradle/unityLibrary/build.gradle had:
///
///     compileSdk 34
///     minSdk 34
///     implementation 'androidx.games:games-activity:4.4.0'
///     implementation 'com.android.billingclient:billing:9.0.0'
///     implementation 'com.unity3d.ads:unity-ads:[4.16.3,4.17['
///
/// All three of those AARs declare minCompileSdk 35, so Gradle refuses to link them
/// against compileSdk 34. In Unity, compileSdk is driven by Target API Level.
///
/// Fixes applied:
///   Target API Level     34  -> Auto (highest installed), which raises compileSdk past 35.
///   Minimum API Level    34  -> 26, so the APK installs on ordinary phones and not only
///                               on Android 14+. Cardboard itself needs 24.
///   Application Entry    -> Activity. The Cardboard plugin predates GameActivity and expects
///                               the classic UnityPlayerActivity; under GameActivity the app
///                               builds fine but dies on a black screen a few seconds in.
///                               androidx.appcompat, which Unity only auto-injects for
///                               GameActivity, is supplied instead by
///                               Assets/Editor/CardboardAndroidDependencies.xml through the
///                               External Dependency Manager. Do not "fix" the AppCompat theme
///                               error by flipping this back to GameActivity.
///   Graphics API         -> OpenGLES3 only. The Cardboard plugin has no Vulkan path.
///   Architectures        -> ARM64 only.
///   Orientation          -> Landscape Left, which is the only orientation Cardboard uses.
/// </summary>
public static class AndroidBuildFix
{
    [MenuItem("Tools/Emergency VR/Fix Android Build Settings")]
    public static void Apply()
    {
        var log = new StringBuilder();
        log.AppendLine("[AndroidBuildFix] Android Player settings:");

        // --- SDK levels: this pair is what actually unblocks CheckAarMetadata ---
        log.AppendLine(Change("Target API Level",
            PlayerSettings.Android.targetSdkVersion.ToString(),
            AndroidSdkVersions.AndroidApiLevelAuto.ToString()));
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

        log.AppendLine(Change("Minimum API Level",
            PlayerSettings.Android.minSdkVersion.ToString(),
            AndroidSdkVersions.AndroidApiLevel26.ToString()));
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;

        // --- Application entry point ---
        // Activity, NOT GameActivity: Cardboard's native renderer hooks UnityPlayerActivity.
        // appcompat comes from Assets/Editor/CardboardAndroidDependencies.xml instead.
        log.AppendLine(Change("Application Entry",
            PlayerSettings.Android.applicationEntry.ToString(),
            AndroidApplicationEntry.Activity.ToString()));
        PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;

        // --- Architecture ---
        log.AppendLine(Change("Target Architectures",
            PlayerSettings.Android.targetArchitectures.ToString(),
            AndroidArchitecture.ARM64.ToString()));
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        // --- Scripting backend ---
        var android = NamedBuildTarget.Android;
        log.AppendLine(Change("Scripting Backend",
            PlayerSettings.GetScriptingBackend(android).ToString(),
            ScriptingImplementation.IL2CPP.ToString()));
        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);

        // --- Graphics API: Cardboard has no Vulkan renderer ---
        var before = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
        log.AppendLine(Change("Graphics APIs",
            before.Length == 0 ? "(auto)" : string.Join(", ", before),
            "OpenGLES3"));
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
            new[] { GraphicsDeviceType.OpenGLES3 });

        // --- Orientation: Cardboard is landscape only ---
        log.AppendLine(Change("Default Orientation",
            PlayerSettings.defaultInterfaceOrientation.ToString(),
            UIOrientation.LandscapeLeft.ToString()));
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;

        AssetDatabase.SaveAssets();
        EditorApplication.ExecuteMenuItem("File/Save Project");

        Debug.Log(log.ToString());
        Report();
    }

    /// <summary>Read-only check. Run it after a build to confirm nothing drifted back.</summary>
    [MenuItem("Tools/Emergency VR/Report Android Build Settings")]
    public static void Report()
    {
        var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);

        var sb = new StringBuilder();
        sb.AppendLine("[AndroidBuildFix] Current Android settings");
        sb.AppendLine($"  Target API Level     {PlayerSettings.Android.targetSdkVersion}   (drives compileSdk; must clear 35)");
        sb.AppendLine($"  Minimum API Level    {PlayerSettings.Android.minSdkVersion}");
        sb.AppendLine($"  Application Entry    {PlayerSettings.Android.applicationEntry}");
        sb.AppendLine($"  Architectures        {PlayerSettings.Android.targetArchitectures}");
        sb.AppendLine($"  Scripting Backend    {PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android)}");
        sb.AppendLine($"  Graphics APIs        {(apis.Length == 0 ? "(auto)" : string.Join(", ", apis))}");
        sb.AppendLine($"  Orientation          {PlayerSettings.defaultInterfaceOrientation}");
        sb.AppendLine($"  Active build target  {EditorUserBuildSettings.activeBuildTarget}");

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            sb.AppendLine("  WARNING: active build target is not Android. Switch platform first.");

        foreach (var api in apis)
        {
            if (api == GraphicsDeviceType.Vulkan)
                sb.AppendLine("  WARNING: Vulkan is still listed. The Cardboard plugin cannot render on it.");
        }

        if (PlayerSettings.Android.applicationEntry != AndroidApplicationEntry.Activity)
            sb.AppendLine("  WARNING: Application Entry is not Activity. Cardboard expects the " +
                          "classic UnityPlayerActivity and crashes on a black screen otherwise.");

        if (!System.IO.File.Exists("Assets/Editor/CardboardAndroidDependencies.xml"))
            sb.AppendLine("  WARNING: CardboardAndroidDependencies.xml is missing. Without it " +
                          "androidx.appcompat is absent and Gradle fails on the AppCompat theme.");

        if (PlayerSettings.Android.targetSdkVersion != AndroidSdkVersions.AndroidApiLevelAuto &&
            (int)PlayerSettings.Android.targetSdkVersion < 35)
            sb.AppendLine("  WARNING: Target API Level is below 35. CheckAarMetadata will reject " +
                          "games-activity and the other AARs.");

        Debug.Log(sb.ToString());
    }

    static string Change(string label, string from, string to)
    {
        return from == to
            ? $"  {label,-22} {to}  (already correct)"
            : $"  {label,-22} {from}  ->  {to}";
    }
}
