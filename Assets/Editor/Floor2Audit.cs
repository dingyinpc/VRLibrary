#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 二楼回廊通行性体检(只报告,不改场景)。回答四件事:
///   ① 走道板有几块、各在哪;
///   ② 每块板**能不能走通**:沿长边切一串断面,数"这个断面还有没有一条 ≥0.70 m 的连续空档",
///      给出可过断面比例 + 最长一段连续堵死的长度(连续堵死 > 0.5 m 才算真断);
///   ③ 板与板搭接没有(缝 > 阈值 = 走过去踩空);
///   ④ 把 AirWalls、所有传送锚点的碰撞体、楼梯上口摊开列出来
///      —— 隐形墙和实心传送垫是两类最典型的"看不见但堵路"。
/// 判据:板面顶 y 从场上 Pad_2F_* 锚点反推(不再写死);顶面高出板面 0.35 m 以上才算挡
/// (压条跨得过去);断面量在板面上方 1.10 m(胸口),人按 ±0.35 m 宽算。
/// 前两版踩过的坑(别再犯):把 0.1 m 宽的边灯 MezzGlow_* 当成地板;把地板自己/外墙/栏杆当障碍,
/// 于是九块板全报"净宽 0.00 m"。**墙与栏杆是走道的边界,不是障碍** —— 所以判据是"断面上有没有
/// 一条过得去的空口",不是"最窄处多宽"。
/// 报告写 Logs/floor2-audit.txt。
/// </summary>
public static class Floor2Audit
{
    const string kScene = "Assets/_Scenes/SmartLibrary.unity";
    const string kLog = "Logs/floor2-audit.txt";

    /// <summary>二层板面顶兜底值(拔高 4.423 之后)。平时由场上锚点反推。</summary>
    const float DeckTopFallback = 4.423f;

    static float _deckTop = -1f;

    /// <summary>二层板面顶:由 Pad_2F_N_E 反推(锚点 = 板面 + 0.045),找不到才用兜底。</summary>
    static float DeckTop
    {
        get
        {
            if (_deckTop > 0f) return _deckTop;
            var a2f = GameObject.Find("SmartLibrary/Pad_2F_N_E");
            if (a2f == null) a2f = GameObject.Find("Pad_2F_N_E");
            if (a2f != null) { _deckTop = a2f.transform.position.y - 0.045f; return _deckTop; }
            Debug.LogWarning("[Floor2Audit] 找不到二层锚点,回退兜底 DeckTop=" + DeckTopFallback);
            _deckTop = DeckTopFallback;
            return _deckTop;
        }
    }

    const float Knee = 0.35f;
    const float Chest = 1.10f;
    const float Head = 1.65f;
    const float GapTolerance = 0.06f;
    const float MinFreeWidth = 0.70f;     // 单断面至少要这么宽才过得去
    const float BlockRunReport = 0.5f;    // 连续堵死超过这么长才算"真断了"
    const float Step = 0.10f;

    struct Slab { public string name; public float x0, z0, x1, z1; public bool horizontal; }

    class Obstacle
    {
        public string name;
        public float x0, z0, x1, z1, y0, y1;
        public int parts;
        public float Area { get { return (x1 - x0) * (z1 - z0); } }
        public float Across(Slab s)       // 在横断方向上占掉多少米
        {
            return s.horizontal ? Mathf.Max(0f, Mathf.Min(z1, s.z1) - Mathf.Max(z0, s.z0))
                                : Mathf.Max(0f, Mathf.Min(x1, s.x1) - Mathf.Max(x0, s.x0));
        }
    }

