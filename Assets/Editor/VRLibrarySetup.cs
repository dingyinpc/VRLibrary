using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Object = UnityEngine.Object;

/// <summary>
/// 智能图书馆(VRlibrary)一键搭建脚本。
/// 通过菜单 VRLibrary/一键搭建智能图书馆场景 或 batch 模式 -executeMethod 执行。
/// 内容:高科技风图书馆场景(地面网格/书架区/阅读区/服务台/中央信息屏)
/// + XR Interaction Setup 玩家端 + 传送区/锚点 + PICO Manager + URP 泛光后处理。
/// </summary>
public static class VRLibrarySetup
{
    const string kScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string kSetupPrefab =
        "Assets/Samples/XR Interaction Toolkit/2.6.5/Starter Assets/Prefabs/XR Interaction Setup.prefab";

    [MenuItem("VRLibrary/一键搭建智能图书馆场景")]
    public static void BuildAllFromMenu()
    {
        BuildAll();
    }

    public static void BuildAll()
    {
        try
        {
            ConfigurePipeline();
            ConfigureXR();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildScene();

            if (!Directory.Exists("Assets/_Scenes"))
                AssetDatabase.CreateFolder("Assets", "_Scenes");
            EditorSceneManager.SaveScene(scene, kScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(kScenePath, true) };

            PlayerSettings.companyName = "VRLibrary";
            PlayerSettings.productName = "VRlibrary";
            AssetDatabase.SaveAssets();

            Debug.Log($"[VRLibrary] 场景搭建完成并已保存:{kScenePath}(已加入 Build Settings)");
        }
        catch (Exception e)
        {
            Debug.LogError("[VRLibrary] 场景搭建失败:" + e);
            throw;
        }
    }

    /// <summary>为已生成的场景补挂 PXR_Manager(不重建场景)。</summary>
    public static void EnsurePXRManager()
    {
        try
        {
            var scene = EditorSceneManager.OpenScene(kScenePath, OpenSceneMode.Single);
            var setup = GameObject.Find("SmartLibrary/XR Interaction Setup");
            if (setup == null) setup = GameObject.Find("XR Interaction Setup");
            if (setup == null) { Debug.LogError("[VRLibrary] 场景中找不到 XR Interaction Setup。"); return; }

            var origin = setup.GetComponentInChildren<XROrigin>();
            if (origin == null) { Debug.LogError("[VRLibrary] 找不到 XR Origin。"); return; }

            var pxrType = Type.GetType("Unity.XR.PXR.PXR_Manager, Unity.XR.PICO");
            if (pxrType == null) { Debug.LogError("[VRLibrary] 找不到类型 Unity.XR.PXR.PXR_Manager。"); return; }

            if (origin.GetComponent(pxrType) == null)
                Undo.AddComponent(origin.gameObject, pxrType);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[VRLibrary] PXR_Manager 已挂载到 XR Origin 并保存场景。");
        }
        catch (Exception e)
        {
            Debug.LogError("[VRLibrary] EnsurePXRManager 失败:" + e);
        }
    }

