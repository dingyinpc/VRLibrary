using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// 智能图书馆 · 小地图与菜单 HUD 的搭建菜单。
/// VRLibrary/装小地图与菜单(会改场景并自动保存)  → 报告 Logs/hud-setup.txt
/// VRLibrary/小地图与菜单·复核(只报告)            → 报告 Logs/hud-verify.txt
///
/// 为什么走菜单而不是手摆:手摆过一次就没人能重建它(遗留 13 那十处坑全这么来的)。
/// HUD 挂在 Main Camera 下,幂等 = 按名先拆后建,重跑只挪不叠。
///
/// 布局量的是"离眼睛多远 = 占多少度":画布 1240×560 px × 缩放 0.0011 = 世界 1.364×0.616 m,
/// 摆在眼前 1.85 m ⇒ 画布角落在视轴 ±20°/±7.7°(跟头显走的 HUD 再往外要出视野、再往里糊脸)。
/// 同一套换算下字高 30 px = 1.02°、32 px = 1.09°,这是 VR 里能读的最小档。
/// </summary>
public static class LibraryHudSetup
{
    const string kCameraPath = "SmartLibrary/XR Interaction Setup/XR Origin (XR Rig)/Camera Offset/Main Camera";
    const string kHudName = "LibraryHud";

    // 画布宽决定"贴角"贴到哪:1240 px × 0.0011 = 1.364 m,在 1.85 m 处张到视轴 ±20.3°。
    // 上一版 940 px 只有 ±15.7°,实拍(Captures/hud-open-1f.png)里左上/右上两块挤在视野正中一条上,不像贴角。
    const int CanvasW = 1240, CanvasH = 560;
    const float CanvasScale = 0.0011f, ViewDist = 1.85f;
    const int PanelW = 340, PanelH = 430;

    static readonly Color BgPanel = new Color(0.02f, 0.045f, 0.075f, 0.93f);
    static readonly Color BtnBg = new Color(0.07f, 0.20f, 0.30f, 1f);
    static readonly Color BtnIcon = new Color(0.10f, 0.16f, 0.24f, 1f);
    static readonly Color Ink = Color.white;
    static readonly Color InkDim = new Color(1f, 1f, 1f, 0.72f);
    static readonly Color None = new Color(0f, 0f, 0f, 0f);

    static StringBuilder sb;
    static int bad;

    // ─────────────────────────────────────────────────────── 菜单

