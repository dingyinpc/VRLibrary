using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// 把 Blender 出的两块地毯(SM_Carpet_Reading_01 / SM_Carpet_Service_01)搬进本工程:
/// 拷资产 → 配导入器 → 建 4 个材质(每块毯 2 个材质槽) → 换掉 Floor_Detailed 里的 7 个 Cube。
/// 依据:E:\Blender\Blender工程\carpet\交付说明_地毯.md
/// 导入器设置与贴图接法沿用 TechFloorSetup.cs 那一套(URP 14 无 Mask 槽,一张遮罩双接)。
/// 每一步都回读磁盘/网格做断言,报告写 Logs/carpet-import.txt 并带时间指纹。
/// 回退:git checkout -- Assets/_Scenes Assets/Materials Assets/Textures/Carpet
/// </summary>
public static class CarpetSetup
{
    const string SrcTex = @"E:\Blender\Blender贴图\carpet";
    const string SrcModel = @"E:\Blender\Blender工程\carpet";
    const string Dir = "Assets/Textures/Carpet";
    const string ReportPath = "Logs/carpet-import.txt";
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";

    const string TexBase = Dir + "/T_Carpet_BaseColor.png";
    const string TexMask = Dir + "/T_Carpet_URPMask.png";
    const string TexNorm = Dir + "/T_Carpet_Normal.png";
    const string TexEmis = Dir + "/T_Carpet_Emissive.png";

    // 每块毯:源 FBX / 目标路径 / 场景落点 / 期望尺寸 / 场景父节点 / 子物体名 / 两个材质名
    struct CarpetSpec
    {
        public string srcFbx, model, parentPath, childName, bodyMat, trimMat, tag;
        public Vector3 pos, expectSize;
        public string[] children;   // 该父节点下本次允许删掉的名字(包边已在 mesh 里,旧 Cube 全数退场)
    }

    static readonly CarpetSpec Reading = new CarpetSpec
    {
        tag = "阅读区",
        srcFbx = SrcModel + "/SM_Carpet_Reading_01.fbx",
        model = Dir + "/SM_Carpet_Reading_01.fbx",
        parentPath = "Floor_Detailed/ReadingCarpet",
        childName = "Carpet",
        // 底面原点:老 Cube 中心 0.033 / 厚 0.008 → 毯体底 0.029、毯面顶 0.037、包边顶 0.042
        pos = new Vector3(13f, 0.029f, 0f),
        expectSize = new Vector3(7.4f, 0.013f, 18.6f),
        bodyMat = "Assets/Materials/M_CarpetReading.mat",
        trimMat = "Assets/Materials/M_CarpetReading_Trim.mat",
        children = new[] { "Carpet", "Trim_W", "Trim_E", "Trim_N", "Trim_S" },
    };

    static readonly CarpetSpec Service = new CarpetSpec
    {
        tag = "服务台",
        srcFbx = SrcModel + "/SM_Carpet_Service_01.fbx",
        model = Dir + "/SM_Carpet_Service_01.fbx",
        parentPath = "Floor_Detailed/ServiceMat",
        childName = "Mat",
        pos = new Vector3(0f, 0.029f, -16f),
        expectSize = new Vector3(4.4f, 0.013f, 3.6f),
        bodyMat = "Assets/Materials/M_CarpetService.mat",
        trimMat = "Assets/Materials/M_CarpetService_Trim.mat",
        children = new[] { "Mat", "AccentLine" },
    };

    static readonly CarpetSpec[] All = { Reading, Service };

    [MenuItem("VRLibrary/导入Carpet地毯(贴图+材质)")]
    public static void Import()
    {
        var sb = NewReport("导入");
        CopyIn(sb);
        SetupTex(TexBase, TextureImporterType.Default, true, TextureImporterFormat.ASTC_6x6, sb);
        SetupTex(TexMask, TextureImporterType.Default, false, TextureImporterFormat.ASTC_6x6, sb);
        SetupTex(TexNorm, TextureImporterType.NormalMap, false, TextureImporterFormat.ASTC_4x4, sb);
        SetupTex(TexEmis, TextureImporterType.Default, true, TextureImporterFormat.ASTC_6x6, sb);
        foreach (var c in All) SetupModel(c, sb);
        BuildMaterials(sb);
        VerifyModels(sb);
        Write(sb);
        AssetDatabase.SaveAssets();
        Debug.Log(sb.ToString());
    }

