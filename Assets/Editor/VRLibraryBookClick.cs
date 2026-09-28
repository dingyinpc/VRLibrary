using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 性能⑨:把书改成"**点击即读**"(去掉抓取与刚体),为的是让它们能进静态合批。
/// 菜单 VRLibrary/性能·⑨ xxx。
///
/// 为什么这是一个性能改动而不是单纯的交互改动:
///   可抓 ⇒ 必须有 Rigidbody ⇒ 被判成"会动的东西" ⇒ **永远进不了静态合批**。
///   而书架两区 1276 个渲染器里大部分是书,A1 那次补标就是因为这条规则把它们全放过了
///   (报告里"自身有 Rigidbody 262 / 祖先有 Rigidbody 524")。
///   用户定案:书只需要点击就能看 —— 于是这条锁打开了。
///
/// 本菜单做三件事(逐件):
///   ① 删 `Rigidbody`(261~262 个);② 删 `XRGrabInteractable`(262 个);
///   ③ 补 `XRSimpleInteractable`(点一下 = 开阅读面板,Collider 保留)。
/// 之后必须再跑一次 `性能·① 静态合批补标`,书才会真的被合批。
///
/// 回退:git checkout HEAD -- Assets/_Scenes/SmartLibrary.unity,
///       或快照 Logs/SmartLibrary.pre-bookclick.unity(首跑落盘,不覆盖)。
/// </summary>
public static class VRLibraryBookClick
{
    const string ScenePath = "Assets/_Scenes/SmartLibrary.unity";
    const string Snapshot = "Logs/SmartLibrary.pre-bookclick.unity";

    [MenuItem("VRLibrary/性能·⑨ 书本改成点击阅读(去抓取 + 去刚体,改场景)")]
    public static void Convert()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) { Debug.LogError("[BookClick] 当前场景不是 SmartLibrary:" + scene.path); return; }
        SnapshotOnce();

        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑨ 书本改成点击阅读 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var books = UnityEngine.Object.FindObjectsOfType<LibraryBook>(true);
        int rbGone = 0, grabGone = 0, clickAdded = 0, noCol = 0;
        foreach (var b in books)
        {
            // ⚠ 顺序不能反:XRGrabInteractable 会把 Rigidbody 自动补回来(RequireComponent),
            // 所以必须**先拆抓取组件,再删刚体**。第一次就是反着写,删完一看 262 个刚体全回来了。
            var grab = b.GetComponent<XRGrabInteractable>();
            if (grab != null) { UnityEngine.Object.DestroyImmediate(grab); grabGone++; }

            if (b.GetComponent<XRSimpleInteractable>() == null)
            { b.gameObject.AddComponent<XRSimpleInteractable>(); clickAdded++; }

            var rb = b.GetComponent<Rigidbody>();
            if (rb != null) { UnityEngine.Object.DestroyImmediate(rb); rbGone++; }
            // 再确认一次:万一还有别的组件往上补刚体,这里会当场暴露
            var still = b.GetComponent<Rigidbody>();
            if (still != null) { UnityEngine.Object.DestroyImmediate(still); rbGone++; }

            if (b.GetComponent<Collider>() == null) noCol++;
        }

        sb.AppendLine("书本 " + books.Length + " 本:删 Rigidbody " + rbGone + "、删 XRGrabInteractable " + grabGone
            + "、补 XRSimpleInteractable " + clickAdded);
        if (noCol > 0) sb.AppendLine("  !! " + noCol + " 本没有 Collider —— 射线点不到,必须补");
        // 残留自证
        int leftGrab = UnityEngine.Object.FindObjectsOfType<XRGrabInteractable>(true).Length;
        int leftRb = 0;
        foreach (var b in books) if (b.GetComponent<Rigidbody>() != null) leftRb++;
        sb.AppendLine("残留自证:场上 XRGrabInteractable " + leftGrab + " 个,书本上 Rigidbody " + leftRb + " 个(都应 0)");

        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("保存场景:" + EditorSceneManager.SaveScene(scene) + "  isDirty(应为 False)=" + scene.isDirty);
        sb.AppendLine("\n下一步:跑 性能·① 静态合批补标(改场景并自动保存),书与书名才真的进合批。");
        sb.AppendLine("回退:git checkout HEAD -- " + ScenePath + " 或 copy " + Snapshot);
        Flush(sb.ToString());
    }

    [MenuItem("VRLibrary/性能·⑨ 复核(只报告)")]
    public static void Verify()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 性能⑨ 复核:书本交互 ==");
        sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var books = UnityEngine.Object.FindObjectsOfType<LibraryBook>(true);
        int click = 0, grab = 0, rb = 0, col = 0, batched = 0, textBatched = 0;
        foreach (var b in books)
        {
            if (b.GetComponent<XRSimpleInteractable>() != null) click++;
            if (b.GetComponent<XRGrabInteractable>() != null) grab++;
            if (b.GetComponent<Rigidbody>() != null) rb++;
            if (b.GetComponent<Collider>() != null) col++;
            if ((GameObjectUtility.GetStaticEditorFlags(b.gameObject) & StaticEditorFlags.BatchingStatic) != 0) batched++;
            foreach (var t in b.GetComponentsInChildren<TextMesh>(true))
                if ((GameObjectUtility.GetStaticEditorFlags(t.gameObject) & StaticEditorFlags.BatchingStatic) != 0) textBatched++;
        }
        sb.AppendLine("书本 " + books.Length + " 本:可点击 " + click + "  残留可抓 " + grab + "  残留刚体 " + rb
            + "  有碰撞体 " + col);
        sb.AppendLine("其中已标 BatchingStatic 的书 " + batched + " 本,书上的书名 TextMesh 已标 " + textBatched + " 个");
        sb.AppendLine(batched == books.Length ? "✅ 全部可合批" : "⚠ 还没全标 —— 去跑 性能·① 静态合批补标");
        Flush(sb.ToString());
    }

    static void SnapshotOnce()
    {
        try
        {
            if (File.Exists(Snapshot)) { Debug.Log("[BookClick] 已有快照,不覆盖:" + Snapshot); return; }
            Directory.CreateDirectory("Logs");
            File.Copy(ScenePath, Snapshot);
            Debug.Log("[BookClick] 落盘快照 " + Snapshot);
        }
        catch (Exception e) { Debug.LogWarning("[BookClick] 快照失败:" + e.Message); }
    }

    static void Flush(string text)
    {
        try { Directory.CreateDirectory("Logs"); File.WriteAllText("Logs/perf-bookclick.txt", text, new UTF8Encoding(false)); }
        catch (Exception e) { Debug.LogWarning("[BookClick] 落盘失败:" + e.Message); }
        Debug.Log(text);
    }
}