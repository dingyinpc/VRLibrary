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

    /// <summary>
    /// 以格子标签为权威,把一层 96 本书与 96 个格子一一对应(允许跨架搬书),
    /// 并把每本书的 slotCode 改成它落脚那格的标签文字。
    /// 编号方向已由用户确认:"以书架标签为准"。
    /// </summary>
    [MenuItem("VRLibrary/按标签统一编号(可跨架搬书)")]
    public static void AlignCodesToLabels()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/_Scenes/SmartLibrary.unity")
        {
            Debug.LogError("[VRLibraryFix] 当前场景不是 SmartLibrary,拒绝改动:" + scene.path);
            return;
        }
        log = new StringBuilder();
        Line("按书架标签统一编号  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Physics.SyncTransforms();

        var labels = CollectLabels();
        var books = BooksOnShelves().Where(b => ShelfName(b.transform).StartsWith("Shelf_")).ToList();
        Line("读到格子标签 " + labels.Count + " 个;一层书 " + books.Count + " 本");
        if (labels.Count == 0 || labels.Count != books.Count)
        {
            Line("!! 标签数与书数不等,放弃执行(先跑审计看差异)");
            Write();
            return;
        }

        // ① 判定配对方向:哪个沿架偏移能让最多书在同层找到标签
        float shift = PickShift(books, labels);
        // ② 学出标签→书的三维位移
        Vector3 delta = LearnDelta(books, labels, shift);
        Line(string.Format("采用标签→书位移 ({0:F3},{1:F3},{2:F3}),沿架配对偏移判定为 {3:F2} m", delta.x, delta.y, delta.z, shift));

        // ③ 全局一对一贪心:距离近的先配
        //     不做"同层/同架"限制:每架每层只有 8 格而各层书数不等,限制会让二部图左右不平衡、
        //     有书配不到格子(实测 3 本)。跨层约 0.9 m、跨架更远,贪心按距离排序自然排在最后处理;
        //     而完全二部图上"距离近的先配"必然得到完美匹配。
        var pairs = new List<Pair>();
        foreach (var b in books)
            foreach (var l in labels)
                pairs.Add(new Pair
                {
                    book = b, label = l,
                    d = Vector3.Distance(b.transform.position, l.pos + delta)
                });
        pairs.Sort((a, c) => a.d.CompareTo(c.d));

        var shelfT = new Dictionary<string, Transform>();
        foreach (var b in books) shelfT[ShelfName(b.transform)] = ShelfOf(b.transform);
        foreach (var l in labels)
            if (!shelfT.ContainsKey(l.shelf))
                foreach (var go in UnityEngine.Object.FindObjectsOfType<Transform>(true))
                    if (go.name == l.shelf) { shelfT[l.shelf] = go; break; }

        var takenB = new HashSet<LibraryBook>();
        var takenL = new HashSet<SlotLabel>();
        int movedBooks = 0;
        foreach (var p in pairs)
        {
            if (takenB.Contains(p.book)) continue;
            if (takenL.Contains(p.label)) continue;
            takenB.Add(p.book); takenL.Add(p.label);
            var from = p.book.transform.position;
            var to = p.label.pos + delta;
            if (ShelfName(p.book.transform) != p.label.shelf && shelfT.ContainsKey(p.label.shelf))
                p.book.transform.SetParent(shelfT[p.label.shelf], true);   // 跨架:换父节点,保持世界坐标
            if (Vector3.Distance(from, to) > 0.02f)
            {
                p.book.transform.position = to;
                movedBooks++;
                Line(string.Format("  搬书 {0} {1} → {2} 格 {3}(位移 {4:F2},{5:F2},{6:F2})",
                    p.book.gameObject.name, fmt(from), p.label.shelf, p.label.code,
                    to.x - from.x, to.y - from.y, to.z - from.z));
            }
            Physics.SyncTransforms();
        }
        Line("配对 " + takenB.Count + " / " + books.Count + " 本;实际搬动 " + movedBooks + " 本");

        // ④ 编号 = 落脚格的标签
        int relabeled = 0;
        foreach (var b in books)
        {
            var l = LabelOf(b, labels, delta);
            if (l == null) { Line("  !! " + b.gameObject.name + " 找不到所属标签"); continue; }
            if (b.slotCode != l.code)
            {
                Line(string.Format("  编号 {0} : {1} → {2}（{3}）", b.gameObject.name, b.slotCode, l.code, l.shelf));
                b.slotCode = l.code;
                relabeled++;
            }
            EditorUtility.SetDirty(b);
        }
        Line(string.Format("改编号 {0} 本;搬动 {1} 本", relabeled, movedBooks));

        EditorSceneManager.MarkSceneDirty(scene);
        Line("保存场景:" + (EditorSceneManager.SaveScene(scene) ? "成功" : "失败"));
        Write();
        Debug.Log("[VRLibraryFix] 编号统一完成 → " + kLog);
    }

    class Pair { public LibraryBook book; public SlotLabel label; public float d; }

    class SlotLabel { public string shelf; public string code; public Vector3 pos; }

    static List<SlotLabel> CollectLabels()
    {
        var list = new List<SlotLabel>();
        foreach (var go in UnityEngine.Object.FindObjectsOfType<TextMesh>(true))
        {
            if (!go.gameObject.name.StartsWith("Slot_") || go.gameObject.name.StartsWith("SlotStrip")) continue;
            string txt = (go.text ?? "").Replace("\n", "");
            if (txt.Length < 5) continue;
            var shelf = ShelfOf(go.transform);
            if (shelf == null || !shelf.name.StartsWith("Shelf_")) continue;
            list.Add(new SlotLabel { shelf = shelf.name, code = txt, pos = go.transform.position });
        }
        return list;
    }

    static SlotLabel LabelOf(LibraryBook b, List<SlotLabel> labels, Vector3 delta)
    {
        SlotLabel best = null; float bd = float.MaxValue;
        foreach (var l in labels)
        {
            float d = Vector3.Distance(b.transform.position, l.pos + delta);
            if (d < bd) { bd = d; best = l; }
        }
        return bd < 0.25f ? best : null;
    }

    /// <summary>从实测差值里挑"能让最多书在同层找到标签"的那个沿架偏移。</summary>
    static float PickShift(List<LibraryBook> books, List<SlotLabel> labels)
    {
        var cand = new SortedSet<float>();
        foreach (var b in books)
            foreach (var l in labels)
            {
                if (l.shelf != ShelfName(b.transform)) continue;
                float dz = b.transform.position.z - l.pos.z;
                if (dz > 0.03f && dz < 1.5f) cand.Add(Mathf.Round(dz * 100f) / 100f);
            }
        int bestHits = -1; float bestShift = 0.56f;
        foreach (var s in cand)
        {
            int hits = 0;
            foreach (var b in books)
                foreach (var l in labels)
                    if (l.shelf == ShelfName(b.transform)
                        && Mathf.Abs(l.pos.y - b.transform.position.y) < 0.35f
                        && Mathf.Abs((b.transform.position.z - l.pos.z) - s) <= 0.06f) { hits++; break; }
            if (hits > bestHits) { bestHits = hits; bestShift = s; }
        }
        Line("  方向判定:偏移 " + bestShift + " m 命中 " + bestHits + " 本");
        return bestShift;
    }

    static Vector3 LearnDelta(List<LibraryBook> books, List<SlotLabel> labels, float shift)
    {
        var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
        foreach (var b in books)
            foreach (var l in labels)
            {
                if (l.shelf != ShelfName(b.transform)) continue;
                if (Mathf.Abs((b.transform.position.z - l.pos.z) - shift) > 0.06f) continue;
                if (Mathf.Abs(b.transform.position.y - l.pos.y) > 0.35f) continue;
                xs.Add(b.transform.position.x - l.pos.x);
                ys.Add(b.transform.position.y - l.pos.y);
                zs.Add(b.transform.position.z - l.pos.z);
                break;
            }
        if (xs.Count < 8) return new Vector3(0f, 0.075f, shift);
        return new Vector3(Median(xs), Median(ys), Median(zs));
    }

    static string ShelfName(Transform t)
    {
        var s = ShelfOf(t);
        return s == null ? "" : s.name;
    }

    static string fmt(Vector3 v) { return string.Format("({0:F2},{1:F2},{2:F2})", v.x, v.y, v.z); }

    static float Median(List<float> v)
    {
        var a = v.OrderBy(x => x).ToList();
        return a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) * 0.5f;
    }

    static void Write()
    {
        Directory.CreateDirectory("Logs");
        File.WriteAllText(kLog, log.ToString(), Encoding.UTF8);
    }

    // ──  放大电子屏与书名 ─────────────────────────────────────────
    const float kScreenFillW = 0.86f;     // 屏宽占书面比例(实测原值仅 0.315)
    const float kScreenFillH = 0.66f;     // 屏高占书面比例(实测原值仅 0.138)

    /// <summary>
    /// 书名太小根因不在字号,而在屏幕本身只占书面 31.5% 宽 × 13.8% 高 ——
    /// 书面大片是空金属壳,字放大会先撞到屏边。所以先把屏放大,再让字撑满。
    /// </summary>
    [MenuItem("VRLibrary/放大电子屏与书名")]
    public static void EnlargeScreensAndTitles()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/_Scenes/SmartLibrary.unity")
        {
            Debug.LogError("[VRLibraryFix] 当前场景不是 SmartLibrary,拒绝改动:" + scene.path);
            return;
        }
        log = new StringBuilder();
        Line("放大电子屏与书名  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Physics.SyncTransforms();

        int screenChanged = 0, textChanged = 0;
        foreach (var b in UnityEngine.Object.FindObjectsOfType<LibraryBook>(true))
        {
            var body = b.GetComponent<MeshRenderer>();
            var disp = DeepChild<MeshRenderer>(b.transform, "Display");
            var frame = DeepChild<MeshRenderer>(b.transform, "Frame");
            var tm = DeepChild<TextMesh>(b.transform, "TitleText");
            if (body == null || disp == null || tm == null) continue;

            // ① 屏放大到书面的一定比例。
            //    注意轴向:这些书面朝 ±x 摆放,书的"宽度"落在世界 z 上,世界 x 只有 0.025 的厚度,
            //    所以一律用 lossyScale(局部轴)算,别用世界包围盒。
            var ls = b.transform.lossyScale;              // 立方原语 1 m → 局部 x=宽, y=高
            var dls = disp.transform.lossyScale;
            float wantW = ls.x * kScreenFillW, wantH = ls.y * kScreenFillH;
            if (dls.x > 0.0001f && dls.y > 0.0001f)
            {
                float fx = wantW / dls.x, fy = wantH / dls.y;
                if (Mathf.Abs(fx - 1f) > 0.02f || Mathf.Abs(fy - 1f) > 0.02f)
                {
                    ScaleLocal(disp.transform, new Vector3(fx, fy, 1f));
                    if (frame != null)
                    {
                        var fls = frame.transform.lossyScale;
                        ScaleLocal(frame.transform,
                            new Vector3(wantW * 1.05f / fls.x, wantH * 1.10f / fls.y, 1f));
                    }
                    screenChanged++;
                    Physics.SyncTransforms();
                }
            }

            // ② 在 4~8 字/行里找能塞进新屏面的最大字号
            var mr = tm.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            // 书名取自数据,不沿用屏上旧文字:旧文字曾被 12 字截断过(带省略号),照抄会把截断固化
            string title = (b.title ?? "").Trim().Replace("\n", "");
            if (title.Length == 0) title = Flat(tm.text);
            float bestSize = 0.0015f;                 // 从下界起算:标题变长后各方案的最大字号都会低于当前值
            string bestText = Wrap(title, 6);
            foreach (int per in new[] { 4, 5, 6, 7, 8 })
            {
                string wrapped = Wrap(title, per);
                float lo = 0.0015f, hi = 0.020f;
                for (int it = 0; it < 14; it++)
                {
                    float mid = (lo + hi) * 0.5f;
                    tm.text = wrapped; tm.characterSize = mid;
                    if (Fits(mr.bounds, disp.bounds)) lo = mid; else hi = mid;
                }
                int lines = wrapped.Split('\n').Length;
                int bestLines = bestText.Split('\n').Length;
                if (lo > bestSize + 1e-6f || (Mathf.Abs(lo - bestSize) < 1e-6f && lines < bestLines))
                { bestSize = lo; bestText = wrapped; }
            }
            tm.text = bestText;
            tm.characterSize = bestSize;
            Physics.SyncTransforms();
            textChanged++;
            var nls = disp.transform.lossyScale;
            Line(string.Format("  {0} 书面 {1:F3}×{2:F3} 屏 {3:F3}×{4:F3} 字号→{5:F6} 行数{6} 「{7}」",
                b.gameObject.name, ls.x, ls.y, nls.x, nls.y, bestSize,
                bestText.Split('\n').Length, title));
        }

        Line(string.Format("屏放大 {0} 本;字号重排 {1} 本", screenChanged, textChanged));
        EditorSceneManager.MarkSceneDirty(scene);
        Line("保存场景:" + (EditorSceneManager.SaveScene(scene) ? "成功" : "失败"));
        Write();
        Debug.Log("[VRLibraryFix] 屏与字号调整完成 → " + kLog);
    }

    static void ScaleLocal(Transform t, Vector3 by)
    {
        var s = t.localScale;
        t.localScale = new Vector3(s.x * by.x, s.y * by.y, s.z * by.z);
    }

    /// <summary>
    /// 词边界换行(全场唯一烘焙书名文本的路径)。行宽单位:汉字 1.0、拉丁/数字 0.55。
    ///  · 英文单词与数字串**整块**挪到下一行,中文逐字断行;
    ///  · 一个词本身就比一行还长 → 先在驼峰边界与 `_ - .` 后拆(CinemachineBrain → Cinemachine|Brain),
    ///    拆完还超长才按宽度硬切。
    /// 这里以前是"每 per 个字符硬切",所以 v7.11 提交说明里的智能换行其实**没有留下代码** ——
    /// 重跑本菜单就会把 Cinema/chineB/rain 式腰斩覆盖回场景(2026-09-22 实测过)。
    /// </summary>
    static string Wrap(string s, int per)
    {
        var lines = new List<string>();
        var cur = new StringBuilder();
        float w = 0f;
        System.Action flush = () => { if (cur.Length > 0) { lines.Add(cur.ToString()); cur.Length = 0; } w = 0f; };

        foreach (var t in Tokenize(s))
        {
            float tw = Width(t);
            if (w + tw > per && w > 0f) flush();

            if (tw > per && IsWordRun(t))
            {
                foreach (var piece in Pieces(t))
                {
                    string p = piece;
                    if (p.Length == 0) continue;
                    if (Width(p) > per)
                    {
                        if (Width(p) <= per * 1.10f)
                        {
                            // 只超一成 → 整块独占一行,让下面的二分搜索去压字号。宁可字小,不可把词腰斩。
                            if (w > 0f) flush();
                            lines.Add(p);
                            continue;
                        }
                        while (Width(p) > per)   // 连驼峰块都放不下 → 按宽度硬切
                        {
                            int n = 1;
                            while (n < p.Length && Width(p.Substring(0, n + 1)) <= per) n++;
                            if (w > 0f) flush();
                            lines.Add(p.Substring(0, n));
                            p = p.Substring(n);
                        }
                        if (p.Length == 0) continue;
                    }
                    if (w + Width(p) > per && w > 0f) flush();
                    cur.Append(p); w += Width(p);
                }
                continue;
            }
            cur.Append(t); w += tw;
        }
        flush();
        return string.Join("\n", lines.ToArray());
    }

    /// <summary>全角字符一字一块;拉丁/数字(含 `_ - .`)连续成块;其余标点各自成块。</summary>
    static List<string> Tokenize(string s)
    {
        var l = new List<string>();
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (IsCjk(c)) { l.Add(c.ToString()); i++; continue; }
            if (IsWordChar(c))
            {
                int j = i;
                while (j < s.Length && IsWordChar(s[j])) j++;
                l.Add(s.Substring(i, j - i)); i = j; continue;
            }
            l.Add(c.ToString()); i++;
        }
        return l;
    }

    /// <summary>把一个长词按驼峰边界与 `_ - .` 切成能独立成行的块。</summary>
    static List<string> Pieces(string t)
    {
        var l = new List<string>();
        int start = 0;
        for (int i = 1; i < t.Length; i++)
        {
            char p = t[i - 1], c = t[i];
            bool sep = p == '_' || p == '-' || p == '.' || c == '_' || c == '-' || c == '.';
            bool camel = char.IsUpper(c) && !char.IsUpper(p) && p != '_' && p != '-' && p != '.';
            if (sep || camel) { l.Add(t.Substring(start, i - start)); start = i; }
        }
        if (start < t.Length) l.Add(t.Substring(start));
        return l;
    }

    static bool IsCjk(char c) { return c >= 0x2E80; }
    static bool IsWordChar(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
            || c == '_' || c == '-' || c == '.';
    }
    static bool IsWordRun(string t) { return t.Length > 1 && IsWordChar(t[0]) && !IsCjk(t[0]); }
    static float Width(string t)
    {
        float w = 0f;
        for (int i = 0; i < t.Length; i++) w += IsCjk(t[i]) ? 1f : 0.55f;
        return w;
    }

    static bool Fits(Bounds t, Bounds d)
    {
        const float tol = 0.002f;
        return t.max.y <= d.max.y + tol && t.min.y >= d.min.y - tol
            && t.max.x <= d.max.x + tol && t.min.x >= d.min.x - tol
            && t.max.z <= d.max.z + tol && t.min.z >= d.min.z - tol;
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

    /// <summary>
    /// 四角须知屏里有两块的可读面背对馆心(实测 `GuideBoard`@(+14,+14) 与 `GuideBoard_C3`@(+14,-14),
    /// 而同批 C1/C2 是朝馆心的)→ 游客只能看到背面透过来的镜像字。判据同坑 26:**可读面 = -forward**。
    /// 这批角牌全场没有一行生成代码(手摆的,属遗留 13 那类分叉),所以只能就地把朝向摆正。
    /// </summary>
    [MenuItem("VRLibrary/修正角牌朝向(只改四角须知屏)")]
    public static void FixCornerBoards()
    {
        log = new StringBuilder();
        Line("== 修正角牌朝向 ==");
        var root = GameObject.Find("SmartLibrary");
        if (root == null) { Line("!! 找不到 SmartLibrary 根节点"); WriteLog(); return; }

        int changed = 0, kept = 0, skipped = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("GuideBoard")) continue;
            var cv = t.GetComponent<Canvas>();
            if (cv == null || cv.renderMode != RenderMode.WorldSpace) { skipped++; continue; }
            var p = t.position;
            // 只处理四角那种"离中心远且 |x|≈|z|"的牌;贴在导览台两侧的 GuideBoard_Left/Right 不是角牌
            if (p.magnitude < 10f || Mathf.Abs(Mathf.Abs(p.x) - Mathf.Abs(p.z)) > 1.5f) { skipped++; continue; }

            var before = -t.forward;
            var want = Quaternion.LookRotation(new Vector3(p.x, 0f, p.z));   // forward 背对馆心 ⇒ 可读面朝馆心
            if (Quaternion.Angle(t.rotation, want) < 0.5f) { kept++; continue; }
            Undo.RecordObject(t, "角牌朝向馆心");
            t.rotation = want;
            EditorUtility.SetDirty(t);
            changed++;
            var after = -t.forward;
            Line(string.Format("  !! {0,-18} 可读面 {1} → {2}(世界 {3})",
                Chain(t.gameObject), V3(before), V3(after), p.ToString("F2")));
        }
        EditorSceneManager.MarkSceneDirty(root.scene);
        Line(string.Format("改了 {0} 块,原本就对 {1} 块,不属于角牌 {2} 块 → 保存场景后重跑体检的 M 节复核",
            changed, kept, skipped));
        WriteLog();
    }

    static string V3(Vector3 v) { return string.Format("({0:0.00},{1:0.00},{2:0.00})", v.x, v.y, v.z); }

    /// <summary>追加而不是覆盖:这份日志里还有别的菜单的结果。</summary>
    static void WriteLog()
    {
        if (log == null) return;
        if (!Directory.Exists("Logs")) Directory.CreateDirectory("Logs");
        File.AppendAllText(kLog, log.ToString() + Environment.NewLine + "----" + Environment.NewLine,
            Encoding.UTF8);
        Debug.Log("[VRLibraryFix] " + log);
        log = null;
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
