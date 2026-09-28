using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 体验⑩:防眩晕环强度 + 转头方式。菜单 VRLibrary/体验·⑩ xxx。
///
/// 用户提的两个问题(2026-09-28):
///   ① **防眩晕环太大** —— 走动的时候屏幕发黑。用户判断"到目前的一半就够起到防眩晕效果了"。
///   ② **按一下转一个固定角度不合理** —— 手柄是有力度的,应该**按住持续转**,并且**力度越大转得越快**。
///
/// 做法与理由:
///   ① 环 = XRI 的 `TunnelingVignetteController`,它有两套参数:静止用 default、移动用 locomotion。
///      发黑是 locomotion 那套在起作用。本菜单把它的**两个维度各减半**:
///         · 颜色不透明度 → 一半(纯"变淡")
///         · 光圈收拢量 → 一半(即 locomotion 的 apertureSize 取"它到 1.0 的中点",圈就小一圈)
///      两个都打在报告里,觉得还大/太小就再跑一次或按报告手调。
///   ② 转头 = 把 `SnapTurnProvider`(按一下跳固定角度)换成 `ActionBasedContinuousTurnProvider`
///      (**按住持续转**),并**照抄原 Snap 提供者的输入引用**(同一个摇杆 Vector2 action),
///      再把 turnSpeed 调到 90°/s。XRI 的连续转是**按输入量(摇杆偏得越多转得越快)**算的,
///      所以"按力度调速"是它自带的行为,不需要自己写脚本。
///      旧组件**不删只禁用**(方便一键回退;也因为它还挂着"180° 快速转身"的设置)。
///
/// 全部只改场景序列化值,可自证(改完就读回),可回退。
/// </summary>
public static class VRLibraryTurnAndVignette
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Snapshot = "Logs/SmartLibrary.pre-turnvignette.unity";
    const string Report = "Logs/experience-turn-vignette.txt";
    const float TurnSpeed = 90f;      // 满推摇杆时的转速(度/秒)

    [MenuItem("VRLibrary/体验·⑩ 现状(只报告)")]
    public static void Show()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 体验⑩ 转头与防眩晕环:现状 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.Append(DumpAll());
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑩ 转头改成按住持续转 + 力度调速(改场景)")]
    public static void ApplyTurn()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 体验⑩ 转头:Snap(按一下跳固定角度) → Continuous(按住持续转 + 力度调速) ==");
        var rig = Rig();
        if (rig == null) { sb.AppendLine("!! 找不到 XR Origin"); Write(sb.ToString()); return; }
        SnapshotOnce();

        var snaps = rig.GetComponentsInChildren<SnapTurnProviderBase>(true);
        var conts = rig.GetComponentsInChildren<ContinuousTurnProviderBase>(true);
        sb.AppendLine("改前:" + DumpAll().TrimEnd());

        if (conts.Length == 0)
        {
            var go = snaps.Length > 0 ? snaps[0].gameObject : rig.gameObject;
            var cont = go.AddComponent<ActionBasedContinuousTurnProvider>();
            conts = new[] { cont };
            sb.AppendLine("已在 " + go.name + " 上新增 ActionBasedContinuousTurnProvider");
        }

        // 实测(2026-09-28):工程里 **Snap 与 Continuous 两个提供者本来就同时开着**,
        // 各绑不同 action(XRI …/Snap Turn 与 …/Turn,而两根 action 都挂在同一个摇杆上)
        // ⇒ 按一下既跳 45° 又持续转,用户感觉就是"按固定角度转头"。
        // 所以正解是:**禁用 Snap、留下已经配好的 Continuous**,而不是新建+抄 action。
        // 只有在 Continuous 的输入引用为空时才去抄 Snap 的引用(兜底)。
        if (snaps.Length > 0 && conts.Length > 0)
        {
            var dst = new SerializedObject(conts[0]);
            var src = new SerializedObject(snaps[0]);
            int copied = 0;
            foreach (var dp in Props(dst).Where(p => p.propertyType == SerializedPropertyType.Generic
                                                  && p.name.ToLower().Contains("turn")))
            {
                var dRef = dp.FindPropertyRelative("m_Reference");
                if (dRef == null || dRef.objectReferenceValue != null) continue;   // 已经有引用就不动它
                bool left = dp.name.ToLower().Contains("left");
                var sp = Props(src).FirstOrDefault(p => p.propertyType == SerializedPropertyType.Generic
                                                     && p.name.ToLower().Contains("turn")
                                                     && p.name.ToLower().Contains(left ? "left" : "right"));
                var sRef = sp == null ? null : sp.FindPropertyRelative("m_Reference");
                if (sRef == null || sRef.objectReferenceValue == null) continue;
                dRef.objectReferenceValue = sRef.objectReferenceValue;
                var dUse = dp.FindPropertyRelative("m_UseReference");
                if (dUse != null) dUse.boolValue = true;
                sb.AppendLine("  抄输入(仅当为空):" + dp.name + " = " + sRef.objectReferenceValue.name);
                copied++;
            }
            if (copied > 0) { dst.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(conts[0]); }
            else sb.AppendLine("  连续性提供者的输入引用本来就有,不动它");
        }

        // 转速:满推 90°/s(XRI 的连续转是按输入量线性缩放,所以力度小自然转得慢)
        var cs = new SerializedObject(conts[0]);
        var sp2 = Props(cs).FirstOrDefault(p => p.propertyType == SerializedPropertyType.Float
                                             && p.name.ToLower().Contains("turnspeed"));
        if (sp2 != null) { sb.AppendLine("  转速 " + sp2.floatValue + " → " + TurnSpeed + " 度/秒"); sp2.floatValue = TurnSpeed; }
        else sb.AppendLine("  !! 没找到 turnSpeed 字段(下面会列出实际字段)");
        cs.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(conts[0]);

        foreach (var s in snaps) { s.enabled = false; EditorUtility.SetDirty(s); sb.AppendLine("  已禁用 " + s.GetType().Name + "(不删,方便回退)"); }

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + EditorSceneManager.SaveScene(scene));
        sb.AppendLine("\n改后:" + DumpAll().TrimEnd());
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑩ 防眩晕环强度减半(改场景)")]
    public static void ApplyVignette()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 体验⑩ 防眩晕环:两个维度各减半 ==");
        var vig = UnityEngine.Object.FindObjectsOfType<TunnelingVignetteController>(true);
        if (vig.Length == 0) { sb.AppendLine("!! 场景里没有 TunnelingVignetteController"); Write(sb.ToString()); return; }
        SnapshotOnce();
        sb.AppendLine("改前:" + DumpAll().TrimEnd());

        foreach (var v in vig)
        {
            var so = new SerializedObject(v);
            // ⚠ 实测:控制器里有两套参数(default 与 locomotion),而 SerializedObject 的迭代器会**下探进结构体**,
            // 于是第二套的字段是"扁平"出现的,靠字段名根本分不出属于哪一套。
            // 所以这里不区分,凡名字匹配的**全都减半**(两套同样处理,行为才一致)。
            int touched = 0;
            foreach (var p in Props(so))
            {
                string fn = p.name.ToLower();
                if (fn.Contains("current")) continue;                       // 运行期缓存值,别写
                if (p.propertyType == SerializedPropertyType.Float && fn.Contains("aperturesize"))
                {
                    float before = p.floatValue;
                    float after = Mathf.Lerp(1f, before, 0.5f);             // 收拢量减半 → 圈小一圈
                    p.floatValue = after; touched++;
                    sb.AppendLine("  " + p.name + " " + before.ToString("0.###") + " → " + after.ToString("0.###"));
                }
                else if (p.propertyType == SerializedPropertyType.Color && fn.Contains("vignettecolor"))
                {
                    var c = p.colorValue;
                    var after = new Color(c.r, c.g, c.b, c.a * 0.5f);        // 黑得减半 → 屏幕不再发黑
                    p.colorValue = after; touched++;
                    sb.AppendLine("  " + p.name + ".a " + c.a.ToString("0.###") + " → " + after.a.ToString("0.###"));
                }
            }
            if (touched == 0) sb.AppendLine("  !! 没匹配到字段(下面列出实际字段)");
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(v);
        }

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + EditorSceneManager.SaveScene(scene));
        sb.AppendLine("\n改后:" + DumpAll().TrimEnd());
        Write(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑩ 回退(恢复 Snap 转头 + 环还原)")]
    public static void Revert()
    {
        var sb = new StringBuilder("== 体验⑩ 回退 ==\n");
        var rig = Rig();
        if (rig != null)
            foreach (var s in rig.GetComponentsInChildren<SnapTurnProviderBase>(true))
            { s.enabled = true; EditorUtility.SetDirty(s); sb.AppendLine("  启用 " + s.GetType().Name); }
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        sb.AppendLine("环的数值要从快照/提交里还原:copy " + Snapshot + " → " + ScenePath + ",或 git checkout HEAD~1 -- " + ScenePath);
        Write(sb.ToString());
    }

    // ------------------------------------------------------------ 读数

    static string DumpAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("---- 转头提供者 ----");
        var rig = Rig();
        if (rig == null) sb.AppendLine("  !! 找不到 XR Origin");
        else
        {
            foreach (var p in rig.GetComponentsInChildren<LocomotionProvider>(true))
            {
                sb.AppendLine("  " + p.GetType().Name + "  enabled=" + p.enabled);
                var so = new SerializedObject(p);
                foreach (var f in Props(so))
                    sb.AppendLine("      " + f.name + " [" + f.propertyType + "] = " + Val(f));
            }
        }
        sb.AppendLine("---- 防眩晕环 ----");
        foreach (var v in UnityEngine.Object.FindObjectsOfType<TunnelingVignetteController>(true))
        {
            sb.AppendLine("  " + v.name + " enabled=" + v.enabled);
            var so = new SerializedObject(v);
            // 顶层字段全列出来(第二套参数的实际名字要看到才知道),Generic 再下探一层
            foreach (var top in Props(so))
            {
                if (top.propertyType != SerializedPropertyType.Generic)
                { sb.AppendLine("    " + top.name + " = " + Val(top)); continue; }
                sb.AppendLine("    " + top.name + " (结构体):");
                var it = top.Copy(); var end = it.GetEndProperty();
                while (it.NextVisible(true) && !SerializedProperty.EqualContents(it, end))
                    sb.AppendLine("        " + it.name + " = " + Val(it));
            }
        }
        return sb.ToString();
    }

    static string Val(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Float: return p.floatValue.ToString("0.####");
            case SerializedPropertyType.Integer: return p.intValue.ToString();
            case SerializedPropertyType.Boolean: return p.boolValue.ToString();
            case SerializedPropertyType.Color: return p.colorValue.ToString();
            case SerializedPropertyType.ObjectReference: return p.objectReferenceValue == null ? "(空)" : p.objectReferenceValue.name;
            case SerializedPropertyType.Enum: return p.enumValueIndex.ToString();
            case SerializedPropertyType.Generic: return "(结构体)";
            default: return p.propertyType.ToString();
        }
    }

    static IEnumerable<SerializedProperty> Props(SerializedObject so)
    {
        var it = so.GetIterator();
        while (it.NextVisible(true)) yield return it.Copy();
    }

    static Transform Rig()
    {
        var origin = UnityEngine.Object.FindObjectsOfType<Unity.XR.CoreUtils.XROrigin>(true);
        return origin.Length == 0 ? null : origin[0].transform;
    }

    static void SnapshotOnce()
    {
        try
        {
            if (System.IO.File.Exists(Snapshot)) { Debug.Log("[TurnVig] 已有快照,不覆盖:" + Snapshot); return; }
            System.IO.Directory.CreateDirectory("Logs");
            System.IO.File.Copy(ScenePath, Snapshot);
            Debug.Log("[TurnVig] 落盘快照 " + Snapshot);
        }
        catch (Exception e) { Debug.LogWarning("[TurnVig] 快照失败:" + e.Message); }
    }

    static void Write(string text)
    {
        try { System.IO.Directory.CreateDirectory("Logs"); System.IO.File.WriteAllText(Report, text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[TurnVig] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}