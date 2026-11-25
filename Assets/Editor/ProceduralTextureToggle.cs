using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 高科技图书馆 · 程序化贴图系统(高精度 512~1024,导出为支持硬件 ASTC 压缩的 PNG)
/// 菜单:VRLibrary/程序化贴图/应用全部 | 移除全部(恢复纯色) | 迁移.asset为ASTC压缩PNG
/// 覆盖:地板(电路板砖)、墙面(饰面板+蚀刻线)、吊顶(穿孔板)、书架(蚀刻横槽)、
///       金属(拉丝+斜纹)、铭牌(角标铭牌板)、书脊(纸纹)。
/// 所有纹理确定性生成(种子固定),保存在 Assets/Textures/Procedural/*.png。
/// </summary>
public static class ProceduralTextureToggle
{
    const string kTexDir = "Assets/Textures/Procedural";
    const string kLegacyDir = "Assets/Materials";

    struct TexSpec
    {
        public string matPath;      // 目标材质
        public string texName;      // 纹理资产名
        public string kind;         // 生成器类型
        public Vector2 tiling;
        public float smoothRemove;  // 移除后恢复的光泽度
        public TexSpec(string m, string t, string k, Vector2 til, float sm) { matPath = m; texName = t; kind = k; tiling = til; smoothRemove = sm; }
    }

    static readonly TexSpec[] Specs = {
        new TexSpec("Assets/Materials/M_Floor.mat",       "T_TechFloor",  "floor",   new Vector2(5, 5),  0.3f),
        new TexSpec("Assets/Materials/M_Wall.mat",        "T_TechWall",   "wall",    new Vector2(8, 1),  0.25f),
        new TexSpec("Assets/Materials/M_Ceiling.mat",     "T_TechCeil",   "ceil",    new Vector2(1, 1),  0.5f),
        new TexSpec("Assets/Materials/M_Shelf.mat",       "T_TechShelf",  "shelf",   new Vector2(2, 1),  0.2f),
        new TexSpec("Assets/Materials/M_Metal.mat",       "T_MetalBrush", "metal",   new Vector2(1, 3),  0.55f),
        new TexSpec("Assets/Materials/M_LabelPlate.mat",  "T_TechPlate",  "plate",   new Vector2(1, 1),  0.6f),
        new TexSpec("Assets/Materials/M_Book0.mat",       "T_BookGrain",  "book",    new Vector2(1, 1),  0.5f),
    };
    // 其余书脊配色材质共用同一张纸纹
    static readonly string[] ExtraBookMats = {
        "Assets/Materials/M_Book1.mat", "Assets/Materials/M_Book2.mat", "Assets/Materials/M_Book3.mat",
        "Assets/Materials/M_Book4.mat", "Assets/Materials/M_Book5.mat", "Assets/Materials/M_Book6.mat",
        "Assets/Materials/M_Book7.mat", "Assets/Materials/M_Book8.mat",
    };

    [MenuItem("VRLibrary/程序化贴图/应用全部(高科技风)")]
    public static void ApplyAll()
    {
        int n = 0;
        foreach (var spec in Specs) {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(spec.matPath);
            if (mat == null) continue;
            var tex = EnsureTexture(spec.texName, spec.kind);
            SetMatTex(mat, tex, spec.tiling);
            EditorUtility.SetDirty(mat);
            n++;
        }
        foreach (string mp in ExtraBookMats) {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mat == null) continue;
            var tex = EnsureTexture("T_BookGrain", "book");
            SetMatTex(mat, tex, new Vector2(1, 1));
            EditorUtility.SetDirty(mat);
            n++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[程序化贴图] 高科技风纹理已应用到 " + n + " 个材质(地板电路砖/饰面板墙/穿孔吊顶/蚀刻书架/拉丝金属/角标铭牌/书面纸纹)。");
    }

