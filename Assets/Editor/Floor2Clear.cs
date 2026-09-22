#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 打通二楼回廊:把两处"看不见但堵路"的东西改掉。菜单 VRLibrary/打通二楼回廊(改场景)。
///
/// 病灶是 `Logs/floor2-audit.txt` 量出来的,不是猜的:
/// ① **传送垫是 1.6 m 高的实心胶囊**。`VRLibrarySetup.MakePad` 用 `CreatePrimitive(Cylinder)`,
///    而 Unity 给 Cylinder 原语自带 CapsuleCollider;垫子盘只有 2.4 cm 高,碰撞体却到人腰/胸口,
///    1.6 m 宽 × 1.6 m 高立在走道正中 → 北/南/东三条走廊各被堵死 1.70 m(断面实际空档只剩 0.10~0.20 m)。
///    改法:换成**贴着盘面的扁盒**(1.6 m 见方、6 cm 高),瞄准射线照样打得中,人直接走过去。
///    为什么不能把胶囊压扁:Unity 强制 capsule 的 height ≥ 2×radius,要 0.06 m 高就只剩 0.03 m 半径;
///    而 Unity **没有 CylinderCollider**(CS0246),所以用盒子外接圆盘。
/// ② **`AirWalls/AirWall_West1F` 高 4 m**,而它只是给一层用的隐形墙 —— 二楼西走廊板面顶 3.005,
///    于是它从 x=-18.8 处把 1.9 m 宽的西走廊整条劈成两半(33.8 m 全程)。改法:顶面降到 2.75 m
///    (回廊板底 2.755),一层照样拦住,二楼放开。
///    另两块 AirWall_North2F / East2F 本来就贴在板外沿(x≈19.7 / z≈-19.7),是二楼的防坠墙,不动。
///
/// 改之前先把场景字节级快照到 Logs/SmartLibrary.pre-floor2.unity(存在就不覆盖),
/// 改完立刻重跑一遍通行性体检,报告写 Logs/floor2-clear.txt。
/// </summary>
public static class Floor2Clear
{
    const string kScene = "Assets/_Scenes/SmartLibrary.unity";
    const string kLog = "Logs/floor2-clear.txt";
    const string kSnapshot = "Logs/SmartLibrary.pre-floor2.unity";

    const float PadColliderHeight = 0.06f;    // 垫子瞄准体改成这么高
    const float WestAirWallTop = 2.75f;       // 一层隐形墙的顶(回廊板底 2.755)

