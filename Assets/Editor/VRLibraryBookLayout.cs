using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 体验⑫:阅读面板(BookReaderPanel)的按钮布局。菜单 VRLibrary/体验·⑫ xxx。
///
/// 【为什么会有这个菜单 —— 2026-09-28 用户报的"全流程卡住"】
/// 用户原话:"书签占住了上一页的效果,所以说你没有办法回到上一页。会导致全流程的卡住。"
/// 就地量出来的真相(见 Logs/experience-book-layout.txt 的"现状"一节):
///     Btn_Prev      anchor (0.06,0)→(0.3,0)  pos(0,39)  size(0,50)   ← 面板内 x 39.6–198.0
///     Btn_Bookmark  anchor (0.06,0)→(0.3,0)  pos(0,39)  size(0,50)   ← **一模一样**
/// 两个键的矩形**逐像素重合**,而 Btn_Bookmark 在层级里排在 Btn_Prev 之后 ⇒ uGUI 后画的先吃
/// 射线,于是"点上一页"永远落在书签键上,只会在书签列表/翻页之间打转 —— 流程就走死了。
/// 这不是手感问题,是**布局撞车**,所以修法是挪位置而不是调参数。
///
/// 【用户拍板的布局】
///   ① 关闭键 → 书本**右上角**(角落键,让出底排中位);
///   ② 书签键 → **顶替原来关闭键的位置**(底排中位)。
/// 附带三处"让位"改动,都是为了让角落键真的可点、不被别的图元压住(每条都在 复核 里有断言):
///   ③ 标题/作者文字框各**缩短 168**(右端从面板 x648 收到 x576)—— 否则字框盖住角落键;
///      两个字都是居中对齐,字框缩短对短标题**肉眼无变化**,只是长标题少 168 的可排宽度;
///   ④ 书签列表面板**顶边从 y584 收到 y540** —— 它是个盖住面板右半边的内嵌浮层,
///      不收的话打开书签列表时角落键会被它压住;
///   ⑤ 角落键高 40(y552–592)落在顶栏带(y548–600)内,不越界到作者文字那一行。
///
/// 全部只改场景序列化值(RectTransform 的 anchor/pivot/anchoredPosition/sizeDelta),
/// 幂等(写的是绝对值,重复跑结果一样)、可自证(改完立刻按锚点反算矩形读回)、可回退。
/// </summary>
public static class VRLibraryBookLayout
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Snapshot = "Logs/SmartLibrary.pre-booklayout.unity";
    const string Report = "Logs/experience-book-layout.txt";

    // ──────────────────────────────────────────────────────────────── 布局表

    struct Spec
    {
        public string name;                 // 面板下的对象名(层里就是这个名字)
        public Vector2 aMin, aMax, pivot, pos, size;
        public string note;                 // 这一版为什么是这个数
    }

    static Spec S(string name, float ax, float ay, float bx, float by,
                  float px, float py, float x, float y, float w, float h, string note)
    {
        return new Spec
        {
            name = name,
            aMin = new Vector2(ax, ay), aMax = new Vector2(bx, by),
            pivot = new Vector2(px, py),
            pos = new Vector2(x, y), size = new Vector2(w, h),
            note = note
        };
    }

    /// <summary>原始布局(2026-09-28 动手前就地量到的值),回退就回到这里。</summary>
    static readonly Spec[] Origin =
    {
        S("Btn_Prev",      0.06f, 0f, 0.30f, 0f, 0.5f, 0.5f,    0f, 39f,    0f, 50f, "底排左"),
        S("Btn_Close",     0.38f, 0f, 0.62f, 0f, 0.5f, 0.5f,    0f, 39f,    0f, 50f, "底排中"),
        S("Btn_Next",      0.70f, 0f, 0.94f, 0f, 0.5f, 0.5f,    0f, 39f,    0f, 50f, "底排右"),
        S("Btn_Bookmark",  0.06f, 0f, 0.30f, 0f, 0.5f, 0.5f,    0f, 39f,    0f, 50f, "★与 Btn_Prev 逐像素重合(事故源)"),
        S("Title",         0f,    1f, 1f,    1f, 0.5f, 0.5f,    0f, -27f, -24f, 46f, "标题字框"),
        S("Author",        0f,    1f, 1f,    1f, 0.5f, 0.5f,    0f, -65f, -24f, 30f, "作者字框"),
        S("BookmarkList",  1f,    0f, 1f,    1f, 0.5f, 0.5f, -163f,  2f, 294f, -36f, "书签浮层(右半屏盖到 y584)"),
    };

    /// <summary>目标布局(用户拍板 + 三处让位)。</summary>
    static readonly Spec[] Target =
    {
        S("Btn_Prev",      0.06f, 0f, 0.30f, 0f, 0.5f, 0.5f,    0f, 39f,    0f, 50f, "不动:底排左"),
        S("Btn_Close",     1f,    1f, 1f,    1f, 1f,   1f,     -8f,  -8f,  72f, 40f, "①移到书本右上角(距右/上各 8,72×40)"),
        S("Btn_Next",      0.70f, 0f, 0.94f, 0f, 0.5f, 0.5f,    0f, 39f,    0f, 50f, "不动:底排右"),
        S("Btn_Bookmark",  0.38f, 0f, 0.62f, 0f, 0.5f, 0.5f,    0f, 39f,    0f, 50f, "②顶替原关闭位(底排中)"),
        S("Title",         0f,    1f, 1f,    1f, 0.5f, 0.5f,    0f, -27f, -168f, 46f, "③右边收 168,给角落键让位"),
        S("Author",        0f,    1f, 1f,    1f, 0.5f, 0.5f,    0f, -65f, -168f, 30f, "③同上"),
        S("BookmarkList",  1f,    0f, 1f,    1f, 0.5f, 0.5f, -163f, -20f, 294f, -80f, "④顶边 584→540,不压角落键"),
    };

    // ──────────────────────────────────────────────────────────────── 菜单

    [MenuItem("VRLibrary/体验·⑫ 阅读面板布局·现状(只报告)")]
    public static void Show()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 体验⑫ 阅读面板布局:现状 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.Append(DumpAll());
        sb.Append(Verify());
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑫ 应用(书签挪到关闭位 + 关闭挪到右上角)(改场景)")]
    public static void Apply()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 体验⑫ 应用目标布局 ==");
        sb.AppendLine("① 关闭键 → 书本右上角;② 书签键 → 顶替原关闭位(底排中)");
        sb.AppendLine("③ 标题/作者字框各收 168 让位;④ 书签浮层顶边 584→540 让位");
        sb.AppendLine("(原事故:Btn_Bookmark 与 Btn_Prev 锚点/位置/尺寸逐像素相同,且排在其后 ⇒ 上一页点不到)");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var br = Reader();
        if (br == null) { sb.AppendLine("!! 场景里找不到 BookReader,什么都不改"); Write(sb.ToString()); return; }
        SnapshotOnce();

        sb.AppendLine("\n---- 改前 ----");
        sb.Append(DumpAll());

        int done = 0;
        foreach (var sp in Target)
        {
            var rt = Find(br, sp.name);
            if (rt == null) { sb.AppendLine("  !! 找不到 " + sp.name + ",跳过"); continue; }
            Write(rt, sp);
            sb.AppendLine("  " + sp.name.PadRight(13) + " 已写 " + Fmt(sp) + "   (" + sp.note + ")");
            done++;
        }
        sb.AppendLine("共写 " + done + "/" + Target.Length + " 个对象");

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + EditorSceneManager.SaveScene(scene));

        sb.AppendLine("\n---- 改后 ----");
        sb.Append(DumpAll());
        sb.Append(Verify());
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑫ 复核(只报告)")]
    public static void ShowVerify()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 体验⑫ 阅读面板布局:复核 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.Append(DumpAll());
        sb.Append(Verify());
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑫ 回退(恢复原布局:书签回 上一页,关闭回底排中)")]
    public static void Revert()
    {
        var sb = new StringBuilder("== 体验⑫ 回退到动手前的布局 ==\n");
        var br = Reader();
        if (br == null) { sb.AppendLine("!! 找不到 BookReader"); Write(sb.ToString()); return; }

        foreach (var sp in Origin)
        {
            var rt = Find(br, sp.name);
            if (rt == null) { sb.AppendLine("  !! 找不到 " + sp.name); continue; }
            Write(rt, sp);
            sb.AppendLine("  " + sp.name.PadRight(13) + " 还原 " + Fmt(sp));
        }
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + EditorSceneManager.SaveScene(scene));
        sb.AppendLine("\n注:回退会**把书签键重新压回上一页上面**(那是动手前的原状)," +
                      "要那份原状只是为了对比,别留在工程里。");
        sb.Append("\n---- 改后 ----");
        sb.Append(DumpAll());
        sb.Append(Verify());
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑫ 把 Scene 视图对准阅读面板(只改编辑器视图,便于截图)")]
    public static void AimSceneView()
    {
        var br = Reader();
        if (br == null) { Debug.LogWarning("[BookLayout] 找不到 BookReader"); return; }
        var panel = (br.panelRoot != null ? br.panelRoot : br.gameObject).GetComponent<RectTransform>();
        var sv = SceneView.lastActiveSceneView;
        if (panel == null || sv == null) { Debug.LogWarning("[BookLayout] 没有 Scene 视图或面板"); return; }
        var center = panel.TransformPoint(new Vector3(panel.rect.center.x, panel.rect.center.y, 0f));
        // 面板在编辑态是**停在书架堆里**的(运行时才被 BookReader.Update 搬到玩家眼前),
        // 直接对准会被书架挡满 ⇒ 顺手把面板"隔离"出来(SceneVisibility 只影响编辑器显示,不进场景、不进包)。
        UnityEditor.SceneVisibilityManager.instance.Isolate(panel.gameObject, true);
        // 站到玩家那一侧看:BookReader.PositionInFront 让 panel.forward 从玩家射向面板,
        // 所以**相机旋转照抄 panel.rotation** 就是玩家眼里的样子(反了的话字是镜像的,一眼能看出来)。
        sv.LookAt(center, panel.rotation, panel.rect.width * panel.lossyScale.x * 1.05f);
        sv.Repaint();
        Debug.Log("[BookLayout] Scene 视图已对准(并隔离)" + panel.name + " 中心 " + center
                  + " 宽 " + (panel.rect.width * panel.lossyScale.x).ToString("0.###") + "m");
    }

    [MenuItem("VRLibrary/体验·⑫ 退出隔离(恢复 Scene 视图可见性)")]
    public static void ExitIsolation()
    {
        UnityEditor.SceneVisibilityManager.instance.ExitIsolation();
        var sv = SceneView.lastActiveSceneView;
        if (sv != null) sv.Repaint();
        Debug.Log("[BookLayout] 已退出隔离");
    }

    // ──────────────────────────────────────────────────────────────── 读写

    static void Write(RectTransform rt, Spec sp)
    {
        rt.anchorMin = sp.aMin;
        rt.anchorMax = sp.aMax;
        rt.pivot = sp.pivot;
        rt.anchoredPosition = sp.pos;
        rt.sizeDelta = sp.size;
        EditorUtility.SetDirty(rt);
    }

    /// <summary>按锚点反算矩形:面板坐标系(原点 = 面板矩形左下角),和 RT.rect 同一套单位。</summary>
    static Rect RectOf(RectTransform rt)
    {
        var p = rt.parent as RectTransform;
        if (p == null) return rt.rect;
        Rect pr = p.rect;
        float ax = pr.xMin + rt.anchorMin.x * pr.width;
        float ay = pr.yMin + rt.anchorMin.y * pr.height;
        float bx = pr.xMin + rt.anchorMax.x * pr.width;
        float by = pr.yMin + rt.anchorMax.y * pr.height;
        float w = (bx - ax) + rt.sizeDelta.x;
        float h = (by - ay) + rt.sizeDelta.y;
        float cx = ax + rt.pivot.x * (bx - ax) + rt.anchoredPosition.x;
        float cy = ay + rt.pivot.y * (by - ay) + rt.anchoredPosition.y;
        return new Rect(cx - rt.pivot.x * w, cy - rt.pivot.y * h, w, h);
    }

    static BookReader Reader()
    {
        return UnityEngine.Object.FindObjectOfType<BookReader>(true);
    }

    static RectTransform Find(BookReader br, string name)
    {
        foreach (var rt in br.GetComponentsInChildren<RectTransform>(true))
            if (rt.name == name) return rt;
        return null;
    }

    /// <summary>关闭键不靠名字认:认"按钮的持久回调打到 BookReader.Close",名字改了也不会失手。</summary>
    static Button ByMethod(BookReader br, string method)
    {
        foreach (var b in br.GetComponentsInChildren<Button>(true))
        {
            var so = new SerializedObject(b);
            var calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
            if (calls == null) continue;
            for (int i = 0; i < calls.arraySize; i++)
            {
                var e = calls.GetArrayElementAtIndex(i);
                var t = e.FindPropertyRelative("m_Target");
                var m = e.FindPropertyRelative("m_MethodName");
                if (t != null && m != null && t.objectReferenceValue == br && m.stringValue == method)
                    return b;
            }
        }
        return null;
    }

    static string Fmt(Spec sp)
    {
        return string.Format("anchor({0:0.##},{1:0.##})→({2:0.##},{3:0.##}) pivot({4:0.##},{5:0.##}) pos({6:0.#},{7:0.#}) size({8:0.#},{9:0.#})",
            sp.aMin.x, sp.aMin.y, sp.aMax.x, sp.aMax.y, sp.pivot.x, sp.pivot.y, sp.pos.x, sp.pos.y, sp.size.x, sp.size.y);
    }

    // ──────────────────────────────────────────────────────────────── 读数与断言

    static string DumpAll()
    {
        var sb = new StringBuilder();
        var br = Reader();
        if (br == null) { sb.AppendLine("!! 场景里找不到 BookReader"); return sb.ToString(); }

        var panel = br.panelRoot != null ? br.panelRoot.GetComponent<RectTransform>() : null;
        if (panel == null) panel = br.GetComponent<RectTransform>();
        sb.AppendLine("面板 " + (panel != null ? panel.name : "(?)") + "  rect=" + (panel != null ? panel.rect.ToString() : "?")
                      + "  sizeDelta=" + (panel != null ? panel.sizeDelta.ToString() : "?")
                      + "  worldScale=" + (panel != null ? panel.lossyScale.x.ToString("0.#####") : "?"));
        if (panel != null)
            sb.AppendLine("   → 世界尺寸 " + (panel.rect.width * panel.lossyScale.x).ToString("0.###") + "m × "
                          + (panel.rect.height * panel.lossyScale.y).ToString("0.###") + "m(角落键 "
                          + (72f * panel.lossyScale.x).ToString("0.###") + "m × " + (40f * panel.lossyScale.y).ToString("0.###") + "m)");

        sb.AppendLine("---- 面板坐标系里的矩形(原点=面板左下角) ----");
        foreach (var rt in br.GetComponentsInChildren<RectTransform>(true))
        {
            if (rt == panel) continue;
            var btn = rt.GetComponent<Button>();
            var r = RectOf(rt);
            string role = btn != null && btn == br.bookmarkBtn ? "书签键(字段)"
                        : btn != null && ByMethod(br, "PrevPage") == btn ? "上一页键"
                        : btn != null && ByMethod(br, "NextPage") == btn ? "下一页键"
                        : btn != null && ByMethod(br, "Close") == btn ? "关闭键"
                        : (btn != null ? "按钮" : "");
            if (rt.parent != panel) continue;    // 只列面板直接子级(底排/字框/浮层)
            sb.AppendLine("  " + rt.name.PadRight(13) + " " + role.PadRight(10)
                          + " x " + r.xMin.ToString("0.0") + "–" + r.xMax.ToString("0.0")
                          + "  y " + r.yMin.ToString("0.0") + "–" + r.yMax.ToString("0.0")
                          + "  w " + r.width.ToString("0.0") + " h " + r.height.ToString("0.0")
                          + "  active=" + rt.gameObject.activeSelf);
        }
        return sb.ToString();
    }

    static string Verify()
    {
        var sb = new StringBuilder();
        sb.AppendLine("\n---- 复核(每条都现场量,不认死值) ----");
        var br = Reader();
        if (br == null) { sb.AppendLine("!! 找不到 BookReader,无法复核"); return sb.ToString(); }

        var panel = (br.panelRoot != null ? br.panelRoot : br.gameObject).GetComponent<RectTransform>();
        var prev = ByMethod(br, "PrevPage") != null ? ByMethod(br, "PrevPage").GetComponent<RectTransform>() : null;
        var next = ByMethod(br, "NextPage") != null ? ByMethod(br, "NextPage").GetComponent<RectTransform>() : null;
        var close = ByMethod(br, "Close") != null ? ByMethod(br, "Close").GetComponent<RectTransform>() : null;
        var bmk = br.bookmarkBtn != null ? br.bookmarkBtn.GetComponent<RectTransform>() : null;
        var list = br.bookmarkListPanel != null ? br.bookmarkListPanel.GetComponent<RectTransform>() : null;
        var title = Find(br, "Title");
        var author = Find(br, "Author");

        if (prev == null || next == null || close == null || bmk == null || panel == null)
        { sb.AppendLine("!! 关键对象认不齐(上一页/下一页/关闭/书签/面板),复核中止 —— 先看上面的列表"); return sb.ToString(); }

        Rect rPrev = RectOf(prev), rNext = RectOf(next), rClose = RectOf(close), rBmk = RectOf(bmk), rPanel = panel.rect;
        int bad = 0;

        // ① 事故本体:书签键与上一页键不许再重合
        bool hit = rBmk.Overlaps(rPrev, true);
        bad += Line(sb, !hit, "书签键不与上一页键重叠(事故本体)"
            + (hit ? "  ← 仍重叠!书签键会吃掉上一页的点击" : "  书签 x" + rBmk.xMin.ToString("0.0") + "–" + rBmk.xMax.ToString("0.0")
                 + " / 上一页 x" + rPrev.xMin.ToString("0.0") + "–" + rPrev.xMax.ToString("0.0")));

        // ② 底排四个键两两不压(排序:上一页 / 书签 / 关闭若在底排 / 下一页)
        var row = new List<KeyValuePair<string, Rect>>();
        row.Add(new KeyValuePair<string, Rect>("上一页", rPrev));
        row.Add(new KeyValuePair<string, Rect>("书签", rBmk));
        row.Add(new KeyValuePair<string, Rect>("下一页", rNext));
        if (rClose.yMax < rPanel.yMin + rPanel.height * 0.25f) row.Add(new KeyValuePair<string, Rect>("关闭", rClose));
        var clash = new List<string>();
        for (int i = 0; i < row.Count; i++)
            for (int j = i + 1; j < row.Count; j++)
                if (row[i].Value.Overlaps(row[j].Value, true)) clash.Add(row[i].Key + "↔" + row[j].Key);
        bad += Line(sb, clash.Count == 0, "底排可点键两两不重叠(" + row.Count + " 个)"
            + (clash.Count > 0 ? "  ← 压住:" + string.Join("、", clash.ToArray()) : ""));

        // ③ 关闭键真的在右上角(右半 + 上四分之一,且不出面板)
        bool inPanel = rPanel.xMin <= rClose.xMin && rClose.xMax <= rPanel.xMax
                    && rPanel.yMin <= rClose.yMin && rClose.yMax <= rPanel.yMax;
        bool topRight = rClose.center.x > rPanel.center.x && rClose.center.y > rPanel.yMin + rPanel.height * 0.75f;
        bad += Line(sb, inPanel && topRight, "关闭键在书本右上角且不出界"
            + "  中心(" + rClose.center.x.ToString("0.0") + "," + rClose.center.y.ToString("0.0") + ")"
            + " 面板中心x " + rPanel.center.x.ToString("0.0") + " 上四分之一线 y " + (rPanel.yMin + rPanel.height * 0.75f).ToString("0.0")
            + " 出界=" + !inPanel);
        float dRight = rPanel.xMax - rClose.xMax, dTop = rPanel.yMax - rClose.yMax;
        sb.AppendLine("     距右边缘 " + dRight.ToString("0.0") + " / 距上边缘 " + dTop.ToString("0.0")
                      + "(面板单位,乘 worldScale 就是米)");

        // ④ 打开书签列表时角落键不被压住
        if (list != null)
        {
            bool cover = RectOf(list).Overlaps(rClose, true);
            bad += Line(sb, !cover, "书签浮层不压住关闭键"
                + "  浮层 y " + RectOf(list).yMin.ToString("0.0") + "–" + RectOf(list).yMax.ToString("0.0")
                + " vs 关闭键 y " + rClose.yMin.ToString("0.0") + "–" + rClose.yMax.ToString("0.0"));
        }

        // ⑤ 关闭键不压标题/作者文字框
        var txtClash = new List<string>();
        if (title != null && RectOf(title).Overlaps(rClose, true)) txtClash.Add("标题");
        if (author != null && RectOf(author).Overlaps(rClose, true)) txtClash.Add("作者");
        bad += Line(sb, txtClash.Count == 0, "关闭键不压标题/作者字框"
            + (txtClash.Count > 0 ? "  ← 压住:" + string.Join("、", txtClash.ToArray()) : ""));

        // ⑥ 角落键够不够点:世界尺寸别小于 8cm(底排键是 34.8cm×11cm,角落键可以小,但别小到手够不着)
        float wm = rClose.width * panel.lossyScale.x, hm = rClose.height * panel.lossyScale.y;
        bad += Line(sb, wm >= 0.08f && hm >= 0.05f, "角落键世界尺寸够点 " + wm.ToString("0.###") + "m × " + hm.ToString("0.###") + "m(门槛 0.08×0.05)");

        sb.AppendLine(bad == 0 ? "复核结论:全部 ✓(" + 6 + " 条)" : "复核结论:!! " + bad + " 条不过");
        return sb.ToString();
    }

    static int Line(StringBuilder sb, bool ok, string text)
    {
        sb.AppendLine("  " + (ok ? "✓ " : "!! ") + text);
        return ok ? 0 : 1;
    }

    static void SnapshotOnce()
    {
        try
        {
            if (System.IO.File.Exists(Snapshot)) { Debug.Log("[BookLayout] 已有快照,不覆盖:" + Snapshot); return; }
            System.IO.Directory.CreateDirectory("Logs");
            System.IO.File.Copy(ScenePath, Snapshot);
            Debug.Log("[BookLayout] 落盘快照 " + Snapshot);
        }
        catch (Exception e) { Debug.LogWarning("[BookLayout] 快照失败:" + e.Message); }
    }

    static void Write(string text)
    {
        try { System.IO.Directory.CreateDirectory("Logs"); System.IO.File.WriteAllText(Report, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[BookLayout] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}