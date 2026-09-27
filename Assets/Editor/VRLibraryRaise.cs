// VRLibraryRaise.cs — 整馆拔高(用户 2026-09-27 定案:一层净高 8.00 m、二层净高 3.60 m)
// 本轮只有"测"(只报告):把场上东西按"底部 y"分层、列出要拉伸的竖向构件(墙/柱/杆/井)、
// 量楼梯的级数/踢面/踏面/足迹,再把目标标高与 Δ 一次算清。
// 为什么先测:场景是手摆 + 生成器已分叉(遗留 13),凭空写 Δ 会把吊顶/须知屏/露台/廊道搞丢。
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class VRLibraryRaise
{
    const string kRoot = "SmartLibrary";
    const string kReport = "Logs/raise-audit.txt";

    // 现测基线(来自 Logs/floor2-audit.txt、terrace-setup.txt、冒烟)
    const float FloorTop = 0.028f;      // 一层地砖顶
    const float Slab2F = 3.005f;        // 二层板面顶
    const float Ceil = 5.525f;          // 吊顶灯槽最低面
    const float TargetL1 = 8.00f;       // 目标:一层净高
    const float TargetL2 = 3.60f;       // 目标:二层净高

    static StringBuilder sb;
    static int bad;

    [MenuItem("VRLibrary/拔高·现状测量(只报告)")]
    public static void Survey()
    {
        sb = new StringBuilder();
        bad = 0;
        sb.AppendLine("整馆拔高·现状测量(只读)  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var root = GameObject.Find(kRoot);
        if (root == null) { sb.AppendLine("  !! 找不到场景根 " + kRoot); Flush(); return; }

        float newCeil = FloorTop + TargetL1;
        float newSlab = newCeil - TargetL2;
        sb.AppendLine(string.Format("  目标:一层净高 {0:0.00} m、二层净高 {1:0.00} m", TargetL1, TargetL2));
        sb.AppendLine(string.Format("  ⇒ 吊顶最低面 {0:0.000} → {1:0.000}(Δ{2:+0.000;-0.000})", Ceil, newCeil, newCeil - Ceil));
        sb.AppendLine(string.Format("  ⇒ 二层板面顶 {0:0.000} → {1:0.000}(Δ{2:+0.000;-0.000})", Slab2F, newSlab, newSlab - Slab2F));
        sb.AppendLine(string.Format("  ⇒ 二层净高 {0:0.000} → {1:0.000};一层净高 {2:0.000} → {3:0.000}",
            Ceil - Slab2F, TargetL2, Ceil - FloorTop, TargetL1));
        sb.AppendLine();

        // ── 1) 按"底部 y"分层
        string[] bandOrder = { "一层(底<2.60)", "二层(2.60~5.00)", "吊顶层(5.00~6.50)", "更高(≥6.50)" };
        var bands = new Dictionary<string, List<Transform>>();
        foreach (var b in bandOrder) bands[b] = new List<Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root.transform) continue;
            if (t.GetComponent<Renderer>() == null && t.GetComponent<Collider>() == null) continue;
            float bottom = World(t).min.y;
            string k = bottom < 2.60f ? bandOrder[0] : bottom < 5.00f ? bandOrder[1] : bottom < 6.50f ? bandOrder[2] : bandOrder[3];
            bands[k].Add(t);
        }
        sb.AppendLine("  ── 分层(只统计带 Renderer/Collider 的节点) ──");
        foreach (var k in bandOrder)
        {
            var list = bands[k];
            float lo = 999f, hi = -999f, top = -999f;
            foreach (var t in list)
            {
                var b = World(t);
                lo = Mathf.Min(lo, b.min.y); hi = Mathf.Max(hi, b.min.y); top = Mathf.Max(top, b.max.y);
            }
            sb.AppendLine(string.Format("    {0,-16} {1,5} 个   底 y {2:0.000}~{3:0.000}   最高顶 y {4:0.000}",
                k, list.Count, list.Count == 0 ? 0f : lo, list.Count == 0 ? 0f : hi, list.Count == 0 ? 0f : top));
        }
        sb.AppendLine();

        // ── 2) 竖向构件:底部在一层、顶 > 4.50 m 的(墙/柱/杆/井)要"拉伸"而不是整体搬
        sb.AppendLine("  ── 竖向构件(底 < 2.60 且 顶 > 4.50,拔高时按比例拉伸到新吊顶) ──");
        var stretch = new List<Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.GetComponent<Renderer>() == null) continue;
            var b = World(t);
            float h = b.size.y;
            if (h < 1.0f) continue;
            if (b.max.y > 4.50f && b.min.y < 2.60f) stretch.Add(t);
        }
        stretch.Sort((a, b) => World(b).max.y.CompareTo(World(a).max.y));
        int n = 0;
        foreach (var t in stretch)
        {
            if (n++ >= 40) { sb.AppendLine("    …(还有 " + (stretch.Count - 40) + " 件)"); break; }
            var b = World(t);
            sb.AppendLine(string.Format("    {0,-40} 底 {1:0.000} 顶 {2:0.000} 高 {3:0.000}  路径 {4}",
                t.name, b.min.y, b.max.y, b.size.y, Path(t)));
        }
        sb.AppendLine();

        // ── 3) 楼梯
        sb.AppendLine("  ── 楼梯 ──");
        var steps = new List<Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("Stair")) steps.Add(t);
        if (steps.Count == 0) { sb.AppendLine("    !! 没找到 Stair* 节点"); bad++; }
        else
        {
            float minx = 999f, maxx = -999f, minz = 999f, maxz = -999f;
            var tops = new List<float>();
            foreach (var t in steps)
            {
                var b = World(t);
                minx = Mathf.Min(minx, b.min.x); maxx = Mathf.Max(maxx, b.max.x);
                minz = Mathf.Min(minz, b.min.z); maxz = Mathf.Max(maxz, b.max.z);
                tops.Add(b.max.y);
            }
            tops.Sort();
            float riser = tops.Count > 1 ? (tops[tops.Count - 1] - tops[0]) / (tops.Count - 1) : 0f;
            float run = maxz - minz;
            float tread = tops.Count > 1 ? run / (tops.Count - 1) : 0f;
            sb.AppendLine(string.Format("    {0} 块  足迹 x[{1:0.00},{2:0.00}] z[{3:0.00},{4:0.00}]  最高踏面 y {5:0.000}",
                steps.Count, minx, maxx, minz, maxz, tops[tops.Count - 1]));
            sb.AppendLine(string.Format("    踢面 {0:0.000} m   踏面(进深) {1:0.000} m   总进深 {2:0.00} m", riser, tread, run));
            float need = newSlab - tops[tops.Count - 1];
            sb.AppendLine(string.Format("    接到新板面 {0:0.000} 还差 {1:0.000} m ⇒ 按现踢面还需 {2} 级;按 0.180 踢面需 {3} 级(×0.28 踏面 = {4:0.00} m 进深)",
                newSlab, need, riser > 0f ? Mathf.CeilToInt(need / riser) : 0, Mathf.CeilToInt(need / 0.18f), 0.28f * Mathf.CeilToInt(need / 0.18f)));
            if (tread < 0.24f) { sb.AppendLine("    !! 踏面 < 0.24 m:CharacterController 很容易卡在踏步棱上(玩家卡住大概率就是这儿)"); bad++; }
            if (riser > 0.20f) { sb.AppendLine("    !! 踢面 > 0.20 m:偏陡,建议 ≤0.18"); bad++; }
        }
        sb.AppendLine();
        sb.AppendLine("  ── 未过 " + bad + " 项 ──");
        Flush();
        Debug.Log("[Raise] 测量 → Logs/raise-audit.txt(未过 " + bad + ")");
    }

    /// <summary>
    /// 分组账:按「场景根下的第一段」分组,打每组的件数与该组的 y 区间。
    /// 拔高的分类必须建立在这张账上 —— 吊顶 36 块 / 六块须知屏 / 露台整片 / 玻璃廊道 / 空气墙 / 吧台 / 楼梯指示牌
    /// 全场**没有生成代码**(遗留 13),只能靠"名字 + y 区间"认出它们属于哪一层。
    /// </summary>
    [MenuItem("VRLibrary/拔高·分组账(只报告)")]
    public static void Ledger()
    {
        sb = new StringBuilder();
        bad = 0;
        sb.AppendLine("拔高·分组账(只读)  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("  现基线:一层地砖顶 0.028 / 二层板面顶 3.005 / 吊顶最低面 5.525");
        sb.AppendLine("  目标(一层 8.00、二层 3.60)⇒ 吊顶层 +2.503、二层组 +1.423");
        sb.AppendLine();
        var root = GameObject.Find(kRoot);
        if (root == null) { sb.AppendLine("  !! 找不到 " + kRoot); Flush("Logs/raise-groups.txt"); return; }

        var keys = new List<string>();
        var counts = new Dictionary<string, int>();
        var mins = new Dictionary<string, float>();
        var maxs = new Dictionary<string, float>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root.transform) continue;
            if (t.GetComponent<Renderer>() == null && t.GetComponent<Collider>() == null) continue;
            string key = TopKey(t);
            var b = World(t);
            if (!counts.ContainsKey(key)) { keys.Add(key); counts[key] = 0; mins[key] = 999f; maxs[key] = -999f; }
            counts[key]++;
            mins[key] = Mathf.Min(mins[key], b.min.y);
            maxs[key] = Mathf.Max(maxs[key], b.max.y);
        }
        keys.Sort((a, b) => maxs[b].CompareTo(maxs[a]));
        sb.AppendLine("  ── 按「场景根下第一段」分组(顶 y 从高到低) ──");
        foreach (var k in keys)
            sb.AppendLine(string.Format("    {0,-32} {1,5} 件   底 {2,8:0.000}   顶 {3,8:0.000}   跨度 {4,6:0.00}",
                k, counts[k], mins[k], maxs[k], maxs[k] - mins[k]));
        sb.AppendLine();
        sb.AppendLine("  判读口径:顶 > 5.30 ⇒ 跟吊顶走(+2.503);底 ≥ 2.60 且顶 < 5.30 ⇒ 跟二层走(+1.423);");
        sb.AppendLine("            底 < 2.60 而顶 > 4.50 ⇒ 竖构件(拉伸到新吊顶,不是整体搬);其余不动。");
        sb.AppendLine("            楼梯必须**重建**(现 13 级、踢面 0.221、踏面 0.164 ⇒ 接不到 4.428,且踏面太窄会卡人)。");
        Flush("Logs/raise-groups.txt");
        Debug.Log("[Raise] 分组账 → Logs/raise-groups.txt");
    }

    /// <summary>取"场景根下第一段"的名字(如 Mezzanine / Walls / TerraceDeck)。</summary>
    static string TopKey(Transform t)
    {
        var p = t;
        while (p.parent != null && p.parent.name != kRoot) p = p.parent;
        return p.name;
    }

    /// <summary>
    /// 逐件账:把混层的大组按「底 y 升序」逐件打出来 —— 这些组里混着 1F 装饰、2F 结构、
    /// 吊顶之上的东西(实测 WallDecor 0.100~7.136、Mezzanine 0.000~5.670),不逐件看清就动会墙裂/露台掉。
    /// </summary>
    [MenuItem("VRLibrary/拔高·混层组逐件账(只报告)")]
    public static void Detail()
    {
        sb = new StringBuilder();
        bad = 0;
        sb.AppendLine("拔高·混层组逐件账(只读)  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var root = GameObject.Find(kRoot);
        if (root == null) { sb.AppendLine("  !! 找不到 " + kRoot); Flush("Logs/raise-detail.txt"); return; }
        string[] groups = { "WallDecor", "Mezzanine", "Polish1", "Walls", "StarryTerrace" };
        foreach (var g in groups)
        {
            var list = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != g) continue;
                foreach (var c in t.GetComponentsInChildren<Transform>(true))
                {
                    if (c == t) continue;
                    if (c.GetComponent<Renderer>() == null && c.GetComponent<Collider>() == null) continue;
                    list.Add(c);
                }
            }
            list.Sort((a, b) => World(a).min.y.CompareTo(World(b).min.y));
            sb.AppendLine();
            sb.AppendLine("  ── " + g + "(" + list.Count + " 件,按底 y 升序;只列前 60) ──");
            int n = 0;
            foreach (var c in list)
            {
                if (n++ >= 60) { sb.AppendLine("    …(还有 " + (list.Count - 60) + " 件)"); break; }
                var b = World(c);
                sb.AppendLine(string.Format("    {0,-32} 底 {1,8:0.000} 顶 {2,8:0.000} 高 {3,6:0.00}  路径 {4}",
                    c.name, b.min.y, b.max.y, b.size.y, Path(c)));
            }
        }
        Flush("Logs/raise-detail.txt");
        Debug.Log("[Raise] 逐件账 → Logs/raise-detail.txt");
    }

    /// <summary>
    /// 执行拔高(会改场景并自动保存)。规则见 Logs/raise-groups.txt 与 raise-detail.txt 定稿的表:
    ///  +2.503 跟吊顶(Ceiling 36 块 / ArchBeam / WallDecor 的 Rivet / 其余底 ≥5.00 的件)
    ///  +1.423 跟二层(底 ≥2.40 的件:二层板栏板 GlassCorridor Fascia Shelves2F Lecterns Pad_2F_* StairSign WallArt 露台结构)
    ///  拉伸到新吊顶 Walls/AirWall/SignRod/HoloBeam/CornerCol/EntryPillar;拉伸到新板底 Mezzanine 的一层支撑柱
    ///  不动:玩家 rig、一层书架与书、灯条六档、服务台/中控台本体、地面三层、墙面装饰板、一层锚点
    ///  重建(本轮不动):Mezzanine/Stair_* 与 StairGlow*(现 10 级、踢面 0.288、顶 2.875,接不到新板面)
    /// ⚠ 父子都有渲染器时只处理祖先,否则会被搬两次。
    /// </summary>
    [MenuItem("VRLibrary/拔高·执行(改场景并自动保存)")]
    public static void Raise()
    {
        sb = new StringBuilder();
        bad = 0;
        sb.AppendLine("整馆拔高  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.path.EndsWith("SmartLibrary.unity")) { Debug.LogError("[Raise] 当前场景不是 SmartLibrary:" + scene.path); return; }
        var root = GameObject.Find(kRoot);
        if (root == null) { sb.AppendLine("  !! 找不到场景根 " + kRoot); Flush(); return; }

        float newCeil = FloorTop + TargetL1;      // 8.028
        float newSlab = newCeil - TargetL2;       // 4.428
        float dCeil = newCeil - Ceil;             // +2.503
        float d2F = newSlab - Slab2F;             // +1.423
        sb.AppendLine(string.Format("  目标:吊顶最低面 {0:0.000}、二层板面顶 {1:0.000}(Δ {2:+0.000;-0.000} / {3:+0.000;-0.000})",
            newCeil, newSlab, dCeil, d2F));

        // 幂等:已经是目标标高就什么都不做
        float ceilNow = 999f;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == "Ceiling" || (t.parent != null && t.parent.name == "Ceiling" && t.GetComponent<Renderer>() != null))
                ceilNow = Mathf.Min(ceilNow, World(t).min.y);
        if (Mathf.Abs(ceilNow - newCeil) < 0.02f)
        {
            sb.AppendLine("  吊顶已经在 " + ceilNow.ToString("0.000") + " → 已经是目标标高,本轮不改");
            Flush();
            return;
        }

        Snapshot(scene.path);

        var done = new HashSet<Transform>();
        int m2 = 0, mc = 0, st = 0, skip = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root.transform) continue;
            if (t.GetComponent<Renderer>() == null && t.GetComponent<Collider>() == null) continue;
            bool anc = false;
            for (var p = t.parent; p != null && p != root.transform; p = p.parent)
                if (done.Contains(p)) { anc = true; break; }
            if (anc) continue;                                  // 祖先搬过了,别再搬一次

            string path = Path(t);
            if (path.StartsWith("XR Interaction Setup")) { skip++; continue; }        // 玩家 rig 绝不搬
            if (path.StartsWith("Mezzanine/Stair")) { skip++; continue; }             // 楼梯留给重建
            if (path.StartsWith("Mezzanine/StairGlow")) { skip++; continue; }

            // 竖构件:够高(≥1 m)才拉伸;薄饰件(压条 Trim_*、柱环 MezzColRing_*)只平移到新顶 ——
            // 实测第一版把 Trim 拉了 ×63.8、把柱环拉了 ×17.7(它们本来只是"贴在某物顶端"的一条),
            // 判据:拉伸倍率 > 4 一律不拉伸,改成平移(否则细件会被拉成一堵高墙)。
            if (IsStretchToCeiling(path))
            {
                var bp = World(t);
                if (bp.size.y >= 1.0f && (newCeil - bp.min.y) / bp.size.y <= 4f) StretchTo(t, newCeil);
                else MoveY(t, newCeil - bp.max.y);
                st++; done.Add(t); continue;
            }
            if (path.StartsWith("Mezzanine/Col") || path.StartsWith("Mezzanine/MezzCol"))
            {
                float top = newSlab - 0.10f;
                var bp = World(t);
                if (bp.size.y >= 1.0f && (top - bp.min.y) / bp.size.y <= 4f) StretchTo(t, top);
                else MoveY(t, top - bp.max.y);
                st++; done.Add(t); continue;
            }

            var b = World(t);
            if (path.Contains("Rivet") || path.StartsWith("Polish1/ArchBeam") || b.min.y >= 5.00f)
            { MoveY(t, dCeil); mc++; done.Add(t); continue; }
            if (b.min.y >= 2.40f)
            { MoveY(t, d2F); m2++; done.Add(t); continue; }
            skip++;
        }

        Physics.SyncTransforms();
        sb.AppendLine(string.Format("  搬动:跟吊顶 {0} 件 / 跟二层 {1} 件;拉伸 {2} 件;不动 {3} 件", mc, m2, st, skip));

        // ── 自证复核
        sb.AppendLine("  ── 复核 ──");
        float c = 999f, pad = 999f, sh = 999f;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "Pad_2F_N_E") pad = Mathf.Min(pad, World(t).min.y);
            if (t.parent != null && t.parent.name == "Shelves2F") sh = Mathf.Min(sh, World(t).min.y);
            if (t.GetComponent<Renderer>() != null && t.name.StartsWith("Coffer")) c = Mathf.Min(c, World(t).min.y);
        }
        sb.AppendLine(string.Format("    吊顶灯槽底 {0:0.000}(目标 {1:0.000})  二层锚点底 {2:0.000}(原 3.033)  二层书架底 {3:0.000}(原 3.000)",
            c, newCeil, pad, sh));
        BadIf(Mathf.Abs(c - newCeil) > 0.05f, "吊顶灯槽底 = " + newCeil.ToString("0.000") + "±0.05");
        BadIf(Mathf.Abs(pad - (newSlab + 0.028f)) > 0.05f, "二层锚点底 = 新板面顶 + 0.028");
        float l1 = c - FloorTop, l2 = c - (pad - 0.028f);
        sb.AppendLine(string.Format("    ⇒ 一层净高 {0:0.00} m(目标 {1:0.00})  二层净高 {2:0.00} m(目标 {3:0.00})", l1, TargetL1, l2, TargetL2));
        BadIf(Mathf.Abs(l1 - TargetL1) > 0.06f || Mathf.Abs(l2 - TargetL2) > 0.06f, "两层净高都到目标");
        sb.AppendLine("    未过 " + bad + " 项");

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        sb.AppendLine("  保存场景:" + (saved ? "成功" : "!! 失败") + " → " + scene.path);
        sb.AppendLine("  回退:copy Logs/SmartLibrary.pre-raise.unity → Assets/_Scenes/SmartLibrary.unity 后 openScene");
        Flush();
        Debug.Log("[Raise] 拔高完成,未过 " + bad + " 项 → Logs/raise-audit.txt");
    }

    static bool IsStretchToCeiling(string path)
    {
        return path.StartsWith("ServiceDesk/SignRod")
            || path.StartsWith("CentralKiosk/HoloBeam")
            || path.StartsWith("Polish1/CornerCol_")
            || path.StartsWith("SciLights/EntryPillar")
            || path.StartsWith("AirWall")
            || path.StartsWith("Walls/");
    }

    static void MoveY(Transform t, float dy)
    {
        var p = t.position; p.y += dy; t.position = p;
    }

    /// <summary>底面钉住、顶面推到目标标高(缩放绕 pivot,所以缩完要复量再把底面按回去)。</summary>
    static void StretchTo(Transform t, float targetTop)
    {
        var b = World(t);
        float h = b.size.y;
        if (h < 1e-3f) return;
        float k = (targetTop - b.min.y) / h;
        var ls = t.localScale; ls.y *= k; t.localScale = ls;
        Physics.SyncTransforms();
        var b2 = World(t);
        var p = t.position; p.y += b.min.y - b2.min.y; t.position = p;
        Physics.SyncTransforms();
        sb.AppendLine(string.Format("    拉伸 {0,-34} 顶 {1:0.000} → {2:0.000}(×{3:0.000})",
            Path(t), b.max.y, World(t).max.y, k));
    }

    static void Snapshot(string scenePath)
    {
        string dst = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "Logs/SmartLibrary.pre-raise.unity");
        if (File.Exists(dst)) { sb.AppendLine("  改前快照已存在,不覆盖:" + dst); return; }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
        string src = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), scenePath);
        File.WriteAllBytes(dst, File.ReadAllBytes(src));
        sb.AppendLine("  改前快照:" + dst + "(" + new FileInfo(dst).Length.ToString("N0") + " B)");
    }

    static void BadIf(bool cond, string what)
    {
        if (cond) { bad++; sb.AppendLine("  !! " + what); }
        else sb.AppendLine("  ✓ " + what);
    }

    /// <summary>世界包围盒:优先 Renderer,其次 Collider。</summary>
    static Bounds World(Transform t)
    {
        var r = t.GetComponent<Renderer>();
        if (r != null) return r.bounds;
        var c = t.GetComponent<Collider>();
        if (c != null) return c.bounds;
        return new Bounds(t.position, Vector3.zero);
    }

    static string Path(Transform t)
    {
        string s = t.name;
        while (t.parent != null && t.parent.name != kRoot) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }

    static void Flush() { Flush(kReport); }

    static void Flush(string rel)
    {
        string full = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
        File.WriteAllText(full, sb.ToString(), Encoding.UTF8);
    }
}