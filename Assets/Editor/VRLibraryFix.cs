using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 智能图书馆 · 陈列与屏幕修复(由 VRLibrary/审计全场景 的报告驱动)。
/// 菜单 VRLibrary/修复陈列与屏幕 → 写 Logs/scene-fix.log。
///
/// 只做三件不含产品约定的事:
///   ① 消叠放 —— 旧导入器把书乱放,导致同一列挤两本书(实测 5 处)且抓取出错;
///   ② 展示屏退化缩放 —— 讲台 3 块屏 y=0(整面不可见)、阅览区 3 块 y<0(翻转),实测 6 处;
///   ③ 屏字出框 —— 长书名超出屏面(实测 18 本),按屏的实际包围盒收字号。
///
/// 格子编号的"读书方向"(书本列序与标签列序相反,详见 Logs/scene-audit.txt E 节)
/// 属于展馆约定,不在本工具职责内 —— 需要用户拍板后再改。
/// 幂等:重复执行结果不变。
/// </summary>
public static class VRLibraryFix
{
    const string kLog = "Logs/scene-fix.log";
    const float kPitch = 0.62f;            // 实测列距(书本与格子标签同为 0.62 m)
    static StringBuilder log;

    [MenuItem("VRLibrary/修复陈列与屏幕")]
    public static void Run()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/_Scenes/SmartLibrary.unity")
        {
            Debug.LogError("[VRLibraryFix] 当前场景不是 SmartLibrary,拒绝改动:" + scene.path);
            return;
        }

        log = new StringBuilder();
        Line("陈列与屏幕修复  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Physics.SyncTransforms();

        int destacked = DestackBooks();
        Line("① 消叠放:挪动 " + destacked + " 本");

        Physics.SyncTransforms();
        int disp = FixDisplayScales();
        Line("② 展示屏缩放修复:" + disp + " 本");

        Physics.SyncTransforms();
        int fitted = FitTitles();
        Line("③ 屏字收进屏面:" + fitted + " 本");

        int left = CountStacked();
        Line("复核:仍在同一列挤着的书 = " + left + " 列(应为 0)");

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Line("保存场景:" + (saved ? "成功" : "失败"));

        Directory.CreateDirectory("Logs");
        File.WriteAllText(kLog, log.ToString(), Encoding.UTF8);
        Debug.Log("[VRLibraryFix] 完成 → " + kLog);
    }

    // ── ① 消叠放 ───────────────────────────────────────────────────
    static int DestackBooks()
    {
        int moved = 0;
        // 只做一层四组书架:二层是多排斜列布局,实测没有挤格
        foreach (var grp in BooksOnShelves()
                    .Where(b => (ShelfOf(b.transform)?.name ?? "").StartsWith("Shelf_"))
                    .GroupBy(b => ShelfOf(b.transform).name))
        {
            var bs = grp.ToList();
            bool ax = AlongAxisIsX(bs);

            foreach (var lvl in bs.GroupBy(b => LevelBucket(b.transform.position.y)))
            {
                // 1) 按沿架坐标聚类成列
                var sorted = lvl.OrderBy(b => Along(b.transform.position, ax)).ToList();
                var clusters = new List<List<LibraryBook>>();
                foreach (var b in sorted)
                {
                    float a = Along(b.transform.position, ax);
                    if (clusters.Count > 0 &&
                        Mathf.Abs(a - Along(clusters[clusters.Count - 1][0].transform.position, ax)) <= 0.15f)
                        clusters[clusters.Count - 1].Add(b);
                    else clusters.Add(new List<LibraryBook> { b });
                }

                // 2) 反推空位:相邻列距≈2 格则中间空一格;再不足 8 列就向两端外推
                var centers = clusters.Select(c => Along(c[0].transform.position, ax)).ToList();
                var free = new List<float>();
                for (int i = 0; i + 1 < centers.Count; i++)
                {
                    float gap = centers[i + 1] - centers[i];
                    if (Mathf.Abs(gap - kPitch * 2f) <= 0.18f) free.Add((centers[i] + centers[i + 1]) * 0.5f);
                }
                for (int guard = 0; guard < 12 && centers.Count + free.Count < 8; guard++)
                {
                    if (guard % 2 == 0) { centers.Sort(); free.Add(centers[0] - kPitch); centers.Insert(0, centers[0] - kPitch); }
                    else { centers.Sort(); free.Add(centers[centers.Count - 1] + kPitch); centers.Add(centers[centers.Count - 1] + kPitch); }
                }

                // 3) 同一列多出来的书搬到空位(只改沿架坐标)
                foreach (var c in clusters.Where(c => c.Count > 1))
                {
                    foreach (var extra in c.Skip(1))
                    {
                        if (free.Count == 0)
                        {
                            Line("  " + grp.Key + " 层" + lvl.Key + " 推不出空列," + extra.gameObject.name + " 暂留原位");
                            continue;
                        }
                        float dst = free[0]; free.RemoveAt(0);
                        var p = extra.transform.position;
                        var t = p;
                        if (ax) t.x = dst; else t.z = dst;
                        extra.transform.position = t;
                        Physics.SyncTransforms();
                        moved++;
                        Line(string.Format("  消叠放 {0} 层{1} 沿架 {2:F2}→{3:F2} (位移 {4:F2},{5:F2},{6:F2})",
                            Chain(extra.gameObject), lvl.Key, Along(p, ax), Along(t, ax), t.x - p.x, t.y - p.y, t.z - p.z));
                    }
                }
            }
        }
        return moved;
    }

