using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 智能图书馆 · 文件夹书籍批量上架(批处理)
/// 用法:
///   1. 编辑器菜单:VRLibrary/导入方案文档为馆藏书(默认读实现方案文件夹);
///      VRLibrary/导入文件夹书籍为馆藏书(弹窗自选任意 md 文件夹);
///   2. 命令行批处理(不开编辑器):
///      Unity.exe -batchmode -projectPath ... -executeMethod SolutionDocImporter.ImportDefault -quit
/// 特性:
///   · 幂等:按书名匹配,已导入的只更新正文,不重复占位;
///   · 自动找书架中层(level 0/1)空位,放大/前移/生成书脊标题+底板;
///   · markdown 轻量清洗,每 400 字自动分页(\f)。
/// </summary>
public static class SolutionDocImporter
{
    const string kDefaultDir = @"E:\Unity xianmu\VR\XR学习知识库\VRlibrary实现方案";
    const string kPrefix = "实现方案 · ";

    [MenuItem("VRLibrary/导入方案文档为馆藏书")]
    public static void ImportFromMenu()
    {
        ImportFrom(kDefaultDir, kPrefix);
    }

    [MenuItem("VRLibrary/导入文件夹书籍为馆藏书(自选文件夹)")]
    public static void ImportPickFolder()
    {
        string dir = EditorUtility.OpenFolderPanel("选择包含 .md 书籍的文件夹", kDefaultDir, "");
        if (string.IsNullOrEmpty(dir)) return;
        ImportFrom(dir, "");
    }

    /// <summary>命令行批处理入口。</summary>
    public static void ImportDefault()
    {
        ImportFrom(kDefaultDir, kPrefix);
    }

    public static void ImportFrom(string dir, string titlePrefix)
    {
        if (!Directory.Exists(dir)) { Debug.LogError("[SolutionDocImporter] 文件夹不存在:" + dir); return; }
        var files = Directory.GetFiles(dir, "*.md");
        Array.Sort(files);
        if (files.Length == 0) { Debug.LogError("[SolutionDocImporter] 文件夹里没有 .md 文件:" + dir); return; }

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/_Scenes/SmartLibrary.unity")
            scene = EditorSceneManager.OpenScene("Assets/_Scenes/SmartLibrary.unity", OpenSceneMode.Single);
        var shelves = GameObject.Find("SmartLibrary").transform.Find("Bookshelves");
        if (shelves == null) { Debug.LogError("[SolutionDocImporter] 场景缺 Bookshelves"); return; }

        var allBooks = UnityEngine.Object.FindObjectsOfType<LibraryBook>(true);
        var byTitle = new Dictionary<string, LibraryBook>();
        foreach (var b in allBooks) { if (!byTitle.ContainsKey(b.title)) byTitle.Add(b.title, b); }

        // 空位:中层(0/1 层)、尚无书脊(非真实书)
        var free = new List<Transform>();
        for (int s = 0; s < shelves.childCount; s++) {
            var shelf = shelves.GetChild(s);
            for (int b = 0; b < shelf.childCount; b++) {
                var book = shelf.GetChild(b);
                if (!book.name.StartsWith("Book_")) continue;
                var pp = book.name.Split('_');
                if (int.Parse(pp[2]) > 1) continue;
                if (book.Find("SpineTitle") != null) continue;
                free.Add(book);
            }
        }

        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var plateMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_LabelPlate.mat");
        int added = 0, updated = 0, skipped = 0;
        int freeIdx = 0;

        foreach (var path in files) {
            string fname = Path.GetFileNameWithoutExtension(path);
            string title = (string.IsNullOrEmpty(titlePrefix) ? "" : titlePrefix)
                + (fname.Length > 3 && char.IsDigit(fname[0]) && fname[1] == '0' && fname[2] == '0' ? fname.Substring(3) : fname);
            string summary = CleanAndPaginate(File.ReadAllText(path, System.Text.Encoding.UTF8));

            LibraryBook target;
            if (byTitle.TryGetValue(title, out target)) {
                target.summary = summary;
                EditorUtility.SetDirty(target);
                updated++;
                continue;
            }
            if (freeIdx >= free.Count) { skipped++; continue; }
            var slot = free[freeIdx++];

            var lib = slot.GetComponent<LibraryBook>();
            lib.title = title;
            lib.author = "VRlibrary 项目组";
            lib.summary = summary;

            var pp = slot.name.Split('_');
            int level = int.Parse(pp[2]);
            float boardTop = 0.35f + level * 0.9f + 0.03f;
            float newH = Mathf.Max(0.27f, slot.localScale.y + 0.03f);
            slot.localScale = new Vector3(0.42f, newH, 0.1f);
            var p = slot.localPosition;
            p.y = boardTop + newH * 0.5f; p.z += 0.095f;
            slot.localPosition = p;

            BuildSpineLabel(slot, font, plateMat, title);
            added++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log(string.Format("[SolutionDocImporter] 完成:新增 {0} 本,更新 {1} 本,槽位不足跳过 {2} 本。", added, updated, skipped));
    }

    static string CleanAndPaginate(string raw)
    {
        string text = raw.Replace("```", "").Replace("**", "")
            .Replace("# ", "\n").Replace("- ", "· ")
            .Replace("::zcode-file-citation{path=", "[文件:").Replace("\" purpose=\"output\"}", "]");
        var sb = new StringBuilder();
        int chars = 0;
        foreach (char ch in text) {
            sb.Append(ch);
            if (ch != '\n') chars++;
            if (chars >= 400) { sb.Append('\f'); chars = 0; }
        }
        return sb.ToString();
    }

    static void BuildSpineLabel(Transform book, Font font, Material plateMat, string title)
    {
        var stGo = new GameObject("SpineTitle");
        stGo.transform.SetParent(book, false);
        var st = stGo.transform;
        var tm = st.gameObject.AddComponent<TextMesh>();
        tm.font = font; tm.fontSize = 48; tm.characterSize = 0.015f;
        tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
        tm.color = new Color(0.75f, 0.98f, 1f);
        string plain = title.Length > 12 ? title.Substring(0, 12) + "…" : title;
        var sbT = new StringBuilder();
        for (int c = 0; c < plain.Length; c++) {
            sbT.Append(plain[c]);
            if ((c + 1) % 4 == 0 && c < plain.Length - 1) sbT.Append('\n');
        }
        tm.text = sbT.ToString();
        st.localRotation = Quaternion.Euler(0f, 180f, 0f);   // TextMesh 从 -Z 面阅读
        st.localPosition = new Vector3(0f, 0f, 0.054f);
        var mr = st.gameObject.GetComponent<MeshRenderer>();
        if (mr != null && tm.font != null) mr.sharedMaterial = tm.font.material;

        int lines = 1;
        foreach (char ch in tm.text) if (ch == '\n') lines++;
        var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plate.name = "LabelPlate"; plate.transform.SetParent(stGo.transform, false);
        plate.transform.localPosition = new Vector3(0f, 0f, 0.001f);
        plate.transform.localScale = new Vector3(0.44f, lines * 0.078f + 0.02f, 0.002f);
        plate.GetComponent<MeshRenderer>().sharedMaterial = plateMat;
        var pc = plate.GetComponent<BoxCollider>();
        if (pc != null) UnityEngine.Object.DestroyImmediate(pc);
    }
}
