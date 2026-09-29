using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 楼梯重建:单跑 10 级(踢面 0.288、顶 2.875)—— 拔高后二层板面到了 4.428,
/// **差 1.553 m,走上去是一堵墙**(2026-09-30 用户定案:改成**折返双跑 + 休息平台**)。
/// 菜单 VRLibrary/楼梯·xxx。
///
/// 为什么不是把老楼梯拉长:
///   · 老楼梯是 10 级楔形块(每块都从 y=0 长到自己的踏面),踢面 0.288、踏面仅 0.164 —— 60° 的"梯子",
///     现场把踏面做到 0.28、踢面压到 0.20 需要 ~6.2 m 进深,单跑放不下;
///   · 折返双跑把同样的爬升折成两段,进深只要一半,还能顺手把踢面从 0.288 降到 0.20(更好爬)。
///
/// 全部几何都**从现场量出来的老楼梯**推:上升轴、上升方向、梯宽、起点 —— 不写死 xyz;
/// 参数(踢面/踏面/级数)由"总爬升 ÷ 目标踢面"算出来,复核时逐条量回来。
/// </summary>
public static class VRLibraryStairs
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Report = "Logs/stairs.txt";
    const string Group = "Mezzanine";              // 老楼梯挂在 Mezzanine 下
    const float SlabTop = 4.428f;                  // 拔高后的二层板面顶
    const float FloorTop = 0.028f;                 // 一层地砖顶
    const float MaxRiser = 0.200f;                 // 目标踢面上限(老楼梯 0.288,实测玩家能爬,所以 0.20 更稳)
    const float Tread = 0.28f;                     // 踏面进深(≥0.24 才不会卡脚:老楼梯 0.164 偏窄)
    const float LandingD = 0.90f;                  // 休息平台进深
    const float Gap = 0.10f;                       // 两跑之间的缝
    const float StepThick = 0.06f;                 // 踏步板厚(视觉)

    static StringBuilder sb;
    static int bad;

    // ──────────────────────────────────────────────────────────── 量

    /// <summary>老楼梯的几何账:每级中心/顶面、上升轴与方向、梯宽、起点,以及折返需要的场地够不够。</summary>
    static List<Transform> OldSteps(GameObject root)
    {
        var list = new List<Transform>();
        if (root == null) return list;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("Stair_")) list.Add(t);
        list.Sort((a, b) => World(a).max.y.CompareTo(World(b).max.y));
        return list;
    }

    [MenuItem("VRLibrary/楼梯·现状测量(只报告)")]
    public static void Survey()
    {
        sb = new StringBuilder(); bad = 0;
        sb.AppendLine("楼梯·现状测量(只读)  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var root = GameObject.Find("SmartLibrary");
        if (root == null) { sb.AppendLine("  !! 找不到 SmartLibrary"); Flush(); return; }
        var steps = OldSteps(root);
        sb.AppendLine("  老楼梯:找到 " + steps.Count + " 级(名字 Stair_*)");
        if (steps.Count < 2) { sb.AppendLine("  !! 太少,无法量"); Flush(); return; }

        var b0 = World(steps[0]); var bN = World(steps[steps.Count - 1]);
        float riser = (bN.max.y - b0.max.y) / (steps.Count - 1);
        // 上升轴:哪一根轴上"级中心"与"踏面高度"单调相关
        float dx = bN.center.x - b0.center.x, dz = bN.center.z - b0.center.z;
        bool alongX = Mathf.Abs(dx) >= Mathf.Abs(dz);
        Vector3 dir = (alongX ? new Vector3(Mathf.Sign(dx), 0, 0) : new Vector3(0, 0, Mathf.Sign(dz)));
        Vector3 perp = new Vector3(-dir.z, 0, dir.x);
        float width = alongX ? Mathf.Max(World(steps[0]).size.z, World(steps[steps.Count - 1]).size.z)
                             : Mathf.Max(World(steps[0]).size.x, World(steps[steps.Count - 1]).size.x);
        // 沿上升轴每级推进多少(踏面)
        float run = 0f;
        for (int i = 1; i < steps.Count; i++)
        {
            var a = World(steps[i - 1]).center; var b = World(steps[i]).center;
            run += alongX ? Mathf.Abs(b.x - a.x) : Mathf.Abs(b.z - a.z);
        }
        run /= (steps.Count - 1);

        sb.AppendLine(string.Format("    上升轴 {0}(方向 {1}) 梯宽 {2:0.00} m 踏面 {3:0.000} m 踢面 {4:0.000} m",
            alongX ? "X" : "Z", dir, width, run, riser));
        sb.AppendLine(string.Format("    底级 {0}: 踏面顶 {1:0.000} 中心 ({2:0.00},{3:0.00})",
            steps[0].name, b0.max.y, b0.center.x, b0.center.z));
        sb.AppendLine(string.Format("    顶级 {0}: 踏面顶 {1:0.000} 中心 ({2:0.00},{3:0.00})",
            steps[steps.Count - 1].name, bN.max.y, bN.center.x, bN.center.z));
        sb.AppendLine(string.Format("    总爬升 {0:0.000} → 目标二层板面 {1:0.000};**还差 {2:0.000} m**",
            bN.max.y - FloorTop, SlabTop, SlabTop - bN.max.y));

        // 目标参数
        float rise = SlabTop - FloorTop;
        int n = Mathf.CeilToInt(rise / MaxRiser);
        float r = rise / n;
        int n1 = n / 2, n2 = n - n1;
        sb.AppendLine(string.Format("    目标:{0} 级 × 踢面 {1:0.000}(总爬升 {2:0.000});折返 ⇒ 第一跑 {3} 级 + 平台 + 第二跑 {4} 级",
            n, r, rise, n1, n2));
        sb.AppendLine(string.Format("    每跑步幅 {0:0.00} m(踏面 {1:0.000} × 级数)+ 平台进深 {2:0.00} m;两跑并排宽度 ≈ {3:0.00} m",
            Tread * n1, Tread, LandingD, width * 2 + Gap));

        // 场地够不够:把折返的足迹框出来,数里面有多少**别人的**碰撞体
        Vector3 start = new Vector3(b0.center.x - dir.x * run * 0.5f, 0, b0.center.z - dir.z * run * 0.5f);
        for (int side = 0; side < 2; side++)
        {
            Vector3 s = perp * (side == 0 ? 1f : -1f);
            var box = Footprint(start, dir, s, width, Tread * n1 + LandingD, width * 2 + Gap);
            int hits = CountIntruders(root, box, steps);
            sb.AppendLine(string.Format("    折返场地方向 {0}: 足迹 x[{1:0.0},{2:0.0}] z[{3:0.0},{4:0.0}]  里面有别人的碰撞体 {5} 个{6}",
                side == 0 ? "+侧" : "-侧", box.min.x, box.max.x, box.min.z, box.max.z, hits,
                hits == 0 ? " ✓" : " ← 要挪或换侧"));
        }
        sb.AppendLine("  复核用:改完跑 `楼梯·复核(只报告)` 与 `拔高·卡人点体检`");
        Flush();
        Debug.Log("[Stairs] 现状 → " + Report);
    }

    static Bounds Footprint(Vector3 start, Vector3 dir, Vector3 perp, float width, float length, float twoWide)
    {
        Vector3 c = start + dir * (length * 0.5f) + perp * (twoWide * 0.5f) + Vector3.up * (SlabTop * 0.5f + FloorTop);
        Vector3 size = new Vector3(
            Mathf.Abs(dir.x) * length + Mathf.Abs(perp.x) * twoWide + Mathf.Abs(dir.z) * 0 + 0.1f,
            SlabTop - FloorTop + 0.4f,
            Mathf.Abs(dir.z) * length + Mathf.Abs(perp.z) * twoWide + 0.1f);
        return new Bounds(c, size);
    }

    static int CountIntruders(GameObject root, Bounds box, List<Transform> ignore)
    {
        int n = 0;
        foreach (var c in root.GetComponentsInChildren<Collider>(true))
        {
            bool skip = false;
            foreach (var t in ignore) if (c.transform == t || c.transform.IsChildOf(t)) { skip = true; break; }
            if (skip) continue;
            if (c.bounds.Intersects(box)) n++;
        }
        return n;
    }

    // ──────────────────────────────────────────────────────────── 建

    [MenuItem("VRLibrary/楼梯·重建双跑楼梯(改场景并自动保存)")]
    public static void Build()
    {
        sb = new StringBuilder(); bad = 0;
        sb.AppendLine("楼梯·重建折返双跑  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.path.EndsWith("SmartLibrary.unity")) { Debug.LogError("[Stairs] 场景不对:" + scene.path); return; }
        var root = GameObject.Find("SmartLibrary");
        var steps = OldSteps(root);
        if (root == null || steps.Count < 2) { Debug.LogError("[Stairs] 找不到老楼梯"); return; }

        var b0 = World(steps[0]); var bN = World(steps[steps.Count - 1]);
        float dx = bN.center.x - b0.center.x, dz = bN.center.z - b0.center.z;
        bool alongX = Mathf.Abs(dx) >= Mathf.Abs(dz);
        Vector3 dir = alongX ? new Vector3(Mathf.Sign(dx), 0, 0) : new Vector3(0, 0, Mathf.Sign(dz));
        Vector3 perp = new Vector3(-dir.z, 0, dir.x);
        float width = alongX ? Mathf.Max(b0.size.z, bN.size.z) : Mathf.Max(b0.size.x, bN.size.x);
        Vector3 start = new Vector3(b0.center.x, 0, b0.center.z) - dir * (0.5f * (alongX ? b0.size.x : b0.size.z));

        float rise = SlabTop - FloorTop;
        int n = Mathf.CeilToInt(rise / MaxRiser);
        float riser = rise / n;
        int n1 = n / 2, n2 = n - n1;

        // 选哪一侧:数足迹里的外人,挑少的
        int hPlus = CountIntruders(root, Footprint(start, dir, perp, width, Tread * n1 + LandingD, width * 2 + Gap), steps);
        int hMinus = CountIntruders(root, Footprint(start, dir, -perp, width, Tread * n1 + LandingD, width * 2 + Gap), steps);
        Vector3 side = (hPlus <= hMinus ? perp : -perp);
        sb.AppendLine(string.Format("  参数:{0} 级 × 踢面 {1:0.000}(爬升 {2:0.000} → 板面 {3:0.000});第一跑 {4} 级 + 平台 + 第二跑 {5} 级",
            n, riser, rise, SlabTop, n1, n2));
        sb.AppendLine(string.Format("  上升轴 {0} 梯宽 {1:0.00} 起于 ({2:0.00},{3:0.00});折返侧 {4}(外人 +侧 {5} / -侧 {6})",
            alongX ? "X" : "Z", width, start.x, start.z, side == perp ? "+" : "-", hPlus, hMinus));

        Snapshot(scene.path);

        var mf = steps[steps.Count - 1].GetComponent<Renderer>();
        Material mat = mf != null ? mf.sharedMaterial : null;
        var glowGo = root.transform.Find(Group + "/StairGlowL");
        Material glow = glowGo != null && glowGo.GetComponent<Renderer>() != null ? glowGo.GetComponent<Renderer>().sharedMaterial : mat;

        // 删老楼梯(含两条发光条)
        int del = 0;
        foreach (var t in steps) { Undo.DestroyObjectImmediate(t.gameObject); del++; }
        foreach (var nm in new[] { "StairGlowL", "StairGlowR" })
        {
            var t = root.transform.Find(Group + "/" + nm);
            if (t != null) { Undo.DestroyObjectImmediate(t.gameObject); del++; }
        }
        sb.AppendLine("  删除老楼梯构件 " + del + " 件");

        var parent = root.transform.Find(Group);
        if (parent == null) { sb.AppendLine("  !! 找不到 " + Group); Flush(); return; }
        var root2 = new GameObject("StairNew"); root2.transform.SetParent(parent, false);

        // 第一跑:沿 dir 上行;第二跑:沿 -dir 上行,并排错开 width+gap
        int made = 0;
        made += Flight(root2.transform, start, dir, side * 0, width, n1, riser, 1, mat);
        float landTop = FloorTop + riser * n1;
        var landCenter = start + dir * (Tread * n1 + LandingD * 0.5f) + side * (width * 0.5f);
        MakeBox(root2.transform, "Landing", landCenter + Vector3.up * (landTop - StepThick * 2f),
            new Vector3(alongX ? LandingD : width, StepThick * 2f, alongX ? width : LandingD), mat, true);
        sb.AppendLine(string.Format("  休息平台:顶 {0:0.000}(= 一层 + {1} 级 × {2:0.000})", landTop, n1, riser));
        // 第二跑:从平台往回走,并排错到 side 一侧
        Vector3 p2 = start + dir * (Tread * n1 + LandingD) + side * (width + Gap);
        made += Flight(root2.transform, p2, -dir, side * 0, width, n2, riser, n1 + 1, mat);
        sb.AppendLine("  新建踏步 " + made + " 级 + 平台");

        // 发光条(装饰:不带碰撞体)
        Glow(root2.transform, start, dir, width, n1, riser, mat, glow, "GlowA");
        Glow(root2.transform, p2, -dir, width, n2, riser, mat, glow, "GlowB");

        Physics.SyncTransforms();

        // ── 复核:逐条量回来
        float top = -999f; int cnt = 0;
        foreach (var t in root2.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("Step_")) continue;
            cnt++; top = Mathf.Max(top, World(t).max.y);
        }
        sb.AppendLine("  ── 复核 ──");
        sb.AppendLine(string.Format("    新楼梯 {0} 级,最高踏面 {1:0.000}(目标二层板面 {2:0.000})", cnt, top, SlabTop));
        BadIf(cnt != n, "级数 = " + n);
        BadIf(Mathf.Abs(top - SlabTop) > 0.02f, "最高踏面 = 二层板面 ±0.02");
        BadIf(riser > MaxRiser + 1e-4f, "踢面 ≤ " + MaxRiser.ToString("0.000"));
        BadIf(Tread < 0.24f, "踏面 ≥ 0.24");
        // 顶上能不能踏到二楼:从**第二跑最高那级的前方**(继续走一步的位置)向下探,看有没有 4.428 的楼板
        Vector3 topStepCenter = p2 - dir * (Tread * (n2 - 1) + Tread * 0.5f);
        Vector3 beyond = topStepCenter - dir * (Tread + 0.25f);
        RaycastHit hh;
        bool land = Physics.Raycast(new Vector3(beyond.x, SlabTop + 0.5f, beyond.z), Vector3.down, out hh, 1.2f, ~0, QueryTriggerInteraction.Ignore);
        sb.AppendLine(string.Format("    顶层落脚点 ({0:0.00},{1:0.00}) 向下探:{2}", beyond.x, beyond.z,
            land ? hh.collider.name + " y " + hh.point.y.ToString("0.000") + (Mathf.Abs(hh.point.y - SlabTop) < 0.10f ? " ✓ 接上二层" : " !! 不是二层板面") : "!! 打空"));
        BadIf(!land || Mathf.Abs(hh.point.y - SlabTop) > 0.10f, "顶层能踏到二层板面");
        sb.AppendLine("    未过 " + bad + " 项");

        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("  保存场景:" + (EditorSceneManager.SaveScene(scene) ? "成功" : "!! 失败"));
        sb.AppendLine("  回退:菜单 `楼梯·回退到改前快照`(快照 Logs/SmartLibrary.pre-stairs-" + DateTime.Now.ToString("yyyyMMdd") + ".unity)");
        Flush();
        Debug.Log("[Stairs] 重建完成,未过 " + bad + " 项 → " + Report);
    }

    /// <summary>一跑踏步:从 from 沿 d 方向逐级抬高。</summary>
    static int Flight(Transform parent, Vector3 from, Vector3 d, Vector3 side, float width, int count, float riser, int idx0, Material mat)
    {
        for (int i = 0; i < count; i++)
        {
            float top = FloorTop + riser * (idx0 + i);
            Vector3 c = from + d * (Tread * i + Tread * 0.5f) + side + Vector3.up * (top - StepThick * 0.5f);
            Vector3 size = new Vector3(Mathf.Abs(d.x) > 0.5f ? Tread : width, StepThick, Mathf.Abs(d.z) > 0.5f ? Tread : width);
            MakeBox(parent, string.Format("Step_{0:00}", idx0 + i), c, size, mat, true);
        }
        return count;
    }

    static void Glow(Transform parent, Vector3 from, Vector3 d, float width, int count, float riser, Material mat, Material glow, string nm)
    {
        if (glow == null) return;
        float len = Tread * count;
        Vector3 c = from + d * (len * 0.5f) + Vector3.up * (FloorTop + riser * (count * 0.5f) + 0.06f);
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = nm; go.transform.SetParent(parent, false);
        var col = go.GetComponent<Collider>(); if (col != null) UnityEngine.Object.DestroyImmediate(col);
        go.transform.position = c;
        go.transform.rotation = Quaternion.LookRotation(d, Vector3.up) * Quaternion.Euler(0, 0, 0);
        go.transform.localScale = new Vector3(width * 0.06f, 0.05f, len);
        go.transform.rotation = Quaternion.LookRotation(d) * Quaternion.Euler(-Mathf.Atan2(riser, Tread) * Mathf.Rad2Deg, 0, 0);
        var r = go.GetComponent<Renderer>(); if (r != null) r.sharedMaterial = glow;
    }

    static void MakeBox(Transform parent, string nm, Vector3 center, Vector3 size, Material mat, bool collider)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = nm; go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        if (!collider) { var c = go.GetComponent<Collider>(); if (c != null) UnityEngine.Object.DestroyImmediate(c); }
        var r = go.GetComponent<Renderer>();
        if (r != null && mat != null) r.sharedMaterial = mat;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
        return;
    }

    // ──────────────────────────────────────────────────────────── 回退 / 复核

    [MenuItem("VRLibrary/楼梯·回退到改前快照(改场景)")]
    public static void Revert()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.path.EndsWith("SmartLibrary.unity")) { Debug.LogError("[Stairs] 场景不对"); return; }
        string src = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath),
            "Logs/SmartLibrary.pre-stairs-" + DateTime.Now.ToString("yyyyMMdd") + ".unity");
        if (!File.Exists(src)) { Debug.LogError("[Stairs] 找不到 " + src); return; }
        if (scene.isDirty) { Debug.LogError("[Stairs] 场景有未保存改动,先处理"); return; }
        string abs = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), scene.path);
        File.Copy(src, abs, true);
        AssetDatabase.Refresh();
        EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
        Debug.Log("[Stairs] 已从 " + src + " 回退并重新载入");
    }

    [MenuItem("VRLibrary/楼梯·复核(只报告)")]
    public static void Verify()
    {
        sb = new StringBuilder(); bad = 0;
        sb.AppendLine("楼梯·复核(只读)  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var root = GameObject.Find("SmartLibrary");
        if (root == null) { sb.AppendLine("  !! 找不到 SmartLibrary"); Flush(); return; }
        var steps = new List<Transform>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith("Step_") || t.name.StartsWith("Stair_")) steps.Add(t);
        steps.Sort((a, b) => World(a).max.y.CompareTo(World(b).max.y));
        sb.AppendLine("  踏步共 " + steps.Count + " 级");
        if (steps.Count == 0) { Flush(); return; }
        float prev = -999f, riserSum = 0f; int k = 0;
        foreach (var t in steps)
        {
            var b = World(t);
            if (prev > -900f) { riserSum += b.max.y - prev; k++; }
            prev = b.max.y;
        }
        float riser = k > 0 ? riserSum / k : 0f;
        float top = World(steps[steps.Count - 1]).max.y;
        sb.AppendLine(string.Format("    最低踏面 {0:0.000} 最高踏面 {1:0.000} 平均踢面 {2:0.000}", World(steps[0]).max.y, top, riser));
        sb.AppendLine(string.Format("    二层板面 {0:0.000} ⇒ 差 {1:0.000} m", SlabTop, SlabTop - top));
        BadIf(Mathf.Abs(top - SlabTop) > 0.05f, "最高踏面接上二层板面(±0.05)");
        BadIf(riser > MaxRiser + 0.02f, "平均踢面 ≤ " + (MaxRiser + 0.02f).ToString("0.000"));
        sb.AppendLine("    未过 " + bad + " 项");
        Flush();
        Debug.Log("[Stairs] 复核 → " + Report);
    }

    static void Snapshot(string scenePath)
    {
        string dst = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath),
            "Logs/SmartLibrary.pre-stairs-" + DateTime.Now.ToString("yyyyMMdd") + ".unity");
        if (File.Exists(dst)) { sb.AppendLine("  今天的改前快照已存在,不覆盖:" + dst); return; }
        string src = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), scenePath);
        File.WriteAllBytes(dst, File.ReadAllBytes(src));
        sb.AppendLine("  改前快照:" + dst + "(" + new FileInfo(dst).Length.ToString("N0") + " B)");
    }

    static void BadIf(bool cond, string what)
    {
        if (cond) { bad++; sb.AppendLine("  !! " + what); } else sb.AppendLine("  ✓ " + what);
    }

    static Bounds World(Transform t)
    {
        var r = t.GetComponent<Renderer>();
        if (r != null) return r.bounds;
        var c = t.GetComponent<Collider>();
        if (c != null) return c.bounds;
        return new Bounds(t.position, Vector3.zero);
    }

    static void Flush()
    {
        string full = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), Report);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
        File.WriteAllText(full, sb.ToString(), Encoding.UTF8);
    }
}