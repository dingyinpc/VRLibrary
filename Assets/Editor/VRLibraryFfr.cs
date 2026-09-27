using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 性能④:固定视场渲染(FFR)的**场景侧**落地。菜单 VRLibrary/性能·④ xxx。
///
/// 为什么要改场景里那个组件:PXR_Manager 在场景里有 1 份,它 Awake 时会用**自己身上的**
/// `foveationLevel` 字段去调 SetFoveationLevel —— 光改 Assets/Resources/PXR_ProjectSetting.asset
/// 是没用的(那个值只在你在 Inspector 上动 PXR_Manager 时被拷进组件)。
/// 这里把组件字段与项目设置一起改,运行期 VRLibraryBoot 还会再调一次兜底,且打印读回的档位。
///
/// 档位:PICO 的 FoveationLevel 是 None=-1, Low=0, Med=1, High=2, TopHigh=3。
/// 选 High(2):边缘降采样被镜片畸变遮住,中心不动;一体机上通常省 20~30% GPU。
/// </summary>
public static class VRLibraryFfr
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string ProjAsset = "Assets/Resources/PXR_ProjectSetting.asset";
    const int Level = 2;   // High

    [MenuItem("VRLibrary/性能·④ 开固定视场渲染 FFR(改场景:High)")]
    public static void On() { Apply(Level); }

    [MenuItem("VRLibrary/性能·④ 关固定视场渲染 FFR(改场景:None)")]
    public static void Off() { Apply(-1); }

    [MenuItem("VRLibrary/性能·④ 复核(只报告)")]
    public static void Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能④ FFR 复核 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("场景 PXR_Manager.foveationLevel = " + SceneLevel());
        var so = new SerializedObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ProjAsset));
        var p = so.FindProperty("foveationLevel");
        sb.AppendLine("项目设置 PXR_ProjectSetting.foveationLevel = " + (p == null ? "找不到" : p.intValue.ToString()));
        sb.AppendLine("运行期兜底:VRLibraryBoot.EnableFfr() 真机上会再调一次并打印读回档位");
        Flush(sb.ToString(), "Logs/perf-ffr.txt");
    }

    static void Apply(int level)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能④ FFR → " + Name(level) + " ==");
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) { Debug.LogError("[FFR] 当前场景不是 SmartLibrary:" + scene.path); return; }

        // ---- 场景里的 PXR_Manager(同样走反射找类型,不把 SDK 类型绑进编辑器程序集)
        var type = Type.GetType("Unity.XR.PXR.PXR_Manager, Unity.XR.PICO");
        Component found = null;
        if (type == null) sb.AppendLine("  !! 找不到类型 PXR_Manager,跳过场景侧");
        else
        {
            foreach (var c in UnityEngine.Object.FindObjectsOfType(type))
                if (c is Component comp) { found = comp; break; }
            if (found == null) sb.AppendLine("  !! 场景里没有 PXR_Manager");
            else
            {
                var so = new SerializedObject(found);
                var p = so.FindProperty("foveationLevel");
                if (p == null) sb.AppendLine("  !! PXR_Manager 上找不到 foveationLevel 字段");
                else
                {
                    sb.AppendLine("  " + Path(found.transform) + " 改前 " + p.intValue + "(" + Name(p.intValue) + ")");
                    p.intValue = level;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(found);
                    sb.AppendLine("  改后 " + p.intValue + "(" + Name(p.intValue) + ")");
                }
            }
        }

        // ---- 项目设置(与场景侧保持一致,免得下次有人在 Inspector 上点一下又拷回 None)
        var proj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ProjAsset);
        if (proj == null) sb.AppendLine("  !! 找不到 " + ProjAsset);
        else
        {
            var so = new SerializedObject(proj);
            var p = so.FindProperty("foveationLevel");
            if (p == null) sb.AppendLine("  !! 项目设置里找不到 foveationLevel");
            else
            {
                sb.AppendLine("  项目设置 改前 " + p.intValue + " → 改后 " + level);
                p.intValue = level;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(proj);
            }
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + (EditorSceneManager.SaveScene(scene) ? "成功" : "失败") + "  isDirty(应为 False)=" + scene.isDirty);
        Flush(sb.ToString(), "Logs/perf-ffr.txt");
    }

    static int SceneLevel()
    {
        var type = Type.GetType("Unity.XR.PXR.PXR_Manager, Unity.XR.PICO");
        if (type == null) return -99;
        foreach (var c in UnityEngine.Object.FindObjectsOfType(type))
        {
            var so = new SerializedObject(c);
            var p = so.FindProperty("foveationLevel");
            if (p != null) return p.intValue;
        }
        return -98;
    }

    static string Name(int lv)
    {
        switch (lv) { case -1: return "None"; case 0: return "Low"; case 1: return "Med"; case 2: return "High"; case 3: return "TopHigh"; default: return "?" + lv; }
    }

    static string Path(Transform t)
    {
        var s = t.name; var p = t.parent;
        while (p != null) { s = p.name + "/" + s; p = p.parent; }
        return s;
    }

    static void Flush(string text, string path)
    {
        try { System.IO.Directory.CreateDirectory("Logs"); System.IO.File.WriteAllText(path, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[FFR] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}