    /// <summary>切换到 Android 构建目标(PICO),反射调用以规避版本 API 差异。</summary>
    public static void SwitchToAndroid()
    {
        try
        {
            var mi = typeof(EditorUserBuildSettings).GetMethod("SwitchBuildTarget",
                new[] { typeof(BuildTargetGroup), typeof(BuildTarget) });
            if (mi != null)
            {
                mi.Invoke(null, new object[] { BuildTargetGroup.Android, BuildTarget.Android });
                Debug.Log("[VRLibrary] 已切换到 Android 构建目标(PICO)。");
            }
            else
            {
                Debug.LogWarning("[VRLibrary] 未找到 SwitchBuildTarget API,请在编辑器中手动切换到 Android 平台。");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[VRLibrary] 切换 Android 失败:" + e);
        }
    }

    // ---------------------------------------------------------------- 配置

    static void ConfigurePipeline()
    {
        var urp = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>("Assets/Settings/URP-Balanced.asset");
        if (urp != null)
        {
            if (GraphicsSettings.defaultRenderPipeline != urp)
                GraphicsSettings.defaultRenderPipeline = urp;

            // 确保开启 HDR,泛光(Bloom)效果需要 HDR
            var so = new SerializedObject(urp);
            var hdr = so.FindProperty("m_HDR");
            if (hdr != null && !hdr.boolValue)
            {
                hdr.boolValue = true;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            // 所有质量等级都指向 URP
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
            }
            QualitySettings.SetQualityLevel(current, false);
        }
        else
        {
            Debug.LogWarning("[VRLibrary] 未找到 Assets/Settings/URP-Balanced.asset,请检查 URP 设置。");
        }
    }

    static void ConfigureXR()
    {
        // 复用旧项目拷贝过来的 XR 配置资产(GUID 未变),注册回 EditorBuildSettings
        RegisterConfigObject("com.unity.xr.management.loader_settings", "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
        RegisterConfigObject("Unity.XR.PXR.Settings", "Assets/XR/Settings/PXR_Settings.asset");
        RegisterConfigObject("com.unity.xr.openxr.settings4", "Assets/XR/Settings/OpenXR Package Settings.asset");
    }

    static void RegisterConfigObject(string key, string path)
    {
        var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
        if (obj == null)
        {
            Debug.LogWarning($"[VRLibrary] 找不到 XR 配置资产:{path}");
            return;
        }
        EditorBuildSettings.AddConfigObject(key, obj, true);
        Debug.Log($"[VRLibrary] 已注册 XR 配置:{key} <- {path}");
    }

    // ---------------------------------------------------------------- 场景

    static void BuildScene()
    {
        EnsureFolder("Assets/Materials");
        EnsureFolder("Assets/_Scenes");

        var root = new GameObject("SmartLibrary");

        // —— 材质 ——
        var matFloor   = MakeMat("M_Floor",   new Color(0.045f, 0.055f, 0.075f), 0.55f, 0.35f);
        var matWall    = MakeMat("M_Wall",    new Color(0.070f, 0.080f, 0.100f), 0.30f, 0.25f);
        var matShelf   = MakeMat("M_Shelf",   new Color(0.100f, 0.110f, 0.130f), 0.60f, 0.40f);
        var matMetal   = MakeMat("M_Metal",   new Color(0.120f, 0.130f, 0.160f), 0.85f, 0.55f);
        // ⚠ 下面这些 _EmissionColor 数值**就是 VRLibrary/灯色 替换后的磁盘终值**(照抄精度)。
        //    改灯色请走 VRLibrary/灯色 菜单,然后把新值回写到这里;
        //    只改一处会出现"跑一次重建就把全场灯色冲回旧配色"或"替换完重建又被改回来"。
        var matCyan    = MakeMat("M_NeonCyan",    Color.black, 0f, 0f, new Color(1.0140814f, 1.681685f, 1.7746425f));
        var matMagenta = MakeMat("M_NeonMagenta", Color.black, 0f, 0f, new Color(1.0356474f, 0.7558556f, 0.8686075f));
        var matPad     = MakeMat("M_TeleportPad", Color.black, 0f, 0f, new Color(0.8634811f, 1.4319395f, 1.511092f));
        var matLightBar= MakeMat("M_LightBar",    Color.black, 0f, 0f, new Color(1.5384961f, 1.8421468f, 1.963607f));
        var matGrid    = MakeMat("M_GridLine",    Color.black, 0f, 0f, new Color(0.9238933f, 1.532123f, 1.6168132f));
        var matScreen  = MakeMat("M_ScreenDark",  new Color(0.02f, 0.04f, 0.07f), 0.2f, 0.6f, new Color(0.09152192f, 0.15177384f, 0.16016336f));

        var bookMats = new Material[6];
        Color[] bookColors =
        {
            new Color(0.08f, 0.16f, 0.35f),
            new Color(0.20f, 0.10f, 0.32f),
            new Color(0.05f, 0.24f, 0.24f),
            new Color(0.30f, 0.08f, 0.10f),
            new Color(0.08f, 0.22f, 0.12f),
            new Color(0.18f, 0.19f, 0.22f),
        };
        for (int i = 0; i < bookColors.Length; i++)
        {
            var c = bookColors[i];
            var emit = new Color(c.r * 0.6f, c.g * 0.6f, c.b * 1.2f) * 0.6f;
            bookMats[i] = MakeMat("M_Book" + i, c, 0.25f, 0.5f, emit);
        }

        // —— 地面 + 传送区域 ——
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.SetParent(root.transform, false);
        floor.transform.localScale = new Vector3(4f, 1f, 4f); // 40m x 40m
        floor.GetComponent<MeshRenderer>().sharedMaterial = matFloor;
        floor.AddComponent<TeleportationArea>();

        // 地面发光网格线
        var grid = new GameObject("FloorGrid");
        grid.transform.SetParent(root.transform, false);
        for (int i = -16; i <= 16; i += 4)
        {
            Box(grid.transform, $"GridX_{i}", new Vector3(i, 0.012f, 0), new Vector3(0.05f, 0.02f, 40f), matGrid);
            Box(grid.transform, $"GridZ_{i}", new Vector3(0, 0.012f, i), new Vector3(40f, 0.02f, 0.05f), matGrid);
        }

        // —— 四面围墙 + 顶部霓虹收边 ——
        var walls = new GameObject("Walls");
        walls.transform.SetParent(root.transform, false);
        Box(walls.transform, "Wall_N", new Vector3(0, 2f, -19.8f), new Vector3(40f, 4f, 0.4f), matWall);
        Box(walls.transform, "Wall_S", new Vector3(0, 2f, 19.8f), new Vector3(40f, 4f, 0.4f), matWall);
        Box(walls.transform, "Wall_E", new Vector3(19.8f, 2f, 0), new Vector3(0.4f, 4f, 40f), matWall);
        Box(walls.transform, "Wall_W", new Vector3(-19.8f, 2f, 0), new Vector3(0.4f, 4f, 40f), matWall);
        Box(walls.transform, "Trim_N", new Vector3(0, 3.6f, -19.55f), new Vector3(39f, 0.07f, 0.05f), matCyan);
        Box(walls.transform, "Trim_S", new Vector3(0, 3.6f, 19.55f), new Vector3(39f, 0.07f, 0.05f), matCyan);
        Box(walls.transform, "Trim_E", new Vector3(19.55f, 3.6f, 0), new Vector3(0.05f, 0.07f, 39f), matCyan);
        Box(walls.transform, "Trim_W", new Vector3(-19.55f, 3.6f, 0), new Vector3(0.05f, 0.07f, 39f), matMagenta);

        // —— 空中灯带(高科技感) ——
        var lightBars = new GameObject("LightBars");
        lightBars.transform.SetParent(root.transform, false);
        foreach (var x in new[] { -9f, 0f, 9f })
            Box(lightBars.transform, $"LightBar_{x}", new Vector3(x, 4.2f, 0), new Vector3(1.6f, 0.06f, 34f), matLightBar);

        // —— 灯光 ——
        var sunGo = new GameObject("Directional Light");
        sunGo.transform.SetParent(root.transform, false);
        // 俯角 = 月亮仰角:`SkyboxSunSync` 取 `_MoonDir = -light.forward`,所以改这里就是改月亮高度。
        // v7.18 定案 15°(原 55° 要仰头看,不符合星空露台"稍微抬头一点点就看到月亮");
        // 方位 -35° 不要动 —— 露台、大屏、锚点全按"月亮在东北 145°"摆,改了净空判据就废了。
        sunGo.transform.rotation = Quaternion.Euler(15f, -35f, 0f);
        var sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(0.72f, 0.82f, 1f);
        sun.intensity = 0.55f;
        sun.shadows = LightShadows.Soft;

        foreach (var z in new[] { -9f, -3f, 3f, 9f })
            PointLight(root.transform, $"ShelfLight_{z}", new Vector3(-15.5f, 2.8f, z), new Color(0.4f, 0.9f, 1f), 1.6f, 11f);
        PointLight(root.transform, "KioskLight", new Vector3(0f, 3.2f, 0f), new Color(1f, 0.45f, 0.9f), 1.2f, 14f);
        PointLight(root.transform, "DeskLight", new Vector3(0f, 2.6f, -15f), new Color(0.4f, 0.9f, 1f), 1.4f, 9f);

        // —— 书架区(西侧 4 组书架,共 96 本占位书) ——
        var shelfRoot = new GameObject("Bookshelves");
        shelfRoot.transform.SetParent(root.transform, false);
        float[] shelfZ = { -9f, -3f, 3f, 9f };
        for (int s = 0; s < shelfZ.Length; s++)
            BuildShelf(shelfRoot.transform, s, new Vector3(-17.5f, 0f, shelfZ[s]), bookMats);

        // —— 阅读区(东侧 3 张圆桌 + 凳子) ——
        var readingRoot = new GameObject("ReadingZone");
        readingRoot.transform.SetParent(root.transform, false);
        float[] tableZ = { -7f, 0f, 7f };
        for (int t = 0; t < tableZ.Length; t++)
            BuildReadingTable(readingRoot.transform, t, new Vector3(13f, 0f, tableZ[t]), matMetal, matCyan);

        // —— 服务台 / 还原口(北侧) ——
        BuildServiceDesk(root.transform, matMetal, matCyan, matMagenta, matScreen);

        // —— 中央信息导览台 ——
        BuildCentralKiosk(root.transform, matMetal, matMagenta);

        // —— 传送锚点 ——
        MakePad(root.transform, "Pad_Entry", new Vector3(0f, 0f, 10f));
        MakePad(root.transform, "Pad_Shelves", new Vector3(-13f, 0f, 0f));
        MakePad(root.transform, "Pad_Reading", new Vector3(13f, 0f, 0f));
        MakePad(root.transform, "Pad_Service", new Vector3(0f, 0f, -12.5f));

        // —— 玩家端(XR Interaction Setup + PICO_Manager) ——
        var setupPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(kSetupPrefab);
        if (setupPrefab != null)
        {
            var player = (GameObject)PrefabUtility.InstantiatePrefab(setupPrefab);
            player.name = "XR Interaction Setup";
            player.transform.SetParent(root.transform, false);
            player.transform.position = new Vector3(0f, 0f, 7f);
            player.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            try
            {
                var origin = player.GetComponentInChildren<XROrigin>();
                if (origin != null && origin.GetComponent("PXR_Manager") == null)
                    origin.gameObject.AddComponent(Type.GetType("Unity.XR.PXR.PXR_Manager, Unity.XR.PICO"));
                Debug.Log("[VRLibrary] 已放置 XR Interaction Setup 并尝试挂载 PXR_Manager。");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRLibrary] PXR_Manager 挂载失败(不影响交互):" + e.Message);
            }
        }
        else
        {
            Debug.LogError($"[VRLibrary] 找不到玩家端预制体:{kSetupPrefab}");
        }

        // —— 全局后处理(Bloom 泛光) ——
        BuildGlobalVolume(root.transform);

        // —— 环境渲染设置 ——
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.10f, 0.12f, 0.16f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogDensity = 0.012f;
        RenderSettings.fogColor = new Color(0.015f, 0.025f, 0.045f);
        RenderSettings.sun = sun;

        Debug.Log("[VRLibrary] 场景内容生成完毕。");
    }

    // ---------------------------------------------------------------- 子构建

    static void BuildShelf(Transform parent, int shelfIndex, Vector3 pos, Material[] bookMats)
    {
        var shelf = new GameObject($"Shelf_{shelfIndex}");
        shelf.transform.SetParent(parent, false);
        shelf.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, 90f, 0f)); // 面朝房间中心(+X)

