#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 冒烟测试入口:写请求文件 → 进 Play → 由 VRLibrarySmoke 在场景加载后接手跑链路。
/// 桥没有"切 Play"的动作、也没有 Console 回读,所以只能这样进、用文件出。
/// 防"编辑器失焦停游戏循环导致假通过":由 VRLibrarySmoke 末尾的帧数推进检查负责。
/// </summary>
public static class VRLibrarySmokeMenu
{
    const string kRequest = "Logs/smoke-request.txt";
    const string kReport = "Logs/runtime-smoke.txt";

    [MenuItem("VRLibrary/冒烟测试(进Play自动跑)")]
    public static void Run()
    {
        Start("");
    }

    /// <summary>
    /// 预览模式:进 Play 只播露台示例片并停一会儿,给远程截图留出窗口,到点自己退出。
    /// 冒烟测试跑完就 isPlaying=false,人没法在外面卡进那两秒的播放窗口,所以单独开一条路。
    /// </summary>
    [MenuItem("VRLibrary/露台示例片预览(进Play,播完自动退出)")]
    public static void Preview()
    {
        Start("preview=45");
    }

    /// <summary>编辑器编译后进程 CWD 会漂到 Library/Bee/... 底下,相对路径的报告会被写进没人看的目录。</summary>
    static string Abs(string rel)
    {
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath),
            rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    static void Start(string request)
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[Smoke] 已在 Play 中,先退出再跑");
            return;
        }
        string req = Abs(kRequest), rep = Abs(kReport);
        Directory.CreateDirectory(Abs("Logs"));
        File.WriteAllText(req, request + " " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        // 必须先删旧报告:留着它,一旦 Play 没真跑起来,读到的就是上一轮的绿(2026-09-22 中过一次)
        if (File.Exists(rep)) File.Delete(rep);

        EditorApplication.isPlaying = true;
        Debug.Log("[Smoke] 已进入 Play(" + (request == "" ? "冒烟" : request) + ")→ " + rep);
    }
}
#endif
