#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

/// <summary>
/// Copies the Console to Logs/Console.txt and compile results to Logs/Compile.txt, so they can be
/// read without looking at the Editor (warnings are skipped, errors and logs are kept).
/// </summary>
[InitializeOnLoad]
static class ConsoleToFile
{
    const string ConsolePath = "Logs/Console.txt";
    const string CompilePath = "Logs/Compile.txt";
    static readonly object Lock = new object();

    static ConsoleToFile()
    {
        Application.logMessageReceivedThreaded -= OnLog;
        Application.logMessageReceivedThreaded += OnLog;
        CompilationPipeline.compilationStarted -= OnStart;
        CompilationPipeline.compilationStarted += OnStart;
        CompilationPipeline.assemblyCompilationFinished -= OnAssembly;
        CompilationPipeline.assemblyCompilationFinished += OnAssembly;
        EditorApplication.playModeStateChanged -= OnPlay;
        EditorApplication.playModeStateChanged += OnPlay;
        Append(CompilePath, $"[{DateTime.Now:HH:mm:ss}] scripts loaded OK");
    }

    static void OnPlay(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.EnteredPlayMode)
            lock (Lock) File.WriteAllText(ConsolePath, $"[{DateTime.Now:HH:mm:ss}] ENTER PLAY\n");
        if (s == PlayModeStateChange.ExitingPlayMode) Append(ConsolePath, $"[{DateTime.Now:HH:mm:ss}] EXIT PLAY");
    }

    static void OnLog(string msg, string stack, LogType type)
    {
        if (type == LogType.Warning) return;
        string line = $"[{DateTime.Now:HH:mm:ss}] {type}: {msg}";
        if (type == LogType.Exception || type == LogType.Error)
        {
            var lines = (stack ?? "").Split('\n');
            line += "\n    " + string.Join("\n    ", lines, 0, Math.Min(4, lines.Length));
        }
        Append(ConsolePath, line);
    }

    static void OnStart(object _) => Append(CompilePath, $"[{DateTime.Now:HH:mm:ss}] compiling...");

    static void OnAssembly(string asm, CompilerMessage[] msgs)
    {
        foreach (var m in msgs)
            if (m.type == CompilerMessageType.Error)
                Append(CompilePath, $"[{DateTime.Now:HH:mm:ss}] ERROR {m.file}:{m.line} {m.message}");
    }

    static void Append(string path, string line)
    {
        try { lock (Lock) File.AppendAllText(path, line + "\n"); } catch { }
    }
}
#endif
