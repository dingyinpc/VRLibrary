using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 智能图书馆 · 二层回廊传送锚点布设。
/// 菜单 VRLibrary/在二层回廊布设传送锚点。
///
/// 为什么需要:二层只能靠走楼梯或"检索→传送到书前"到达,地面 4 个传送锚点全在一层,
/// 摇杆点对点传送上不去。
///
/// 做法:直接克隆现场已有的 SmartLibrary/Pad_Shelves(它已带 MeshFilter/MeshRenderer/
/// CapsuleCollider/TeleportationAnchor 与 Halo 子物体),这样组件配置原样继承,
/// 不手搭、也不会和一层锚点行为不一致。按名幂等:再跑一次只挪位不新增。
/// 摆位依据(实测):回廊板面高 y=3.005(板中心 2.88 + 半厚 0.125),
///   北/南板 z=±18.5 深 2.6、东板 x=18.5 深 2.6、西板 x=-18.85 深 1.9;
///   二层书架 Shelf2F_E 在 z=-19.35、Shelf2F_G 在 z=+19.35、Shelf2F_F 在 x=+19.35,
///   所以锚点放在各书架组正前方约 1.1~1.3 m 的板上,楼梯口另设一个。
/// 每个落点都向下打射线自检是否真踩在板上,结果写 Logs/anchors-2f.txt。
/// </summary>
public static class VRLibraryAnchors2F
{
    const float PadY = 3.045f;      // 板面 3.005 + 一层垫高(一层 Pad 在 0.04)
    static readonly Vector3[] kSpots =
    {
        new Vector3(-8f,  PadY, -18.2f),   // 北走道西段:对 Shelf2F_E(x=-12,-4)
        new Vector3( 8f,  PadY, -18.2f),   // 北走道东段:对 Shelf2F_E(x=4,12)
        new Vector3(-8f,  PadY,  18.2f),   // 南走道西段:对 Shelf2F_G(x=-12,-4)
        new Vector3( 8f,  PadY,  18.2f),   // 南走道东段:对 Shelf2F_G(x=4,12)
        new Vector3(18.2f, PadY,  9.5f),   // 东走道:对 Shelf2F_F(z=5,14)
        new Vector3(18.3f, PadY, -10.5f),  // 楼梯口(楼梯在 x≈17.1,z=-10.5)
    };
    static readonly string[] kNames =
    {
        "Pad_2F_N_W", "Pad_2F_N_E", "Pad_2F_S_W", "Pad_2F_S_E", "Pad_2F_E", "Pad_2F_Stair"
    };

    [MenuItem("VRLibrary/在二层回廊布设传送锚点")]
    public static void Place()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/_Scenes/SmartLibrary.unity")
        {
            Debug.LogError("[Anchors2F] 当前场景不是 SmartLibrary:" + scene.path);
            return;
        }

        var src = GameObject.Find("SmartLibrary/Pad_Shelves");
        if (src == null)
        {
            Debug.LogError("[Anchors2F] 找不到样板 SmartLibrary/Pad_Shelves,无法克隆");
            return;
        }
        var root = src.transform.parent;
        Physics.SyncTransforms();

        var log = new StringBuilder();
        log.AppendLine("二层回廊传送锚点布设  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        log.AppendLine("样板:" + src.name + "  父级:" + (root != null ? root.name : "无"));
        int added = 0, moved = 0, bad = 0;

        for (int i = 0; i < kSpots.Length; i++)
        {
            Transform go = root != null ? root.Find(kNames[i]) : null;
            bool isNew = go == null;
            if (isNew)
            {
                var clone = Object.Instantiate(src, root);
                clone.name = kNames[i];
                Undo.RegisterCreatedObjectUndo(clone, "创建 " + kNames[i]);
                go = clone.transform;
                added++;
            }
            var from = go.position;
            go.position = kSpots[i];
            if (!isNew && Vector3.Distance(from, kSpots[i]) > 0.01f) moved++;

            // 自检:向下 0.8 m 内必须打到板面,否则玩家会传送到空中或板下
            Physics.SyncTransforms();
            Ray ray = new Ray(kSpots[i] + Vector3.up * 0.6f, Vector3.down);
            var hits = Physics.RaycastAll(ray, 1.6f);
            string landed = hits.Length > 0 ? hits[0].collider.gameObject.name : "(空)";
            float dropY = hits.Length > 0 ? hits[0].point.y : -99f;
            bool ok = hits.Length > 0 && dropY > 2.85f && dropY < 3.15f;
            if (!ok) bad++;
            log.AppendLine(string.Format("  {0} @ ({1:F2},{2:F2},{3:F2})  下方打到「{4}」y={5:F3}  {6}",
                kNames[i], kSpots[i].x, kSpots[i].y, kSpots[i].z, landed, dropY,
                ok ? "踩在回廊板上" : "!! 没踩到板面"));
        }

        log.AppendLine(string.Format("新增 {0} 个,挪位 {1} 个,落点异常 {2} 个", added, moved, bad));
        EditorSceneManager.MarkSceneDirty(scene);
        log.AppendLine("保存场景:" + (EditorSceneManager.SaveScene(scene) ? "成功" : "失败"));

        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/anchors-2f.txt", log.ToString(), System.Text.Encoding.UTF8);
        Debug.Log("[Anchors2F] 完成 → Logs/anchors-2f.txt(新增 " + added + ",异常 " + bad + "）");
    }
}
