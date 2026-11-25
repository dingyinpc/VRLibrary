#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 2026-11-07:入口导览屏(开始菜单 = CentralKiosk/InfoCanvas)最底下加一颗通栏「退出图书馆」按钮。
///
/// 原面板底部是两排各 3 颗按钮(y=150 / y=65,高 78),再往下就贴到面板下沿了。
/// 本工具把两排各上移一行(y=172 / y=88),空出最底一排(y=4)放退出按钮,
/// 并把 StatusText 从 0.40~0.68 抬到 0.42~0.70,避免和第一排贴住。
///
/// 按钮由 Button_EnterLibrary 克隆而来(尺寸/字体/配色完全一致),
/// onClick 重新绑定到 LibraryUIController.OnExitLibraryButton(两下确认后退出应用)。
/// 可重复执行(已存在则复用并重设布局/监听),报告写 Logs/exit-button.txt。
/// </summary>
public static class VRLibraryExitButton
{
    const string ExitName = "Button_ExitLibrary";
    const string ReportPath = "Logs/exit-button.txt";

    [MenuItem("VRLibrary/体验·⑬ 开始菜单加「退出图书馆」按钮(改场景)", false, 500)]
    public static void Apply()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 开始菜单(InfoCanvas)加「退出图书馆」 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.IsValid()) { sb.AppendLine("!! 没有打开的场景"); Flush(sb); return; }

        RectTransform canvas = FindInfoCanvas();
        if (canvas == null) { sb.AppendLine("!! 找不到 CentralKiosk/InfoCanvas"); Flush(sb); return; }
        sb.AppendLine("canvas = " + canvas.name + "  size=" + canvas.rect.width + "x" + canvas.rect.height);

        var ctrl = Object.FindObjectOfType<LibraryUIController>(true);
        if (ctrl == null) { sb.AppendLine("!! 场上没有 LibraryUIController"); Flush(sb); return; }
        sb.AppendLine("controller = " + ctrl.gameObject.name);

        Undo.RegisterFullObjectHierarchyUndo(canvas.gameObject, "Add Exit Button");

        // 1) 原两排各上移一行,空出最底一排
        MoveRow(canvas, "Button_EnterLibrary", 172f, sb);
        MoveRow(canvas, "Button_Search", 172f, sb);
        MoveRow(canvas, "Button_Cinema", 172f, sb);
        MoveRow(canvas, "Button_Lights", 88f, sb);
        MoveRow(canvas, "Button_DayNight", 88f, sb);
        MoveRow(canvas, "Button_Guide", 88f, sb);

        // 2) 状态文字上抬,别和第一排贴住
        var status = canvas.Find("StatusText") as RectTransform;
        if (status != null)
        {
            status.anchorMin = new Vector2(0.04f, 0.42f);
            status.anchorMax = new Vector2(0.96f, 0.70f);
            status.offsetMin = Vector2.zero;
            status.offsetMax = Vector2.zero;
            sb.AppendLine("StatusText 上移到 0.42~0.70");
        }

        // 3) 造/更新退出按钮
        var src = canvas.Find("Button_EnterLibrary") as RectTransform;
        if (src == null) { sb.AppendLine("!! 找不到模板 Button_EnterLibrary"); Flush(sb); return; }

        var existing = canvas.Find(ExitName) as RectTransform;
        RectTransform exitRt;
        if (existing == null)
        {
            GameObject go = Object.Instantiate(src.gameObject, canvas);
            go.name = ExitName;
            exitRt = (RectTransform)go.transform;
            sb.AppendLine("已新建 " + ExitName);
        }
        else
        {
            exitRt = existing;
            sb.AppendLine("复用已存在的 " + ExitName);
        }

        exitRt.SetAsLastSibling();
        SetRect(exitRt, 0.03f, 0.97f, 4f, 78f);

        var label = exitRt.Find("Text");
        if (label != null)
        {
            var t = label.GetComponent<Text>();
            if (t != null)
            {
                t.text = "退出图书馆";
                sb.AppendLine("文字 = 退出图书馆 (fontSize=" + t.fontSize + ")");
            }
        }

        // 3.5) 换配色:深红,和"进入图书馆"的绿色区分开(退出语义)
        var img = exitRt.GetComponent<Image>();
        if (img != null)
        {
            img.color = new Color(0.42f, 0.15f, 0.15f, 1f);
            sb.AppendLine("配色 = 深红 " + ColorUtility.ToHtmlStringRGB(img.color));
        }

        // 4) 重新绑事件(清掉克隆带来的旧监听)
        var btn = exitRt.GetComponent<Button>();        if (btn == null) btn = exitRt.gameObject.AddComponent<Button>();
        btn.onClick = new Button.ButtonClickedEvent();
        UnityEventTools.AddPersistentListener(btn.onClick, ctrl.OnExitLibraryButton);
        sb.AppendLine("onClick → " + ctrl.GetType().Name + "." + nameof(ctrl.OnExitLibraryButton));

        // 5) 复核
        sb.AppendLine();
        sb.AppendLine("-- 复核(从面板底部往上) --");
        string[] all =
        {
            ExitName, "Button_Lights", "Button_DayNight", "Button_Guide",
            "Button_EnterLibrary", "Button_Search", "Button_Cinema"
        };
        foreach (string n in all)
        {
            var rt = canvas.Find(n) as RectTransform;
            if (rt == null) { sb.AppendLine("  " + n + " : 缺失"); continue; }
            var b = rt.GetComponent<Button>();
            int cnt = b != null ? b.onClick.GetPersistentEventCount() : -1;
            string who = "";
            if (b != null && cnt > 0)
            {
                var target = b.onClick.GetPersistentTarget(0) as Component;
                who = " → " + (target != null ? target.GetType().Name + "." : "") + b.onClick.GetPersistentMethodName(0);
            }
            sb.AppendLine("  " + n.PadRight(20) + " y=" + rt.anchoredPosition.y.ToString("0").PadLeft(4) +
                          "  h=" + rt.sizeDelta.y.ToString("0") +
                          "  x锚=" + rt.anchorMin.x.ToString("0.##") + "~" + rt.anchorMax.x.ToString("0.##") +
                          "  监听=" + cnt + who);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        sb.AppendLine("场景已保存 = " + saved);

        Flush(sb);
        Debug.Log(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑬ 复核(只报告)", false, 501)]
    public static void Verify()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 退出图书馆按钮 · 复核 ==");
        RectTransform canvas = FindInfoCanvas();
        if (canvas == null) { sb.AppendLine("!! 找不到 InfoCanvas"); Flush(sb); return; }
        var rt = canvas.Find(ExitName) as RectTransform;
        if (rt == null) { sb.AppendLine("!! 没有 " + ExitName); Flush(sb); return; }

        var b = rt.GetComponent<Button>();
        var txt = rt.Find("Text") != null ? rt.Find("Text").GetComponent<Text>() : null;
        sb.AppendLine("存在 = True");
        sb.AppendLine("activeInHierarchy = " + rt.gameObject.activeInHierarchy);
        sb.AppendLine("文字 = " + (txt != null ? txt.text : "<无>"));
        sb.AppendLine("y = " + rt.anchoredPosition.y + "  h = " + rt.sizeDelta.y);
        sb.AppendLine("x 锚 = " + rt.anchorMin.x + "~" + rt.anchorMax.x);
        if (b == null) sb.AppendLine("!! 没有 Button 组件");
        else
        {
            int cnt = b.onClick.GetPersistentEventCount();
            sb.AppendLine("监听数 = " + cnt);
            for (int i = 0; i < cnt; i++)
            {
                var target = b.onClick.GetPersistentTarget(i) as Component;
                sb.AppendLine("  [" + i + "] " + (target != null ? target.GetType().Name + "@" + target.gameObject.name : "<null>")
                              + "." + b.onClick.GetPersistentMethodName(i));
            }
        }
        Flush(sb);
        Debug.Log(sb.ToString());
    }

    static void MoveRow(RectTransform canvas, string name, float y, StringBuilder sb)
    {
        var rt = canvas.Find(name) as RectTransform;
        if (rt == null) { sb.AppendLine("  !! 缺 " + name); return; }
        SetRect(rt, rt.anchorMin.x, rt.anchorMax.x, y, 78f);
        sb.AppendLine("  " + name + " → y=" + y);
    }

    static void SetRect(RectTransform rt, float axMin, float axMax, float y, float h)
    {
        rt.anchorMin = new Vector2(axMin, 0f);
        rt.anchorMax = new Vector2(axMax, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(0f, h);
        rt.anchoredPosition = new Vector2(0f, y);
    }

    static RectTransform FindInfoCanvas()
    {
        foreach (var c in Object.FindObjectsOfType<Canvas>(true))
            if (c.name == "InfoCanvas" && c.transform.parent != null && c.transform.parent.name == "CentralKiosk")
                return (RectTransform)c.transform;

        GameObject go = GameObject.Find("SmartLibrary/CentralKiosk/InfoCanvas");
        if (go == null) go = GameObject.Find("CentralKiosk/InfoCanvas");
        return go != null ? go.GetComponent<RectTransform>() : null;
    }

    static void Flush(StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (System.Exception e) { Debug.LogWarning("[ExitButton] 报告落盘失败:" + e.Message); }
    }
}
#endif
