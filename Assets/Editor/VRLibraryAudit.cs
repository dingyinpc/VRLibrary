using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 智能图书馆 · 全场景体检。
/// 菜单 VRLibrary/审计全场景(写报告) → 输出 Logs/scene-audit.txt。
/// 只读不改:每项检查都给出"判定依据 + 命中数量 + 前若干个样本",便于定位后手工修。
/// 为什么需要:桥接没有 Console 回读通道,MCP 动态编译也看不到新脚本,
///            所以把探针写成菜单项、把结果落到文件再读。
/// </summary>
public static class VRLibraryAudit
{
    const string kReport = "Logs/scene-audit.txt";
    const int kSample = 8;

    [MenuItem("VRLibrary/审计全场景(写报告)")]
    public static void Run()
    {
        Physics.SyncTransforms();
        var scene = EditorSceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        var all = new List<GameObject>();
        foreach (var r in roots) all.AddRange(r.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject));

        var sb = new StringBuilder();
        Line(sb, "智能图书馆 · 全场景体检报告");
        Line(sb, "生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Line(sb, "场景: " + scene.path + "   已激活: " + scene.IsValid() + "   未保存改动: " + scene.isDirty);
        Line(sb, "根节点: " + roots.Length + "   物体总数(含隐藏): " + all.Count);
        Line(sb, "运行时组件: LibraryBook=" + Count<LibraryBook>(all)
             + "  XRGrabInteractable=" + Count<XRGrabInteractable>(all)
             + "  TextMesh=" + Count<TextMesh>(all)
             + "  Rigidbody=" + Count<Rigidbody>(all)
             + "  Collider=" + Count<Collider>(all)
             + "  TeleportationArea=" + Count<TeleportationArea>(all)
             + "  TeleportationAnchor=" + Count<TeleportationAnchor>(all));
        Line(sb, "XR 装备: XROrigin=" + Count<Unity.XR.CoreUtils.XROrigin>(all));

        Section(sb, "A. 丢引用 / 缺失脚本");
        CheckMissingScripts(sb, all);

        Section(sb, "B. 非法 Transform(NaN / 零缩放 / 极端值)");
        CheckTransforms(sb, all);

        Section(sb, "C. 书籍交互链路完整性(缺组件就抓不到/打不开)");
        CheckBooks(sb, all);

        Section(sb, "D. 电子屏标题与屏体是否匹配 / 是否溢出屏外");
        CheckScreens(sb, all);

        Section(sb, "E. 编号(slotCode)质量:缺失 / 重复 → 检索跳错书");
        CheckSlotCodes(sb, all);

        Section(sb, "F. TextMesh 材质受光检查(坑 #5:必须无光照)");
        CheckTextMaterials(sb, all);

        Section(sb, "G. 同层书本碰撞体重叠(挤在一起就抓不出来)");
        CheckBookCollisions(sb, all);

        Section(sb, "H. 传送与出生点(坑 #6:Origin 必须回到局部原点)");
        CheckTeleport(sb, all);

        Section(sb, "I. 隐藏但仍参与交互的物体");
        CheckInactiveInteractables(sb, all);

        var dir = "Logs";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/" + System.IO.Path.GetFileName(kReport), sb.ToString(), Encoding.UTF8);
        DumpBooksCsv(dir + "/audit-books.csv", all);
        DumpSlotsCsv(dir + "/audit-slots.csv", all);
        Debug.Log("[VRLibraryAudit] 体检完成 → " + kReport + " 与 Logs/audit-books.csv");
    }

