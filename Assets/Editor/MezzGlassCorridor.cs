#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 二楼玻璃廊道改造 · 现状测绘(只报告,不改场景)。
///
/// 为什么要先量:"可走动范围变大"只有两条路 —— 往中庭里扩板,或架一条跨中庭的廊桥。
/// 哪条能走取决于三件实测的事:① 一层有什么东西顶到 2.7 m 以上(扩板的硬下界),
/// ② 中庭天窗/吊顶往下到哪儿(玻璃栏板与雨棚的高度上限),
/// ③ 阅读区/书桌占住中庭哪一块(扩板会把一层采光盖掉多少)。
///
/// 报告写 Logs/mezz-corridor-survey.txt。
/// </summary>
public static class MezzGlassCorridor
{
    const string kScene = "Assets/_Scenes/SmartLibrary.unity";
    const string kLog = "Logs/mezz-corridor-survey.txt";

    const float DeckTop = 3.005f;              // 二楼板面顶(实测)
    const float DeckThk = 0.25f;
    const float DeckBottom = DeckTop - DeckThk;   // 2.755 = 板底,一层的东西不能顶过这条线

    struct Box
    {
        public string name, mat;
        public float x0, y0, z0, x1, y1, z1;
        public int parts;
        public float Area { get { return (x1 - x0) * (z1 - z0); } }
    }

