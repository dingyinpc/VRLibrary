using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// VRlibrary PICO 真机打包工具。
/// 菜单:
///   VRLibrary / 打包PICO真机 / ① 配置Android打包设置
///   VRLibrary / 打包PICO真机 / ② 一键打包APK
///   VRLibrary / 打包PICO真机 / ③ 安装APK并启动到真机
/// 说明:本项目已启用 PICO 原生通道(XR Management - PXR_Loader),
///      脚本仅保证标准构建参数正确,不干预 XR 加载器配置。
/// </summary>
public static class VRLibraryBuildTools
{
    const string PackageId = "com.vrlibrary.xr";
    const string OutputDir = "Builds";
    const string OutputName = "VRlibrary.apk";

    [MenuItem("VRLibrary/打包PICO真机/① 配置Android打包设置")]
    public static void ConfigureAndroidMenu() => ConfigureAndroid();

    [MenuItem("VRLibrary/打包PICO真机/② 一键打包APK")]
    public static void BuildApkMenu() => BuildApk();

    [MenuItem("VRLibrary/打包PICO真机/③ 安装APK并启动到真机")]
    public static void InstallAndRunMenu() => InstallAndRun();

    // ------------------------------------------------------------ 配置

    public static void ConfigureAndroid()
    {
        // 1) 切换到 Android 平台
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            var ret = EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            if (!ret)
            {
                UnityEngine.Debug.LogError("[VRLibraryBuild] 切换到 Android 平台失败,请检查 XR Plug-in Management / Android 模块。");
                return;
            }
        }

        // 2) 包名(默认包名含 DefaultCompany 时替换为正式包名)
        string cur = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
        if (string.IsNullOrEmpty(cur) || cur.Contains("DefaultCompany"))
        {
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, PackageId);
            UnityEngine.Debug.Log("[VRLibraryBuild] 应用包名已设置为 " + PackageId);
        }

        // 3) 标准 VR 构建参数
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
            new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });

        // 4) 报表
        UnityEngine.Debug.Log("[VRLibraryBuild] PICO 打包设置已就绪:" +
            "\n  平台=Android  IL2CPP  ARM64  最低API=29" +
            "\n  图形API=Vulkan(优先)+OpenGLES3  包名=" +
            PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android) +
            "\n  XR加载器=按项目现有配置(已启用 PXR_Loader / 请到 Project Settings → XR Plug-in Management → Android 确认勾选 PICO)");
        AssetDatabase.SaveAssets();
    }

    // ------------------------------------------------------------ 打包

    public static void BuildApk()
    {
        ConfigureAndroid();

        if (!Directory.Exists(OutputDir))
            Directory.CreateDirectory(OutputDir);
        string outPath = Path.Combine(OutputDir, OutputName);

        var scenes = new string[EditorBuildSettings.scenes.Length];
        for (int i = 0; i < scenes.Length; i++)
            scenes[i] = EditorBuildSettings.scenes[i].path;

        UnityEngine.Debug.Log("[VRLibraryBuild] 开始打包(首次 IL2CPP 构建需 5~15 分钟)…");
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;
        if (summary.result == BuildResult.Succeeded)
        {
            UnityEngine.Debug.Log($"[VRLibraryBuild] 打包成功 ✔\n  路径:{outPath}\n  大小:{summary.totalSize / 1048576f:F1} MB" +
                "\n  下一步:连接真机 → 菜单 VRLibrary → 打包PICO真机 → ③ 安装APK并启动到真机,或手动 adb install -r ");
        }
        else
        {
            UnityEngine.Debug.LogError("[VRLibraryBuild] 打包失败: " + summary.result +
                "\n完整日志见 Console;常见原因:包名冲突 / SDK 路径未配置 / 场景未加入 Build Settings。");
        }
    }

    // ------------------------------------------------------------ 安装

    public static void InstallAndRun()
    {
        string apk = Path.Combine(OutputDir, OutputName);
        if (!File.Exists(apk))
        {
            UnityEngine.Debug.LogError("[VRLibraryBuild] 未找到 " + apk + ",请先执行 ② 一键打包APK。");
            return;
        }

        string adb = FindAdb();
        if (adb == null)
        {
            UnityEngine.Debug.LogError("[VRLibraryBuild] 找不到 adb,请确认 Unity Android 模块的 SDK 已安装。");
            return;
        }

        // adb devices
        string devOut = Run(adb, "devices");
        UnityEngine.Debug.Log("[VRLibraryBuild] adb devices:\n" + devOut);
        bool hasDevice = devOut != null &&
            devOut.Split('\n').Any(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("List") && !l.Contains("daemon") && l.Contains("device"));

        if (!hasDevice)
        {
            UnityEngine.Debug.LogError("[VRLibraryBuild] 未检测到真机!\n" +
                "请检查:① 头显开启开发者模式 ② 设置→开发者→打开USB调试 ③ 数据线连接并允许授权 ④ 驱动/换线(推荐原装CC线)。");
            return;
        }

        // 安装(-r 覆盖安装)
        UnityEngine.Debug.Log("[VRLibraryBuild] 正在安装到真机…");
        string pkg = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
        string instOut = Run(adb, "install -r \"" + apk + "\"");
        UnityEngine.Debug.Log("[VRLibraryBuild] adb install:\n" + instOut);
        if (instOut != null && instOut.Contains("Success"))
        {
            UnityEngine.Debug.Log("[VRLibraryBuild] 安装成功,正在启动应用…");
            string launch = Run(adb, "shell monkey -p " + pkg + " -c android.intent.category.LAUNCHER 1");
            UnityEngine.Debug.Log("[VRLibraryBuild] 启动命令已发送(输出):\n" + launch +
                "\n戴上头显即可体验。日志查看:adb logcat -s Unity");
        }
        else
        {
            UnityEngine.Debug.LogError("[VRLibraryBuild] 安装失败(批注:确认头显已解锁并允许USB调试,或先卸载旧版本再装)。");
        }
    }

    // ------------------------------------------------------------ 工具

    static string FindAdb()
    {
        // 优先取 Unity 自带 Android SDK
        string module = Path.Combine(Path.GetDirectoryName(EditorApplication.applicationPath),
            "Data", "PlaybackEngines", "AndroidPlayer", "SDK", "platform-tools", "adb.exe");
        if (File.Exists(module)) return module;

        // 其次取 EditorPrefs AndroidSdkRoot
        if (EditorPrefs.HasKey("AndroidSdkRoot"))
        {
            string custom = Path.Combine(EditorPrefs.GetString("AndroidSdkRoot"), "platform-tools", "adb.exe");
            if (File.Exists(custom)) return custom;
        }
        return null;
    }

    static string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var proc = Process.Start(psi))
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n[stderr] " + stderr);
            }
        }
        catch (System.Exception e)
        {
            return "[错误] " + e.Message;
        }
    }
}