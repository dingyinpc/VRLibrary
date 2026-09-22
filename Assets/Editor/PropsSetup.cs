using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 把工单 A/B/C 交付的四个 FBX(圆桌/凳/中央服务台/吊顶灯槽)搬进本工程:
/// 拷资产 → 配导入器 → 建 3 个材质 → 换掉脚本生成的 Cube 家具与手摆的吊顶。
/// 依据:E:\Blender\Blender工程\interior_props\交付说明_室内构件.md
/// 三条已定案的判断(2026-09-20):
///   ① 吊顶不加遮光檐,先看 Unity 真实渲染再判(要改也是挪发光岛,不动几何);
///   ② 服务台 60 mm ledge 保持字面值(本意是"给屏一个落脚",不是桌板);
///   ③ 桌/凳高度在 Unity 侧缩高度轴修正(台面顶 0.975→0.75、凳面 0.59→0.45),水平尺寸不动。
///      高度轴在 -90°X 之后是 local Z,不是 local Y —— 缩错轴会把家具压扁而不是压矮。
/// 换模型不只是换几何:桌面上摆着的配件(书目屏/台灯)和服务台的两块世界空间 Canvas
/// 都是按旧台面顶 / 旧屏位摆的,模型一矮一挪就必须跟着重新落座,否则就是悬浮的配件。
/// 落点一律"读现值再放",不硬编码坐标 —— 吊顶是手摆的,只有场景自己知道格网在哪。
/// 生成器同步:桌/凳/服务台由 VRLibrarySetup 调本文件的 PlaceGenerated(算法只有一份);
///   吊顶是手摆对象,全场没有一行代码造 CeilTile → 整链重建后必须重跑本菜单补吊顶。
/// 快照 Logs/SmartLibrary.pre-props.unity 一旦存在就不再覆盖,重跑多少次回退点都是同一个。
/// 回退:Logs/SmartLibrary.pre-props.unity 是本步之前的字节级快照。
/// </summary>
public static class PropsSetup
{
    const string SrcTex = @"E:\Blender\Blender贴图\interior_props";
    const string SrcModel = @"E:\Blender\Blender工程\interior_props";
    const string Dir = "Assets/Textures/InteriorProps";
    const string MatDir = "Assets/Materials";
    const string ReportPath = "Logs/props-import.txt";
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Snapshot = "Logs/SmartLibrary.pre-props.unity";

    /// <summary>一个可导入的模型 + 它要用的那份材质(桌和凳共用 M_ReadingFurniture)。(public:生成器要用)</summary>
    public struct PropSpec
    {
        public string tag, model, srcFbx, matPath, matName, texSet;
        public Vector3 blenderSize;   // 交付说明 §2 实测的 Blender 包围盒(Z-up)
        public float yScale;          // Unity 侧沿高度轴缩的比例(1 = 不动)
        public float yOffset;         // 脚下那个面的顶高:家具不能陷进砖/毯里,也不能悬空
        public string[] keep;         // 本不该删、也不该报警的保留子物体名前缀(配件 / 世界空间 Canvas)
        public string[] rides;        // 保留 + 还要随新顶面一起平移的前缀,格式 "名前缀=期望缝隙(米)"
    }

    public static readonly PropSpec Table = new PropSpec
    {
        tag = "圆桌", model = Dir + "/SM_ReadingTable_01.fbx",
        srcFbx = SrcModel + "/SM_ReadingTable_01.fbx",
        matPath = MatDir + "/M_ReadingFurniture.mat", matName = "M_ReadingFurniture",
        texSet = "T_ReadingFurniture", blenderSize = new Vector3(1f, 1f, 0.975f),
        yScale = 0.75f / 0.975f, yOffset = 0.037f,
        // 预检实测:每张桌上还摆着书目屏和台灯,它们不是 Cube 零件,但**坐在桌面上**
        keep = new[] { "Stool" },
        // "名=期望缝隙":换装前预检(旧台面顶 0.975)量到的各自底面高 0.988 / 0.980 / 1.075。
        // 灯头本就该悬在灯杆顶,所以"离台面有缝"对它是正常的 —— 要守的不变量是"缝隙没变大",
        // 缝隙变大才是配件漏跟(整桌降 0.188,漏跟一个就多出 0.188)。
        rides = new[] { "DisplayBook=0.013", "LampPole=0.005", "LampBulb=0.100" },
    };
    public static readonly PropSpec Stool = new PropSpec
    {
        tag = "凳", model = Dir + "/SM_ReadingStool_01.fbx",
        srcFbx = SrcModel + "/SM_ReadingStool_01.fbx",
        matPath = MatDir + "/M_ReadingFurniture.mat", matName = "M_ReadingFurniture",
        texSet = "T_ReadingFurniture", blenderSize = new Vector3(0.5f, 0.5f, 0.59f),
        yScale = 0.45f / 0.59f, yOffset = 0.037f,
    };
    public static readonly PropSpec Kiosk = new PropSpec
    {
        tag = "服务台", model = Dir + "/SM_ServiceKiosk_01.fbx",
        srcFbx = SrcModel + "/SM_ServiceKiosk_01.fbx",
        matPath = MatDir + "/M_ServiceKiosk.mat", matName = "M_ServiceKiosk",
        texSet = "T_Kiosk", blenderSize = new Vector3(1.35f, 1.35f, 1.9581f),
        yOffset = 0.028f,   // 服务台不在服务地毯上(那块毯在 z=-16 的老 ServiceDesk 下),踩的是 2 m 方砖顶面
        yScale = 1f,
        keep = new[] { "InfoCanvas", "SearchPanel", "Holo" },
    };
    static readonly PropSpec Coffer = new PropSpec
    {
        tag = "吊顶", model = Dir + "/SM_CeilingCoffer_01.fbx",
        srcFbx = SrcModel + "/SM_CeilingCoffer_01.fbx",
        matPath = MatDir + "/M_CeilingCoffer.mat", matName = "M_CeilingCoffer",
        texSet = "T_CeilingCoffer", blenderSize = new Vector3(5.4f, 5.4f, 0.15f),
        yScale = 1f, yOffset = 0f,
    };

    static readonly PropSpec[] All = { Table, Stool, Kiosk, Coffer };

    /// <summary>三份材质:各自贴图集 + 发光色要抄的场上同族霓虹材质。</summary>
    struct MatSpec
    {
        public string name, path, set, refMat, tag;
        public int maxSize;
        public Color fallback;    // 抄不到现值时的线性兜底色
    }

    static readonly MatSpec[] Mats =
    {
        new MatSpec { name = "M_ReadingFurniture", path = MatDir + "/M_ReadingFurniture.mat",
            set = "T_ReadingFurniture", refMat = "Assets/Materials/M_NeonCyan.mat", tag = "桌+凳灯带",
            maxSize = 512, fallback = new Color(120f / 255f, 199f / 255f, 210f / 255f) },
        new MatSpec { name = "M_ServiceKiosk", path = MatDir + "/M_ServiceKiosk.mat",
            set = "T_Kiosk", refMat = "Assets/Materials/M_NeonMagenta.mat", tag = "底座霓虹",
            maxSize = 512, fallback = new Color(248f / 255f, 181f / 255f, 208f / 255f) },
        new MatSpec { name = "M_CeilingCoffer", path = MatDir + "/M_CeilingCoffer.mat",
            set = "T_CeilingCoffer", refMat = "Assets/Materials/M_LightBar.mat", tag = "槽底发光",
            maxSize = 1024, fallback = new Color(152f / 255f, 182f / 255f, 194f / 255f) },
    };

    // ------------------------------------------------------------ 1. 导入

    [MenuItem("VRLibrary/导入室内构件(贴图+材质)")]
    public static void Import()
    {
        var sb = NewReport("导入");
        CopyIn(sb);
        foreach (var m in Mats)
        {
            SetupTex(Dir + "/" + m.set + "_BaseColor.png", TextureImporterType.Default, true, AndroidFmt(512), m.maxSize, sb);
            SetupTex(Dir + "/" + m.set + "_URPMask.png", TextureImporterType.Default, false, AndroidFmt(512), m.maxSize, sb);
            SetupTex(Dir + "/" + m.set + "_Normal.png", TextureImporterType.NormalMap, false, AndroidFmt(512), m.maxSize, sb);
            SetupTex(Dir + "/" + m.set + "_Emissive.png", TextureImporterType.Default, true, AndroidFmt(512), m.maxSize, sb);
        }
        foreach (var p in All) SetupModel(p, sb);
        BuildMaterials(sb);
        VerifyModels(sb);
        Write(sb);
        AssetDatabase.SaveAssets();
        Debug.Log(sb.ToString());
    }

    static TextureImporterFormat AndroidFmt(int max)
    {
        return max >= 1024 ? TextureImporterFormat.ASTC_4x4 : TextureImporterFormat.ASTC_6x6;
    }

    // ------------------------------------------------------------ 2. 预检(只读)

