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
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[Smoke] 已在 Play 中,先退出再跑");
            return;
        }
        Directory.CreateDirectory("Logs");
        File.WriteAllText(kRequest, System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (File.Exists(kReport)) File.Delete(kReport);

        EditorApplication.isPlaying = true;
        Debug.Log("[Smoke] 已进入 Play,冒烟链路将在场景加载后自动执行 → " + kReport);
    }
}
#endif
