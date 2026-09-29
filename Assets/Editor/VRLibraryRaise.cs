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
            // ── 「吊挂件」判据(2026-09-30 修)────────────────────────────────
            // 旧判据只看"底 y ≥ 5.00 就跟吊顶",于是**挂在吊顶、但垂得低的灯**被算成"跟二层"只加了 1.423:
            // 旧几何里这种灯垂到 3.70 m(一层地面之上 3.67 m,谁都走得过去,所以没人察觉),
            // 拔高后只到 5.12 m,而二层板面已经抬到 4.428 ⇒ 灯底离二层地面只剩 **0.70 m**,
            // 挂在二层**膝盖高度**上 —— 用户报的"灯这种悬空的东西没一并拔高、会导致人卡住"就是它
            // (其中带碰撞体的那几个真挡人;三盏塑料吊灯没有碰撞体,挡不住人但视觉上就悬在那儿,
            //  所以"高度不够"的观感也是它造成的)。
            // 新判据换成**看它挂在谁身上**:
            //   · 顶**贴在吊顶面上**(吊顶 −0.40 ~ 吊顶 +0.70)且底不贴任何一层地面 ⇒ 挂在吊顶上 ⇒ 跟吊顶走
            //     ⚠ 上界很重要:第一版只写"顶 ≥ 吊顶 −0.20",结果把**穿过吊顶的结构**也抓进来了 ——
            //       实测露台屏的两根立柱 ScreenPylon_L/R(底 2.85、顶 6.45)被误判成吊挂件;
            //       它们是**从露台地板上长出来的柱子**,必须跟二层走。
            //   · 或者名字本身就是吊挂件(Pendant/LampCord/LampRing…)且底 ≥ 2.40(排除桌上台灯)
            bool hang = (b.max.y >= Ceil - 0.40f && b.max.y <= Ceil + 0.70f
                         && Mathf.Abs(b.min.y - Slab2F) > 0.15f
                         && Mathf.Abs(b.min.y - FloorTop) > 0.15f)
                        || SuspendedFixture(t.name, b.min.y);
            bool oldRule = path.Contains("Rivet") || path.StartsWith("Polish1/ArchBeam") || b.min.y >= 5.00f;
            if (oldRule || hang)
            {
                if (hang && !oldRule)
                    sb.AppendLine(string.Format("    改判(旧规则会跟二层):{0,-42} 底 {1:0.000} 顶 {2:0.000} 高 {3:5:0.00} → 跟吊顶 +{4:0.000}",
                        path, b.min.y, b.max.y, b.size.y, dCeil));
                MoveY(t, dCeil); mc++; done.Add(t); continue;
            }
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
        sb.AppendLine("  回退:copy Logs/SmartLibrary.pre-raise-" + System.DateTime.Now.ToString("yyyyMMdd")
                      + ".unity → " + scene.path + " 后 openScene(⚠ 别用更早那份带日期的快照,会把中间改动一起抹掉)");
        sb.AppendLine("  ⚠ 拔高只改场景文件、且**不会自己进提交**:做完请确认提交,否则任何一次 `git checkout HEAD -- 场景` 都会把它抹掉");
        sb.AppendLine("     (2026-09-30 实测:09-27 那次拔高就是这么丢的 —— 报告写着保存成功,HEAD 里却从来没有过)");
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

    /// <summary>名字本身就是"吊挂件"(兜底:有些吊灯的顶够不到吊顶,中间还隔着一段线)。
    /// b.minY 门槛用来排除桌上的台灯(那是在一层桌面上,拔高时不该动)。</summary>
    static bool SuspendedFixture(string name, float minY)
    {
        if (minY < 2.40f) return false;
        string s = name.ToLower();
        return s.Contains("pendant") || s.Contains("chandelier") || s.Contains("lustre")
            || s.Contains("hanging") || s.Contains("lampcord") || s.Contains("lampring")
            || s.Contains("ceilinglamp") || s.Contains("downlight");
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

    [MenuItem("VRLibrary/拔高·回退到今天的改前快照(改场景)")]
    public static void RevertToday()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.path.EndsWith("SmartLibrary.unity")) { Debug.LogError("[Raise] 当前场景不是 SmartLibrary:" + scene.path); return; }
        string src = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath),
            "Logs/SmartLibrary.pre-raise-" + System.DateTime.Now.ToString("yyyyMMdd") + ".unity");
        if (!File.Exists(src)) { Debug.LogError("[Raise] 找不到今天的改前快照:" + src); return; }
        if (scene.isDirty) { Debug.LogError("[Raise] 场景有未保存改动,先保存/手动处理,别在这里悄悄丢"); return; }

        string abs = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), scene.path);
        System.IO.File.Copy(src, abs, true);
        AssetDatabase.Refresh();
        // 文件换了还得**重新载入**,否则内存里那份还是改过的 —— 这一步漏了就会"回退了却看不出变化"
        EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
        Debug.Log("[Raise] 已从 " + src + " 回退并重新载入场景");
    }

    static void Snapshot(string scenePath)
    {
        // ⚠ 2026-09-30:旧写法固定叫 `pre-raise.unity`,于是 09-27 那份一直留着不被覆盖 ——
        // 而拿一份**过期快照**回退会把中间所有改动(遮挡标记、书本、阅读面板布局…)一起抹掉。
        // 改成按日期落盘,回退目标永远等于"这一次动手之前"。
        string dst = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath),
            "Logs/SmartLibrary.pre-raise-" + System.DateTime.Now.ToString("yyyyMMdd") + ".unity");
        if (File.Exists(dst)) { sb.AppendLine("  今天的改前快照已存在,不覆盖:" + dst); return; }
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

    // ════════════════════════════════════════════════════════════════
    // 净高体检(2026-09-29 用户报"一楼二楼的高度还是不够 + 人会被卡住")
    //
    // 用户原话:"你没有将所有的东西一并向上去拔高,比如说灯啊,这种悬空的东西,这样子会导致人卡住。"
    //
    // 上一轮(Raise())的分桶判据是**物体包围盒的底 y**:
    //     底 ≥ 5.00 → 跟吊顶 +2.503;  底 ≥ 2.40 → 跟二层 +1.423;  其余不动
    // 于是**挂在吊顶上、但垂得比较低的灯**(底 y 落在 2.40~5.00)**被当成"跟二层"只加了 1.423**。
    // 旧几何里这种灯垂到 4.0 m(一层地面之上 3.97 m,谁都走得过去,没人察觉);
    // 拔高后它只到 5.42 m,而二层板面已经抬到 **4.428** ⇒ 灯底离二层地面只剩 **0.99 m**,
    // 二层上走动的人**头直接撞灯、被卡住**:这也正是用户感觉"二楼高度不够"的来源 ——
    // 不是板面不够高,是**该跟吊顶走的悬空件没跟上去**。
    //
    // 所以这里换判据:**不看"底在哪",看"它会不会站在人的头高带里"** ——
    //   ① 只认带**实体 Collider**的(纯装饰挡不住人,汇报时单列);
    //   ② 底落在 [层面 + 0.40, 层面 + Head] ⇒ 悬在半空又低于头顶 = 挡人;
    //   ③ 底落在 [层面 + 0.02, 层面 + 0.40] ⇒ 落地障碍(家具/书架,本来就该有,只计数)。
    // ════════════════════════════════════════════════════════════════

    const float Head = 2.10f;          // 头顶净空门槛:低于它就会挡人(眼睛 1.6~1.7 + 头显/头发余量)

    [MenuItem("VRLibrary/拔高·净高体检(只报告)")]
    public static void Clearance()
    {
        sb = new StringBuilder();
        bad = 0;
        sb.AppendLine("拔高·净高体检(只读)  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var root = GameObject.Find(kRoot);
        if (root == null) { sb.AppendLine("  !! 找不到 " + kRoot); Flush(kClear); return; }

        var lv = Levels(root);
        sb.AppendLine(string.Format("  量到的标高:一层地砖顶 {0:0.000} / 二层板面顶 {1:0.000} / 吊顶最低面 {2:0.000}",
            lv.x, lv.y, lv.z));
        sb.AppendLine(string.Format("  ⇒ 结构净高:一层(吊顶下) {0:0.00} m、二层 {1:0.00} m;二层板下的一层净空 {2:0.00} m",
            lv.z - lv.x, lv.z - lv.y, lv.y - lv.x));
        sb.AppendLine();

        string[] names = { "一层", "二层" };
        float[] fl = { lv.x, lv.y };
        for (int i = 0; i < 2; i++)
        {
            float f = fl[i];
            sb.AppendLine("  ── " + names[i] + "(地面 " + f.ToString("0.000") + ") ──");
            var solid = new List<Transform>();       // 实体碰撞体
            var ghost = new List<Transform>();       // 只有渲染器/触发器的同高度件
            float low = 999f; float lowFloor = 999f;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var col = t.GetComponent<Collider>();
                var ren = t.GetComponent<Renderer>();
                if (col == null && ren == null) continue;
                var b = World(t);
                if (b.size.y < 0.05f) continue;
                float bot = b.min.y;
                if (bot < f + 0.02f || bot > f + Head) continue;
                if (bot <= f + 0.40f) { lowFloor = Mathf.Min(lowFloor, b.max.y); continue; }   // 落地家具
                if (col != null && !col.isTrigger) { solid.Add(t); low = Mathf.Min(low, bot); }
                else ghost.Add(t);
            }
            sb.AppendLine(string.Format("    落地障碍(底 ≤ 地面+0.40)最高顶 {0:0.000} —— 家具/书架,本来就该有",
                lowFloor == 999f ? 0f : lowFloor));
            sb.AppendLine(string.Format("    悬空挡人(实体碰撞体,底 地面+0.40 ~ 地面+{0:0.0})  {1} 件   最低底 {2:0.000} ⇒ **有效净高 {3:0.00} m**",
                Head, solid.Count, low == 999f ? 0f : low, low == 999f ? lv.z - f : low - f));
            sb.AppendLine(string.Format("    同类但无实体碰撞体(纯装饰,挡不住人) {0} 件", ghost.Count));
            if (low != 999f && low - f < Head - 0.05f) bad++;
            solid.Sort((a, b) => World(a).min.y.CompareTo(World(b).min.y));
            int n = 0;
            foreach (var t in solid)
            {
                if (n++ >= 40) { sb.AppendLine("      …(还有 " + (solid.Count - 40) + " 件)"); break; }
                var b = World(t);
                var c = t.GetComponent<Collider>();
                sb.AppendLine(string.Format("      {0,-30} 底 {1,7:0.000} 顶 {2,7:0.000} 高 {3,5:0.00}  离地面 {4,6:0.00}  {5}  xy({6:0.0},{7:0.0})  路径 {8}",
                    t.name, b.min.y, b.max.y, b.size.y, b.min.y - f, Lamp(t.name) ? "【灯/发光件】" : "",
                    b.center.x, b.center.z, Path(t)));
            }
            if (ghost.Count > 0)
            {
                n = 0;
                sb.AppendLine("    ── 无碰撞体的同高度件(给灯高做参考) ──");
                ghost.Sort((a, b) => World(a).min.y.CompareTo(World(b).min.y));
                foreach (var t in ghost)
                {
                    if (n++ >= 20) { sb.AppendLine("      …(还有 " + (ghost.Count - 20) + " 件)"); break; }
                    var b = World(t);
                    sb.AppendLine(string.Format("      {0,-30} 底 {1,7:0.000} 顶 {2,7:0.000}  离地面 {3,6:0.00}  路径 {4}",
                        t.name, b.min.y, b.max.y, b.min.y - f, Path(t)));
                }
            }
            sb.AppendLine();
        }

        // 全场"灯/发光件"清单:看它们各挂在什么高度,是判断"该跟吊顶走还是该跟二层走"的直接依据
        sb.AppendLine("  ── 全场灯/发光件(名字命中关键字的,按底 y 升序) ──");
        var lamps = new List<Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!Lamp(t.name)) continue;
            if (t.GetComponent<Renderer>() == null && t.GetComponent<Collider>() == null) continue;
            lamps.Add(t);
        }
        lamps.Sort((a, b) => World(a).min.y.CompareTo(World(b).min.y));
        int m = 0;
        foreach (var t in lamps)
        {
            if (m++ >= 60) { sb.AppendLine("    …(还有 " + (lamps.Count - 60) + " 件)"); break; }
            var b = World(t);
            var col = t.GetComponent<Collider>();
            sb.AppendLine(string.Format("    {0,-30} 底 {1,7:0.000} 顶 {2,7:0.000} 高 {3,5:0.00}  {4}  路径 {5}",
                t.name, b.min.y, b.max.y, b.size.y, col != null && !col.isTrigger ? "有碰撞体" : "无实体碰撞体", Path(t)));
        }
        sb.AppendLine();
        sb.AppendLine("  ── 未过 " + bad + " 项(每一项 = 某一层的有效净高低于 " + Head.ToString("0.0") + " m) ──");
        Flush(kClear);
        Debug.Log("[Raise] 净高体检 → " + kClear + "(未过 " + bad + ")");
    }

    const string kClear = "Logs/raise-clearance.txt";

    /// <summary>量三个标高:一层地砖顶 / 二层板面顶 / 吊顶最低面。
    /// 用可站立锚点(Pad_*)反算层面 —— 直接扫 Floor_Detailed 的子件会取到 y=0 的地面片,量出来是 0.000(踩过这个坑)。</summary>
    static Vector3 Levels(GameObject root)
    {
        float l1 = 999f, l2 = -999f, ceil = 999f;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.parent != null && t.parent.name == kRoot && t.name.StartsWith("Pad_"))
            {
                float top = World(t).max.y;
                if (top < 1.0f) l1 = Mathf.Min(l1, top);
                else if (top < 4.5f) l2 = Mathf.Max(l2, top);     // 二层锚点顶
            }
            if (t.name.StartsWith("Coffer") || (t.parent != null && t.parent.name == "Ceiling"))
                ceil = Mathf.Min(ceil, World(t).min.y);
        }
        if (l2 > 0f) l2 -= 0.052f;      // 二层锚点是 0.024 厚、架在板面 +0.028 上的贴片 ⇒ 反算板面顶
        return new Vector3(l1 == 999f ? 0.028f : l1, l2, ceil);
    }

    /// <summary>名字像"灯/发光件"吗(只用于报告里打标,不参与任何搬动判定)。</summary>
    static bool Lamp(string name)
    {
        string s = name.ToLower();
        return s.Contains("light") || s.Contains("lamp") || s.Contains("pendant") || s.Contains("lustre")
            || s.Contains("chandelier") || s.Contains("coffer") || s.Contains("glow") || s.Contains("neon")
            || s.Contains("holo") || s.Contains("strip") || s.Contains("bar");
    }

    // ════════════════════════════════════════════════════════════════
    // 卡人点体检:把"人会不会卡住"从主观感受变成可量的数
    //
    // 用户原话:"你没有将所有的东西一并向上去拔高,比如说灯啊,这种悬空的东西,这样子会导致人卡住。"
    // 上面那份"按高度列清单"的体检有个致命缺点:**它不知道人站哪儿** ——
    // 书架里的书、墙上的画都在"头高带"里,但它们根本不在人走的地方,全是假警报。
    //
    // 所以这里换成物理判据:在**每个可站立锚点(Pad_*)四周 3 m 的网格上**,
    // 按玩家胶囊(半径 0.30 m、身高 1.80 m)真做一次 `Physics.OverlapCapsule` ——
    // 有东西插进胶囊就是"这里站不住"。再按"是不是悬空(底离地 ≥0.45 m)"分成两类:
    //   ① **悬空挡人** = 人走得到、又悬在头高带里的东西(灯/吊挂件)—— 这条是本次要清零的;
    //   ② 落地障碍 = 桌子/书架,本来就该有,只计数。
    // ════════════════════════════════════════════════════════════════

    const float BodyR = 0.30f;         // 玩家胶囊半径
    const float BodyH = 1.80f;         // 玩家身高(胶囊顶 = 地面 + 身高)
    const float GridStep = 0.5f;       // 采样步长
    const float FootHalf = 21f;        // 采样范围(整馆 ±21 m)

    const string kStuck = "Logs/raise-stuck.txt";

    /// <summary>家具/结构类前缀:它们伸进头高带是正常的(书架隔板、墙上压条、桌沿)。</summary>
    static readonly string[] Furniture =
    {
        "Bookshelves/", "Shelves2F/", "Walls/", "ReadingZone/", "ServiceDesk/", "Mezzanine/",
        "Polish1/", "CentralKiosk/", "GuidePoles", "Floor", "Ceiling", "WallDecor", "WallArt",
        "StairSign", "Pad_", "AirWall", "StarryTerrace", "ShelfWhiteBar",
    };

    static bool IsFurniture(string path)
    {
        foreach (var f in Furniture) if (path.StartsWith(f)) return true;
        return false;
    }

    [MenuItem("VRLibrary/拔高·卡人点体检(只报告)")]
    public static void StuckProbe()
    {
        sb = new StringBuilder();
        bad = 0;
        sb.AppendLine("拔高·卡人点体检(只读,真做玩家胶囊重叠测试)  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var root = GameObject.Find(kRoot);
        if (root == null) { sb.AppendLine("  !! 找不到 " + kRoot); Flush(kStuck); return; }

        var lv = Levels(root);
        sb.AppendLine(string.Format("  标高:一层地砖顶 {0:0.000} / 二层板面顶 {1:0.000} / 吊顶最低面 {2:0.000}", lv.x, lv.y, lv.z));
        sb.AppendLine(string.Format("  玩家胶囊 半径 {0:0.00} m × 高 {1:0.00} m;全场按 {2:0.0} m 网格扫两层",
            BodyR, BodyH, GridStep));
        sb.AppendLine();
        // ── 地板碰撞到底在哪:向下打射线看命中物(这一条是"脚下有楼板"判据的依据,必须先量清楚)
        sb.AppendLine("  ── 地板碰撞探针(从标高 +2 m 向下打 3 m,列前 2 个命中) ──");
        string[] probes = { "一层·入口(0,10)", "一层·书架(-13,0)", "一层·阅读(10,0)", "一层·服务台(0,-12.5)",
                            "二层·西北(-8,-18.2)", "二层·东(18.2,9.5)" };
        Vector2[] pxy = { new Vector2(0, 10), new Vector2(-13, 0), new Vector2(10, 0), new Vector2(0, -12.5f),
                          new Vector2(-8, -18.2f), new Vector2(18.2f, 9.5f) };
        for (int i = 0; i < probes.Length; i++)
        {
            sb.AppendLine("    " + probes[i] + ":");
            float y0 = i < 4 ? lv.x : lv.y;
            var hits = Physics.RaycastAll(new Vector3(pxy[i].x, y0 + 2.0f, pxy[i].y), Vector3.down, 3.0f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            if (hits.Length == 0) { sb.AppendLine("      !! 向下 3 m 什么都没打到(这一层的碰撞地面不在下方)"); continue; }
            for (int k = 0; k < Mathf.Min(2, hits.Length); k++)
                sb.AppendLine(string.Format("      命中 {0,-34} y {1,7:0.000}  (标高 {2:0.000} 之差 {3:+0.000;-0.000})",
                    Path(hits[k].collider.transform), hits[k].point.y, y0, hits[k].point.y - y0));
        }
        sb.AppendLine();
        sb.AppendLine("  判据(四道):");
        sb.AppendLine("    ① **脚下有楼板** —— 向下打 0.8 m 射线,命中点必须落在本层标高 ±0.15(二层挑空中庭上方没有板);");
        sb.AppendLine("    ② **腿部自由** —— 站立面+0.07~+0.95 被占的点不算「人能站的地方」(这道闸把书架/桌椅/墙自动排除);");
        sb.AppendLine("    ③ **头高带·物理** —— 站立面+0.30~+" + BodyH.ToString("0.00") + " 的胶囊里有**带碰撞体**的东西 ⇒ 真会卡住;");
        sb.AppendLine("    ④ **头高带·视觉** —— 同范围内**不带碰撞体**的渲染件(实测三盏塑料吊灯就是这样)⇒ 挡不住人但看得见,");
        sb.AppendLine("       「高度不够」的观感来自它。③④ 都按归属分:【悬空件】= 非家具前缀(灯/吊挂装饰)← 要清零的是它。");
        sb.AppendLine();

        var buf = new Collider[64];
        float[] floors = { lv.x, lv.y };
        string[] lnames = { "一层", "二层" };
        int grandFloat = 0, grandVis = 0;
        for (int L = 0; L < 2; L++)
        {
            float f = floors[L];
            int standable = 0, floatPts = 0, furnPts = 0, visPts = 0;
            var hitBy = new Dictionary<string, int>();
            var info = new Dictionary<string, Bounds>();
            var at = new Dictionary<string, Vector3>();
            var visBy = new Dictionary<string, int>();

            // 无碰撞体的渲染件也要测:实测那三盏一层吊灯(SciLights/LampCord_*/LampRing_*)压根没有碰撞体,
            // **挡不住人,但视觉上就挂在二层膝盖高度** —— 用户说的"高度不够"正是这种"看得到、穿得过"的东西。
            // 所以先按"底 y 落在本层头高带内"筛出候选,再逐点做 AABB 相交(纯几何,不依赖物理)。
            var cand = new List<Renderer>();
            var candPath = new List<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.GetComponent<Collider>() != null) continue;
                var b = r.bounds;
                if (b.size.y < 0.02f || b.size.x < 0.02f) continue;
                if (b.min.y < f + 0.30f || b.min.y > f + BodyH) continue;
                var pth = Path(r.transform);
                if (pth.StartsWith("XR Interaction Setup")) continue;   // 玩家自己(防眩晕环就挂在相机上,别自报)
                if (IsFurniture(pth)) continue;
                cand.Add(r); candPath.Add(pth);
            }
            sb.AppendLine(string.Format("  ── {0}(站立面 {1:0.000}) ──", lnames[L], f));
            sb.AppendLine(string.Format("    (无碰撞体的悬空渲染件候选 {0} 件,只测非家具前缀的)", cand.Count));

            for (float x = -FootHalf; x <= FootHalf; x += GridStep)
                for (float z = -FootHalf; z <= FootHalf; z += GridStep)
                {
                    // ① 脚下真的有这一层的板吗(二层在挑空中庭上方是没有板的)—— 拿命中点高度认,别把一层书架顶当楼板
                    RaycastHit dh;
                    if (!Physics.Raycast(new Vector3(x, f + 0.60f, z), Vector3.down, out dh, 0.80f, ~0, QueryTriggerInteraction.Ignore))
                        continue;
                    if (Mathf.Abs(dh.point.y - f) > 0.15f) continue;
                    // ② 腿部自由?(注意:胶囊是"圆柱+两端半球",端点要各自再让出一个半径,
                    //    否则球端会扎进地板里,把所有点都判成"站不住" —— 上一版就是这么错的)
                    Vector3 lp0 = new Vector3(x, f + 0.03f + BodyR, z);
                    Vector3 lp1 = new Vector3(x, f + 0.95f - BodyR, z);
                    if (Physics.OverlapCapsuleNonAlloc(lp0, lp1, BodyR, buf, ~0, QueryTriggerInteraction.Ignore) > 0)
                        continue;
                    standable++;
                    // ③ 头高带(物理:有碰撞体的)
                    Vector3 p0 = new Vector3(x, f + BodyR + 0.30f, z);
                    Vector3 p1 = new Vector3(x, f + BodyH - BodyR, z);
                    int n = Physics.OverlapCapsuleNonAlloc(p0, p1, BodyR, buf, ~0, QueryTriggerInteraction.Ignore);
                    bool anyFloat = false, anyFurn = false;
                    for (int i = 0; i < n; i++)
                    {
                        var c = buf[i];
                        if (c == null) continue;
                        var path = Path(c.transform);
                        if (path.StartsWith("XR Interaction Setup")) continue;
                        bool furn = IsFurniture(path);
                        if (furn) { anyFurn = true; continue; }
                        anyFloat = true;
                        int k; hitBy.TryGetValue(path, out k); hitBy[path] = k + 1;
                        info[path] = c.bounds; at[path] = new Vector3(x, f, z);
                    }
                    if (anyFloat) floatPts++;
                    else if (anyFurn) furnPts++;
                    // ④ 头高带(视觉:没有碰撞体但看得见的)
                    float y0 = f + 0.30f, y1 = f + BodyH;
                    bool anyVis = false;
                    for (int i = 0; i < cand.Count; i++)
                    {
                        var b = cand[i].bounds;
                        if (b.max.y < y0 || b.min.y > y1) continue;
                        if (b.max.x < x - BodyR || b.min.x > x + BodyR) continue;
                        if (b.max.z < z - BodyR || b.min.z > z + BodyR) continue;
                        anyVis = true;
                        int k; visBy.TryGetValue(candPath[i], out k); visBy[candPath[i]] = k + 1;
                        if (!info.ContainsKey(candPath[i])) { info[candPath[i]] = b; at[candPath[i]] = new Vector3(x, f, z); }
                    }
                    if (anyVis) visPts++;
                }
            grandFloat += floatPts;
            grandVis += visPts;
            sb.AppendLine(string.Format("    人能站的点 {0} 个(全网格 {1} 点,其余没有楼板或被家具/墙体占住)",
                standable, (int)Mathf.Pow(2 * FootHalf / GridStep + 1, 2)));
            sb.AppendLine(string.Format("    头顶撞东西的点:【悬空件·物理】{0} 个 / 【悬空件·纯视觉无碰撞体】{1} 个 / 仅家具结构 {2} 个",
                floatPts, visPts, furnPts));
            var rank = new List<KeyValuePair<string, int>>(hitBy);
            rank.Sort((a, b) => b.Value.CompareTo(a.Value));
            int m = 0;
            if (rank.Count > 0) sb.AppendLine("    ── 物理命中排行 ──");
            foreach (var kv in rank)
            {
                if (m++ >= 20) { sb.AppendLine("      …(还有 " + (rank.Count - 20) + " 类)"); break; }
                var b = info[kv.Key]; var p = at[kv.Key];
                sb.AppendLine(string.Format("      {0,4} 点  {1,-44} 底 {2,7:0.000} 顶 {3,7:0.000}  离站立面 {4,6:0.00}  {5}  例如 xy({6:0.0},{7:0.0})",
                    kv.Value, kv.Key, b.min.y, b.max.y, b.min.y - f, Lamp(kv.Key) ? "【灯/发光件】" : "", p.x, p.z));
            }
            var vrank = new List<KeyValuePair<string, int>>(visBy);
            vrank.Sort((a, b) => b.Value.CompareTo(a.Value));
            m = 0;
            if (vrank.Count > 0) sb.AppendLine("    ── 纯视觉命中排行(无碰撞体,挡不住人但看得见) ──");
            foreach (var kv in vrank)
            {
                if (m++ >= 20) { sb.AppendLine("      …(还有 " + (vrank.Count - 20) + " 类)"); break; }
                var b = info[kv.Key]; var p = at[kv.Key];
                sb.AppendLine(string.Format("      {0,4} 点  {1,-44} 底 {2,7:0.000} 顶 {3,7:0.000}  离站立面 {4,6:0.00}  {5}  例如 xy({6:0.0},{7:0.0})",
                    kv.Value, kv.Key, b.min.y, b.max.y, b.min.y - f, Lamp(kv.Key) ? "【灯/发光件】" : "", p.x, p.z));
            }
            if (floatPts > 0 || visPts > 0) bad++;
            sb.AppendLine();
        }
        sb.AppendLine("  ── 未过 " + bad + " 项(每一项 = 某一层还有悬空的东西挂在人能站的地方的头高带里) ──");
        sb.AppendLine("  合计:【悬空件·物理】" + grandFloat + " 点 / 【悬空件·纯视觉】" + grandVis + " 点");
        Flush(kStuck);
        Debug.Log("[Raise] 卡人点体检 → " + kStuck + "(物理 " + grandFloat + " / 视觉 " + grandVis + ")");
    }
}