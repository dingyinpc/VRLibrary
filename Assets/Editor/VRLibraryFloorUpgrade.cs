using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 智能图书馆——地面精装修(图书馆地编升级)。
/// 菜单:VRLibrary / 精装修地面(图书馆风)
/// 内容:400 块抛光地砖+深色美缝、沿墙踢脚线+霓虹灯条、书架架位区、
///      阅读区地毯(霓虹包边)、服务台地毯、入口到达区、中央同心圆地花、
///      霓虹地面导向线(入口→中央台→各功能区),并把 4 个传送垫抬高到地砖之上。
/// 幂等:重复执行会先删除旧的 Floor_Detailed 再重建。
/// 注意:保留原 Floor 平面(它是传送射线命中的碰撞层,地砖等装饰均无碰撞体)。
/// </summary>
public static class VRLibraryFloorUpgrade
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string RootName = "SmartLibrary";
    const string MatDir = "Assets/Materials";

    [MenuItem("VRLibrary/精装修地面(图书馆风)")]
    public static void UpgradeFloorFromMenu()
    {
        UpgradeFloor();
    }

    public static void UpgradeFloor()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var root = GameObject.Find(RootName);
        if (root == null)
        {
            Debug.LogError("[VRLibraryFloor] 未找到根节点:" + RootName);
            return;
        }

        // ---- 清理旧装饰(保留 Floor 平面 = 传送碰撞层)----
        var oldDetailed = root.transform.Find("Floor_Detailed");
        if (oldDetailed != null) Object.DestroyImmediate(oldDetailed.gameObject);
        var oldGrid = root.transform.Find("FloorGrid");
        if (oldGrid != null) Object.DestroyImmediate(oldGrid.gameObject);

        // ---- 材质 ----
        var neonCyan  = LoadOrCreate("M_NeonCyan.mat",    new Color(0f, 0f, 0f), 0f, 0f,    new Color(0f, 2.4f, 3f));
        var neonMag   = LoadOrCreate("M_NeonMagenta.mat", new Color(0f, 0f, 0f), 0f, 0f,    new Color(2.6f, 0.4f, 1.8f));
        var tile      = LoadOrCreate("M_FloorTile.mat",        new Color(0.055f, 0.062f, 0.078f), 0.45f, 0.60f, null);
        var tileAcc   = LoadOrCreate("M_FloorTileAccent.mat",  new Color(0.045f, 0.050f, 0.065f), 0.50f, 0.55f, null);
        var grout     = LoadOrCreate("M_FloorGrout.mat",       new Color(0.016f, 0.019f, 0.032f), 0.20f, 0.30f, null);
        var baseb     = LoadOrCreate("M_Baseboard.mat",        new Color(0.022f, 0.026f, 0.042f), 0.35f, 0.40f, null);
        var carpet    = LoadOrCreate("M_CarpetReading.mat",    new Color(0.050f, 0.105f, 0.165f), 0f, 0.25f, null);
        var shelfZone = LoadOrCreate("M_ShelfZonePad.mat",     new Color(0.030f, 0.036f, 0.054f), 0.10f, 0.35f, null);
        var inlay     = LoadOrCreate("M_FloorInlayDark.mat",   new Color(0.020f, 0.024f, 0.040f), 0.30f, 0.45f, null);

        // ---- 新建地面装饰层级 ----
        var holder = new GameObject("Floor_Detailed");
        holder.transform.SetParent(root.transform, false);
        var f = holder.transform;

        BuildGrout(f, grout);                                        // 美缝底台(覆盖全地板)
        BuildTiles(f, tile, tileAcc);                                // 400 块 2m x 2m 地砖
        BuildBaseboards(f, baseb, neonCyan);                         // 四面踢脚线 + 霓虹灯条
        BuildGuideLines(f, neonCyan);                                // 导向地灯
        BuildKioskInlay(f, inlay, neonCyan, neonMag);                // 中央同心圆地花
        BuildEntryInlay(f, inlay, neonCyan);                         // 入口到达区
        BuildShelfZones(f, shelfZone);                               // 书架架位区
        BuildReadingCarpet(f, carpet, neonCyan);                     // 阅读区地毯
        BuildServiceMat(f, carpet, neonMag);                         // 服务台地毯

        // ---- 抬高 4 个传送垫,避免被新地砖埋住 ----
        foreach (var padName in new[] { "Pad_Entry", "Pad_Shelves", "Pad_Reading", "Pad_Service" })
        {
            var pad = root.transform.Find(padName);
            if (pad != null)
            {
                var p = pad.position;
                pad.position = new Vector3(p.x, 0.036f, p.z);
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[VRLibraryFloor] 图书馆地面精装修完成(Floor_Detailed),场景已保存。");
    }

    // ------------------------------------------------------------ 构建

    static void BuildGrout(Transform f, Material mat)
    {
        Box(f, "GroutBase", new Vector3(0f, 0.004f, 0f), new Vector3(40f, 0.008f, 40f), mat);
    }

    static void BuildTiles(Transform f, Material tile, Material tileAcc)
    {
        var tiles = NewChild(f, "Tiles");
        for (int ix = 0; ix < 20; ix++)
        {
            for (int iz = 0; iz < 20; iz++)
            {
                float cx = -19f + ix * 2f;
                float cz = -19f + iz * 2f;
                bool accent = (ix * 7 + iz * 11) % 43 == 0; // 稀疏深色点缀砖
                Box(tiles, "Tile_" + ix + "_" + iz,
                    new Vector3(cx, 0.018f, cz), new Vector3(1.96f, 0.02f, 1.96f),
                    accent ? tileAcc : tile, true, true);
            }
        }
    }

    static void BuildBaseboards(Transform f, Material body, Material led)
    {
        var bb = NewChild(f, "Baseboards");
        float y = 0.06f, h = 0.12f, d = 0.04f, half = 19.6f;
        // 主体:沿四面墙内壁
        Box(bb, "BB_N", new Vector3(0f, y, -half + 0.02f), new Vector3(39.2f, h, d), body);
        Box(bb, "BB_S", new Vector3(0f, y, half - 0.02f),  new Vector3(39.2f, h, d), body);
        Box(bb, "BB_E", new Vector3(half - 0.02f, y, 0f), new Vector3(d, h, 39.2f), body);
        Box(bb, "BB_W", new Vector3(-half + 0.02f, y, 0f), new Vector3(d, h, 39.2f), body);
        // 顶部霓虹灯条(朝室内)
        float ly = 0.126f;
        Box(bb, "LED_N", new Vector3(0f, ly, -half + 0.055f), new Vector3(39.2f, 0.012f, 0.02f), led);
        Box(bb, "LED_S", new Vector3(0f, ly, half - 0.055f), new Vector3(39.2f, 0.012f, 0.02f), led);
        Box(bb, "LED_E", new Vector3(half - 0.055f, ly, 0f), new Vector3(0.02f, 0.012f, 39.2f), led);
        Box(bb, "LED_W", new Vector3(-half + 0.055f, ly, 0f), new Vector3(0.02f, 0.012f, 39.2f), led);
    }

    static void BuildGuideLines(Transform f, Material mat)
    {
        var g = NewChild(f, "GuideLines");
        // 外圈轮廓(内缩 0.5m)
        Box(g, "GL_Perimeter_N", new Vector3(0f, 0.031f, -19.5f), new Vector3(39f, 0.006f, 0.05f), mat);
        Box(g, "GL_Perimeter_S", new Vector3(0f, 0.031f, 19.5f), new Vector3(39f, 0.006f, 0.05f), mat);
        Box(g, "GL_Perimeter_E", new Vector3(19.5f, 0.031f, 0f), new Vector3(0.05f, 0.006f, 39f), mat);
        Box(g, "GL_Perimeter_W", new Vector3(-19.5f, 0.031f, 0f), new Vector3(0.05f, 0.006f, 39f), mat);
        // 入口 -> 中央台(双线)
        Box(g, "GL_Entry_L", new Vector3(0.85f, 0.031f, 1f), new Vector3(0.05f, 0.006f, 8f), mat);
        Box(g, "GL_Entry_R", new Vector3(-0.85f, 0.031f, 1f), new Vector3(0.05f, 0.006f, 8f), mat);
        // 中央台 -> 书架区
        Box(g, "GL_Shelves", new Vector3(-8.5f, 0.031f, 0f), new Vector3(12.4f, 0.006f, 0.05f), mat);
        // 中央台 -> 阅读区(止于地毯边缘)
        Box(g, "GL_Reading", new Vector3(5.65f, 0.031f, 0.6f), new Vector3(6.4f, 0.006f, 0.05f), mat);
        // 中央台 -> 服务台
        Box(g, "GL_Service", new Vector3(0f, 0.031f, -8.8f), new Vector3(0.05f, 0.006f, 10f), mat);
    }

    static void BuildKioskInlay(Transform f, Material inlay, Material cyan, Material magenta)
    {
        var k = NewChild(f, "KioskInlay");
        Disc(k, "KI_Base", Vector3.zero, 4.2f, inlay, 0.028f, 0.006f);
        Disc(k, "KI_Cyan", Vector3.zero, 3.4f, cyan, 0.029f, 0.006f);
        Disc(k, "KI_Magenta", Vector3.zero, 2.4f, magenta, 0.030f, 0.006f);
        Disc(k, "KI_CyanCore", Vector3.zero, 1.4f, cyan, 0.031f, 0.006f);
    }

    static void BuildEntryInlay(Transform f, Material inlay, Material cyan)
    {
        var e = NewChild(f, "EntryInlay");
        Disc(e, "EI_Base", new Vector3(0f, 0f, 10f), 2.4f, inlay, 0.028f, 0.006f);
        Disc(e, "EI_Cyan", new Vector3(0f, 0f, 10f), 1.6f, cyan, 0.029f, 0.006f);
    }

    static void BuildShelfZones(Transform f, Material mat)
    {
        var s = NewChild(f, "ShelfZones");
        foreach (var z in new[] { -9f, -3f, 3f, 9f })
        {
            var name = "SZ_" + ((int)z).ToString();
            if (z > 0) name = "SZ_+" + (int)z;
            Box(s, name, new Vector3(-17.5f, 0.032f, z), new Vector3(2.6f, 0.008f, 6.6f), mat);
        }
    }

    static void BuildReadingCarpet(Transform f, Material carpet, Material trim)
    {
        var c = NewChild(f, "ReadingCarpet");
        Box(c, "Carpet", new Vector3(13f, 0.033f, 0f), new Vector3(7.4f, 0.008f, 18.6f), carpet);
        // 霓虹包边
        Box(c, "Trim_W", new Vector3(9.33f, 0.0395f, 0f), new Vector3(0.035f, 0.005f, 18.72f), trim);
        Box(c, "Trim_E", new Vector3(16.67f, 0.0395f, 0f), new Vector3(0.035f, 0.005f, 18.72f), trim);
        Box(c, "Trim_N", new Vector3(13f, 0.0395f, -9.36f), new Vector3(7.34f, 0.005f, 0.035f), trim);
        Box(c, "Trim_S", new Vector3(13f, 0.0395f, 9.36f), new Vector3(7.34f, 0.005f, 0.035f), trim);
    }

    static void BuildServiceMat(Transform f, Material carpet, Material magenta)
    {
        var m = NewChild(f, "ServiceMat");
        Box(m, "Mat", new Vector3(0f, 0.033f, -16f), new Vector3(4.4f, 0.008f, 3.6f), carpet);
        Box(m, "AccentLine", new Vector3(0f, 0.0395f, -15.05f), new Vector3(3.4f, 0.005f, 0.05f), magenta);
    }

    // ------------------------------------------------------------ 工具

    static Transform NewChild(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    /// <summary>创建盒子(localPosition.y 即盒体中心高度)。装饰物默认移除碰撞体。</summary>
    static Transform Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat,
        bool removeCollider = true, bool castShadow = false)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = size;
        if (removeCollider)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
        }
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = castShadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            mr.receiveShadows = true;
        }
        go.isStatic = true;
        return go.transform;
    }

    /// <summary>创建扁平圆柱盘(radius 为实际半径,baseY 为盘底高度,顶部朝上)。装饰物无碰撞体。</summary>
    static void Disc(Transform parent, string name, Vector3 center, float radius, Material mat,
        float baseY, float height)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(center.x, baseY + height * 0.5f, center.z);
        go.transform.localScale = new Vector3(2f * radius, height * 0.5f, 2f * radius);
        var col = go.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
        }
        go.isStatic = true;
    }

    static Material LoadOrCreate(string fileName, Color color, float metallic, float smoothness, Color? emission)
    {
        if (!AssetDatabase.IsValidFolder(MatDir))
            AssetDatabase.CreateFolder("Assets", "Materials");
        string path = MatDir + "/" + fileName;
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            name = Path.GetFileNameWithoutExtension(fileName)
        };
        mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
        if (emission.HasValue)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emission.Value);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}