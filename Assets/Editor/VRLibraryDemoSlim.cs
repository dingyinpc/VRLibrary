#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 2026-11-06 演示裁剪(用户定案,演示前执行):
///
///   ① **归还服务整体移除** —— 书在 v7.31 改成"点击即读"(XRSimpleInteractable、无 Rigidbody、不可抓),
///      `XRSocketInteractor` 永远收不到书 ⇒ 这条链路实际不可用。删:
///        ServiceDesk/BookReturnSocket(含 ReturnRing)、ServiceDesk/ReturnStatusCanvas、
///        CentralKiosk/InfoCanvas/Button_ReturnInfo;并把剩下两颗底排按钮重新居中。
///   ② **HUD 整块禁用** —— 演示时不显示菜单/小地图/楼层。只 SetActive(false),不删,随时可恢复。
///      (语音脚本 LibraryVoiceCommands.cs 已直接删除,不在此脚本里处理。)
///
/// 为什么要"请求文件 + InitializeOnLoadMethod":演示前 Unity 已经开着,直接改 .unity 会被内存态覆盖;
/// 这里在域重载后自动跑一次(也可以手动点菜单)。幂等:已是目标状态就什么都不做。
/// 报告 `Logs/demo-slim.txt`,快照 `Logs/SmartLibrary.pre-demoslim.unity`。
/// </summary>
public static class VRLibraryDemoSlim
{
    const string kScene = "Assets/_Scenes/SmartLibrary.unity";
    const string kSnapshot = "Logs/SmartLibrary.pre-demoslim.unity";
    const string kReport = "Logs/demo-slim.txt";
    const string kRequest = "Logs/demo-slim-request.txt";

    [InitializeOnLoadMethod]
    static void AutoRun()
    {
        EditorApplication.delayCall += () =>
        {
            try
            {
                if (Application.isPlaying) return;
                string req = Abs(kRequest);
                if (!File.Exists(req)) return;
                File.Delete(req);
                Run(true);
            }
            catch (System.Exception e) { Debug.LogWarning("[DemoSlim] 自动执行失败:" + e.Message); }
        };
    }

    [MenuItem("VRLibrary/演示裁剪·现状(只报告)")]
    static void Audit() { Run(false); }

    [MenuItem("VRLibrary/演示裁剪·删归还服务 + 关 HUD(改场景)")]
    static void Fix() { Run(true); }