    static float Along(Vector3 p, bool alongX) { return alongX ? p.x : p.z; }

    /// <summary>同架各层共用列位;沿架轴 = 该架书本水平跨度更大的那个轴。</summary>
    static bool AlongAxisIsX(List<LibraryBook> bs)
    {
        float sx = bs.Max(b => b.transform.position.x) - bs.Min(b => b.transform.position.x);
        float sz = bs.Max(b => b.transform.position.z) - bs.Min(b => b.transform.position.z);
        return sx > sz;
    }

    static int ColIndex(Vector3 p, bool alongX)
    {
        return Mathf.RoundToInt((alongX ? p.x : p.z) / kPitch);
    }

    static int CountStacked()
    {
        int n = 0;
        foreach (var grp in BooksOnShelves().GroupBy(b => ShelfOf(b.transform).name))
        {
            var bs = grp.ToList();
            bool ax = AlongAxisIsX(bs);
            foreach (var lvl in bs.GroupBy(b => LevelBucket(b.transform.position.y)))
                if (lvl.GroupBy(b => ColIndex(b.transform.position, ax)).Any(g => g.Count() > 1)) n++;
        }
        return n;
    }

    /// <summary>层号:一层书架实测 y 约 0.52 / 1.42 / 2.30。</summary>
    static int LevelBucket(float y)
    {
        if (y < 0.95f) return 0;
        if (y < 1.85f) return 1;
        return 2;
    }

    // ── ② 展示屏退化缩放 ───────────────────────────────────────────
    static int FixDisplayScales()
    {
        const float ratio = 0.192f / 0.395f;      // 正常书的 Display 长宽比(实测)
        int n = 0;
        foreach (var b in UnityEngine.Object.FindObjectsOfType<LibraryBook>(true))
        {
            var disp = DeepChild<MeshRenderer>(b.transform, "Display");
            if (disp == null) continue;
            var s = disp.transform.localScale;
            if (s.y > 0.0021f) continue;           // 只处理零/翻转/退化的薄片
            float y = Mathf.Abs(s.x) * ratio;
            disp.transform.localScale = new Vector3(s.x, y, s.z);
            n++;
            Line(string.Format("  展示屏 {0} Display.y {1:F4} → {2:F4}(按正常屏长宽比)", Chain(b.gameObject), s.y, y));
        }
        return n;
    }

    // ── ③ 屏字收进屏面 ─────────────────────────────────────────────
    static int FitTitles()
    {
        int n = 0;
        foreach (var b in UnityEngine.Object.FindObjectsOfType<LibraryBook>(true))
            if (FitOne(b)) n++;
        return n;
    }

    /// <summary>把一本书的屏上书名收进屏面;发生改动时返回 true。供导入器复用。</summary>
    public static bool FitOne(LibraryBook b)
    {
        if (b == null) return false;
        var tm = DeepChild<TextMesh>(b.transform, "TitleText");
        var disp = DeepChild<MeshRenderer>(b.transform, "Display");
        if (tm == null || disp == null) return false;
        var mr = tm.GetComponent<MeshRenderer>();
        if (mr == null) return false;
        if (!Overflows(mr.bounds, disp.bounds)) return false;

        var t = mr.bounds; var d = disp.bounds;
        float tw = Mathf.Max(t.size.x, t.size.z), th = t.size.y;
        float dw = Mathf.Max(d.size.x, d.size.z), dh = d.size.y;
        if (tw <= 0f || th <= 0f || dw <= 0f || dh <= 0f) return false;

        // 保住完整书名优先:屏上只显示书名,截断成几个字就没法定位了
        float from = tm.characterSize;
        tm.characterSize = Mathf.Max(0.0024f, from * Mathf.Min(dw * 0.92f / tw, dh * 0.90f / th));
        Physics.SyncTransforms();
        Line(string.Format("  屏字 {0} 「{1}」 charSize {2:F6}→{3:F6} {4}",
            b.gameObject.name, Flat(tm.text), from, tm.characterSize,
            Overflows(mr.bounds, d) ? "仍出框" : "已收进屏内"));
        return true;
    }

    static bool Overflows(Bounds t, Bounds d)
    {
        const float tol = 0.012f;
        return t.max.y > d.max.y + tol || t.min.y < d.min.y - tol
            || t.max.x > d.max.x + tol || t.min.x < d.min.x - tol
            || t.max.z > d.max.z + tol || t.min.z < d.min.z - tol;
    }

    // ── 小工具 ─────────────────────────────────────────────────────
    static List<LibraryBook> BooksOnShelves()
    {
        return UnityEngine.Object.FindObjectsOfType<LibraryBook>(true)
            .Where(b => ShelfOf(b.transform) != null).ToList();
    }

    static Transform ShelfOf(Transform t)
    {
        while (t != null)
        {
            if (t.name.StartsWith("Shelf_") || t.name.StartsWith("Shelf2F_")) return t;
            if (t.name == "Bookshelves" || t.name == "Shelves2F") return null;
            t = t.parent;
        }
        return null;
    }

    static T DeepChild<T>(Transform root, string name) where T : Component
    {
        foreach (var c in root.GetComponentsInChildren<T>(true)) if (c.gameObject.name == name) return c;
        return null;
    }

    static string Flat(string x) { return (x ?? "").Replace("\n", ""); }
    static string Chain(GameObject go)
    {
        var s = go.name; var p = go.transform.parent;
        while (p != null) { s = p.name + "/" + s; p = p.parent; }
        return s;
    }
    static void Line(string x) { if (log != null) log.AppendLine(x); }
}