    [MenuItem("VRLibrary/Carpet 换上地毯(替换Cube)")]
    public static void ApplyToScene()
    {
        var sb = NewReport("换装");
        var root = GameObject.Find("SmartLibrary");
        if (root == null)
        {
            sb.AppendLine("!! 找不到根节点 SmartLibrary —— 请先打开 Assets/_Scenes/SmartLibrary.unity");
            Write(sb); Debug.Log(sb.ToString()); return;
        }

        foreach (var c in All)
        {
            var parent = root.transform.Find(c.parentPath);
            if (parent == null)
            {
                sb.AppendLine("!! 找不到 " + c.parentPath + " —— 先跑 VRLibrary/精装修地面(图书馆风)");
                continue;
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(c.model);
            var body = AssetDatabase.LoadAssetAtPath<Material>(c.bodyMat);
            var trim = AssetDatabase.LoadAssetAtPath<Material>(c.trimMat);
            if (prefab == null || body == null || trim == null)
            {
                sb.AppendLine("!! " + c.tag + " 缺资产:模型=" + (prefab == null ? "无" : "有")
                    + " 毯面材质=" + (body == null ? "无" : "有") + " 包边材质=" + (trim == null ? "无" : "有")
                    + " → 先跑 VRLibrary/导入Carpet地毯(贴图+材质)");
                continue;
            }

            // 清掉旧的 Cube(毯体 + 4 条包边 / 毯体 + 饰条),包边已在 mesh 里。
            // 先报名字再动手:父节点下若出现清单外的东西,说明有人手摆过新物件,宁可停下也别误删。
            string[] known = c.children;
            var names = new List<string>();
            for (int i = 0; i < parent.childCount; i++) names.Add(parent.GetChild(i).name);
            sb.AppendLine("  " + c.tag + " " + c.parentPath + " 现有子物体 [" + string.Join(" ", names) + "]");

            int removed = 0, blocked = 0;
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var ch = parent.GetChild(i);
                bool ok = System.Array.IndexOf(known, ch.name) >= 0;
                if (!ok) { blocked++; sb.AppendLine("    !! 清单外的子物体 " + ch.name + " —— 没删,自己看一眼"); continue; }
                Undo.DestroyObjectImmediate(ch.gameObject);
                removed++;
            }
            if (blocked > 0) sb.AppendLine("    !! 有 " + blocked + " 个物件不敢删,新地毯仍会照常摆上,可能重叠");

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.name = c.childName;
            Undo.RegisterCreatedObjectUndo(inst, "换上 Carpet 地毯");
            inst.transform.SetParent(parent, false);
            inst.transform.localPosition = c.pos;
            // 交付说明 §3 的映射 Unity=(BlenderX, BlenderZ, -BlenderY) 就是绕 X 转 -90°。
            // 实测 Unity 的 FBX 导入器**没有**替我们做这步(Z-up 原样带进来了,
            // 实例根旋转 0°、厚度还在 Z、18.6 m 长边立在 Y 上),所以这里手动转。
            // 转完局部厚度轴落 Y、底面仍在 local y=0 → pos.y=0.029 让毯面顶正好 0.037。
            inst.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            inst.transform.localScale = Vector3.one;

            var rs = inst.GetComponentsInChildren<MeshRenderer>(true);
            string detail = "";
            if (rs.Length != 1)
            {
                sb.AppendLine("!! " + c.tag + " 实例里有 " + rs.Length + " 个 MeshRenderer(预期 1),"
                    + "材质槽只挂到第一个,后面的自己看一眼");
            }
            if (rs.Length > 0)
            {
                var r = rs[0];
                int slots = r.sharedMaterials.Length;
                if (slots != 2)
                    sb.AppendLine("!! " + c.tag + " submesh 数 = " + slots + " 而不是 2 → 双槽方案失效,"
                        + "包边不会发光。检查 ModelImporter.materialImportMode 是否把材质链接整个剥掉了");
                var mats = new List<Material>();
                mats.Add(body);
                mats.Add(slots >= 2 ? trim : null);
                for (int i = 2; i < slots; i++) mats.Add(trim);
                r.sharedMaterials = mats.ToArray();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = true;

                var col = inst.GetComponentInChildren<Collider>(true);
                if (col != null) { Object.DestroyImmediate(col); detail += " 已摘碰撞体"; }
                // 渲染器可能在子节点上,静态标记必须逐物体设,否则漏掉的那份不参与合批
                foreach (var t in inst.GetComponentsInChildren<Transform>(true))
                    t.gameObject.isStatic = true;

                var b = r.bounds;
                float derr = Mathf.Max(Mathf.Abs(b.size.x - c.expectSize.x),
                    Mathf.Max(Mathf.Abs(b.size.y - c.expectSize.y), Mathf.Abs(b.size.z - c.expectSize.z)));
                detail += string.Format(" 世界包围盒 {0:0.000}×{1:0.000}×{2:0.000} y {3:0.000}~{4:0.000}",
                    b.size.x, b.size.y, b.size.z, b.min.y, b.max.y);
                sb.AppendLine(string.Format("  {0,-6} {1} 落点({2:0.000},{3:0.000},{4:0.000}) 材质[{5}][{6}]{7}",
                    c.tag, c.childName, c.pos.x, c.pos.y, c.pos.z, body.name, trim.name, detail));

                // 毯面顶必须还是 0.037、包边顶 0.042 —— 桌椅都坐在毯面上(工单 A 的落点依据这条)
                sb.AppendLine(string.Format("    期望世界尺寸 ({0:0.000},{1:0.000},{2:0.000}) 最大偏差 {3:0.000}"
                    + " / 毯面顶 y={4:0.000}(须 0.037) 包边顶 y={5:0.000}(须 0.042)",
                    c.expectSize.x, c.expectSize.y, c.expectSize.z, derr, c.pos.y + 0.008f, b.max.y));
                if (derr > 0.002f)
                    sb.AppendLine("    !! 世界尺寸不符(差 " + (derr * 1000f).ToString("0.0") + " mm)→ 朝向没转对,"
                        + "或者 Blender 那侧把 Z-up 补偿烘进 mesh 了(那样这里的 -90° 就是重复转,得去掉)");
                if (Mathf.Abs(b.min.y - c.pos.y) > 0.0015f)
                    sb.AppendLine("    !! 毯底不在落点 y 上(实测 " + b.min.y.ToString("0.000")
                        + " 期望 " + c.pos.y.ToString("0.000") + ")→ 底面不在原点,会和 2 m 方砖 z-fighting");
                if (Mathf.Abs(b.max.y - (c.pos.y + 0.013f)) > 0.0015f)
                    sb.AppendLine("    !! 包边顶不是 0.042 —— 总高不是 13 mm");
                if (Mathf.Abs(c.pos.y + 0.008f - 0.037f) > 0.0015f)
                    sb.AppendLine("    !! 毯面顶不是 0.037 —— 上面那批桌/凳会悬空或陷进毯里");
            }
            sb.AppendLine("  " + c.tag + " 清掉旧 Cube " + removed + " 个");
        }

        var s = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(s);
        EditorSceneManager.SaveOpenScenes();
        sb.AppendLine("场景 " + s.path + " 已保存,isDirty(应为 False)=" + s.isDirty);
        sb.AppendLine("回退:git checkout -- Assets/_Scenes/SmartLibrary.unity 后 openScene");
        Write(sb);
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// 只报告,不改。先重开场景是为了让量到的数字来自磁盘上那份文件,
    /// 而不是内存里刚摆好、可能根本没写出去的临时状态。
    /// </summary>
    [MenuItem("VRLibrary/Carpet 复核落盘(重开场景量)")]
    public static void VerifySaved()
    {
        var sb = NewReport("复核");
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        sb.AppendLine("  从磁盘重开 " + scene.path + " 未保存改动=" + scene.isDirty);
        var root = GameObject.Find("SmartLibrary");
        if (root == null) { sb.AppendLine("!! 重开后找不到 SmartLibrary"); Write(sb); Debug.Log(sb.ToString()); return; }

        foreach (var c in All)
        {
            var parent = root.transform.Find(c.parentPath);
            if (parent == null) { sb.AppendLine("!! 找不到 " + c.parentPath); continue; }
            var names = new List<string>();
            for (int i = 0; i < parent.childCount; i++) names.Add(parent.GetChild(i).name);
            sb.AppendLine("  " + c.tag + " 子物体 [" + string.Join(" ", names) + "](应只剩 " + c.childName + ")");

            var t = parent.Find(c.childName);
            if (t == null) { sb.AppendLine("    !! 没有 " + c.childName); continue; }
            var r = t.GetComponentInChildren<MeshRenderer>();
            if (r == null) { sb.AppendLine("    !! 没有 MeshRenderer"); continue; }
            var b = r.bounds;
            float derr = Mathf.Max(Mathf.Abs(b.size.x - c.expectSize.x),
                Mathf.Max(Mathf.Abs(b.size.y - c.expectSize.y), Mathf.Abs(b.size.z - c.expectSize.z)));
            string mats = "";
            foreach (var m in r.sharedMaterials) mats += (m == null ? "null " : m.name + " ");
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf == null ? null : mf.sharedMesh;
            sb.AppendLine(string.Format("    网格 {0} 三角 {1} 世界 {2:0.000}×{3:0.000}×{4:0.000} y {5:0.000}~{6:0.000}"
                + " 尺寸偏差 {7:0.000} 本地旋转X {8:0.0}° 静态={9} 材质[{10}]",
                mesh == null ? "?" : mesh.name,
                mesh == null ? 0 : mesh.triangles.Length / 3,
                b.size.x, b.size.y, b.size.z, b.min.y, b.max.y, derr,
                t.localRotation.eulerAngles.x, t.gameObject.isStatic, mats.Trim()));
            if (derr > 0.002f) sb.AppendLine("    !! 落盘后的世界尺寸仍不对 → 旋转没写出去");
            if (Mathf.Abs(b.max.y - 0.042f) > 0.0015f) sb.AppendLine("    !! 包边顶不是 0.042");
            if (r.sharedMaterials.Length != 2) sb.AppendLine("    !! 落盘后材质槽不是 2");
            if (t.GetComponentInChildren<Collider>(true) != null)
                sb.AppendLine("    !! 还带着碰撞体 → 会挡住地毯上的传送射线/交互");
        }
        sb.AppendLine("回退本步:copy Logs/SmartLibrary.pre-carpet.unity → Assets/_Scenes/SmartLibrary.unity 后重开场景");
        Write(sb);
        Debug.Log(sb.ToString());
    }

    [MenuItem("VRLibrary/Carpet 检查接线(只报告)")]
    public static void Inspect()
    {
        var sb = NewReport("检查");
        // 对照:这两个材质在场上确实发光。它们的状态才是"发光需要什么"的基准 ——
        // 如果它们也报 IsKeywordEnabled=False,那这个探针量的就不是发光的开关,别拿它判地毯的罪。
        foreach (var p in new[] { "Assets/Materials/M_NeonCyan.mat", "Assets/Materials/M_LightBar.mat" })
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) continue;
            sb.AppendLine(string.Format("  [对照]{0,-22} 关键词=[{1}] EmitColor={2} IsKeywordEnabled(_EMISSION)={3}"
                    + " EmitMap={4} GIflags={5}", m.name, string.Join(",", m.shaderKeywords),
                    m.GetColor("_EmissionColor"), m.IsKeywordEnabled("_EMISSION"), Tex(m, "_EmissionMap"),
                    m.globalIlluminationFlags));
        }
        foreach (var c in All)
        {
            foreach (var p in new[] { c.bodyMat, c.trimMat })
            {
                // 先强制重导入再量:_EMISSION 是被 AwakeFromLoad 摘掉的,
                // 不重导入就只能看到"刚写完那一瞬"的内存值,那种读法永远报喜。
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m == null) { sb.AppendLine("  缺材质 " + p); continue; }
                sb.AppendLine(string.Format("  {0,-24} shader={1} 关键词=[{2}] Base={3} MG={4} Occ={5} Bump={6} "
                    + "EmitMap={7} EmitColor={8} ST={9} Metallic={10:0.00} Smooth={11:0.00}",
                    m.name, m.shader.name, string.Join(",", m.shaderKeywords),
                    Tex(m, "_BaseMap"), Tex(m, "_MetallicGlossMap"), Tex(m, "_OcclusionMap"),
                    Tex(m, "_BumpMap"), Tex(m, "_EmissionMap"), m.GetColor("_EmissionColor"),
                    m.GetTextureScale("_BaseMap"), m.GetFloat("_Metallic"), m.GetFloat("_Smoothness")));
                // 落盘 YAML 里明明列了 _EMISSION,shaderKeywords 却不返回它 ——
                // 真正决定 shader 走不走发光分支的是这两个,所以单独量一遍。
                sb.AppendLine(string.Format("      IsKeywordEnabled(_EMISSION)={0}  shaderKeywords 含 _EMISSION={1}"
                    + "  globalIlluminationFlags={2}",
                    m.IsKeywordEnabled("_EMISSION"),
                    System.Array.IndexOf(m.shaderKeywords, "_EMISSION") >= 0, m.globalIlluminationFlags));
                bool wantsGlow = m.GetColor("_EmissionColor").maxColorComponent > 0.001f;
                if (wantsGlow && !m.IsKeywordEnabled("_EMISSION"))
                    sb.AppendLine("      !! 有发光色但 _EMISSION 没活过重导入 → 场上其实是黑的。"
                        + "重跑 VRLibrary/导入Carpet地毯(贴图+材质)");
                if (!wantsGlow && m.IsKeywordEnabled("_EMISSION"))
                    sb.AppendLine("      !! 毯面不该发光却带着 _EMISSION → 检查 MakeMat 的分支");
            }
        }
        Write(sb);
        Debug.Log(sb.ToString());
    }

    // ---------------------------------------------------------------- 内部

    static void CopyIn(StringBuilder sb)
    {
        if (!Directory.Exists(SrcTex) || !Directory.Exists(SrcModel))
        {
            sb.AppendLine("!! 找不到 Blender 源目录:" + SrcTex + " / " + SrcModel); return;
        }
        Directory.CreateDirectory(Dir);
        Copy(Path.Combine(SrcTex, "T_Carpet_BaseColor.png"), TexBase, sb);
        Copy(Path.Combine(SrcTex, "T_Carpet_URPMask.png"), TexMask, sb);
        Copy(Path.Combine(SrcTex, "T_Carpet_Normal.png"), TexNorm, sb);
        Copy(Path.Combine(SrcTex, "T_Carpet_Emissive.png"), TexEmis, sb);
        foreach (var c in All) Copy(c.srcFbx, c.model, sb);
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
        sb.AppendLine("  拷入 " + Path.GetFileName(dst) + "  源时间 "
            + new FileInfo(src).LastWriteTime.ToString("MM-dd HH:mm:ss"));
    }

    static void SetupTex(string path, TextureImporterType type, bool srgb,
                         TextureImporterFormat android, StringBuilder sb)
    {
        var im = AssetImporter.GetAtPath(path) as TextureImporter;
        if (im == null) { sb.AppendLine("!! 没有导入器 " + path); return; }
        im.textureType = type;
        im.sRGBTexture = srgb;
        im.wrapMode = TextureWrapMode.Repeat;
        im.mipmapEnabled = true;
        im.filterMode = FilterMode.Trilinear;
        im.anisoLevel = 8;
        im.maxTextureSize = 1024;
        im.streamingMipmaps = false;
        im.SetPlatformTextureSettings(new TextureImporterPlatformSettings
        {
            name = "Android", overridden = true, maxTextureSize = 1024,
            format = android, compressionQuality = 1,
        });
        im.SaveAndReimport();
        sb.AppendLine("  " + Path.GetFileName(path) + " → " + type + (srgb ? " sRGB" : " 线性")
            + " Repeat/Trilinear/aniso8 Android=" + android);
    }

    static void SetupModel(CarpetSpec c, StringBuilder sb)
    {
        var im = AssetImporter.GetAtPath(c.model) as ModelImporter;
        if (im == null) { sb.AppendLine("!! 没有模型导入器 " + c.model); return; }
        // 材质由本工具单独建。注意:None 只跳过"生成材质资产",不删 mesh 的 submesh 划分 ——
        // 双槽方案靠的是 mesh 里的材质分组,实测(见 VerifyModels)两个槽都读得回来。
        im.materialImportMode = ModelImporterMaterialImportMode.None;
        im.useFileScale = true;
        im.globalScale = 1f;
        im.importBlendShapes = false;
        im.SaveAndReimport();
        sb.AppendLine("  " + Path.GetFileName(c.model) + " → materialImportMode=None, 1 unit=1 m");
    }

    static void BuildMaterials(StringBuilder sb)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) { sb.AppendLine("!! 找不到 URP/Lit"); return; }
        var basec = AssetDatabase.LoadAssetAtPath<Texture2D>(TexBase);
        var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(TexMask);
        var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(TexNorm);
        if (basec == null || mask == null || nrm == null)
        { sb.AppendLine("!! 有贴图没导入成功,材质先不建"); return; }

        // 包边的发光色**不写死**:直接抄场上同族霓虹材质的现值。
        // 老地毯的包边用的就是 M_NeonCyan / M_NeonMagenta 这两个材质,抄它们才叫"换模型不换观感";
        // 而且灯色再变时重跑本菜单会自动跟上,不必在第二处维护同一个颜色。
        Color cyanEmit = EmissionOf("Assets/Materials/M_NeonCyan.mat", new Color(120f, 199f, 210f) / 255f, sb);
        Color magEmit = EmissionOf("Assets/Materials/M_NeonMagenta.mat", new Color(248f, 181f, 208f) / 255f, sb);

        MakeMat(Reading.bodyMat, "M_CarpetReading", shader, basec, mask, nrm, null, sb);
        MakeMat(Reading.trimMat, "M_CarpetReading_Trim", shader, basec, mask, nrm, cyanEmit, sb);
        MakeMat(Service.bodyMat, "M_CarpetService", shader, basec, mask, nrm, null, sb);
        MakeMat(Service.trimMat, "M_CarpetService_Trim", shader, basec, mask, nrm, magEmit, sb);
    }

    static Color EmissionOf(string matPath, Color fallback, StringBuilder sb)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (m == null || !m.HasProperty("_EmissionColor"))
        {
            sb.AppendLine("  !! " + Path.GetFileName(matPath)
                + " 读不到发光色,包边退回工单里的字面目标色(会比其它霓虹暗一档)");
            return fallback;
        }
        var e = m.GetColor("_EmissionColor");
        sb.AppendLine("  包边发光抄自 " + m.name + " = " + Fmt(e));
        return e;
    }

    /// <summary>emission 传 null = 毯面(绝对不发光);传值 = 包边(纯色发光,不接 Emissive 图)。</summary>
    static void MakeMat(string path, string name, Shader shader, Texture2D basec, Texture2D mask,
                        Texture2D nrm, Color? emission, StringBuilder sb)
    {
        var old = AssetDatabase.LoadAssetAtPath<Material>(path);
        var mat = old ?? new Material(shader);
        mat.name = name;
        mat.shader = shader;
        mat.SetTexture("_BaseMap", basec);
        mat.SetTexture("_MetallicGlossMap", mask);
        mat.SetTexture("_OcclusionMap", mask);
        mat.SetTexture("_BumpMap", nrm);
        mat.SetFloat("_Metallic", 1f);       // 接图后 shader 取遮罩 R(地毯 R 全 0 = 非金属)
        mat.SetFloat("_Smoothness", 1f);     // 会乘遮罩 A(≈0.25),再压就没了
        mat.SetFloat("_OcclusionStrength", 1f);
        mat.SetFloat("_BumpScale", 1f);
        mat.SetFloat("_Surface", 0f);
        // 贴图的 UV 已经是世界尺度(1 UV 格 = 1 m),所以平铺保持 1×1,不能再乘尺寸
        mat.SetTextureScale("_BaseMap", new Vector2(1f, 1f));

        if (emission.HasValue)
        {
            // 包边**不接** _EmissionMap:T_Carpet_Emissive 是全黑图,乘上去发光直接被抹平。
            mat.SetTexture("_EmissionMap", null);
            mat.SetColor("_EmissionColor", emission.Value);
            mat.EnableKeyword("_EMISSION");
            // 光 EnableKeyword 只活这一场:Material 在 AwakeFromLoad 里按 globalIlluminationFlags
            // 重算,标记还是 EmissiveIsBlack 就把 _EMISSION 摘掉 —— 实测重导入后包边不亮。
            // 场上确实发光的 M_NeonCyan / M_LightBar 带的都是 RealtimeEmissive,照抄这个。
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        else
        {
            mat.SetTexture("_EmissionMap", null);
            mat.SetColor("_EmissionColor", new Color(0f, 0f, 0f, 1f));
            mat.DisableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }
        mat.EnableKeyword("_METALLICSPECGLOSSMAP");
        mat.EnableKeyword("_OCCLUSIONMAP");
        mat.EnableKeyword("_NORMALMAP");
        mat.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
        mat.DisableKeyword("_SPECULAR_SETUP");
        mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHATEST_ON");

        if (old == null) AssetDatabase.CreateAsset(mat, path);
        else EditorUtility.SetDirty(mat);
        sb.AppendLine(string.Format("  {0,-24} 发光={1} 关键词=[{2}]", name,
            emission.HasValue ? Fmt(emission.Value) : "关(黑)", string.Join(",", mat.shaderKeywords)));
    }

    /// <summary>
    /// 轴无关的回读验证:不假设 Blender 那套"平面在 XY / 厚度在 Z"映射成立
    /// (实测本 FBX 进 Unity 后厚度落在 Z、长边落在 Y,交付说明 §3 的映射在这里不对),
    /// 所以先按尺寸最小的一轴认定"厚度轴",再在这个轴上找毯面顶/包边顶两层顶点。
    /// </summary>
    static void VerifyModels(StringBuilder sb)
    {
        foreach (var c in All)
        {
            var objs = AssetDatabase.LoadAllAssetsAtPath(c.model);
            Mesh mesh = null;
            if (objs != null)
                foreach (var o in objs)
                {
                    var mf = o as Mesh;
                    if (mf != null && mf.vertexCount > 0) mesh = mf;
                }
            if (mesh == null) { sb.AppendLine("!! " + c.tag + " 里读不到网格 " + c.model); continue; }

            var b = mesh.bounds;
            int tris = mesh.triangles.Length / 3;
            var uv = mesh.uv;
            Vector4 uvr = new Vector4(99f, -99f, 99f, -99f);
            foreach (var t in uv)
            {
                uvr.x = Mathf.Min(uvr.x, t.x); uvr.y = Mathf.Max(uvr.y, t.x);
                uvr.z = Mathf.Min(uvr.z, t.y); uvr.w = Mathf.Max(uvr.w, t.y);
            }

            int thick = AxisOfLeast(b.size);
            string ax = "XYZ"[thick].ToString();
            float tmin = Extent(b, thick, true), tmax = Extent(b, thick, false);
            float bodyT = tmin + 0.008f, trimT = tmin + 0.013f;

            int atBody = 0, atTrim = 0;
            var verts = mesh.vertices;
            foreach (var v in verts)
            {
                float t = Axis(v, thick);
                if (Mathf.Abs(t - bodyT) < 0.0005f) atBody++;
                if (Mathf.Abs(t - trimT) < 0.0005f) atTrim++;
            }

            sb.AppendLine(string.Format("  {0} 网格 {1}: 尺寸 {2:0.0000}×{3:0.0000}×{4:0.0000}"
                + " 厚度轴={5}(范围 {6:0.0000}~{7:0.0000}) 三角 {8} submesh {9} 顶点 {10}",
                c.tag, mesh.name, b.size.x, b.size.y, b.size.z, ax, tmin, tmax,
                tris, mesh.subMeshCount, mesh.vertexCount));
            sb.AppendLine(string.Format("     包围盒 min {0:0.000} {1:0.000} {2:0.000} / max {3:0.000} {4:0.000} {5:0.000}",
                b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z));
            sb.AppendLine(string.Format("     UV.u {0:0.00}~{1:0.00}  UV.v {2:0.00}~{3:0.00}",
                uvr.x, uvr.y, uvr.z, uvr.w));
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var sm = mesh.GetSubMesh(i);
                sb.AppendLine(string.Format("     槽 {0}: {1} 个三角形 起始顶点 {2}", i, sm.indexCount / 3, sm.baseVertex));
            }
            sb.AppendLine(string.Format("     {0} 轴上:毯面顶 {1:0.000} 处 {2} 个顶点 / 包边顶 {3:0.000} 处 {4} 个顶点",
                ax, bodyT, atBody, trimT, atTrim));

            if (Axis(b.size, thick) > 0.02f)
                sb.AppendLine("     !! 厚度轴比 20 mm 还厚,不像地毯 —— 检查轴映射");
            if (Mathf.Abs(Axis(b.size, thick) - 0.013f) > 0.001f)
                sb.AppendLine("     !! 厚度不是交付说明写的 13 mm");
            if (atTrim == 0) sb.AppendLine("     !! 没有包边顶那一层顶点 → 包边其实不在 mesh 里");
            if (mesh.subMeshCount != 2) sb.AppendLine("     !! submesh 不是 2,双槽方案失效");
            ReportWorldPose(c, sb);
            ReportUvScale(c, mesh, thick, bodyT, sb);
        }
    }

    /// <summary>
    /// 量"真的摆进场景"的世界朝向。mesh.bounds 是 FBX 自己的 Z-up 坐标(厚度在 Z),
    /// 导入器的 Z-up→Y-up 补偿是打在 Transform 上的,不进顶点 ——
    /// 所以只读 mesh.bounds 会把"平铺在地"误判成"竖着站起来"。这里临时实例化再读世界包围盒。
    /// </summary>
    static void ReportWorldPose(CarpetSpec c, StringBuilder sb)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(c.model);
        if (prefab == null) { sb.AppendLine("     !! 读不到 prefab " + c.model); return; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        try
        {
            go.transform.localScale = Vector3.one;
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            var r = go.GetComponentInChildren<MeshRenderer>();
            if (r == null) { sb.AppendLine("     !! 实例里没有 MeshRenderer"); return; }
            var raw = r.bounds;
            bool flatAsIs = raw.size.y <= 0.02f;
            sb.AppendLine(string.Format("     不旋转时世界尺寸 {0:0.0000}×{1:0.0000}×{2:0.0000}(导入器{3} Z-up→Y-up)",
                raw.size.x, raw.size.y, raw.size.z, flatAsIs ? "已经做好" : "没做"));

            go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var b = r.bounds;
            sb.AppendLine(string.Format("     转 -90°X 后 {0:0.0000}×{1:0.0000}×{2:0.0000}  y {3:0.0000}~{4:0.0000}",
                b.size.x, b.size.y, b.size.z, b.min.y, b.max.y));
            if (b.size.y > 0.02f)
                sb.AppendLine("     !! 转了 -90° 仍然不平 → 两条轴向都要重新对(或 Blender 那侧改了导出轴)");
            if (b.min.y < -0.0005f)
                sb.AppendLine("     !! 转后底面仍在原点以下 → 原点不在底面,y 落点要按 min.y 反推");
            if (flatAsIs)
                sb.AppendLine("     !! 资产本身已经是 Y-up 平的,这里再转 -90° 就是重复转 → 摆放要改回 identity");
        }
        finally { Object.DestroyImmediate(go); }
    }

    /// <summary>
    /// 只验"从上往下看得到的那一面"的 UV。Blender 那侧(build_carpet.py:108-120)是
    /// **逐面按主法线轴投影**的:平放面 uv=(x,y),侧壁面 uv=(y,z) 或 (x,z)。
    /// 所以拿整个网格做一次全局线性拟合必然对不上(残差好几米),那是量的方法错了,不是资产错了。
    /// 工单要的"1 UV 格 = 1 m"只对可见的毯面成立,就只测它。
    /// </summary>
    static void ReportUvScale(CarpetSpec c, Mesh mesh, int thick, float topLayer, StringBuilder sb)
    {
        var verts = mesh.vertices;
        var uv = mesh.uv;
        var tri = mesh.triangles;
        if (uv.Length != verts.Length || tri.Length == 0)
        { sb.AppendLine("     !! 没有可用的 UV/顶点配对"); return; }

        var keep = new HashSet<int>();
        int faces = 0;
        for (int i = 0; i + 2 < tri.Length; i += 3)
        {
            var a = verts[tri[i]]; var b = verts[tri[i + 1]]; var d = verts[tri[i + 2]];
            Vector3 nrm = Vector3.Cross(b - a, d - a);
            if (nrm.sqrMagnitude < 1e-12f) continue;
            nrm.Normalize();
            if (Mathf.Abs(Axis(nrm, thick)) < 0.9f) continue;         // 不是平放面(倒角/侧壁)
            float cz = Axis((a + b + d) * (1f / 3f), thick);
            if (Mathf.Abs(cz - topLayer) > 0.0005f) continue;         // 不是毯面顶那一层
            faces++;
            keep.Add(tri[i]); keep.Add(tri[i + 1]); keep.Add(tri[i + 2]);
        }
        if (keep.Count < 3) { sb.AppendLine("     !! 找不到平放的毯面三角,UV 无从判定"); return; }

        var pv = new List<Vector3>(); var pu = new List<Vector2>();
        foreach (var ix in keep) { pv.Add(verts[ix]); pu.Add(uv[ix]); }
        var va = pv.ToArray(); var ua = pu.ToArray();

        for (int ch = 0; ch < 2; ch++)
        {
            float a2, b2, k, err;
            Solve(va, ua, ch, out a2, out b2, out k, out err);
            string name = ch == 0 ? "u" : "v";
            float rep = Mathf.Max(Mathf.Abs(a2), Mathf.Abs(b2));
            string geo = Mathf.Abs(a2) >= Mathf.Abs(b2) ? "X" : "Y";
            sb.AppendLine(string.Format("     毯面({0} 格)UV.{1} = {2:0.0000}·X + {3:0.0000}·Y + {4:0.000}"
                + " 残差 {5:0.0000} → 主方向 {6}, {7:0.000} 重复/米", faces, name, a2, b2, k, err, geo, rep));
            if (err > 0.002f)
                sb.AppendLine("     !! 毯面 UV 连平面投影都不是(残差 " + err.ToString("0.0000") + ")→ 回 Blender 查 UV");
            else if (Mathf.Abs(rep - 1f) > 0.02f)
                sb.AppendLine("     !! 毯面 UV 不是 1 格 1 米(实测 " + rep.ToString("0.000")
                    + ")→ 织纹沿 " + geo + " 被" + (rep > 1 ? "压密" : "拉疏") + "。_BaseMap_ST." + name.ToUpper()
                    + " 填 " + (1f / Mathf.Max(rep, 1e-4f)).ToString("0.0000") + " 可拉回,不用回 Blender");
        }
    }

    /// <summary>最小二乘解 uv = a·X + b·Y + k(3 未知数正规方程),顺带给最大残差。</summary>
    static void Solve(Vector3[] v, Vector2[] uv, int ch, out float a, out float b, out float k, out float err)
    {
        a = b = k = err = 0f;
        float n = v.Length;
        float sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, su = 0, sxu = 0, syu = 0;
        for (int i = 0; i < v.Length; i++)
        {
            float x = v[i].x, y = v[i].y, u = ch == 0 ? uv[i].x : uv[i].y;
            sxx += x * x; sxy += x * y; syy += y * y;
            sx += x; sy += y; su += u; sxu += x * u; syu += y * u;
        }
        // [sxx sxy sx; sxy syy sy; sx sy n] · [a b k]^T = [sxu syu su]^T
        float m00 = sxx, m01 = sxy, m02 = sx, m11 = syy, m12 = sy;
        float det = m00 * (m11 * n - m12 * m12) - m01 * (m01 * n - m12 * sx) + m02 * (m01 * m12 - m11 * sx);
        if (Mathf.Abs(det) < 1e-9f) return;
        float r0 = sxu, r1 = syu, r2 = su;
        a = (r0 * (m11 * n - m12 * m12) - m01 * (r1 * n - m12 * r2) + m02 * (r1 * m12 - m11 * r2)) / det;
        b = (m00 * (r1 * n - m12 * r2) - r0 * (m01 * n - m12 * sx) + m02 * (m01 * r2 - r1 * sx)) / det;
        k = (m00 * (m11 * r2 - r1 * m12) - m01 * (m01 * r2 - r1 * sx) + r0 * (m01 * m12 - m11 * sx)) / det;
        for (int i = 0; i < v.Length; i++)
        {
            float u = ch == 0 ? uv[i].x : uv[i].y;
            err = Mathf.Max(err, Mathf.Abs(u - (a * v[i].x + b * v[i].y + k)));
        }
    }

    static int AxisOfLeast(Vector3 s)
    {
        if (s.x <= s.y && s.x <= s.z) return 0;
        return s.y <= s.z ? 1 : 2;
    }

    static float Axis(Vector3 v, int i) { return i == 0 ? v.x : i == 1 ? v.y : v.z; }

    static float Extent(Bounds b, int i, bool min)
    {
        var m = min ? b.min : b.max;
        return Axis(m, i);
    }

    static string Fmt(Color c)
    {
        return string.Format("({0:0.000},{1:0.000},{2:0.000})", c.r, c.g, c.b);
    }

    static string Tex(Material m, string prop)
    {
        var t = m.GetTexture(prop);
        return t == null ? "空" : t.name;
    }

    static StringBuilder NewReport(string what)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== Carpet 地毯" + what + " ==");
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
        catch (System.Exception e) { Debug.LogWarning("[Carpet] 报告落盘失败:" + e.Message); }
    }
}