    /// <summary>逐书几何导出,供离线分析定改法(单位:世界米)。</summary>
    static void DumpBooksCsv(string file, List<GameObject> all)
    {
        var sb = new StringBuilder();
        sb.AppendLine("chain,name,slotCode,charSize,lines,maxLineLen,titleFlat," +
                      "dispW,dispH,textW,textH,overW,overH,dispLocalScale,bookLocalPos,bookWorldPos");
        foreach (var b in all.SelectMany(g => g.GetComponents<LibraryBook>()))
        {
            var tm = FindDeep<TextMesh>(b.transform, "TitleText");
            var disp = FindDeep<MeshRenderer>(b.transform, "Display");
            if (tm == null || disp == null) continue;
            var tmr = tm.GetComponent<MeshRenderer>();
            if (tmr == null) continue;
            var a = tmr.bounds; var d = disp.bounds;
            // TitleText 带 180°Y 旋转,world 的 x/z 会互换:屏幕宽取 max(x,z)
            float tw = Mathf.Max(a.size.x, a.size.z), th = a.size.y;
            float dw = Mathf.Max(d.size.x, d.size.z), dh = d.size.y;
            var flat = tm.text.Replace("\n", "");
            int maxLen = 0, cur = 0;
            foreach (char c in tm.text) { if (c == '\n') { maxLen = Mathf.Max(maxLen, cur); cur = 0; } else cur++; }
            maxLen = Mathf.Max(maxLen, cur);
            var dls = disp.transform.localScale;
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(Chain(b.gameObject)), Csv(b.gameObject.name), Csv(b.slotCode ?? ""),
                tm.characterSize.ToString("F8"), tm.text.Count(x => x == '\n').ToString(),
                maxLen.ToString(), Csv(flat),
                dw.ToString("F4"), dh.ToString("F4"), tw.ToString("F4"), th.ToString("F4"),
                (tw - dw).ToString("F4"), (th - dh).ToString("F4"),
                Csv(string.Format("{0:F4}|{1:F4}|{2:F4}", dls.x, dls.y, dls.z)),
                Csv(string.Format("{0:F4}|{1:F4}|{2:F4}", b.transform.localPosition.x,
                                  b.transform.localPosition.y, b.transform.localPosition.z)),
                Csv(string.Format("{0:F4}|{1:F4}|{2:F4}", b.transform.position.x,
                                  b.transform.position.y, b.transform.position.z)),
            }));
        }
        File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>书架格子编号标签导出(编号文字的权威来源)。</summary>
    static void DumpSlotsCsv(string file, List<GameObject> all)
    {
        var sb = new StringBuilder();
        sb.AppendLine("slotPath,shelfPath,worldPos,labelText");
        foreach (var go in all)
        {
            if (!go.name.StartsWith("Slot_") || go.name.StartsWith("SlotStrip")) continue;
            var t = go.GetComponent<TextMesh>();
            if (t == null) continue;
            var shelf = go.transform.parent != null ? go.transform.parent.parent : null;
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(Chain(go)), Csv(shelf != null ? Chain(shelf.gameObject) : ""),
                Csv(string.Format("{0:F4}|{1:F4}|{2:F4}", go.transform.position.x,
                                  go.transform.position.y, go.transform.position.z)),
                Csv(t.text.Replace("\n", "")),
            }));
        }
        File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
    }

    static string Csv(string x)
    {
        if (string.IsNullOrEmpty(x)) return "";
        if (x.Contains(",") || x.Contains("\"")) return "\"" + x.Replace("\"", "\"\"") + "\"";
        return x.Replace("\n", "↵").Replace("\r", "");
    }

    /// <summary>
    /// 把 Scene 视图移到指定机位。桥只有 captureScreenshot(截当前 Scene 视图)、没有相机控制,
    /// 所以远程验收截图前先用这个取景:往 Logs/frame.txt 写一行 "x,y,z|yaw,pitch|size" 再执行本菜单。
    /// </summary>
    [MenuItem("VRLibrary/取景(读Logs/frame.txt)")]
    public static void FrameView()
    {
        const string f = "Logs/frame.txt";
        if (!File.Exists(f)) { Debug.LogError("[VRLibraryAudit] 缺少 " + f + ",内容格式:x,y,z|yaw,pitch|size"); return; }
        var parts = File.ReadAllText(f).Trim().Split('|');
        if (parts.Length < 3) { Debug.LogError("[VRLibraryAudit] " + f + " 格式应为 x,y,z|yaw,pitch|size"); return; }
        var p = parts[0].Split(','); var r = parts[1].Split(',');
        var sv = SceneView.lastActiveSceneView;
        if (sv == null) { Debug.LogError("[VRLibraryAudit] 没有可用的 Scene 视图窗口"); return; }
        sv.LookAt(new Vector3(p[0].ToF(), p[1].ToF(), p[2].ToF()),
                  Quaternion.Euler(r[1].ToF(), r[0].ToF(), 0f), parts[2].ToF(), false);
        sv.Repaint();
        Debug.Log("[VRLibraryAudit] 已取景到 " + parts[0] + " 朝向 " + parts[1]);
    }

    static float ToF(this string s)
    {
        float v; return float.TryParse(s.Trim(), out v) ? v : 0f;
    }

    // ── 各项检查 ────────────────────────────────────────────────────

    static void CheckMissingScripts(StringBuilder sb, List<GameObject> all)
    {
        int n = 0; var hits = new List<string>();
        foreach (var go in all)
        {
            var comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] == null)
                {
                    n++; if (hits.Count < kSample) hits.Add(Chain(go) + " 的第 " + i + " 个组件为 null(脚本被删/改名)");
                    continue;
                }
                var mb = comps[i] as MonoBehaviour;
                if (mb == null) continue;
                var so = new SerializedObject(mb);
                var p = so.FindProperty("m_Script");
                if (p != null && p.objectReferenceValue == null)
                {
                    n++; if (hits.Count < kSample) hits.Add(Chain(go) + " 上 " + comps[i].GetType().Name + " 的脚本引用丢失");
                }
            }
        }
        Report(sb, n == 0, "丢失脚本 / null 组件", n, hits);
    }

    static void CheckTransforms(StringBuilder sb, List<GameObject> all)
    {
        int n = 0; var hits = new List<string>();
        foreach (var go in all)
        {
            var t = go.transform;
            bool isUI = t is RectTransform;
            bool bad = IsNaN(t.localPosition) || IsNaN(t.localScale) || IsNaN(t.localEulerAngles);
            if (!bad && (t.localScale.x <= 0f || t.localScale.y <= 0f || t.localScale.z <= 0f)) bad = true;
            if (!bad && !isUI && t.localPosition.magnitude > 500f) bad = true;
            if (bad)
            {
                n++; if (hits.Count < kSample)
                    hits.Add(Chain(go) + " pos=" + Fmt(t.localPosition) + " scale=" + Fmt(t.localScale));
            }
        }
        Report(sb, n == 0, "NaN / 零缩放 / 飞出去", n, hits);
    }

    static void CheckBooks(StringBuilder sb, List<GameObject> all)
    {
        var books = all.SelectMany(g => g.GetComponents<LibraryBook>()).ToList();
        var missGrab = new List<string>(); var missRb = new List<string>();
        var missCol = new List<string>(); var emptyTitle = new List<string>();
        foreach (var b in books)
        {
            if (b.GetComponent<XRGrabInteractable>() == null) missGrab.Add(Chain(b.gameObject) + " title=" + b.title);
            if (b.GetComponent<Rigidbody>() == null) missRb.Add(Chain(b.gameObject));
            if (b.GetComponent<Collider>() == null) missCol.Add(Chain(b.gameObject));
            if (string.IsNullOrEmpty(b.title) || string.IsNullOrEmpty(b.summary)) emptyTitle.Add(Chain(b.gameObject));
        }
        Report(sb, missGrab.Count == 0, "书本总数 " + books.Count + " 本 · 缺抓取组件", missGrab.Count, missGrab.Take(kSample).ToList());
        Report(sb, missRb.Count == 0, "缺 Rigidbody(抓取会掉穿)", missRb.Count, missRb.Take(kSample).ToList());
        Report(sb, missCol.Count == 0, "缺 Collider(射线点不到)", missCol.Count, missCol.Take(kSample).ToList());
        Report(sb, emptyTitle.Count == 0, "书名/正文为空", emptyTitle.Count, emptyTitle.Take(kSample).ToList());

        // 阅读面板入口(对象池设计:面板初始关闭,必须带 includeInactive)
        var rd = UnityEngine.Object.FindObjectOfType<BookReader>(true);
        Report(sb, rd != null, "BookReader 存在(抓书→扳机才能开面板)", rd ? 0 : 1,
               rd ? null : new List<string> { "场景里找不到 BookReader 组件" });
        var ui = UnityEngine.Object.FindObjectOfType<LibraryUIController>(true);
        Report(sb, ui != null, "LibraryUIController 存在", ui ? 0 : 1, null);
        var search = UnityEngine.Object.FindObjectOfType<BookSearchController>(true);
        Report(sb, search != null, "BookSearchController 存在", search ? 0 : 1, null);
    }

    static void CheckScreens(StringBuilder sb, List<GameObject> all)
    {
        var books = all.SelectMany(g => g.GetComponents<LibraryBook>()).ToList();
        var mismatch = new List<string>();
        var spill = new List<string>();
        int noScreen = 0;
        foreach (var b in books)
        {
            var tm = FindDeep<TextMesh>(b.transform, "TitleText");
            var disp = FindDeep<MeshRenderer>(b.transform, "Display");
            if (tm == null || disp == null) { noScreen++; if (mismatch.Count < kSample) mismatch.Add(Chain(b.gameObject) + " 找不到 TitleText/Display"); continue; }
            string want = b.title.Replace("\n", "").Replace(" ", "");
            string got = tm.text.Replace("\n", "").Replace(" ", "").Replace("…", "");
            if (!got.StartsWith(want.Substring(0, Math.Min(4, want.Length))))
                mismatch.Add(Chain(b.gameObject) + " 屏上=" + Qu(tb(tm.text)) + " 数据=" + Qu(tb(b.title)));
            else
            {
                var tmr = tm.GetComponent<MeshRenderer>();
                if (tmr == null) { noScreen++; if (mismatch.Count < kSample) mismatch.Add(Chain(b.gameObject) + " TitleText 没有 MeshRenderer"); continue; }
                var a = tmr.bounds;
                var bnd = disp.bounds;
                float tol = 0.012f;
                if (a.max.y > bnd.max.y + tol || a.min.y < bnd.min.y - tol ||
                    a.max.x > bnd.max.x + tol || a.min.x < bnd.min.x - tol)
                    spill.Add(Chain(b.gameObject) + " 标题盒 " + Fmt(a.size) + " vs 屏盒 " + Fmt(bnd.size) + " 标题=" + Qu(tb(b.title)));
            }
        }
        Report(sb, noScreen == 0, "无电子屏子物体的书", noScreen, null);
        Report(sb, mismatch.Count == 0, "屏上文字与 LibraryBook.title 不一致", mismatch.Count, mismatch.Take(kSample).ToList());
        Report(sb, spill.Count == 0, "标题超出屏面(会被裁或压到边框外)", spill.Count, spill.Take(kSample).ToList());
    }

    static void CheckSlotCodes(StringBuilder sb, List<GameObject> all)
    {
        var books = all.SelectMany(g => g.GetComponents<LibraryBook>()).ToList();
        var labels = new List<KeyValuePair<Transform, string>>();
        foreach (var go in all)
        {
            if (!go.name.StartsWith("Slot_") || go.name.StartsWith("SlotStrip")) continue;
            var t = go.GetComponent<TextMesh>();
            if (t == null || string.IsNullOrEmpty(t.text)) continue;
            labels.Add(new KeyValuePair<Transform, string>(go.transform, t.text.Replace("\n", "")));
        }

        var onShelf = books.Where(b => OnShelf(b.transform)).ToList();
        var empty = onShelf.Where(b => string.IsNullOrEmpty(b.slotCode))
                           .Select(b => Chain(b.gameObject) + " " + b.title).ToList();
        Report(sb, empty.Count == 0, "书架上的书 slotCode 为空", empty.Count, empty.Take(kSample).ToList());

        // 一、纯几何:一层每架每层按 0.62 m 聚类,同一列挤了几本书(不涉及任何编号约定)
        //     二层是多排斜列(不同排的书沿架坐标相同),不参与,否则会虚报挤格
        int stackedCols = 0;
        var stackSample = new List<string>();
        var oneFloor = onShelf.Where(b => (ShelfOf(b.transform) ?? "").Contains("/Shelf_")).ToList();
        foreach (var grp in oneFloor.GroupBy(b => ShelfOf(b.transform)))
        {
            foreach (var lvl in grp.GroupBy(b => LevelBucket(b.transform.position.y)))
                foreach (var col in lvl.GroupBy(b => Mathf.RoundToInt(b.transform.position.z / 0.62f)))
                    if (col.Count() > 1)
                    {
                        stackedCols++;
                        if (stackSample.Count < kSample)
                            stackSample.Add(grp.Key.Split('/').Last() + " 层" + lvl.Key + " 一列挤 " + col.Count() + " 本:"
                                            + string.Join(" + ", col.Select(x => x.gameObject.name + "[" + x.title + "]")));
                    }
        }
        Report(sb, stackedCols == 0, "一层同一列挤着书(会互相穿插、抓取拿错)", stackedCols, stackSample);

        // 二、编号方向:标签装在格与格之间(实测标签恒在书下方 0.075 m、沿架差 0.56 m = 一格-0.06),
        //     所以"最近标签"其实是邻格。哪种读法算本馆约定属于产品决定,这里只把证据列出来。
        if (labels.Count == 0) { Line(sb, "  · 没找到格子编号标签,无法给出编号方向证据"); return; }
        int far = 0, near = 0, neither = 0, unknown = 0;
        foreach (var b in oneFloor)
        {
            if (string.IsNullOrEmpty(b.slotCode)) continue;
            var p = b.transform.position;
            string farCode = null, nearCode = null;
            foreach (var l in labels)
            {
                if (ShelfOf(l.Key) != ShelfOf(b.transform)) continue;
                if (Mathf.Abs(l.Key.position.y - p.y) > 0.35f) continue;      // 不同层
                float dz = Mathf.Abs(l.Key.position.z - p.z);
                if (dz < 0.15f) nearCode = l.Value;
                else if (dz > 0.45f && dz < 0.70f) farCode = l.Value;
            }
            if (farCode == null && nearCode == null) { unknown++; continue; }
            if (farCode == b.slotCode) far++;
            else if (nearCode == b.slotCode) near++;
            else neither++;
        }
        Line(sb, "  · 编号方向证据(一层 " + (far + near + neither) + " 本有编号):"
             + "书编号 = 0.56 m 外标签 的有 " + far + " 本;= 0.06 m 邻格标签 的有 " + near
             + " 本;两者都不是 " + neither + " 本;找不到邻近标签 " + unknown + " 本");
        Line(sb, "  · 实测书本列序与标签列序相反(列号互补为 9)。以哪一侧为准是展馆编号约定,需人工确认后再统一。");
    }

    /// <summary>是否挂在书架(Bookshelves/Shelves2F)之下。</summary>
    static bool OnShelf(Transform t)
    {
        while (t != null)
        {
            if (t.name == "Bookshelves" || t.name == "Shelves2F") return true;
            t = t.parent;
        }
        return false;
    }

    static string ShelfOf(Transform t)
    {
        while (t != null)
        {
            if (t.name.StartsWith("Shelf")) return Chain(t.gameObject);
            t = t.parent;
        }
        return "";
    }

    /// <summary>层号:一层书架实测 y 约 0.52 / 1.42 / 2.30。</summary>
    static int LevelBucket(float y)
    {
        if (y < 0.95f) return 0;
        if (y < 1.85f) return 1;
        return 2;
    }

    static void CheckTextMaterials(StringBuilder sb, List<GameObject> all)
    {
        var lit = new List<string>();
        var missing = new List<string>();
        foreach (var t in all.SelectMany(g => g.GetComponents<TextMesh>()))
        {
            var mr = t.GetComponent<MeshRenderer>();
            if (mr == null || mr.sharedMaterial == null) { missing.Add(Chain(t.gameObject)); continue; }
            string shName = mr.sharedMaterial.shader.name;
            if (!shName.Contains("Text Shader") && !shName.Contains("GUI"))
                lit.Add(Chain(t.gameObject) + " 材质=" + mr.sharedMaterial.name + " shader=" + shName);
        }
        Report(sb, lit.Count == 0, "受光照的 TextMesh 材质(远处/暗处会看不清)", lit.Count, lit.Take(kSample).ToList());
        Report(sb, missing.Count == 0, "TextMesh 无渲染器或无材质", missing.Count, missing.Take(kSample).ToList());
    }

    static void CheckBookCollisions(StringBuilder sb, List<GameObject> all)
    {
        var cols = all.Where(g => g.activeInHierarchy && g.GetComponent<LibraryBook>() != null)
                      .SelectMany(g => g.GetComponents<Collider>())
                      .Where(c => c is BoxCollider)
                      .ToList();
        var hits = new List<string>();
        int n = 0;
        for (int i = 0; i < cols.Count; i++)
            for (int j = i + 1; j < cols.Count; j++)
            {
                var a = cols[i].bounds; var b = cols[j].bounds;
                float vol = OverlapVolume(a, b);
                if (vol > 0.00015f)
                {
                    n++;
                    if (hits.Count < kSample) hits.Add(cols[i].gameObject.name + " ↔ " + cols[j].gameObject.name + " 重叠体积 " + vol.ToString("F5"));
                }
            }
        Report(sb, n == 0, "书架书本碰撞体互相重叠(共 " + cols.Count + " 个盒)", n, hits);
    }

    static void CheckTeleport(StringBuilder sb, List<GameObject> all)
    {
        var areas = all.SelectMany(g => g.GetComponents<TeleportationArea>()).ToList();
        var anchors = all.SelectMany(g => g.GetComponents<TeleportationAnchor>()).ToList();
        Report(sb, areas.Count > 0, "TeleportationArea 数量", areas.Count == 0 ? 1 : 0,
               areas.Count == 0 ? new List<string> { "没有可传送地面" } : null);
        var on2f = anchors.Where(a => a.transform.position.y > 2.5f).Select(a => Chain(a.gameObject)).ToList();
        Line(sb, "  · 锚点共 " + anchors.Count + " 个,其中在二层(y>2.5)的 " + on2f.Count + " 个"
             + (on2f.Count == 0 ? " ← 与遗留问题#2 一致:二层只能靠检索传送" : " "));

        var origins = all.SelectMany(g => g.GetComponents<Unity.XR.CoreUtils.XROrigin>()).ToList();
        var off = new List<string>();
        foreach (var o in origins)
        {
            var t = o.transform;
            if (t.localPosition.sqrMagnitude > 0.0004f || Quaternion.Angle(t.localRotation, Quaternion.identity) > 0.5f)
                off.Add(Chain(o.gameObject) + " 局部 pos=" + Fmt(t.localPosition) + " rot=" + Fmt(t.localEulerAngles));
        }
        Report(sb, off.Count == 0, "XR Origin 未归位(坑 #6,出生点会漂)", off.Count, off.Take(kSample).ToList());
        foreach (var o in origins)
            Line(sb, "  · Origin " + Chain(o.gameObject) + " pos=" + Fmt(o.transform.position));
    }

    static void CheckInactiveInteractables(StringBuilder sb, List<GameObject> all)
    {
        var hits = new List<string>();
        foreach (var g in all)
        {
            if (g.activeSelf) continue;
            string kind = null;
            foreach (var c in g.GetComponents<Component>())
            {
                if (c == null) continue;
                string tn = c.GetType().Name;
                if (tn == "XRGrabInteractable" || tn == "UIButtonInteraction"
                    || tn == "Button" || tn == "Toggle") { kind = tn; break; }
            }
            if (kind == null) continue;
            {
                // 对象池元素(父节点叫 Items)平时就是关着的,由代码逐条点亮 —— 不算异常
                var par = g.transform.parent;
                if (par != null && par.name == "Items") continue;
                // 整条父链都关着 = 设计如此的对象池/面板;自己关着但父链开着 = 可疑
                bool parentActive = par != null && par.gameObject.activeInHierarchy;
                if (parentActive) hits.Add(Chain(g) + " (activeSelf=false 但父级是开的) " + kind);
            }
        }
        Report(sb, hits.Count == 0, "自身关闭、父级打开的可交互物(可能是漏开的面板)", hits.Count, hits.Take(kSample).ToList());
    }

    // ── 小工具 ──────────────────────────────────────────────────────

    static float OverlapVolume(Bounds a, Bounds b)
    {
        var i = Vector3.Min(a.max, b.max) - Vector3.Max(a.min, b.min);
        return (i.x > 0 && i.y > 0 && i.z > 0) ? i.x * i.y * i.z : 0f;
    }
    static T FindDeep<T>(Transform root, string name) where T : Component
    {
        foreach (var t in root.GetComponentsInChildren<T>(true))
            if (t.gameObject.name == name) return t;
        return null;
    }
    static int Count<T>(List<GameObject> all) where T : Component
    {
        return all.Sum(g => g.GetComponents<T>().Length);
    }
    static bool IsNaN(Vector3 v) { return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z); }
    static string Fmt(Vector3 v) { return "(" + v.x.ToString("F3") + "," + v.y.ToString("F3") + "," + v.z.ToString("F3") + ")"; }
    static string tb(string x) { return (x ?? "").Replace("\n", "").Replace("\r", ""); }
    static string Qu(string x) { return "「" + (x.Length > 22 ? x.Substring(0, 22) + "…" : x) + "」"; }
    static string Chain(GameObject go)
    {
        var s = go.name; var p = go.transform.parent;
        while (p != null) { s = p.name + "/" + s; p = p.parent; }
        return s;
    }
    static void Line(StringBuilder sb, string x) { sb.AppendLine(x); }
    static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine("■ " + title);
    }
    static void Report(StringBuilder sb, bool ok, string what, int count, List<string> samples)
    {
        sb.AppendLine((ok ? "  [OK]   " : "  [!!]   ") + what + (ok && count == 0 ? "" : " = " + count));
        if (ok || samples == null) return;
        foreach (var x in samples) sb.AppendLine("         - " + x);
    }
}
