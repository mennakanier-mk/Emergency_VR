#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Android builds fail on a Windows set to Arabic: Unity runs the Android sdkmanager (Java) and
/// Java formats its progress text with the Arabic locale, which throws
/// "UnknownFormatConversionException: Conversion = '-'" -> "Failed to update Android SDK package
/// list" -> build failed.
///
/// This sets JAVA_TOOL_OPTIONS for the Unity Editor process only, every time scripts load, so
/// every Java tool Unity starts (sdkmanager, Gradle, the JDK) runs in English. Windows itself and
/// other programs are not touched.
/// </summary>
[InitializeOnLoad]
static class JavaLocaleFix
{
    // sdkmanager.bat passes SDKMANAGER_OPTS and JAVA_OPTS to java; Gradle reads GRADLE_OPTS and
    // JAVA_OPTS. Setting these (rather than JAVA_TOOL_OPTIONS) avoids the "Picked up ..." line on
    // stderr that Unity can mistake for an error.
    static readonly string[] Vars = { "SDKMANAGER_OPTS", "JAVA_OPTS", "GRADLE_OPTS" };
    const string Opts = "-Duser.language=en -Duser.country=US -Duser.region=US -Dfile.encoding=UTF-8";

    static JavaLocaleFix() => Apply(false);

    [MenuItem("Tools/Emergency VR/Fix Android Build (Java Locale)")]
    static void ApplyFromMenu() => Apply(true);

    static void Apply(bool log)
    {
        foreach (var v in Vars)
        {
            string cur = Environment.GetEnvironmentVariable(v) ?? "";
            if (cur.Contains("-Duser.language=")) continue;
            Environment.SetEnvironmentVariable(v, string.IsNullOrEmpty(cur) ? Opts : cur + " " + Opts,
                                               EnvironmentVariableTarget.Process);
            log = true;
        }

        // Undo the earlier version of this fix, which used JAVA_TOOL_OPTIONS.
        string jto = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS");
        if (jto != null && jto.Contains(Opts.Substring(0, 20)) )
            Environment.SetEnvironmentVariable("JAVA_TOOL_OPTIONS", null, EnvironmentVariableTarget.Process);

        if (log)
            Debug.Log("[JavaLocaleFix] SDKMANAGER_OPTS / JAVA_OPTS / GRADLE_OPTS = " + Opts +
                      " (Android SDK tools and Gradle run in English, so an Arabic locale no longer breaks the build).");
    }
}
#endif