    [MenuItem("VRLibrary/打通二楼回廊(改场景)")]
    public static void Run()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != kScene)
        {
            Debug.LogError("[打通二楼] 当前场景不是 SmartLibrary:" + scene.path);
            return;
        }
        if (!Snapshot()) return;
        Physics.SyncTransforms();

        var sb = new StringBuilder();
        sb.AppendLine("打通二楼回廊  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("快照:" + kSnapshot);

        EditorSceneManager.MarkSceneDirty(scene);
        int flat = FlattenPads(sb), wall = LowerWestAirWall(sb);
        if (flat == 0 && wall == 0) sb.AppendLine("!! 两处都没改动 —— 要么已经修过,要么目标对象没找到,别当成成功");

        AssetDatabase.SaveAssets();
        bool saved = EditorSceneManager.SaveScene(scene);
        sb.AppendLine(string.Format("保存场景:{0}  isDirty(应为 False)={1}", saved ? "成功" : "失败",
            scene.isDirty));
        File.WriteAllText(kLog, sb.ToString());
        Debug.Log("[打通二楼] " + sb);
    }

    /// <summary>
    /// 把每个传送锚点的碰撞体换成"贴着盘面"的扁盒(幂等:已经是扁盒的也会重新落位)。
    /// 落位基准是**锚点自己的 transform 平面** —— 第一版按旧胶囊的底面放,结果一层垫子的瞄准体
    /// 掉到地板下 0.7 m、二楼的悬在板下,传送直接打不中。
    /// </summary>
    static int FlattenPads(StringBuilder sb)
    {
        sb.AppendLine("  [① 传送垫碰撞体]");
        int n = 0;
        foreach (var a in Object.FindObjectsOfType<TeleportationAnchor>())
        {
            var t = a.transform;
            float sx = Mathf.Max(0.0001f, Mathf.Abs(t.lossyScale.x));
            float sy = Mathf.Max(0.0001f, Mathf.Abs(t.lossyScale.y));
            float sz = Mathf.Max(0.0001f, Mathf.Abs(t.lossyScale.z));

            var cap = a.GetComponent<CapsuleCollider>();
            var box = a.GetComponent<BoxCollider>();
            float worldR = cap != null ? cap.radius * Mathf.Max(sx, sz) : 0.8f;
            float oldH = cap != null ? cap.bounds.max.y - cap.bounds.min.y
                                     : (box != null ? box.bounds.size.y : 0f);

            if (cap != null) Undo.DestroyObjectImmediate(cap);
            if (box == null) box = Undo.AddComponent<BoxCollider>(a.gameObject);
            box.size = new Vector3(worldR * 2f / sx, PadColliderHeight / sy, worldR * 2f / sz);
            box.center = Vector3.zero;          // 盒子中心就压在盘面所在的高度
            EditorUtility.SetDirty(box);

            var nb = box.bounds;
            float gap = nb.max.y - t.position.y;
            bool ok = nb.size.y < 0.2f && Mathf.Abs(gap) < 0.15f;
            sb.AppendLine(string.Format("     {0,-22} 原碰撞体高 {1:0.00} m → 扁盒 {2:0.00}×{3:0.00}×{4:0.00} m 顶面比盘面 {5:+0.00;-0.00} m {6}",
                Node(t), oldH, nb.size.x, nb.size.y, nb.size.z, gap, ok ? "✓" : "!! 落位不对"));
            n++;
        }
        if (n == 0) sb.AppendLine("     !! 一个传送锚点都没找到");
        return n;
    }

    /// <summary>一层西侧隐形墙降到回廊板底以下。返回改动个数。</summary>
    static int LowerWestAirWall(StringBuilder sb)
    {
        sb.AppendLine("  [② 一层西侧隐形墙]");
        int n = 0, found = 0;
        foreach (var c in Object.FindObjectsOfType<BoxCollider>())
        {
            if (!c.name.Contains("AirWall_West")) continue;
            found++;
            var t = c.transform;
            var b = c.bounds;
            if (b.max.y <= WestAirWallTop)
            {
                sb.AppendLine(string.Format("     {0} 顶面已经只有 {1:0.00} m,不用改", Node(t), b.max.y));
                continue;
            }
            float newTop = WestAirWallTop;
            float newH = newTop - b.min.y;
            float sy = Mathf.Max(0.0001f, Mathf.Abs(t.lossyScale.y));
            Undo.RecordObject(c, "Lower west air wall");
            c.size = new Vector3(c.size.x, newH / sy, c.size.z);
            // 中心沿 Y 挪到新的中点(注意 center 是局部坐标)
            float midWorld = b.min.y + newH * 0.5f;
            c.center = new Vector3(c.center.x, c.center.y + (midWorld - (b.min.y + b.max.y) * 0.5f) / sy, c.center.z);
            EditorUtility.SetDirty(c);
            var nb = c.bounds;
            sb.AppendLine(string.Format("     {0} y[{1:0.00},{2:0.00}] → y[{3:0.00},{4:0.00}] {5}",
                Node(t), b.min.y, b.max.y, nb.min.y, nb.max.y, nb.max.y <= WestAirWallTop + 0.01f ? "✓ 不再穿到二楼" : "!! 仍然顶到二楼"));
            n++;
        }
        if (found == 0) sb.AppendLine("     !! 没找到 AirWall_West* —— 名字变了?先跑二楼体检看清单");
        return n;
    }

    static bool Snapshot()
    {
        try
        {
            Directory.CreateDirectory("Logs");
            if (!File.Exists(kSnapshot))
            {
                File.WriteAllBytes(kSnapshot, File.ReadAllBytes(kScene));
                Debug.Log("[打通二楼] 快照已存 " + kSnapshot);
            }
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError("[打通二楼] 快照失败,先不动场景:" + e.Message);
            return false;
        }
    }

    static string Node(Transform t)
    {
        string s = t.name;
        for (var p = t.parent; p != null && p.name != "SmartLibrary"; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}
#endif
