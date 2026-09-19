using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// 把 Blender 出的 SM_TechFloor_Tile_01 技术地板搬进本工程,并按 URP 14 的原生槽位接成材质。
public static class TechFloorSetup
{
    const string SrcTexDir = @"E:\Blender\Blender贴图\tech_floor";
    const string SrcFbx = @"E:\Blender\Blender工程\tech_floor\SM_TechFloor_Tile_01.fbx";
    const string TexDir = "Assets/Textures/TechFloor";
    const string ModelPath = TexDir + "/SM_TechFloor_Tile_01.fbx";
    const string MatPath = "Assets/Materials/M_TechFloor.mat";

    const string BaseColor = TexDir + "/T_TechFloor_BaseColor.png";
    const string Mask = TexDir + "/T_TechFloor_URPMask.png";
    const string Normal = TexDir + "/T_TechFloor_Normal.png";
    const string Emissive = TexDir + "/T_TechFloor_Emissive.png";

    [MenuItem("VRLibrary/导入TechFloor地砖(贴图+材质)")]
    public static void Run()
    {
        CopyIn();

        SetupTex(BaseColor, TextureImporterType.Default, true, TextureImporterFormat.ASTC_6x6);
        SetupTex(Mask, TextureImporterType.Default, false, TextureImporterFormat.ASTC_6x6);
        SetupTex(Normal, TextureImporterType.NormalMap, false, TextureImporterFormat.ASTC_4x4);
        SetupTex(Emissive, TextureImporterType.Default, true, TextureImporterFormat.ASTC_6x6);
        SetupModel();

        BuildMaterial();
        ReportMeshSize();
        AssetDatabase.SaveAssets();
        Debug.Log("[TechFloor] 完成。贴图在 " + TexDir + ",材质在 " + MatPath);
    }

    static void CopyIn()
    {
        if (!Directory.Exists(SrcTexDir)) { Debug.LogError("[TechFloor] 找不到贴图源目录:" + SrcTexDir); return; }
        Directory.CreateDirectory(TexDir);
        Copy(SrcFbx, ModelPath);
        Copy(Path.Combine(SrcTexDir, "T_TechFloor_BaseColor.png"), BaseColor);
        Copy(Path.Combine(SrcTexDir, "T_TechFloor_URPMask.png"), Mask);
        Copy(Path.Combine(SrcTexDir, "T_TechFloor_Normal.png"), Normal);
        Copy(Path.Combine(SrcTexDir, "T_TechFloor_Emissive.png"), Emissive);
        AssetDatabase.Refresh();
    }

    static void Copy(string src, string dst)
    {
        if (!File.Exists(src)) { Debug.LogError("[TechFloor] 缺文件:" + src); return; }
        if (File.Exists(dst) && new FileInfo(src).LastWriteTimeUtc <= new FileInfo(dst).LastWriteTimeUtc) return;
        File.Copy(src, dst, true);
        var meta = dst + ".meta";
        if (File.Exists(meta)) File.Delete(meta);   // 让 Unity 重新生成,避免旧 meta 与新内容错位
    }