    [MenuItem("VRLibrary/二楼玻璃廊道·现状测绘(只报告)")]
    public static void Survey()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != kScene) { Debug.LogError("[廊道测绘] 当前场景不是 SmartLibrary:" + scene.path); return; }
        Physics.SyncTransforms();

        var sb = new StringBuilder();
        sb.AppendLine("二楼玻璃廊道 · 现状测绘  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(string.Format("基准:二楼板面顶 y={0:0.000},板底 y={1:0.000}(板厚 {2:0.00} m)", DeckTop, DeckBottom, DeckThk));

        var ring = FindRing(out float vn, out float vs, out float ve, out float vw);
        DumpRing(sb, ring, vn, vs, ve, vw);
        DumpRails(sb);
        DumpFixtures(sb);
        DumpUnderDeck(sb, vn, vs, ve, vw);
        DumpHeadroom(sb);
        DumpAnchors(sb, vn, vs, ve, vw);
        TryAreas(sb, vn, vs, ve, vw);
        DumpEmission(sb);

        Write(sb);
    }

    // ------------------------------------------------------------ ① 四环板与中庭

    static List<Box> FindRing(out float vn, out float vs, out float ve, out float vw)
    {
        var ring = new List<Box>();
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger) continue;
            var b = c.bounds;
            if (Mathf.Abs(b.max.y - DeckTop) > 0.05f) continue;
            if (b.size.x < 1f || b.size.z < 1f) continue;
            if (Node(c.transform).Contains("Terrace")) continue;
            ring.Add(Of(c.transform, b, Mat(c)));
        }
        vn = -99f; vs = 99f; ve = 99f; vw = -99f;
        foreach (var s in ring)
        {
            if (s.z1 < 0f) vn = Mathf.Max(vn, s.z1);   // 北环内沿
            if (s.z0 > 0f) vs = Mathf.Min(vs, s.z0);   // 南环内沿
            if (s.x0 > 0f) ve = Mathf.Min(ve, s.x0);   // 东环内沿
            if (s.x1 < 0f) vw = Mathf.Max(vw, s.x1);   // 西环内沿
        }
        return ring;
    }

    static void DumpRing(StringBuilder sb, List<Box> ring, float vn, float vs, float ve, float vw)
    {
        sb.AppendLine("  [① 四环走道板]");
        foreach (var s in ring)
        {
            float depth = Mathf.Min(s.x1 - s.x0, s.z1 - s.z0);
            float len = Mathf.Max(s.x1 - s.x0, s.z1 - s.z0);
            sb.AppendLine(string.Format("     {0,-22} x[{1:0.00},{2:0.00}] z[{3:0.00},{4:0.00}] 深 {5:0.00} × 长 {6:0.00} = {7:0.0} m²  {8}",
                s.name, s.x0, s.x1, s.z0, s.z1, depth, len, depth * len, s.mat));
        }
        sb.AppendLine(string.Format("     中庭空洞 x[{0:0.00},{1:0.00}] z[{2:0.00},{3:0.00}] = {4:0.0} × {5:0.0} m",
            vw, ve, vn, vs, ve - vw, vs - vn));
    }

    // ------------------------------------------------------------ ② 护栏

    static void DumpRails(StringBuilder sb)
    {
        sb.AppendLine("  [② 护栏构成](名字含 Rail)");
        var map = new Dictionary<string, Box>();
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            string p = Node(c.transform);
            if (p.Contains("Rail")) Add(map, c, p);
        }
        foreach (var o in Sorted(map))
            sb.AppendLine(string.Format("     {0,-26} y[{1:0.00},{2:0.00}] x[{3:0.00},{4:0.00}] z[{5:0.00},{6:0.00}] {7} 件",
                o.name, o.y0, o.y1, o.x0, o.x1, o.z0, o.z1, o.parts));
        if (map.Count == 0) sb.AppendLine("     !! 一个 Rail* 都没找到");

        sb.AppendLine("     —— 材质(改造沿用/替换的依据)");
        foreach (var kv in MaterialsMatching(@"Rail|Glass"))
            sb.AppendLine("     " + kv);
    }

    /// <summary>四环中离它最近的那条内沿,再往里推多少米板底就会碰到这个盒子(负=现在就已经在板下)。</summary>
    static float PushMarginTo(Box o, float vn, float vs, float ve, float vw)
    {
        return Mathf.Min(Mathf.Min(ve - o.x1, o.x0 - vw), Mathf.Min(vs - o.z1, o.z0 - vn));
    }

    // ------------------------------------------------------------ ③ 环上的柱与吧台

    static void DumpFixtures(StringBuilder sb)
    {
        sb.AppendLine("  [③ 中庭/环上的柱、吧台、压条](改造要绕开或一起重做)");
        var map = new Dictionary<string, Box>();
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            string p = Node(c.transform);
            if (!Regex.IsMatch(p, @"MezzCol|Col[NSEW]_|CornerCol|BarTop|BarLeg|BarGlow|Trim_|MezzGlow|MezzSign")) continue;
            Add(map, c, p);
        }
        foreach (var o in Sorted(map))
        {
            string where = o.y1 <= DeckBottom + 0.05f ? "板下" : (o.y0 >= DeckTop - 0.05f ? "站在板面上(吃掉走道)" : "竖向跨板");
            sb.AppendLine(string.Format("     {0,-26} y[{1:0.00},{2:0.00}] x[{3:0.00},{4:0.00}] z[{5:0.00},{6:0.00}] {7} {8} 件",
                o.name, o.y0, o.y1, o.x0, o.x1, o.z0, o.z1, where, o.parts));
        }
        if (map.Count == 0) sb.AppendLine("     (无)");
    }

    // ------------------------------------------------------------ ④ 一层顶起物

    static void DumpUnderDeck(StringBuilder sb, float vn, float vs, float ve, float vw)
    {
        sb.AppendLine(string.Format("  [④ 一层顶起物](顶面 > {0:0.00} m 的都会被扩板压到;按顶面从高到低)", DeckBottom));
        var map = new Dictionary<string, Box>();
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger) continue;
            var b = c.bounds;
            if (b.max.y < 2.50f) continue;
            if (b.min.y > DeckTop) continue;                       // 二层自己的东西
            string p = Node(c.transform);
            if (Regex.IsMatch(p, @"Wall|Trim_|Rail|Slab|Pad_|Shelf2F|Stair|Sky|Ceiling|Coffer|Light|Lamp") || IsPlayer(c)) continue;
            Add(map, c, p);
        }
        var list = Sorted(map);
        list.Sort((a, b) => b.y1.CompareTo(a.y1));
        int tall = 0;
        foreach (var o in list)
        {
            bool blocks = o.y1 > DeckBottom;
            if (blocks) tall++;
            sb.AppendLine(string.Format("     {0} {1,-28} y[{2:0.00},{3:0.00}] 占地 x[{4:0.00},{5:0.00}] z[{6:0.00},{7:0.00}] {8:0.0} m² 内沿可推 {9:0.00} m 才压到它",
                blocks ? "!!" : "  ", o.name, o.y0, o.y1, o.x0, o.x1, o.z0, o.z1, o.Area,
                PushMarginTo(o, vn, vs, ve, vw)));
        }
        sb.AppendLine(string.Format("     一层高度 ≥ {0:0.00} m 的物体 {1} 个 —— 扩板推过它们所在的那条线就会撞", DeckBottom, tall));
    }

    // ------------------------------------------------------------ ⑤ 竖向净空

    static void DumpHeadroom(StringBuilder sb)
    {
        sb.AppendLine("  [⑤ 板面往上的最低障碍](玻璃栏板/雨棚的高度上限)");
        float low = 99f; string lowAt = "-";
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger) continue;
            var b = c.bounds;
            if (b.min.y < DeckTop + 0.3f) continue;
            if (Node(c.transform).Contains("Terrace")) continue;
            if (b.min.y < low) { low = b.min.y; lowAt = Node(c.transform); }
        }
        sb.AppendLine(string.Format("     {0} 底 y={1:0.00} → 板面以上净空 {2:0.00} m", lowAt, low, low - DeckTop));

        var map = new Dictionary<string, Box>();
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            string p = Node(r.transform);
            if (!Regex.IsMatch(p, @"Skylight|Coffer|Ceiling|Roof|Trim_")) continue;
            var b = r.bounds;
            if (b.min.y < DeckTop) continue;
            AddR(map, r, p);
        }
        foreach (var o in Sorted(map))
            sb.AppendLine(string.Format("     {0,-26} y[{1:0.00},{2:0.00}] x[{3:0.00},{4:0.00}] z[{5:0.00},{6:0.00}] {7} 件",
                o.name, o.y0, o.y1, o.x0, o.x1, o.z0, o.z1, o.parts));
    }

    // ------------------------------------------------------------ ⑥ 锚点与楼梯

    static void DumpAnchors(StringBuilder sb, float vn, float vs, float ve, float vw)
    {
        sb.AppendLine("  [⑥ 二楼锚点/楼梯上口离各自内沿多远](加宽后必须仍在板上、且射线仍打得到)");
        foreach (var a in Object.FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor>())
        {
            string p = Node(a.transform);
            if (!p.Contains("Pad_2F")) continue;
            var t = a.transform.position;
            float d = Mathf.Abs(t.x) > Mathf.Abs(t.z)
                ? (t.x > 0f ? t.x - ve : t.x - vw)
                : (t.z > 0f ? t.z - vs : t.z - vn);
            sb.AppendLine(string.Format("     {0,-20} @ ({1:0.00},{2:0.00}) 距内沿 {3:0.00} m(正=在板上,负=已悬空)", p, t.x, t.z, d));
        }
        float top = -1f, tx = 0f, tz = 0f; int steps = 0;
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger || c.gameObject.name.StartsWith("Pad_")) continue;
            if (!Node(c.transform).Contains("Stair")) continue;
            steps++;
            if (c.bounds.max.y > top) { top = c.bounds.max.y; tx = c.bounds.center.x; tz = c.bounds.center.z; }
        }
        sb.AppendLine(string.Format("     楼梯 {0} 级,上口 ({1:0.00},{2:0.00}) 顶 y={3:0.00} 距东内沿 {4:0.00} m",
            steps, tx, tz, top, tx - ve));
    }

    // ------------------------------------------------------------ ⑦ 面积试算

    static void TryAreas(StringBuilder sb, float vn, float vs, float ve, float vw)
    {
        float outer = 19.8f;
        sb.AppendLine("  [⑦ 加宽试算]外沿按 ±" + outer.ToString("0.0") + " m;空洞 = 四边内沿各往中庭推 D 米");
        foreach (float d in new[] { 0f, 0.7f, 1.5f, 2.5f, 3.5f, 4.5f })
        {
            // 板往中庭里推 D,空洞就得**缩小** D:西/北边界朝 +、东/南边界朝 −。
            // 上一版符号反了,量出"空洞越来越大、盖掉 -8%",等于算了个不存在的方案。
            float nvw = vw + d, nve = ve - d, nvn = vn + d, nvs = vs - d;
            float voidA = (nve - nvw) * (nvs - nvn);
            float walkA = (2 * outer) * (2 * outer) - voidA;
            sb.AppendLine(string.Format("     D={0:0.0} → 空洞 {1:0.0}×{2:0.0}={3:0.0} m²(盖掉 {4:0%} 的中庭开口) 环面 {5:0.0} m²(+{6:0.0})",
                d, nve - nvw, nvs - nvn, voidA, 1f - voidA / ((ve - vw) * (vs - vn)), walkA,
                walkA - ((2 * outer) * (2 * outer) - (ve - vw) * (vs - vn))));
        }
        sb.AppendLine("     —— 廊桥试算:跨中庭的窄桥,盖掉的中庭开口小、可走面积是净增");
        foreach (float wd in new[] { 1.4f, 1.8f, 2.4f })
        {
            float lenNS = vs - vn, lenEW = ve - vw;
            sb.AppendLine(string.Format("     桥宽 {0:0.0} m → 南北向 长 {1:0.0} m 占顶 {2:0.0} m²({3:0%} 中庭)/ 东西向 长 {4:0.0} m 占顶 {5:0.0} m²({6:0%})",
                wd, lenNS, wd * lenNS, wd * lenNS / ((ve - vw) * (vs - vn)), lenEW, wd * lenEW, wd * lenEW / ((ve - vw) * (vs - vn))));
        }
    }

    // ------------------------------------------------------------ ⑧ 自发光面积账

    /// <summary>
    /// "亮不亮"不能靠肉眼吵。这里把每个 renderer 的**世界空间三角面积**记到它自己的材质上,
    /// 再按"有没有真在发光"(_EMISSION 关键字)分开累加,于是能回答两个问题:
    ///  ① 新加的廊道光带在全馆出光面积里占多大一片(面积才是过曝的成因,自发光值 1.84 vs 1.77 差 4% 不算理由);
    ///  ② 哪些材质"配了发光色但关键字没活" —— 那就是体检里 !! 的现场版,顺带把它的使用者名字打出来。
    /// 多材质槽的物体按 **submesh** 分面积(整块各记一份会把 5 mm 包边算成几百 m²)。
    /// </summary>
    static void DumpEmission(StringBuilder sb)
    {
        var liveArea = new Dictionary<string, float>();     // 真在发光的面积
        var intentArea = new Dictionary<string, float>();    // 配了发光色(含没活的)
        var peak = new Dictionary<string, float>();          // 自发光峰值
        var liveFlag = new Dictionary<string, bool>();
        var deadUsers = new Dictionary<string, List<string>>();
        var bucket = new Dictionary<string, float>();       // 分组 -> 出光面积
        int rend = 0, meshless = 0;

        System.Action<Renderer, Material, float, Dictionary<string, List<string>>> acc =
            (r, m, a, dead) =>
        {
            if (m == null || a <= 0.0001f || !m.HasProperty("_EmissionColor")) return;
            var ec = m.GetColor("_EmissionColor");
            if (ec.maxColorComponent <= 0.001f) return;              // 连发光意图都没有,不计账
            string k = m.name;
            intentArea.TryGetValue(k, out float ia); intentArea[k] = ia + a;
            if (!peak.ContainsKey(k) || peak[k] < ec.maxColorComponent) peak[k] = ec.maxColorComponent;

            bool live = m.IsKeywordEnabled("_EMISSION");
            bool had;
            if (liveFlag.TryGetValue(k, out had)) liveFlag[k] = had && live; else liveFlag[k] = live;
            if (!live)
            {
                List<string> l;
                if (!dead.TryGetValue(k, out l)) { l = new List<string>(); dead[k] = l; }
                if (l.Count < 6) l.Add(Chain(r.transform));
                return;
            }
            liveArea.TryGetValue(k, out float la); liveArea[k] = la + a;
            string b = Bucket(r.gameObject);
            bucket.TryGetValue(b, out float ba); bucket[b] = ba + a;
        };

        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            rend++;
            Mesh msh = null;
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null) msh = mf.sharedMesh;
            else { var smr = r.GetComponent<SkinnedMeshRenderer>(); if (smr != null) msh = smr.sharedMesh; }
            if (msh == null) { meshless++; continue; }
            float a = WorldArea(r, msh);
            if (a <= 0.0001f) { meshless++; continue; }

            var slots = r.sharedMaterials;
            if (slots.Length > 1)
            {
                // 多槽必须按 submesh 分面积:整块面积各记一份会把"地毯四周 5 mm 包边"算成 279 m²(baseline 实测),
                // 那数量级会把真正的光带挤到看不见。
                var verts = msh.vertices;
                for (int s = 0; s < msh.subMeshCount && s < slots.Length; s++)
                    acc(r, slots[s], TriArea(r.transform, verts, msh.GetTriangles(s)), deadUsers);
            }
            else acc(r, slots.Length > 0 ? slots[0] : null, a, deadUsers);
        }

        float total = 0f;
        foreach (var kv in liveArea) total += kv.Value;
        sb.AppendLine(string.Format("  [⑧ 自发光出光面积账] 有材质的 renderer {0} 个(无网格/面积为 0 跳过 {1} 个);全馆在发光的表面积 {2:0.0} m²",
            rend, meshless, total));

        float corr = 0f;
        foreach (var kv in bucket) if (kv.Key == GroupName) corr = kv.Value;
        sb.AppendLine(string.Format("     分组占比:{0}",
            string.Join(" / ", SortedBuckets(bucket, total))));
        if (total > 0f)
            sb.AppendLine(string.Format("     → 玻璃廊道(GlassCorridor)出光 {0:0.0} m²,占全馆 {1:0%};其余 {2:0.0} m²",
                corr, corr / total, total - corr));

        sb.AppendLine("     按材质排行(出光面积 / 自发光峰值;★=配了发光色但关键字没活,场上是黑的):");
        var rows = new List<KeyValuePair<string, float>>(liveArea.Count + deadUsers.Count);
        foreach (var kv in liveArea) rows.Add(kv);
        foreach (var kv in deadUsers) if (!liveArea.ContainsKey(kv.Key))
                rows.Add(new KeyValuePair<string, float>(kv.Key, 0f));
        rows.Sort((x, y) => y.Value.CompareTo(x.Value));
        int shown = 0;
        foreach (var kv in rows)
        {
            string k = kv.Key;
            if (kv.Value < 0.05f && !deadUsers.ContainsKey(k)) continue;
            if (++shown > 14) break;
            float ia; intentArea.TryGetValue(k, out ia);
            bool live; liveFlag.TryGetValue(k, out live);
            float pk; peak.TryGetValue(k, out pk);
            sb.AppendLine(string.Format("       {0}{1,-22} 发光 {2,7:0.0} m² / 意图 {3,7:0.0} m² 峰值 {4:0.00}{5}",
                live ? " " : "★", k, kv.Value, ia, pk,
                live ? "" : " ← 使用者: " + string.Join(", ", deadUsers[k].ToArray())));
        }
        sb.AppendLine("     判据:新加一圈光带之前先比面积,别比自发光值 —— 130 m 连续亮边的观感是**面积**堆出来的。");
    }

    static IEnumerable<string> SortedBuckets(Dictionary<string, float> bucket, float total)
    {
        var l = new List<KeyValuePair<string, float>>(bucket);
        l.Sort((a, b) => b.Value.CompareTo(a.Value));
        for (int i = 0; i < l.Count && i < 6; i++)
            yield return string.Format("{0} {1:0.0} m²({2:0%})", l[i].Key, l[i].Value,
                total > 0f ? l[i].Value / total : 0f);
    }

    static string Bucket(GameObject go)
    {
        string p = Chain(go.transform);
        if (p.Contains("/" + GroupName + "/") || p.Contains("/" + GroupName)) return GroupName;
        var seg = p.Split('/');
        return seg.Length >= 3 ? seg[1] : seg[seg.Length - 1];
    }

    static string Chain(Transform t)
    {
        string s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }

    /// <summary>逐三角换到世界空间求和 —— 非等比缩放和旋转都算得对,比包围盒近似准。</summary>
    static float WorldArea(Renderer r, Mesh m)
    {
        var tri = m.triangles;
        if (tri == null || tri.Length == 0) return 0f;
        if (tri.Length > 30000)   // 细网格直接用包围盒表面积,别把编辑器卡住
        {
            var s = r.bounds.size;
            return 2f * (s.x * s.y + s.y * s.z + s.x * s.z);
        }
        return TriArea(r.transform, m.vertices, tri);
    }

    static float TriArea(Transform t, Vector3[] v, int[] tri)
    {
        if (v == null || tri == null || v.Length == 0) return 0f;
        float sum = 0f;
        for (int i = 0; i + 2 < tri.Length; i += 3)
        {
            int i0 = tri[i], i1 = tri[i + 1], i2 = tri[i + 2];
            if (i0 >= v.Length || i1 >= v.Length || i2 >= v.Length) continue;
            var a = t.TransformPoint(v[i0]);
            var b = t.TransformPoint(v[i1]);
            var c = t.TransformPoint(v[i2]);
            sum += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }
        return sum;
    }

    // ============================================================ 改造
    /// <summary>
    /// 用户诉求是"半开放式玻璃廊道 + 可走动范围变大"。测绘给出的边界:
    ///  · 西侧不能扩 —— 一层 4 排书柜顶到 3.20 m,高出二楼板面 0.20 m,西板内沿只剩 0.11 m 余量;
    ///  · 北侧只敢推 1.00 m —— 服务台两根吊牌立柱(ServiceDesk/SignRodL/R,顶 5.50 m)在 z=-15.71;
    ///  · 四角有通高角柱(Polish1/CornerCol_*,顶 5.40 m)占 |16.13~16.88|,所以挑台只做跨中 ±14 m;
    ///  · 东侧楼梯进口在 z=-11.4~-9.6 留洞(那是二楼唯一的非传送通道,堵了就是"又堵路"回来);
    ///  · 头顶不能加东西 —— 站姿眼高约 4.6 m,吊顶灯槽底 5.52 m,夹出来的空档放不下廊架。
    /// 所以这一版把"玻璃"做在**脚下和外侧**:挑台/廊桥整块用可行走玻璃楼板,栏板换成 1.2 m 无框玻璃,
    /// 外沿挂 0.32 m 发光裙板(中庭和一层都看得见那道亮边),不加任何顶棚。
    /// 新物件全挂在 Mezzanine/GlassCorridor 下,重跑先删这组再建;被替换的旧栏板另列名单。
    /// </summary>
    const string kSnapshot = "Logs/SmartLibrary.pre-corridor.unity";
    const string kBuildLog = "Logs/mezz-corridor-build.txt";
    const string GroupName = "GlassCorridor";

    // 空洞边界:来自 Logs/mezz-corridor-survey.txt 的实测值
    const float VW = -17.90f, VE = 17.20f, VN = -17.20f, VS = 17.20f;
    const float BayN = 1.00f, BayS = 1.20f, BayE = 1.20f;   // 各侧往中庭推的深度(西侧不做)
    const float Span = 14.00f;                               // 挑台只做跨中 ±14 m,避开四角角柱
    const float EastZ0 = -8.00f, EastZ1 = 14.00f;           // 东挑台的 z 范围(下界避开楼梯)
    const float StairPad = 0.12f;          // 楼梯进口洞 = 实测最高那级台阶的 z 跨度再各让这么多
    static float StairA = -11.92f, StairB = -9.55f;         // 运行时按实测台阶改写(Build 开头量)
    // 桥心 x:不在中庭正中(那会是 -0.35)。装饰拱 Polish1/ArchBeam 的拱顶段摊到 x 3.20、底面 y 4.20,
    // 正好压在桥的西侧栏板线上;4.40 − 0.90 = 3.50 才让开拱头约 0.2 m。
    const float BridgeX = 4.40f, BridgeHalf = 0.90f, HallHalf = 2.20f;

    const float DeckThkNew = 0.14f;      // 新玻璃楼板厚
    const float RailH = 1.20f;           // 无框玻璃栏板高(自板面)
    const float BeamH = 0.23f, BeamW = 0.18f;      // 栏板下的金属边梁
    const float FasciaH = 0.32f, FasciaT = 0.10f;  // 发光裙板

    struct Seg { public float x0, z0, x1, z1; public bool rail; public string tag; }

    static StringBuilder rep;
    static GameObject grp;
    static Material matGlass, matMetal, matGlow, matCyan, matPink;
    static int pieces, decks;
    static float newArea;

    [MenuItem("VRLibrary/二楼玻璃廊道·改造(改场景)")]
    public static void Build()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != kScene) { Debug.LogError("[廊道改造] 当前场景不是 SmartLibrary:" + scene.path); return; }

        rep = new StringBuilder();
        rep.AppendLine("二楼玻璃廊道改造  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        pieces = decks = 0;
        newArea = 0f;
        SnapshotOnce();
        Physics.SyncTransforms();
        MeasureStair();

        MakeMaterials();
        ClearOld();

        var mezz = GameObject.Find("SmartLibrary/Mezzanine");
        var g = new GameObject(GroupName);
        g.transform.SetParent(mezz != null ? mezz.transform : null, false);
        grp = g;
        Undo.RegisterCreatedObjectUndo(g, "创建 " + GroupName);

        BuildDecks();
        BuildBalustrade();
        BuildHallTrim();

        rep.AppendLine("  [复核]");
        int bad = Verify();
        rep.AppendLine(string.Format("== 新增 {0} 件(玻璃楼板 {1} 块,净增可走 {2:0.0} m²),复核未通过 {3} 项 ==",
            pieces, decks, newArea, bad));

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        rep.AppendLine("  已存场景;" + (bad == 0 ? "复核全绿" : "!! 有 " + bad + " 项没过,先看报告"));
        Directory.CreateDirectory("Logs");
        File.WriteAllText(kBuildLog, rep.ToString());
        Debug.Log("[廊道改造] " + (bad == 0 ? "完成" : "完成但有 " + bad + " 项复核未过") + ",报告写 " + kBuildLog);
    }

    static void SnapshotOnce()
    {
        if (File.Exists(kSnapshot))
        {
            rep.AppendLine("  快照已在(不覆盖)" + kSnapshot + " 最后写入 "
                + File.GetLastWriteTime(kSnapshot).ToString("MM-dd HH:mm:ss"));
            return;
        }
        File.Copy(kScene, kSnapshot);
        rep.AppendLine("  已快照 " + kSnapshot + "(" + new FileInfo(kSnapshot).Length + " 字节,改前的字节级回退点)");
    }

    /// <summary>
    /// 楼梯进口留多宽:现场量,不写死。台阶是手摆的 Stair_0..N,以前那一版把洞写死成
    /// z[-11.92,-9.55],台阶挪一次就变成"栏板长在楼梯线上"—— 二楼唯一的非传送通道被自己焊死。
    /// 取所有台阶碰撞体(排除 Pad_*,那是传送垫不是台阶)的 z 并集,再各让出半根边梁 + 余量。
    /// </summary>
    static void MeasureStair()
    {
        float z0 = 999f, z1 = -999f, x0 = 999f, x1 = -999f, top = -1f;
        int steps = 0;
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger || c.gameObject.name.StartsWith("Pad_")) continue;
            if (!Node(c.transform).Contains("Stair")) continue;
            var b = c.bounds;
            steps++;
            z0 = Mathf.Min(z0, b.min.z); z1 = Mathf.Max(z1, b.max.z);
            x0 = Mathf.Min(x0, b.min.x); x1 = Mathf.Max(x1, b.max.x);
            top = Mathf.Max(top, b.max.y);
        }
        if (steps == 0)
        {
            rep.AppendLine("  !! 现场量不到台阶(名字带 Stair 的非触发碰撞体 0 个)→ 沿用占位洞"
                + " z[" + StairA.ToString("0.00") + "," + StairB.ToString("0.00") + "],栏板可能压在梯口上");
            return;
        }
        float pad = BeamW * 0.5f + StairPad;
        StairA = z0 - pad;
        StairB = z1 + pad;
        rep.AppendLine(string.Format("  [楼梯口实测] {0} 级台阶 x[{1:0.00},{2:0.00}] z[{3:0.00},{4:0.00}] 顶 y={5:0.00}"
            + " → 东栏板留洞 z[{6:0.00},{7:0.00}](各让 {8:0.00} m)",
            steps, x0, x1, z0, z1, top, StairA, StairB, pad));
    }

    static void MakeMaterials()
    {
        matMetal = Load("Assets/Materials/M_Metal.mat");
        matGlow = Load("Assets/Materials/M_LightBar.mat");
        matCyan = Load("Assets/Materials/M_NeonCyan.mat");
        matPink = Load("Assets/Materials/M_NeonMagenta.mat");
        matGlass = CorridorGlass();
        rep.AppendLine("  [材质] 复用 M_Metal / M_LightBar / M_NeonCyan / M_NeonMagenta(不新增灯色族);玻璃用 "
            + (matGlass != null ? matGlass.name : "!! 取不到"));
    }

    static Material Load(string path) { return AssetDatabase.LoadAssetAtPath<Material>(path); }

    /// <summary>
    /// 可行走的玻璃 = 拷一份 M_Glass 再改不透明度。必须拷贝而不是手搓:URP 的透明靠一串 shader
    /// keyword 和渲染队列,单设 _Surface/_Blend 这些浮点值容易做出不透明排序挡住后面的假透明。
    /// 已存在就完全不碰(尊重事后手工调过的值)。
    /// </summary>
    static Material CorridorGlass()
    {
        const string src = "Assets/Materials/M_Glass.mat", dst = "Assets/Materials/M_CorridorGlass.mat";
        var m = Load(dst);
        if (m != null) { rep.AppendLine("  材质已存在,原样复用不覆盖:M_CorridorGlass"); return m; }
        if (!AssetDatabase.CopyAsset(src, dst))
        {
            rep.AppendLine("  !! 拷 M_CorridorGlass 失败,退回 M_Glass");
            return Load(src);
        }
        AssetDatabase.Refresh();
        m = Load(dst);
        m.name = "M_CorridorGlass";
        var c = new Color(0.60f, 0.84f, 0.94f, 0.34f);   // α0.34:看得穿,又不能看不见——踩上去要读得出这是玻璃
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);             // 双面:从一层往上也要看得到楼板
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.9f);
        EditorUtility.SetDirty(m);
        AssetDatabase.SaveAssets();
        rep.AppendLine("  新建材质:M_CorridorGlass(拷 M_Glass,α=0.34,双面)");
        return m;
    }

    /// <summary>删上一版成品 + 被替换的旧栏板。楼梯横梁 RailLintel_E 与露台栏杆不在名单里。</summary>
    static void ClearOld()
    {
        int own = 0, rail = 0;
        var old = GameObject.Find("SmartLibrary/Mezzanine/" + GroupName);
        if (old != null)
        {
            own = old.transform.childCount;
            Undo.DestroyObjectImmediate(old);
        }
        foreach (var t in Object.FindObjectsOfType<Transform>())
        {
            string n = t.name;
            if (!(n.StartsWith("RailGlass_") || n.StartsWith("RailTop_") || n.StartsWith("RailPost_"))) continue;
            Undo.DestroyObjectImmediate(t.gameObject);
            rail++;
        }
        rep.AppendLine("  [幂等] 删上一版成品 " + own + " 件;拆旧栏板(玻璃/压顶/立柱)" + rail
            + " 件(RailLintel_E 与露台栏杆保留)");
    }

    // ------------------------------------------------------------ 玻璃楼板

    static void BuildDecks()
    {
        rep.AppendLine("  [玻璃挑台/廊桥]");
        Deck("Bay_N", -Span, Span, VN, VN + BayN);
        Deck("Bay_S", -Span, Span, VS - BayS, VS);
        Deck("Bay_E", VE - BayE, VE, EastZ0, EastZ1);
        Deck("Bridge", BridgeX - BridgeHalf, BridgeX + BridgeHalf, VN + BayN, VS - BayS);
        Deck("Hall_W", BridgeX - HallHalf, BridgeX - BridgeHalf, -HallHalf, HallHalf);
        Deck("Hall_E", BridgeX + BridgeHalf, BridgeX + HallHalf, -HallHalf, HallHalf);
    }

    static void Deck(string name, float x0, float x1, float z0, float z1)
    {
        Cuboid(name, new Vector3((x0 + x1) * 0.5f, DeckTop - DeckThkNew * 0.5f, (z0 + z1) * 0.5f),
            new Vector3(x1 - x0, DeckThkNew, z1 - z0), matGlass);
        decks++;
        newArea += (x1 - x0) * (z1 - z0);
        rep.AppendLine(string.Format("     {0,-8} x[{1:0.00},{2:0.00}] z[{3:0.00},{4:0.00}] 顶 y={5:0.000} {6:0.0} m²",
            name, x0, x1, z0, z1, DeckTop, (x1 - x0) * (z1 - z0)));
        Ribs(name, x0, x1, z0, z1);
    }

    /// <summary>板底的横向钢梁:玻璃自己没有可读的结构,不加几道肋就像一块会消失的地板。</summary>
    static void Ribs(string tag, float x0, float x1, float z0, float z1)
    {
        bool wide = x1 - x0 >= z1 - z0;
        float len = wide ? x1 - x0 : z1 - z0;
        int n = Mathf.Max(2, Mathf.CeilToInt(len / 4f));
        float bottom = DeckTop - DeckThkNew;
        for (int i = 1; i < n; i++)
        {
            float t = i * len / n;
            var c = wide ? new Vector3(x0 + t, bottom - 0.06f, (z0 + z1) * 0.5f)
                         : new Vector3((x0 + x1) * 0.5f, bottom - 0.06f, z0 + t);
            var s = wide ? new Vector3(0.10f, 0.12f, z1 - z0) : new Vector3(x1 - x0, 0.12f, 0.10f);
            Cuboid("Rib_" + tag + "_" + i, c, s, matMetal);
        }
    }

    // ------------------------------------------------------------ 栏板环

    /// <summary>一圈闭合折线,每段同时出金属边梁 + 发光裙板 + 玻璃栏板 + 霓虹压顶;楼梯进口那段整段不放。</summary>
    static void BuildBalustrade()
    {
        rep.AppendLine("  [无框玻璃栏板 1.20 m + 霓虹压顶 + 发光裙板]");
        foreach (var s in Perimeter())
        {
            float len = Mathf.Abs(s.x1 - s.x0) + Mathf.Abs(s.z1 - s.z0);
            if (len < 0.05f) continue;
            if (!s.rail)
            {
                rep.AppendLine(string.Format("     {0,-10} 长 {1:0.00} m —— 楼梯进口,梁和裙板也不放(放了就是堵死唯一的非传送通道)",
                    s.tag, len));
                continue;
            }
            float cx = (s.x0 + s.x1) * 0.5f, cz = (s.z0 + s.z1) * 0.5f;
            bool alongX = Mathf.Abs(s.x1 - s.x0) >= Mathf.Abs(s.z1 - s.z0);

            Cuboid("Beam_" + s.tag, new Vector3(cx, DeckTop - BeamH * 0.5f, cz),
                alongX ? new Vector3(len + BeamW, BeamH, BeamW) : new Vector3(BeamW, BeamH, len + BeamW), matMetal);
            Cuboid("Fascia_" + s.tag, new Vector3(cx, DeckTop - BeamH - FasciaH * 0.5f, cz),
                alongX ? new Vector3(len + FasciaT, FasciaH, FasciaT)
                       : new Vector3(FasciaT, FasciaH, len + FasciaT), matGlow);
            Cuboid("Glass_" + s.tag, new Vector3(cx, DeckTop + RailH * 0.5f, cz),
                alongX ? new Vector3(len + 0.03f, RailH, 0.024f) : new Vector3(0.024f, RailH, len + 0.03f), matGlass);
            var cap = CapMat(s.tag);
            Cuboid("Cap_" + s.tag, new Vector3(cx, DeckTop + RailH + 0.04f, cz),
                alongX ? new Vector3(len + 0.06f, 0.08f, 0.08f) : new Vector3(0.08f, 0.08f, len + 0.06f), cap);
            rep.AppendLine(string.Format("     {0,-10} 长 {1:0.00} m 玻璃顶 y={2:0.00} 压顶 {3}",
                s.tag, len, DeckTop + RailH, cap != null ? cap.name : "?"));
        }
    }

    static Material CapMat(string tag)
    {
        return (tag.StartsWith("S") || tag.StartsWith("W")) ? matPink : matCyan;
    }

    static List<Seg> Perimeter()
    {
        var l = new List<Seg>();
        System.Action<float, float, float, float, bool, string> add = (x0, z0, x1, z1, rail, tag) =>
            l.Add(new Seg { x0 = x0, z0 = z0, x1 = x1, z1 = z1, rail = rail, tag = tag });
        float ne = VN + BayN, se = VS - BayS, ex = VE - BayE;

        add(VW, VN, -Span, VN, true, "N1");
        add(-Span, VN, -Span, ne, true, "N2");
        add(-Span, ne, BridgeX - BridgeHalf, ne, true, "N3a");     // 廊桥落口必须断开,否则桥走到头撞栏板
        add(BridgeX + BridgeHalf, ne, Span, ne, true, "N3b");
        add(Span, ne, Span, VN, true, "N4");
        add(Span, VN, VE, VN, true, "N5");

        add(VE, VN, VE, StairA, true, "E1");
        add(VE, StairA, VE, StairB, false, "E2stair");             // 楼梯进口:整段什么都不放
        add(VE, StairB, VE, EastZ0, true, "E3");
        add(VE, EastZ0, ex, EastZ0, true, "E4");
        add(ex, EastZ0, ex, EastZ1, true, "E5");
        add(ex, EastZ1, VE, EastZ1, true, "E6");
        add(VE, EastZ1, VE, VS, true, "E7");

        add(VE, VS, Span, VS, true, "S1");
        add(Span, VS, Span, se, true, "S2");
        add(Span, se, BridgeX + BridgeHalf, se, true, "S3a");
        add(BridgeX - BridgeHalf, se, -Span, se, true, "S3b");
        add(-Span, se, -Span, VS, true, "S4");
        add(-Span, VS, VW, VS, true, "S5");

        add(VW, VS, VW, VN, true, "W1");

        // 廊桥两侧:经过观景厅那一段开口,不然进不去厅
        add(BridgeX - BridgeHalf, ne, BridgeX - BridgeHalf, -HallHalf, true, "B1a");
        add(BridgeX - BridgeHalf, HallHalf, BridgeX - BridgeHalf, se, true, "B1b");
        add(BridgeX + BridgeHalf, ne, BridgeX + BridgeHalf, -HallHalf, true, "B2a");
        add(BridgeX + BridgeHalf, HallHalf, BridgeX + BridgeHalf, se, true, "B2b");

        // 观景厅外圈(两条桥落进来的边不放)
        add(BridgeX - HallHalf, -HallHalf, BridgeX - HallHalf, HallHalf, true, "HW");
        add(BridgeX + HallHalf, -HallHalf, BridgeX + HallHalf, HallHalf, true, "HE");
        add(BridgeX - HallHalf, HallHalf, BridgeX - BridgeHalf, HallHalf, true, "HN1");
        add(BridgeX + BridgeHalf, HallHalf, BridgeX + HallHalf, HallHalf, true, "HN2");
        add(BridgeX - HallHalf, -HallHalf, BridgeX - BridgeHalf, -HallHalf, true, "HS1");
        add(BridgeX + BridgeHalf, -HallHalf, BridgeX + BridgeHalf, -HallHalf, true, "HS2");
        return l;
    }

    /// <summary>观景厅地面镶一圈发光格线:夜里从一层抬头能看出"上面有个厅"。</summary>
    static void BuildHallTrim()
    {
        rep.AppendLine("  [观景厅地面发光格线]");
        float y = DeckTop + 0.006f;
        Inlay("HallRing_N", new Vector3(BridgeX, y, -HallHalf + 0.35f), new Vector3(4.1f, 0.012f, 0.06f));
        Inlay("HallRing_S", new Vector3(BridgeX, y, HallHalf - 0.35f), new Vector3(4.1f, 0.012f, 0.06f));
        Inlay("HallRing_W", new Vector3(BridgeX - HallHalf + 0.35f, y, 0f), new Vector3(0.06f, 0.012f, 3.7f));
        Inlay("HallRing_E", new Vector3(BridgeX + HallHalf - 0.35f, y, 0f), new Vector3(0.06f, 0.012f, 3.7f));
    }

    static void Inlay(string name, Vector3 c, Vector3 size)
    {
        var go = Cuboid(name, c, size, matCyan);
        Object.DestroyImmediate(go.GetComponent<Collider>());   // 贴地的一条线,别挡传送射线
    }

    static GameObject Cuboid(string name, Vector3 center, Vector3 size, Material m)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Undo.RegisterCreatedObjectUndo(go, "创建 " + name);
        go.transform.SetParent(grp.transform, false);
        go.transform.position = center;
        go.transform.localScale = size;
        if (m != null) go.GetComponent<MeshRenderer>().sharedMaterial = m;
        // 透明件不能参与静态合批:合批后多个玻璃并进一个网格,逐件排序没了 → 会出现"看得穿的板
        // 挡住了身后的东西"。金属梁/裙板/压顶是不透明的,照旧 static。
        go.isStatic = m != matGlass;
        pieces++;
        return go;
    }

    // ------------------------------------------------------------ 复核

    /// <summary>
    /// 三件必须自证的事:
    ///  ① 新玻璃板面顶要和旧板面齐平(差几毫米就是踩脚/台阶);
    ///  ② 每块新楼板都得有自己的实心碰撞体(没碰撞体 = 人掉下去、传送射线也打不到);
    ///  ③ 新构件不许和一层物体重叠 —— 这条不写死坐标,现场重新量,因为角柱/吊牌立柱/书柜都是手摆的,
    ///     以后它们挪了这里必须能报出来。
    /// </summary>
    static int Verify()
    {
        int bad = 0;
        var mine = new List<Bounds>();
        var names = new List<string>();
        foreach (var r in grp.GetComponentsInChildren<Renderer>(true))
        {
            mine.Add(r.bounds);
            names.Add(r.gameObject.name);
            string n = r.gameObject.name;
            if (n.StartsWith("Bay_") || n.StartsWith("Bridge") || n.StartsWith("Hall_"))
            {
                var col = r.gameObject.GetComponent<Collider>();
                bool flat = Mathf.Abs(r.bounds.max.y - DeckTop) < 0.002f;
                if (!flat || col == null || col.isTrigger)
                {
                    bad++;
                    rep.AppendLine("     !! " + n + " 板面顶 " + r.bounds.max.y.ToString("0.000")
                        + "(应 " + DeckTop.ToString("0.000") + ") 碰撞体 "
                        + (col == null ? "无" : (col.isTrigger ? "触发体" : "有")));
                }
            }
        }
        rep.AppendLine("     板面齐平 + 实心碰撞体:未列问题即通过(新构件 " + pieces + " 件)");

        int hits = 0;
        foreach (var c in Object.FindObjectsOfType<Collider>())
        {
            if (c.isTrigger) continue;
            string p = Node(c.transform);
            if (p.Contains(GroupName) || IsPlayer(c)) continue;
            // 这三类是"必须贴上去"的:走道板(新挑台/边梁就挂在它的内沿上)、板边灯条、露台板。
            // 不豁免的话每次重跑都刷三十来行假"撞",真撞了反而看不见。
            if (Regex.IsMatch(p, @"Slab_|MezzGlow|TerraceDeck")) continue;
            var b = c.bounds;
            if (b.max.y < 0.20f) continue;                 // 地面/地毯本身
            // 只排开"头顶够不着"的东西:站姿眼高约板面上 1.6 m,头顶 1.75 m。
            // 上一版按栏板高(4.405)截,拱梁 4.20~4.50 这种"挂在桥上刚好撞头"的差 0.35 m 就漏了。
            if (b.min.y > DeckTop + 1.75f) continue;
            for (int i = 0; i < mine.Count; i++)
            {
                var m = mine[i];
                bool over = m.min.x < b.max.x && m.max.x > b.min.x
                         && m.min.y < b.max.y && m.max.y > b.min.y
                         && m.min.z < b.max.z && m.max.z > b.min.z;
                if (!over) continue;
                hits++;
                if (hits <= 10)
                    rep.AppendLine(string.Format("     !! 撞:{0} ∩ {1} y[{2:0.00},{3:0.00}] x[{4:0.00},{5:0.00}] z[{6:0.00},{7:0.00}]",
                        p, names[i], b.min.y, b.max.y, b.min.x, b.max.x, b.min.z, b.max.z));
                break;
            }
        }
        bad += hits;
        rep.AppendLine("     与一层/露台物体重叠的新构件:" + hits + " 个" + (hits == 0 ? " ✓" : " !!"));

        float voidA = (VE - VW) * (VS - VN);
        rep.AppendLine(string.Format("     新占中庭开口 {0:0.0} m²({1:0%}),净增可走面 {2:0.0} m²(现状环面 360.7 m²)",
            newArea, newArea / voidA, newArea));
        return bad;
    }

    // ------------------------------------------------------------ 工具

    static List<string> MaterialsMatching(string pattern)
    {
        var set = new List<string>();
        foreach (var r in Object.FindObjectsOfType<Renderer>())
        {
            string p = Node(r.transform);
            if (!Regex.IsMatch(p, pattern)) continue;
            if (r.sharedMaterial == null) continue;
            var m = r.sharedMaterial;
            var col = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor")
                    : (m.HasProperty("_Color") ? m.GetColor("_Color") : new Color(1, 1, 1, 1));
            float em = m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor").grayscale : 0f;
            set.Add(string.Format("{0,-26} {1,-22} 色({2:0.00},{3:0.00},{4:0.00},α{5:0.00}) 自发光 {6:0.00}",
                p, m.name, col.r, col.g, col.b, col.a, em));
        }
        return set;
    }

    static void Add(Dictionary<string, Box> map, Collider c, string path)
    {
        var b = c.bounds;
        Box o;
        if (!map.TryGetValue(path, out o))
        {
            o = new Box { name = path, mat = Mat(c), x0 = b.min.x, y0 = b.min.y, z0 = b.min.z,
                          x1 = b.max.x, y1 = b.max.y, z1 = b.max.z };
            map[path] = o;
        }
        o.parts++;
        o.x0 = Mathf.Min(o.x0, b.min.x); o.y0 = Mathf.Min(o.y0, b.min.y); o.z0 = Mathf.Min(o.z0, b.min.z);
        o.x1 = Mathf.Max(o.x1, b.max.x); o.y1 = Mathf.Max(o.y1, b.max.y); o.z1 = Mathf.Max(o.z1, b.max.z);
        map[path] = o;
    }

    static void AddR(Dictionary<string, Box> map, Renderer r, string path)
    {
        var b = r.bounds;
        Box o;
        if (!map.TryGetValue(path, out o))
        {
            o = new Box { name = path, mat = r.sharedMaterial != null ? r.sharedMaterial.name : "",
                          x0 = b.min.x, y0 = b.min.y, z0 = b.min.z, x1 = b.max.x, y1 = b.max.y, z1 = b.max.z };
            map[path] = o;
        }
        o.parts++;
        o.x0 = Mathf.Min(o.x0, b.min.x); o.y0 = Mathf.Min(o.y0, b.min.y); o.z0 = Mathf.Min(o.z0, b.min.z);
        o.x1 = Mathf.Max(o.x1, b.max.x); o.y1 = Mathf.Max(o.y1, b.max.y); o.z1 = Mathf.Max(o.z1, b.max.z);
        map[path] = o;
    }

    static List<Box> Sorted(Dictionary<string, Box> map)
    {
        var l = new List<Box>(map.Values);
        l.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return l;
    }

    static Box Of(Transform t, Bounds b, string mat)
    {
        return new Box { name = Node(t), mat = mat, x0 = b.min.x, y0 = b.min.y, z0 = b.min.z,
                         x1 = b.max.x, y1 = b.max.y, z1 = b.max.z, parts = 1 };
    }

    static string Mat(Collider c)
    {
        var r = c.GetComponent<Renderer>();
        return r != null && r.sharedMaterial != null ? r.sharedMaterial.name : "";
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
        Debug.Log("[廊道测绘] 报告已写 " + kLog + "\n" + sb);
    }
}
#endif