    [MenuItem("VRLibrary/装小地图与菜单(会改场景并自动保存)")]
    public static void Build()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (!scene.path.EndsWith("SmartLibrary.unity"))
        {
            Debug.LogError("[HUD] 当前场景不是 SmartLibrary:" + scene.path);
            return;
        }
        sb = new StringBuilder();
        bad = 0;
        sb.AppendLine("小地图与菜单 HUD 搭建  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Snapshot();

        var camGo = GameObject.Find(kCameraPath);
        if (camGo == null)
        {
            sb.AppendLine("  !! 找不到相机 " + kCameraPath + " → 没建任何东西");
            bad++;
            Flush("Logs/hud-setup.txt");
            return;
        }
        var cam = camGo.transform;

        int wiped = Wipe(cam);
        var origin = FindOrigin();
        if (origin == null) sb.AppendLine("  !! 场上没有可用的 XROrigin → 传送按钮要手动接 origin");

        var root = New(kHudName, cam);
        var hud = root.AddComponent<LibraryHud>();
        var cv = root.AddComponent<Canvas>();
        cv.renderMode = RenderMode.WorldSpace;
        var crt = root.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(CanvasW, CanvasH);
        crt.localScale = Vector3.one * CanvasScale;
        crt.localPosition = new Vector3(0f, 0f, ViewDist);
        crt.localRotation = Quaternion.identity;      // uGUI 可见面是自身 -Z;父级就是相机 ⇒ identity 正对眼睛
        root.AddComponent<GraphicRaycaster>();
        root.AddComponent<TrackedDeviceGraphicRaycaster>();
        var hudGroup = root.AddComponent<CanvasGroup>();
        hudGroup.alpha = 1f;

        var backdrop = Backdrop(root.transform);
        var toggle = MenuToggleButton(root.transform);
        RectTransform dial; Text floorLabel;
        Minimap(root.transform, out dial, out floorLabel);
        CanvasGroup menuGroup; GameObject pageHome, pageSettings, quitConfirm, tipSettings, tipQuit;
        Slider volSlider; Text volLabel; Button[] tpButtons; Button btnSettings, btnQuit;
        var panel = MenuPanel(root.transform, out menuGroup, out pageHome, out pageSettings,
            out volSlider, out volLabel, out quitConfirm, out tpButtons,
            out btnSettings, out btnQuit, out tipSettings, out tipQuit);

        // 三处传送复用现成的 KioskTeleporter(它补偿头显在 Rig 内的偏移,落的是"人"而不是 Rig 原点)。
        // keepRigHeight 必须关掉:那份逻辑原本为同层设计,跨层会把人落在走道板里面。
        string[] destNames = { "SmartLibrary/Pad_Service", "SmartLibrary/Pad_2F_N_E", "CinemaSeat" };
        string[] destLabel = { "一层·中央服务台", "二层·北侧回廊(正面看得到玻璃廊桥)", "星空影台·观影座位" };
        string[] methodName = { "TeleportFloor1", "TeleportFloor2", "TeleportTerrace" };
        var tps = new KioskTeleporter[3];
        for (int i = 0; i < 3; i++)
        {
            var go = New("Tp_" + (i + 1), root.transform);
            tps[i] = go.AddComponent<KioskTeleporter>();
            tps[i].origin = origin;
            tps[i].keepRigHeight = false;
            var d = FindAny(destNames[i]);
            if (tps[i].destination != d)
                sb.AppendLine(string.Format("  [落点{0}] 重算:" + (d == null ? "(没找到)" : d.name), i + 1));
            tps[i].destination = d;
            if (d == null) { sb.AppendLine("  !! 落点「" + destNames[i] + "」找不到 → 第 " + (i + 1) + " 颗按钮不会动"); bad++; }
            else
            {
                sb.AppendLine(string.Format("  [落点{0}] {1} = {2} @ {3} 朝向 {4:0.0}° → 楼层判读「{5}」",
                    i + 1, destLabel[i], d.name, Pos(d), d.eulerAngles.y, LibraryHud.FloorNameAt(d.position)));
                ProbeGround(d);
            }
            Bind(tpButtons[i], hud, methodName[i]);
        }

        hud.origin = origin;
        hud.hudGroup = hudGroup;
        hud.menuGroup = menuGroup;
        hud.menuPanel = panel.GetComponent<RectTransform>();
        hud.menuOpenPos = new Vector2(16f, -132f);
        hud.menuClosedPos = new Vector2(-PanelW - 20f, -132f);
        hud.backdrop = backdrop;
        hud.pageHome = pageHome;
        hud.pageSettings = pageSettings;
        hud.floorLabel = floorLabel;
        hud.dial = dial;
        hud.quitConfirm = quitConfirm;
        hud.volumeSlider = volSlider;
        hud.volumeLabel = volLabel;
        hud.teleporters = tps;

        Bind(toggle, hud, "ToggleMenu");
        Bind(backdrop.GetComponent<Button>(), hud, "CloseMenu");
        Bind(btnSettings, hud, "OpenSettings");
        Bind(btnQuit, hud, "RequestQuit");
        var back = Back<Button>(panel.transform, "Btn_Back");
        if (back != null) Bind(back, hud, "OpenMenu");
        else { sb.AppendLine("  !! 设置页没有 Btn_Back → 回不去菜单"); bad++; }

        volSlider.minValue = 0f; volSlider.maxValue = 1f; volSlider.wholeNumbers = false;
        volSlider.SetValueWithoutNotify(LibraryHud.DefaultVolume);
        if (volLabel != null) volLabel.text = "总音量 50%(默认)";
        BindFloat(volSlider, hud, "SetVolume");          // Slider 事件带 float,m_Mode=0 即"用事件参数"

        HoverTip(btnSettings, tipSettings, "打开设置");
        HoverTip(btnQuit, tipQuit, "退出应用");
        BgmSlot(root.transform);

        sb.AppendLine("  拆旧 HUD " + wiped + " 个;按钮 " + root.GetComponentsInChildren<Button>(true).Length
                      + " 颗;文字 " + root.GetComponentsInChildren<Text>(true).Length + " 块;全部编辑期建好(坑 1)");
        Verify(root, hud, cv, hudGroup, menuGroup);
        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        sb.AppendLine("  保存场景:" + (saved ? "成功" : "!! 失败(HUD 只在内存里,重启就没了)")
                      + " → " + scene.path + "(" + new FileInfo(Abs(scene.path)).Length.ToString("N0") + " B)");
        Flush("Logs/hud-setup.txt");
        Debug.Log("[HUD] " + (saved ? "已搭建并保存场景" : "!! 场景没保存成功")
                  + " → Logs/hud-setup.txt(未过项 " + bad + ")");
    }