    [MenuItem("VRLibrary/二楼通行性体检(只报告)")]
    public static void Run()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != kScene) { Debug.LogError("[二楼体检] 当前场景不是 SmartLibrary:" + scene.path); return; }
        Physics.SyncTransforms();

        var sb = new StringBuilder();
        sb.AppendLine("二楼回廊通行性体检  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(string.Format("判据:板面顶 {0:0.000};挡 = 顶面高出板面 {1:0.00} m 且底面低于 {2:0.00} m;"
            + "断面量在板面上方 {3:0.00} m 这一层,把挡物投到横断轴上取**实际空档**;"
            + "空档 ≥ {4:0.00} m(肩宽)算过得去;连续堵死 > {5:0.00} m 才算真断",
            DeckTop, Knee, DeckTop + Head, Chest, MinFreeWidth, BlockRunReport));

        var slabs = FindSlabs(out var slabNames);
        if (slabs.Count == 0) { sb.AppendLine("!! 一块板都没找到 → 判据不对,别信这份报告"); Write(sb); return; }
        sb.AppendLine(string.Format("  [① 走道板] {0} 块:", slabs.Count));
        foreach (var s in slabs)
            sb.AppendLine(string.Format("     {0,-28} x[{1:0.00},{2:0.00}] z[{3:0.00},{4:0.00}] 深 {5:0.00} m 长 {6:0.00} m",
                s.name, s.x0, s.x1, s.z0, s.z1, s.horizontal ? s.z1 - s.z0 : s.x1 - s.x0,
                s.horizontal ? s.x1 - s.x0 : s.z1 - s.z0));

        var obs = FindObstacles(slabs, slabNames);
        sb.AppendLine(string.Format("  [通行带上的物体] {0} 个(排除地板/触发体/玩家)", obs.Count));

        int cut = 0;
        foreach (var s in slabs)
        {
            float lo = s.horizontal ? s.x0 : s.z0, hi = s.horizontal ? s.x1 : s.z1;
            float cLo = s.horizontal ? s.z0 : s.x0, cHi = s.horizontal ? s.z1 : s.x1;
            int samples = 0, ok = 0, run = 0, worstRun = 0, worstAt = 0;
            for (int k = 0; ; k++)
            {
                float t = lo + k * Step;
                if (t > hi) break;
                samples++;
                if (LargestGap(s, t, cLo, cHi, obs) >= MinFreeWidth) { ok++; run = 0; }
                else { run++; if (run > worstRun) { worstRun = run; worstAt = k; } }
            }
            float runLen = worstRun * Step;
            bool bad = runLen > BlockRunReport;
            if (bad) cut++;
            sb.AppendLine(string.Format("  [② 能不能走] {0,-28} 可过断面 {1}/{2}({3:0%}) 最长连续堵死 {4:0.00} m {5}",
                s.name, ok, samples, samples == 0 ? 0f : (float)ok / samples, runLen, bad ? "!! 断了" : "✓"));
            if (worstRun > 0)
            {
                float t = lo + worstAt * Step;
                var on = OnSection(s, t, obs);
                sb.AppendLine(string.Format("        堵死段中心 {0}{1:0.00}(板边到板边 {2:0.00} m),压在该断面上的物体 {3} 个:",
                    s.horizontal ? "x=" : "z=", t, cHi - cLo, on.Count));
                for (int i = 0; i < on.Count && i < 4; i++)
                {
                    var o = on[i];
                    sb.AppendLine(string.Format("        占 {0:0.00} m ← {1} ({2} 块) y[{3:0.00},{4:0.00}] x[{5:0.00},{6:0.00}] z[{7:0.00},{8:0.00}]",
                        o.Across(s), o.name, o.parts, o.y0, o.y1, o.x0, o.x1, o.z0, o.z1));
                }
            }
        }

        sb.AppendLine("  [③ 板间连通](两条板的矩形挨到 ≤ " + GapTolerance.ToString("0.00")
            + " m 算能走过去;露台板只靠传送到达,不参与)");
        int sn = slabs.Count;
        var par = new int[sn];
        for (int a = 0; a < sn; a++) par[a] = a;
        System.Func<int, int> find = a => { while (par[a] != a) a = par[a]; return a; };
        int gaps = 0;
        for (int a = 0; a < sn; a++)
            for (int b = a + 1; b < sn; b++)
            {
                if (slabs[a].name.Contains("Terrace") || slabs[b].name.Contains("Terrace")) continue;
                if (RectGap(slabs[a], slabs[b]) <= GapTolerance) par[find(a)] = find(b);
            }
        int comps = 0;
        // 露台板天生谁也不接(只能传过去),算进分量就永远是"2 个分量",真断了也看不出差别
        for (int a = 0; a < sn; a++)
            if (find(a) == a && !slabs[a].name.Contains("Terrace")) comps++;
        // 缝只有一种要报:两块板**不在同一分量**里 —— 那才是"跨过去踩空"。
        // 挨得近但绕得到的(廊桥经挑台才接到环板)是设计,报出来只会把真断点埋了。
        for (int a = 0; a < sn; a++)
            for (int b = a + 1; b < sn; b++)
            {
                if (slabs[a].name.Contains("Terrace") || slabs[b].name.Contains("Terrace")) continue;
                if (find(a) == find(b)) continue;
                float g = RectGap(slabs[a], slabs[b]);
                if (g > 6f) continue;
                gaps++;
                sb.AppendLine(string.Format("     !! {0} ↔ {1} 缝 {2:0.00} m,且两者之间没有任何板接上 → 走过去踩空",
                    slabs[a].name, slabs[b].name, g));
            }
        sb.AppendLine(string.Format("     连通分量 {0} 个({1})/ 跨分量近缝 {2} 处", comps,
            comps == 1 ? "全部板走得通" : "!! 有走不到的板", gaps));

        DumpGroup(sb, "④ 隐形墙", "AirWalls");
        DumpPads(sb, "⑤ 传送锚点碰撞体");
        DumpStairs(sb, slabs, obs);
        DumpAim(sb);

        sb.AppendLine(string.Format("== 汇总:走道板 {0} 块 / 走不通的 {1} 块 / 板间断缝 {2} 处 / 通行带物体 {3} 个 ==",
            slabs.Count, cut, gaps, obs.Count));
        Write(sb);
    }

    // ------------------------------------------------------------ 找东西

    static List<Slab> FindSlabs(out HashSet<string> names)
    {
        var slabs = new List<Slab>();
        names = new HashSet<string>();
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger) continue;
            var b = c.bounds;
            if (b.max.y < DeckTop - 0.05f || b.max.y > DeckTop + 0.05f) continue;
            if (b.size.x < 1f || b.size.z < 1f) continue;          // 0.1 m 宽的边灯不是地板
            slabs.Add(new Slab
            {
                name = Node(c.transform),
                x0 = b.min.x, z0 = b.min.z, x1 = b.max.x, z1 = b.max.z,
                horizontal = b.size.x >= b.size.z,
            });
            names.Add(c.gameObject.name);
        }
        return slabs;
    }

    static List<Obstacle> FindObstacles(List<Slab> slabs, HashSet<string> slabNames)
    {
        var byPath = new Dictionary<string, Obstacle>();
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger) continue;
            if (slabNames.Contains(c.gameObject.name)) continue;   // 地板自己
            var b = c.bounds;
            if (b.max.y - DeckTop < Knee) continue;
            if (b.min.y > DeckTop + Head) continue;
            if (IsPlayer(c)) continue;
            bool overAny = false;
            foreach (var s in slabs)
                if (b.max.x > s.x0 && b.min.x < s.x1 && b.max.z > s.z0 && b.min.z < s.z1) { overAny = true; break; }
            if (!overAny) continue;

            string key = Node(c.transform);
            Obstacle o;
            if (!byPath.TryGetValue(key, out o))
            {
                o = new Obstacle { name = key, x0 = b.min.x, z0 = b.min.z, x1 = b.max.x, z1 = b.max.z,
                                   y0 = b.min.y, y1 = b.max.y };
                byPath[key] = o;
            }
            o.parts++;
            o.x0 = Mathf.Min(o.x0, b.min.x); o.z0 = Mathf.Min(o.z0, b.min.z);
            o.x1 = Mathf.Max(o.x1, b.max.x); o.z1 = Mathf.Max(o.z1, b.max.z);
            o.y0 = Mathf.Min(o.y0, b.min.y); o.y1 = Mathf.Max(o.y1, b.max.y);
        }
        var list = new List<Obstacle>(byPath.Values);
        list.Sort((a, b) => b.Area.CompareTo(a.Area));              // 占地大的先说
        return list;
    }

    /// <summary>
    /// 这个断面上最大的**实际空档**有多宽:把压在此断面上的物体投到横断轴上成区间,
    /// 排序后取相邻区间(含板边)之间的最大间隙。
    /// 之前按"人中心 ±0.35 m"采样,等于两侧各扣一次体宽 → 要 1.4 m 才判得过,
    /// 把 0.9 m 的可通行段误报成堵死。肩宽 0.70 m 才是该跟空档比的数。
    /// </summary>
    static float LargestGap(Slab s, float t, float cLo, float cHi, List<Obstacle> obs)
    {
        float y = DeckTop + Chest;
        var iv = new List<Vector2>();
        foreach (var o in obs)
        {
            if (o.y1 <= y - 0.55f || o.y0 >= y + 0.55f) continue;
            float a = s.horizontal ? o.z0 : o.x0;
            float b = s.horizontal ? o.z1 : o.x1;
            float along0 = s.horizontal ? o.x0 : o.z0;
            float along1 = s.horizontal ? o.x1 : o.z1;
            if (t < along0 || t > along1) continue;
            a = Mathf.Max(a, cLo); b = Mathf.Min(b, cHi);
            if (b > a) iv.Add(new Vector2(a, b));
        }
        iv.Sort((p, q) => p.x.CompareTo(q.x));
        float best = 0f, cur = cLo;
        for (int i = 0; i < iv.Count; i++)
        {
            if (iv[i].x - cur > best) best = iv[i].x - cur;
            cur = Mathf.Max(cur, iv[i].y);
        }
        if (cHi - cur > best) best = cHi - cur;
        return best;
    }

    /// <summary>压在这个断面上的物体(横断方向占位从大到小)。</summary>
    static List<Obstacle> OnSection(Slab s, float t, List<Obstacle> obs)
    {
        float y = DeckTop + Chest;
        var on = obs.FindAll(o => o.y1 > y - 0.55f && o.y0 < y + 0.55f
            && (s.horizontal ? (t >= o.x0 && t <= o.x1) : (t >= o.z0 && t <= o.z1)));
        on.Sort((a, b) => b.Across(s).CompareTo(a.Across(s)));
        return on;
    }

    static float RectGap(Slab a, Slab b)
    {
        float dx = Mathf.Max(0f, Mathf.Max(a.x0 - b.x1, b.x0 - a.x1));
        float dz = Mathf.Max(0f, Mathf.Max(a.z0 - b.z1, b.z0 - a.z1));
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ------------------------------------------------------------ 专项清单

    static void DumpGroup(StringBuilder sb, string tag, string groupName)
    {
        var g = GameObject.Find("SmartLibrary/" + groupName);
        sb.AppendLine("  [" + tag + "] " + groupName + (g == null ? " !! 场上没有这一组" : ":"));
        if (g == null) return;
        foreach (var c in g.GetComponentsInChildren<Collider>(true))
        {
            var b = c.bounds;
            bool hits2F = b.max.y - DeckTop >= Knee && b.min.y <= DeckTop + Head;
            sb.AppendLine(string.Format("     {0,-24} {1} y[{2:0.00},{3:0.00}] x[{4:0.00},{5:0.00}] z[{6:0.00},{7:0.00}] {8}",
                Node(c.transform), c.GetType().Name, b.min.y, b.max.y, b.min.x, b.max.x, b.min.z, b.max.z,
                hits2F ? "!! 压在二楼通行带上" : "✓ 不碰二楼"));
        }
    }

    static void DumpPads(StringBuilder sb, string tag)
    {
        sb.AppendLine("  [" + tag + "]");
        int solid = 0, n = 0;
        foreach (var a in Object.FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor>())
        {
            foreach (var c in a.GetComponents<Collider>())
            {
                n++;
                var b = c.bounds;
                bool blocks = !c.isTrigger && b.max.y - DeckTop >= Knee && b.min.y <= DeckTop + Head;
                if (blocks) solid++;
                sb.AppendLine(string.Format("     {0,-24} {1} y[{2:0.00},{3:0.00}] 高 {4:0.00} m trigger={5} {6}",
                    Node(c.transform), c.GetType().Name, b.min.y, b.max.y, b.max.y - b.min.y, c.isTrigger,
                    blocks ? "!! 实心挡路" : ""));
            }
        }
        sb.AppendLine(string.Format("     共 {0} 个锚点碰撞体,其中压在二楼通行带上的实心体 {1} 个", n, solid));
    }

    static void DumpStairs(StringBuilder sb, List<Slab> slabs, List<Obstacle> obs)
    {
        // 楼梯是 Stair_0..Stair_N 一叠台阶,而 Pad_2F_Stair 只是它旁边的垫子 —— 别按名字头一个匹配
        float x0 = 999f, z0 = 999f, x1 = -999f, z1 = -999f, top = -1f, topX = 0f, topZ = 0f;
        string parent = null;
        int steps = 0;
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger) continue;
            string path = Node(c.transform);
            if (!path.Contains("Stair") || c.gameObject.name.StartsWith("Pad_")) continue;
            var b = c.bounds;
            x0 = Mathf.Min(x0, b.min.x); z0 = Mathf.Min(z0, b.min.z);
            x1 = Mathf.Max(x1, b.max.x); z1 = Mathf.Max(z1, b.max.z);
            steps++;
            if (parent == null) parent = path;
            if (b.max.y > top)
            {
                top = b.max.y;
                topX = (b.min.x + b.max.x) * 0.5f; topZ = (b.min.z + b.max.z) * 0.5f;
            }
        }
        if (steps == 0) { sb.AppendLine("  [⑥ 楼梯] !! 场上找不到名字带 Stair 且带碰撞体的台阶"); return; }
        sb.AppendLine(string.Format("  [⑥ 楼梯] {0} 等 {1} 块台阶 x[{2:0.00},{3:0.00}] z[{4:0.00},{5:0.00}] 顶面 y 到 {6:0.00}",
            parent, steps, x0, x1, z0, z1, top));
        int land = -1;
        for (int i = 0; i < slabs.Count; i++)
        {
            var s = slabs[i];
            if (topX > s.x0 - 0.5f && topX < s.x1 + 0.5f && topZ > s.z0 - 0.5f && topZ < s.z1 + 0.5f) { land = i; break; }
        }
        sb.AppendLine("        上口 (" + topX.ToString("0.00") + "," + topZ.ToString("0.00") + ") → "
            + (land < 0 ? "!! 0.5 m 内没有走道板,上不去" : "落在 " + slabs[land].name));
        if (land < 0) return;
        var sl = slabs[land];
        float t2 = sl.horizontal ? Mathf.Clamp(topX, sl.x0, sl.x1) : Mathf.Clamp(topZ, sl.z0, sl.z1);
        float free = LargestGap(sl, t2, sl.horizontal ? sl.z0 : sl.x0, sl.horizontal ? sl.z1 : sl.x1, obs);
        sb.AppendLine(string.Format("        落点断面最宽空档 {0:0.00} m {1}", free, free < MinFreeWidth ? "!! 上来就堵" : "✓"));
    }

    /// <summary>
    /// 瞄准可达性:垫子碰撞体改扁之后,传送射线还打不打得中 —— 打不中是"静默失效",
    /// 比堵路更难查(人只觉得"怎么传不过去")。所以这里真打射线:垂直 + 45° 斜射,
    /// 各打盘心与 60% 半径处,并要求命中的第一个物体就是这个锚点自己。
    /// </summary>
    static void DumpAim(StringBuilder sb)
    {
        sb.AppendLine("  [⑦ 传送瞄准可达性](垂直 / 45° 斜射 × 盘心 / 60% 半径)");
        var buf = new RaycastHit[8];
        int bad = 0, n = 0;
        foreach (var a in Object.FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor>())
        {
            var t = a.transform;
            var col = a.GetComponent<Collider>();
            if (col == null) { sb.AppendLine("     !! " + Node(t) + " 身上没有碰撞体 → 根本打不中"); bad++; continue; }
            float r = Mathf.Max(col.bounds.extents.x, col.bounds.extents.z);
            int miss = 0;
            var lines = new StringBuilder();
            for (int k = 0; k < 4; k++)
            {
                float radial = (k % 2 == 0) ? 0f : r * 0.6f;
                Vector3 aim = t.position + new Vector3(radial, 0f, 0f);
                Vector3 from = k < 2
                    ? aim + Vector3.up * 2f
                    : aim + new Vector3(1.2f, 1.2f, 0f);
                Vector3 dir = (aim - from).normalized;
                int hits = Physics.RaycastNonAlloc(from, dir, buf, 6f, ~0, QueryTriggerInteraction.Ignore);
                string first = hits > 0 ? Node(buf[0].collider.transform) : "(什么都没打到)";
                bool self = false;
                for (int i = 0; i < hits; i++) if (buf[i].collider == col) { self = true; break; }
                if (!self) miss++;
                lines.Append(" " + (k < 2 ? "垂" : "斜") + (k % 2 == 0 ? "心" : "边") + (self ? "✓" : "✗" + first));
            }
            n++;
            if (miss > 0) bad++;
            sb.AppendLine(string.Format("     {0,-24} 碰撞体高 {1:0.00} m 半径 {2:0.00} m 漏 {3}/4 {4}{5}",
                Node(t), col.bounds.size.y, r, miss, miss == 0 ? "✓" : "!! 打不中", lines.ToString()));
        }
        sb.AppendLine(string.Format("     共 {0} 个锚点,{1} 个存在打不中的射线{2}", n, bad, bad == 0 ? " ✓" : " !!"));
    }

    static bool IsPlayer(Collider c)
    {
        for (Transform t = c.transform; t != null; t = t.parent)
        {
            string n = t.name;
            if (n.Contains("XR Origin") || n.Contains("Trampoline") || n.Contains("Camera")
                || n.Contains("Controller") || n.Contains("Player")) return true;
        }
        return false;
    }

    static string Node(Transform t)
    {
        string s = t.name;
        for (var p = t.parent; p != null && p.name != "SmartLibrary"; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    static void Write(StringBuilder sb)
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(kLog, sb.ToString());
        Debug.Log("[二楼体检] 报告已写 " + kLog + "\n" + sb);
    }
}
#endif