        var matShelf = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_Shelf.mat");
        var matMetal = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_Metal.mat");
        var matCyan  = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_NeonCyan.mat");

        Box(shelf.transform, "Back", new Vector3(0f, 1.6f, -0.25f), new Vector3(5.6f, 3.2f, 0.08f), matShelf);
        foreach (var sx in new[] { -2.75f, 2.75f })
            foreach (var sz in new[] { -0.22f, 0.22f })
                Box(shelf.transform, $"Post_{sx}_{sz}", new Vector3(sx, 1.6f, sz), new Vector3(0.08f, 3.2f, 0.08f), matMetal);

        float[] levelY = { 0.35f, 1.25f, 2.15f };
        int bookCount = 0;
        for (int l = 0; l < levelY.Length; l++)
        {
            float y = levelY[l];
            Box(shelf.transform, $"Board_{l}", new Vector3(0f, y, 0f), new Vector3(5.5f, 0.06f, 0.45f), matShelf);
            Box(shelf.transform, $"Neon_{l}", new Vector3(0f, y + 0.05f, 0.22f), new Vector3(5.5f, 0.035f, 0.03f), matCyan);

            for (int b = 0; b < 8; b++)
            {
                var book = GameObject.CreatePrimitive(PrimitiveType.Cube);
                book.name = $"Book_{shelfIndex}_{l}_{b}";
                book.transform.SetParent(shelf.transform, false);
                float bx = -2.45f + b * 0.62f;
                float bz = UnityEngine.Random.Range(-0.06f, 0.06f);
                book.transform.localPosition = new Vector3(bx, y + 0.03f + 0.13f, bz);
                book.transform.localRotation = Quaternion.Euler(0f, UnityEngine.Random.Range(-2f, 2f), 0f);
                book.transform.localScale = new Vector3(0.34f, 0.26f, 0.07f);
                book.GetComponent<MeshRenderer>().sharedMaterial = bookMats[bookCount % bookMats.Length];

                // 2026-09-28 用户定案:书**只可点击、不可抓**。
                // 挂 Rigidbody 就会被判成"会动的东西",整片书永远进不了静态合批(性能账见知识库);
                // 点击读取由 LibraryBook 绑 XRSimpleInteractable 的 selectEntered 完成。
                book.AddComponent<XRSimpleInteractable>();

                var libBook = book.AddComponent<LibraryBook>();
                libBook.title = $"智能图书馆样本图书 {shelfIndex + 1}-{l + 1}-{b + 1}";
                libBook.author = "佚名(等待馆藏数据)";
                libBook.summary = "占位书籍:真实馆藏数据(书名/作者/简介)将由使用者后续提供,\n届时直接在本组件 Inspector 中填写即可。";
                bookCount++;
            }
        }
    }

    static void BuildReadingTable(Transform parent, int tableIndex, Vector3 pos, Material matMetal, Material matCyan)
    {
        var table = new GameObject($"Table_{tableIndex}");
        table.transform.SetParent(parent, false);
        table.transform.position = pos;

        Cylinder(table.transform, "Top", new Vector3(0f, 0.95f, 0f), new Vector3(1f, 0.025f, 1f), matMetal);
        Cylinder(table.transform, "GlowRing", new Vector3(0f, 0.985f, 0f), new Vector3(1.02f, 0.008f, 1.02f), matCyan);
        Cylinder(table.transform, "Leg", new Vector3(0f, 0.46f, 0f), new Vector3(0.12f, 0.45f, 0.12f), matMetal);
        Cylinder(table.transform, "Base", new Vector3(0f, 0.02f, 0f), new Vector3(0.5f, 0.02f, 0.5f), matMetal);

        // 凳子(朝向桌子一侧)
        var stool = new GameObject("Stool");
        stool.transform.SetParent(table.transform, false);
        stool.transform.localPosition = new Vector3(1.2f, 0f, 0f);
        Cylinder(stool.transform, "Seat", new Vector3(0f, 0.55f, 0f), new Vector3(0.5f, 0.04f, 0.5f), matMetal);
        Cylinder(stool.transform, "Leg", new Vector3(0f, 0.27f, 0f), new Vector3(0.08f, 0.26f, 0.08f), matMetal);
        Cylinder(stool.transform, "Base", new Vector3(0f, 0.02f, 0f), new Vector3(0.32f, 0.02f, 0.32f), matMetal);

        // —— Blender 工单 A:上面那批 Cube 只是占位,有真模型就当场换掉(落位算法与菜单同一份)。
        // 台面上那三样配件(书目屏/灯杆/灯头)是手摆的、本生成器从没造过,所以从零重建不需要"跟桌降高"。
        var notes = new List<string>();
        PropsSetup.PlaceGenerated(table.transform, PropsSetup.Table,
            new[] { "Top", "GlowRing", "Leg", "Base" }, "TableMesh", notes);
        PropsSetup.PlaceGenerated(stool.transform, PropsSetup.Stool,
            new[] { "Seat", "Leg", "Base", "StoolRing" }, "StoolMesh", notes);
        foreach (var n in notes) Debug.Log("[室内构件·生成器 " + table.name + "] " + n);
    }

    static void BuildServiceDesk(Transform root, Material matMetal, Material matCyan, Material matMagenta, Material matScreen)
    {
        var desk = new GameObject("ServiceDesk");
        desk.transform.SetParent(root, false);
        desk.transform.position = new Vector3(0f, 0f, -16f);

        Box(desk.transform, "Body", new Vector3(0f, 0.525f, 0f), new Vector3(1.2f, 1.05f, 0.7f), matMetal);
        Box(desk.transform, "TopGlow", new Vector3(0f, 1.065f, 0f), new Vector3(1.22f, 0.03f, 0.72f), matCyan);

        // 2026-11-06 演示裁剪:归还服务整体移除。
        // 书在 v7.31 改成"点击即读"(XRSimpleInteractable、无 Rigidbody、不可抓),
        // XRSocketInteractor 永远收不到书 ⇒ 这条链路实际不可用,所以不再生成
        // BookReturnSocket / ReturnRing / ReturnStatusCanvas。
        // 服务台本体(Body / TopGlow)保留。
    }

    static void BuildCentralKiosk(Transform root, Material matMetal, Material matMagenta)
    {
        var kiosk = new GameObject("CentralKiosk");
        kiosk.transform.SetParent(root, false);

        Cylinder(kiosk.transform, "Pedestal", new Vector3(0f, 0.5f, 0f), new Vector3(0.6f, 0.5f, 0.6f), matMetal);
        var controller = kiosk.AddComponent<LibraryUIController>();

        // 信息屏 Canvas(正字面朝玩家出生点 +Z)
        // UGUI 世界空间 Canvas 的可读面是 **-forward**,所以"朝 +Z 给游客读"必须带 180° Y;
        // 只写 Euler(-8,0,0) 造出来的屏正字朝 -Z,游客看到的是背面透过来的镜像字。
        var canvasGo = new GameObject("InfoCanvas", typeof(RectTransform), typeof(Canvas));
        canvasGo.transform.SetParent(kiosk.transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rt = canvasGo.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(760f, 460f);
        canvasGo.transform.localPosition = new Vector3(0f, 1.55f, 0.28f);
        canvasGo.transform.localRotation = Quaternion.Euler(-8f, 180f, 0f);
        canvasGo.transform.localScale = Vector3.one * 0.0016f;
        canvasGo.AddComponent<TrackedDeviceGraphicRaycaster>();

        var bg = canvasGo.AddComponent<Image>();
        bg.color = new Color(0.03f, 0.06f, 0.10f, 0.93f);

        MakeText(canvasGo.transform, "Title", "智能图书馆", 64, Color.white, TextAnchor.UpperCenter,
            new Vector2(0f, 0.72f), new Vector2(1f, 1f), Vector2.zero, new Vector2(0f, -8f));
        MakeText(canvasGo.transform, "Subtitle", "SMART LIBRARY · 未来阅读体验", 28, new Color(0.3f, 0.9f, 1f), TextAnchor.UpperCenter,
            new Vector2(0f, 0.60f), new Vector2(1f, 0.78f), Vector2.zero, Vector2.zero);

        var status = MakeText(canvasGo.transform, "StatusText",
            "欢迎使用智能图书馆\n用射线指向按钮并扣动扳机,或传送/行走探索场馆",
            26, new Color(0.8f, 0.87f, 0.92f), TextAnchor.MiddleCenter,
            new Vector2(0.04f, 0.24f), new Vector2(0.96f, 0.58f), Vector2.zero, Vector2.zero);
        controller.statusText = status;

        // 2026-11-06 演示裁剪:「归还服务」随归还功能一起移除,只剩两颗并列居中。
        string[] labels = { "馆藏导览", "智能检索" };
        var buttons = new Button[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            float x = -125f + i * 250f;
            buttons[i] = MakeButton(canvasGo.transform, $"Button_{labels[i]}", labels[i],
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(x - 110f, 22f), new Vector2(x + 110f, 88f),
                new Color(0.10f, 0.22f, 0.34f, 0.95f));
        }

        UnityEventTools.AddVoidPersistentListener(buttons[0].onClick, new UnityAction(controller.OnGuideButton));
        UnityEventTools.AddVoidPersistentListener(buttons[1].onClick, new UnityAction(controller.OnSearchButton));

        // 底座霓虹环
        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "KioskGlowRing";
        ring.transform.SetParent(kiosk.transform, false);
        ring.transform.localPosition = new Vector3(0f, 0.015f, 0f);
        ring.transform.localScale = new Vector3(1.35f, 0.008f, 1.35f);
        var ringMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_NeonMagenta.mat");
        if (ringMat != null) ring.GetComponent<MeshRenderer>().sharedMaterial = ringMat;

        // —— Blender 工单 B:整块服务台(屏框+底座霓虹都在模型里),所以换掉时连 Pedestal 和霓虹环一起删。
        // 换完必须重新落座屏幕:旧屏位是照着"Cube 柱子 + 悬浮屏"摆的,新屏框中心/倾角都由模型决定。
        // 搜索面板 SearchPanel 是手摆的、本生成器不造,所以这里只会对到 InfoCanvas 一块。
        var notes = new List<string>();
        if (PropsSetup.PlaceGenerated(kiosk.transform, PropsSetup.Kiosk,
                new[] { "Pedestal", "KioskGlowRing" }, "KioskMesh", notes))
            PropsSetup.SeatCanvasesGenerated(kiosk.transform, notes);
        foreach (var n in notes) Debug.Log("[室内构件·生成器 CentralKiosk] " + n);
    }

    static void MakePad(Transform root, string name, Vector3 pos)
    {
        var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        pad.name = name;
        pad.transform.SetParent(root, false);
        pad.transform.position = new Vector3(pos.x, 0.012f, pos.z);
        pad.transform.localScale = new Vector3(1.6f, 0.012f, 1.6f);
        var padMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_TeleportPad.mat");
        if (padMat != null) pad.GetComponent<MeshRenderer>().sharedMaterial = padMat;
        pad.AddComponent<TeleportationAnchor>();
        // Cylinder 原语自带**实心胶囊碰撞体**(缩完是 1.6 m 高 × 1.6 m 宽),立在走道正中就是
        // 隐形路障 —— 二楼回廊三条走廊各被它堵死 1.7 m(实测见 Logs/floor2-audit.txt)。
        // 换成贴着盘面的扁盒子:1.6 m 见方照旧打得中瞄准射线,高 5×0.012=0.06 m 直接走过去。
        // (Unity 没有 CylinderCollider,所以用盒子外接盘子。)
        var cap = pad.GetComponent<CapsuleCollider>();
        if (cap != null) Object.DestroyImmediate(cap);
        var box = pad.AddComponent<BoxCollider>();
        box.size = new Vector3(1f, 5f, 1f);        // ×localScale(1.6,0.012,1.6) → 1.6×0.06×1.6 m
        box.center = Vector3.zero;
    }

    static void BuildGlobalVolume(Transform root)
    {
        const string profilePath = "Assets/Settings/VRLibraryVolumeProfile.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, profilePath);

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(0.9f);
            bloom.intensity.Override(0.55f);
            AssetDatabase.AddObjectToAsset(bloom, profile);

            var colorAdj = profile.Add<ColorAdjustments>(true);
            colorAdj.saturation.Override(8f);
            AssetDatabase.AddObjectToAsset(colorAdj, profile);

            EditorUtility.SetDirty(profile);
        }

        var volGo = new GameObject("GlobalVolume");
        volGo.transform.SetParent(root, false);
        var vol = volGo.AddComponent<Volume>();
        vol.isGlobal = true;
        vol.sharedProfile = profile;
    }

    // ---------------------------------------------------------------- 工具

    static Material MakeMat(string name, Color baseColor, float metallic, float smoothness, Color? emission = null)
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
        mat.SetColor("_BaseColor", baseColor);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
        if (emission.HasValue)
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", emission.Value);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        string path = $"Assets/Materials/{name}.mat";
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat, float yRot = 0f)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.transform.localRotation = Quaternion.Euler(0f, yRot, 0f);
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go;
    }

    static GameObject Cylinder(Transform parent, string name, Vector3 pos, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go;
    }

    static void PointLight(Transform parent, string name, Vector3 pos, Color color, float intensity, float range)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = range;
    }

    static Text MakeText(Transform parent, string name, string content, int size, Color color,
        TextAnchor anchor, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
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
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        return text;
    }

    static Button MakeButton(Transform parent, string name, string label,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offMin, Vector2 offMax, Color bgColor)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = bgColor;
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.15f, 0.55f, 0.75f);
        colors.pressedColor = new Color(0.08f, 0.35f, 0.5f);
        btn.colors = colors;
        var rt = btn.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offMin;
        rt.offsetMax = offMax;
        MakeText(go.transform, "Text", label, 30, Color.white, TextAnchor.MiddleCenter,
            Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        return btn;
    }

    static void EnsureFolder(string folder)
    {
        var parts = folder.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = cur + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
}