    [MenuItem("VRLibrary/小地图与菜单·复核(只报告)")]
    public static void Audit()
    {
        sb = new StringBuilder();
        bad = 0;
        sb.AppendLine("小地图与菜单 HUD 复核(只读)  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var root = GameObject.Find(kCameraPath + "/" + kHudName);
        if (root == null)
        {
            sb.AppendLine("  !! 场上没有 " + kHudName + " → 跑 VRLibrary/装小地图与菜单");
            bad++;
            Flush("Logs/hud-verify.txt");
            return;
        }
        sb.AppendLine("  节点:" + Tree(root.transform));
        var mg = root.transform.Find("MenuPanel");
        Verify(root, root.GetComponent<LibraryHud>(), root.GetComponent<Canvas>(),
               root.GetComponent<CanvasGroup>(), mg == null ? null : mg.GetComponent<CanvasGroup>());
        RaycastWiring(root);
        Flush("Logs/hud-verify.txt");
        Debug.Log("[HUD] 复核 → Logs/hud-verify.txt(未过项 " + bad + ")");
    }

    /// <summary>
    /// 冒烟是直调 onClick 验"点了会动",它证不了手柄射线打不打得中这块画布。
    /// 这一节只比能在包里证实的东西:画布的 layer 与 GraphicRaycaster.eventMask,
    /// 基准是场上一直能点的中央导览台 InfoCanvas。
    /// 注意:对上了也只到"接线一致"这一级,真机射线命中仍要在头显里点一次才算验过。
    /// </summary>
    static void RaycastWiring(GameObject hudGo)
    {
        sb.AppendLine("  ── 射线接线对照(基准 = 中央导览台 InfoCanvas) ──");
        var info = GameObject.Find("SmartLibrary/CentralKiosk/InfoCanvas");
        sb.AppendLine("  " + WiringLine("HUD ", hudGo));
        sb.AppendLine("  " + (info == null ? "基准 场上没有 InfoCanvas,没法对照" : WiringLine("基准 ", info)));
        if (info == null) { bad++; return; }
        BadIf(hudGo.layer != info.layer,
            "画布 layer 与基准一致:" + hudGo.layer + "(" + LayerMask.LayerToName(hudGo.layer) + ")");

        // 决定"手柄射线会不会被挡"的三项(XRI 2.6.5 的 TrackedDeviceGraphicRaycaster 没有 m_EventMask,
        // 上一版按 eventMask 比是找错了字段,两项都读不到反而一直误报未过)。
        var tdH = hudGo.GetComponent<TrackedDeviceGraphicRaycaster>();
        var tdI = info.GetComponent<TrackedDeviceGraphicRaycaster>();
        string[] fields = { "m_IgnoreReversedGraphics", "m_CheckFor3DOcclusion", "m_BlockingMask" };
        var a = new StringBuilder();
        bool same = tdH != null && tdI != null;
        foreach (var f in fields)
        {
            string x = Field(tdH, f), y = Field(tdI, f);
            if (x == y) { a.Append(f.Substring(2) + "=" + x + "  "); }
            else { same = false; a.Append("!!" + f.Substring(2) + "=" + x + "(基准 " + y + ")  "); }
        }
        BadIf(!same, "手柄射线的遮挡三项与基准一致:" + a);
        sb.AppendLine("  (以上只到\"接线一致\"这一级;真机射线命中要在头显里点一次才算验过)");
    }

    /// <summary>按序列化字段名读一个值,读不到就明写,免得又拿"空"当成"一致"。</summary>
    static string Field(Component c, string field)
    {
        if (c == null) return "(没这个组件)";
        var p = new SerializedObject(c).FindProperty(field);
        if (p == null) return "(没这个字段)";
        if (p.propertyType == SerializedPropertyType.Boolean) return p.boolValue ? "开" : "关";
        // LayerMask 与 Integer 都按值比:只返回类型名的话两边永远"一致",判据形同虚设
        if (p.propertyType == SerializedPropertyType.Integer || p.propertyType == SerializedPropertyType.LayerMask)
            return "0x" + p.intValue.ToString("X8");
        return p.propertyType.ToString();
    }

    static string WiringLine(string tag, GameObject go)
    {
        var td = go.GetComponent<TrackedDeviceGraphicRaycaster>();
        return string.Format("{0}layer={1}({2})  手柄射线={3}  键鼠 raycaster={4}  反向忽略={5} 3D遮挡={6} 遮挡层={7}",
            tag, go.layer, LayerMask.LayerToName(go.layer), td != null,
            go.GetComponent<GraphicRaycaster>() != null,
            Field(td, "m_IgnoreReversedGraphics"), Field(td, "m_CheckFor3DOcclusion"), Field(td, "m_BlockingMask"));
    }

    // ─────────────────────────────────────────────────────── 复核

    static void Verify(GameObject root, LibraryHud hud, Canvas cv, CanvasGroup hudGroup, CanvasGroup menuGroup)
    {
        sb.AppendLine("  ── 复核 ─────────────────────────────");
        if (hud == null) { Bad("根物体上没有 LibraryHud"); return; }
        BadIf(cv == null || cv.renderMode != RenderMode.WorldSpace, "画布是 WorldSpace");
        BadIf(root.transform.parent == null || root.transform.parent.GetComponent<Camera>() == null,
              "挂在相机下:" + (root.transform.parent == null ? "无父级" : root.transform.parent.name));
        BadIf(root.GetComponentsInChildren<GraphicRaycaster>(true).Length != 1
              || root.GetComponentsInChildren<TrackedDeviceGraphicRaycaster>(true).Length != 1,
            "键鼠与手柄射线各一份 raycaster");
        BadIf(hudGroup == null || menuGroup == null, "两级 CanvasGroup(hudGroup 淡隐 / menuGroup 侧边栏)");
        BadIf(hud.origin == null, "origin 接到 XROrigin");
        BadIf(menuGroup != null && menuGroup.alpha > 0.01f, "初始菜单已收起(alpha="
              + menuGroup.alpha.ToString("0.00") + ")");

        for (int i = 0; i < hud.teleporters.Length; i++)
        {
            var t = hud.teleporters[i];
            BadIf(t == null || t.destination == null || t.keepRigHeight,
                "传送键" + (i + 1) + ":" + (t == null ? "没接" :
                    t.destination == null ? "落点空" :
                    t.destination.name + " @ " + Pos(t.destination) + " keepRigHeight=" + t.keepRigHeight));
        }

        var s = root.transform.Find("MenuPanel/PageSettings/VolSlider");
        var sl = s == null ? null : s.GetComponent<Slider>();
        BadIf(sl == null || sl.fillRect == null || sl.handleRect == null, "音量滑条的 fillRect/handleRect 都接上");
        BadIf(sl != null && Mathf.Abs(sl.value - LibraryHud.DefaultVolume) > 0.001f,
            "滑条建好就是 0.5(实为 " + (sl == null ? -1 : sl.value).ToString("0.00") + ")");

        int tips = 0;
        foreach (var h in root.GetComponentsInChildren<HudHoverTip>(true))
            if (h.tip != null && h.GetComponent<Button>() != null) tips++;
        BadIf(tips != 2, "射线悬停提示 2 条(设置/关机),实到 " + tips);
        BadIf(Deep(root.transform, "QuitConfirm") == null, "退出的两下确认那一行在");
        BadIf(root.transform.Find("BgmSlot") == null, "BGM 空槽在(音频放进 Resources/bgm 即自动播)");
        var bg = root.transform.Find("Backdrop");
        BadIf(bg == null || bg.gameObject.activeSelf, "backdrop 存在且初始不挡射线");
        BadIf(Deep(root.transform, "PageSettings") == null || Deep(root.transform, "PageSettings").gameObject.activeSelf,
            "设置页初始收起");

        int texts = 0, nullFont = 0;
        foreach (var t in root.GetComponentsInChildren<Text>(true))
        {
            texts++;
            if (t.font == null) nullFont++;
        }
        BadIf(nullFont != 0, "文字 " + texts + " 块都带字体(Play 里补不了,坑 1)");
        int btns = 0, wired = 0;
        foreach (var b in root.GetComponentsInChildren<Button>(true))
        {
            btns++;
            var so = new SerializedObject(b);
            var calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
            if (calls != null && calls.arraySize > 0) wired++;
        }
        BadIf(btns != 8 || wired != 8,
            "按钮 " + btns + " 颗、其中 " + wired + " 颗带持久化 onClick(应是 8/8)");
        sb.AppendLine("  ── 未过 " + bad + " 项 ─────────────────");
    }

    /// <summary>落点下方必须真踩得住:这块是跨层传送新开的口子,y 一改就可能传到板子底下或半空。</summary>
    static void ProbeGround(Transform d)
    {
        Physics.SyncTransforms();
        var hits = Physics.RaycastAll(new Ray(d.position + Vector3.up * 0.6f, Vector3.down), 1.4f);
        float best = -99f; string who = "(脚下空)";
        foreach (var h in hits)
        {
            if (h.point.y > d.position.y + 0.3f) continue;
            if (h.point.y > best) { best = h.point.y; who = h.collider.gameObject.name; }
        }
        bool ok = best > d.position.y - 0.6f;
        sb.AppendLine(string.Format("      落点下方打到「{0}」y={1:0.000}(目标 y={2:0.000}){3}",
            who, best, d.position.y, ok ? " 踩得住" : "  !! 落点悬空,人掉下去了"));
        if (!ok) bad++;
    }

    // ─────────────────────────────────────────────────────── 搭建小件

    static GameObject Backdrop(Transform parent)
    {
        var go = Stretch("Backdrop", parent, None);
        go.AddComponent<Button>().targetGraphic = go.GetComponent<Image>();
        go.SetActive(false);
        return go;
    }

    static Button MenuToggleButton(Transform parent)
    {
        var go = Box("MenuToggle", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(24f, -24f), new Vector2(92f, 92f), BtnIcon);
        for (int i = -1; i <= 1; i++)
            Box("Bar" + (i + 2), go.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, i * 22f), new Vector2(58f, 8f), Ink);
        var b = go.AddComponent<Button>();
        b.targetGraphic = go.GetComponent<Image>();
        Paint(b);
        return b;
    }

