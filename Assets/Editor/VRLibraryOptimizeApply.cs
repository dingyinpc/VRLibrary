#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// 一次性/幂等的工程整改落地脚本(菜单 + batchmode 可调):
/// 1) 清理主场景 Missing Script 并重新挂载 SDK 2.5.3 PXR_Manager (High FFR, openMRC=false)
/// 2) 禁用与 TrackedDeviceGraphicRaycaster 冲突的 6 个冗余 GraphicRaycaster
/// 3) 禁用本工程未使用的 XR Rig 冗余交互组件 (Gaze/Climb/GrabMove/Direct)
/// 4) 删除 Resources 中残留的 SDK 3.3.0 坏资产 (保留 LightingData 依赖的 ReflectionProbe-0.exr)
/// 5) 迁移 10 个未压缩 RGBA32 .asset 贴图到 ASTC_6x6 压缩 PNG
/// 6) 烘焙 Static Occlusion Culling 并保存场景
/// 7) 运行全量审计报告验证
/// </summary>
public static class VRLibraryOptimizeApply
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string ReportPath = "Logs/optimize-apply.txt";

    [MenuItem("VRLibrary/诊断PXR资产加载")]
    public static void DiagPxrAssets()
    {
        string[] scripts =
        {
            "Assets/sdk/PICO Unity Integration SDK 2.5.3/Runtime/Scripts/PXR_Settings.cs",
            "Assets/sdk/PICO Unity Integration SDK 2.5.3/Runtime/Scripts/PXR_ProjectSetting.cs",
            "Assets/sdk/PICO Unity Integration SDK 2.5.3/Runtime/Scripts/PXR_Loader.cs",
            "Assets/sdk/PICO Unity Integration SDK 2.5.3/Platform/Scripts/Framework/PXR_PlatformSetting.cs",
        };
        foreach (var sp in scripts)
        {
            var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(sp);
            Debug.Log($"[DiagPXR] BEFORE {sp} -> ms={(ms==null?"NULL":ms.name)} class={(ms==null||ms.GetClass()==null?"NULL":ms.GetClass().FullName)}");
            AssetDatabase.ImportAsset(sp, ImportAssetOptions.ForceUpdate);
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        foreach (var sp in scripts)
        {
            var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(sp);
            Debug.Log($"[DiagPXR] AFTER  {sp} -> ms={(ms==null?"NULL":ms.name)} class={(ms==null||ms.GetClass()==null?"NULL":ms.GetClass().FullName)}");
        }
        foreach (var ap in new[] { "Assets/XR/Settings/PXR_Settings.asset", "Assets/Resources/PXR_ProjectSetting.asset", "Assets/Resources/PXR_PlatformSetting.asset", "Assets/XR/Loaders/PXR_Loader.asset" })
        {
            AssetDatabase.ImportAsset(ap, ImportAssetOptions.ForceUpdate);
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ap);
            Debug.Log($"[DiagPXR] ASSET  {ap} -> obj={(obj==null?"NULL":obj.GetType().FullName)}");
        }
    }

    [MenuItem("VRLibrary/一键落地全部体检整改(改场景+资产+烘焙)")]
    public static void RunAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== VRlibrary 体检整改一键落地 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        // 1. 删除 Resources 里的损坏旧 SDK 3.3.0 调试资产 (注意: ReflectionProbe-0.exr 被 LightingData.asset 依赖, 严禁删除)
        string[] deadAssets =
        {
            "Assets/Resources/DebuggerPanel.prefab",
            "Assets/Resources/PICODebugger.prefab",
            "Assets/Resources/PXR_PicoDebuggerSO.asset",
        };
        int deadDeleted = 0;
        foreach (var path in deadAssets)
        {
            if (File.Exists(path) && AssetDatabase.DeleteAsset(path))
            {
                sb.AppendLine("  删除损坏/孤儿资产: " + path);
                deadDeleted++;
            }
        }
        sb.AppendLine("[1] 清理损坏/孤儿资产: " + deadDeleted + " 个");

        // 2. 清理场景 Missing Scripts 并重新挂载 SDK 2.5.3 PXR_Manager
        int removedMissing = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                int c = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                if (c > 0)
                {
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                    EditorUtility.SetDirty(t.gameObject);
                    removedMissing += c;
                    sb.AppendLine("  移除 Missing Script ×" + c + " @ " + t.name);
                }
            }
        }

        var origin = UnityEngine.Object.FindObjectOfType<XROrigin>(true);
        if (origin != null)
        {
            var pxrType = Type.GetType("Unity.XR.PXR.PXR_Manager, Unity.XR.PICO");
            if (pxrType != null)
            {
                var mgr = origin.GetComponent(pxrType) ?? origin.gameObject.AddComponent(pxrType);
                var so = new SerializedObject(mgr);
                var pFov = so.FindProperty("foveationLevel");
                if (pFov != null) pFov.intValue = 2; // High
                var pMrc = so.FindProperty("openMRC");
                if (pMrc != null) pMrc.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(mgr);
                sb.AppendLine("[2] PXR_Manager (SDK 2.5.3) 已挂载在 " + origin.name + " (foveationLevel=High, openMRC=false), 移除 Missing Script=" + removedMissing);
            }
            else
            {
                sb.AppendLine("[2] !! 找不到 Unity.XR.PXR.PXR_Manager 类型");
            }
        }

        // 3. 禁用与 TrackedDeviceGraphicRaycaster 共存的冗余 GraphicRaycaster
        int rayDisabled = 0;
        foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
        {
            var gr = cv.GetComponent<GraphicRaycaster>();
            if (gr == null || !gr.enabled) continue;
            bool hasXrRay = cv.GetComponent("TrackedDeviceGraphicRaycaster") != null
                         || cv.GetComponent("XRGraphicRaycaster") != null;
            if (hasXrRay)
            {
                gr.enabled = false;
                EditorUtility.SetDirty(gr);
                rayDisabled++;
                sb.AppendLine("  禁用冗余 GraphicRaycaster @ " + cv.name);
            }
        }
        sb.AppendLine("[3] 禁用冲突 GraphicRaycaster: " + rayDisabled + " 个");

        // 4. 禁用本工程未使用的 XR Rig 冗余独立组件(保留 Direct Interactor 以免影响 ActionBasedControllerManager 组协调)
        string[] disableRigChildren =
        {
            "Gaze Interactor",
            "Climb",
            "Grab Move",
        };
        int rigDisabled = 0;
        if (origin != null && origin.transform.parent != null)
        {
            var setupRoot = origin.transform.parent;
            foreach (var t in setupRoot.GetComponentsInChildren<Transform>(true))
            {
                for (int i = 0; i < disableRigChildren.Length; i++)
                {
                    if (t.name == disableRigChildren[i] && t.gameObject.activeSelf)
                    {
                        t.gameObject.SetActive(false);
                        EditorUtility.SetDirty(t.gameObject);
                        rigDisabled++;
                        sb.AppendLine("  禁用未用 XR 子物体: " + t.name);
                    }
                }
            }
        }
        sb.AppendLine("[4] 禁用冗余 XR Rig 子物体: " + rigDisabled + " 个");

        // 5. 迁移程序化 .asset 贴图 -> ASTC 6x6 压缩 PNG
        ProceduralTextureToggle.MigrateAssetsToCompressedPng();
        sb.AppendLine("[5] 程序化贴图已迁移为 ASTC_6x6 压缩 PNG");

        // 6. 同步 PICO 设置注册与保存场景
        VRLibraryPicoSettingsFix.Ensure(true);
        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        sb.AppendLine("[6] 保存场景=" + saved + "  isDirty=" + scene.isDirty);

        // 7. 清理无用 0KB Occlusion 数据(挑空大厅 Umbra 合并为单 Tile 无法遮挡裁剪,由 LibraryZoneCuller 负责)
        if (StaticOcclusionCulling.umbraDataSize == 0)
        {
            VRLibraryOcclusion.Revert();
            var cam = Camera.main;
            if (cam != null && cam.useOcclusionCulling)
            {
                cam.useOcclusionCulling = false;
                EditorUtility.SetDirty(cam);
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            sb.AppendLine("[7] 清理空 Umbra Occlusion 数据并关闭 Camera.useOcclusionCulling (由 LibraryZoneCuller 负责剔除)");
        }
        else
        {
            sb.AppendLine("[7] 保留现有 Occlusion Culling 数据, umbraDataSize=" + (StaticOcclusionCulling.umbraDataSize / 1024) + " KB");
        }

        // 8. 确认光照烘焙与反射探针数据完整，并输出最新体检报告
        bool probeOk = File.Exists("Assets/_Scenes/SmartLibrary/ReflectionProbe-0.exr")
                    && Lightmapping.lightingDataAsset != null
                    && LightmapSettings.lightmaps.Length > 0;
        sb.AppendLine("[8] 光照贴图/反射探针完整=" + probeOk
                    + " (lightmaps=" + LightmapSettings.lightmaps.Length
                    + ", reflectionProbe=" + File.Exists("Assets/_Scenes/SmartLibrary/ReflectionProbe-0.exr") + ")");
        if (!probeOk)
            Debug.LogError("[VRLibraryOptimizeApply] 光照贴图或 ReflectionProbe-0.exr 缺失，请检查 Assets/_Scenes/SmartLibrary/");

        VRLibraryAudit.Run();
        SanityAudit.Run();
        VRLibraryPerf.StaticAudit();
        VRLibraryFfr.Report();
        VRLibraryStereoAsw.Report();
        sb.AppendLine("[9] 已重新生成 scene-audit / sanity-audit / perf-static / perf-ffr / perf-stereo 报告");

        Directory.CreateDirectory("Logs");
        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        Debug.Log(sb.ToString());
    }

    [MenuItem("VRLibrary/一键验证贴图账与清单")]
    public static void VerifyReports()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        VRLibraryPicoSettingsFix.Ensure(true);
        VRLibraryPerf.TextureAudit();
        VRLibraryPicoManifestProbe.Run();
    }

    static bool smokeWaitPlay;
    static double smokeStartTime;

    /// <summary>
    /// 供批处理/自动化模式调用:准备冒烟请求 -> 进入 Play -> 等待 VRLibrarySmoke 完成后退出 Unity。
    /// </summary>
    public static void RunSmokeBatch()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        VRLibraryPicoSettingsFix.Ensure(true);
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/smoke-request.txt", "smoke", new UTF8Encoding(false));
        smokeWaitPlay = false;
        smokeStartTime = EditorApplication.timeSinceStartup;
        EditorApplication.update += WatchSmoke;
        EditorApplication.isPlaying = true;
    }

    static void WatchSmoke()
    {
        if (EditorApplication.isPlaying)
        {
            smokeWaitPlay = true;
            if (EditorApplication.timeSinceStartup - smokeStartTime > 150.0)
            {
                Debug.LogError("[SmokeBatch] 超时 150s 强行终止 Play");
                EditorApplication.isPlaying = false;
                EditorApplication.update -= WatchSmoke;
                EditorApplication.Exit(2);
            }
            return;
        }
        if (smokeWaitPlay || EditorApplication.timeSinceStartup - smokeStartTime > 20.0)
        {
            EditorApplication.update -= WatchSmoke;
            EditorApplication.Exit(0);
        }
    }
}
#endif
