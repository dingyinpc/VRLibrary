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
    const string kRootPath = "SmartLibrary";
    const string kHudName = "LibraryHud";

    // uGUI 屏幕空间 Overlay:画布就是整块眼缓冲,所以"左上角/右上角"是真的贴到屏幕角上。
    // (上一版是挂在相机前 1.85 m 的世界空间画布,实拍里两块挤在视野正中一条上 —— 用户直接指出"放中间算什么"。)
    // 按宽匹配 ⇒ 参考宽恒为 RefW,侧边栏 = RefW/3 就是"屏幕宽的 1/3",与分辨率无关。
    // 字号换算:30 px = 参考宽的 1.9%;PICO 水平 FOV 约 105° ⇒ 字高约 1.9°,在 VR 可读下限之上。
    const float RefW = 1600f;
    const float PanelFrac = 1f / 3f;
    const float PanelRefW = RefW * PanelFrac;      // = 533.3 参考像素

    // 中心锚点(anchorMin==anchorMax)的 sizeDelta 就是**实际尺寸**,不是"离父级差多少";
    // 只有拉伸锚点(0→1)才是差值。上一版把 -60 直接写进中心锚点的按钮,宽就成了 -60 ⇒ 整排按钮不可见。
    const float ItemW = PanelRefW - 60f;

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

        var sceneRoot = GameObject.Find(kRootPath);
        if (sceneRoot == null)
        {
            sb.AppendLine("  !! 找不到场景根 " + kRootPath + " → 没建任何东西");
            bad++;
            Flush("Logs/hud-setup.txt");
            return;
        }

        int wiped = Wipe(sceneRoot.transform);
        var origin = FindOrigin();
        if (origin == null) sb.AppendLine("  !! 场上没有可用的 XROrigin → 传送按钮要手动接 origin");

        var root = New(kHudName, sceneRoot.transform);
        var hud = root.AddComponent<LibraryHud>();
        var cv = root.AddComponent<Canvas>();
        cv.renderMode = RenderMode.ScreenSpaceOverlay;
        cv.sortingOrder = 100;                        // Overlay 不吃 overrideSorting,靠 sortingOrder 压在其它 UI 上
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(RefW, RefW);
        scaler.matchWidthOrHeight = 0f;               // 按宽匹配:侧边栏的 1/3 才在任何分辨率上都是 1/3
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
        // 菜单键与小地图排到最后 ⇒ 永远画在侧边栏之上(否则侧栏一开就把菜单键盖住,"再点一次收起"点不到)。
        // 必须在所有兄弟都建完之后做:早先放在 BgmSlot 之前重排,后者一建就又把菜单键挤了一位。
        toggle.transform.SetAsLastSibling();
        root.transform.Find("Minimap").SetAsLastSibling();

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
        var root = GameObject.Find(kRootPath + "/" + kHudName);
        if (root == null)
        {
            sb.AppendLine("  !! 场上没有 " + kHudName + " → 跑 VRLibrary/装小地图与菜单");
            bad++;
            Flush("Logs/hud-verify.txt");
            return;
        }
        sb.AppendLine("  节点:" + Tree(root.transform) + "  (Overlay 画布,父级只是归档,不参与定位)");
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
        BadIf(cv == null || cv.renderMode != RenderMode.ScreenSpaceOverlay, "画布是 ScreenSpaceOverlay");
        var scaler = root.GetComponent<CanvasScaler>();
        BadIf(scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize
              || Mathf.Abs(scaler.matchWidthOrHeight) > 0.01f,
              "CanvasScaler 按宽匹配(侧边栏的 1/3 才与分辨率无关)");
        // "在不在角上"要能判伪,但**不能拿 Canvas.scaleFactor 换算**:编辑态它是未刷新的缓存值
        // (实测读出 1,于是 2855 的屏幕宽被当成参考宽,量出"侧边栏只占 19%"这种假结论)。
        // matchWidthOrHeight=0 ⇒ 参考宽恒等于 referenceResolution.x;高度按视口比例换算一次即可。
        float cw = scaler == null ? RefW : scaler.referenceResolution.x;
        float ch = cw;
        if (scaler != null)
        {
            var srt = cv == null ? null : cv.GetComponent<RectTransform>();
            if (srt != null && srt.rect.width > 1f) ch = cw * srt.rect.height / srt.rect.width;
        }
        var rToggle = CornerOf(root.transform.Find("MenuToggle") as RectTransform, cw, ch);
        var rMini = CornerOf(root.transform.Find("Minimap") as RectTransform, cw, ch);
        var rPanel = AnchorSpanOf(root.transform.Find("MenuPanel") as RectTransform, cw, ch);
        sb.AppendLine(string.Format("    画布参考尺寸 {0:0}×{1:0}  菜单键 x{2:0} y顶{3:0}  小地图 x右{4:0}  侧栏宽{5:0}(占 {6:0}%) 侧栏高{7:0}",
            cw, ch, rToggle.xMin, ch - rToggle.yMax, rMini.xMax, rPanel.width,
            rPanel.width / Mathf.Max(1e-3f, cw) * 100f, rPanel.height));
        BadIf(rToggle.xMin > 60f || ch - rToggle.yMax > 60f, "菜单键贴在左上角(距两边 ≤60 参考像素)");
        BadIf(cw - rMini.xMax > 60f || ch - rMini.yMax > 60f, "小地图贴在右上角(距两边 ≤60 参考像素)");
        BadIf(Mathf.Abs(rPanel.width - cw / 3f) > cw * 0.02f, "侧边栏宽 = 屏幕 1/3(±2%)");
        BadIf(Mathf.Abs(rPanel.height - ch) > ch * 0.02f, "侧边栏占满上下(±2%)");
        // 中心锚点写错 sizeDelta 会让整排按钮宽成负数 ⇒ 场上"东西都在"却一个看不见(实拍抓到过)。
        // 阈值必须"变得出红":上一版写"宽<60 算小",而 -60 取绝对值正好是 60,卡在边界上照样绿。
        // 所以这里每项给"该有多宽"的下限,并把实测宽全打出来。
        object[][] items = {
            new object[] { "MenuPanel/PageHome/Btn_1", 0.25f },
            new object[] { "MenuPanel/PageHome/Btn_2", 0.25f },
            new object[] { "MenuPanel/PageHome/Btn_3", 0.25f },
            new object[] { "MenuPanel/PageSettings/VolSlider", 0.25f },
            new object[] { "MenuPanel/PageHome/QuitConfirm", 0.25f },
            new object[] { "MenuPanel/PageSettings/Btn_Back", 0.25f },
            new object[] { "Minimap/Ring", 0.10f },          // 190/1600 = 11.9%
            new object[] { "Minimap/FloorLabel", 0.20f },    // 360/1600 = 22.5%
        };
        var tooSmall = new System.Text.StringBuilder();
        var measured = new System.Text.StringBuilder();
        foreach (var it in items)
        {
            string pn = (string)it[0];
            var rt = root.transform.Find(pn) as RectTransform;
            var r = RefRect(rt, cw, ch);
            measured.Append(pn.Substring(pn.LastIndexOf('/') + 1) + "=" + r.width.ToString("0")
                            + "×" + r.height.ToString("0") + "  ");
            if (rt == null || r.width < (float)it[1] * cw || r.height < 24f)
                tooSmall.Append(pn + " ");
        }
        sb.AppendLine("    实测尺寸:" + measured);
        var rTog = CornerOf(root.transform.Find("MenuToggle") as RectTransform, cw, ch);
        var overl = new System.Text.StringBuilder();
        foreach (var tn in new[] { "MenuPanel/PageHome/Title", "MenuPanel/PageSettings/Title" })
        {
            var r = CornerOf(root.transform.Find(tn) as RectTransform, cw, ch);
            if (r.xMin < rTog.xMax && r.xMax > rTog.xMin && r.yMin < rTog.yMax && r.yMax > rTog.yMin)
                overl.Append(tn + " ");
        }
        BadIf(overl.Length > 0, "两页标题都不与菜单键重叠" + (overl.Length > 0 ? ",压住:" + overl.ToString() : ""));
        BadIf(tooSmall.Length > 0, "8 个可见件都够大(宽≥各自下限、高≥24)"
              + (tooSmall.Length > 0 ? ",不合格:" + tooSmall.ToString() : ""));
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
        // 真正要成立的是"菜单键画在侧边栏之上",与它排倒数第几无关(uGUI 同级后建的画在上面)
        var tog = root.transform.Find("MenuToggle");
        var pnl = root.transform.Find("MenuPanel");
        BadIf(tog == null || pnl == null || tog.GetSiblingIndex() <= pnl.GetSiblingIndex(),
            "菜单键排在侧边栏之后(侧栏展开时仍点得到它);实测 toggle="
            + (tog == null ? "-" : tog.GetSiblingIndex().ToString())
            + " panel=" + (pnl == null ? "-" : pnl.GetSiblingIndex().ToString()));

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
            new Vector2(28f, -28f), new Vector2(104f, 104f), BtnIcon);
        for (int i = -1; i <= 1; i++)
            Box("Bar" + (i + 2), go.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, i * 26f), new Vector2(64f, 9f), Ink);
        var b = go.AddComponent<Button>();
        b.targetGraphic = go.GetComponent<Image>();
        Paint(b);
        return b;
    }

    static void Minimap(Transform parent, out RectTransform dial, out Text floorLabel)
    {
        var box = Box("Minimap", parent, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-28f, -28f), new Vector2(360f, 246f), None);
        // pivot 必须是 (0.5,1) 让它从顶往下挂;写成 (0.5,0.5) 会把圆盘上半截顶到屏幕外(实拍已见过)
        var ring = BoxSprite("Ring", box.transform, "Assets/Textures/HudDialRing.png",
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, 0f), new Vector2(190f, 190f), InkDim);
        var rt = Box("Dial", ring.transform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero, None);
        dial = rt.GetComponent<RectTransform>();
        // 针从盘心朝上;盘的 localRotation 由 LibraryHud 按相机 yaw 反向转 ⇒ 针尖永远指真实北方(本工程北 = -Z)
        Box("Needle", rt.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f),
            new Vector2(0f, 0f), new Vector2(8f, 52f), new Color(0.98f, 0.72f, 0.36f, 1f));
        Label("North", rt.transform, "北", 30, Ink, TextAnchor.MiddleCenter,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 72f), new Vector2(60f, 38f));
        // 用户要"小地图下面标着本地位置":这行由 LibraryHud 每帧写成"当前位置 · 图书馆 2 楼"
        floorLabel = Label("FloorLabel", box.transform, "当前位置 · 图书馆 1 楼", 32, Ink, TextAnchor.UpperCenter,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
            new Vector2(0f, -196f), new Vector2(360f, 40f));
    }

    static GameObject MenuPanel(Transform parent, out CanvasGroup menuGroup, out GameObject pageHome,
        out GameObject pageSettings, out Slider volSlider, out Text volLabel, out GameObject quitConfirm,
        out Button[] tpButtons, out Button btnSettings, out Button btnQuit,
        out GameObject tipSettings, out GameObject tipQuit)
    {
        // 占满左边上下、宽 = 屏幕 1/3:靠锚点算,不写死像素 ⇒ 换分辨率比例不变
        var panel = Box("MenuPanel", parent, new Vector2(0f, 0f), new Vector2(PanelFrac, 1f),
            new Vector2(0f, 0.5f), Vector2.zero, Vector2.zero, BgPanel);
        menuGroup = panel.AddComponent<CanvasGroup>();
        menuGroup.alpha = 0f;               // 靠 alpha + blocksRaycasts 收,不靠 SetActive(收了运行时没法再淡入)
        menuGroup.interactable = false;
        menuGroup.blocksRaycasts = false;

        var home = Box("PageHome", panel.transform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero, None);
        pageHome = home;
        Label("Title", home.transform, "传送到", 38, Ink, TextAnchor.MiddleLeft,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(30f, -148f), new Vector2(-60f, 52f));

        string[] labels = { "图书馆 1 楼", "图书馆 2 楼", "星空影台" };
        tpButtons = new Button[3];
        for (int i = 0; i < 3; i++)
            tpButtons[i] = PushButton("Btn_" + (i + 1), home.transform, labels[i], (1 - i) * 118f);

        btnSettings = IconButton("Btn_Settings", home.transform, "Assets/Textures/HudIconSettings.png",
            new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(30f, 28f));
        btnQuit = IconButton("Btn_Quit", home.transform, "Assets/Textures/HudIconQuit.png",
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-30f, 28f));

        quitConfirm = Label("QuitConfirm", home.transform, "再按一次确认退出应用", 30,
            new Color(1f, 0.80f, 0.45f, 1f), TextAnchor.MiddleCenter,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, 152f), new Vector2(ItemW, 36f)).gameObject;
        quitConfirm.SetActive(false);

        // 提示写在图标旁边而不是上方:上方那一条要给"再按一次确认退出"留位置
        tipSettings = Label("TipSettings", home.transform, "打开设置", 28, Ink, TextAnchor.MiddleLeft,
            new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f),
            new Vector2(134f, 56f), new Vector2(220f, 34f)).gameObject;
        tipQuit = Label("TipQuit", home.transform, "退出应用", 28, Ink, TextAnchor.MiddleRight,
            new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-134f, 56f), new Vector2(220f, 34f)).gameObject;
        tipSettings.SetActive(false);
        tipQuit.SetActive(false);

        var set = Box("PageSettings", panel.transform, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
            Vector2.zero, Vector2.zero, None);
        pageSettings = set;
        pageSettings.SetActive(false);
        Label("Title", set.transform, "设置", 38, Ink, TextAnchor.MiddleLeft,
            new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            new Vector2(30f, -148f), new Vector2(-60f, 52f));
        Label("Hint", set.transform, "总音量(同时影响 BGM 与影片声)", 26, InkDim, TextAnchor.MiddleLeft,
            new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 1f),
            new Vector2(30f, 128f), new Vector2(-60f, 34f));
        volLabel = Label("VolLabel", set.transform, "总音量 50%", 32, Ink, TextAnchor.MiddleLeft,
            new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 1f),
            new Vector2(30f, 82f), new Vector2(-60f, 40f));
        volSlider = SliderAt("VolSlider", set.transform, 0f);
        PushButton("Btn_Back", set.transform, "返回菜单", -118f);
        return panel;
    }

    static Slider SliderAt(string name, Transform parent, float topOffset)
    {
        var bg = Box(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, topOffset), new Vector2(ItemW, 38f), new Color(0.12f, 0.18f, 0.26f, 1f));
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
        var go = Box(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0f, topOffset), new Vector2(ItemW, 96f), BtnBg);
        var b = go.AddComponent<Button>();
        b.targetGraphic = go.GetComponent<Image>();
        Paint(b);
        Label("Text", go.transform, label, 34, Ink, TextAnchor.MiddleCenter,
            Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        return b;
    }

    static Button IconButton(string name, Transform parent, string spritePath,
        Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos)
    {
        var go = Box(name, parent, aMin, aMax, pivot, pos, new Vector2(88f, 88f), BtnIcon);
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

    /// <summary>
    /// 按名先拆后建。v7.26 从"挂在 Main Camera 前的世界空间画布"迁到 Overlay(挂场景根下),
    /// 旧那份还留在相机下 ⇒ 两个位置都要清,否则场上会有两份 LibraryHud(两份抢排序、两份都响应同一批按钮)。
    /// </summary>
    static int Wipe(Transform sceneRoot)
    {
        int n = 0;
        var places = new System.Collections.Generic.List<Transform> { sceneRoot };
        var camGo = GameObject.Find("SmartLibrary/XR Interaction Setup/XR Origin (XR Rig)/Camera Offset/Main Camera");
        if (camGo != null) places.Add(camGo.transform);
        foreach (var t in places)
            for (int i = t.childCount - 1; i >= 0; i--)
                if (t.GetChild(i).name == kHudName) { Object.DestroyImmediate(t.GetChild(i).gameObject, true); n++; }
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

    /// <summary>
    /// 贴在角上的件:直接由"锚点 + anchoredPosition + 尺寸"推出它在参考单位下的矩形。
    /// 这条路完全不碰 Canvas.scaleFactor,所以编辑态与 Play 里读出来是同一个数。
    /// </summary>
    static Rect CornerOf(RectTransform rt, float cw, float ch)
    {
        if (rt == null) return new Rect(-1e6f, -1e6f, 0f, 0f);
        // 点锚与拉伸锚都要加上 anchoredPosition:上一版点锚分支漏加,
        // 于是"菜单键贴在左上角"对它形同虚设(不管挪到哪都算 x=0 通过)。
        float ax = rt.anchorMin.x * cw + rt.anchoredPosition.x;
        float ay = rt.anchorMin.y * ch + rt.anchoredPosition.y;
        float px = rt.pivot.x * rt.rect.width, py = rt.pivot.y * rt.rect.height;
        float x0 = ax - px, y0 = ay - py;
        return Rect.MinMaxRect(x0, y0, x0 + rt.rect.width, y0 + rt.rect.height);
    }

    /// <summary>拉伸锚点的件(侧边栏):宽 = (anchorMax.x-anchorMin.x)×参考宽,高 = 满幅。</summary>
    static Rect AnchorSpanOf(RectTransform rt, float cw, float ch)
    {
        if (rt == null) return new Rect(-1e6f, -1e6f, 0f, 0f);
        float w = (rt.anchorMax.x - rt.anchorMin.x) * cw + rt.sizeDelta.x;
        float h = (rt.anchorMax.y - rt.anchorMin.y) * ch + rt.sizeDelta.y;
        return Rect.MinMaxRect(0f, 0f, w, h);
    }

    /// <summary>Overlay 画布上 RectTransform 的世界角点就是屏幕像素,除以 scaleFactor 回到参考单位。</summary>
    static Rect RefRect(RectTransform rt, float cw, float ch)
    {
        if (rt == null) return new Rect(-1e6f, -1e6f, 0f, 0f);
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        float f = 1f;
        var cvs = rt.GetComponentInParent<Canvas>();
        if (cvs != null && cvs.scaleFactor > 1e-4f) f = 1f / cvs.scaleFactor;
        float x0 = c[0].x * f, y0 = c[0].y * f, x1 = c[2].x * f, y1 = c[2].y * f;
        if (x1 < x0) { var t = x0; x0 = x1; x1 = t; }
        if (y1 < y0) { var t2 = y0; y0 = y1; y1 = t2; }
        return Rect.MinMaxRect(x0, y0, x1, y1);
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