    static void Run(bool apply)
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != kScene)
        {
            // 场景没开着就尝试打开(只有当前场景没改动时才敢动)
            if (apply && !scene.isDirty)
            {
                scene = EditorSceneManager.OpenScene(kScene, OpenSceneMode.Single);
            }
            else
            {
                Debug.LogWarning("[DemoSlim] 当前场景不是 SmartLibrary(" + scene.path + "),跳过。");
                return;
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("== 演示裁剪 2026-11-06 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var sock = FindGo("BookReturnSocket");
        var status = FindGo("ReturnStatusCanvas");
        var retBtn = FindGo("Button_ReturnInfo");
        var hud = FindGo("LibraryHud");

        sb.AppendLine("  现状: BookReturnSocket=" + State(sock)
            + "  ReturnStatusCanvas=" + State(status)
            + "  Button_ReturnInfo=" + State(retBtn)
            + "  LibraryHud=" + State(hud));

        if (!apply) { Flush(sb); return; }

        SnapshotOnce(sb);

        int changed = 0;

        // ① 归还口(先删组件所在物,再删状态屏)
        if (sock != null) { Undo.DestroyObjectImmediate(sock); sb.AppendLine("  删除 ServiceDesk/BookReturnSocket"); changed++; }
        if (status != null) { Undo.DestroyObjectImmediate(status); sb.AppendLine("  删除 ServiceDesk/ReturnStatusCanvas"); changed++; }

        // ② 导览屏的「归还服务」按钮(只在 InfoCanvas 下才删,防止同名误删)
        if (retBtn != null)
        {
            var cv = retBtn.GetComponentInParent<Canvas>();
            if (cv != null && cv.name == "InfoCanvas")
            {
                Undo.DestroyObjectImmediate(retBtn);
                sb.AppendLine("  删除 CentralKiosk/InfoCanvas/Button_ReturnInfo");
                changed++;
            }
            else
            {
                sb.AppendLine("  !! Button_ReturnInfo 不在 InfoCanvas 下(父画布=" + (cv == null ? "无" : cv.name) + "),跳过");
            }
        }

        // ③ 底排剩下两颗重新居中:原 x 间距 245(生成器值)才动,手摆过的不碰
        var g1 = FindGo("Button_Guide");
        var g2 = FindGo("Button_Search");
        if (g1 != null && g2 != null)
        {
            var a = g1.GetComponent<RectTransform>();
            var b = g2.GetComponent<RectTransform>();
            if (a != null && b != null && Mathf.Abs((b.anchoredPosition.x - a.anchoredPosition.x) - 245f) < 8f)
            {
                Undo.RecordObject(a, "底排按钮居中");
                Undo.RecordObject(b, "底排按钮居中");
                a.anchoredPosition = new Vector2(a.anchoredPosition.x + 122.5f, a.anchoredPosition.y);
                b.anchoredPosition = new Vector2(b.anchoredPosition.x + 122.5f, b.anchoredPosition.y);
                EditorUtility.SetDirty(a); EditorUtility.SetDirty(b);
                sb.AppendLine("  底排两颗按钮右移 122.5 → 居中");
                changed++;
            }
            else sb.AppendLine("  底排按钮位置非生成器默认,跳过居中(不猜)");
        }

        // ④ HUD 整块禁用(不删)
        if (hud != null && hud.activeSelf)
        {
            hud.SetActive(false);
            EditorUtility.SetDirty(hud);
            sb.AppendLine("  LibraryHud → SetActive(false)");
            changed++;
        }

        // ⑤ 信息屏状态文案去掉"归还"相关引导
        var ui = Object.FindObjectOfType<LibraryUIController>(true);
        if (ui != null && ui.statusText != null && ui.statusText.text.Contains("归还"))
        {
            Undo.RecordObject(ui.statusText, "导览屏文案去掉归还");
            ui.statusText.text = "欢迎使用智能图书馆\n用射线指向按钮并扣动扳机,或传送/行走探索场馆";
            EditorUtility.SetDirty(ui.statusText);
            sb.AppendLine("  导览屏状态文案去掉「归还」");
            changed++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);

        sb.AppendLine();
        sb.AppendLine("  改后: BookReturnSocket=" + State(FindGo("BookReturnSocket"))
            + "  ReturnStatusCanvas=" + State(FindGo("ReturnStatusCanvas"))
            + "  Button_ReturnInfo=" + State(FindGo("Button_ReturnInfo"))
            + "  LibraryHud=" + State(FindGo("LibraryHud")));
        bool ok = FindGo("BookReturnSocket") == null
                  && FindGo("ReturnStatusCanvas") == null
                  && FindGo("Button_ReturnInfo") == null
                  && (FindGo("LibraryHud") == null || !FindGo("LibraryHud").activeSelf);
        sb.AppendLine("  改 " + changed + " 项;保存场景:" + (saved ? "成功" : "失败")
            + "  isDirty(应为 False)=" + scene.isDirty + "  → " + (ok ? "通过 ✓" : "!! 未通过"));
        sb.AppendLine("  回退:把 " + kSnapshot + " 复制回 " + kScene + " 后重开场景");
        Flush(sb);
        Debug.Log("[DemoSlim] 完成:改 " + changed + " 项 → " + kReport);
    }

    [MenuItem("VRLibrary/演示裁剪·导览屏按钮三列均分(改场景)")]
    static void SpreadKioskButtons()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != kScene) { Debug.LogWarning("[DemoSlim] 当前场景不是 SmartLibrary,跳过"); return; }

        var sb = new StringBuilder();
        sb.AppendLine("== 演示裁剪·导览屏底排三列均分 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");

        var names = new[] { "Button_EnterLibrary", "Button_Search", "Button_Cinema" };
        // 「归还服务」删掉后空出一格(0.51~0.73);把剩下三颗按 0.03~0.97 均分成三列。
        float[] x0 = { 0.03f, 0.35f, 0.67f };
        float[] x1 = { 0.33f, 0.65f, 0.97f };

        int changed = 0;
        for (int i = 0; i < names.Length; i++)
        {
            var go = FindGo(names[i]);
            if (go == null) { sb.AppendLine("  !! 找不到 " + names[i]); continue; }
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) { sb.AppendLine("  !! " + names[i] + " 没有 RectTransform"); continue; }
            sb.AppendLine("  " + names[i] + " 改前 " + rt.anchorMin + "~" + rt.anchorMax);
            Undo.RecordObject(rt, "导览屏底排均分");
            rt.anchorMin = new Vector2(x0[i], rt.anchorMin.y);
            rt.anchorMax = new Vector2(x1[i], rt.anchorMax.y);
            EditorUtility.SetDirty(rt);
            sb.AppendLine("  " + names[i] + " 改后 " + rt.anchorMin + "~" + rt.anchorMax);
            changed++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        sb.AppendLine("  改 " + changed + " 项;保存场景:" + (saved ? "成功" : "失败") + " isDirty=" + scene.isDirty);
        FlushTo("Logs/demo-slim-layout.txt", sb);
    }

    static void FlushTo(string rel, StringBuilder sb)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText(rel, sb.ToString(), new UTF8Encoding(false)); }
        catch (System.Exception e) { Debug.LogWarning("[DemoSlim] 报告落盘失败:" + e.Message); }
        Debug.Log(sb.ToString());
    }

    static string State(GameObject go) { return go == null ? "已移除" : (go.activeSelf ? "在" : "在(未激活)"); }

    static GameObject FindGo(string name)
    {
        foreach (var go in Object.FindObjectsOfType<GameObject>(true))
            if (go.name == name && go.hideFlags == HideFlags.None && go.scene.IsValid()) return go;
        return null;
    }

    static void SnapshotOnce(StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory("Logs");
            if (File.Exists(kSnapshot)) { sb.AppendLine("  快照已在(不覆盖):" + kSnapshot); return; }
            File.Copy(kScene, kSnapshot);
            sb.AppendLine("  快照:" + kSnapshot);
        }
        catch (System.Exception e) { sb.AppendLine("  !! 快照失败(仍继续):" + e.Message); }
    }

    static string Abs(string rel)
    {
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), rel.Replace('/', Path.DirectorySeparatorChar));
    }

    static void Flush(StringBuilder sb)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText(kReport, sb.ToString(), new UTF8Encoding(false)); }
        catch (System.Exception e) { Debug.LogWarning("[DemoSlim] 报告落盘失败:" + e.Message); }
        Debug.Log(sb.ToString());
    }
}
#endif
