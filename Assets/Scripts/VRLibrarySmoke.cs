#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using Unity.XR.CoreUtils;

/// <summary>
/// 智能图书馆 · 运行时冒烟测试(仅编辑器内存在,不进包)。
/// 由菜单 VRLibrary/冒烟测试(进Play自动跑) 触发:它写下 Logs/smoke-request.txt 并进 Play,
/// 本组件在场景加载后自检测该文件,存在就跑完整链路,结果写 Logs/runtime-smoke.txt。
///
/// 为什么这么绕:编辑器桥没有 Console 回读通道,也没法直接切 Play,
/// 所以只能"文件进、文件出",并且必须自己抓 Application.logMessageReceived 才能知道有没有报错。
/// </summary>
public class VRLibrarySmoke : MonoBehaviour
{
    const string kRequest = "Logs/smoke-request.txt";
    const string kReport = "Logs/runtime-smoke.txt";

    static StringBuilder rep;
    static List<string> errors;
    int frame0;
    float t0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string request = Abs(kRequest);
        if (!File.Exists(request)) return;
        string req = File.ReadAllText(request).Trim();
        File.Delete(request);                       // 一次性,避免每次进 Play 都跑

        rep = new StringBuilder();
        errors = new List<string>();
        Application.logMessageReceived += OnLog;