    /// <summary>
    /// 动手前先量场景现值:落点、现有子物体、吊顶格网、以及"删掉会不会连带丢掉组件"。
    /// 吊顶是手摆的(Assets 里查不到任何生成脚本),所以格网只能从场景里读,不能照文档硬编码。
    /// </summary>
    [MenuItem("VRLibrary/室内构件预检(只报告)")]
    public static void Precheck()
    {
        var sb = NewReport("预检");
        var root = RequireRoot(sb);
        if (root == null) { Write(sb); Debug.Log(sb.ToString()); return; }

        DumpChildren(sb, root, "阅读区", "ReadingZone");
        DumpChildren(sb, root, "服务台", "CentralKiosk");
        DumpChildren(sb, root, "吊顶", "Ceiling");

        var ceil = root.transform.Find("Ceiling");
        if (ceil != null)
        {
            var xs = new List<float>(); var zs = new List<float>();
            int tiles = 0, glows = 0, already = 0; Vector3 size = Vector3.zero; float bottom = 999f, top = -999f;
            for (int i = 0; i < ceil.childCount; i++)
            {
                var c = ceil.GetChild(i);
                var r = c.GetComponentInChildren<MeshRenderer>();
                if (r == null) continue;
                // 只认 CeilTile_ 的话,第二轮场上只剩 Coffer_*,这里会量到 tile 0 个 +
                // 尺寸 0 + 缝宽 999,报出"换上去会露缝"的假警报(换装路径本来就把 Coffer_ 当格心来源)。
                if (c.name.StartsWith("CeilTile_")) { tiles++; }
                else if (c.name.StartsWith("Coffer_")) { tiles++; already++; }
                else if (c.name.StartsWith("CeilGlow_")) glows++;
                else continue;
                size = r.bounds.size;
                bottom = Mathf.Min(bottom, r.bounds.min.y); top = Mathf.Max(top, r.bounds.max.y);
                xs.Add(c.position.x); zs.Add(c.position.z);
            }
            sb.AppendLine(string.Format("  [吊顶格网] tile {0} 个(其中 {1} 个已是灯槽 Coffer_*)/ glow {2} 个"
                + "  tile 世界尺寸 {3:0.000}×{4:0.000}×{5:0.000}  y {6:0.000}~{7:0.000}",
                tiles, already, glows, size.x, size.y, size.z, bottom, top));
            sb.AppendLine("    x 集合 " + FmtSet(xs) + "  z 集合 " + FmtSet(zs));
            sb.AppendLine("    → 灯槽实例将逐个放在这些 x/z 上,y 取 tile 底面 " + bottom.ToString("0.000")
                + "(交付说明 §6 说的 5.525 与此" + (Mathf.Abs(bottom - 5.525f) < 0.002f ? "一致" : "不一致,以实测为准") + ")");
            float gap = NeighbourGap(xs, zs);
            float seam = gap - size.x;
            sb.AppendLine("    相邻格心间距 " + gap.ToString("0.000")
                + " → 缝宽 = 间距 - tile 边长 = " + seam.ToString("0.000")
                + ";灯槽模块自身 5.4 m,所以拼出来应当正好是 0.600 m");
            // 上一版这里断言"tile 必须 6 m 见方",量到 5.400 就报警 —— 那是检查写错了:
            // 6 m 是**格距**,场上 tile 本来就只占 5.4 m、留 0.6 m 不通风格缝。改成量对的东西。
            // blenderSize 是 Blender Z-up 的 (平面X, 平面Y, 厚度Z);绕 X 转 -90° 后
            // 世界足迹 = (x, 厚度, y)。拿 world size.z 去比 blenderSize.z 就是拿 5.4 比 0.15,必炸。
            float dx = size.x - Coffer.blenderSize.x, dz = size.z - Coffer.blenderSize.y;
            if (Mathf.Abs(dx) > 0.01f || Mathf.Abs(dz) > 0.01f)
                sb.AppendLine(string.Format("    !! 现有 tile 足迹 {0:0.000}×{1:0.000} vs 灯槽 {2:0.000}×{3:0.000}"
                    + "(差 {4:+0.000;-0.000;0} / {5:+0.000;-0.000;0})→ 换上去会露缝或互相插进去",
                    size.x, size.z, Coffer.blenderSize.x, Coffer.blenderSize.y, dx, dz));
            if (Mathf.Abs(seam - 0.6f) > 0.02f)
                sb.AppendLine("    !! 缝宽 " + seam.ToString("0.000") + " 不是交付说明 §2 实测的 0.600 m → 格距被改过,灯槽铺上去对不上");
        }

        // 传送落点与家具是否重叠。必须**每次都打测量值**:上一版只在重叠时出声,
        // 结果"没报警"到底是"没重叠"还是"根本没找到这个 pad"完全分不出来。
        // 实测:Pad_Reading 在场景里已被挪到 x=10(生成器 VRLibrarySetup.cs:282 还写着 13),
        // 离最近的 Table_1(13,0,0) 有 3 m,所以不重叠是真的不重叠。
        sb.AppendLine("  [传送落点 vs 桌子]");
        foreach (var padName in new[] { "Pad_Reading", "Pad_Shelves", "Pad_Entry", "Pad_Service" })
        {
            var pad = Find(root.transform, padName);
            if (pad == null) { sb.AppendLine("    " + padName + " 场景里没有(不叫这个名或不在 SmartLibrary 下)"); continue; }
            var tbls = new List<Transform>();
            foreach (var tt in root.GetComponentsInChildren<Transform>(true))
                if (tt.name.StartsWith("Table_")) tbls.Add(tt);
            if (tbls.Count == 0) { sb.AppendLine("    " + padName + " 世界 " + pad.position.ToString("F2") + " 但场上没有 Table_"); continue; }
            var near = tbls[0]; float d = 999f;
            foreach (var t in tbls)
            {
                float dd = Vector2.Distance(new Vector2(pad.position.x, pad.position.z),
                                            new Vector2(t.position.x, t.position.z));
                if (dd < d) { d = dd; near = t; }
            }
            sb.AppendLine(string.Format("    {0} 世界({1:0.00},{2:0.00}) 最近 {3}({4:0.00},{5:0.00}) 距 {6:0.00} m {7}",
                pad.name, pad.position.x, pad.position.z, near.name, near.position.x, near.position.z, d,
                d < 1.2f ? "!! 玩家会落在家具里(本次不改落点,只报)" : "→ 不重叠"));
        }

        // 脚下踩的是什么面。yOffset 是写死的常数,所以每次都要拿现场量到的高度对一遍,
        // 不然毯子/地砖一挪高,家具就会陷进去或悬空,而报告里什么都看不出来。
        sb.AppendLine("  [落点高度]");
        ReportSurface(sb, "Floor_Detailed/ReadingCarpet/Carpet", "桌+凳", Table.yOffset);
        sb.AppendLine("    服务台 yOffset " + Kiosk.yOffset.ToString("0.000")
            + " 对比 2 m 方砖顶面 0.028(TechFloorSetup.cs:213 实测)→ 服务台踩地砖,不踩毯");
        sb.AppendLine("    注:旧 Cube 家具底面落在 y=0.000,即**陷进**砖里 28 mm / 毯里 37 mm;"
            + "本次改成踩在面顶,所以模型整体比旧件高起这一截");

        // SeatCanvases 会改写这两块的位姿,先在只读阶段把"现值 / 将要算出的缩放"打出来
        foreach (var name in new[] { "InfoCanvas", "SearchPanel" })
        {
            var t = Find(root.transform, name);
            if (t == null) { sb.AppendLine("  !! 没有 " + name); continue; }
            var rt = t.GetComponent<RectTransform>();
            float w = rt == null ? 0f : rt.rect.width, h = rt == null ? 0f : rt.rect.height;
            float s = (w > 1f && h > 1f) ? Mathf.Min(1.22f / w, 0.74f / h) * 0.98f : 0f;
            sb.AppendLine(string.Format("  {0} 现 局部{1} 缩 {2:0.00000} rect {3:0}×{4:0} 现正字侧 {5}"
                + " → 换装后要缩成 {6:0.00000} 摆到 (0, {7:0.000}, 0.28) 俯 -8° + 转 180° Y(正字朝 +Z 给游客)",
                name, t.localPosition.ToString("F3"), t.localScale.x, w, h, ReadSide(t), s,
                Kiosk.yOffset + 1.55f));
        }

        foreach (var p in All) ReportAsset(p, sb);
        Write(sb);
        Debug.Log(sb.ToString());
    }

    // ------------------------------------------------------------ 3. 换装(会改场景)

