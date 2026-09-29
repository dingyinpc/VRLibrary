using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 体验⑪:点击链路的**静态接线诊断**(只报告)。
/// 用户 2026-09-28 反馈"点书点不开"。点不响的可能原因有一串,先把这个链路的每一段读出来:
///   ① 书在哪个物理层、有没有 Collider;
///   ② 射线交互器的 **raycastMask(物理层遮罩)** 是否包含书的层 —— 这是最可能的元凶:
///      抓取走的是"手碰碰撞体"(overlap),**不需要射线**;所以以前能抓不代表射线打得中;
///   ③ 交互器/可交互体的 **interactionLayers** 是否对得上;
///   ④ 选中动作(selectInput)到底有没有绑上;
///   ⑤ 场景里有没有 XRInteractionManager。
/// 运行期那条(真·SelectEnter → 面板会不会开)在冒烟测试的 "书籍点击链路" 一项里跑。
/// </summary>
public static class VRLibraryClickDiag
{
    [MenuItem("VRLibrary/体验·⑪ 点击链路诊断(只报告)")]
    public static void Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 体验⑪ 点击链路诊断 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var mgr = UnityEngine.Object.FindObjectsOfType<XRInteractionManager>(true);
        sb.AppendLine("XRInteractionManager:" + (mgr.Length == 0 ? "!! 没有" : mgr.Length + " 个(" + mgr[0].name + " enabled=" + mgr[0].enabled + ")"));

        // ---- 书
        var books = UnityEngine.Object.FindObjectsOfType<LibraryBook>(true);
        sb.AppendLine("\n书本 " + books.Length + " 本。抽样前 3 本:");
        foreach (var b in books.Take(3))
        {
            var col = b.GetComponent<Collider>();
            var inter = b.GetComponent<XRBaseInteractable>();
            sb.AppendLine("  " + b.name + "  层=" + LayerMask.LayerToName(b.gameObject.layer) + "(" + b.gameObject.layer + ")"
                + "  碰撞体=" + (col == null ? "!! 无" : col.GetType().Name + " enabled=" + col.enabled + " isTrigger=" + col.isTrigger)
                + "  可交互=" + (inter == null ? "!! 无" : inter.GetType().Name + " enabled=" + inter.enabled + " interactionLayers=" + Bits(inter.interactionLayers.value)));
        }

        // ---- 交互器
        sb.AppendLine("\n交互器:");
        foreach (var it in UnityEngine.Object.FindObjectsOfType<XRBaseInteractor>(true))
        {
            var ray = it as XRRayInteractor;
            var baseCtrl = it as XRBaseControllerInteractor;
            sb.AppendLine("  " + it.GetType().Name + " [" + it.gameObject.name + "] enabled=" + it.enabled
                + "  active=" + it.gameObject.activeInHierarchy
                + " interactionLayers=" + Bits(it.interactionLayers.value));
            if (ray != null)
                sb.AppendLine("      raycastMask(物理层)=" + Layers(ray.raycastMask.value)
                    + "  maxDistance=" + ray.maxRaycastDistance + "  lineType=" + ray.lineType);
            if (baseCtrl != null)
            {
                // 不再猜字段名:把名字里带 action/select/activate 的序列化属性全列出来(含它的引用指向谁)
                var so = new SerializedObject(baseCtrl);
                var iter = so.GetIterator();
                while (iter.NextVisible(true))
                {
                    var p = iter.Copy();
                    var pn = p.name.ToLower();
                    if (!(pn.Contains("action") || pn.Contains("select") || pn.Contains("activate") || pn.Contains("hover"))) continue;
                    string extra = "";
                    var r = p.FindPropertyRelative("m_Reference");
                    if (r != null) extra = " → m_Reference=" + (r.objectReferenceValue == null ? "(空)" : r.objectReferenceValue.name);
                    else
                    {
                        var act = p.FindPropertyRelative("m_Action");
                        if (act != null)
                        {
                            var map = act.FindPropertyRelative("m_ActionMap");
                            var nm = act.FindPropertyRelative("m_Name");
                            extra = " → 直接 InputAction(name=" + (nm == null ? "?" : nm.stringValue)
                                + ", map=" + (map == null || map.objectReferenceValue == null ? "空" : map.objectReferenceValue.name) + ")";
                        }
                    }
                    sb.AppendLine("      " + p.name + " [" + p.propertyType + "]" + extra);
                }
            }
        }

        // ---- 控制器:XRI 2.x 的输入动作真正挂在这里(交互器只是听它的状态)
        // ⚠ 我前面一直在交互器上找 m_SelectAction,所以永远"找不到" —— 那些字段在 ActionBasedController 上。
        sb.AppendLine("\n控制器(输入动作真正挂在这里):");
        foreach (var c in UnityEngine.Object.FindObjectsOfType<XRBaseController>(true))
        {
            sb.AppendLine("  " + c.GetType().Name + " [" + c.gameObject.name + "] enabled=" + c.enabled
                + " active=" + c.gameObject.activeInHierarchy);
            var cso = new SerializedObject(c);
            var cit = cso.GetIterator();
            while (cit.NextVisible(true))
            {
                var p = cit.Copy();
                if (!p.name.ToLower().Contains("action")) continue;
                var r = p.FindPropertyRelative("m_Reference");
                string val;
                if (r != null) val = r.objectReferenceValue == null ? "**(空!按了也没用)**" : r.objectReferenceValue.name;
                else
                {
                    var nm = p.FindPropertyRelative("m_Name");
                    val = "(直接 InputAction) name=" + (nm == null ? "?" : nm.stringValue);
                }
                sb.AppendLine("      " + p.name + " = " + val);
            }
        }

        // ---- 物理层总览(看书的层和别的物体是否在同一层)
        sb.AppendLine("\n物理层占用:");
        var counts = new int[32];
        foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>(true)) counts[go.layer]++;
        for (int i = 0; i < 32; i++)
            if (counts[i] > 0) sb.AppendLine("  " + i + " " + LayerMask.LayerToName(i) + " : " + counts[i] + " 个物体");

        Write(sb.ToString());
    }

    static string Bits(int v)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 32; i++) if ((v & (1 << i)) != 0) sb.Append(i).Append(',');
        return sb.Length == 0 ? "0(空!)" : sb.ToString().TrimEnd(',');
    }

    static string Layers(int v)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 32; i++)
            if ((v & (1 << i)) != 0)
            {
                var n = LayerMask.LayerToName(i);
                sb.Append(string.IsNullOrEmpty(n) ? i.ToString() : n).Append(',');
            }
        return sb.Length == 0 ? "0(空!)" : sb.ToString().TrimEnd(',');
    }

    static void Write(string text)
    {
        try { System.IO.Directory.CreateDirectory("Logs"); System.IO.File.WriteAllText("Logs/click-diag.txt", text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[ClickDiag] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}