        var go = new GameObject("__VRLibrarySmoke__");
        DontDestroyOnLoad(go);
        // 编辑器一失焦/最小化就挂起或节流 Play 循环,而 runMenuItem 自己就会把窗口最小化:
        // 症状是"冒烟跑到第 5 项冻住"或整轮掉到 17 fps 让按时间量的项假失败。
        Application.runInBackground = true;
        var runner = go.AddComponent<VRLibrarySmoke>();
        if (req.StartsWith("preview")) runner.StartCoroutine(runner.Preview(req));
        else runner.StartCoroutine(runner.Run());
    }

    /// <summary>预览模式:只把露台示例片播起来,留一段窗口给远程截图,到点自己退出 Play。</summary>
    IEnumerator Preview(string req)
    {
        float secs = 45f;
        var eq = req.IndexOf('=');
        if (eq > 0) float.TryParse(req.Substring(eq + 1).Split(' ')[0], out secs);

        Line("露台示例片预览  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  计划停留 " + secs + " s");
        frame0 = Time.frameCount; t0 = Time.time;
        yield return new WaitForSeconds(2.5f);          // 等各组件 Awake/Start

        var sc = FindObjectOfType<TerraceScreen>();
        if (sc == null) Fail("预览", "场上没有 TerraceScreen → 跑 VRLibrary/搭建星空露台");
        else
        {
            // 先用观影把人钉到露台座位:相机在座位上,屏上的字才是观众真正看到的那个透视
            var cine = FindObjectOfType<TerraceCinema>();
            if (cine == null) Line("预览:场上没有 TerraceCinema,无法把人放到座位上");
            else
            {
                cine.Enter();
                cine.OnAckNotice();           // 目录自动展开:先抓一张"座位视角的目录"
                yield return new WaitForSeconds(0.8f);
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot("Captures/cinema-eye-catalog.png");
                Line("预览:座位视角目录已抓 → Captures/cinema-eye-catalog.png");
                cine.Leave();                 // 立刻解锁并把三块面板收干净,只留人在座位上
                yield return new WaitForSeconds(0.5f);
            }

            // 信息卡含"解码路线"那行,开播后会被藏掉,所以趁它还挂着先抓一张
            var f0 = TerraceScreen.Films.Length > 0 ? TerraceScreen.Films[0] : null;
            if (f0 != null)
            {
                sc.ShowInfoCard(f0);
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot("Captures/cinema-decode-card.png");
                Line("预览:信息卡(含解码行)已抓 → Captures/cinema-decode-card.png  " + sc.DecodeLine);
            }

            sc.PlayFile(f0 != null ? f0.fileName : "", () => { if (sc.infoCard != null) sc.infoCard.SetActive(false); });
            yield return new WaitForSeconds(4f);
            Line("预览:出帧=" + sc.Decoding + " 帧号=" + sc.FrameIndex + " RT平均亮度="
                 + sc.AverageLuma().ToString("0.000") + " 画面 " + sc.FaceWidth.ToString("0.00") + "×"
                 + sc.FaceHeight.ToString("0.00") + " m(屏框 " + sc.PanelWidth.ToString("0.00") + "×"
                 + sc.PanelHeight.ToString("0.00") + " m)"
                 + " 实测 " + sc.MeasuredFps.ToString("0.0") + " / 源 " + sc.SourceFps.ToString("0.0") + " fps");
            DumpRt(sc, "rt-screen");

            // 只用引擎自己的 ScreenCapture:GameView 帧末由引擎写盘,不存在"看不见运行时改动"的疑点
            if (cine != null)
            {
                yield return new WaitForEndOfFrame();
                ScreenCapture.CaptureScreenshot("Captures/terrace-playing-seat.png");
                yield return new WaitForSeconds(1.5f);
                Line("预览:已用 ScreenCapture 抓玩家视角 → Captures/terrace-playing-seat.png");
            }
        }
        Flush();

        while (Time.time - t0 < secs) yield return null;
        Finish();
    }

    void Finish()
    {
        int dFrames = Time.frameCount - frame0;
        float secs = Time.time - t0;
        float fps = secs > 0.01f ? dFrames / secs : 0f;
        Line("");
        Line("帧数推进 " + dFrames + ",时间推进 " + secs.ToString("F2") + " s,平均 " + fps.ToString("0.0") + " fps"
             + (dFrames < 30 ? "   ← 游戏循环几乎没跑,本次结果不可信!" : "")
             + (dFrames >= 30 && fps < 30f ? "   ← 平均帧率低于 30:编辑器多半失焦被节流了(Unity 会把后台游戏循环压到十几帧),"
                 + "解码吞吐/播放进度这类按时间量的项会假失败。先跑 Tools/restore-window.ps1 再重跑,别当回归查" : ""));
        Line("捕获到的报错/异常 " + errors.Count + " 条");
        foreach (var e in errors) Line("  ! " + e);
        Flush();
        Application.logMessageReceived -= OnLog;

        Debug.Log("[Smoke] 完成 → " + kReport);
        UnityEditor.EditorApplication.isPlaying = false;
        Destroy(gameObject);
    }

    /// <summary>把 RT 导成 PNG。**亮度数字会骗人**(未初始化显存也可能很亮),看图不会。</summary>
    static void DumpRt(TerraceScreen sc, string tag)
    {
        var src = sc.Target;
        if (src == null) { Line("  " + tag + ":RT 为空,没得导"); return; }
        var probe = RenderTexture.GetTemporary(256, 144, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, probe);
        var tex = new Texture2D(256, 144, TextureFormat.RGBA32, false);
        var prev = RenderTexture.active;
        RenderTexture.active = probe;
        tex.ReadPixels(new Rect(0, 0, 256, 144), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(probe);
        try
        {
            string dir = Abs("Captures");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(System.IO.Path.Combine(dir, tag + ".png"), tex.EncodeToPNG());
            Line("  已导 Captures/" + tag + ".png");
        }
        catch (Exception e) { Line("  导 RT 失败:" + e.Message); }
        Destroy(tex);
    }

    static void OnLog(string cond, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        if (cond.Contains("__VRLibrarySmoke__")) return;
        errors.Add(type + ": " + cond + "\n         " + First(stack));
    }
    static string First(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var ls = s.Split('\n');
        return ls.Length > 1 ? ls[1].Trim() : ls[0].Trim();
    }

    IEnumerator Run()
    {
        // 区域剔除/文字剔除是**画面优化**,冒烟测的是**逻辑内容**:整片渲染器被关掉时,
        // 二层书架与露台影院在画面里是"空的",容易把这轮测试引到假失败上。这里一律让开。
        LibraryZoneCuller.ForceAll = true;
        Line("运行时冒烟测试  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Line("Unity " + Application.unityVersion + "   场景 " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
        frame0 = Time.frameCount; t0 = Time.time;

        yield return new WaitForSeconds(1.5f);       // 等各组件 Awake/Start 跑完

        yield return TestReader();
        yield return TestSearchAndTeleport();
        yield return TestKioskTeleport();
        yield return TestLights();
        yield return TestDayNight();
        yield return TestKioskButtons();
        TestWiring();
        yield return TestCinema();
        yield return TestHud();          // 放最后:它会反复把玩家传送走

        Flush();
        yield return new WaitForSeconds(0.3f);
        Finish();
    }

    // ── 各项 ───────────────────────────────────────────────────────
    IEnumerator TestReader()
    {
        yield return null;
        var r = BookReader.Instance;
        if (r == null) { Fail("阅读面板", "场景里没有 BookReader 实例"); yield break; }

        LibraryBook book = null;
        foreach (var b in FindObjectsOfType<LibraryBook>())
            if (b.summary != null && b.summary.Length > 500) { book = b; break; }
        if (book == null) { Fail("阅读面板", "找不到一本有正文的书(summary>500)"); yield break; }

        int bmBefore = BookReader.Bookmarks.Count;
        bool wasOpen = r.panelRoot.activeSelf;
        r.Open(book);
        bool opened = r.panelRoot.activeSelf && !wasOpen;
        string body = r.contentText != null ? r.contentText.text : "";
        string head = r.titleText != null ? r.titleText.text : "";
        string p0 = r.pageText != null ? r.pageText.text : "";
        // 书签可能把书恢复到末页(末页 NextPage 无动作属正常):先跳回首页再测翻页前进
        var pageField = typeof(BookReader).GetField("page", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (pageField != null) pageField.SetValue(r, 0);
        var refreshM = typeof(BookReader).GetMethod("Refresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (refreshM != null) refreshM.Invoke(r, new object[] { false });
        string pHome = r.pageText != null ? r.pageText.text : "";
        r.NextPage(); r.NextPage();
        yield return new WaitForSeconds(0.35f);
        string p1 = r.pageText != null ? r.pageText.text : "";
        string bodyAfterFlip = r.contentText != null ? r.contentText.text : "";
        var pagesArr = typeof(BookReader).GetField("pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(r) as string[];
        bool flipSyncOk = pagesArr != null && pagesArr.Length >= 3 && bodyAfterFlip == pagesArr[2];
        r.Close();
        bool closed = !r.panelRoot.activeSelf;
        int bmAfter = BookReader.Bookmarks.Count;

        Check("阅读面板", opened && body.Length > 20 && closed,
            "《" + book.title + "》面板=" + opened + " 正文" + body.Length + "字 页码\"" + p0 + "\"→\"" + p1
            + "\" 关合=" + closed + " 书签 " + bmBefore + "→" + bmAfter);
        Check("阅读面板·翻页", pHome != p1 && flipSyncOk, "首页\"" + pHome + "\"→连点前进两页\"" + p1 + "\" 正文同步=" + flipSyncOk);

        // 2026-09-28 用户报"点书点不开":这里走一次**真·射线选中**(XRInteractionManager.SelectEnter),
        // 把"输入/层遮罩"与"逻辑"分开定位 —— 抓取走的是手碰碰撞体,不需要射线,所以以前能抓不代表射线打得中。
        var mgr = FindObjectOfType<UnityEngine.XR.Interaction.Toolkit.XRInteractionManager>();
        var inter = book.GetComponent<UnityEngine.XR.Interaction.Toolkit.XRBaseInteractable>();
        var rays = FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.XRRayInteractor>();
        var ray = rays.Length > 0 ? rays[0] : null;
        bool selOpened = false; string why;
        if (mgr == null) why = "场上没有 XRInteractionManager";
        else if (inter == null) why = "书上没有可交互组件";
        else if (ray == null) why = "场上没有 XRRayInteractor";
        else
        {
            r.Close();
            bool can = ray.CanSelect(inter);
            mgr.SelectEnter(ray, inter);
            selOpened = r.panelRoot.activeSelf;
            mgr.SelectExit(ray, inter);
            r.Close();
            why = ray.name + " 可选中=" + can
                + " 射线raycastMask=" + ray.raycastMask.value + " 书层=" + book.gameObject.layer
                + " 交互器层=" + ray.interactionLayers.value + " 可交互层=" + inter.interactionLayers.value
                + " → 面板=" + selOpened;
        }
        Check("书籍点击链路(射线选中→开面板)", selOpened, why);

        // 射线**打得中**吗:对抽样书各自"站到书正前方 1.2m"打一条射线,看第一阻挡是谁。
        // 抓取走的是手碰碰撞体(overlap),点击要靠射线 —— 书架的前沿/挡板完全可能挡在书前面,
        // 这就是"以前能抓、现在点不开"的最可能原因。(上一次从相机正前方打,目标在 19.2m 外,测的是射程不是遮挡)
        int scanned = 0, sampled = 0, reachable = 0; var blockers = new List<string>();
        foreach (var b in FindObjectsOfType<LibraryBook>())
        {
            if (b.GetComponent<Collider>() == null) continue;
            if (scanned % 12 != 0) { scanned++; continue; }   // 抽样:每 12 本取 1 本
            scanned++; sampled++;
            var p = b.transform.position;
            // 从 4 个水平方向各试一次。**判据就是用户那句话:射线碰到书就该算数** ——
            // 命中物必须是这本书自己(书架结构已被挪到射线遮罩之外的层,不该再挡路)。
            bool ok = false; string block = "";
            foreach (var d in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
            {
                var origin = p + d * 1.5f;
                RaycastHit h;
                bool hit = Physics.Raycast(origin, (p - origin).normalized, out h, 3.5f, ~0, QueryTriggerInteraction.Ignore);
                if (!hit) { if (block.Length == 0) block = "打空"; continue; }
                if (h.collider.GetComponentInParent<LibraryBook>() == b) { ok = true; break; }
                if (block.Length == 0) block = h.collider.name + "@" + h.distance.ToString("0.00") + "m";
            }
            if (ok) reachable++;
            else if (blockers.Count < 4) blockers.Add("《" + b.title + "》被 " + block + " 挡住");
        }
        Check("射线能打到书(书架不挡)", sampled > 0 && reachable == sampled,
            "全场 " + scanned + " 本,抽样 " + sampled + " 本 → 射线直达 " + reachable + " 本"
            + (blockers.Count > 0 ? " · 反例:" + string.Join(" | ", blockers) : " · 全通"));

        // 空气墙自证:上一版我加的隐形点击板(层 3 的实体碰撞体)必须一个都不剩 ——
        // 用户 2026-09-28 报"被空气墙挡住",根因就是它。
        int ghost = 0;
        foreach (var c in FindObjectsOfType<Collider>())
            if (c.gameObject.layer == 3) ghost++;
        Check("没有隐形空气墙(层 3 无碰撞体)", ghost == 0, "场上 layer 3 的碰撞体 " + ghost + " 个(应为 0)");

        // 用户真正的那一环:**书自己的碰撞体被选中 → 会不会开书**
        var sampleBook = FindObjectsOfType<LibraryBook>()[0];
        bool bookOpened = false; string bookWhy;
        var bi = sampleBook.GetComponent<UnityEngine.XR.Interaction.Toolkit.XRBaseInteractable>();
        if (mgr == null || ray == null || bi == null) bookWhy = "缺少 InteractionManager/射线/书上的可交互组件";
        else
        {
            r.Close();
            bool can = ray.CanSelect(bi);
            mgr.SelectEnter(ray, bi);
            bookOpened = r.panelRoot.activeSelf;
            mgr.SelectExit(ray, bi);
            r.Close();
            bookWhy = "《" + sampleBook.title + "》层=" + sampleBook.gameObject.layer
                + " 可选中=" + can + " → 面板=" + bookOpened;
        }
        Check("书自身可点开(射线碰到书就开)", bookOpened, bookWhy);

        // 2026-09-29 用户报"书签占住了上一页的效果,没有办法回到上一页,全流程卡住"。
        // 就地量出来的真相:底排的 `Btn_Bookmark` 与 `Btn_Prev` 锚点/位置/尺寸**逐像素相同**
        // (都是 anchor 0.06→0.3、pos(0,39)、158.4×50),而书签键在层级里排在上一页键**之后**
        // ⇒ uGUI 后画的先吃射线,点"上一页"永远落在书签键上。
        // 这类"布局撞车"光比坐标容易看漏(两个键各自都合法),所以这里不认死值:
        // **借 EventSystem 真打一枪 UI 射线,看点下去到底落到谁头上** —— 也顺手截一张玩家视角的图。
        r.Open(sampleBook);
        yield return null;                    // 让 BookReader.Update 把面板摆到眼前
        yield return null;
        string layWhy; bool layOk = ProbeReaderRow(r, out layWhy);
        var shotPath = Abs("Captures/reader-layout.png");                     // 玩家眼里的阅读面板
        Directory.CreateDirectory(Path.GetDirectoryName(shotPath));
        ScreenCapture.CaptureScreenshot(shotPath);
        yield return new WaitForSeconds(0.4f);
        Check("阅读面板按钮互不遮挡(上一页点得到)", layOk, layWhy);
        r.Close();
    }

    /// <summary>
    /// 认一个按钮:它的持久回调打到 `BookReader.<method>`。走公开的 UnityEvent 反射口,
    /// 不依赖 UnityEditor(冒烟是运行时组件),名字改了也不会认错。
    /// </summary>
    static Button BtnByMethod(BookReader r, string method)
    {
        foreach (var b in r.GetComponentsInChildren<Button>(true))
        {
            var e = b.onClick;
            for (int i = 0; i < e.GetPersistentEventCount(); i++)
                if (e.GetPersistentTarget(i) == (UnityEngine.Object)r && e.GetPersistentMethodName(i) == method)
                    return b;
        }
        return null;
    }

    /// <summary>
    /// 面板直接子级里"会吃射线的图形"的本地矩形(锚点反算,和编辑器菜单同一套算法)。
    /// </summary>
    static Rect LocalRect(RectTransform rt)
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

    /// <summary>这个对象(或它的子级)有没有"会吃射线的图形"。</summary>
    static bool EatsRay(RectTransform rt)
    {
        foreach (var g in rt.GetComponentsInChildren<Graphic>(true))
            if (g.raycastTarget) return true;
        return rt.GetComponent<Button>() != null;
    }

    /// <summary>
    /// 底排三个可点键"点下去落到谁头上"。
    ///
    /// ⚠ 这里**故意不用 `EventSystem.RaycastAll` + `WorldToScreenPoint`**:2026-09-29 实测它在本工程
    /// 会给出错误结论 —— PICO 的 XR 眼睛投影矩阵是**非对称**的,`Camera.main.WorldToScreenPoint`
    /// 与真正渲染用的投影差着一个垂直平移,底排三个键整体被算高约 46px(截图比对),于是三条射线
    /// 全打空。错的是测量,不是布局。所以改成量**同一块画布内的几何 + 绘制次序**:
    ///   uGUI 的命中规则就是"同一画布里**后画的先中**",所以只要某个更晚绘制、且会吃射线的兄弟
    ///   盖住了这个键的中心,这个键就点不到。原事故(书签键与上一页键矩形逐像素重合、且排在后面)
    ///   被这条判据一击命中。
    /// </summary>
    static bool ProbeReaderRow(BookReader r, out string why)
    {
        var panel = (r.panelRoot != null ? r.panelRoot : r.gameObject).GetComponent<RectTransform>();
        if (panel == null) { why = "找不到面板 RectTransform"; return false; }

        var prev = BtnByMethod(r, "PrevPage");
        var next = BtnByMethod(r, "NextPage");
        var bmk = r.bookmarkBtn;
        if (prev == null || next == null || bmk == null)
        {
            why = "底排键认不齐(上一页=" + (prev != null) + " 书签=" + (bmk != null) + " 下一页=" + (next != null) + ")";
            return false;
        }

        // 面板直接子级里会吃射线的那些,按层级次序(次序 = 绘制次序 = 命中优先级)
        var blockers = new List<RectTransform>();
        for (int i = 0; i < panel.childCount; i++)
        {
            var c = panel.GetChild(i) as RectTransform;
            if (c == null || !c.gameObject.activeInHierarchy) continue;
            if (EatsRay(c)) blockers.Add(c);
        }

        var btns = new List<Button> { prev, bmk, next };
        var names = new List<string> { "上一页", "书签", "下一页" };
        var sb = new StringBuilder();
        int okCount = 0;
        for (int i = 0; i < btns.Count; i++)
        {
            var rt = btns[i].GetComponent<RectTransform>();
            var mine = LocalRect(rt);
            var c0 = new Vector2(mine.center.x, mine.center.y);
            string cover = "";
            int myIdx = blockers.IndexOf(rt);
            for (int j = 0; j < blockers.Count; j++)
            {
                if (blockers[j] == rt) continue;
                if (myIdx >= 0 && j < myIdx) continue;                  // 更早画的被它盖住,不算它被盖
                if (LocalRect(blockers[j]).Contains(c0)) { cover = blockers[j].name; break; }
            }
            bool self = cover.Length == 0;
            if (self) okCount++;
            sb.Append(names[i] + "(" + rt.name + " 矩形 x" + mine.xMin.ToString("0") + "–" + mine.xMax.ToString("0")
                      + " y" + mine.yMin.ToString("0") + "–" + mine.yMax.ToString("0") + ")"
                      + (self ? " ✓ 没被盖" : " ✗ 中心被「" + cover + "」盖住"));
            if (i < btns.Count - 1) sb.Append(" · ");
        }
        sb.Append("  [同一画布内后画的先中,判据就是这一条]");
        why = sb.ToString();
        return okCount == btns.Count;
    }

    IEnumerator TestSearchAndTeleport()
    {
        // 该对象初始未激活(对象池式预建),Instance 要等第一次激活才在 Awake 里赋值,
        // 所以必须 includeInactive 找组件,再像按钮那样直接调 Show()
        var sc = FindObjectOfType<BookSearchController>(true);
        if (sc == null) { Fail("检索传送", "场景里找不到 BookSearchController 组件"); yield break; }
        if (sc.inputField == null) { Fail("检索传送", "inputField 未接"); yield break; }

        sc.Show();
        sc.inputField.text = "实现方案";
        sc.DoSearch();
        int shown = 0;
        Button first = null;
        foreach (var go in sc.resultPool)
            if (go != null && go.activeSelf)
            {
                shown++;
                if (first == null) first = go.GetComponent<Button>();
            }
        Check("检索出结果", shown > 0, "\"实现方案\" 命中并激活 " + shown + " 条");

        if (first == null || sc.origin == null) { Fail("检索传送", "结果条目没挂 Button 或 origin 未接"); yield break; }

        var o = sc.origin.transform;
        var from = o.position;
        first.onClick.Invoke();
        yield return new WaitForSeconds(0.2f);
        float moved = Vector3.Distance(from, o.position);

        LibraryBook target = null;
        float best = float.MaxValue;
        foreach (var b in FindObjectsOfType<LibraryBook>())
        {
            if (b.title == null || !b.title.Contains("实现方案")) continue;
            float d = Vector3.Distance(b.transform.position, o.position);
            if (d < best) { best = d; target = b; }
        }
        Check("检索→传送到书前", moved > 0.5f && target != null && best < 3.5f,
            "Origin 移动 " + moved.ToString("F2") + " m;最近目标《" + (target != null ? target.title : "?")
            + "》距离 " + best.ToString("F2") + " m");
        Check("传送后有发光标记", GameObject.Find("SearchMarker") != null, "SearchMarker 存在(10 秒后自动销毁)");

        // 跨层检索回归检查:找一本二层书搜索传送,验证 Rig Y 抬到二层(≈4.43)而非留在 0
        LibraryBook book2F = null;
        foreach (var b in FindObjectsOfType<LibraryBook>())
            if (b.transform.position.y > 2.5f && !string.IsNullOrEmpty(b.title)) { book2F = b; break; }
        if (book2F != null)
        {
            sc.Show();
            sc.inputField.text = book2F.title;
            sc.DoSearch();
            Button btn2F = null;
            foreach (var go in sc.resultPool)
                if (go != null && go.activeSelf) { btn2F = go.GetComponent<Button>(); break; }
            if (btn2F != null) btn2F.onClick.Invoke();
            yield return new WaitForSeconds(0.2f);
            float y2F = o.position.y;
            sc.Show();
            sc.inputField.text = "实现方案";
            sc.DoSearch();
            Button btn1F = null;
            foreach (var go in sc.resultPool)
                if (go != null && go.activeSelf) { btn1F = go.GetComponent<Button>(); break; }
            if (btn1F != null) btn1F.onClick.Invoke();
            yield return new WaitForSeconds(0.2f);
            float y1F = o.position.y;
            Check("跨层检索传送高度(1F↔2F)", y2F > 4.0f && y1F < 0.5f,
                "二层书《" + book2F.title + "》落点 y=" + y2F.ToString("0.00") + "m → 返回一层 y=" + y1F.ToString("0.00") + "m");
        }
    }

    IEnumerator TestKioskTeleport()
    {
        // 场上现在有多份 KioskTeleporter(中央台 1 份 + 露台面板回程 2 份),
        // FindObjectOfType 会随机抓到露台面板那份 → 上一版因此假报"距目标点 3.05 m"。
        KioskTeleporter kt = null;
        foreach (var c in FindObjectsOfType<KioskTeleporter>())
            if (c.gameObject.name == "CentralKiosk") { kt = c; break; }
        if (kt == null) kt = FindObjectOfType<KioskTeleporter>();
        if (kt == null) { Fail("导览屏传送", "场景里没有 KioskTeleporter"); yield break; }
        var o = kt.origin != null ? kt.origin.transform : null;
        if (o == null) { Fail("导览屏传送", "origin 未接"); yield break; }
        var from = o.position;
        kt.TeleportToDestination();
        yield return new WaitForSeconds(0.2f);
        // 按水平距离量:TeleportToDestination 故意不改 Rig 的 Y(那由地板/房间标定决定),
        // 拿 3D 距离会把"目标点比当前高 3 m"的锚点(露台)算成没到位。
        float moved = Vector2.Distance(new Vector2(from.x, from.z), new Vector2(o.position.x, o.position.z));
        float toDest = kt.destination != null
            ? Vector2.Distance(new Vector2(o.position.x, o.position.z),
                               new Vector2(kt.destination.position.x, kt.destination.position.z)) : -1f;
        Check("导览屏一键传送", moved > 0.5f && (toDest < 0 || toDest < 1f),
            "用的是 " + kt.gameObject.name + " 上那份;Origin 水平移动 " + moved.ToString("F2")
            + " m,距目标点(水平) " + toDest.ToString("F2") + " m");
    }

    IEnumerator TestLights()
    {
        var dn = DayNightController.Instance;
        if (dn == null) { Fail("灯光开关", "没有 DayNightController 实例"); yield break; }

        // ToggleLights 改的是"非平行光的 enabled",不是 intensity;别抓太阳灯
        Light key = null;
        foreach (var l in FindObjectsOfType<Light>())
            if (l.type != LightType.Directional) { key = l; break; }
        if (key == null) { Fail("灯光开关", "场景里没有非平行光可测"); yield break; }

        int total = 0;
        foreach (var l in FindObjectsOfType<Light>()) if (l.type != LightType.Directional) total++;

        bool on0 = dn.lightsOn, e0 = key.enabled;
        dn.ToggleLights();
        yield return new WaitForSeconds(0.35f);
        bool e1 = key.enabled;
        dn.ToggleLights();
        yield return new WaitForSeconds(0.35f);
        bool e2 = key.enabled;

        Check("灯光总开关", dn.lightsOn == on0 && e0 != e1 && e2 == e0,
            "受控非平行光 " + total + " 盏;样本灯 enabled " + e0 + "→" + e1 + "→" + e2
            + "(关后应变、开回应复原)");
    }

    IEnumerator TestDayNight()
    {
        var dn = DayNightController.Instance;
        if (dn == null) { Fail("日夜切换", "没有实例"); yield break; }

        bool d0 = dn.isDay;
        Color env0 = RenderSettings.ambientLight;
        dn.ToggleDayNight();
        yield return new WaitForSeconds(dn.blendSeconds + 0.6f);
        Color env1 = RenderSettings.ambientLight;
        float envDelta = Mathf.Sqrt(
            Mathf.Pow(env1.r - env0.r, 2) + Mathf.Pow(env1.g - env0.g, 2) + Mathf.Pow(env1.b - env0.b, 2));
        Check("日夜渐变", dn.isDay != d0 && envDelta > 0.02f,
            "isDay " + d0 + "→" + dn.isDay + ";环境光 RGB 变化 " + envDelta.ToString("F3")
            + " (" + env0.ToString() + " → " + env1.ToString() + ")");
        dn.ToggleDayNight();                                   // 复位
        yield return new WaitForSeconds(dn.blendSeconds + 0.6f);

        // v7.17 起 DayNightController.Awake 会克隆一份运行时材质挂回 RenderSettings.skybox,
        // 磁盘那份 .mat 不再被写。旧版这里必须强制重导入才能擦掉白天参数 —— 因为 Play 里对"资产"的
        // 修改退出后不回滚,而且不能靠"读回来再写回去"复原:实测 GetColor→SetColor→SaveAssets 仍会让
        // _ZenithColor 从 0.044999883 漂成 0.04499986,Unity 序列化 float 本身不幂等。
        // 现在改成正向断言(遗留 14 的回归闸):
        var sky = RenderSettings.skybox;
        bool instance = sky != null && string.IsNullOrEmpty(UnityEditor.AssetDatabase.GetAssetPath(sky));
        Check("日夜不再污染天空盒资产", instance,
            sky == null ? "场上没有天空盒材质" : "RenderSettings.skybox = " + sky.name
            + (instance ? "(运行时实例,磁盘资产未被动)"
                        : "(仍是磁盘资产本身 → 这次写入会永久留在 .mat 里!)"));
    }

    /// <summary>
    /// 导览屏上每颗按钮逐个 `onClick.Invoke`。前面几项测的是"直接调控制器方法",这一项测的是
    /// **"屏上那颗按钮真的绑到了那个方法"**(持久化事件 + 目标对象在位) —— 扳机射线之后剩下的那一环。
    /// 射线命中本身在编辑器里自动验不了(要 XR Device Simulator 或真机),这里只能到"接线 + 运行时"层。
    /// </summary>
    IEnumerator TestKioskButtons()
    {
        var ui = LibraryUIController.Instance;
        var dn = DayNightController.Instance;
        KioskTeleporter kt = null;   // 兜底参照优先取中央台那份,别随机抓露台的
        foreach (var c in FindObjectsOfType<KioskTeleporter>())
            if (c.gameObject.name == "CentralKiosk") { kt = c; break; }
        if (kt == null) kt = FindObjectOfType<KioskTeleporter>();
        var rig = kt != null && kt.origin != null ? kt.origin.transform : null;
        BookSearchController bsc = null;   // 检索控制器初始未激活,FindObjectOfType 找不到(见 TestSearchAndTeleport 的注释)
        foreach (var c in Resources.FindObjectsOfTypeAll<BookSearchController>())
            if (c.hideFlags == HideFlags.None && c.gameObject.scene.IsValid()) { bsc = c; break; }
        var homePos = rig != null ? rig.position : Vector3.zero;
        var cine = FindObjectOfType<TerraceCinema>();   // "星空观影"会锁站位,逐颗复位时要先解锁

        int ok = 0, total = 0;
        var miss = new List<string>();
        var binds = new List<string>();
        foreach (var canvasName in new[] { "InfoCanvas", "TerracePanel" })
        {
            var canvas = GameObject.Find(canvasName);
            if (canvas == null) { miss.Add(canvasName + " 整块不在场上了"); continue; }
            var btns = canvas.GetComponentsInChildren<Button>(true);
            if (btns.Length == 0) { miss.Add(canvasName + " 上没有 Button"); continue; }

            foreach (var btn in btns)
            {
                total++;
                // 每颗都从"已知的起始态"开始点:前一项测试已经把玩家传到过目的地,
                // 不挪开再点传送按钮位移自然是 0,会被误判成"按钮没反应"。
                if (bsc != null) bsc.Hide();
                var own = btn.GetComponent<KioskTeleporter>();
                var parkFrom = own != null && own.destination != null ? own.destination : (kt != null ? kt.destination : null);
                if (rig != null && parkFrom != null) rig.position = parkFrom.position + new Vector3(0f, 0f, -4f);
                yield return new WaitForSeconds(0.1f);

                string label = canvasName + "/" + "\"" + Label(btn) + "\"";
                string st0 = ui != null && ui.statusText != null ? ui.statusText.text : "";
                bool lights0 = dn != null && dn.lightsOn, day0 = dn != null && dn.isDay;
                var pos0 = rig != null ? rig.position : Vector3.zero;
                bool sp0 = bsc != null && bsc.panelRoot != null && bsc.panelRoot.activeSelf;
                bool ch0 = bsc != null && bsc.canvasToHide != null && bsc.canvasToHide.activeSelf;

                int n = btn.onClick.GetPersistentEventCount();
                var tb = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    var t = btn.onClick.GetPersistentTarget(i);
                    if (i > 0) tb.Append(';');
                    tb.Append(t == null ? "空" : t.GetType().Name).Append('.').Append(btn.onClick.GetPersistentMethodName(i));
                }
                string tgt = n == 0 ? "无" : tb.ToString();
                binds.Add(label + "→" + tgt);

                btn.onClick.Invoke();
                yield return new WaitForSeconds(0.7f);

                bool moved = rig != null && Vector3.Distance(rig.position, pos0) > 0.5f;
                bool textChanged = ui != null && ui.statusText != null && ui.statusText.text != st0;
                bool flip = dn != null && (dn.lightsOn != lights0 || dn.isDay != day0);
                bool swap = (bsc != null && bsc.panelRoot != null && bsc.panelRoot.activeSelf != sp0)
                            || (bsc != null && bsc.canvasToHide != null && bsc.canvasToHide.activeSelf != ch0);
                if (moved || textChanged || flip || swap) ok++;
                else miss.Add(label + (n == 0 ? "(没绑事件)" : "(绑了 " + tgt + " 但场上没看到变化)"));

                // 复原,别把后面的按钮和场上状态带偏
                if (dn != null)
                {
                    if (dn.lightsOn != lights0) dn.ToggleLights();
                    if (dn.isDay != day0) { dn.ToggleDayNight(); yield return new WaitForSeconds(dn.blendSeconds + 0.4f); }
                }
                if (ui != null && ui.statusText != null && textChanged) ui.statusText.text = st0;
                if (bsc != null) bsc.Hide();
                if (cine != null && cine.IsSeated) cine.Leave();   // 观影锁站位会把下一颗按钮的位移观测吃掉
            }
        }
        if (rig != null) rig.position = homePos;
        Check("屏上按钮逐个可触发(" + total + " 颗)", miss.Count == 0,
            "点了有反应 " + ok + "/" + total + (miss.Count == 0 ? "" : "  没反应:" + string.Join("、", miss))
            + "  绑定:" + string.Join(" | ", binds));
    }

    /// <summary>
    /// 星空观影全链路,**按用户给的操作顺序实走一遍**:进入观影区 → 开目录 → 开须知 → 关须知 →
    /// 关目录 → 再开目录 → 再开须知 → 选片观看 → 跳 5 秒 → 暂停 → 关影片 → 验硬锁站位 → 回一层大厅。
    /// 放在最后一项:它会锁站位、改 PlayerPrefs,跑完自己复原。
    /// </summary>
    IEnumerator TestCinema()
    {
        var c = FindObjectOfType<TerraceCinema>();
        if (c == null) { Fail("星空观影链路", "场上没有 TerraceCinema → 跑 VRLibrary/搭建星空露台"); yield break; }
        var rig = c.origin != null ? c.origin.transform : null;
        if (rig == null) { Fail("星空观影链路", "cinema.origin 没接"); yield break; }
        var sc = c.screen;
        var homePos = rig.position;

        // ① 进入观影区(第一次必须先过须知)
        PlayerPrefs.DeleteKey(c.ackKey);
        PlayerPrefs.Save();
        c.Enter();
        yield return new WaitForSeconds(0.15f);
        Check("观影·进入观影区(锁站位 + 先弹须知)",
              c.IsSeated && SprintController.suppressed && c.NoticeVisible && !c.CatalogVisible,
              "在座=" + c.IsSeated + " 加速已抑制=" + SprintController.suppressed
            + " 须知=" + c.NoticeVisible + " 目录=" + c.CatalogVisible);

        // ② 关闭使用须知(确认)→ 目录自动展开
        c.OnAckNotice();
        yield return new WaitForSeconds(0.15f);
        Check("观影·关闭使用须知(确认过后目录展开)",
              PlayerPrefs.HasKey(c.ackKey) && !c.NoticeVisible && c.CatalogVisible,
              "PlayerPrefs 有记录=" + PlayerPrefs.HasKey(c.ackKey)
            + " 须知=" + c.NoticeVisible + " 目录=" + c.CatalogVisible);

        // ③ 关闭目录
        c.CloseCatalog();
        yield return new WaitForSeconds(0.15f);
        Check("观影·关闭目录", !c.CatalogVisible, "目录=" + c.CatalogVisible);

        // ④ 再打开目录
        c.OpenCatalog();
        yield return new WaitForSeconds(0.15f);
        Check("观影·再打开目录", c.CatalogVisible, "目录=" + c.CatalogVisible);

        // ⑤ 再打开使用须知(回看,不该再卡流程)
        c.OnShowNotice();
        yield return new WaitForSeconds(0.15f);
        Check("观影·回看使用须知", c.NoticeVisible && !c.CatalogVisible,
            "须知=" + c.NoticeVisible + " 目录(应让位)=" + c.CatalogVisible);

        // ⑥ 再关闭须知 → 目录回来
        c.OnAckNotice();
        yield return new WaitForSeconds(0.15f);
        Check("观影·再次关闭须知后目录回到原位", !c.NoticeVisible && c.CatalogVisible,
            "须知=" + c.NoticeVisible + " 目录=" + c.CatalogVisible);

        // ⑦ 目录内容确实来自片单
        var films = TerraceScreen.Films;
        string lab0 = c.filmLabels != null && c.filmLabels.Length > 0 && c.filmLabels[0] != null
            ? c.filmLabels[0].text : "";
        Check("观影·目录被片单灌上", films.Length >= 3 && lab0.Contains(films[0].title),
            "片单 " + films.Length + " 条,首槽=\"" + lab0 + "\"");

        // ⑧ 选片 → 信息卡上屏、目录自动收起
        c.OnSelectFilm0();
        yield return new WaitForSeconds(0.2f);
        bool cardOn = sc != null && sc.infoCard != null && sc.infoCard.activeSelf;
        Check("观影·选片上屏并自动收目录", c.CurrentFilm == 0 && !c.CatalogVisible && cardOn,
            "片号=" + c.CurrentFilm + " 目录已收=" + !c.CatalogVisible + " 信息卡=" + cardOn
            + " 应为《" + (films.Length > 0 ? films[0].title : "?") + "》");

        // ⑧b 播片之前先把解码路线问明白:有设备内置硬解就走硬解,没有就回退,屏上那行字要讲清走了哪条
        var plan = sc != null ? sc.Plan : new DecoderPlan { route = DecodeRoute.Unknown };
        string dline = sc != null ? sc.DecodeLine : "";
        Check("观影·播片前探到解码路线且判定可播", sc != null && !string.IsNullOrEmpty(dline) && plan.Playable,
            dline + " | 本机:" + VideoDecodeProbe.PlatformDescription
            + (VideoDecodeProbe.CanEnumerate ? "" : "(此平台枚举不到解码器清单,路线只能 Unknown,不算失败)"));

        // ⑨ 停顿到点真的开播,并且屏上确实有画面(只看"出帧"会假绿,见坑 30/31)
        yield return new WaitForSeconds(c.cardHoldSeconds + 2.5f);
        float luma = sc != null ? sc.AverageLuma() : -1f;
        float fw = sc != null ? sc.FaceWidth : 0f, fh = sc != null ? sc.FaceHeight : 0f;
        Check("观影·影片真的在屏上出画面",
              sc != null && sc.Decoding && luma > 0.02f
              && Mathf.Abs(fh - sc.PanelHeight) < 0.02f && Mathf.Abs(fw - sc.PanelWidth) < 0.02f,
              "在播=" + (sc != null && sc.Decoding) + " RT平均亮度=" + luma.ToString("0.000")
            + " 画面 " + fw.ToString("0.00") + "×" + fh.ToString("0.00")
            + " m / 屏框 " + (sc != null ? sc.PanelWidth.ToString("0.00") : "?") + "×"
            + (sc != null ? sc.PanelHeight.ToString("0.00") : "?")
            + " m(屏框已按 16:9 建,16:9 片子应当**铺满**、不留左右黑边)");

        // ⑨b 实测解码吞吐:路线对不对是本机属性,跟不跟得上要量出来才算数(软解经常在"能播"和"播得动"之间翻车)
        yield return new WaitForSeconds(2.5f);         // 让看门狗至少走完一个 1 s 窗口
        Check("观影·实测解码跟得上片源帧率",
              sc != null && sc.MeasuredFps > 0f && sc.KeepingUp,
              (sc != null ? VideoDecodeProbe.ShortReport(sc.Plan, sc.MeasuredFps, sc.SourceFps) : "没有 TerraceScreen")
              + ",1 s 窗口落后 " + (sc != null ? sc.LagSeconds.ToString("0.00") : "?") + " s"
              + "(真机上这项挂了就是解码器顶不住:换低分辨率源,或给应用装内置软解插件)");

        // ⑩ 前进 5 秒 / 后退 5 秒
        double t0 = sc != null ? sc.Clock : -1;
        c.OnSeekForward(); yield return new WaitForSeconds(0.4f);
        double t1 = sc != null ? sc.Clock : -1;
        c.OnSeekBack(); yield return new WaitForSeconds(0.4f);
        double t2 = sc != null ? sc.Clock : -1;
        Check("观影·前进/后退 5 秒真的改了播放位置",
              t0 >= 0 && t1 - t0 > 4.0 && t1 - t0 < 7.0 && t1 - t2 > 3.5 && t1 - t2 < 7.0,
              t0.ToString("0.0") + "s →前进→ " + t1.ToString("0.0") + "s →后退→ " + t2.ToString("0.0")
            + "s(两跳各应约 ±5 s)片长 " + (sc != null ? sc.ClipLength.ToString("0.0") : "?") + "s");

        // ⑪ 暂停 / 继续
        c.OnPauseToggle(); yield return new WaitForSeconds(0.3f);
        bool held = sc != null && !sc.IsPlayingNow;
        string pl = c.pauseLabel != null ? c.pauseLabel.text : "";
        c.OnPauseToggle(); yield return new WaitForSeconds(0.3f);
        Check("观影·暂停与继续", held && pl == "继续播放" && sc != null && sc.IsPlayingNow,
            "暂停后停住=" + held + " 按钮字改口=\"" + pl + "\" 再按恢复在播=" + (sc != null && sc.IsPlayingNow));

        // ⑫ 倍速
        c.OnCycleSpeed(); yield return new WaitForSeconds(0.1f);
        string sl = c.speedLabel != null ? c.speedLabel.text : "";
        Check("观影·倍速按钮有效", sl.Contains("×") && sl != "倍速 1.0×", "现在写着\"" + sl + "\"");
        c.OnCycleSpeed();                                     // 再按三下把倍速转回 1.0×,免得后面几项在快放里量
        c.OnCycleSpeed(); c.OnCycleSpeed();

        // ⑬ 关闭影片(只停片,人还在观影态)
        c.OnCycleSpeed(); yield return new WaitForSeconds(0.05f); // 故意停在非 1.0× 测试关片复位
        c.OnStopFilm(); yield return new WaitForSeconds(0.4f);
        bool standby = sc != null && sc.infoCard != null && sc.infoCard.activeSelf
                       && sc.cardTitle != null && sc.cardTitle.text.Contains("未选片");
        bool speedResetOk = c.speedLabel == null || c.speedLabel.text.Contains("1.0×");
        Check("观影·关闭影片退回待机卡", c.IsSeated && sc != null && !sc.IsPlayingNow && standby
              && c.CurrentFilm < 0 && speedResetOk,
            "仍在观影态=" + c.IsSeated + " 已停播=" + (sc != null && !sc.IsPlayingNow)
            + " 待机卡=" + standby + " 片号清成=" + c.CurrentFilm + " 倍速复位=" + speedResetOk);

        // ⑭ 硬锁站位:推摇杆/瞬移都不再弹「留下继续看 / 离座走动」,大位移会被下一帧拽回座位。
        //     2026-10-19 定案:观影态强制限制自由走动,不提供任何就地解锁入口。
        var seatPos = c.seat.position;
        rig.position += new Vector3(3.2f, 0f, 1.4f);
        yield return new WaitForSeconds(0.25f);
        float snap = Vector3.Distance(rig.position, seatPos);
        bool leaveGone = true;
        foreach (var t in c.GetComponentsInChildren<Transform>(true))
            if (t.name == "LeavePanel" || t.name == "Exit") leaveGone = false;
        Check("观影·硬锁站位(大位移拽回,无离座/退出入口)",
              c.IsSeated && snap < 0.1f && leaveGone,
            "在座=" + c.IsSeated + " 位移后距座位=" + snap.ToString("0.000") + " m"
            + " 离座/退出入口=" + (leaveGone ? "已移除" : "!! 仍在"));

        // ⑯ 面前那颗「回一层大厅」真的把人送回图书馆(不用转身打身后那块板)
        var dest = c.lobbyTeleporter != null && c.lobbyTeleporter.destination != null
            ? c.lobbyTeleporter.destination.position : new Vector3(9999f, 0f, 9999f);
        c.OnBackToLobby();
        yield return new WaitForSeconds(0.7f);
        float toLobby = Vector3.Distance(rig.position, dest);
        Check("观影·播控面板「回一层大厅」生效", !c.IsSeated && toLobby < 4f,
            "点后离目的地「" + (c.lobbyTeleporter != null && c.lobbyTeleporter.destination != null
                ? c.lobbyTeleporter.destination.name : "?") + "」 " + toLobby.ToString("0.0")
            + " m(在座=" + c.IsSeated + ")");
        rig.position = homePos;
    }

    static string Label(Button b)
    {
        var t = b.GetComponentInChildren<Text>(true);
        return t != null && !string.IsNullOrEmpty(t.text) ? t.text.Trim() : b.name;
    }

    void TestWiring()
    {
        // 只验"接线",不假装验手感:这些组件缺了就整条链路断
        var sp = FindObjectOfType<SprintController>();
        Check("加速组件接线", sp != null && sp.moveProviderBehaviour != null,
            sp == null ? "场景里没有 SprintController"
                       : "已解析到移动 Provider:" + (sp.moveProviderBehaviour != null ? sp.moveProviderBehaviour.GetType().Name : "null")
                         + " ×" + sp.sprintMultiplier);

        // 2026-11-06 演示裁剪:归还服务已整体移除(书不可抓),不再检查归还口接线。

        var ui = LibraryUIController.Instance;
        if (ui == null || ui.statusText == null)
        {
            Check("信息面板", false, "LibraryUIController 或 statusText 未接");
        }
        else
        {
            ui.OnSearchButton();
            string shown = ui.statusText.text;
            Check("信息面板·按钮到屏显", shown.Contains("检索"),
                "点\"智能检索\"后屏上:" + shown.Replace("\n", " / "));
        }

        int books = FindObjectsOfType<LibraryBook>().Length;
        int grabs = FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable>().Length;
        int clicks = 0, noCol = 0, rbLeft = 0;
        foreach (var b in FindObjectsOfType<LibraryBook>())
        {
            if (b.GetComponent<UnityEngine.XR.Interaction.Toolkit.XRSimpleInteractable>() != null) clicks++;
            if (b.GetComponent<Collider>() == null) noCol++;
            if (b.GetComponent<Rigidbody>() != null) rbLeft++;
        }
        // 2026-09-28 用户定案:书只可点击、不可抓(可抓=必须有刚体=永远进不了静态合批)
        Check("书籍点击可读(不再可抓)", books == 262 && clicks == books && grabs == 0 && rbLeft == 0 && noCol == 0,
            "运行中 LibraryBook=" + books + " 可点击=" + clicks + " 残留可抓=" + grabs
            + " 残留刚体=" + rbLeft + " 缺碰撞体=" + noCol);

        var anchors = FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor>();
        int up = 0, upBad = 0;
        foreach (var a in anchors)
        {
            if (a.transform.position.y <= 2.5f) continue;
            up++;
            if (!a.gameObject.activeInHierarchy || a.GetComponent<Collider>() == null) upBad++;
        }
        Check("二层传送锚点", up >= 6 && upBad == 0,
            "锚点共 " + anchors.Length + " 个(一层 " + (anchors.Length - up) + " / 二层 " + up + "),"
            + "二层缺碰撞体或未激活 " + upBad + " 个");

        // 露台/二层的"面板标高"(2026-10-05 新增):v7.35 的整馆拔高是"给带 Renderer 的对象加 1.423",
        // 于是**纯空物体与 UI Canvas 全被留在旧标高** —— 装三块观影面板的 CinemaController 就是空物体,
        // 面板因此比眼高低 1.42 m(约在小腿高度),玩家看到的是"面板飘得很远、人显得特别高";
        // TerraceSign 更惨,落到二层板面**以下**,直接埋进楼板里。
        // 判据必须是"面板中心落在座位眼位附近",而不是"物体在不在" —— 后者在坏掉时照样全绿。
        var cin = FindObjectOfType<TerraceCinema>();
        if (cin == null || cin.seat == null)
        {
            Fail("露台面板标高(面板应落在眼位高度)", "场上没有 TerraceCinema 或没接 seat");
        }
        else
        {
            float eye = cin.seat.position.y + 1.60f;
            var pnames = new[] { "目录", "播控", "须知" };
            var pgos = new GameObject[] { cin.catalogPanel, cin.transportPanel, cin.noticePanel };
            int pbad = 0;
            var plines = new List<string>();
            for (int i = 0; i < pgos.Length; i++)
            {
                if (pgos[i] == null) { pbad++; plines.Add(pnames[i] + "=缺"); continue; }
                float d = pgos[i].transform.position.y - eye;
                bool ok = d > -0.60f && d < 0.40f;
                if (!ok) pbad++;
                plines.Add(pnames[i] + "=" + (d >= 0f ? "+" : "") + d.ToString("0.00") + "m" + (ok ? "" : "!!"));
            }
            // 目录必须沿"座位→银幕"方向摆在正前方,而不是偏在一边 / 跟随头部朝向
            float catAlong = 0f, catLateral = 0f;
            bool catFront = false;
            if (cin.catalogPanel != null)
            {
                var f = cin.seat.forward; f.y = 0f; f.Normalize();
                var to = cin.catalogPanel.transform.position - cin.seat.position; to.y = 0f;
                catAlong = Vector3.Dot(to, f);
                catLateral = Vector3.Dot(to, cin.seat.right);
                catFront = catAlong > 0.5f && Mathf.Abs(catLateral) < 0.05f;
                if (!catFront) pbad++;
            }
            // 楼梯口指示牌:应在二层板面顶 + 1.35。两层楼板都是 4.423,座位垫面比板面顶高 0.045,减回去即板面顶。
            var sign = GameObject.Find("SmartLibrary/Mezzanine/TerraceSign");
            float signD = 0f; bool signOk = true;
            if (sign == null) signOk = false;
            else
            {
                signD = sign.transform.position.y - ((cin.seat.position.y - 0.045f) + 1.35f);
                signOk = Mathf.Abs(signD) < 0.20f;
            }
            Check("露台面板标高与目录朝向", pbad == 0 && signOk,
                "眼位 y=" + eye.ToString("0.00") + "(座位 " + cin.seat.position.y.ToString("0.00") + " + 1.60)"
                + " 各面板与眼位差 " + string.Join(" ", plines.ToArray())
                + " | 目录沿座位→银幕 " + catAlong.ToString("0.00") + " m / 横向 "
                + catLateral.ToString("+0.00;-0.00;0.00") + " m" + (catFront ? " ✓" : " !!")
                + " | 楼梯口指示牌偏差 " + signD.ToString("0.00") + " m" + (signOk ? "" : " !!"));
        }
    }

    // ── HUD ────────────────────────────────────────────────────────
    /// <summary>
    /// HUD 一律按"点了会动"验:每颗按钮都走它自己的 onClick.Invoke(持久化绑定的那条路),
    /// 只查物体在不在等于没查 —— 绑错方法的按钮,场上照样"齐全"。
    /// </summary>
    IEnumerator TestHud()
    {
        var hud = LibraryHud.Instance;
        // 2026-11-06 演示裁剪:HUD 已整块禁用 → 本条按"跳过"处理,不算失败。
        // (要恢复就把场景里 LibraryHud 物体重新启用,再跑一次冒烟)
        if (hud == null) { Line("  HUD:已禁用(演示裁剪),跳过"); yield break; }

        var cv = hud.GetComponent<Canvas>();
        var scaler = hud.GetComponent<CanvasScaler>();
        float pw = hud.menuPanel != null ? hud.menuPanel.rect.width : 0f;
        // 把三个候选宽度全打出来:哪个是假的,一次跑就能看出来(别再靠推理)
        var crt = cv == null ? null : cv.GetComponent<RectTransform>();
        float canvasPx = crt == null ? 0f : crt.rect.width;
        float sf = cv == null ? 0f : cv.scaleFactor;
        float refW = scaler != null ? scaler.referenceResolution.x : 0f;
        float third = refW / 3f;
        // v7.28:画布已改成头锁世界空间 —— 判据也跟着改成"世界空间 + 挂在相机下 + 张角在舒适区"(角度才算数,坑 38)
        var hudRt = hud.GetComponent<RectTransform>();
        float hudDist = hudRt != null ? Mathf.Abs(hudRt.localPosition.z) : 0f;
        var camT = hud.origin != null && hud.origin.Camera != null ? hud.origin.Camera.transform : null;
        bool headLocked = camT != null && hud.transform.parent == camT;
        float halfW = (hudRt != null && hudDist > 0.01f)
            ? Mathf.Atan(hudRt.rect.width * hudRt.localScale.x * 0.5f / hudDist) * Mathf.Rad2Deg : 999f;
        float halfH = (hudRt != null && hudDist > 0.01f)
            ? Mathf.Atan(hudRt.rect.height * hudRt.localScale.y * 0.5f / hudDist) * Mathf.Rad2Deg : 999f;
        Check("HUD 结构", cv != null && cv.renderMode == RenderMode.WorldSpace && headLocked
              && halfW > 18f && halfW < 32f && halfH > 8f && halfH < 20f
              && hud.hudGroup != null && hud.menuGroup != null
              && hud.origin != null && hud.teleporters != null && hud.teleporters.Length == 3
              && Mathf.Abs(pw - third) < third * 0.05f,
              "世界空间=" + (cv != null && cv.renderMode == RenderMode.WorldSpace) + " 头锁=" + headLocked
              + " 张角=" + halfW.ToString("0.0") + "°×" + halfH.ToString("0.0") + "°"
              + " hudGroup=" + (hud.hudGroup != null)
              + " menuGroup=" + (hud.menuGroup != null) + " 传送器 3 份"
              + ";侧边栏实宽 " + pw.ToString("0") + " = 画布宽 "
              + (refW > 0f ? (pw / refW * 100f).ToString("0") : "-") + "%(要 33%±2)"
              + "  [画布rect=" + canvasPx.ToString("0") + " 距眼=" + hudDist.ToString("0.00") + "m"
              + " scaleFactor=" + sf.ToString("0.000") + " 参考宽=" + refW.ToString("0") + "]");

        // ── 开合:菜单键开 → 点空白收 → 菜单键开 → 再点菜单键收
        var btnToggle = Child<Button>(hud.transform, "MenuToggle");
        var btnTp1 = Child<Button>(hud.transform, "MenuPanel/PageHome/Btn_1");
        if (btnToggle == null || btnTp1 == null)
        {
            Fail("HUD 菜单开合", "找不到 MenuToggle 或 PageHome/Btn_1");
            yield break;
        }
        btnToggle.onClick.Invoke();
        yield return new WaitForSeconds(0.05f);
        bool opened = hud.IsMenuOpen && hud.backdrop != null && hud.backdrop.activeSelf;
        if (hud.backdrop != null) hud.backdrop.GetComponent<Button>().onClick.Invoke();   // 点其他区域
        yield return new WaitForSeconds(0.05f);
        bool closedByEmpty = !hud.IsMenuOpen && !hud.backdrop.activeSelf;
        btnToggle.onClick.Invoke();
        yield return new WaitForSeconds(0.05f);
        btnToggle.onClick.Invoke();                                                       // 再点一次菜单键
        yield return new WaitForSeconds(0.05f);
        bool closedByToggle = !hud.IsMenuOpen;
        Check("HUD 菜单开合", opened && closedByEmpty && closedByToggle,
              "开=" + opened + " 点空白收=" + closedByEmpty + " 再点菜单键收=" + closedByToggle);
        // 这块 HUD 跟着相机走 ⇒ SceneView 截图里根本不会出现它,只能等 Play 里由引擎自己写盘
        btnToggle.onClick.Invoke();
        yield return new WaitForSeconds(0.4f);            // 等侧边栏滑完 0.18 s + 淡入
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot("Captures/hud-open-1f.png");
        yield return new WaitForSeconds(0.6f);

        // ── 三处传送:量**相机**到落点的水平距离 —— KioskTeleporter 承诺的是"把头显放到目标点"
        //    (它先转 Rig、再把相机在 Rig 内的水平偏移减掉),拿 Rig 到目标的距离当标准会在
        //    头显偏移大时三处同时"差同一个数"(实测 1.51 m),那是量具错不是传送错。
        //    垂直差仍量 Rig:跨层必须连 Rig 的 Y 一起改,这才是这条判据要防的事(坑 37)。
        var o = hud.origin != null ? hud.origin.transform : null;
        if (o == null) { Fail("HUD 传送", "origin 未接"); yield break; }
        string[] names = { "Btn_1", "Btn_2", "Btn_3" };
        var lines = new List<string>();
        bool allOk = true;
        for (int i = 0; i < 3; i++)
        {
            var b = Child<Button>(hud.transform, "MenuPanel/PageHome/" + names[i]);
            if (b == null) { allOk = false; lines.Add(names[i] + " 不在"); continue; }
            b.onClick.Invoke();
            yield return new WaitForSeconds(0.35f);        // 让 CharacterController 把落地的几厘米沉下去
            var t = hud.teleporters[i];
            var ct = t == null || t.origin == null ? null : t.origin.Camera;
            if (t == null || t.destination == null || ct == null)
            {
                allOk = false;
                lines.Add(names[i] + " 接线不全(落点=" + (t != null && t.destination != null)
                          + " 相机=" + (ct != null) + ")");
                continue;
            }
            var cp = ct.transform.position;
            float flat = Vector2.Distance(new Vector2(cp.x, cp.z),
                                          new Vector2(t.destination.position.x, t.destination.position.z));
            float dy = Mathf.Abs(o.position.y - t.destination.position.y);
            float head = Vector2.Distance(new Vector2(o.position.x, o.position.z), new Vector2(cp.x, cp.z));
            bool ok = flat < 0.5f && dy < 0.35f;
            if (!ok) allOk = false;
            lines.Add(string.Format("{0}→{1} 相机距落点{2:0.00}m 头显偏移{3:0.00}m Rig垂直差{4:0.00}m 楼层读作「{5}」",
                names[i], t.destination.name, flat, head, dy, hud.FloorText));
        }
        Check("HUD 三处传送", allOk, string.Join(" | ", lines.ToArray()));

        // 坐在星空影台前,小地图那行应当读作"星空影台":这是"露台与二楼同为 3.0 m、只能靠轮廓判层"那条规则的实拍证据
        var b3 = Child<Button>(hud.transform, "MenuPanel/PageHome/Btn_3");
        if (b3 != null)
        {
            b3.onClick.Invoke();
            yield return new WaitForSeconds(0.8f);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot("Captures/hud-terrace.png");
            yield return new WaitForSeconds(0.5f);
        }
        btnToggle.onClick.Invoke();
        yield return new WaitForSeconds(0.5f);            // 收起状态单独抓一张:只留左上菜单键 + 右上小地图
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot("Captures/hud-closed.png");
        yield return new WaitForSeconds(0.4f);

        // ── 设置页与音量:走滑条的 onValueChanged,而不是直接调方法
        var btnSet = Child<Button>(hud.transform, "MenuPanel/PageHome/Btn_Settings");
        if (btnSet == null || hud.volumeSlider == null)
        {
            Fail("HUD 音量", "Btn_Settings 或滑条不在");
        }
        else
        {
            btnSet.onClick.Invoke();
            yield return new WaitForSeconds(0.05f);
            bool onSettings = hud.pageSettings.activeSelf && !hud.pageHome.activeSelf;
            hud.volumeSlider.onValueChanged.Invoke(0.33f);
            // 证据要在归位之前取走:拖完就去读的值才是"滑条真的作用到了总音量"这件事
            float vol = AudioListener.volume;
            float saved = PlayerPrefs.GetFloat(LibraryHud.VolumeKey, -1f);
            string cap = hud.volumeLabel.text;
            bool applied = Mathf.Abs(vol - 0.33f) < 0.01f
                           && Mathf.Abs(saved - 0.33f) < 0.01f
                           && cap.Contains("33");
            hud.volumeSlider.onValueChanged.Invoke(LibraryHud.DefaultVolume);    // 归位,别把后面测试的响度改了
            var btnBack = Child<Button>(hud.transform, "MenuPanel/PageSettings/Btn_Back");
            if (btnBack != null) btnBack.onClick.Invoke();
            yield return new WaitForSeconds(0.05f);
            bool backHome = hud.pageHome.activeSelf && !hud.pageSettings.activeSelf;
            Check("HUD 音量与设置页", onSettings && applied && backHome,
                  "设置页=" + onSettings + " 拖到 33% 当时:AudioListener=" + vol.ToString("0.00")
                  + " 存盘=" + saved.ToString("0.00") + " 文案「" + cap + "」 返回菜单=" + backHome
                  + "(已归位到 " + AudioListener.volume.ToString("0.00") + ")");
        }

        // ── 悬停提示与退出的两下确认
        int tipOk = 0, tipN = 0;
        foreach (var h in hud.GetComponentsInChildren<HudHoverTip>(true))
        {
            tipN++;
            if (h.tip == null) continue;
            h.OnPointerEnter(null);
            bool on = h.tip.activeSelf;
            h.OnPointerExit(null);
            if (on && !h.tip.activeSelf) tipOk++;
        }
        var btnQuit = Child<Button>(hud.transform, "MenuPanel/PageHome/Btn_Quit");
        if (btnQuit != null) btnQuit.onClick.Invoke();       // 第一下只应当".arm",不许真退
        yield return new WaitForSeconds(0.05f);
        bool armed = hud.quitConfirm != null && hud.quitConfirm.activeSelf;
        Check("HUD 悬停提示与退出确认", tipOk == 2 && tipN == 2 && armed,
              "射线指上就显字的提示 " + tipOk + "/" + tipN + " 条;按第一下关机只出确认行=" + armed
              + "(没按第二下,应用仍在跑)");

        // ── 呼出键:真机 = 右手 A/B 键,编辑器 = M/N。这里用 Input System **合成按键**,
        //    验的是"读键 → 动作"这一整条线(不是直接调 API —— 那样只能证明方法在)。
        //    只覆盖得到编辑器这一半:真机上按 A/B 仍要在头显里按一次才算过(与遗留 19 同口径)。
        var kb = Keyboard.current;
        if (kb == null)
        {
            Check("HUD 呼出键(M/N 合成按键)", false, "没有键盘设备,合成不了按键 → 只能真机验");
        }
        else
        {
            // 每帧重发一次"按下"(最多 6 帧):XR Device Simulator 这类活动输入源会逐帧重写键盘状态,
            // 单发一次会被抹掉 —— 同一条代码实测出现过 PASS 也出现过 FAIL,就是被抹掉的那次(坑 36 同族)。
            bool menuWas = hud.IsMenuOpen;
            bool menuNow = menuWas;
            for (int f = 0; f < 6 && menuNow == menuWas; f++)
            {
                InputSystem.QueueStateEvent(kb, new KeyboardState(Key.M));
                InputSystem.Update();
                yield return null;
                menuNow = hud.IsMenuOpen;
            }
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            InputSystem.Update();
            yield return null;

            bool mapWas = hud.IsMinimapVisible;
            bool mapNow = mapWas;
            for (int f = 0; f < 6 && mapNow == mapWas; f++)
            {
                InputSystem.QueueStateEvent(kb, new KeyboardState(Key.N));
                InputSystem.Update();
                yield return null;
                mapNow = hud.IsMinimapVisible;
            }
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            InputSystem.Update();
            yield return null;
            hud.SetMinimapVisible(true);                       // 归位,别把后面的用例带歪
            bool mapObjBack = hud.minimap == null || hud.minimap.activeSelf;

            Check("HUD 呼出键(M/N 合成按键)", menuNow != menuWas && mapNow != mapWas && mapObjBack,
                  "M 键让菜单翻转=" + (menuNow != menuWas) + "(" + menuWas + "→" + menuNow + ")"
                  + ";N 键让小地图翻转=" + (mapNow != mapWas) + ";小地图已归位=" + mapObjBack
                  + "(真机右手 A/B 走同一条路,需在头显里按一次)");
        }
    }

    /// <summary>
    /// HUD 按名找件:v7.26 起件都收在 `FadeContainer` 下,而 `Transform.Find` 只查直接子级(坑 28),
    /// 所以先从根找、找不到再落到容器上 —— 新旧挂法都能跑,不会因为换代就把两条 HUD 判据打成假 FAIL。
    /// </summary>
    static T Child<T>(Transform root, string path) where T : Component
    {
        var t = root.Find(path);
        if (t == null)
        {
            var fc = root.Find("FadeContainer");
            if (fc != null) t = fc.Find(path);
        }
        return t == null ? null : t.GetComponent<T>();
    }

    static void Check(string what, bool ok, string detail)
    {
        Line((ok ? "  [PASS] " : "  [FAIL] ") + what + " —— " + detail);
        Flush();
    }
    static void Fail(string what, string why) { Check(what, false, why); }
    static void Line(string x) { if (rep != null) rep.AppendLine(x); }
    static void Flush()
    {
        if (rep == null) return;
        string full = Abs(kReport);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
        File.WriteAllText(full, rep.ToString(), Encoding.UTF8);
    }

    /// <summary>工程绝对路径:CWD 漂走时报告才不会写进 Library/Bee/... 里,让人拿上一轮的绿当本轮的。</summary>
    static string Abs(string rel)
    {
        string root = System.IO.Path.GetDirectoryName(Application.dataPath);
        return System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }
}
#endif
