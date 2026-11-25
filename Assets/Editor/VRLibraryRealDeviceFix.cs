using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 2026-10-15 真机反馈修复(一次性,幂等,可回退)。
/// 菜单:VRLibrary/真机修复·2026-10-15 一键(改场景)
///
/// 修 5 件事里的 4 件(第 5 件"书本刷不出来"在运行期脚本 LibraryZoneCuller 里改):
///   ① 防晕眩环:apertureSize 0.85 → 0.70(环宽 0.15→0.30,翻倍);颜色 alpha 0.5 → 0.85。
///   ② 右手不能转头:Starter Assets 的 ActionBasedControllerManager 会在运行期按
///      m_SmoothTurnEnabled 开关 Turn/SnapTurn 输入动作(m_Turn = !smoothMotion && smoothTurn)。
///      右手 smoothMotion=false、smoothTurn=false ⇒ Turn 被禁、SnapTurn 被开;而 SnapTurnProvider 组件
///      已被 v7.32 禁用 ⇒ 两头都断。把 smoothTurn 置 true,右手即落到已配好的 ContinuousTurnProvider。
///   ③ 人物过高:XR Origin 的 RequestedTrackingOriginMode = NotSpecified 时,若追踪原点没被识别成
///      Floor,XRI 就不会把 Camera Offset 归零(Floor→0 / Device→CameraYOffset),"Camera Offset Y=1.361"
///      会叠加在真实头高上 ⇒ 眼高 ≈ 3.06 m。显式请求 Floor 后由 XRI 归零,眼高 = 真实头高。
///   ④ 菜单按钮无响应:HUD 画布同时挂了 GraphicRaycaster + TrackedDeviceGraphicRaycaster,
///      而场上一直能点的中央导览台 InfoCanvas **只有** TrackedDeviceGraphicRaycaster。
///      旧版 GraphicRaycaster 会用 Camera.main 按"屏幕坐标"去投射线(XR 指针没有屏幕坐标),
///      可能产生莫名的命中把指针抢走 ⇒ 关掉它(只关启用,不删组件,复核脚本的计数判据不受影响)。
///
/// 判据/写法沿用工程习惯:改前先落快照、按字段名改、改完自证、报告写 Logs/realdevice-fix.txt。
/// </summary>
public static class VRLibraryRealDeviceFix
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Snapshot = "Logs/SmartLibrary.pre-devfix-20261015.unity";
    const string Report = "Logs/realdevice-fix.txt";

    const float VignetteAperture = 0.70f;   // XRI 原始默认
    const float VignetteAlpha = 1.00f;      // XRI 原始默认(纯黑)
    const float VignetteFeathering = 0.20f; // XRI 原始默认(柔边)—— 恢复 v7.32 之前的状态
    const int TrackingOriginFloor = 2;      // XROrigin.TrackingOriginMode { NotSpecified=0, Device=1, Floor=2 }

    [MenuItem("VRLibrary/真机修复·只改防眩晕环黑度(改场景)")]
    public static void ApplyVignetteOnly()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 防眩晕环黑度(只改这一项) ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        SnapshotOnce();
        FixVignette(sb);
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("\n保存场景:" + EditorSceneManager.SaveScene(scene));
        sb.AppendLine("\n---- 改后自证 ----");
        DumpVignette(sb);
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/真机修复·2026-10-15 现状(只报告)")]
    public static void Show()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 真机修复·2026-10-15 现状 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        DumpVignette(sb);
        DumpTurn(sb);
        DumpRig(sb);
        DumpHudRaycaster(sb);
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/真机修复·2026-10-15 一键(改场景)")]
    public static void ApplyAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 真机反馈修复 2026-10-15 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        SnapshotOnce();

        FixVignette(sb);
        FixTurn(sb);
        FixRig(sb);
        FixHudRaycaster(sb);

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("\n保存场景:" + EditorSceneManager.SaveScene(scene));

        sb.AppendLine("\n---- 改后自证 ----");
        DumpVignette(sb);
        DumpTurn(sb);
        DumpRig(sb);
        DumpHudRaycaster(sb);
        Write(sb.ToString());
    }

    // ------------------------------------------------------------ ① 防晕眩环

    static void FixVignette(StringBuilder sb)
    {
        sb.AppendLine("\n[1] 防晕眩环 aperture → " + VignetteAperture.ToString("0.00")
            + ",feathering → " + VignetteFeathering.ToString("0.00")
            + ",颜色 alpha → " + VignetteAlpha.ToString("0.00"));
        foreach (var v in UnityEngine.Object.FindObjectsOfType<TunnelingVignetteController>(true))
        {
            var so = new SerializedObject(v);
            int n = 0;
            foreach (var p in AllProps(so))
            {
                string fn = p.name.ToLowerInvariant();
                if (p.propertyType == SerializedPropertyType.Float && fn.Contains("aperturesize"))
                {
                    sb.AppendLine("  " + p.propertyPath + " " + p.floatValue.ToString("0.###") + " → " + VignetteAperture.ToString("0.###"));
                    p.floatValue = VignetteAperture; n++;
                }
                else if (p.propertyType == SerializedPropertyType.Float && fn.Contains("featheringeffect"))
                {
                    sb.AppendLine("  " + p.propertyPath + " " + p.floatValue.ToString("0.###") + " → " + VignetteFeathering.ToString("0.###"));
                    p.floatValue = VignetteFeathering; n++;
                }
                else if (p.propertyType == SerializedPropertyType.Color && fn.Contains("vignettecolor"))
                {
                    var c = p.colorValue;
                    sb.AppendLine("  " + p.propertyPath + ".a " + c.a.ToString("0.###") + " → " + VignetteAlpha.ToString("0.###"));
                    p.colorValue = new Color(c.r, c.g, c.b, VignetteAlpha); n++;
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(v);
            sb.AppendLine("  (" + v.name + ":命中 " + n + " 个字段)");
        }
    }

    static void DumpVignette(StringBuilder sb)
    {
        sb.AppendLine("---- 防晕眩环 ----");
        foreach (var v in UnityEngine.Object.FindObjectsOfType<TunnelingVignetteController>(true))
        {
            var so = new SerializedObject(v);
            foreach (var p in AllProps(so))
            {
                string fn = p.name.ToLowerInvariant();
                if (p.propertyType == SerializedPropertyType.Float && fn.Contains("aperturesize"))
                    sb.AppendLine("  " + p.propertyPath + " = " + p.floatValue.ToString("0.###"));
                else if (p.propertyType == SerializedPropertyType.Color && fn.Contains("vignettecolor"))
                    sb.AppendLine("  " + p.propertyPath + " = " + p.colorValue);
            }
        }
    }

    // ------------------------------------------------------------ ② 右手转头

    static void FixTurn(StringBuilder sb)
    {
        sb.AppendLine("\n[2] 右手转头:ActionBasedControllerManager.m_SmoothTurnEnabled → true");
        foreach (var mb in Managers())
        {
            var so = new SerializedObject(mb);
            var p = so.FindProperty("m_SmoothTurnEnabled");
            if (p == null) { sb.AppendLine("  !! " + mb.gameObject.name + " 没有 m_SmoothTurnEnabled 字段"); continue; }
            sb.AppendLine("  " + mb.gameObject.name + " smoothTurn " + p.boolValue + " → true");
            if (!p.boolValue) { p.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(mb); }
        }
    }

    static void DumpTurn(StringBuilder sb)
    {
        sb.AppendLine("---- 转头(ActionBasedControllerManager)----");
        foreach (var mb in Managers())
        {
            var p1 = new SerializedObject(mb).FindProperty("m_SmoothMotionEnabled");
            var p2 = new SerializedObject(mb).FindProperty("m_SmoothTurnEnabled");
            sb.AppendLine("  " + mb.gameObject.name + " smoothMotion=" + (p1 == null ? "?" : p1.boolValue.ToString())
                + " smoothTurn=" + (p2 == null ? "?" : p2.boolValue.ToString()));
        }
    }

    static IEnumerable<MonoBehaviour> Managers()
    {
        // Starter Assets 采样程序集不被 Assembly-CSharp-Editor 引用 ⇒ 按类型名反射找
        foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
            if (mb.GetType().Name == "ActionBasedControllerManager") yield return mb;
    }

    // ------------------------------------------------------------ ③ 眼高 / 追踪原点

    static void FixRig(StringBuilder sb)
    {
        sb.AppendLine("\n[3] 眼高:XR Origin RequestedTrackingOriginMode → Floor");
        var origins = UnityEngine.Object.FindObjectsOfType<Unity.XR.CoreUtils.XROrigin>(true);
        if (origins.Length == 0) { sb.AppendLine("  !! 找不到 XROrigin"); return; }
        foreach (var o in origins)
        {
            var so = new SerializedObject(o);
            var pm = so.FindProperty("m_RequestedTrackingOriginMode");
            var pc = so.FindProperty("m_CameraYOffset");
            if (pm != null)
            {
                sb.AppendLine("  " + o.gameObject.name + " RequestedTrackingOriginMode " + pm.enumValueIndex
                    + " → " + TrackingOriginFloor + "(Floor)");
                pm.enumValueIndex = TrackingOriginFloor;
            }
            if (pc != null) sb.AppendLine("  CameraYOffset(仅 Device 兜底,Floor 下由 XRI 归零)= " + pc.floatValue.ToString("0.###"));
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(o);
        }
    }

    static void DumpRig(StringBuilder sb)
    {
        sb.AppendLine("---- XR Origin / 眼高 ----");
        foreach (var o in UnityEngine.Object.FindObjectsOfType<Unity.XR.CoreUtils.XROrigin>(true))
        {
            var so = new SerializedObject(o);
            var pm = so.FindProperty("m_RequestedTrackingOriginMode");
            var pc = so.FindProperty("m_CameraYOffset");
            sb.AppendLine("  " + o.gameObject.name + " trackingMode=" + (pm == null ? "?" : pm.enumValueIndex.ToString())
                + " CameraYOffset=" + (pc == null ? "?" : pc.floatValue.ToString("0.###"))
                + " originY=" + o.transform.position.y.ToString("0.###"));
        }
    }

    // ------------------------------------------------------------ ④ 菜单按钮(HUD 画布)

    static void FixHudRaycaster(StringBuilder sb)
    {
        sb.AppendLine("\n[4] 菜单按钮:关掉 HUD 画布上多挂的 GraphicRaycaster(与可用的 InfoCanvas 对齐,只留 TrackedDeviceGraphicRaycaster)");
        var hud = FindByName("LibraryHud");
        if (hud == null) { sb.AppendLine("  !! 找不到 LibraryHud"); return; }
        var grs = hud.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true);
        sb.AppendLine("  找到 " + grs.Length + " 个 GraphicRaycaster");
        foreach (var g in grs)
        {
            sb.AppendLine("  " + g.gameObject.name + " enabled " + g.enabled + " → false");
            g.enabled = false;
            EditorUtility.SetDirty(g);
        }
        var td = hud.GetComponentInChildren<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>(true);
        sb.AppendLine("  TrackedDeviceGraphicRaycaster 仍在且启用 = " + (td != null && td.enabled));
    }

    static void DumpHudRaycaster(StringBuilder sb)
    {
        sb.AppendLine("---- HUD 画布射线器 ----");
        var hud = FindByName("LibraryHud");
        if (hud == null) { sb.AppendLine("  (没有 LibraryHud)"); return; }
        foreach (var g in hud.GetComponentsInChildren<UnityEngine.UI.GraphicRaycaster>(true))
            sb.AppendLine("  GraphicRaycaster on " + g.gameObject.name + " enabled=" + g.enabled);
        foreach (var t in hud.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>(true))
            sb.AppendLine("  TrackedDeviceGraphicRaycaster on " + t.gameObject.name + " enabled=" + t.enabled);
    }

    static GameObject FindByName(string name)
    {
        foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>(true))
            if (go.name == name) return go;
        return null;
    }

    // ------------------------------------------------------------ 工具

    static IEnumerable<SerializedProperty> AllProps(SerializedObject so)
    {
        var it = so.GetIterator();
        while (it.NextVisible(true)) yield return it.Copy();
    }

    static void SnapshotOnce()
    {
        try
        {
            if (System.IO.File.Exists(Snapshot)) { Debug.Log("[DevFix] 已有快照,不覆盖:" + Snapshot); return; }
            System.IO.Directory.CreateDirectory("Logs");
            System.IO.File.Copy(ScenePath, Snapshot);
            Debug.Log("[DevFix] 落盘快照 " + Snapshot);
        }
        catch (Exception e) { Debug.LogWarning("[DevFix] 快照失败:" + e.Message); }
    }

    static void Write(string text)
    {
        try { System.IO.Directory.CreateDirectory("Logs"); System.IO.File.WriteAllText(Report, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[DevFix] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}
