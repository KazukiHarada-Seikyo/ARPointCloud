using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Android の APK を1コマンドで作る。
///
/// Unity を開かずにビルドしたいときに使う。設定は Build Settings /
/// Player Settings のものをそのまま使い、**ここでは何も書き換えない**。
/// (署名やパッケージ名を script が勝手に変えると、あとで原因を追いにくい)
///
///   Unity.exe -batchmode -quit -projectPath . ^
///             -executeMethod BuildAndroid.Build -logFile build.log
///
/// 出力先は Builds/arpointcloud_vfx.apk。
/// -buildOut &lt;path&gt; を渡すと変えられる。
/// エディタからは メニュー &gt; ARPointCloud &gt; Android ビルド。
/// </summary>
public static class BuildAndroid
{
    private const string DefaultOut = "Builds/arpointcloud_vfx.apk";

    [MenuItem("ARPointCloud/Android ビルド")]
    public static void BuildFromMenu()
    {
        Run(DefaultOut);
    }

    /// <summary>バッチから呼ばれる入口</summary>
    public static void Build()
    {
        string outPath = ArgAfter("-buildOut") ?? DefaultOut;
        BuildReport report = Run(outPath);

        // バッチでは -quit が付いていても、失敗を終了コードに乗せたい
        if (report == null || report.summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }

    private static BuildReport Run(string outPath)
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("Build Settings にシーンが1つも登録されていません");
            return null;
        }

        string dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Debug.Log($"[BuildAndroid] {scenes.Length} シーン → {outPath}");
        foreach (string s in scenes) Debug.Log($"  {s}");

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary s2 = report.summary;

        Debug.Log($"[BuildAndroid] {s2.result} / {s2.totalSize / (1024 * 1024)} MB "
                  + $"/ {s2.totalTime}");

        if (s2.result != BuildResult.Succeeded)
        {
            foreach (BuildStep step in report.steps)
            {
                foreach (BuildStepMessage m in step.messages)
                {
                    if (m.type == LogType.Error || m.type == LogType.Exception)
                        Debug.LogError($"[BuildAndroid] {step.name}: {m.content}");
                }
            }
        }

        return report;
    }

    private static string ArgAfter(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
