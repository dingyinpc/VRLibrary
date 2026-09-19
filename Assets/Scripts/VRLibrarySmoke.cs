#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
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
        if (!File.Exists(kRequest)) return;
        File.Delete(kRequest);                       // 一次性,避免每次进 Play 都跑

        rep = new StringBuilder();
        errors = new List<string>();
        Application.logMessageReceived += OnLog;

        var go = new GameObject("__VRLibrarySmoke__");
        DontDestroyOnLoad(go);
        var runner = go.AddComponent<VRLibrarySmoke>();
        runner.StartCoroutine(runner.Run());
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
        Line("运行时冒烟测试  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Line("Unity " + Application.unityVersion + "   场景 " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
        frame0 = Time.frameCount; t0 = Time.time;

        yield return new WaitForSeconds(1.5f);       // 等各组件 Awake/Start 跑完

        yield return TestReader();
        yield return TestSearchAndTeleport();
        yield return TestKioskTeleport();
        yield return TestLights();
        yield return TestDayNight();
        TestWiring();

        Flush();
        yield return new WaitForSeconds(0.3f);

        int dFrames = Time.frameCount - frame0;
        Line("");
        Line("帧数推进 " + dFrames + ",时间推进 " + (Time.time - t0).ToString("F2") + " s"
             + (dFrames < 30 ? "   ← 游戏循环几乎没跑,本次结果不可信!" : ""));
        Line("捕获到的报错/异常 " + errors.Count + " 条");
        foreach (var e in errors) Line("  ! " + e);
        Flush();
        Application.logMessageReceived -= OnLog;

        Debug.Log("[Smoke] 冒烟测试完成 → " + kReport);
        UnityEditor.EditorApplication.isPlaying = false;
        Destroy(gameObject);
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
        r.NextPage(); r.NextPage();
        string p1 = r.pageText != null ? r.pageText.text : "";
        r.Close();
        bool closed = !r.panelRoot.activeSelf;
        int bmAfter = BookReader.Bookmarks.Count;

        Check("阅读面板", opened && body.Length > 20 && closed,
            "《" + book.title + "》面板=" + opened + " 正文" + body.Length + "字 页码\"" + p0 + "\"→\"" + p1
            + "\" 关合=" + closed + " 书签 " + bmBefore + "→" + bmAfter);
        Check("阅读面板·翻页", p0 != p1, "页码文本变化即翻页生效");
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
    }

    IEnumerator TestKioskTeleport()
    {
        var kt = FindObjectOfType<KioskTeleporter>();
        if (kt == null) { Fail("导览屏传送", "场景里没有 KioskTeleporter"); yield break; }
        var o = kt.origin != null ? kt.origin.transform : null;
        if (o == null) { Fail("导览屏传送", "origin 未接"); yield break; }
        var from = o.position;
        kt.TeleportToDestination();
        yield return new WaitForSeconds(0.2f);
        float moved = Vector3.Distance(from, o.position);
        float toDest = kt.destination != null ? Vector3.Distance(o.position, kt.destination.position) : -1f;
        Check("导览屏一键传送", moved > 0.5f && (toDest < 0 || toDest < 3f),
            "Origin 移动 " + moved.ToString("F2") + " m,距目标点 " + toDest.ToString("F2") + " m");
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

        // 日夜切换会写 RenderSettings.skybox 这个共享材质,而 Play 里对"资产"的修改退出后不回滚
        // (只有场景改动会回滚)。也不能靠"读回来再写回去"复原:实测 GetColor→SetColor→SaveAssets
        // 仍会让 _ZenithColor/_HorizonColor 从 0.044999883 变成 0.04499986 —— Unity 序列化 float
        // 本身不幂等,写一次就漂。所以直接丢弃内存改动、从磁盘重导入。
        var sky = RenderSettings.skybox;
        if (sky != null)
        {
            string path = UnityEditor.AssetDatabase.GetAssetPath(sky);
            if (!string.IsNullOrEmpty(path))
                UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceUpdate);
        }
    }

    void TestWiring()
    {
        // 只验"接线",不假装验手感:这些组件缺了就整条链路断
        var sp = FindObjectOfType<SprintController>();
        Check("加速组件接线", sp != null && sp.moveProviderBehaviour != null,
            sp == null ? "场景里没有 SprintController"
                       : "已解析到移动 Provider:" + (sp.moveProviderBehaviour != null ? sp.moveProviderBehaviour.GetType().Name : "null")
                         + " ×" + sp.sprintMultiplier);

        var sock = FindObjectOfType<BookReturnSocket>();
        bool sockOk = sock != null
                      && sock.GetComponent<UnityEngine.XR.Interaction.Toolkit.XRSocketInteractor>() != null;
        Check("归还口接线", sockOk,
            sock == null ? "没有 BookReturnSocket"
                         : "XRSocketInteractor " + (sock.GetComponent<UnityEngine.XR.Interaction.Toolkit.XRSocketInteractor>() != null ? "在" : "缺失(Start() 会抛 NRE)"));

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
        Check("书籍可抓取数", books == 262 && grabs == 262, "运行中 LibraryBook=" + books + " GrabInteractable=" + grabs);

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
    }

    // ── 记录 ───────────────────────────────────────────────────────
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
        Directory.CreateDirectory("Logs");
        File.WriteAllText(kReport, rep.ToString(), Encoding.UTF8);
    }
}
#endif