    [MenuItem("VRLibrary/程序化贴图/迁移.asset为ASTC压缩PNG(省内存+包体)")]
    public static void MigrateAssetsToCompressedPng()
    {
        ApplyAll();
        string[] legacyAssets =
        {
            "T_TechFloor.asset", "T_TechWall.asset", "T_TechCeil.asset",
            "T_TechShelf.asset", "T_MetalBrush.asset", "T_TechPlate.asset",
            "T_BookGrain.asset", "T_FloorNoise.asset", "T_ShelfNoise.asset", "T_WallStreak.asset"
        };
        int deleted = 0;
        foreach (var f in legacyAssets)
        {
            string p = kLegacyDir + "/" + f;
            if (AssetDatabase.LoadAssetAtPath<Object>(p) != null && AssetDatabase.DeleteAsset(p))
                deleted++;
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[程序化贴图] 已迁移并压缩为 Assets/Textures/Procedural/*.png (ASTC_6x6, isReadable=0), 删除未压缩旧 .asset " + deleted + " 个。");
    }

    static void SetMatTex(Material mat, Texture2D tex, Vector2 tiling)
    {
        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap", tex);
            mat.SetTextureScale("_BaseMap", tiling);
        }
        if (mat.HasProperty("_MainTex"))
        {
            mat.SetTexture("_MainTex", tex);
            mat.SetTextureScale("_MainTex", tiling);
        }
    }

    [MenuItem("VRLibrary/程序化贴图/移除全部(恢复纯色)")]
    public static void RemoveAll()
    {
        int n = 0;
        foreach (var spec in Specs) {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(spec.matPath);
            if (mat == null) continue;
            mat.SetTexture("_BaseMap", null);
            mat.SetTextureScale("_BaseMap", Vector2.one);
            mat.SetFloat("_Smoothness", spec.smoothRemove);
            EditorUtility.SetDirty(mat);
            n++;
        }
        foreach (string mp in ExtraBookMats) {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mat == null) continue;
            mat.SetTexture("_BaseMap", null);
            EditorUtility.SetDirty(mat);
            n++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[程序化贴图] 已移除,恢复纯色。纹理资产保留在 " + kTexDir + ",可随时重新应用。");
    }

    [MenuItem("VRLibrary/程序化贴图/改三线性过滤(修远处块状)")]
    public static void FixFiltering()
    {
        var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { kTexDir });
        int changed = 0;
        foreach (var g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) continue;
            bool need = tex.filterMode != FilterMode.Trilinear || tex.anisoLevel < 8;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 8;
            if (tex.mipmapCount < 2)
                Debug.LogWarning("[程序化贴图] " + tex.name + " 没有 mip 链(远处只能靠放大低层撑),距离越远越糊 —— 生成时 mipChain 要开");
            if (need) { EditorUtility.SetDirty(tex); changed++; }
            Debug.Log("[程序化贴图] " + tex.name + " " + tex.width + "x" + tex.height
                + " mip=" + tex.mipmapCount + " 格式=" + tex.format
                + " 过滤=" + tex.filterMode + " 各向异性=" + tex.anisoLevel
                + " 未压缩体积≈" + (new System.IO.FileInfo(path).Length / 1024) + "KB");
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[程序化贴图] 存量过滤已修:" + changed + "/" + guids.Length + " 张改为三线性。");
    }

    // ------------------------------------------------ 纹理生成

    static Texture2D EnsureTexture(string name, string kind)
    {
        string pngAssetPath = kTexDir + "/" + name + ".png";
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(pngAssetPath);
        if (existing != null)
        {
            ConfigureImporter(pngAssetPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(pngAssetPath);
        }

        // 优先从旧的 .asset 拷贝原始像素(保证 100% 像素零差异),找不到再确定性生成
        Texture2D tex = null;
        string legacyAssetPath = kLegacyDir + "/" + name + ".asset";
        var legacyTex = AssetDatabase.LoadAssetAtPath<Texture2D>(legacyAssetPath);
        if (legacyTex != null && legacyTex.isReadable)
        {
            tex = new Texture2D(legacyTex.width, legacyTex.height, TextureFormat.RGBA32, false);
            tex.SetPixels32(legacyTex.GetPixels32());
            tex.Apply();
        }
        else
        {
            tex = Generate(name, kind);
        }
        byte[] png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);

        string absDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), kTexDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(absDir);
        string absFile = Path.Combine(absDir, name + ".png");
        File.WriteAllBytes(absFile, png);

        AssetDatabase.ImportAsset(pngAssetPath, ImportAssetOptions.ForceUpdate);
        ConfigureImporter(pngAssetPath);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(pngAssetPath);
    }

    static void ConfigureImporter(string assetPath)
    {
        var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (ti == null) return;
        bool dirty = false;
        if (ti.textureType != TextureImporterType.Default) { ti.textureType = TextureImporterType.Default; dirty = true; }
        if (!ti.sRGBTexture) { ti.sRGBTexture = true; dirty = true; }
        if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; dirty = true; }
        if (ti.filterMode != FilterMode.Trilinear) { ti.filterMode = FilterMode.Trilinear; dirty = true; }
        if (ti.anisoLevel != 8) { ti.anisoLevel = 8; dirty = true; }
        if (ti.wrapMode != TextureWrapMode.Repeat) { ti.wrapMode = TextureWrapMode.Repeat; dirty = true; }
        if (ti.isReadable) { ti.isReadable = false; dirty = true; }
        if (ti.maxTextureSize != 1024) { ti.maxTextureSize = 1024; dirty = true; }
        if (ti.textureCompression != TextureImporterCompression.Compressed) { ti.textureCompression = TextureImporterCompression.Compressed; dirty = true; }

        var and = ti.GetPlatformTextureSettings("Android");
        if (!and.overridden || and.maxTextureSize != 1024 || and.format != TextureImporterFormat.ASTC_6x6)
        {
            and.overridden = true;
            and.maxTextureSize = 1024;
            and.format = TextureImporterFormat.ASTC_6x6;
            ti.SetPlatformTextureSettings(and);
            dirty = true;
        }
        if (dirty) ti.SaveAndReimport();
    }

    static Texture2D NewTex(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
        tex.wrapMode = TextureWrapMode.Repeat;
        // 带 mip 链却用 Bilinear 时,远处会在相邻 mip 层之间硬跳,看起来就是"比马赛克高一级"的软方块。
        tex.filterMode = FilterMode.Trilinear;
        tex.anisoLevel = 8;
        return tex;
    }

    static Texture2D Generate(string name, string kind)
    {
        if (kind == "floor") return GenFloor(name);
        if (kind == "wall") return GenWall(name);
        if (kind == "ceil") return GenCeil(name);
        if (kind == "shelf") return GenShelf(name);
        if (kind == "metal") return GenMetal(name);
        if (kind == "plate") return GenPlate(name);
        return GenBookGrain(name);
    }

    static Color Clamp(Color c) { return new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f); }

    /// <summary>地板:4×4 大板,双线砖缝 + 四角铆钉 + 电路走线(随机 L 形细线 + 端点节点)</summary>
    static Texture2D GenFloor(string name)
    {
        int size = 1024, tile = size / 4;
        var tex = NewTex(size);
        var bg = new Color(0.05f, 0.062f, 0.082f);
        var rand = new System.Random(11);
        var px = new Color[size * size];
        for (int i = 0; i < px.Length; i++) {
            float d = (float)(rand.NextDouble() - 0.5) * 0.05f;
            px[i] = new Color(bg.r + d, bg.g + d, bg.b + d, 1f);
        }
        for (int ty = 0; ty < 4; ty++) {
            for (int tx = 0; tx < 4; tx++) {
                int x0 = tx * tile, y0 = ty * tile;
                // 双线砖缝
                for (int k = 0; k < 2; k++) {
                    for (int a = 0; a < tile; a++) {
                        Dim(px, size, x0 + a, y0 + k, 0.55f);
                        Dim(px, size, x0 + a, y0 + tile - 1 - k, 0.55f);
                        Dim(px, size, x0 + k, y0 + a, 0.55f);
                        Dim(px, size, x0 + tile - 1 - k, y0 + a, 0.55f);
                    }
                }
                // 四角铆钉
                Dot(px, size, x0 + 14, y0 + 14, 3, new Color(0.28f, 0.34f, 0.42f));
                Dot(px, size, x0 + tile - 14, y0 + 14, 3, new Color(0.28f, 0.34f, 0.42f));
                Dot(px, size, x0 + 14, y0 + tile - 14, 3, new Color(0.28f, 0.34f, 0.42f));
                Dot(px, size, x0 + tile - 14, y0 + tile - 14, 3, new Color(0.28f, 0.34f, 0.42f));
                // 电路走线:1~2 条 L 形线 + 端点节点
                int traces = rand.Next(1, 3);
                for (int t = 0; t < traces; t++) {
                    int sx = x0 + rand.Next(40, tile - 40), sy = y0 + rand.Next(40, tile - 40);
                    int len1 = rand.Next(60, tile / 2), dir = rand.Next(4);
                    int dx = dir == 0 ? 1 : dir == 1 ? -1 : 0, dy = dir == 2 ? 1 : dir == 3 ? -1 : 0;
                    for (int a = 0; a < len1; a++) {
                        int cx = sx + dx * a, cy = sy + dy * a;
                        if (cx < x0 + 8 || cx >= x0 + tile - 8 || cy < y0 + 8 || cy >= y0 + tile - 8) break;
                        Dim(px, size, cx, cy, 0.7f);
                        if (a == len1 - 1) Dot(px, size, cx, cy, 3, new Color(0.10f, 0.13f, 0.17f));
                    }
                }
            }
        }
        SetAll(tex, px);
        return tex;
    }

    /// <summary>墙面:大饰面板 + 中缝 + 水平蚀刻线带节点 + 竖向拉丝</summary>
    static Texture2D GenWall(string name)
    {
        int size = 1024;
        var tex = NewTex(size);
        var bg = new Color(0.07f, 0.08f, 0.10f);
        var rand = new System.Random(21);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++) {
            float colShift = ((y * 131) % 89) / 89f - 0.5f;
            for (int x = 0; x < size; x++) {
                float d = (float)(rand.NextDouble() - 0.5) * 0.035f + colShift * 0.02f;
                px[y * size + x] = new Color(bg.r + d, bg.g + d, bg.b + d, 1f);
            }
        }
        // 中缝(双线)
        for (int k = 0; k < 2; k++)
            for (int y = 0; y < size; y++) { Dim(px, size, size / 2 + k, y, 0.55f); Dim(px, size, size / 2 - 1 - k, y, 0.55f); }
        // 水平蚀刻线 y=340/700 + 节点方块
        foreach (int ly in new[] { 340, 700 }) {
            for (int x = 0; x < size; x++) Dim(px, size, x, ly, 0.72f);
            for (int x = 64; x < size; x += 256) Dot(px, size, x, ly, 5, new Color(0.11f, 0.14f, 0.18f));
        }
        // 四角角标(亮 L 形)
        Corner(px, size, 20, 20, 1, 1, 0.25f);
        Corner(px, size, size - 20, 20, -1, 1, 0.25f);
        Corner(px, size, 20, size - 20, 1, -1, 0.25f);
        Corner(px, size, size - 20, size - 20, -1, -1, 0.25f);
        SetAll(tex, px);
        return tex;
    }

    /// <summary>吊顶:穿孔微孔板 + 边框槽 + 中央十字槽</summary>
    static Texture2D GenCeil(string name)
    {
        int size = 512;
        var tex = NewTex(size);
        var bg = new Color(0.10f, 0.11f, 0.13f);
        var px = new Color[size * size];
        for (int i = 0; i < px.Length; i++) px[i] = bg;
        for (int y = 8; y < size - 8; y += 16)
            for (int x = 8; x < size - 8; x += 16)
                Dot(px, size, x, y, 2, new Color(0.055f, 0.06f, 0.075f));
        for (int k = 0; k < 3; k++) {
            for (int a = 0; a < size; a++) {
                Dim(px, size, a, k, 0.6f); Dim(px, size, a, size - 1 - k, 0.6f);
                Dim(px, size, k, a, 0.6f); Dim(px, size, size - 1 - k, a, 0.6f);
            }
        }
        for (int a = 0; a < size; a++) { Dim(px, size, a, size / 2, 0.7f); Dim(px, size, size / 2, a, 0.7f); }
        SetAll(tex, px);
        return tex;
    }

    /// <summary>书架:横槽蚀刻 + 细噪声</summary>
    static Texture2D GenShelf(string name)
    {
        int size = 512;
        var tex = NewTex(size);
        var bg = new Color(0.10f, 0.11f, 0.13f);
        var rand = new System.Random(31);
        var px = new Color[size * size];
        for (int i = 0; i < px.Length; i++) {
            float d = (float)(rand.NextDouble() - 0.5) * 0.05f;
            px[i] = new Color(bg.r + d, bg.g + d, bg.b + d, 1f);
        }
        for (int ly = 64; ly < size; ly += 96)
            for (int x = 0; x < size; x++) Dim(px, size, x, ly, 0.7f);
        SetAll(tex, px);
        return tex;
    }

    /// <summary>金属:横向拉丝 + 稀疏斜纹</summary>
    static Texture2D GenMetal(string name)
    {
        int size = 512;
        var tex = NewTex(size);
        var bg = new Color(0.12f, 0.13f, 0.16f);
        var rand = new System.Random(41);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++) {
            float rowShift = ((y * 331) % 71) / 71f - 0.5f;
            for (int x = 0; x < size; x++) {
                float d = (float)(rand.NextDouble() - 0.5) * 0.05f + rowShift * 0.03f;
                px[y * size + x] = new Color(bg.r + d, bg.g + d, bg.b + d, 1f);
            }
        }
        for (int i = 0; i < size; i += 64)
            for (int a = 0; a < size; a++) {
                int x = a + i; if (x >= size) x -= size;
                Dim(px, size, x, a, 0.82f);
            }
        SetAll(tex, px);
        return tex;
    }

    /// <summary>铭牌:深底 + 四角亮角标 + 细网格</summary>
    static Texture2D GenPlate(string name)
    {
        int size = 512;
        var tex = NewTex(size);
        var bg = new Color(0.02f, 0.045f, 0.08f);
        var px = new Color[size * size];
        for (int i = 0; i < px.Length; i++) px[i] = bg;
        for (int a = 0; a < size; a += 64)
            for (int b = 0; b < size; b++) { Dim(px, size, a, b, 0.85f); Dim(px, size, b, a, 0.85f); }
        Corner(px, size, 26, 26, 1, 1, 0.14f);
        Corner(px, size, size - 26, 26, -1, 1, 0.14f);
        Corner(px, size, 26, size - 26, 1, -1, 0.14f);
        Corner(px, size, size - 26, size - 26, -1, -1, 0.14f);
        SetAll(tex, px);
        return tex;
    }

    /// <summary>书面:细竖向纸纹</summary>
    static Texture2D GenBookGrain(string name)
    {
        int size = 256;
        var tex = NewTex(size);
        var px = new Color[size * size];
        var rand = new System.Random(51);
        for (int x = 0; x < size; x++) {
            float col = ((x * 577) % 83) / 83f - 0.5f;
            for (int y = 0; y < size; y++) {
                float d = (float)(rand.NextDouble() - 0.5) * 0.05f + col * 0.04f;
                px[y * size + x] = new Color(0.5f + d, 0.5f + d, 0.5f + d, 1f);
            }
        }
        SetAll(tex, px);
        return tex;
    }

    // 像素工具
    static void Dim(Color[] px, int size, int x, int y, float k) {
        x = (x + size) % size; y = (y + size) % size;
        var c = px[y * size + x];
        px[y * size + x] = new Color(c.r * k, c.g * k, c.b * k, 1f);
    }
    static void Dot(Color[] px, int size, int x, int y, int r, Color c) {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++) {
                if (dx * dx + dy * dy > r * r) continue;
                int xx = (x + dx + size) % size, yy = (y + dy + size) % size;
                px[yy * size + xx] = Clamp(new Color(c.r, c.g, c.b, 1f));
            }
    }
    static void Corner(Color[] px, int size, int x, int y, int sx, int sy, float bright) {
        // bright <1 = 压暗蚀刻;>1 无意义,取 0.2 表示亮蚀刻时用减色
        for (int a = 0; a < 80; a++) {
            Dim(px, size, x + sx * a, y, bright);
            Dim(px, size, x, y + sy * a, bright);
        }
    }
    static void SetAll(Texture2D tex, Color[] px) {
        tex.SetPixels(px); tex.Apply();
    }
}