    static void Minimap(Transform parent, out RectTransform dial, out Text floorLabel)
    {
        var box = Box("Minimap", parent, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-24f, -24f), new Vector2(168f, 210f), None);
        var ring = BoxSprite("Ring", box.transform, "Assets/Textures/HudDialRing.png",
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 0f), new Vector2(168f, 168f), InkDim);
        var rt = Box("Dial", ring.transform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero, None);
        dial = rt.GetComponent<RectTransform>();
        // 针从盘心朝上;盘的 localRotation 由 LibraryHud 按相机 yaw 反向转 ⇒ 针尖永远指真实北方(本工程北 = -Z)
        Box("Needle", rt.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f),
            new Vector2(0f, 0f), new Vector2(7f, 46f), new Color(0.98f, 0.72f, 0.36f, 1f));
        Label("North", rt.transform, "北", 26, Ink, TextAnchor.MiddleCenter,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 64f), new Vector2(56f, 34f));
        floorLabel = Label("FloorLabel", box.transform, "图书馆 1 楼", 30, Ink, TextAnchor.UpperCenter,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
            new Vector2(0f, -172f), new Vector2(220f, 36f));
    }

    static GameObject MenuPanel(Transform parent, out CanvasGroup menuGroup, out GameObject pageHome,
        out GameObject pageSettings, out Slider volSlider, out Text volLabel, out GameObject quitConfirm,
        out Button[] tpButtons, out Button btnSettings, out Button btnQuit,
        out GameObject tipSettings, out GameObject tipQuit)
    {
        var panel = Box("MenuPanel", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(16f, -132f), new Vector2(PanelW, PanelH), BgPanel);
        menuGroup = panel.AddComponent<CanvasGroup>();
        menuGroup.alpha = 0f;               // 靠 alpha + blocksRaycasts 收,不靠 SetActive(收了运行时没法再淡入)
        menuGroup.interactable = false;
        menuGroup.blocksRaycasts = false;

        var home = Box("PageHome", panel.transform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero, None);
        pageHome = home;
        Label("Title", home.transform, "传送到", 32, Ink, TextAnchor.MiddleLeft,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -14f), new Vector2(-40f, 40f));

        string[] labels = { "图书馆 1 楼", "图书馆 2 楼", "星空影台" };
        tpButtons = new Button[3];
        for (int i = 0; i < 3; i++)
            tpButtons[i] = PushButton("Btn_" + (i + 1), home.transform, labels[i], -58f - i * 90f);

        btnSettings = IconButton("Btn_Settings", home.transform, "Assets/Textures/HudIconSettings.png",
            new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(26f, 20f));
        btnQuit = IconButton("Btn_Quit", home.transform, "Assets/Textures/HudIconQuit.png",
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-26f, 20f));

        quitConfirm = Label("QuitConfirm", home.transform, "再按一次确认退出应用", 26,
            new Color(1f, 0.80f, 0.45f, 1f), TextAnchor.MiddleCenter,
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -330f), new Vector2(320f, 30f)).gameObject;
        quitConfirm.SetActive(false);

        // 提示写在图标旁边而不是上方:上方那一条要给"再按一次确认退出"留位置
        tipSettings = Label("TipSettings", home.transform, "打开设置", 24, Ink, TextAnchor.MiddleLeft,
            new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(112f, 42f), new Vector2(190f, 30f)).gameObject;
        tipQuit = Label("TipQuit", home.transform, "退出应用", 24, Ink, TextAnchor.MiddleRight,
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-112f, 42f), new Vector2(190f, 30f)).gameObject;
        tipSettings.SetActive(false);
        tipQuit.SetActive(false);

        var set = Box("PageSettings", panel.transform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero, None);
        pageSettings = set;
        pageSettings.SetActive(false);
        Label("Title", set.transform, "设置", 32, Ink, TextAnchor.MiddleLeft,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -14f), new Vector2(-40f, 40f));
        Label("Hint", set.transform, "总音量(同时影响 BGM 与影片声)", 22, InkDim, TextAnchor.MiddleLeft,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -58f), new Vector2(-40f, 30f));
        volLabel = Label("VolLabel", set.transform, "总音量 50%", 28, Ink, TextAnchor.MiddleLeft,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(20f, -96f), new Vector2(-40f, 36f));
        volSlider = SliderAt("VolSlider", set.transform, -150f);
        PushButton("Btn_Back", set.transform, "返回菜单", -210f);
        return panel;
    }

    static Slider SliderAt(string name, Transform parent, float topOffset)
    {
        var bg = Box(name, parent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, topOffset), new Vector2(300f, 30f), new Color(0.12f, 0.18f, 0.26f, 1f));
        var s = bg.AddComponent<Slider>();
        var fillArea = Box("Fill Area", bg.transform, new Vector2(0f, 0f), new Vector2(1f, 1f),
            new Vector2(0.5f, 0.5f), new Vector2(8f, 0f), new Vector2(-16f, 0f), None);
        var fill = Box("Fill", fillArea.transform, new Vector2(0f, 0f), new Vector2(0f, 1f),
            new Vector2(1f, 0.5f), Vector2.zero, Vector2.zero, new Color(0.30f, 0.78f, 0.90f, 1f));
        s.fillRect = fill.GetComponent<RectTransform>();
        var area = Box("Handle Slide Area", bg.transform, new Vector2(0f, 0f), new Vector2(1f, 1f),
            new Vector2(0.5f, 0.5f), new Vector2(10f, 0f), new Vector2(-20f, 0f), None);
        var handle = Box("Handle", area.transform, new Vector2(0f, 0f), new Vector2(0f, 1f),
            new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(20f, 0f), Ink);
        s.handleRect = handle.GetComponent<RectTransform>();
        s.targetGraphic = handle.GetComponent<Image>();
        return s;
    }

    static Button PushButton(string name, Transform parent, string label, float topOffset)
    {
        var go = Box(name, parent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, topOffset), new Vector2(300f, 78f), BtnBg);
        var b = go.AddComponent<Button>();
        b.targetGraphic = go.GetComponent<Image>();
        Paint(b);
        Label("Text", go.transform, label, 30, Ink, TextAnchor.MiddleCenter,
            Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        return b;
    }

    static Button IconButton(string name, Transform parent, string spritePath,
        Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos)
    {
        var go = Box(name, parent, aMin, aMax, pivot, pos, new Vector2(72f, 72f), BtnIcon);
        BoxSprite("Icon", go.transform, spritePath, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero, Ink);
        var b = go.AddComponent<Button>();
        b.targetGraphic = go.GetComponent<Image>();
        Paint(b);
        return b;
    }

    static void HoverTip(Button btn, GameObject tip, string expect)
    {
        if (btn == null || tip == null) { sb.AppendLine("  !! 悬停提示接不上(" + expect + ")"); bad++; return; }
        var h = btn.gameObject.AddComponent<HudHoverTip>();
        h.tip = tip;
        var t = tip.GetComponent<Text>();
        if (t == null || t.text != expect)
            sb.AppendLine("  !! 提示文案不是「" + expect + "」,实为「" + (t == null ? "(不是 Text)" : t.text) + "」");
    }

    /// <summary>BGM 只是个槽:clip 由 LibraryHud 在运行时从 Resources/bgm 找,文件后补即自动接上。</summary>
    static void BgmSlot(Transform parent)
    {
        var go = New("BgmSlot", parent);
        var a = go.AddComponent<AudioSource>();
        a.loop = true;
        a.playOnAwake = false;
        a.spatialBlend = 0f;        // 2D:不跟随任何位置,才不会跟着脑袋转
        a.volume = 1f;              // 打折交给 AudioListener.volume,这一路不再二次衰减
    }

    static void Paint(Button b)
    {
        var c = b.colors;
        c.highlightedColor = new Color(0.15f, 0.55f, 0.75f);
        c.pressedColor = new Color(0.08f, 0.35f, 0.5f);
        c.selectedColor = c.highlightedColor;
        b.colors = c;
    }

    // ─────────────────────────────────────────────────────── 三张小图

    static void EnsureIcons()
    {
        WritePngIfMissing("Assets/Textures/HudDialRing.png", 256, DialGlyph);
        WritePngIfMissing("Assets/Textures/HudIconSettings.png", 128, GearGlyph);
        WritePngIfMissing("Assets/Textures/HudIconQuit.png", 128, PowerGlyph);
        AssetDatabase.Refresh();
    }

    static void WritePngIfMissing(string path, int n, System.Func<float, float, bool> glyph)
    {
        string full = Abs(path);
        if (File.Exists(full) && new FileInfo(full).Length > 128) return;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        var px = new Color32[n * n];
        float c = (n - 1) * 0.5f, R = n * 0.46f;
        var solid = new Color32(255, 255, 255, 255);
        var clear = new Color32(255, 255, 255, 0);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                px[y * n + x] = glyph((x - c) / R, (y - c) / R) ? solid : clear;
        tex.SetPixels32(px);
        tex.Apply();
        File.WriteAllBytes(full, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        sb.AppendLine("  [小图] 生成 " + path + " " + n + "²(无 mipmap、alpha 当透明、三线性)");
    }

    static float Abs(float v) { return Mathf.Abs(v); }
    static float Len(float x, float y) { return Mathf.Sqrt(x * x + y * y); }
    /// <summary>角度:0 = 正上方,顺时针增(与"北在顶上"的直觉一致)。</summary>
    static float Bearing(float x, float y)
    {
        float a = Mathf.Atan2(x, y) * Mathf.Rad2Deg;
        return a < 0f ? a + 360f : a;
    }

    static bool DialGlyph(float x, float y)
    {
        float r = Len(x, y), d = Bearing(x, y);
        bool ring = r > 0.90f && r < 0.99f;
        bool tick = r > 0.74f && r < 0.90f && (d % 90f) < 5f;
        bool north = r > 0.60f && r < 0.90f && d < 4f;
        return ring || tick || north;
    }

    static bool GearGlyph(float x, float y)
    {
        float r = Len(x, y), d = Bearing(x, y);
        bool tooth = r < 1f && r > 0.62f && (d % 45f) < 22f;
        bool ring = r > 0.40f && r < 0.66f;
        return tooth || ring;
    }

    static bool PowerGlyph(float x, float y)
    {
        float r = Len(x, y), d = Bearing(x, y);
        bool ring = r > 0.52f && r < 0.74f && d > 30f && d < 330f;      // 顶部留一道开口
        bool stem = Abs(x) < 0.11f && y > 0.05f && y < 0.92f;
        return ring || stem;
    }

    static Sprite LoadSprite(string path)
    {
        var imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp == null) { sb.AppendLine("  !! " + path + " 没进库"); bad++; return null; }
        bool dirty = imp.textureType != TextureImporterType.Sprite || imp.mipmapEnabled
                     || imp.filterMode != FilterMode.Trilinear || !imp.alphaIsTransparency
                     || imp.maxTextureSize != 256 || imp.wrapMode != TextureWrapMode.Clamp;
        if (dirty)
        {
            imp.textureType = TextureImporterType.Sprite;
            imp.spriteImportMode = SpriteImportMode.Single;
            imp.alphaIsTransparency = true;
            imp.mipmapEnabled = false;
            imp.filterMode = FilterMode.Trilinear;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.maxTextureSize = 256;
            imp.SaveAndReimport();
        }
        var sp = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sp == null) { sb.AppendLine("  !! 没能把 " + path + " 导成 Sprite"); bad++; }
        return sp;
    }

    // ─────────────────────────────────────────────────────── 通用小件

    static GameObject New(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go;
    }

    static GameObject Box(string name, Transform parent, Vector2 aMin, Vector2 aMax, Vector2 pivot,
        Vector2 pos, Vector2 size, Color bg)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot;
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        if (bg.a > 0f) go.AddComponent<Image>().color = bg;
        return go;
    }

    static GameObject Stretch(string name, Transform parent, Color c)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        go.AddComponent<Image>().color = c;
        return go;
    }

    static GameObject BoxSprite(string name, Transform parent, string path, Vector2 aMin, Vector2 aMax,
        Vector2 pivot, Vector2 pos, Vector2 size, Color c)
    {
        EnsureIcons();
        var go = Box(name, parent, aMin, aMax, pivot, pos, size, c);
        var img = go.GetComponent<Image>() ?? go.AddComponent<Image>();
        img.sprite = LoadSprite(path);
        img.preserveAspect = true;
        return go;
    }

    static Text Label(string name, Transform parent, string content, int size, Color c, TextAnchor anchor,
        Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 sizeDelta)
    {
        var go = Box(name, parent, aMin, aMax, pivot, pos, sizeDelta, None);
        var t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.text = content;
        t.fontSize = size;
        t.color = c;
        t.alignment = anchor;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    static Transform Deep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static T Back<T>(Transform root, string name) where T : Component
    {
        var t = Deep(root, name);
        return t == null ? null : t.GetComponent<T>();
    }

    static int Wipe(Transform cam)
    {
        int n = 0;
        for (int i = cam.childCount - 1; i >= 0; i--)
            if (cam.GetChild(i).name == kHudName) { Object.DestroyImmediate(cam.GetChild(i).gameObject, true); n++; }
        return n;
    }

    static void Bind(Button btn, MonoBehaviour target, string method)
    {
        if (btn == null || target == null) { sb.AppendLine("  !! 按钮没接上:" + method); bad++; return; }
        var so = new SerializedObject(btn);
        var calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
        if (calls == null) { sb.AppendLine("  !! 找不到 m_OnClick 序列化路径:" + method); bad++; return; }
        AddCall(calls, target, method);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void BindFloat(Slider s, MonoBehaviour target, string method)
    {
        if (s == null) { sb.AppendLine("  !! 滑条不在,没接:" + method); bad++; return; }
        var so = new SerializedObject(s);
        var calls = so.FindProperty("m_OnValueChanged.m_PersistentCalls.m_Calls");
        if (calls == null) { sb.AppendLine("  !! 找不到 m_OnValueChanged:" + method); bad++; return; }
        AddCall(calls, target, method);
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void AddCall(SerializedProperty calls, MonoBehaviour target, string method)
    {
        for (int i = 0; i < calls.arraySize; i++)
        {
            var c = calls.GetArrayElementAtIndex(i);
            if (c.FindPropertyRelative("m_MethodName").stringValue == method
                && c.FindPropertyRelative("m_Target").objectReferenceValue == target) return;   // 幂等
        }
        int idx = calls.arraySize;
        calls.InsertArrayElementAtIndex(idx);
        var e = calls.GetArrayElementAtIndex(idx);
        e.FindPropertyRelative("m_Target").objectReferenceValue = target;
        e.FindPropertyRelative("m_MethodName").stringValue = method;
        e.FindPropertyRelative("m_Mode").intValue = 0;          // 0 = EventDefined(没参的 onClick 与带 float 的滑条都是这一档)
        e.FindPropertyRelative("m_CallState").intValue = 2;     // 2 = None
        var arg = e.FindPropertyRelative("m_Arguments.m_ObjectArgumentAssemblyTypeName");
        if (arg != null) arg.stringValue = "UnityEngine.Object, UnityEngine";
    }

    // ─────────────────────────────────────────────────────── 杂项

    static void Snapshot()
    {
        const string dst = "Logs/SmartLibrary.pre-hud.unity";
        string src = EditorSceneManager.GetActiveScene().path;
        if (!File.Exists(Abs(dst)))
        {
            Directory.CreateDirectory(Abs("Logs"));
            File.WriteAllBytes(Abs(dst), File.ReadAllBytes(src));
            sb.AppendLine("  改前快照:" + Abs(dst) + "(" + new FileInfo(Abs(dst)).Length.ToString("N0") + " B)");
        }
        else sb.AppendLine("  改前快照已存在,不覆盖:" + Abs(dst));
    }

    static Transform FindAny(string nameOrPath)
    {
        var go = GameObject.Find(nameOrPath);
        if (go != null) return go.transform;
        string leaf = nameOrPath.IndexOf('/') >= 0 ? nameOrPath.Substring(nameOrPath.LastIndexOf('/') + 1) : nameOrPath;
        foreach (var t in Object.FindObjectsOfType<Transform>())
            if (t.name == leaf && t.name != kHudName) return t;
        return null;
    }

    static XROrigin FindOrigin()
    {
        foreach (var x in Object.FindObjectsOfType<XROrigin>())
            if (x.Camera != null) return x;
        return null;
    }

    static string Pos(Transform t)
    {
        var p = t.position;
        return "(" + p.x.ToString("0.0") + "," + p.y.ToString("0.0") + "," + p.z.ToString("0.0") + ")";
    }

    static string Tree(Transform t)
    {
        string s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }

    static void Bad(string what) { bad++; sb.AppendLine("  !! " + what); }
    static void BadIf(bool cond, string what)
    {
        if (cond) { bad++; sb.AppendLine("  !! " + what); }
        else sb.AppendLine("  ✓ " + what);
    }

    /// <summary>
    /// 一律换成工程绝对路径。编辑器刚编译完时进程 CWD 会漂到 Library/Bee/&lt;hash&gt; 底下
    /// (2026-09-22 实测 Environment.CurrentDirectory = ...X0b0aEP),
    /// 相对路径的报告会写进那个没人会去看的目录里,看着就像"菜单没跑"。
    /// </summary>
    static string Abs(string rel)
    {
        return System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Application.dataPath), rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    static void Flush(string path)
    {
        string full = Abs(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
        File.WriteAllText(full, sb.ToString(), Encoding.UTF8);
        sb.AppendLine("  报告写在:" + full);
    }
}