    // 关键:Android 覆写里把尺寸锁在 1024(=原图尺寸),真机就不会再被降一档;
    // 格式写死 ASTC 而不是 Automatic,免得 GLES3 构建退到 ETC2 把细节抹平。
    static void SetupTex(string path, TextureImporterType type, bool srgb, TextureImporterFormat androidFormat)
    {
        var im = AssetImporter.GetAtPath(path) as TextureImporter;
        if (im == null) { Debug.LogError("[TechFloor] 没有导入器:" + path); return; }
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
            name = "Android",
            overridden = true,
            maxTextureSize = 1024,
            format = androidFormat,
            compressionQuality = 1     // 该字段是 int:0=Low 1=Normal 2=High
        });
        im.SaveAndReimport();
        Debug.Log("[TechFloor] " + Path.GetFileName(path) + " → " + type
            + (srgb ? " sRGB" : " 线性") + " Android=" + androidFormat);
    }

    static void SetupModel()
    {
        var im = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (im == null) { Debug.LogError("[TechFloor] 没有模型导入器:" + ModelPath); return; }
        im.materialImportMode = ModelImporterMaterialImportMode.None;   // 材质由本工具单独建,别生成一份重复的
        im.useFileScale = true;
        im.globalScale = 1f;
        im.SaveAndReimport();
    }

    static void BuildMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) { Debug.LogError("[TechFloor] 找不到 URP/Lit shader"); return; }
        var basec = AssetDatabase.LoadAssetAtPath<Texture2D>(BaseColor);
        var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(Mask);
        var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(Normal);
        var em = AssetDatabase.LoadAssetAtPath<Texture2D>(Emissive);
        if (basec == null || mask == null || nrm == null || em == null)
        { Debug.LogError("[TechFloor] 有贴图没导入成功,材质先不建"); return; }

        // URP 14 的 Lit 没有 Mask Map 槽(那是 HDRP/ShaderGraph 的)。但从 Shaders/LitInput.hlsl
        // 看它的取法正好对上这张遮罩图:metallic=_MetallicGlossMap.r、smoothness=_MetallicGlossMap.a*_Smoothness、
        // occlusion=_OcclusionMap.g。所以同一张遮罩接进两个槽即可,无需拆图,B 通道闲置。
        var old = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        var mat = old != null ? old : new Material(shader);
        mat.name = "M_TechFloor";
        mat.shader = shader;
        mat.SetTexture("_BaseMap", basec);
        mat.SetTexture("_MetallicGlossMap", mask);
        mat.SetTexture("_OcclusionMap", mask);
        mat.SetTexture("_BumpMap", nrm);
        mat.SetTexture("_EmissionMap", em);
        mat.SetFloat("_Metallic", 1f);        // 接了图时 shader 直接取 R,这个值不参与;留 1 以便日后摘图
        mat.SetFloat("_Smoothness", 1f);      // 会乘 A:不是 1 就把贴图平滑度整片压掉
        mat.SetFloat("_OcclusionStrength", 1f);
        mat.SetFloat("_BumpScale", 1f);
        mat.SetFloat("_Surface", 0f);         // Opaque
        // 默认关:本场景地上已有一套发光导光线(GL_* / Guide_*),砖的发光线画在贴图内部,
        // 平铺后会与那套线错位叠加 → 灯光线紊乱。要亮就用菜单 TechFloor 地板发光开关。
        mat.SetColor("_EmissionColor", new Color(0f, 0f, 0f, 1f));

        Enable(mat, "_METALLICSPECGLOSSMAP", true);
        Enable(mat, "_OCCLUSIONMAP", true);
        Enable(mat, "_NORMALMAP", true);
        Enable(mat, "_EMISSION", true);
        Enable(mat, "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", false);  // 平滑度在遮罩 A,不在底色 A
        Enable(mat, "_SPECULAR_SETUP", false);
        Enable(mat, "_SURFACE_TYPE_TRANSPARENT", false);
        Enable(mat, "_ALPHATEST_ON", false);

        if (old == null) AssetDatabase.CreateAsset(mat, MatPath);
        else { EditorUtility.SetDirty(mat); }
        Debug.Log("[TechFloor] 材质关键词:" + string.Join(",", mat.shaderKeywords));
    }

    [MenuItem("VRLibrary/TechFloor 检查材质接线")]
    public static void Inspect()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null) { Debug.LogError("[TechFloor] 没有 " + MatPath); return; }
        Debug.Log("[TechFloor] 检查 shader=" + mat.shader.name
            + " 关键词=[" + string.Join(",", mat.shaderKeywords) + "]"
            + " BaseMap=" + Name(mat, "_BaseMap")
            + " MetallicGloss=" + Name(mat, "_MetallicGlossMap")
            + " Occlusion=" + Name(mat, "_OcclusionMap")
            + " Bump=" + Name(mat, "_BumpMap")
            + " Emission=" + Name(mat, "_EmissionMap")
            + " _Metallic=" + mat.GetFloat("_Metallic")
            + " _Smoothness=" + mat.GetFloat("_Smoothness")
            + " _EmissionColor=" + mat.GetColor("_EmissionColor")
            + " _BaseMap_ST=" + mat.GetVector("_BaseMap_ST"));
        // 这里只报告、不擅自开启:本场景地上已有一套导光线(GL_* / M_GridLine),
        // 砖的发光线画在贴图内部而非边缘,平铺后与那套线错位叠加 → 灯光线紊乱。默认应为关。
        Debug.Log("[TechFloor] 自发光" + (EmissionOn(mat) ? "开" : "关") + "（强度 " + mat.GetColor("_EmissionColor").maxColorComponent.ToString("F1") + "）"
            + "(切换用菜单 VRLibrary → TechFloor 地板发光开关)");
    }

    static bool EmissionOn(Material m) { return m.GetColor("_EmissionColor").maxColorComponent > 0.001f; }

    [MenuItem("VRLibrary/TechFloor 地板发光开关")]
    public static void ToggleEmission()
    {
        // 场上真正在渲染的是 2m 方砖用的 M_TechFloorTile,两份都要翻,否则点了没反应
        bool? target = null;
        foreach (var p in new[] { MatPath, "Assets/Materials/M_TechFloorTile.mat" })
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat == null) continue;
            if (target == null) target = !EmissionOn(mat);
            // 不用 EnableKeyword/DisableKeyword 当开关:实测 EnableKeyword 能落盘、DisableKeyword 落不了盘,
            // 切两次文件里 _EMISSION 依旧在。颜色写 0 时 shader 里 map.rgb × 0 = 无发光,效果等价且必定持久。
            mat.SetColor("_EmissionColor", target.Value ? new Color(4f, 4f, 4f, 1f) : new Color(0f, 0f, 0f, 1f));
            EditorUtility.SetDirty(mat);
        }
        AssetDatabase.SaveAssets();
        var sb = new System.Text.StringBuilder();
        foreach (var p in new[] { MatPath, "Assets/Materials/M_TechFloorTile.mat" })
        {
            var disk = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (disk == null) continue;
            sb.Append(disk.name + "=" + (EmissionOn(disk) ? "开" : "关") + " ");
        }
        Debug.Log("[TechFloor] 地板自发光落盘状态:" + sb.ToString().Trim());
    }

    [MenuItem("VRLibrary/TechFloor 接到主地板")]
    public static void ApplyToFloor()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null) { Debug.LogError("[TechFloor] 没有 " + MatPath); return; }
        var floor = GameObject.Find("Floor");
        var r = floor == null ? null : floor.GetComponent<MeshRenderer>();
        if (r == null) { Debug.LogError("[TechFloor] 找不到 SmartLibrary/Floor 的 MeshRenderer"); return; }

        // 实测:Floor 是内置 Plane(10m) 缩放 4 → 40m×40m,UV 0..1;砖网格 1m×1m。
        // URP Lit 所有贴图共用 _BaseMap_ST(LitForwardPass.hlsl:155),设一次就全跟着走。
        mat.SetTextureScale("_BaseMap", new Vector2(40f, 40f));
        EditorUtility.SetDirty(mat);

        var old = r.sharedMaterial;
        r.sharedMaterial = mat;
        EditorUtility.SetDirty(r);
        AssetDatabase.SaveAssets();
        Debug.Log("[TechFloor] 主地板材质 " + (old == null ? "null" : old.name) + " → M_TechFloor,平铺 40×40(1 砖 1 米)。"
            + " 回退:git checkout HEAD -- Assets/_Scenes/SmartLibrary.unity 后重新打开场景。");
    }

    // 场上真正露出来的表面是 Floor_Detailed/Tiles 里那 400 块 2 m 方砖(顶面 y=0.028),
    // 主地板只在 4 cm 砖缝里露一线。所以地砖要铺到这些方砖上才看得见。
    [MenuItem("VRLibrary/TechFloor 铺到 2m 地砖(推荐)")]
    public static void ApplyToDetailTiles()
    {
        var baseMat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        var floorOld = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_Floor.mat");
        if (baseMat == null || floorOld == null)
        { Debug.LogError("[TechFloor] 缺 M_TechFloor 或 M_Floor"); return; }

        var tileMat = CloneWithOneTile(baseMat);
        if (tileMat == null) return;

        var fd = GameObject.Find("Floor_Detailed");
        if (fd == null) { Debug.LogError("[TechFloor] 找不到 Floor_Detailed"); return; }

        int swapped = 0, kept = 0;
        foreach (var r in fd.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!r.name.StartsWith("Tile_")) continue;
            var m = r.sharedMaterial;
            if (m != null && m.name == "M_FloorTileAccent") { kept++; continue; }   // accent 砖保留自己的配色
            Undo.RecordObject(r, "TechFloor 铺砖");
            r.sharedMaterial = tileMat;
            EditorUtility.SetDirty(r);
            swapped++;
        }

        // 主地板退回深色 M_Floor:砖缝要读成暗缝,不能露出地砖的黑边(那会形成第二套线)
        var floor = GameObject.Find("Floor");
        var fr = floor == null ? null : floor.GetComponent<MeshRenderer>();
        if (fr != null)
        {
            Undo.RecordObject(fr, "TechFloor 还原底面");
            fr.sharedMaterial = floorOld;
            EditorUtility.SetDirty(fr);
        }

        AssetDatabase.SaveAssets();
        SaveScene();
        Debug.Log("[TechFloor] 2m 方砖换面 " + swapped + " 块(accent 保留 " + kept + " 块),"
            + "主地板退回 " + (fr == null ? "未找到" : floorOld.name) + "(当砖缝)。"
            + " 回退:git checkout HEAD -- Assets/_Scenes/SmartLibrary.unity 后 openScene");
    }

    // 内置 Cube 每面 UV 就是 0..1 → 一砖一面,平铺必须回到 1×1
    static Material CloneWithOneTile(Material src)
    {
        const string p = "Assets/Materials/M_TechFloorTile.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
        bool fresh = mat == null;
        if (fresh) mat = new Material(src.shader);
        mat.name = "M_TechFloorTile";
        foreach (var prop in new[] { "_BaseMap", "_MetallicGlossMap", "_OcclusionMap", "_BumpMap", "_EmissionMap" })
            mat.SetTexture(prop, src.GetTexture(prop));
        mat.SetFloat("_Metallic", src.GetFloat("_Metallic"));
        mat.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
        mat.SetFloat("_OcclusionStrength", src.GetFloat("_OcclusionStrength"));
        mat.SetFloat("_BumpScale", src.GetFloat("_BumpScale"));
        mat.SetColor("_EmissionColor", src.GetColor("_EmissionColor"));
        mat.shaderKeywords = src.shaderKeywords;
        mat.SetTextureScale("_BaseMap", new Vector2(1f, 1f));
        if (fresh) AssetDatabase.CreateAsset(mat, p);
        else EditorUtility.SetDirty(mat);
        return mat;
    }

    static void SaveScene()
    {
        var s = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(s);
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
    }

    static string Name(Material m, string prop)
    {
        var t = m.GetTexture(prop);
        return t == null ? "空" : t.name;
    }

    static void Enable(Material m, string kw, bool on)
    {
        if (on) m.EnableKeyword(kw); else m.DisableKeyword(kw);
    }

    static void ReportMeshSize()
    {
        var arr = AssetDatabase.LoadAllAssetsAtPath(ModelPath);
        if (arr == null) return;
        foreach (var o in arr)
        {
            var go = o as GameObject;
            if (go == null) continue;
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var b = mf.sharedMesh.bounds;
            var uv = mf.sharedMesh.uv;
            string uvs = "无 UV";
            if (uv.Length > 0)
            {
                float minx = 9f, maxx = -9f, miny = 9f, maxy = -9f;
                foreach (var t in uv)
                {
                    minx = Mathf.Min(minx, t.x); maxx = Mathf.Max(maxx, t.x);
                    miny = Mathf.Min(miny, t.y); maxy = Mathf.Max(maxy, t.y);
                }
                uvs = "UV 范围 x[" + minx.ToString("F2") + "," + maxx.ToString("F2") + "] y["
                      + miny.ToString("F2") + "," + maxy.ToString("F2") + "]";
            }
            Debug.Log("[TechFloor] 网格 " + mf.sharedMesh.name + " 尺寸 " + b.size
                + " 三角 " + (mf.sharedMesh.triangles.Length / 3) + " " + uvs);
        }
    }

    // 量一下场景里现有地板,决定贴图平铺密度
    [MenuItem("VRLibrary/量一下现有地板(给平铺建议)")]
    public static void MeasureFloor()
    {
        foreach (var name in new[] { "Floor", "Floor_Detailed" })
        {
            var go = GameObject.Find(name);
            if (go == null) { Debug.Log("[TechFloor] 场景里没有 " + name + "(或在未打开的场景里)"); continue; }
            var r = go.GetComponent<MeshRenderer>();
            if (r == null) { Debug.Log("[TechFloor] " + name + " 没有 MeshRenderer"); continue; }
            var bb = r.bounds;
            string mats = "";
            foreach (var m in r.sharedMaterials) mats += (m == null ? "null " : m.name + " ");
            Debug.Log("[TechFloor] " + name + " 世界包围盒 " + bb.size + " 中心 " + bb.center
                + " 材质:[" + mats.Trim() + "]"
                + " → 想每块砖 1 m,平铺 " + Mathf.Max(bb.size.x, bb.size.z).ToString("F0"));
        }
    }
}
