using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// 星空露台(二层外扩的露天影厅)。菜单 VRLibrary/搭建星空露台(会改场景)。
///
/// 需求定案(2026-09-20 与用户确认):
///   ① 月亮仰角降到 15°(原 55° 要仰头看,不符合"稍微抬头一点点");方位不变,仍在东北;
///   ② 露台**真扩到建筑外**(东北角外),不切墙不开洞 —— 这一区只靠传送到达;
///   ③ 大屏先做占位屏,以后把 mp4 丢进 Assets/StreamingAssets/ 自动上屏(TerraceScreen);
///   ④ 站 7 m 外看;屏框按 16:9 建(5.33×3 m),让 1920×1080 这类片源铺满整块屏。
///
/// 关键几何约束(所以屏高只有 3 m,不是随手定的):
///   屏与月亮在同一个方向上,屏太高就会把月亮挡住。
///   落地站在 7 m 外看 5.33×3(16:9)的屏,屏上沿仰角 ≈ 12.2°,月亮 15° → 从屏上沿露出来 2.8°。
///   想要 9 m 宽的 16:9(9×5.06)上沿会顶到 21°、月亮整个被屏吃掉 —— 所以只能**守住 3 m 高、
///   把宽从 9 m 收到 5.33 m**;要更大的屏得先把座位往后挪(现距 7 m)或改月亮方位。
///   本菜单每次跑完都把这些角度重新量一遍写进报告,改了距离/尺寸会立刻看出来。
///
/// 幂等:整棵 StarryTerrace 子树 + Pad_Terrace + TerracePanel + TerraceSign + 两个落地目标
/// 先按名删掉再重建;材质存在就复用(不覆盖手调过的参数)。
/// 回退:Logs/SmartLibrary.pre-terrace.unity 是本步之前的字节级快照(存在就不覆盖)。
///
/// v7.18 首版曾报 8 条编译错(CS1503×5 / CS1061×2 / CS0266),原因是把 `New()` 返回的 GameObject
/// 当 Transform 传、`PlacePad` 返回 GameObject 却按 `.position` 用、以及 `TextMesh.alignment`
/// 错用了 UGUI 的 `TextAnchor`。三处都已就地修好;若控制台里还看得到这批条目,那是失败那次
/// 留下的旧记录 —— Unity 不会自动清,按 Console 右上角的 Clear(或 Ctrl+R 重编译)即可。
/// </summary>
public static class TerraceSetup
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Snapshot = "Logs/SmartLibrary.pre-terrace.unity";
    const string ReportPath = "Logs/terrace-setup.txt";
    const string MatDir = "Assets/Materials";

    const float MoonElevation = 15f;      // 定案①:主光俯角 = 月亮仰角
    const float SunAzimuth = -35f;        // 原值,方位不动
    const float ViewDist = 7f;            // 定案④:站 7 m 外
    // 屏高是**被月亮钉死的**:屏上沿仰角 = atan2(0.15+3-1.6, 7) ≈ 12.5°,月亮 15° → 净空只剩 +2.5°。
    // 所以 16:9 只能"守住 3 m 高、把宽从 9 m 收到 5.33 m";想要更大的屏得先把座位往后挪或让月亮改方位。
    const float ScreenH = 3f, ScreenW = ScreenH * 16f / 9f;   // 16:9 = 5.333×3 m,与 1920×1080 片源同比例
    const float Margin = 4.2f;            // 锚点离建筑外墙角的距离

    // ── 标高基准:不再写死,从场上活着的锚点反推 ───────────────────────────
    // v7.35 整馆拔高 +1.423 m 后,这里曾写死 DeckTop=3.0 / PadY=3.045(旧标高),
    // 重跑本菜单会把整块露台摆回拔高前的位置。现改为运行时解析。
    const float DeckTopFallback = 4.423f; // 露台板面顶(拔高后实测)
    const float PadYFallback = 4.468f;    // 露台锚点高 = 板面 + 0.045

    static float _deckTop = -1f, _padY = -1f;

    /// <summary>露台板面顶:优先由场上 Pad_Terrace 反推(锚点 = 板面 + 0.045),再退 TerraceDeck 顶面。</summary>
    static float DeckTop
    {
        get
        {
            if (_deckTop > 0f) return _deckTop;
            var pad = GameObject.Find("SmartLibrary/StarryTerrace/Pad_Terrace");
            if (pad != null) { _deckTop = pad.transform.position.y - 0.045f; return _deckTop; }
            var deck = GameObject.Find("SmartLibrary/StarryTerrace/TerraceDeck");
            if (deck != null)
            {
                var r = deck.GetComponent<Renderer>();
                if (r != null) { _deckTop = r.bounds.max.y; return _deckTop; }
            }
            Debug.LogWarning("[Terrace] 场上找不到 Pad_Terrace / TerraceDeck,回退兜底 DeckTop=" + DeckTopFallback);
            _deckTop = DeckTopFallback;
            return _deckTop;
        }
    }

    /// <summary>露台锚点高:优先沿用场上 Pad_Terrace,退二层回廊锚点。</summary>
    static float PadY
    {
        get
        {
            if (_padY > 0f) return _padY;
            var pad = GameObject.Find("SmartLibrary/StarryTerrace/Pad_Terrace");
            if (pad != null) { _padY = pad.transform.position.y; return _padY; }
            var a2f = GameObject.Find("SmartLibrary/Pad_2F_N_E");
            if (a2f != null) { _padY = a2f.transform.position.y; return _padY; }
            Debug.LogWarning("[Terrace] 场上找不到任何锚点,回退兜底 PadY=" + PadYFallback);
            _padY = PadYFallback;
            return _padY;
        }
    }

    static float _mezzDeckTop = -1f;

    /// <summary>二层环廊板面顶:由 Pd_2F_* 锚点反推(锚点 = 板面 + 0.045)。用于报告里比对露台是否与环廊齐平。</summary>
    static float MezzDeckTop
    {
        get
        {
            if (_mezzDeckTop > 0f) return _mezzDeckTop;
            var a2f = GameObject.Find("SmartLibrary/Pad_2F_N_E");
            if (a2f != null) { _mezzDeckTop = a2f.transform.position.y - 0.045f; return _mezzDeckTop; }
            _mezzDeckTop = DeckTop;   // 二层与露台同高,退化成同一基准
            return _mezzDeckTop;
        }
    }

    static StringBuilder sb;
    static Material matDeck, matFrame, matScreen, matPlaza, matRail, matGlow;

    [MenuItem("VRLibrary/搭建星空露台(会改场景)")]
    public static void Build()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            Debug.LogError("[Terrace] 当前场景不是 SmartLibrary:" + scene.path);
            return;
        }
        var root = GameObject.Find("SmartLibrary");
        if (root == null) { Debug.LogError("[Terrace] 找不到根节点 SmartLibrary"); return; }

        sb = new StringBuilder();
        sb.AppendLine("== 搭建星空露台 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + "  指纹 " + EditorApplication.timeSinceStartup.ToString("0.000"));

        // ⚠ 必须在 Wipe 之前解析标高基准 —— Wipe 会把 Pad_Terrace 删掉,
        //   之后就没有"场上活着的锚点"可读了。这里一次性钉住并写进报告。
        _deckTop = _padY = _mezzDeckTop = -1f;          // 清缓存,强制重解析
        float baseDeckTop = DeckTop, basePadY = PadY, baseMezz = MezzDeckTop;
        sb.AppendLine(string.Format("标高基准(开工前从场上解析):露台板面 {0:0.000}  锚点 {1:0.000}  二层环廊板面 {2:0.000}",
            baseDeckTop, basePadY, baseMezz));
        SnapshotOnce(scene);

        var moon = AimMoon(root.transform);
        Wipe(root.transform);
        MakeMaterials();

        var terrace = New("StarryTerrace", root.transform).transform;
        var pad = PlacePad(terrace, moon.yaw);
        var deck = BuildDeck(terrace, moon.yaw);
        var screen = BuildScreen(terrace, pad.position, moon.yaw);
        BuildRailings(terrace, deck);
        BuildLight(terrace, deck);
        BuildPlaza(terrace);
        var lobbyTp = BuildPanel(terrace, pad.position, moon.yaw);
        BuildCorridorSign(root.transform, moon.yaw);
        BuildCinema(terrace, screen, pad.position, moon.yaw, lobbyTp);
        AddCinemaButton(root.transform);

        Verify(pad, screen, moon);

        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("  保存场景:" + (EditorSceneManager.SaveScene(scene) ? "成功" : "失败")
            + "  isDirty(应为 False)=" + scene.isDirty);
        sb.AppendLine("  回退:copy " + Snapshot + " → " + ScenePath + " 后 openScene");
        try
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        } catch (System.Exception e) { Debug.LogWarning("[Terrace] 报告落盘失败:" + e.Message); }
        Debug.Log(sb.ToString());
        AssetDatabase.Refresh();
    }

    // ------------------------------------------------------------ 前置

    /// <summary>
    /// 三块观影面板编辑期默认关着,而桥按名字找物体走的是 GameObject.Find —— 找不到未激活物体,
    /// 所以远程截图验收只能由这个菜单来摆面板显隐:读 `Logs/panel-mode.txt`(逗号分隔的名单,或 all / none),
    /// 名单内的开、其余关。**只改内存,不保存场景**。
    /// </summary>
    [MenuItem("VRLibrary/露台观影面板显隐(读Logs/panel-mode.txt)")]
    public static void ApplyPanelMode()
    {
        var root = GameObject.Find("SmartLibrary");
        if (root == null) { Debug.LogError("[Terrace] 找不到根节点 SmartLibrary"); return; }
        const string f = "Logs/panel-mode.txt";
        if (!File.Exists(f)) { Debug.LogError("[Terrace] 缺少 " + f + ",内容形如 CatalogPanel,TransportPanel / all / none"); return; }

        string mode = File.ReadAllText(f).Trim();
        var want = new List<string>();
        if (mode.Equals("all", System.StringComparison.OrdinalIgnoreCase))
            want.AddRange(new[] { "NoticePanel", "CatalogPanel", "TransportPanel" });
        else if (!mode.Equals("none", System.StringComparison.OrdinalIgnoreCase))
            foreach (var s in mode.Split(new[] { ',', '，', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries))
                want.Add(s.Trim());

        var msg = new StringBuilder();
        foreach (var n in new[] { "NoticePanel", "CatalogPanel", "TransportPanel" })
        {
            var go = FindDeep(root.transform, n);
            if (go == null) { msg.Append(n + ":缺失  "); continue; }
            bool on = want.Contains(n);
            if (go.activeSelf != on) go.SetActive(on);
            msg.Append(n + ":" + (go.activeSelf ? "开" : "关") + "  ");
        }
        Debug.Log("[Terrace] 面板显隐(" + mode + ") → " + msg);
    }

    static void SnapshotOnce(UnityEngine.SceneManagement.Scene scene)
    {
        if (File.Exists(Snapshot))
        {
            sb.AppendLine("  快照已在(不覆盖)" + Snapshot + " 最后写入 "
                + File.GetLastWriteTime(Snapshot).ToString("MM-dd HH:mm:ss"));
            return;
        }
        if (scene.isDirty)
        { sb.AppendLine("  !! 场景有未保存改动 → 不写快照(可能已含本次改动),回退请走 git"); return; }
        Directory.CreateDirectory("Logs");
        File.Copy(ScenePath, Snapshot);
        sb.AppendLine("  已快照 " + Snapshot + " " + new FileInfo(Snapshot).Length + " 字节(本步之前的字节级回退点)");
    }

    /// <summary>
    /// 把主光俯角改成 MoonElevation。月亮方向 = -light.forward(SkyboxSunSync 就是这么对齐的),
    /// 所以"俯角 = 仰角"是同一件事,方位角 -35 原样保留 ⇒ 月亮仍在东北。
    /// </summary>
    static (float elev, float yaw) AimMoon(Transform root)
    {
        var sun = RenderSettings.sun;
        if (sun == null)
            foreach (var l in root.GetComponentsInChildren<Light>(true))
                if (l.type == LightType.Directional) { sun = l; break; }
        if (sun == null) { sb.AppendLine("  !! 找不到平行光 → 月亮高度没改"); return (0f, 0f); }

        var before = sun.transform.eulerAngles;
        Undo.RecordObject(sun.transform, "主光俯角降到 15°");
        sun.transform.rotation = Quaternion.Euler(MoonElevation, SunAzimuth, 0f);
        EditorUtility.SetDirty(sun);
        Physics.SyncTransforms();

        var moon = -sun.transform.forward;
        float elev = Mathf.Asin(Mathf.Clamp(moon.y, -1f, 1f)) * Mathf.Rad2Deg;
        float yaw = Mathf.Atan2(moon.x, moon.z) * Mathf.Rad2Deg;
        sb.AppendLine(string.Format("  [月亮] 主光欧拉 {0} → {1}", before.ToString("F1"),
            sun.transform.eulerAngles.ToString("F1")));
        sb.AppendLine(string.Format("    月亮方向 {0} 仰角 {1:0.0}° 方位 {2:0.0}°(从 +Z 起算,{3}){4}",
            F(moon), elev, yaw, Compass(yaw),
            Mathf.Abs(elev - MoonElevation) < 0.05f ? "" : " !! 没落到目标仰角"));
        return (elev, yaw);
    }

    static string Compass(float yaw)
    {
        // 本工程约定:-Z 是北(服务台在 z=-16 被叫作"北侧服务台"),+X 是东
        string[] n = { "南", "东南", "东", "东北", "北", "西北", "西", "西南" };
        int i = Mathf.RoundToInt(((yaw + 360f) % 360f) / 45f) % 8;
        return n[i];
    }

    static void Wipe(Transform root)
    {
        // 必须**递归**找:TerraceSign 挂在 Mezzanine 下,而 Transform.Find 只查直接子级 ——
        // 上一版因此每跑一次就留一块重影(实测场上叠了 3 块同名同位的牌)。
        var doomed = new List<GameObject>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            foreach (var name in new[] { "StarryTerrace", "Pad_Terrace", "TerracePanel", "TerraceSign",
                                         "TerraceGoLobby", "TerraceGoMezz", "TerraceView", "Button_Cinema" })
                if (t.name == name) { doomed.Add(t.gameObject); break; }
        int found = doomed.Count;
        // 父级一删,名单里的子级就跟着没了;再去 DestroyObjectImmediate 会踩已销毁对象抛异常。
        // 所以只删"名单里没有祖先"的顶层节点。
        var keep = new List<GameObject>();
        foreach (var g in doomed)
        {
            bool childOfList = false;
            for (var p = g.transform.parent; p != null; p = p.parent)
                if (doomed.Contains(p.gameObject)) { childOfList = true; break; }
            if (!childOfList) keep.Add(g);
        }
        foreach (var g in keep) Undo.DestroyObjectImmediate(g);
        sb.AppendLine("  [幂等] 递归找到 " + found + " 个,删掉顶层 " + keep.Count
            + " 个(其余随父级子树一起走 Undo;挂在 Mezzanine 下的指示牌也在内)");
    }

    // ------------------------------------------------------------ 材质

    static void MakeMaterials()
    {
        matDeck = Mat("M_TerraceDeck", new Color(0.055f, 0.062f, 0.080f), 0.55f, 0.42f, null);
        matFrame = Mat("M_ScreenFrame", new Color(0.030f, 0.034f, 0.046f), 0.75f, 0.50f, null);
        matPlaza = Mat("M_Plaza", new Color(0.020f, 0.024f, 0.034f), 0.20f, 0.65f, null);
        matScreen = Mat("M_TerraceScreen", new Color(0.020f, 0.026f, 0.036f), 0f, 0.25f,
            new Color(0.62f, 0.86f, 0.96f));
        // 栏杆与压边直接复用场上定过色的两份,不再新增颜色(白青 = M_LightBar,金属 = M_Metal)
        matRail = Load("Assets/Materials/M_Metal.mat") ?? matFrame;
        matGlow = Load("Assets/Materials/M_LightBar.mat") ?? matScreen;
    }

    static Material Load(string path) { return AssetDatabase.LoadAssetAtPath<Material>(path); }

    static Material Mat(string name, Color baseCol, float metallic, float smooth, Color? emis)
    {
        var path = MatDir + "/" + name + ".mat";
        var m = Load(path);
        if (m != null) { sb.AppendLine("  材质已存在,复用不覆盖:" + name); return m; }
        m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
        m.color = baseCol;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", baseCol);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emis.HasValue)
        {
            m.SetColor("_EmissionColor", emis.Value);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;   // 坑 22
        }
        Directory.CreateDirectory(MatDir);
        AssetDatabase.CreateAsset(m, path);
        sb.AppendLine("  新建材质:" + name + (emis.HasValue ? "(发光,RealtimeEmissive)" : ""));
        return m;
    }

    // ------------------------------------------------------------ 几何

    static GameObject New(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Undo.RegisterCreatedObjectUndo(go, "创建 " + name);
        return go;
    }

    /// <summary>立方体:给世界中心 + 世界尺寸(不用 localScale 猜,免得父级带缩放时看错)。</summary>
    static GameObject Box(string name, Transform parent, Vector3 center, Vector3 size, Material m)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Undo.RegisterCreatedObjectUndo(go, "创建 " + name);
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.localScale = size;
        if (m != null) go.GetComponent<MeshRenderer>().sharedMaterial = m;
        go.isStatic = true;
        return go;
    }

    static GameObject Quad(string name, Transform parent, Vector3 center, Quaternion rot,
        float w, float h, Material m, bool collider)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        Undo.RegisterCreatedObjectUndo(go, "创建 " + name);
        go.transform.SetParent(parent, false);
        go.transform.position = center;
        go.transform.rotation = rot;
        go.transform.localScale = new Vector3(w, h, 1f);
        if (m != null) go.GetComponent<MeshRenderer>().sharedMaterial = m;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        go.isStatic = true;
        return go;
    }

    /// <summary>局部坐标版 Box:父级已经摆好位姿时用,尺寸仍按世界米给(露台这一路都不带缩放)。</summary>
    static GameObject BoxL(string name, Transform parent, Vector3 localCenter, Vector3 size, Material m)
    {
        var go = Box(name, parent, Vector3.zero, size, m);
        go.transform.localPosition = localCenter;
        go.transform.localRotation = Quaternion.identity;
        return go;
    }

    static GameObject QuadL(string name, Transform parent, Vector3 localCenter, Quaternion localRot,
        float w, float h, Material m, bool collider)
    {
        var go = Quad(name, parent, Vector3.zero, localRot, w, h, m, collider);
        go.transform.localPosition = localCenter;
        go.transform.localRotation = localRot;
        return go;
    }

    /// <summary>露台地板:从建筑外墙角(x=19.8 / z=-19.8)向外铺到能容下锚点与大屏,再留 4 m。</summary>
    static Bounds BuildDeck(Transform parent, float moonYaw)
    {
        var dir = YawVec(moonYaw);
        var pad = YawPad(dir);
        float maxX = Mathf.Max(pad.x, (pad + dir * ViewDist).x) + 4f;
        float minZ = Mathf.Min(pad.z, (pad + dir * ViewDist).z) - 4f;
        maxX = Mathf.Max(maxX, 30f); minZ = Mathf.Min(minZ, -30f);
        const float wallX = 19.8f, wallZ = -19.8f;

        var c = new Vector3((wallX + maxX) * 0.5f, DeckTop - 0.15f, (wallZ + minZ) * 0.5f);
        var s = new Vector3(maxX - wallX, 0.30f, wallZ - minZ);
        var deck = Box("TerraceDeck", parent, c, s, matDeck);
        var b = new Bounds(c, s);
        sb.AppendLine(string.Format("  [露台板] 足迹 x {0:0.00}~{1:0.00}  z {2:0.00}~{3:0.00}({4:0.0}×{5:0.0} m)"
            + "  板面顶 y {7:0.000}(二层环廊板面 {9:0.000},差 {8:+0.000;-0.000;0}){6}",
            wallX, maxX, minZ, wallZ, s.x, s.z, "", b.max.y, b.max.y - MezzDeckTop, MezzDeckTop));
        // 压边霓虹:沿两条外边各一条,给夜里一个"这里到边了"的读感
        var e1 = Box("TerraceEdge_E", parent, new Vector3(maxX - 0.06f, DeckTop + 0.012f, (wallZ + minZ) * 0.5f),
            new Vector3(0.12f, 0.024f, s.z), matGlow);
        var e2 = Box("TerraceEdge_N", parent, new Vector3((wallX + maxX) * 0.5f, DeckTop + 0.012f, minZ + 0.06f),
            new Vector3(s.x, 0.024f, 0.12f), matGlow);
        // Cube 原语自带 BoxCollider,不是 MeshCollider —— 按基类取,否则 MissingComponentException
        Object.DestroyImmediate(e1.GetComponent<Collider>());
        Object.DestroyImmediate(e2.GetComponent<Collider>());
        return b;
    }

    static Vector3 YawPad(Vector3 dir)
    {
        // 锚点落在东北角外 Margin 处(外墙角在 +19.8 / -19.8)
        return new Vector3(19.8f + Margin, PadY, -19.8f - Margin);
    }

    static Vector3 YawVec(float yaw)
    {
        var r = Quaternion.Euler(0f, yaw, 0f);
        return r * Vector3.forward;
    }

    static Transform PlacePad(Transform parent, float moonYaw)
    {
        var src = GameObject.Find("SmartLibrary/Pad_Shelves");
        if (src == null) { sb.AppendLine("  !! 找不到样板 SmartLibrary/Pad_Shelves → 没造传送锚点"); return null; }
        var dir = YawVec(moonYaw);
        var pos = YawPad(dir);
        var clone = Object.Instantiate(src, parent);
        clone.name = "Pad_Terrace";
        Undo.RegisterCreatedObjectUndo(clone, "创建 Pad_Terrace");
        clone.transform.position = pos;
        clone.transform.rotation = Quaternion.Euler(0f, moonYaw, 0f);   // forward = 落地后玩家朝向 = 月亮方向
        sb.AppendLine(string.Format("  [锚点] Pad_Terrace @ {0} 朝向 yaw {1:0.0}°(摇杆瞄准即可传送过来)",
            pos.ToString("F2"), moonYaw));
        return clone.transform;
    }

    /// <summary>大屏:屏面 + 背板 + 基座 + 两侧立柱,并挂 TerraceScreen 控制器。</summary>
    static Transform BuildScreen(Transform parent, Vector3 padPos, float moonYaw)
    {
        var dir = YawVec(moonYaw);
        var center = padPos + dir * ViewDist;
        center.y = DeckTop + 0.15f + ScreenH * 0.5f;      // 屏底离板面 150 mm

        var g = New("TerraceScreenGroup", parent);
        g.transform.position = center;
        g.transform.rotation = Quaternion.LookRotation(-dir);

        // 组内一律用**局部**坐标(Box/Quad 那两个写的是世界坐标,拿来摆局部件会把屏丢到原点)。
        // 方向约定:组的 forward = +localZ = 朝观众那一侧,所以"屏面"要放在背板的 +Z 前面,
        // 上一版写成 -0.115 等于把屏塞到 0.2 m 厚的背板背后,现场看到的就是一块黑背板 + 镜像字。
        BoxL("ScreenBacker", g.transform, Vector3.zero, new Vector3(ScreenW + 0.5f, ScreenH + 0.5f, 0.20f), matFrame);
        // Quad 原语的**可见面是自身 -Z**(GameView 探针实测:把一块 Quad 摆成与相机同朝向放在它前面,
        // 能看见 → 朝相机那面是 -Z)。而本组的 +Z 才是"朝观众",所以 identity 旋转的屏面正对观众的
        // 是背面,被 _Cull=2 剔掉 —— 换成亮品红 Unlit 也照样不显示(实测)。必须补 Euler(0,180,0)。
        QuadL("ScreenFace", g.transform, new Vector3(0f, 0f, 0.115f), Quaternion.Euler(0f, 180f, 0f),
            ScreenW, ScreenH, matScreen, false);
        // 视频面单独一块 Quad 贴在屏面前 20 mm:TerraceScreen 按片子真实比例缩它的 localScale。
        // 屏框已经是 16:9,所以 1920×1080 这类片子**铺满**、只剩上下黑边由屏面 ScreenFace 当;
        // 比例不同的片子(4:3、超宽)才会露出左右黑边 —— 这是正常信箱,不是 bug。
        var video = QuadL("VideoSurface", g.transform, new Vector3(0f, 0f, 0.135f), Quaternion.Euler(0f, 180f, 0f),
            ScreenW, ScreenH, matScreen, false);
        // **必须取消 static**:静态合批在建场时把顶点和当时的 sharedMaterial 一起烘进组合网格,
        // 之后运行时 `renderer.material`(接视频 RT)、改 localScale/localRotation 全都不进画面
        // ——实测三张探针(翻面/提亮/换 Unlit 品红)字节完全相同,就是被合批烘死了。
        video.isStatic = false;
        // 基座与立柱:让 9 m 宽的屏看起来是"架在露台上"的实体,不是飘着
        BoxL("ScreenBase", g.transform, new Vector3(0f, -(ScreenH * 0.5f + 0.075f) - 0.15f, 0f),
            new Vector3(ScreenW + 0.9f, 0.30f, 0.9f), matFrame);
        for (int s = -1; s <= 1; s += 2)
            BoxL("ScreenPylon_" + (s < 0 ? "L" : "R"), g.transform,
                new Vector3(s * (ScreenW * 0.5f + 0.25f), 0f, 0f),
                new Vector3(0.30f, ScreenH + 0.6f, 0.60f), matFrame);

        // 说明字挂在**组**下而不是屏面下:屏面那块 Quad 的 localScale 是 (9,3,1),
        // 挂在它下面会被继承成"横着拉宽 3 倍"的变形字。
        var cap = Caption(g.transform, "影片位：片单在 Resources/filmography.json，mp4 放 StreamingAssets/");
        var ts = g.AddComponent<TerraceScreen>();
        ts.screen = video.GetComponent<MeshRenderer>();
        EditorUtility.SetDirty(ts);

        sb.AppendLine(string.Format("  [大屏] 中心 {0} 尺寸 {1:0.0}×{2:0.0} m 距锚点 {3:0.00} m 屏面法线 {4}",
            center.ToString("F2"), ScreenW, ScreenH,
            Vector3.Distance(new Vector3(padPos.x, 0, padPos.z), new Vector3(center.x, 0, center.z)),
            F(-dir)));
        return g.transform;
    }

    /// <summary>屏上的占位说明字:复用场上已有 TextMesh 的字体与无光照材质(坑 5)。</summary>
    static TextMesh Caption(Transform face, string text)
    {
        TextMesh proto = null;
        foreach (var t in Object.FindObjectsOfType<TextMesh>())
            if (t.gameObject.name == "TitleText") { proto = t; break; }
        var go = new GameObject("Caption");
        Undo.RegisterCreatedObjectUndo(go, "创建 Caption");
        go.transform.SetParent(face, false);
        go.transform.localPosition = new Vector3(0f, -0.95f, 0.135f);   // 屏面在 +0.115,字再朝观众 20 mm
        // TextMesh 的可读面和 UGUI 一样是 **-forward**(只有 Quad/Mesh 才朝 +Z),
        // 所以这里必须转 180° Y,否则材质 Cull Off 会让你从背面看见一行镜像字。
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        go.transform.localScale = new Vector3(0.0055f, 0.0055f, 0.0055f);
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.alignment = TextAlignment.Center;
        tm.characterSize = 1f;
        tm.fontSize = 48;
        tm.color = new Color(0.72f, 0.92f, 1f);
        if (proto != null)
        {
            tm.font = proto.font;
            var r = go.GetComponent<MeshRenderer>();
            var src = face.GetComponent<MeshRenderer>();
            var unlit = proto.GetComponent<MeshRenderer>();
            if (unlit != null) r.sharedMaterial = unlit.sharedMaterial;
            else if (src != null) r.sharedMaterial = src.sharedMaterial;
        }
        else sb.AppendLine("  !! 场上找不到样板 TextMesh → 占位字用默认字体(可能不认中文)");
        return tm;
    }

    static void BuildRailings(Transform parent, Bounds deck)
    {
        float top = DeckTop + 1.10f;
        // 只有两条外边需要栏杆(另两边贴着建筑外墙)
        Box("Rail_E", parent, new Vector3(deck.max.x - 0.06f, top, (deck.min.z + deck.max.z) * 0.5f),
            new Vector3(0.08f, 0.08f, deck.size.z), matGlow);
        Box("Rail_N", parent, new Vector3((deck.min.x + deck.max.x) * 0.5f, top, deck.min.z + 0.06f),
            new Vector3(deck.size.x, 0.08f, 0.08f), matGlow);
        int posts = 0;
        for (int i = 0; i <= 6; i++)
        {
            float t = i / 6f;
            Box("Post_E" + i, parent, new Vector3(deck.max.x - 0.06f, DeckTop + 0.55f,
                Mathf.Lerp(deck.max.z, deck.min.z, t)), new Vector3(0.07f, 1.10f, 0.07f), matRail);
            Box("Post_N" + i, parent, new Vector3(Mathf.Lerp(deck.max.x, deck.min.x, t), DeckTop + 0.55f,
                deck.min.z + 0.06f), new Vector3(0.07f, 1.10f, 0.07f), matRail);
            posts += 2;
        }
        sb.AppendLine(string.Format("  [栏杆] 两条外边压顶霓虹 + {0} 根立柱(高 1.10 m,顶 y {1:0.00})", posts, top));
    }

    static void BuildLight(Transform parent, Bounds deck)
    {
        var go = New("TerraceLight", parent);
        go.transform.position = new Vector3((deck.min.x + deck.max.x) * 0.5f, DeckTop + 2.6f,
            (deck.min.z + deck.max.z) * 0.5f);
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = new Color(152f / 255f, 182f / 255f, 194f / 255f);   // 白青,与全场灯色同族
        l.intensity = 0.75f;
        l.range = 22f;
        l.shadows = LightShadows.None;                                 // 移动端一盏实时阴影就够贵了
        sb.AppendLine("  [补光] 1 盏白青点光(无阴影)range 22 / intensity 0.75 —— 全场点聚光数会 +1");
    }

    static void BuildPlaza(Transform parent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
        go.name = "TerracePlaza";
        Undo.RegisterCreatedObjectUndo(go, "创建 TerracePlaza");
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(0f, -0.02f, 0f);
        go.transform.localScale = new Vector3(16f, 1f, 16f);           // 160×160 m,压住"外面是虚空"
        go.GetComponent<MeshRenderer>().sharedMaterial = matPlaza;
        // 关键:摘掉碰撞体。它只是给"从露台往下看"一个底,绝不能变成可传送/可行走的面
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.isStatic = true;
        sb.AppendLine("  [广场] 160×160 深色地平面 y=-0.02,**已摘碰撞体** → 不参与任何射线/传送落点判定");
    }

    // ------------------------------------------------------------ 面板与指示牌

    static KioskTeleporter BuildPanel(Transform parent, Vector3 padPos, float moonYaw)
    {
        // 面板立在锚点左后方,可读面(-forward)朝向锚点 —— 坑 26
        var pos = padPos + new Vector3(-2.4f, 1.38f, 2.4f);
        var read = (padPos - pos); read.y = 0f; read.Normalize();

        var canvas = Canvas("TerracePanel", parent, pos, read, 460, 300, 0.0034f);
        MakeText(canvas.transform, "Title", "星空露台", 46, Color.white, TextAnchor.UpperCenter,
            new Vector2(0f, 0.74f), new Vector2(1f, 1f), new Vector2(0f, -10f), new Vector2(0f, 0f));
        MakeText(canvas.transform, "Hint", "抬头一点点就是月亮\n大屏会自动播 StreamingAssets 里的视频",
            22, new Color(0.72f, 0.86f, 0.94f), TextAnchor.UpperCenter,
            new Vector2(0f, 0.46f), new Vector2(1f, 0.74f), new Vector2(8f, 0f), new Vector2(-8f, 0f));

        var lobby = Target("TerraceGoLobby", parent, new Vector3(0f, 0.05f, 6f), 180f);
        var mezz = Target("TerraceGoMezz", parent, new Vector3(18.2f, PadY, -10.5f), -90f);

        var b1 = MakeButton(canvas.transform, "Button_BackLobby", "回一层大厅",
            new Vector2(0.06f, 0.06f), new Vector2(0.48f, 0.40f), new Color(0.10f, 0.22f, 0.34f, 0.95f));
        var b2 = MakeButton(canvas.transform, "Button_BackMezz", "回二楼环廊",
            new Vector2(0.52f, 0.06f), new Vector2(0.94f, 0.40f), new Color(0.10f, 0.22f, 0.34f, 0.95f));

        var t1 = b1.gameObject.AddComponent<KioskTeleporter>();
        t1.origin = FindOrigin(); t1.destination = lobby;
        var t2 = b2.gameObject.AddComponent<KioskTeleporter>();
        t2.origin = FindOrigin(); t2.destination = mezz;
        UnityEventTools.AddVoidPersistentListener(b1.onClick, new UnityAction(t1.TeleportToDestination));
        UnityEventTools.AddVoidPersistentListener(b2.onClick, new UnityAction(t2.TeleportToDestination));
        EditorUtility.SetDirty(b1); EditorUtility.SetDirty(b2);

        sb.AppendLine(string.Format("  [回程面板] TerracePanel @ {0} 可读面 {1}({2:0.0}°) 两颗按钮各自挂一份 KioskTeleporter",
            pos.ToString("F2"), F(read), Mathf.Atan2(read.x, read.z) * Mathf.Rad2Deg));
        return t1;        // 「回一层大厅」那份:播控面板上的新出口复用同一个目的地
    }

    static void BuildCorridorSign(Transform root, float moonYaw)
    {
        // 二层东环廊楼梯口:告诉游客"露台要用摇杆瞄传送点",否则没人知道这一区存在
        var mezz = root.Find("Mezzanine");
        if (mezz == null) { sb.AppendLine("  !! 找不到 Mezzanine → 没造楼梯口指示牌"); return; }
        var pos = new Vector3(19.45f, 4.35f, -13.5f);
        var read = new Vector3(-1f, 0f, 0f);                 // 朝环廊内侧(-X)
        var canvas = Canvas("TerraceSign", mezz, pos, read, 520, 210, 0.0026f);
        MakeText(canvas.transform, "Title", "星空露台", 40, Color.white, TextAnchor.UpperCenter,
            new Vector2(0f, 0.62f), new Vector2(1f, 1f), new Vector2(0f, -8f), new Vector2(0f, 0f));
        MakeText(canvas.transform, "Body", "东北角外 · 只靠传送到达\n摇杆瞄准发光圆筒即可传送\n回去用露台上的面板",
            22, new Color(0.72f, 0.86f, 0.94f), TextAnchor.UpperCenter,
            new Vector2(0f, 0.04f), new Vector2(1f, 0.62f), new Vector2(10f, 0f), new Vector2(-10f, 0f));
        sb.AppendLine(string.Format("  [指示牌] TerraceSign @ {0} 可读面 {1}(楼梯口下板即见)", pos.ToString("F2"), F(read)));
    }

    static Transform Target(string name, Transform parent, Vector3 pos, float yaw)
    {
        var go = New(name, parent);
        go.transform.position = pos;
        go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        return go.transform;
    }

    static XROrigin FindOrigin()
    {
        foreach (var kt in Object.FindObjectsOfType<KioskTeleporter>())
            if (kt.origin != null) return kt.origin;
        sb.AppendLine("  !! 场上没有一份 KioskTeleporter 接了 origin → 回程按钮要手动接 XROrigin");
        return null;
    }

    static GameObject Canvas(string name, Transform parent, Vector3 pos, Vector3 readDir,
        int pxW, int pxH, float scale)
    {
        var go = New(name, parent);
        var cv = go.AddComponent<Canvas>();
        cv.renderMode = RenderMode.WorldSpace;
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(pxW, pxH);
        rt.localScale = Vector3.one * scale;
        go.transform.position = pos;
        go.transform.rotation = Quaternion.LookRotation(-readDir);      // 可读面 = -forward(坑 26)
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.02f, 0.045f, 0.075f, 0.93f);
        go.AddComponent<GraphicRaycaster>();
        go.AddComponent<TrackedDeviceGraphicRaycaster>();
        return go;
    }

    static Text MakeText(Transform parent, string name, string content, int size, Color color,
        TextAnchor anchor, Vector2 aMin, Vector2 aMax, Vector2 offMin, Vector2 offMax)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.text = content;
        text.fontSize = size;
        text.color = color;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        var rt = text.rectTransform;
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.offsetMin = offMin; rt.offsetMax = offMax;
        return text;
    }

    static Button MakeButton(Transform parent, string name, string label,
        Vector2 aMin, Vector2 aMax, Color bg)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bg;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var c = btn.colors;
        c.highlightedColor = new Color(0.15f, 0.55f, 0.75f);
        c.pressedColor = new Color(0.08f, 0.35f, 0.5f);
        btn.colors = c;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        MakeText(go.transform, "Text", label, 26, Color.white, TextAnchor.MiddleCenter,
            Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        return btn;
    }

    // ------------------------------------------------------------ 观影模式

    static TerraceCinema cinema;

    /// <summary>
    /// 观影模式的控制器与三块面板。**所有 UI 都在这里编辑期建好**,运行时只切 activeSelf
    /// (Play 里 AddComponent&lt;Text&gt; 会 NRE,见坑 1)。
    /// </summary>
    static Transform BuildCinema(Transform parent, Transform screenGroup, Vector3 seatPos, float moonYaw,
        KioskTeleporter lobbyTp)
    {
        var seatRot = Quaternion.Euler(0f, moonYaw, 0f);
        var fwd = seatRot * Vector3.forward;          // 朝屏方向
        var side = seatRot * Vector3.right;           // 观众的右手侧 —— 别手算叉积,容易反

        var go = New("CinemaController", parent);
        go.transform.position = seatPos;
        go.transform.rotation = seatRot;
        cinema = go.AddComponent<TerraceCinema>();

        var seat = New("CinemaSeat", parent);
        seat.transform.position = seatPos;
        seat.transform.rotation = seatRot;
        cinema.origin = FindOrigin();
        cinema.seat = seat.transform;
        cinema.screen = screenGroup.GetComponent<TerraceScreen>();

        //  teleporter:导览屏那颗按钮要能把人送到露台上
        var tp = go.AddComponent<KioskTeleporter>();
        tp.origin = cinema.origin;
        tp.destination = seat.transform;
        cinema.teleporter = tp;
        cinema.lobbyTeleporter = lobbyTp;              // 播控面板那颗「回一层大厅」复用露台面板同一个目的地

        var eye = seatPos + Vector3.up * 1.60f;       // 坐定后的眼睛高度(1.6 m 站姿头高)

        // 目录:0.0018@0.88 m 摊到 63° 高(截图复核出来的,扫六行要仰头)→ 收到 40° 高 / 35° 宽。
        // v7.41(2026-10-19)用户定案:**"前"以座位→银幕方向为准,不跟随头部朝向** ——
        // 目录从"左前方 0.95 m / 左 0.52 m"挪到座位正前方,中心落在 fwd 轴上;人转头/转身时它原地不动。
        // 左前方那版的斜距是 sqrt(0.95²+0.52²)≈1.08 m;居中后必须沿用 1.08 m,
        // 否则正前方 0.95 m 会把张角从 40° 撑到 45°,标题被顶出视野(截图实测)。
        cinema.catalogPanel = Panel(go.transform, "CatalogPanel",
            eye + fwd * 1.08f, eye, 520, 600, 0.0013f);
        cinema.transportPanel = Panel(go.transform, "TransportPanel",
            eye + fwd * 0.85f + side * 0.52f + Vector3.up * -0.25f, eye, 420, 380, 0.00135f);
        // 须知 640×430:放在 1.60 m 会摊到 66° 宽(截图复核出来的),得转头才能读 → 推到 1.80 m、缩到 48°
        cinema.noticePanel = Panel(go.transform, "NoticePanel",
            eye + fwd * 1.80f, eye, 640, 430, 0.0025f);

        // —— 目录:标题 + 6 颗预建槽 + 底部提示
        var cat = cinema.catalogPanel.transform;
        MakeText(cat, "Title", "观影目录", 34, Color.white, TextAnchor.UpperCenter,
            new Vector2(0f, 0.88f), new Vector2(1f, 1f), new Vector2(0f, -4f), new Vector2(0f, 0f));
        for (int i = 0; i < 6; i++)
        {
            float top = 0.855f - i * 0.125f;
            var b = MakeButton(cat, "Slot_" + i, "（空）",
                new Vector2(0.05f, top - 0.115f), new Vector2(0.95f, top),
                new Color(0.09f, 0.19f, 0.30f, 0.95f));
            cinema.filmSlots[i] = b;
            cinema.filmLabels[i] = b.GetComponentInChildren<Text>();
        }
        Bind(cinema.filmSlots[0], cinema, "OnSelectFilm0");
        Bind(cinema.filmSlots[1], cinema, "OnSelectFilm1");
        Bind(cinema.filmSlots[2], cinema, "OnSelectFilm2");
        Bind(cinema.filmSlots[3], cinema, "OnSelectFilm3");
        Bind(cinema.filmSlots[4], cinema, "OnSelectFilm4");
        Bind(cinema.filmSlots[5], cinema, "OnSelectFilm5");
        // 目录底部左半边:回看须知(确认过之后还能再打开);提示字缩到右半边
        cinema.noticeButton = MakeButton(cat, "Notice", "观影须知",
            new Vector2(0.05f, 0.015f), new Vector2(0.46f, 0.095f), new Color(0.13f, 0.26f, 0.36f, 0.95f));
        Bind(cinema.noticeButton, cinema, "OnShowNotice");
        MakeText(cat, "Hint", "选一片开始播放\n右手 Grip 按一下呼出/收起本面板", 19,
            new Color(0.62f, 0.78f, 0.88f), TextAnchor.LowerCenter,
            new Vector2(0.48f, 0.01f), new Vector2(0.98f, 0.10f), new Vector2(2f, 2f), new Vector2(-2f, 0f));

        // —— 播控:暂停 / 倍速 / 后退 / 前进 / 跳过 / 关闭影片 / 退出(两列四行)
        var tr = cinema.transportPanel.transform;
        MakeText(tr, "Title", "播控", 30, Color.white, TextAnchor.UpperCenter,
            new Vector2(0f, 0.88f), new Vector2(1f, 1f), new Vector2(0f, -2f), new Vector2(0f, 0f));
        var tBlue = new Color(0.10f, 0.22f, 0.34f, 0.95f);
        cinema.pauseButton = MakeButton(tr, "Pause", "暂停",
            new Vector2(0.05f, 0.66f), new Vector2(0.48f, 0.85f), tBlue);
        cinema.speedButton = MakeButton(tr, "Speed", "倍速 1.0×",
            new Vector2(0.52f, 0.66f), new Vector2(0.95f, 0.85f), tBlue);
        cinema.seekBackButton = MakeButton(tr, "SeekBack", "后退 5 秒",
            new Vector2(0.05f, 0.47f), new Vector2(0.48f, 0.64f), tBlue);
        cinema.seekFwdButton = MakeButton(tr, "SeekFwd", "前进 5 秒",
            new Vector2(0.52f, 0.47f), new Vector2(0.95f, 0.64f), tBlue);
        cinema.skipButton = MakeButton(tr, "Skip", "跳过下一片",
            new Vector2(0.05f, 0.28f), new Vector2(0.48f, 0.45f), tBlue);
        cinema.stopButton = MakeButton(tr, "StopFilm", "关闭影片",
            new Vector2(0.52f, 0.28f), new Vector2(0.95f, 0.45f), tBlue);
        // 最后一行只留「回一层大厅」:观影态没有任何就地解锁/离座入口,
        // 这颗是唯一出口,所以占满整行(不再和「退出观影」并排)。
        cinema.lobbyButton = MakeButton(tr, "BackLobby", "回一层大厅",
            new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.24f), new Color(0.10f, 0.22f, 0.34f, 0.95f));
        cinema.pauseLabel = cinema.pauseButton.GetComponentInChildren<Text>();
        cinema.speedLabel = cinema.speedButton.GetComponentInChildren<Text>();
        Bind(cinema.pauseButton, cinema, "OnPauseToggle");
        Bind(cinema.speedButton, cinema, "OnCycleSpeed");
        Bind(cinema.seekBackButton, cinema, "OnSeekBack");
        Bind(cinema.seekFwdButton, cinema, "OnSeekForward");
        Bind(cinema.skipButton, cinema, "OnSkip");
        Bind(cinema.stopButton, cinema, "OnStopFilm");
        Bind(cinema.lobbyButton, cinema, "OnBackToLobby");

        // —— 观影院须知
        var no = cinema.noticePanel.transform;
        MakeText(no, "Title", "观影院须知", 40, Color.white, TextAnchor.UpperCenter,
            new Vector2(0f, 0.84f), new Vector2(1f, 1f), new Vector2(0f, -6f), new Vector2(0f, 0f));
        MakeText(no, "Body", "一、进入观影后身体位置会被锁定，无法走动；转头即可环视；\n"
            + "二、观影期间不提供离座选项，需要离开时请按播控面板上的「回一层大厅」；\n"
            + "三、右手 Grip 按一下呼出/收起目录，左手 Grip 按一下呼出/收起播控；\n"
            + "四、影片源由馆方放入 StreamingAssets 后自动上屏，缺片时屏上会显示提示；\n"
            + "五、关闭影片只退回待机卡，不会解锁站位；回一层大厅后抓书、加速等按键恢复原用法。",
            22, new Color(0.82f, 0.90f, 0.96f), TextAnchor.UpperLeft,
            new Vector2(0.04f, 0.20f), new Vector2(0.96f, 0.82f), new Vector2(0f, 0f), new Vector2(0f, 0f));
        cinema.ackButton = MakeButton(no, "Ack", "我已知晓，开始观影",
            new Vector2(0.2f, 0.03f), new Vector2(0.8f, 0.16f), new Color(0.10f, 0.30f, 0.22f, 0.95f));
        Bind(cinema.ackButton, cinema, "OnAckNotice");

        // 面板默认全关(运行时由 TerraceCinema 显隐);信息卡交给 Start 决定
        cinema.catalogPanel.SetActive(false);
        cinema.transportPanel.SetActive(false);
        cinema.noticePanel.SetActive(false);

        var sc = cinema.screen;
        if (sc != null)
        {
            var card = BuildInfoCard(screenGroup, sc);
            sc.infoCard = card;
        }
        EditorUtility.SetDirty(cinema);
        sb.AppendLine(string.Format("  [观影模式] CinemaController + 3 块预建面板(目录/播控/须知) + 大屏信息卡;"
            + "目录在座位正前方 1.08 m(以座位→银幕为前,40° 高 / 35° 宽),播控 0.85 m 右前方(36° 宽),"
            + "须知 1.80 m(48° 宽);不建离座确认/退出观影 —— 观影态硬锁站位,唯一出口是「回一层大厅」"));
        return go.transform;
    }

    /// <summary>大屏正前方的影片信息卡(片名 / 类型·时长 / 简介 / 提示 / 解码路线),与屏面同尺寸同比例。</summary>
    static GameObject BuildInfoCard(Transform screenGroup, TerraceScreen sc)
    {
        var eye = sc.screen.transform.position;
        // 卡片像素直接用片源分辨率 1920×1080,缩放算到"铺满屏框",这样字号与屏宽的比例
        // 不随屏子改尺寸而变;MakeText 默认双向 Overflow,长文案一律换成横向 Wrap(见下)。
        var card = Canvas("FilmInfoCard", screenGroup,
            eye + screenGroup.forward * 0.06f + Vector3.up * 0f,
            (eye - screenGroup.position).normalized,        // 朝观众:从屏心往外就是可读方向
            1920, 1080, ScreenW / 1920f);
        var bg = card.GetComponent<Image>();
        if (bg != null) bg.color = new Color(0.01f, 0.03f, 0.05f, 0.72f);

        sc.cardTitle = MakeText(card.transform, "Title", "（未选片）", 120, Color.white,
            TextAnchor.UpperCenter, new Vector2(0.06f, 0.62f), new Vector2(0.94f, 0.95f),
            new Vector2(0f, 0f), new Vector2(0f, 0f));
        sc.cardMeta = MakeText(card.transform, "Meta", "— · — 分钟", 62,
            new Color(0.62f, 0.90f, 1f), TextAnchor.UpperCenter,
            new Vector2(0.06f, 0.48f), new Vector2(0.94f, 0.60f), new Vector2(0f, 0f), new Vector2(0f, 0f));
        sc.cardSummary = MakeText(card.transform, "Summary", "", 37,
            new Color(0.86f, 0.92f, 0.96f), TextAnchor.UpperCenter,
            new Vector2(0.08f, 0.20f), new Vector2(0.92f, 0.46f), new Vector2(0f, 0f), new Vector2(0f, 0f));
        sc.cardHint = MakeText(card.transform, "Hint", "", 43,
            new Color(0.55f, 0.75f, 0.86f), TextAnchor.LowerCenter,
            new Vector2(0.06f, 0.09f), new Vector2(0.94f, 0.19f), new Vector2(0f, 0f), new Vector2(0f, 0f));
        sc.cardDecoder = MakeText(card.transform, "Decoder", "", 32,
            new Color(0.42f, 0.60f, 0.70f), TextAnchor.LowerCenter,
            new Vector2(0.06f, 0.015f), new Vector2(0.94f, 0.08f), new Vector2(0f, 0f), new Vector2(0f, 0f));
        // MakeText 给的是"短标签不折行"的默认值;卡上这四行都是长文案,不 Wrap 就会顶出屏边
        foreach (var t in new[] { sc.cardTitle, sc.cardSummary, sc.cardHint, sc.cardDecoder })
        {
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
        }
        sc.infoCard = card;
        return card;
    }

    /// <summary>导览屏顶排加第 7 颗"星空观影":先把顶排三颗从 3 列改成 4 列,再插新按钮。</summary>
    static void AddCinemaButton(Transform root)
    {
        var canvas = root.Find("CentralKiosk/InfoCanvas");
        if (canvas == null || cinema == null) { sb.AppendLine("  !! 找不到 InfoCanvas 或没建 CinemaController → 没加观影入口"); return; }

        // 顶排(y 锚点 150)三颗:0.03~0.31 / 0.35~0.63 / 0.67~0.95 → 改 4 列等宽
        var top = new[] {
            canvas.Find("Button_EnterLibrary"), canvas.Find("Button_Search"), canvas.Find("Button_ReturnInfo")
        };
        float[][] cols = { new[] { 0.030f, 0.250f }, new[] { 0.270f, 0.490f },
                           new[] { 0.510f, 0.730f }, new[] { 0.750f, 0.970f } };
        for (int i = 0; i < 3; i++)
        {
            if (top[i] == null) { sb.AppendLine("  !! 顶排少了 " + (i + 1) + " 号按钮,布局没动"); continue; }
            var rt = top[i].GetComponent<RectTransform>();
            Undo.RecordObject(rt, "导览屏顶排改 4 列");
            var a = rt.anchorMin; var b = rt.anchorMax;
            rt.anchorMin = new Vector2(cols[i][0], a.y);
            rt.anchorMax = new Vector2(cols[i][1], b.y);
            EditorUtility.SetDirty(rt);
        }

        var btn = MakeButton(canvas, "Button_Cinema", "星空观影",
            new Vector2(cols[3][0], 0f), new Vector2(cols[3][1], 0f),
            new Color(0.10f, 0.26f, 0.36f, 0.95f));
        var brt = btn.GetComponent<RectTransform>();
        brt.anchorMin = new Vector2(cols[3][0], 0f);
        brt.anchorMax = new Vector2(cols[3][1], 0f);
        brt.sizeDelta = new Vector2(0f, 78f);
        brt.anchoredPosition = new Vector2(0f, 150f);
        EditorUtility.SetDirty(brt);
        Bind(btn, cinema, "EnterFromKiosk");
        sb.AppendLine("  [导览屏] 顶排改成 4 列并新增 Button_Cinema「星空观影」→ TerraceCinema.EnterFromKiosk");
    }

    /// <summary>把按钮的 onClick 持久化绑到某个无参方法(Inspector 里看得见、可手改)。</summary>
    static void Bind(Button btn, MonoBehaviour target, string method)
    {
        if (btn == null || target == null) return;
        var so = new SerializedObject(btn);
        // 注意:C# 属性叫 onClick,但序列化字段是 m_OnClick —— 路径写错 FindProperty 会返回 null
        var calls = so.FindProperty("m_OnClick.m_PersistentCalls.m_Calls");
        if (calls == null)
        {
            sb.AppendLine("  !! Bind 找不到 m_OnClick 序列化路径(" + target.GetType().Name + "." + method + ")");
            return;
        }
        int idx = calls.arraySize;
        calls.InsertArrayElementAtIndex(idx);
        var c = calls.GetArrayElementAtIndex(idx);
        c.FindPropertyRelative("m_Target").objectReferenceValue = target;
        c.FindPropertyRelative("m_MethodName").stringValue = method;
        c.FindPropertyRelative("m_Mode").intValue = 0;              // FunctionCall(无参)
        c.FindPropertyRelative("m_Arguments.m_ObjectArgumentAssemblyTypeName")
            .stringValue = "UnityEngine.Object, UnityEngine";
        c.FindPropertyRelative("m_CallState").intValue = 2;         // RuntimeOnly
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>建一块世界空间面板:可读面 = -forward(坑 26),所以用"朝观众的方向"反推旋转。</summary>
    static GameObject Panel(Transform parent, string name, Vector3 pos, Vector3 viewerEye,
        int pxW, int pxH, float scale)
    {
        var read = (viewerEye - pos); read.y = 0f;
        if (read.sqrMagnitude < 1e-6f) read = -parent.forward;
        read.Normalize();
        return Canvas(name, parent, pos, read, pxW, pxH, scale);
    }

    // ------------------------------------------------------------ 复核

    static void Verify(Transform pad, Transform screen, (float elev, float yaw) moon)
    {
        if (pad == null) return;
        Physics.SyncTransforms();

        // ① 锚点必须踩在露台板上,否则玩家被传送到空中
        var hits = Physics.RaycastAll(new Ray(pad.position + Vector3.up * 0.6f, Vector3.down), 2.0f);
        string landed = hits.Length > 0 ? hits[0].collider.gameObject.name : "(空)";
        float top = hits.Length > 0 ? hits[0].point.y : -99f;
        sb.AppendLine(string.Format("  [复核·落点] 锚点下方打到「{0}」y={1:0.000} {2}", landed, top,
            landed == "TerraceDeck" && Mathf.Abs(top - DeckTop) < 0.05f ? "→ 踩在露台板上" : "!! 没踩到露台板"));

        // ② 屏上沿仰角 vs 月亮仰角:屏不许把月亮吃掉
        var eye = new Vector3(pad.position.x, DeckTop + 1.60f, pad.position.z);
        MeshRenderer sr = null;
        foreach (var r in screen.GetComponentsInChildren<MeshRenderer>())
            if (r.gameObject.name == "ScreenFace") { sr = r; break; }
        if (sr == null) sr = screen.GetComponentInChildren<MeshRenderer>();
        float topY = sr == null ? 0 : sr.bounds.max.y;
        float d = Vector3.Distance(new Vector3(eye.x, 0, eye.z), new Vector3(screen.position.x, 0, screen.position.z));
        float screenElev = Mathf.Atan2(topY - eye.y, Mathf.Max(d, 0.01f)) * Mathf.Rad2Deg;
        float clear = moon.elev - screenElev;
        sb.AppendLine(string.Format("  [复核·月亮不被挡] 眼高 {0:0.00} 距屏 {1:0.00} m 屏上沿 y {2:0.00} 仰角 {3:0.0}°"
            + "  月亮 {4:0.0}°  → 净空 {5:+0.0;-0.0}° {6}",
            eye.y, d, topY, screenElev, moon.elev, clear,
            clear >= 1.5f ? "✓ 月亮从屏上沿露出来" : (clear > 0f ? "!! 净空不足 1.5°,抬头就会蹭到屏边" : "!! 屏把月亮挡住了,降屏高或推远")));

        // ③ 屏面朝向:**Quad 的可见面是自身 -forward**(GameView 探针实测,见交接文档坑 30)。
        //    上一版在这里写的是"+forward",于是两块屏面都按 identity 摆 → 正对观众的其实是背面,
        //    被 _Cull=2 剔掉,视频和底色一概不显示,而这条复核当时照样打 ✓(它量的是组的 +Z,不是面的可见侧)。
        foreach (var quadName in new[] { "ScreenFace", "VideoSurface" })
        {
            var qd = FindDeep(screen, quadName);
            if (qd == null) { sb.AppendLine("  [复核·屏朝向] !! 场上没有 " + quadName); continue; }
            var vis = -qd.transform.forward;                       // 可见面法线
            float qa = Vector3.Angle(vis, (eye - qd.transform.position).normalized);
            sb.AppendLine(string.Format("  [复核·屏朝向] {0} 可见面 {1} 与「看向观众」差 {2:0.0}° {3}",
                quadName, F(vis), qa, qa < 3f ? "✓ 正对观众" : "!! 背面朝观众 → 会被剔除,这块面什么都不显示"));
        }

        // ④ 两块新 Canvas 的可读侧(-forward,坑 26)是否朝着该看它的人
        foreach (var probe in new[] {
            new { name = "TerracePanel", target = new Vector3(pad.position.x, 0f, pad.position.z),
                  who = "露台锚点" },
            new { name = "TerraceSign", target = new Vector3(18.2f, 0f, -10.5f), who = "二层环廊" } })
        {
            GameObject c = null;
            foreach (var t in Object.FindObjectsOfType<Transform>())
                if (t.name == probe.name) { c = t.gameObject; break; }
            if (c == null) { sb.AppendLine("  !! " + probe.name + " 不在场上了"); continue; }
            var read = -c.transform.forward;
            var to = probe.target - new Vector3(c.transform.position.x, 0f, c.transform.position.z);
            sb.AppendLine(string.Format("  [复核·{0}] 可读面 {1} → {2}{3} ✓", probe.name, F(read),
                probe.who, Vector3.Dot(read, to) > 0f ? "一侧" : "!! 背对"));
        }
        // ⑤ 屏上说明字是不是"正着读":光看 717×349 的缩略图判不出来(这类反字本轮已栽两次),
        //    所以量两个点积:字面要朝观众、且文字的阅读方向要与观众视线的右手侧同向。
        //    判据先拿场上已验收的中央导览屏做基准对照 —— 它现在游客看着是正字,数值必须同号。
        foreach (var probe in new[] {
            new { who = "露台说明字", go = FindDeep(screen, "Caption"), eye = eye },
            new { who = "基准·导览屏Title", go = FindDeep(pad.root, "Title"), eye = new Vector3(0f, 1.58f, 3.2f) } })
        {
            if (probe.go == null) { sb.AppendLine("  !! " + probe.who + " 找不到"); continue; }
            var p = probe.go.transform.position;
            var look = (p - probe.eye); look.y = 0f;
            if (look.sqrMagnitude < 1e-6f) continue;
            look.Normalize();
            var viewerRight = Vector3.Cross(Vector3.up, look);
            // TextMesh / UGUI 的可读面都是 -forward(基线:导览屏 Title 游客看着是正字,量出来就该是正号)
            float faceDot = Vector3.Dot(-probe.go.transform.forward, (probe.eye - p).normalized);
            float readDot = Vector3.Dot(probe.go.transform.right, viewerRight);
            sb.AppendLine(string.Format("  [复核·{0}] 字面朝观众 {1:0.00} 阅读方向同向 {2:0.00} → {3}",
                probe.who, faceDot, readDot,
                faceDot > 0f && readDot > 0.9f ? "正字 ✓" : "!! 反字/背面(检查 forward 与 right)"));
        }

        // ⑥ 观影模式接线:引用、面板、按钮绑定、座位落点、片单条数
        if (cinema == null) sb.AppendLine("  !! 没建 CinemaController(观影模式整条链缺失)");
        else
        {
            int bad = 0;
            if (cinema.origin == null) { sb.AppendLine("  !! cinema.origin 空 → 锁不了站位"); bad++; }
            if (cinema.seat == null) { sb.AppendLine("  !! cinema.seat 空"); bad++; }
            if (cinema.screen == null) { sb.AppendLine("  !! cinema.screen 空 → 选片不会上屏"); bad++; }
            if (cinema.teleporter == null) { sb.AppendLine("  !! cinema.teleporter 空 → 导览屏按钮传不过来"); bad++; }
            var panels = new[] {
                new { n = "须知", go = cinema.noticePanel }, new { n = "目录", go = cinema.catalogPanel },
                new { n = "播控", go = cinema.transportPanel } };
            foreach (var p in panels)
            {
                if (p.go == null) { sb.AppendLine("  !! " + p.n + "面板没建"); bad++; continue; }
                if (p.go.GetComponent<Canvas>() == null) { sb.AppendLine("  !! " + p.n + "面板没有 Canvas"); bad++; }
                if (p.go.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
                { sb.AppendLine("  !! " + p.n + "面板没有射线器 → 看得见点不着"); bad++; }
            }
            // 离座确认/退出观影已删除:场景里不该再留这两块
            if (FindDeep(cinema.transform, "LeavePanel") != null)
            { sb.AppendLine("  !! 还留着 LeavePanel(离座确认已取消)"); bad++; }
            if (FindDeep(cinema.transform, "Exit") != null)
            { sb.AppendLine("  !! 还留着 Exit 按钮(退出观影已取消)"); bad++; }
            // 目录必须落在"座位→银幕"方向的正前方,而不是跟着头部朝向或偏在一边
            if (cinema.catalogPanel != null && cinema.seat != null)
            {
                var f = cinema.seat.forward; f.y = 0f; f.Normalize();
                var to = cinema.catalogPanel.transform.position - cinema.seat.position; to.y = 0f;
                float along = Vector3.Dot(to, f);
                float lateral = Vector3.Dot(to, cinema.seat.right);
                float yawOff = Vector3.Angle(cinema.catalogPanel.transform.forward, cinema.seat.forward);
                bool frontOk = along > 0.5f && Mathf.Abs(lateral) < 0.05f && yawOff < 1f;
                sb.AppendLine(string.Format("  [复核·目录朝前] 沿座位→银幕 {0:0.00} m,横向偏 {1:+0.00;-0.00;0.00} m,"
                    + "朝向差 {2:0.0}° {3}", along, lateral, yawOff,
                    frontOk ? "✓ 正前方" : "!! 没摆正(要求沿前 >0.5、横向 <5 cm、朝向差 <1°)"));
                if (!frontOk) bad++;
            }
            int bound = 0;
            for (int i = 0; i < cinema.filmSlots.Length; i++)
                if (cinema.filmSlots[i] != null && cinema.filmSlots[i].onClick.GetPersistentEventCount() > 0) bound++;
            if (bound < cinema.filmSlots.Length)
            { sb.AppendLine("  !! 目录槽只绑了 " + bound + "/" + cinema.filmSlots.Length + " 颗"); bad++; }
            var acts = new[] {
                new { n = "暂停", b = cinema.pauseButton }, new { n = "倍速", b = cinema.speedButton },
                new { n = "后退5秒", b = cinema.seekBackButton }, new { n = "前进5秒", b = cinema.seekFwdButton },
                new { n = "跳过", b = cinema.skipButton }, new { n = "关闭影片", b = cinema.stopButton },
                new { n = "须知确认", b = cinema.ackButton }, new { n = "回看须知", b = cinema.noticeButton },
                new { n = "回一层大厅", b = cinema.lobbyButton } };
            if (cinema.lobbyTeleporter == null)
            { sb.AppendLine("  !! cinema.lobbyTeleporter 空 → 播控面板那颗「回一层大厅」按了不会传送"); bad++; }
            int abBad = 0;
            foreach (var a in acts)
                if (a.b == null || a.b.onClick.GetPersistentEventCount() == 0)
                { sb.AppendLine("  !! 按钮「" + a.n + "」没建或没绑方法"); bad++; abBad++; }
            sb.AppendLine(string.Format("  [复核·观影按钮] {0}/{1} 颗已建且绑上方法{2}",
                acts.Length - abBad, acts.Length, abBad == 0 ? " ✓" : " !!"));

            var sc = cinema.screen;                       // 空引用上面已报过,这里只跳过卡片接线复核
            if (sc != null)
            {
                var texts = new[] {
                    new { n = "片名", t = sc.cardTitle }, new { n = "类型时长", t = sc.cardMeta },
                    new { n = "简介", t = sc.cardSummary }, new { n = "操作提示", t = sc.cardHint },
                    new { n = "解码路线", t = sc.cardDecoder } };
                int miss = 0;
                foreach (var x in texts) if (x.t == null) { sb.AppendLine("  !! 信息卡缺「" + x.n + "」那行"); miss++; bad++; }
                sb.AppendLine(string.Format("  [复核·信息卡] 五行文字接线 {0}/{1}{2}",
                    texts.Length - miss, texts.Length, miss == 0 ? " ✓" : " !!"));
            }

            string done = "|";
            foreach (var f in TerraceScreen.Films)
            {
                string key = (f.codec ?? "") + "|";
                if (done.Contains(key)) continue;
                done += key;
                int n = 0;
                foreach (var g in TerraceScreen.Films)
                    if (((g.codec ?? "") + "|") == key) n++;
                sb.AppendLine("  [复核·解码] " + VideoDecodeProbe.CodecLabel(VideoDecodeProbe.MimeOf(f.codec))
                    + " ×" + n + " 条 → 片源闸门:" + CodecGate(f.codec)
                    + " | 本机路线:" + VideoDecodeProbe.Report(VideoDecodeProbe.PlanFor(f.codec, true)));
            }
            sb.AppendLine("  [复核·本机] " + VideoDecodeProbe.PlatformDescription
                + " —— 这是开发机的答案,头盔上要跑冒烟或看 logcat");

            Physics.SyncTransforms();
            var sp = cinema.seat == null ? Vector3.zero : cinema.seat.position;
            var hits2 = Physics.RaycastAll(new Ray(sp + Vector3.up * 0.6f, Vector3.down), 2.0f);
            bool onDeck = hits2.Length > 0 && hits2[0].collider.gameObject.name == "TerraceDeck";
            if (!onDeck) { sb.AppendLine("  !! 座位锚点没踩在露台板上"); bad++; }
            sb.AppendLine(string.Format("  [复核·观影模式] 引用缺失/面板/按钮/落点问题 {0} 处,片单 {1} 条,"
                + "目录槽绑定 {2}/{3} 颗,座位{4} {5}", bad, TerraceScreen.Films.Length, bound,
                cinema.filmSlots.Length, onDeck ? "在板上 ✓" : "!!",
                bad == 0 ? "→ 接线齐" : "→ 有缺项"));
        }
    }

    /// <summary>
    /// 片源编码闸门:内置软解码器(VLC for Unity)是源码发布、Android 侧要自己 meson+NDK 交叉编译,
    /// 试用版还会烧水印 —— 所以"能不能播"这件事目前**只能在片源格式上保证**,这里把话说明白。
    /// </summary>
    static string CodecGate(string codec)
    {
        string c = (codec ?? "").Trim().ToLowerInvariant();
        if (c == "" || c == "h264" || c == "avc" || c == "x264") return "安全(AOSP 自带软解,无硬解也能播)";
        if (c == "hevc" || c == "h265" || c == "x265" || c == "vp9")
            return "警告(老一体机可能没硬解,只能吃平台软解 → 建议转 H.264)";
        return "危险(一体机普遍没有该解码器 → 必须转 H.264 再上屏)";
    }

    static GameObject FindDeep(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t.gameObject;
        return null;
    }

    static string F(Vector3 v)
    {
        return string.Format("({0:0.000},{1:0.000},{2:0.000})", v.x, v.y, v.z);
    }
}