    [MenuItem("VRLibrary/换上室内构件(替换Cube)")]
    public static void ApplyToScene()
    {
        var sb = NewReport("换装");
        var root = RequireRoot(sb);
        if (root == null) { Write(sb); Debug.Log(sb.ToString()); return; }

        var scene = SceneManager.GetActiveScene();
        if (File.Exists(Snapshot))
            sb.AppendLine("  快照已在(不覆盖)" + Snapshot + " 最后写入 "
                + File.GetLastWriteTime(Snapshot).ToString("MM-dd HH:mm:ss")
                + " —— 这就是换装前的字节级回退点;重跑会覆盖旧场景,但不会覆盖它");
        else if (!scene.isDirty)
        {
            Directory.CreateDirectory("Logs");
            File.Copy(ScenePath, Snapshot);
            sb.AppendLine("  已快照 " + Snapshot + " " + new FileInfo(Snapshot).Length + " 字节(本步之前的字节级回退点)");
        }
        else sb.AppendLine("  !! 场景有未保存改动 → 不覆盖快照(可能已含本次改动),先确认再回退");

        int okCount = 0;
        okCount += ReplaceFurniture(root, sb);
        okCount += ReplaceKiosk(root, sb);
        okCount += ReplaceCeiling(root, sb);

        sb.AppendLine("  完成 " + okCount + "/3 组");
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        sb.AppendLine("场景 " + scene.path + " 已保存,isDirty(应为 False)=" + scene.isDirty);
        sb.AppendLine("回退本步:copy " + Snapshot + " → " + ScenePath + " 后重开场景");
        Write(sb);
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// A:三张圆桌 + 凳子。父节点(Table_i / Stool)原样保留,只换掉里面的 Cube 零件。
    /// 桌面从 0.975 降到 0.75,桌上摆着的书目屏和台灯必须跟着降 —— 它们原来是"贴着旧桌面"放的,
    /// 所以按同一世界位移平移最忠实(它们与旧台面之间那点缝/嵌深原样保留),不要重新猜坐标。
    /// </summary>
    static int ReplaceFurniture(GameObject root, StringBuilder sb)
    {
        var zone = root.transform.Find("ReadingZone");
        if (zone == null) { sb.AppendLine("!! 找不到 ReadingZone"); return 0; }
        int tables = 0;
        for (int i = 0; i < zone.childCount; i++)
        {
            var table = zone.GetChild(i);
            if (!table.name.StartsWith("Table_")) { sb.AppendLine("  !! ReadingZone 下清单外的节点 " + table.name + " —— 没碰"); continue; }

            float oldTop = TopOf(table, "Top");   // 删掉之前先量,这是配件唯一可靠的参照
            var stool = table.Find("Stool");
            // 先记落点再删:凳子的世界位置决定它是否还在桌面半径内(缩高度轴不动 X/Z,所以不会挪位)
            Vector3 stoolPos = stool == null ? Vector3.zero : stool.position;

            float newTop = Place(table, Table, new[] { "Top", "GlowRing", "Leg", "Base" }, "TableMesh", sb);
            if (newTop < 0f) continue;
            tables++;
            sb.AppendLine(string.Format("    桌面顶 {0:0.000} → {1:0.000}(Δ {2:+0.000;-0.000;0} m)",
                oldTop, newTop, newTop - oldTop));
            RideAccessories(table, oldTop, newTop, sb);

            if (stool != null)
            {
                sb.AppendLine(string.Format("    (凳世界落点 {0:0.000},{1:0.000},{2:0.000},距桌心 {3:0.000} m)",
                    stoolPos.x, stoolPos.y, stoolPos.z,
                    Vector3.Distance(stoolPos, table.position + Vector3.up * 0.02f)));
                Place(stool, Stool, new[] { "Seat", "Leg", "Base", "StoolRing" }, "StoolMesh", sb);
            }
            else sb.AppendLine("  !! " + table.name + " 下没有 Stool");
        }
        return tables > 0 ? 1 : 0;
    }

    /// <summary>某个直接子零件的世界顶高;找不到返回 NaN(表示这是第二次跑,旧 Cube 已经不在了)。</summary>
    static float TopOf(Transform parent, string childName)
    {
        var t = parent.Find(childName);
        var r = t == null ? null : t.GetComponent<MeshRenderer>();
        return r == null ? float.NaN : r.bounds.max.y;
    }

    /// <summary>把桌上"贴着台面摆"的配件整体平移 Δ,让它们继续坐在新台面上。</summary>
    static void RideAccessories(Transform table, float oldTop, float newTop, StringBuilder sb)
    {
        if (float.IsNaN(oldTop))
        {
            sb.AppendLine("    !! 读不到旧 Top(本节点大概已经换过一次)→ 配件不挪,自己看一眼是否悬空");
            return;
        }
        float d = newTop - oldTop;
        if (Mathf.Abs(d) < 0.0005f) { sb.AppendLine("    配件无需移动(Δ=0)"); return; }
        foreach (var ch in DirectChildren(table))
        {
            if (!IsRide(ch.name, Table.rides)) continue;
            Undo.RecordObject(ch, "配件随新桌面降高");
            ch.position = new Vector3(ch.position.x, ch.position.y + d, ch.position.z);
            sb.AppendLine("    " + ch.name + " 降到 y " + ch.position.y.ToString("0.000")
                + " 顶 " + BoundsTop(ch).ToString("0.000") + "(新台面 " + newTop.ToString("0.000") + ")");
        }
    }

    static float BoundsTop(Transform t)
    {
        var r = t.GetComponentInChildren<MeshRenderer>();
        return r == null ? float.NaN : r.bounds.max.y;
    }

    static List<Transform> DirectChildren(Transform t)
    {
        var l = new List<Transform>();
        for (int i = 0; i < t.childCount; i++) l.Add(t.GetChild(i));
        return l;
    }

    /// <summary>
    /// B:中央服务台。预检实测 CentralKiosk 的子物体是
    /// [Pedestal InfoCanvas KioskGlowRing HoloOrbiters HoloBeam HoloRing_0 HoloRing_1 SearchPanel]
    /// —— 霓虹环叫 KioskGlowRing(不是 Cylinder/NeonRing),要删的只有 Pedestal 和它这两个。
    /// 换完模型还要把两块世界空间 Canvas 挪进新屏框:旧的 InfoCanvas 在局部 (0,2.35,0.55)、
    /// SearchPanel 在 (0,1.85,0.55),那是照着"0.6 m 粗、1.0 m 高的 Pedestal + 悬浮屏"摆的;
    /// FBX 的屏框中心在局部 (0,1.55,+0.28)、绕 X 俯 -8°、中央 1.22×0.74 打通。不挪就是两个洞。
    /// </summary>
    static int ReplaceKiosk(GameObject root, StringBuilder sb)
    {
        var kiosk = root.transform.Find("CentralKiosk");
        if (kiosk == null) { sb.AppendLine("!! 找不到 CentralKiosk"); return 0; }
        var comp = kiosk.GetComponent<MonoBehaviour>();
        sb.AppendLine("  CentralKiosk 上的组件:" + string.Join(", ",
            System.Array.ConvertAll(kiosk.GetComponents<Component>(), c => c == null ? "missing!" : c.GetType().Name)));
        float top = Place(kiosk, Kiosk, new[] { "Pedestal", "KioskGlowRing" }, "KioskMesh", sb);
        if (top < 0f) return 0;
        SeatCanvases(kiosk, sb);
        var canvas = kiosk.Find("InfoCanvas");
        if (canvas == null) sb.AppendLine("    !! InfoCanvas 不见了 → 导览屏整块没了,立刻回退");
        return comp != null && canvas != null ? 1 : 0;
    }

    /// <summary>
    /// 把两块屏对进新屏框。缩放不写死:量各自 RectTransform 的实际尺寸,
    /// 反算"刚好塞进 1.22×0.74 洞口"的比例 —— 生成器和场景里这两块 Canvas 的 sizeDelta 并不同源,
    /// 写死一个数迟早对不上。两块屏由 LibraryUIController / BookSearchController 互斥显隐,
    /// 所以同面共位是对的;SearchPanel 沿屏面法线让出 20 mm,避免万一同时激活时打面。
    /// </summary>
    static void SeatCanvases(Transform kiosk, StringBuilder sb)
    {
        const float openW = 1.22f, openH = 0.74f;      // 交付说明 §4-B:屏框中央 1.22×0.74 打通
        // 屏框中心/倾角是**模型自己**的局部量,加上实例脚下那个 yOffset 才是本节点的局部量
        var center = new Vector3(0f, Kiosk.yOffset + 1.55f, 0.28f);
        // UGUI 世界空间 Canvas 的正字面是 **-forward**(默认旋转下相机要在 -Z 才读得到),
        // 所以"要朝 +Z 给游客读"必须额外转 180° Y。只写 Euler(-8,0,0) 会让正字朝 -Z,
        // 台子正面(+Z,ledge 那一侧)看到的是背面透过来的镜像 —— 用户报的"面板弄反了"就是它。
        // Euler(-8,180,0) 与 v7.11 场上那份 (0, 0.9975641, 0.06975647, ~0) 逐分量相同。
        var tilt = Quaternion.Euler(-8f, 180f, 0f);
        foreach (var name in new[] { "InfoCanvas", "SearchPanel" })
        {
            var t = kiosk.Find(name);
            if (t == null) { sb.AppendLine("    没有 " + name + " → 本场景不带这块屏(整链重建只造 InfoCanvas,"
                + "SearchPanel 是手摆的),跳过落座"); continue; }
            var rt = t.GetComponent<RectTransform>();
            if (rt == null) { sb.AppendLine("    !! " + name + " 没有 RectTransform,不是 Canvas"); continue; }
            float w = Mathf.Max(rt.rect.width, 1f), h = Mathf.Max(rt.rect.height, 1f);
            float s = Mathf.Min(openW / w, openH / h) * 0.98f;   // 2% 余量:屏框 40 mm 边宽别被压到
            Undo.RecordObject(t, "屏对进新屏框");
            // tilt 里含 180° Y,局部 +Z 已经指向世界 -Z,所以要减号才朝 +Z 游客那侧让出 20 mm
            var want = center + tilt * new Vector3(0f, 0f, name == "SearchPanel" ? -0.02f : 0f);
            // 写 RectTransform 自己的锚点位置,不写 Transform.localPosition —— 后者只落到了 z:
            // 场景 YAML 里 x/y 存在 m_AnchoredPosition、z 存在 m_LocalPosition,一次 Apply 之后
            // 重开场景 z=0.280/0.2998 都对、俯角和缩放也都对,唯独 y 退回旧值(2.35/1.85)。
            // 锚点与轴心都是 (0.5,0.5) 且父节点是普通 Transform(缩放 1),所以锚点位置 ≡ 局部位置。
            rt.anchoredPosition3D = want;
            t.localRotation = tilt;
            rt.localScale = Vector3.one * s;
            EditorUtility.SetDirty(t);
            Canvas.ForceUpdateCanvases();
            bool stuck = (t.localPosition - want).magnitude > 0.001f;
            sb.AppendLine(string.Format("    {0} rect {1:0}×{2:0} → 缩放 {3:0.00000}(占 {4:0.00}×{5:0.00} m,"
                + " 洞口 {6:0.00}×{7:0.00}) 局部位置 {8} 锚点 ({9:0.000},{10:0.000}){11}",
                name, w, h, s, w * s, h * s, openW, openH, t.localPosition.ToString("F3"),
                rt.anchoredPosition.x, rt.anchoredPosition.y,
                stuck ? " !! 写进去又读不回来" : ""));
            if (h * s > openH + 0.001f || w * s > openW + 0.001f)
                sb.AppendLine("    !! 算完还是超出洞口 → 检查 sizeDelta / 洞口尺寸");
        }
    }

    /// <summary>C:吊顶。手摆对象、无生成脚本,所以逐个沿用现有 CeilTile 的 x/z 与底面 y。</summary>
    static int ReplaceCeiling(GameObject root, StringBuilder sb)
    {
        var ceil = root.transform.Find("Ceiling");
        if (ceil == null) { sb.AppendLine("!! 找不到 Ceiling"); return 0; }
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Coffer.model);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(Coffer.matPath);
        if (prefab == null || mat == null)
        { sb.AppendLine("!! 缺吊顶资产或材质 → 先跑 VRLibrary/导入室内构件(贴图+材质)"); return 0; }

        var slots = new List<Vector3>();
        var doomed = new List<GameObject>();
        int rerun = 0;
        for (int i = 0; i < ceil.childCount; i++)
        {
            var c = ceil.GetChild(i);
            if (c.name.StartsWith("CeilTile_") || c.name.StartsWith("Coffer_"))
            {
                var r = c.GetComponentInChildren<MeshRenderer>();
                slots.Add(new Vector3(c.position.x, r == null ? 5.525f : r.bounds.min.y, c.position.z));
                doomed.Add(c.gameObject);
                if (rerun == 0 && c.name.StartsWith("Coffer_")) rerun = 1;
            }
            else if (c.name.StartsWith("CeilGlow_")) doomed.Add(c.gameObject);
            else sb.AppendLine("  !! Ceiling 下清单外的子物体 " + c.name + " —— 没删");
        }
        sb.AppendLine("  吊顶:读到 " + slots.Count + " 个格心,待删 " + doomed.Count + " 个("
            + (rerun == 0 ? "CeilTile/CeilGlow,即原来的 Cube" : "上一轮放的 Coffer_,格心沿用它们的落点") + ")");
        if (slots.Count == 0) return 0;

        foreach (var g in doomed) Undo.DestroyObjectImmediate(g);
        for (int i = 0; i < slots.Count; i++)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(inst, "换上 CeilingCoffer");
            inst.name = "Coffer_" + i;
            inst.transform.SetParent(ceil, false);
            inst.transform.position = slots[i];
            inst.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            inst.transform.localScale = Vector3.one;
            var r = inst.GetComponentInChildren<MeshRenderer>();
            r.sharedMaterial = mat;
            // 吊顶在灯上方/玩家头顶,不参与投影;原来那批 Cube 也没配阴影策略,保持一致
            r.shadowCastingMode = ShadowCastingMode.Off;
            foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;
            var col = inst.GetComponentInChildren<Collider>(true);
            if (col != null) Object.DestroyImmediate(col);
            var b = r.bounds;
            if (i == 0 || i == slots.Count - 1)
                sb.AppendLine(string.Format("    Coffer_{0} 落点({1:0.000},{2:0.000},{3:0.000}) 世界 {4:0.000}×{5:0.000}×{6:0.000} y {7:0.000}~{8:0.000}",
                    i, slots[i].x, slots[i].y, slots[i].z, b.size.x, b.size.y, b.size.z, b.min.y, b.max.y));
            if (Mathf.Abs(b.size.x - 5.4f) > 0.002f || Mathf.Abs(b.size.z - 5.4f) > 0.002f)
                sb.AppendLine("    !! 灯槽世界尺寸不是 5.4×5.4 → 600 mm 格缝的前提塌了");
        }
        sb.AppendLine("    相邻实例间距 " + NeighbourGap(slots) + ";灯槽 5.4 m → 缝宽 = 间距 - 5.4");
        sb.AppendLine("    注:原 CeilTile/CeilGlow 带 Cube 碰撞体,灯槽不给碰撞体(玩家在头顶以下活动,不影响传送射线)");
        return 1;
    }

    static string NeighbourGap(List<Vector3> slots)
    {
        return BestGap(slots).ToString("0.000") + " m";
    }

    static float NeighbourGap(List<float> xs, List<float> zs)
    {
        var pts = new List<Vector3>();
        for (int i = 0; i < xs.Count && i < zs.Count; i++) pts.Add(new Vector3(xs[i], 0f, zs[i]));
        return BestGap(pts);
    }

    /// <summary>Distinct 点对里的最小 XZ 间距(共格的 tile/glow 配对应忽略,所以排除 0)。</summary>
    static float BestGap(List<Vector3> pts)
    {
        float best = 999f;
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                float d = Vector2.Distance(new Vector2(pts[i].x, pts[i].z), new Vector2(pts[j].x, pts[j].z));
                if (d > 0.001f) best = Mathf.Min(best, d);
            }
        return best;
    }

    /// <summary>
    /// 通用一步:在 parent 下按白名单删掉旧 Cube 零件,放一个 FBX 实例,挂材质、补碰撞体。
    /// 白名单外一律不删:命中 keep 前缀的按"保留"记账,其余报 !! —— 手摆过的东西不能被工具顺手清掉。
    /// 返回值 = 新模型的顶面世界 y(失败 -1),调用方用它算配件要降多少。
    /// </summary>
    static float Place(Transform parent, PropSpec p, string[] removable, string meshName, StringBuilder sb)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.model);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(p.matPath);
        if (prefab == null || mat == null)
        {
            sb.AppendLine("!! " + p.tag + " 缺资产(模型=" + (prefab == null ? "无" : "有")
                + " 材质=" + (mat == null ? "无" : "有") + ")→ 先跑 VRLibrary/导入室内构件(贴图+材质)");
            return -1f;
        }

        var names = new List<string>();
        for (int i = 0; i < parent.childCount; i++) names.Add(parent.GetChild(i).name);
        sb.AppendLine("  " + p.tag + " " + parent.name + " 现有子物体 [" + string.Join(" ", names) + "]");

        int removed = 0, kept = 0, blocked = 0;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var ch = parent.GetChild(i);
            if (System.Array.IndexOf(removable, ch.name) < 0)
            {
                // 碰撞体子物体由本工具自己管(下面会先销毁再重建),不能算进"清单外"
                if (ch.name.EndsWith("Collider")) continue;
                // 同理,meshName 是本工具自己放的:第二次重跑时它马上会被"覆盖上一次放的"那句销毁
                if (ch.name == meshName) continue;
                if (StartsWithAny(ch.name, p.keep) || IsRide(ch.name, p.rides))
                {
                    kept++;
                    sb.AppendLine("    保留 " + ch.name + "(y " + ch.position.y.ToString("0.000")
                        + ",本就不是 Cube 零件)"
                        + (IsRide(ch.name, p.rides) ? " → 稍后随新顶面一起平移" : ""));
                    continue;
                }
                blocked++;
                sb.AppendLine("    !! 白名单和保留清单都没有 " + ch.name + " —— 没删,自己看一眼");
                continue;
            }
            Undo.DestroyObjectImmediate(ch.gameObject);
            removed++;
        }

        var old = parent.Find(meshName);
        if (old != null) { Undo.DestroyObjectImmediate(old.gameObject); sb.AppendLine("    覆盖上一次放的 " + meshName); }

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(inst, "换上 " + p.tag);
        inst.name = meshName;
        inst.transform.SetParent(parent, false);
        inst.transform.localPosition = new Vector3(0f, p.yOffset, 0f);
        // 交付说明 §3:Unity=(BlenderX, BlenderZ, -BlenderY) = 绕 X 转 -90°,和地毯那次同一件事。
        // 导入器有没有替我们做,由 ReportPose 实测说了算,不靠假设。
        inst.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        // 转 -90°X 之后 local Z 才是世界竖直轴(local Z→world Y),所以"只缩高度"要缩 Z;
        // 缩 Y 会去缩世界深度(local Y→world -Z),桌子会变扁而不是变矮。
        inst.transform.localScale = new Vector3(1f, 1f, p.yScale);

        var r = inst.GetComponentInChildren<MeshRenderer>();
        if (r == null) { sb.AppendLine("    !! 实例里没有 MeshRenderer"); return -1f; }
        r.sharedMaterial = mat;
        r.shadowCastingMode = p.tag == Coffer.tag ? ShadowCastingMode.Off : ShadowCastingMode.On;
        foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;

        // 旧家具是 CreatePrimitive 出来的,自带碰撞体(生成代码从没摘过)。FBX 没有 → 必须补一个,
        // 否则换完模型玩家会直接穿桌而过。
        // 碰撞体不能挂在实例上:实例带着 -90° 旋转和非 1 缩放,BoxCollider.size 是**模型局部单位**,
        // 拿世界包围盒的米数去填会被旋转/缩放二次改写。另起一个 identity 姿态的兄弟节点,
        // size 就等于世界 AABB,一眼能核。
        var oldCol = parent.Find(meshName + "Collider");
        if (oldCol != null) Object.DestroyImmediate(oldCol.gameObject);
        var b = r.bounds;
        // 服务台只围下半身:上半是空心的屏框,原来那里也没有任何东西挡人
        float h = p.tag == Kiosk.tag ? Mathf.Min(b.size.y, 1.1f) : b.size.y;
        var colGo = new GameObject(meshName + "Collider");
        Undo.RegisterCreatedObjectUndo(colGo, "换上 " + p.tag + " 碰撞体");
        colGo.transform.SetParent(parent, false);
        colGo.transform.localPosition = new Vector3(0f, p.yOffset + h * 0.5f, 0f);
        colGo.transform.localRotation = Quaternion.identity;
        colGo.transform.localScale = Vector3.one;
        colGo.isStatic = true;
        var box = colGo.AddComponent<BoxCollider>();
        box.size = new Vector3(b.size.x, h, b.size.z);
        if (p.tag == Kiosk.tag)
            sb.AppendLine("    碰撞体只包到 " + h.ToString("0.000") + " m 高(屏框那段是空心的,原来也没挡)");

        sb.AppendLine(string.Format("    {0} 落点局部({1:0.000},{2:0.000},{3:0.000}) 世界 {4:0.000}×{5:0.000}×{6:0.000}"
            + " y {7:0.000}~{8:0.000} 顶面 {9:0.000} 材质={10} 缩高度轴 {11:0.0000} 清掉旧零件 {12} 个 保留 {13} 个 清单外未删 {14} 个",
            meshName, inst.transform.localPosition.x, inst.transform.localPosition.y, inst.transform.localPosition.z,
            b.size.x, b.size.y, b.size.z, b.min.y, b.max.y, b.max.y, mat.name, p.yScale, removed, kept, blocked));
        return b.max.y;
    }

    // ---------------------------------------------------- 3b. 生成器用的公开入口(共用上面那份算法)

    /// <summary>
    /// 每引入一个 FBX 都要付一次"同步生成代码"的税:BuildAll 会从代码重造整个场景,
    /// 生成器若只造 Cube,重建一次就把模型打回原形。所以生成器调这里,落位算法全场只有一份。
    /// 资产还没导入(新克隆的仓库、没跑导入菜单)时返回 false,调用方保留 Cube 占位 —— 整链不断。
    /// </summary>
    public static bool PlaceGenerated(Transform parent, PropSpec p, string[] removable, string meshName, List<string> notes)
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(p.model) == null
            || AssetDatabase.LoadAssetAtPath<Material>(p.matPath) == null)
        {
            notes.Add("  " + p.tag + " 的 FBX/材质尚未导入 → 保留 Cube 占位"
                + "(先跑菜单 VRLibrary/导入室内构件(贴图+材质) 再重跑本工具)");
            return false;
        }
        var sb = new StringBuilder();
        float top = Place(parent, p, removable, meshName, sb);
        Forward(sb, notes);
        if (top < 0f) notes.Add("  !! " + p.tag + " 放置失败,见上一行");
        return top >= 0f;
    }

    /// <summary>生成器用的落座屏幕:换完服务台模型后,两块世界空间 Canvas 要对进新屏框。</summary>
    public static void SeatCanvasesGenerated(Transform kiosk, List<string> notes)
    {
        var sb = new StringBuilder();
        SeatCanvases(kiosk, sb);
        Forward(sb, notes);
    }

    static void Forward(StringBuilder sb, List<string> notes)
    {
        foreach (var line in sb.ToString().Split('\n'))
        {
            var t = line.TrimEnd();
            if (t.Trim().Length > 0) notes.Add(t);
        }
    }

    static bool StartsWithAny(string name, string[] prefixes)
    {
        if (prefixes == null) return false;
        foreach (var s in prefixes)
            if (name.StartsWith(s)) return true;
        return false;
    }

    static string RidePrefix(string entry)
    {
        int i = entry.IndexOf('=');
        return i < 0 ? entry : entry.Substring(0, i);
    }

    /// <summary>rides 条目带 "=期望缝隙" 后缀,不能直接喂给 StartsWithAny(名字永远不匹配)。</summary>
    static bool IsRide(string name, string[] rides)
    {
        if (rides == null) return false;
        foreach (var r in rides)
            if (name.StartsWith(RidePrefix(r))) return true;
        return false;
    }

    static bool TryRideGap(string name, string[] rides, out float gap)
    {
        gap = 0f;
        foreach (var r in rides)
            if (name.StartsWith(RidePrefix(r)))
                return float.TryParse(r.Substring(RidePrefix(r).Length + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out gap);
        return false;
    }

    // ------------------------------------------------------------ 4. 复核落盘

    [MenuItem("VRLibrary/室内构件复核落盘(重开场景量)")]
    public static void VerifySaved()
    {
        var sb = NewReport("复核");
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        sb.AppendLine("  从磁盘重开 " + scene.path + " 未保存改动=" + scene.isDirty);
        var root = RequireRoot(sb);
        if (root == null) { Write(sb); Debug.Log(sb.ToString()); return; }

        var zone = root.transform.Find("ReadingZone");
        if (zone != null)
            foreach (Transform t in zone)
            {
                var tm = t.Find("TableMesh");
                ReportInstance(sb, t.name + "/TableMesh", tm, Table);
                var sm = tm == null ? null : tm.parent.Find("Stool/StoolMesh");
                ReportInstance(sb, t.name + "/Stool/StoolMesh", sm, Stool);
                if (tm != null) ReportRidden(t, tm, sb);
            }

        var kiosk = root.transform.Find("CentralKiosk");
        ReportInstance(sb, "CentralKiosk/KioskMesh", kiosk == null ? null : kiosk.Find("KioskMesh"), Kiosk);
        if (kiosk != null)
        {
            sb.AppendLine("    组件:" + string.Join(", ", System.Array.ConvertAll(kiosk.GetComponents<Component>(),
                c => c == null ? "missing!" : c.GetType().Name)));
            sb.AppendLine("    子物体[" + string.Join(" ", ChildNames(kiosk)) + "]");
            ReportSeated(kiosk, sb);
        }

        var ceil = root.transform.Find("Ceiling");
        if (ceil != null)
        {
            int n = 0; float minX = 999f, maxX = -999f, minY = 999f, maxY = -999f;
            foreach (Transform c in ceil)
            {
                if (!c.name.StartsWith("Coffer_")) continue;
                var r = c.GetComponentInChildren<MeshRenderer>(); if (r == null) continue;
                n++;
                minX = Mathf.Min(minX, r.bounds.min.x); maxX = Mathf.Max(maxX, r.bounds.max.x);
                minY = Mathf.Min(minY, r.bounds.min.y); maxY = Mathf.Max(maxY, r.bounds.max.y);
                if (r.sharedMaterial == null || r.sharedMaterial.name != "M_CeilingCoffer")
                    sb.AppendLine("    !! " + c.name + " 材质没挂上");
                if (c.GetComponentInChildren<Collider>(true) != null)
                    sb.AppendLine("    !! " + c.name + " 还带碰撞体");
            }
            sb.AppendLine(string.Format("  吊顶 Coffer_ 实例 {0} 个 覆盖 x {1:0.00}~{2:0.00} y {3:0.000}~{4:0.000}",
                n, minX, maxX, minY, maxY));
            if (n < 30) sb.AppendLine("    !! 实例只有 " + n + " 个,不像 6×6=36 → 检查 Ceiling 下的命名");
        }
        Write(sb);
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// 配件有没有真的跟着桌面降下来:拿它与新台面顶的缝隙比 rides 里登记的**换装前实测**缝隙。
    /// 不用"离台面 >2cm 就是悬空" —— 灯头本来就悬在灯杆顶(设计缝 +0.100),那样会误报;
    /// 真正要抓的是缝隙变大了 ≈0.188 m(整桌降幅),那就是这个配件漏跟。
    /// </summary>
    static void ReportRidden(Transform table, Transform tableMesh, StringBuilder sb)
    {
        var r = tableMesh.GetComponentInChildren<MeshRenderer>();
        float top = r == null ? float.NaN : r.bounds.max.y;
        foreach (var ch in DirectChildren(table))
        {
            if (!IsRide(ch.name, Table.rides)) continue;
            float bot = BottomOf(ch);
            float gap;
            bool known = TryRideGap(ch.name, Table.rides, out gap);
            float dev = bot - top - gap;
            sb.AppendLine(string.Format("    配件 {0,-14} 底 {1:0.000} 顶 {2:0.000} 距新台面 {3:+0.000;-0.000;0} m"
                + " 设计缝 {4:0.000} → 偏 {5:+0.000;-0.000;0} {6}",
                ch.name, bot, BoundsTop(ch), bot - top, known ? gap : float.NaN, known ? dev : float.NaN,
                float.IsNaN(bot) ? "(无渲染器,只报位移)"
                    : !known ? "(未登记设计缝,不判定)"
                    : (Mathf.Abs(dev) > 0.025f
                        ? "!! 缝隙比换装前" + (dev > 0 ? "大了" : "小了") + " " + Mathf.Abs(dev).ToString("0.000")
                          + " m → 这个配件没跟着台面挪"
                        : "→ 缝隙与换装前一致(跟着台面挪过了)")));
        }
    }

    static float BottomOf(Transform t)
    {
        var r = t.GetComponentInChildren<MeshRenderer>();
        return r == null ? float.NaN : r.bounds.min.y;
    }

    /// <summary>
    /// 两块屏有没有对进屏框。Canvas 的内容既不是 Renderer 也不是 Collider(UGUI 走 Graphic),
    /// 所以世界尺寸只能取 RectTransform.rect × 自身 localScale —— 这恰好就是 SeatCanvases 用的算法。
    /// </summary>
    static void ReportSeated(Transform kiosk, StringBuilder sb)
    {
        foreach (var name in new[] { "InfoCanvas", "SearchPanel" })
        {
            var t = kiosk.Find(name);
            if (t == null) { sb.AppendLine("    !! " + name + " 不见了"); continue; }
            var rt = t.GetComponent<RectTransform>();
            float w = rt == null ? 0f : rt.rect.width * t.localScale.x;
            float h = rt == null ? 0f : rt.rect.height * t.localScale.y;
            float wantY = Kiosk.yOffset + 1.55f;
            bool onPlane = Mathf.Abs(t.position.y - wantY) < 0.02f && Mathf.Abs(t.position.z - 0.28f) < 0.03f;
            bool fits = w <= 1.221f && h <= 0.741f;
            sb.AppendLine(string.Format("    {0} 世界中心({1:0.000},{2:0.000},{3:0.000}) 俯 {4:0.0}° 缩 {5:0.00000}"
                + " 占 {6:0.00}×{7:0.00} m(框心 y {8:0.000},洞口 1.22×0.74){9}{10}",
                name, t.position.x, t.position.y, t.position.z, t.eulerAngles.x, t.localScale.x, w, h, wantY,
                onPlane ? " → 在屏框平面上" : " !! 没落在屏框平面上",
                fits ? "" : " !! 比洞口大,会被屏框边条切掉"));
        }
    }

    static void ReportInstance(StringBuilder sb, string label, Transform t, PropSpec p)
    {
        if (t == null) { sb.AppendLine("  !! 没有 " + label); return; }
        var r = t.GetComponentInChildren<MeshRenderer>();
        if (r == null) { sb.AppendLine("  !! " + label + " 没有 MeshRenderer"); return; }
        var b = r.bounds;
        var mf = r.GetComponent<MeshFilter>();
        var mesh = mf == null ? null : mf.sharedMesh;
        // 期望:Blender 的 (x,y,z) 经 -90°X 变 Unity 的 (x, z, y),再按 yScale 缩高度
        var want = new Vector3(p.blenderSize.x, p.blenderSize.z * p.yScale, p.blenderSize.y);
        float derr = Mathf.Max(Mathf.Abs(b.size.x - want.x), Mathf.Max(Mathf.Abs(b.size.y - want.y), Mathf.Abs(b.size.z - want.z)));
        sb.AppendLine(string.Format("  {0,-26} 三角 {1} 世界 {2:0.000}×{3:0.000}×{4:0.000} y {5:0.000}~{6:0.000}"
            + " 期望 {7:0.000}×{8:0.000}×{9:0.000} 偏差 {10:0.000} 本地旋转X {11:0.0}° 缩Z {12:0.0000} 静态={13} 材质={14}",
            label, mesh == null ? 0 : mesh.triangles.Length / 3,
            b.size.x, b.size.y, b.size.z, b.min.y, b.max.y, want.x, want.y, want.z, derr,
            t.localRotation.eulerAngles.x, t.localScale.z, t.gameObject.isStatic,
            r.sharedMaterial == null ? "null" : r.sharedMaterial.name));
        if (derr > 0.003f) sb.AppendLine("    !! 世界尺寸与「转 -90°X + 缩 Z」的预期差 " + (derr * 1000f).ToString("0.0") + " mm");
        // 碰撞体是实例的兄弟节点(世界 AABB 才好核对),所以要往父节点整棵子树找
        if (t.parent.GetComponentInChildren<Collider>(true) == null)
            sb.AppendLine("    !! 父节点下没有碰撞体 → 玩家会穿过它走");
    }

    static List<string> ChildNames(Transform t)
    {
        var l = new List<string>();
        for (int i = 0; i < t.childCount; i++) l.Add(t.GetChild(i).name);
        return l;
    }

    // ------------------------------------------------------------ 内部:资产侧

    static void CopyIn(StringBuilder sb)
    {
        if (!Directory.Exists(SrcTex) || !Directory.Exists(SrcModel))
        { sb.AppendLine("!! 找不到 Blender 源目录:" + SrcTex + " / " + SrcModel); return; }
        Directory.CreateDirectory(Dir);
        foreach (var m in Mats)
            foreach (var suffix in new[] { "BaseColor", "URPMask", "Normal", "Emissive" })
                Copy(Path.Combine(SrcTex, m.set + "_" + suffix + ".png"),
                     Dir + "/" + m.set + "_" + suffix + ".png", sb);
        foreach (var p in All) Copy(p.srcFbx, p.model, sb);
        AssetDatabase.Refresh();
    }

    static void Copy(string src, string dst, StringBuilder sb)
    {
        if (!File.Exists(src)) { sb.AppendLine("!! 缺文件 " + src); return; }
        if (File.Exists(dst) && new FileInfo(src).LastWriteTimeUtc <= new FileInfo(dst).LastWriteTimeUtc)
        { sb.AppendLine("  跳过(未变) " + Path.GetFileName(dst)); return; }
        File.Copy(src, dst, true);
        var meta = dst + ".meta";
        if (File.Exists(meta)) File.Delete(meta);
        sb.AppendLine("  拷入 " + Path.GetFileName(dst) + "  源时间 " + new FileInfo(src).LastWriteTime.ToString("MM-dd HH:mm:ss"));
    }

    static void SetupTex(string path, TextureImporterType type, bool srgb,
                         TextureImporterFormat android, int max, StringBuilder sb)
    {
        var im = AssetImporter.GetAtPath(path) as TextureImporter;
        if (im == null) { sb.AppendLine("!! 没有导入器 " + path); return; }
        im.textureType = type;
        im.sRGBTexture = srgb;
        im.wrapMode = TextureWrapMode.Clamp;   // 每块模型各一套图集(手工分区),Repeat 会把对面岛的颜色抄过来
        im.mipmapEnabled = true;
        im.filterMode = FilterMode.Trilinear;
        im.anisoLevel = 8;
        im.maxTextureSize = max;
        im.streamingMipmaps = false;
        im.SetPlatformTextureSettings(new TextureImporterPlatformSettings
        {
            name = "Android", overridden = true, maxTextureSize = max,
            format = android, compressionQuality = 1,
        });
        im.SaveAndReimport();
        sb.AppendLine("  " + Path.GetFileName(path) + " → " + type + (srgb ? " sRGB" : " 线性")
            + " Clamp/Trilinear/aniso8 上限" + max + " Android=" + android);
    }

    static void SetupModel(PropSpec p, StringBuilder sb)
    {
        var im = AssetImporter.GetAtPath(p.model) as ModelImporter;
        if (im == null) { sb.AppendLine("!! 没有模型导入器 " + p.model); return; }
        im.materialImportMode = ModelImporterMaterialImportMode.None;
        im.useFileScale = true;
        im.globalScale = 1f;
        im.importBlendShapes = false;
        im.generateSecondaryUV = false;
        im.SaveAndReimport();
        sb.AppendLine("  " + Path.GetFileName(p.model) + " → materialImportMode=None, 1 unit=1 m");
    }

    static void BuildMaterials(StringBuilder sb)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) { sb.AppendLine("!! 找不到 URP/Lit"); return; }
        foreach (var m in Mats)
        {
            var basec = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + m.set + "_BaseColor.png");
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + m.set + "_URPMask.png");
            var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + m.set + "_Normal.png");
            var emis = AssetDatabase.LoadAssetAtPath<Texture2D>(Dir + "/" + m.set + "_Emissive.png");
            if (basec == null || mask == null || nrm == null || emis == null)
            { sb.AppendLine("!! " + m.name + " 有贴图没导入成功,先不建"); continue; }
            MakeMat(m, shader, basec, mask, nrm, emis, sb);
        }
    }

    static void MakeMat(MatSpec m, Shader shader, Texture2D basec, Texture2D mask,
                        Texture2D nrm, Texture2D emis, StringBuilder sb)
    {
        var old = AssetDatabase.LoadAssetAtPath<Material>(m.path);
        var mat = old ?? new Material(shader);
        mat.name = m.name;
        mat.shader = shader;
        mat.SetTexture("_BaseMap", basec);
        mat.SetTexture("_MetallicGlossMap", mask);
        mat.SetTexture("_OcclusionMap", mask);
        mat.SetTexture("_BumpMap", nrm);
        mat.SetTexture("_EmissionMap", emis);
        mat.SetFloat("_Metallic", 1f);
        mat.SetFloat("_Smoothness", 1f);
        mat.SetFloat("_OcclusionStrength", 1f);
        mat.SetFloat("_BumpScale", 1f);
        mat.SetFloat("_Surface", 0f);

        // 发光亮度不写死:让"贴图里那条岛"的最终出射亮度和场上同族霓虹一致。
        // 贴图存的是 sRGB 定案色,采样后会被解码成线性(亮度掉一半以上),
        // 而 M_NeonCyan 那批的 _EmissionColor 是灯色工具按亮度补偿过的线性值 ——
        // 直接给白色会让新灯带比全场暗一档。
        // 倍率取**单一标量** luma(参考)/luma(岛),不逐通道除:
        // 逐通道会把贴图噪声按通道放大数倍(岛色 R 只有 0.19 → 放大 5.4 次),
        // 而且等于用参考材质的色相改写作者定案的色相。本工程灯色一贯是"只补亮度不动色相"。
        Color refE = RefEmission(m, sb);
        Color island = IslandColor(Dir + "/" + m.set + "_Emissive.png", m.fallback, sb);
        float iL = Luma(island), rL = Luma(refE);
        float k = rL / Mathf.Max(iL, 1e-4f);
        var mul = new Color(k, k, k, 1f);
        mat.SetColor("_EmissionColor", mul);
        mat.EnableKeyword("_EMISSION");
        // 坑 22:光 EnableKeyword 活不过重载 —— Material 在 AwakeFromLoad 里按
        // globalIlluminationFlags 重算 _EMISSION,标记是 EmissiveIsBlack 就被摘掉。
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

        mat.EnableKeyword("_METALLICSPECGLOSSMAP");
        mat.EnableKeyword("_OCCLUSIONMAP");
        mat.EnableKeyword("_NORMALMAP");
        mat.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
        mat.DisableKeyword("_SPECULAR_SETUP");
        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHATEST_ON");

        if (old == null) AssetDatabase.CreateAsset(mat, m.path);
        else EditorUtility.SetDirty(mat);
        sb.AppendLine(string.Format("  {0,-20} {1} 参考色={2} 岛色={3} → _EmissionColor={4} GI={5}",
            m.name, m.tag, Fmt(refE), Fmt(island), Fmt(mul), mat.globalIlluminationFlags));
    }

    static Color RefEmission(MatSpec m, StringBuilder sb)
    {
        var r = AssetDatabase.LoadAssetAtPath<Material>(m.refMat);
        if (r == null || !r.HasProperty("_EmissionColor"))
        {
            sb.AppendLine("  !! " + m.refMat + " 读不到发光色," + m.name + " 退回工单字面色(会比全场其它霓虹暗一档)");
            return m.fallback;
        }
        return r.GetColor("_EmissionColor");
    }

    /// <summary>
    /// 量 Emissive 图里"非零那一小片"的平均颜色(线性空间)。
    /// 用 LoadImage 读回一份临时可读贴图,不去改导入器的 isReadable —— 那会为了量一个数重导入一次。
    /// </summary>
    static Color IslandColor(string pngPath, Color fallback, StringBuilder sb)
    {
        if (!File.Exists(pngPath)) return fallback;
        var tmp = new Texture2D(2, 2);
        try
        {
            tmp.LoadImage(File.ReadAllBytes(pngPath));
            var px = tmp.GetPixels32();
            float r = 0, g = 0, b = 0, n = 0, maxLum = 0;
            foreach (var p in px)
            {
                float lr = ToLinear(p.r), lg = ToLinear(p.g), lb = ToLinear(p.b);
                float lum = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                if (lum <= 0.0005f) continue;
                r += lr; g += lg; b += lb; n++;
                maxLum = Mathf.Max(maxLum, lum);
            }
            if (n < 1) { sb.AppendLine("  !! " + Path.GetFileName(pngPath) + " 整张是黑的 → 接上也不会发光"); return fallback; }
            sb.AppendLine(string.Format("    {0} 亮像素 {1:0.00}% 岛均色(线性)({2:0.000},{3:0.000},{4:0.000}) 最亮亮度 {5:0.000}",
                Path.GetFileName(pngPath), 100f * n / px.Length, r / n, g / n, b / n, maxLum));
            return new Color(r / n, g / n, b / n, 1f);
        }
        finally { Object.DestroyImmediate(tmp); }
    }

    /// <summary>Rec.709 亮度权重,与 LightColorSwap 用的是同一套。</summary>
    static float Luma(Color c) { return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; }

    static float ToLinear(byte v)
    {
        float c = v / 255f;
        return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>
    /// 量一块毯的"能踩的那一面"顶高,和 PropsSpec 里写死的 yOffset 对一次。
    /// 毯总厚 13 mm = 毯体 8 mm + 包边再高 5 mm(CarpetSetup 的 spec),
    /// 而世界包围盒的 max.y 是包边顶,家具站在毯体上所以参照要用 min.y + 0.008。
    /// </summary>
    static void ReportSurface(StringBuilder sb, string path, string who, float yOffset)
    {
        var root = GameObject.Find("SmartLibrary");
        var t = root == null ? null : root.transform.Find(path);
        var r = t == null ? null : t.GetComponent<MeshRenderer>();
        if (r == null) { sb.AppendLine("    !! 读不到 " + path + " 的渲染器 → " + who + " 的落点高度无从对照"); return; }
        float bodyTop = r.bounds.min.y + 0.008f;
        sb.AppendLine(string.Format("    {0} 毯体顶 {1:0.000} / 包边顶 {2:0.000}({3} 用 yOffset {4:0.000},差 {5:0.000})",
            path, bodyTop, r.bounds.max.y, who, yOffset, yOffset - bodyTop));
        if (Mathf.Abs(yOffset - bodyTop) > 0.003f)
            sb.AppendLine("      !! " + who + " 的 yOffset 与实测毯面差 "
                + ((yOffset - bodyTop) * 1000f).ToString("0") + " mm → 陷进去或悬空,改 yOffset 或改毯的高度");
    }

    // ------------------------------------------------------------ 内部:量资产

    static void ReportAsset(PropSpec p, StringBuilder sb)
    {
        var objs = AssetDatabase.LoadAllAssetsAtPath(p.model);
        Mesh mesh = null;
        if (objs != null)
            foreach (var o in objs)
            {
                var mf = o as Mesh;
                if (mf != null && mf.vertexCount > 0) mesh = mf;
            }
        if (mesh == null) { sb.AppendLine("!! " + p.tag + " 里读不到网格 " + p.model); return; }
        var b = mesh.bounds;
        var uv = mesh.uv;
        float uMin = 9f, uMax = -9f, vMin = 9f, vMax = -9f;
        foreach (var t in uv) { uMin = Mathf.Min(uMin, t.x); uMax = Mathf.Max(uMax, t.x); vMin = Mathf.Min(vMin, t.y); vMax = Mathf.Max(vMax, t.y); }
        sb.AppendLine(string.Format("  {0} 网格 {1}: Blender 包围盒 {2:0.0000}×{3:0.0000}×{4:0.0000}(交付说 {5:0.000}×{6:0.000}×{7:0.000})"
            + " 三角 {8} submesh {9} 顶点 {10} UV u[{11:0.000}~{12:0.000}] v[{13:0.000}~{14:0.000}]",
            p.tag, mesh.name, b.size.x, b.size.y, b.size.z,
            p.blenderSize.x, p.blenderSize.y, p.blenderSize.z,
            mesh.triangles.Length / 3, mesh.subMeshCount, mesh.vertexCount, uMin, uMax, vMin, vMax));
        if (Vector3.Distance(b.size, p.blenderSize) > 0.002f)
            sb.AppendLine("     !! 包围盒与交付说明 §2 的实测值不符 → 重导过或单位映射变了");
        if (Mathf.Abs(b.min.z) > 0.0005f)
            sb.AppendLine("     !! 底面不在原点(min.z=" + b.min.z.ToString("0.0000") + ")→ y 落点要按 min 反推");
        if (mesh.subMeshCount != 1)
            sb.AppendLine("     !! submesh 不是 1(工单说单槽)→ 材质要挂几份?自己看一眼");
        if (uMax > 1.02f || uMin < -0.02f || vMax > 1.02f || vMin < -0.02f)
            sb.AppendLine("     !! UV 超出 0..1 → 图集是 Clamp 的,越界会拉到对侧颜色(或那本来就是重复 UV)");
        ReportPose(p, sb);
    }

    /// <summary>
    /// 实例化后量世界尺寸:mesh.bounds 是 FBX 自己的 Z-up 坐标,导入器的 Z-up→Y-up
    /// 补偿(如果它做了)打在 Transform 上、不进顶点 —— 地毯那次就是这么看走眼的。
    /// </summary>
    static void ReportPose(PropSpec p, StringBuilder sb)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p.model);
        if (prefab == null) { sb.AppendLine("     !! 读不到 prefab " + p.model); return; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        try
        {
            go.transform.localScale = Vector3.one;
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            var r = go.GetComponentInChildren<MeshRenderer>();
            if (r == null) { sb.AppendLine("     !! 实例里没有 MeshRenderer"); return; }
            var raw = r.bounds;
            // 不能用"哪个轴最长"猜朝向:圆桌是 1.0×1.0×0.975,水平两轴比高度还长。
            // 拿 identity 姿态的世界尺寸和两种假设各比一次距离,谁近是谁。
            var h0 = new Vector3(p.blenderSize.x, p.blenderSize.y, p.blenderSize.z);  // 没做转换(Z-up 原样进来)
            var h1 = new Vector3(p.blenderSize.x, p.blenderSize.z, p.blenderSize.y);   // 已做转换(立在 Y 上)
            float d0 = Vector3.Distance(raw.size, h0), d1 = Vector3.Distance(raw.size, h1);
            bool converted = d1 < d0 - 0.001f;
            bool ambiguous = Mathf.Abs(d0 - d1) < 0.001f;
            sb.AppendLine(string.Format("     不旋转时世界 {0:0.0000}×{1:0.0000}×{2:0.0000} → 导入器{3}"
                + "(差 没做 {4:0.0000} / 已做 {5:0.0000})",
                raw.size.x, raw.size.y, raw.size.z,
                ambiguous ? "两种假设量不出区别" : (converted ? "已做 Z-up→Y-up" : "没做 Z-up→Y-up"), d0, d1));

            go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var b = r.bounds;
            var want = new Vector3(p.blenderSize.x, p.blenderSize.z, p.blenderSize.y);
            float derr = Mathf.Max(Mathf.Abs(b.size.x - want.x), Mathf.Max(Mathf.Abs(b.size.y - want.y), Mathf.Abs(b.size.z - want.z)));
            sb.AppendLine(string.Format("     转 -90°X 后 {0:0.0000}×{1:0.0000}×{2:0.0000} y {3:0.0000}~{4:0.0000}"
                + " 期望 {5:0.000}×{6:0.000}×{7:0.000} 偏差 {8:0.0000}",
                b.size.x, b.size.y, b.size.z, b.min.y, b.max.y, want.x, want.y, want.z, derr));
            if (converted)
                sb.AppendLine("     !! 资产进 Unity 已经是立着的,这里再转 -90° 就是重复转 → 摆放要改回 identity");
            if (derr > 0.002f)
                sb.AppendLine("     !! 转完尺寸仍对不上 → 轴向映射和交付说明 §3 不是一条,摆放得重推");
            if (b.min.y < -0.0005f)
                sb.AppendLine("     !! 转后底面仍在原点以下 → 落点 y 要按 min.y 反推");
        }
        finally { Object.DestroyImmediate(go); }
    }

    static void VerifyModels(StringBuilder sb)
    {
        sb.AppendLine("-- 四个模型的实测 --");
        foreach (var p in All) ReportAsset(p, sb);
    }

    // ------------------------------------------------- 5. 出光朝向实测(回答工单 C 的判断①)

    /// <summary>
    /// 灯槽的发光面到底朝哪边、有多高,决定"玩家能不能看到裸发光面"。
    /// 717×349 的 Scene 截图看不出来(整块天花板在截图里就是一团黑),所以改成量几何:
    /// 逐三角形用 UV 重心采样交付的 Emissive 图 → 亮区就是发光岛 → 按世界法线分朝下/朝上/侧向,
    /// 统计各自面积与 y 范围,再跟口沿 y 比。朝上面积远大于朝下 = 光打在天花板里,房间照不到。
    /// </summary>
    [MenuItem("VRLibrary/吊顶出光朝向(只报告)")]
    public static void ReportCofferLight()
    {
        var sb = NewReport("出光朝向");
        var root = RequireRoot(sb);
        if (root == null) { Write(sb); Debug.Log(sb.ToString()); return; }
        var coffer = Find(root.transform, "Coffer_0");
        var r = coffer == null ? null : coffer.GetComponent<MeshRenderer>();
        var mf = r == null ? null : r.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            sb.AppendLine("!! 找不到 SmartLibrary/Ceiling/Coffer_0 的网格 → 先跑换装菜单");
            Write(sb); Debug.Log(sb.ToString()); return;
        }
        var png = Dir + "/" + Coffer.texSet + "_Emissive.png";
        if (!File.Exists(png))
        {
            sb.AppendLine("!! 读不到 " + png + " → 先跑导入菜单");
            Write(sb); Debug.Log(sb.ToString()); return;
        }

        // Scene 视图从上看得到吊顶、从下看只剩天空盒 —— 先把"该不该显示"和"到底铺在哪"量出来
        sb.AppendLine("  -- 可见性/覆盖探针 --");
        var mat0 = r.sharedMaterial;
        sb.AppendLine("    Coffer_0 activeSelf=" + coffer.gameObject.activeSelf + " activeInHierarchy=" + coffer.gameObject.activeInHierarchy
            + " renderer.enabled=" + r.enabled + " 材质=" + (mat0 == null ? "无" : mat0.name)
            + " _Cull=" + (mat0 == null ? "?" : mat0.HasProperty("_Cull") ? mat0.GetFloat("_Cull").ToString("0") : "无此属性")
            + " 投影=" + r.shadowCastingMode + " 层=" + coffer.gameObject.layer + "/" + LayerMask.LayerToName(coffer.gameObject.layer)
            + " 光照探针=" + r.lightProbeUsage);
        var cols = new[] { 0f, 1.2f, 4f, 13f, -15f };
        var cover = new int[cols.Length];
        foreach (Transform t in coffer.parent)
        {
            var rr = t.GetComponent<MeshRenderer>();
            if (rr == null) { sb.AppendLine("    " + t.name + " 没有 MeshRenderer"); continue; }
            var b = rr.bounds;
            sb.AppendLine(string.Format("    {0,-10} 中心({1:0.00},{2:0.00}) 尺寸{3:0.00}×{4:00} y{5:0.000}~{6:0.000}{7}{8}",
                t.name, b.center.x, b.center.z, b.size.x, b.size.z, b.min.y, b.max.y,
                rr.enabled ? "" : " [渲染器关]", t.gameObject.activeInHierarchy ? "" : " [未激活]"));
            for (int k = 0; k < cols.Length; k++)
                if (b.min.x <= cols[k] && cols[k] <= b.max.x && b.min.z <= cols[k] && cols[k] <= b.max.z) cover[k]++;
        }
        for (int k = 0; k < cols.Length; k++)
            sb.AppendLine("    柱位 (" + cols[k].ToString("0.00") + ", " + cols[k].ToString("0.00") + ") 被 " + cover[k] + " 块吊顶覆盖");

        // 仰视只剩一团深色渐变,而吊顶明明 activeSelf/renderer.enabled 都是 True → 查是谁挡在它下面
        var probe = new Vector3(1.2f, 5.45f, 1.2f);
        sb.AppendLine("    探针点 " + probe.ToString("F2") + " 处的渲染器(仰视时挡在玩家和灯槽之间的东西都在这):");
        foreach (var rr in root.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!rr.bounds.Contains(probe)) continue;
            var b = rr.bounds;
            var m = rr.sharedMaterial;
            sb.AppendLine(string.Format("      {0,-28} {1} 尺寸{2} y{3:0.000}~{4:0.000} {5}{6}",
                rr.transform.parent == null ? rr.name : rr.transform.parent.name + "/" + rr.name,
                rr.name, b.size.ToString("F2"), b.min.y, b.max.y,
                m == null ? "无材质" : m.name, rr.enabled ? "" : " [关]"));
        }

        var tmp = new Texture2D(2, 2);
        tmp.LoadImage(File.ReadAllBytes(png));
        int tw = tmp.width, th = tmp.height;
        var px = tmp.GetPixels32();
        Object.DestroyImmediate(tmp);

        var mesh = mf.sharedMesh;
        var v = mesh.vertices; var uv = mesh.uv; var nm = mesh.normals; var tri = mesh.triangles;
        if (uv.Length != v.Length) { sb.AppendLine("!! 顶点数 " + v.Length + " 与 UV 数 " + uv.Length + " 不等,没法按 UV 分类"); Write(sb); Debug.Log(sb.ToString()); return; }
        var M = r.transform.localToWorldMatrix;
        var NR = r.transform.localToWorldMatrix;   // 只用旋转部分做法线变换(缩放是 1,不必求逆转置)

        float areaDown = 0f, areaUp = 0f, areaSide = 0f;
        int nDown = 0, nUp = 0, nSide = 0;
        float downMinY = 99f, downMaxY = -99f, upMinY = 99f, upMaxY = -99f;
        float islandTotal = 0f, meshTotal = 0f;
        // 判断遮挡真正要看的是"口沿(非发光的朝下面)比发光面低多少":
        // 两者同高 = 齐平的一块发光板,槽壁再深也挡不住任何视线。
        int frDown = 0;
        float frDownMinY = 99f, frDownMaxY = -99f, frDownArea = 0f;
        float fxMin = 99f, fxMax = -99f, fzMin = 99f, fzMax = -99f;    // 口沿朝下面的平面范围
        float ixMin = 99f, ixMax = -99f, izMin = 99f, izMax = -99f;    // 发光面朝下面的平面范围

        var bounds = r.bounds;
        var cx0 = bounds.center.x; var cz0 = bounds.center.z;
        var dump = new List<string>();

        for (int i = 0; i + 2 < tri.Length; i += 3)
        {
            int i0 = tri[i], i1 = tri[i + 1], i2 = tri[i + 2];
            var a = M.MultiplyPoint(v[i0]); var b = M.MultiplyPoint(v[i1]); var c = M.MultiplyPoint(v[i2]);
            var cross = Vector3.Cross(b - a, c - a);
            float area = cross.magnitude * 0.5f;
            meshTotal += area;
            var n = NR.MultiplyVector(nm.Length == v.Length ? nm[i0] : cross.normalized);
            float u = (uv[i0].x + uv[i1].x + uv[i2].x) / 3f;
            float yy = (uv[i0].y + uv[i1].y + uv[i2].y) / 3f;
            int xi = Mathf.Clamp(Mathf.FloorToInt(u * tw), 0, tw - 1);
            int yi = Mathf.Clamp(Mathf.FloorToInt(yy * th), 0, th - 1);
            var p = px[yi * tw + xi];
            float lum = 0.2126f * ToLinear(p.r) + 0.7152f * ToLinear(p.g) + 0.0722f * ToLinear(p.b);
            dump.Add(string.Format("    #{0:00} {1} {2,-9} {3:0.000} m²  y {4:0.000}~{5:0.000}  中心({6:0.00}, {7:0.00})",
                i / 3, lum > 0.0005f ? "发光" : "暗",
                n.y < -0.9f ? "朝下" : n.y > 0.9f ? "朝上" : "斜 n.y" + n.y.ToString("0.00"),
                area,
                Mathf.Min(a.y, b.y, c.y), Mathf.Max(a.y, b.y, c.y),
                (a.x + b.x + c.x) / 3f - cx0, (a.z + b.z + c.z) / 3f - cz0));
            if (lum <= 0.0005f)                        // 暗区:不是发光岛
            {
                // 但其中"朝下"的那些正是口沿/槽沿平面 —— 遮挡只能看它比发光面低多少
                if (n.y < -0.5f)
                {
                    frDown++; frDownArea += area;
                    frDownMinY = Mathf.Min(frDownMinY, a.y, b.y, c.y);
                    frDownMaxY = Mathf.Max(frDownMaxY, a.y, b.y, c.y);
                    foreach (var pt in new[] { a, b, c })
                    {
                        fxMin = Mathf.Min(fxMin, pt.x); fxMax = Mathf.Max(fxMax, pt.x);
                        fzMin = Mathf.Min(fzMin, pt.z); fzMax = Mathf.Max(fzMax, pt.z);
                    }
                }
                continue;
            }
            islandTotal += area;
            if (n.y < -0.5f)
            {
                nDown++; areaDown += area;
                downMinY = Mathf.Min(downMinY, a.y, b.y, c.y); downMaxY = Mathf.Max(downMaxY, a.y, b.y, c.y);
                foreach (var pt in new[] { a, b, c })
                {
                    ixMin = Mathf.Min(ixMin, pt.x); ixMax = Mathf.Max(ixMax, pt.x);
                    izMin = Mathf.Min(izMin, pt.z); izMax = Mathf.Max(izMax, pt.z);
                }
            }
            else if (n.y > 0.5f)
            {
                nUp++; areaUp += area;
                upMinY = Mathf.Min(upMinY, a.y, b.y, c.y); upMaxY = Mathf.Max(upMaxY, a.y, b.y, c.y);
            }
            else nSide++;
        }

        sb.AppendLine("  贴图 " + tw + "×" + th + " 网格 " + mesh.triangles.Length / 3 + " 三角 / 世界 "
            + bounds.size.ToString("F3") + " y " + bounds.min.y.ToString("0.000") + "~" + bounds.max.y.ToString("0.000"));
        sb.AppendLine(string.Format("  发光岛面积 {0:0.000} m²(占整个模块 {1:0.0}%)", islandTotal,
            meshTotal > 0f ? islandTotal / meshTotal * 100f : 0f));
        sb.AppendLine(string.Format("    朝下(照进房间) {0} 面 {1:0.000} m² y {2:0.000}~{3:0.000}", nDown, areaDown,
            nDown == 0 ? 0f : downMinY, nDown == 0 ? 0f : downMaxY));
        sb.AppendLine(string.Format("    朝上(打进天花板) {0} 面 {1:0.000} m² y {2:0.000}~{3:0.000}", nUp, areaUp,
            nUp == 0 ? 0f : upMinY, nUp == 0 ? 0f : upMaxY));
        sb.AppendLine("    侧向(槽壁) " + nSide + " 面");
        sb.AppendLine("  -- 逐三角全展开(中心是相对模块中心的 x,z) --");
        foreach (var d in dump) sb.AppendLine(d);

        sb.AppendLine("  -- 遮挡实测(回答工单 C 判断①) --");
        sb.AppendLine(string.Format("    发光面朝下面 平面范围 x {0:0.00}~{1:0.00}  z {2:0.00}~{3:0.00}",
            ixMin, ixMax, izMin, izMax));
        sb.AppendLine(string.Format("    口沿(非发光朝下面) {0} 面 {1:0.000} m² y {2:0.000}~{3:0.000} 平面范围 x {4:0.00}~{5:0.00}  z {6:0.00}~{7:0.00}",
            frDown, frDownArea, frDown == 0 ? 0f : frDownMinY, frDown == 0 ? 0f : frDownMaxY,
            fxMin, fxMax, fzMin, fzMax));

        if (nDown == 0)
        {
            sb.AppendLine("  判定: !! 没有朝下的发光面 → 先修朝向,遮挡无从谈起");
        }
        else if (frDown == 0)
        {
            sb.AppendLine("  判定: !! 整个下表面全是发光面,连一道口沿都没有 → 360° 全裸,必须加内翻遮光檐");
        }
        else
        {
            float recess = downMinY - frDownMinY;          // 发光面比口沿高多少 = 凹进去多深
            float lip = Mathf.Min(Mathf.Min(fxMax - ixMax, ixMin - fxMin),
                                  Mathf.Min(fzMax - izMax, izMin - fzMin)); // 口沿比发光面宽出多少 = 唇宽
            sb.AppendLine(string.Format("    凹陷 d = {0:0.###} m   唇宽 h = {1:0.###} m", recess, lip));
            if (recess < 0.001f)
                sb.AppendLine("  判定: !! 发光面与口沿齐平(d≈0) → 槽壁挡不住任何视线,"
                    + "工单 C 说的\"偏离法线 35° 以上被挡住\"在 Unity 里不成立;正下方及平视都能看到裸发光面 → 要加内翻遮光檐");
            else if (lip <= 0f)
                sb.AppendLine("  判定: !! 发光面没有向内需缩(h≤0) → 口沿形同虚设,加檐或把发光面往里缩");
            else
            {
                float crit = Mathf.Atan(lip / recess) * Mathf.Rad2Deg;
                sb.AppendLine(string.Format(
                    "  判定: 视线偏离竖直 {0:0.0}° 以内看得到裸发光面,超过 {1:0.0}° 被近侧口沿挡住(工单 C 的 35° 说法{2})",
                    crit, crit, crit <= 40f ? "成立,玩家站着平视看不到" : "偏乐观,1.7 m 眼高在 3~5 m 外就落进可见区"));
            }
        }
        Write(sb);
        Debug.Log(sb.ToString());
    }

    // ------------------------------------------------- 6. 服务台屏的出字侧实测(回答"面板弄反了")

    /// <summary>
    /// "面板弄反了"在截图里只表现为"字是反的",但反的原因有三种,修法互相冲突:
    /// ① Canvas 自己转了 180°(fwd 左右反号,该换到台子另一侧读);
    /// ② 父链把朝向带偏(比如 FBX 实例自带 -90° X,fwd 会翻到朝上,看着像"反"其实是躺了);
    /// ③ 负缩放造成真镜像(fwd 正常但行列式为负,此时字仍朝着对的侧却左右互换,只有 Canvas 转 180 救不了)。
    /// 所以逐节把链量出来,别拿截图猜。
    /// </summary>
    [MenuItem("VRLibrary/服务台面板朝向(只报告)")]
    public static void ReportPanelFacing()
    {
        var sb = NewReport("面板朝向");
        var root = RequireRoot(sb);
        if (root == null) { Write(sb); Debug.Log(sb.ToString()); return; }
        var kiosk = Find(root.transform, "CentralKiosk");
        if (kiosk == null)
        {
            sb.AppendLine("!! 找不到 SmartLibrary 下的 CentralKiosk → 先跑「换上室内构件」");
            Write(sb); Debug.Log(sb.ToString()); return;
        }

        sb.AppendLine("  [台身链]");
        Chain(sb, kiosk);
        var kids = new List<string>();
        for (int i = 0; i < kiosk.childCount; i++) kids.Add(kiosk.GetChild(i).name);
        sb.AppendLine("    直接子物体 " + kiosk.childCount + " 个 [" + string.Join(" ", kids) + "]");
        var kr = kiosk.GetComponent<MeshRenderer>() ?? kiosk.GetComponentInChildren<MeshRenderer>(true);
        if (kr == null) sb.AppendLine("    !! 台身链上找不到 MeshRenderer → 模型没在 CentralKiosk 下");
        else
            sb.AppendLine(string.Format("    台身 {0}({1}) 包围 x {2:0.000}~{3:0.000}  y {4:0.000}~{5:0.000}  z {6:0.000}~{7:0.000}",
                NodePath(kr.transform), kr.enabled ? "渲染开" : "!!渲染关",
                kr.bounds.min.x, kr.bounds.max.x, kr.bounds.min.y, kr.bounds.max.y,
                kr.bounds.min.z, kr.bounds.max.z));
        ReportKioskFront(sb);

        // ReturnStatusCanvas 是同一类"用 forward 判方向"的世界空间屏(VRLibrarySetup.cs:440),顺带量
        foreach (var name in new[] { "InfoCanvas", "SearchPanel", "ReturnStatusCanvas" })
        {
            var t = kiosk.Find(name);
            if (t == null)
            {
                t = Find(root.transform, name);
                if (t == null) { sb.AppendLine("  -- " + name + " 整场景都没有(归还屏只在服务台链上)"); continue; }
                sb.AppendLine("  [屏] " + name + " 不在 CentralKiosk 直接子级,实际在 " + NodePath(t));
            }
            sb.AppendLine("  [屏] " + NodePath(t));
            Chain(sb, t);

            var f = t.forward;
            var rg = t.right;
            var up = t.up;
            float det = Vector3.Dot(Vector3.Cross(rg, up), f);
            var rt = t.GetComponent<RectTransform>();
            float w = rt == null ? 0f : rt.rect.width * t.localScale.x;
            float h = rt == null ? 0f : rt.rect.height * t.localScale.y;
            sb.AppendLine(string.Format("    世界欧拉{0} 缩放{1}", t.eulerAngles.ToString("F2"), t.lossyScale.ToString("F4")));
            sb.AppendLine(string.Format("    fwd{0}  right{1}  up{2}  行列式{3:0.000}→{4}",
                Fv(f), Fv(rg), Fv(up), det, det < 0f ? "!! 负缩放真镜像" : "手性正常"));
            sb.AppendLine(string.Format("    世界中心{0} 占 {1:0.00}×{2:0.00} m", t.position.ToString("F3"), w, h));
            string side = ReadSide(f);
            var read = -f;
            sb.AppendLine(string.Format("    判定: 正字只能从 {0} 一侧读(可读面 = -forward,UGUI 的 quad 法线朝 -Z);"
                + " 站对面看到的是背面透视过来的镜像字。", side));
            sb.AppendLine(string.Format("      与 +Z 站立点(0,1.3,2.2) 同侧? {0}   与 -Z? {1}",
                Vector3.Dot(read, new Vector3(0f, 0f, 2.2f) - t.position) > 0f ? "是" : "否",
                Vector3.Dot(read, new Vector3(0f, 0f, -2.2f) - t.position) > 0f ? "是" : "否"));

            var cv = t.GetComponent<Canvas>();
            if (cv != null)
                sb.AppendLine(string.Format("    Canvas {0} planeDistance {1:0.000} sortOrder {2} enabled {3} 自身激活 {4}",
                    cv.renderMode, cv.planeDistance, cv.sortingOrder, cv.enabled, t.gameObject.activeInHierarchy));
            int shown = 0;
            foreach (var tx in t.GetComponentsInChildren<UnityEngine.UI.Text>(true))
            {
                if (shown++ >= 4) break;
                sb.AppendLine(string.Format("      文字[{0}] \"{1}\" 激活 {2}", tx.name,
                    tx.text.Replace("\n", "\\n"), tx.gameObject.activeInHierarchy));
            }
            if (shown == 0) sb.AppendLine("      !! 这块屏下没有任何 UI.Text → 看到的是别的物件");
        }
        Write(sb);
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// 只重摆三块世界空间屏,不碰模型/桌椅/吊顶 —— 朝向这类问题要反复量,跑整条换装链的代价大得多。
    /// </summary>
    [MenuItem("VRLibrary/服务台与归还屏重新落座(只改屏)")]
    public static void ReseatPanels()
    {
        var sb = NewReport("屏重新落座");
        var root = RequireRoot(sb);
        var kiosk = root == null ? null : root.transform.Find("CentralKiosk");
        if (kiosk == null) sb.AppendLine("!! 找不到 SmartLibrary/CentralKiosk");
        else SeatCanvases(kiosk, sb);

        // 归还屏犯的是同一个错:Euler(25,0,0) 的可读面(-forward)朝 -Z,正对着后墙
        var ret = root == null ? null : Find(root.transform, "ReturnStatusCanvas");
        if (ret == null) sb.AppendLine("  -- 没有 ReturnStatusCanvas(本场景不带归还台?)");
        else
        {
            Undo.RecordObject(ret, "归还屏掰回 +Z");
            ret.localRotation = Quaternion.Euler(-25f, 180f, 0f);
            EditorUtility.SetDirty(ret);
            Canvas.ForceUpdateCanvases();
            sb.AppendLine(string.Format("  ReturnStatusCanvas 欧拉{0} → 正字侧 {1}",
                ret.localEulerAngles.ToString("F1"), ReadSide(ret)));
        }

        if (root != null)
        {
            EditorSceneManager.MarkSceneDirty(root.scene);
            sb.AppendLine("  已改并标脏 → 接着「保存场景」,再跑复核落盘才会读到新值");
        }
        Write(sb);
        Debug.Log(sb.ToString());
    }

    // ------------------------------------------------------------ 内部:场景侧

    static GameObject RequireRoot(StringBuilder sb)
    {
        var root = GameObject.Find("SmartLibrary");
        if (root == null) sb.AppendLine("!! 找不到根节点 SmartLibrary —— 请先打开 " + ScenePath);
        return root;
    }

    static void DumpChildren(StringBuilder sb, GameObject root, string tag, string childName)
    {
        var t = root.transform.Find(childName);
        if (t == null) { sb.AppendLine("!! 找不到 SmartLibrary/" + childName); return; }
        var names = new List<string>();
        for (int i = 0; i < t.childCount; i++) names.Add(t.GetChild(i).name);
        sb.AppendLine("  [" + tag + "] " + t.parent.name + "/" + t.name
            + " 世界 " + t.position.ToString("F3") + " 子物体 " + t.childCount + " 个 [" + string.Join(" ", names) + "]");
        if (tag == "吊顶") return;   // 72 个名字不必逐个展开,格网统计在后面
        foreach (Transform c in t) DumpNode(sb, "    ", c);
    }

    static void DumpNode(StringBuilder sb, string pad, Transform c)
    {
        var r = c.GetComponentInChildren<MeshRenderer>();
        var col = c.GetComponent<Collider>();
        sb.AppendLine(string.Format("{0}{1,-16} 世界{2} 局部{3} 缩放{4}{5}{6}",
            pad, c.name, c.position.ToString("F3"), c.localPosition.ToString("F3"), c.localScale.ToString("F3"),
            r == null ? "" : " 包围 " + r.bounds.size.ToString("F3") + " y" + r.bounds.min.y.ToString("0.000") + "~" + r.bounds.max.y.ToString("0.000"),
            col == null ? "" : " 碰撞=" + col.GetType().Name));
        foreach (Transform g in c) DumpNode(sb, pad + "  ", g);
    }

    static Transform Find(Transform root, string name)
    {
        foreach (Transform t in root)
        {
            if (t.name == name) return t;
            var hit = Find(t, name);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// 模型自己的正面落在 Unity 的哪一侧 —— 导入器没做 Z-up→Y-up,实例靠 Euler(-90,0,0) 扶起来,
    /// 所以资产局部 (x,y,z) 进 Unity 是 (x, z, -y):Blender 的 +y 面 = Unity 的 -Z 面。
    /// 靠两个只可能长在正面的特征定案:屏框下沿那道 ledge 的**朝顶面**,和法线沿 ±y 的竖直皮谁更外伸。
    /// </summary>
    static void ReportKioskFront(StringBuilder sb)
    {
        Mesh mesh = null;
        var objs = AssetDatabase.LoadAllAssetsAtPath(Kiosk.model);
        if (objs != null)
            foreach (var o in objs)
            {
                var m = o as Mesh;
                if (m != null && m.vertexCount > 0) mesh = m;
            }
        if (mesh == null) { sb.AppendLine("  !! 读不到 " + Kiosk.model + " 的网格 → 判不了模型正面"); return; }

        var v = mesh.vertices;
        var tri = mesh.triangles;
        sb.AppendLine("  [模型正面] 资产 " + mesh.name + " 局部包围 " + mesh.bounds.size.ToString("F3")
            + " 三角 " + (tri.Length / 3));
        for (int side = 0; side < 2; side++)
        {
            float sgn = side == 0 ? 1f : -1f;
            int up = 0, wall = 0;
            float upArea = 0f, outMost = 0f;
            float yMin = 9f, yMax = -9f, xMin = 9f, xMax = -9f, zMin = 9f, zMax = -9f;
            for (int i = 0; i < tri.Length; i += 3)
            {
                var a = v[tri[i]];
                var b = v[tri[i + 1]];
                var c = v[tri[i + 2]];
                var cen = (a + b + c) * 0.3333333f;
                var nn = Vector3.Cross(b - a, c - a);
                float area = nn.magnitude * 0.5f;
                if (area <= 1e-8f) continue;
                nn /= nn.magnitude;
                if (sgn * cen.y <= 0f) continue;
                if (nn.z > 0.9f && cen.z > 1.00f && cen.z < 1.28f)
                {
                    up++; upArea += area;
                    yMin = Mathf.Min(yMin, cen.y); yMax = Mathf.Max(yMax, cen.y);
                    xMin = Mathf.Min(xMin, cen.x); xMax = Mathf.Max(xMax, cen.x);
                    zMin = Mathf.Min(zMin, cen.z); zMax = Mathf.Max(zMax, cen.z);
                }
                if (sgn * nn.y > 0.9f)
                {
                    wall++;
                    outMost = Mathf.Max(outMost, sgn * cen.y);
                }
            }
            sb.AppendLine(string.Format("    Blender {0}y 侧: ledge 顶面 {1} 片 {2:0.000} m² y{3:0.000}~{4:0.000} x{5:0.000}~{6:0.000} z{7:0.000}~{8:0.000}"
                + " | 朝{0}y 竖直面 {9} 片,最外伸 y {10:0.000}",
                side == 0 ? "+" : "-", up, upArea,
                up == 0 ? 0f : yMin, up == 0 ? 0f : yMax, up == 0 ? 0f : xMin, up == 0 ? 0f : xMax,
                up == 0 ? 0f : zMin, up == 0 ? 0f : zMax, wall, outMost));
        }
        sb.AppendLine("    读法: 台身 1.35×1.35 见方,ledge 只会长在屏框那一面 → 哪一侧有 z 1.00~1.28 的朝上面,"
            + " 哪一侧就是 Blender 正面;正面 +y → Unity -Z,正面 -y → Unity +Z。");
    }

    static string NodePath(Transform t)
    {
        string s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    /// <summary>自顶向下打印每一节的局部量。朝向问题九成出在链上某一节的旋转或负缩放,链不看全就只能猜。</summary>
    static void Chain(StringBuilder sb, Transform leaf)
    {
        var chain = new List<Transform>();
        for (var t = leaf; t != null; t = t.parent) chain.Insert(0, t);
        for (int i = 0; i < chain.Count; i++)
        {
            var t = chain[i];
            string neg = "";
            if (chain[i].localScale.x < 0f || chain[i].localScale.y < 0f || chain[i].localScale.z < 0f)
                neg = " !! 负缩放";
            sb.AppendLine(string.Format("    {0}{1,-14} 局部{2} 欧拉{3} 缩{4}{5}",
                new string('.', i), t.name, t.localPosition.ToString("F3"),
                t.localEulerAngles.ToString("F1"), t.localScale.ToString("F4"), neg));
        }
    }

    static string Fv(Vector3 v)
    {
        return string.Format("({0:0.000},{1:0.000},{2:0.000})", v.x, v.y, v.z);
    }

    /// <summary>
    /// UGUI 世界空间 Canvas 的正字侧。**可读面 = -transform.forward**,不是 forward ——
    /// Canvas 默认旋转下要相机在 -Z 才读得到正字,quad 法线朝 -Z。
    /// 拿 forward 判方向会把"该转 180° Y"判成"不用转",这就是 v7.16 服务台面板弄反的根因。
    /// </summary>
    static string ReadSide(Vector3 fwd)
    {
        var v = -fwd;
        return Mathf.Abs(v.z) > Mathf.Abs(v.x) ? (v.z > 0f ? "+Z" : "-Z") : (v.x > 0f ? "+X" : "-X");
    }

    static string ReadSide(Transform t) { return ReadSide(t.forward); }

    /// <summary>只报告用。**排序前先拷贝** —— 就地 Sort 会打乱别处还在用的 (x,z) 配对。</summary>
    static string FmtSet(List<float> v)
    {
        var sorted = new List<float>(v);
        sorted.Sort();
        var seen = new List<string>();
        foreach (var x in sorted)
        {
            string s = x.ToString("0.00");
            if (!seen.Contains(s)) seen.Add(s);
        }
        return "[" + string.Join(" ", seen) + "]";
    }

    static string Fmt(Color c)
    {
        return string.Format("({0:0.000},{1:0.000},{2:0.000})", c.r, c.g, c.b);
    }

    static StringBuilder NewReport(string what)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 室内构件" + what + " ==");
        sb.AppendLine("指纹 " + EditorApplication.timeSinceStartup.ToString("0.000")
            + "  活动场景 " + SceneManager.GetActiveScene().path);
        return sb;
    }

    static void Write(StringBuilder sb)
    {
        try
        {
            if (!Directory.Exists("Logs")) Directory.CreateDirectory("Logs");
            File.AppendAllText(ReportPath, sb + "\n----\n", new UTF8Encoding(false));
        }
        catch (System.Exception e) { Debug.LogWarning("[Props] 报告落盘失败:" + e.Message); }
    }
}
