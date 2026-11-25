using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 语音探针入口(和冒烟测试同一套"文件进、文件出"的路子):
/// 桥没有"切 Play"的动作,所以菜单只写请求文件 + 进 Play,由 LibraryVoiceProbe 接手。
/// 菜单 VRLibrary/语音·固定台词探针(进Play,自动跑登记+预检+自测+性能)。
/// </summary>
public static class VRLibraryVoice
{
    const string kRequest = "Logs/voice-request.txt";
    const string kReport = "Logs/voice-test.txt";

    [MenuItem("VRLibrary/语音·固定台词探针(进Play,自动跑登记+预检+自测+性能)")]
    public static void Start()
    {
        if (EditorApplication.isPlaying) { Debug.LogError("[Voice] 已在 Play 中,先退出再跑"); return; }
        Directory.CreateDirectory(Abs("Logs"));
        File.WriteAllText(Abs(kRequest), "voice " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (File.Exists(Abs(kReport))) File.Delete(Abs(kReport));   // 先删旧报告,免得读到上一轮的绿
        EditorApplication.isPlaying = true;
        Debug.Log("[Voice] 已进入 Play → 跑完写 " + Abs(kReport) + "(会自己退出 Play)");
    }

    [MenuItem("VRLibrary/语音·打开报告(文本)")]
    public static void OpenReport()
    {
        string p = Abs(kReport);
        if (!File.Exists(p)) { Debug.LogWarning("[Voice] 还没有报告:" + p); return; }
        EditorUtility.OpenWithDefaultApp(p);
    }

    [MenuItem("VRLibrary/语音·现状说明(只打印)")]
    public static void Explain()
    {
        Debug.Log(
            "[Voice] 固定台词识别(模板匹配 MFCC+DTW)\n" +
            "  · 台词 5 句:打开菜单 / 关闭菜单 / 去一楼 / 去二楼 / 去影院\n" +
            "  · 触发方式(待接):按住手柄键说话 —— 避免与场景音乐争音频会话,占空比 ~1%\n" +
            "  · 为什么不用常开:MFCC+DTW 的算力本身就白送(约 0.1% 单核),\n" +
            "    但常开=麦克风一直开着,固定功耗/热预算/音频路由才是真代价 ⇒ 先按需\n" +
            "  · 真机前提:Android 清单里必须有 android.permission.RECORD_AUDIO\n" +
            "  · 报告:Logs/voice-test.txt(含混淆矩阵、区分度预检、MFCC/匹配耗时与常开推算)");
    }

    static string Abs(string rel)
    {
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), rel.Replace('/', Path.DirectorySeparatorChar));
    }
}