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

        var allBooks = UnityEngine.Object.FindObjectsOfType<LibraryBook>(true);
        var byTitle = new Dictionary<string, LibraryBook>();
        foreach (var b in allBooks) { if (!byTitle.ContainsKey(b.title)) byTitle.Add(b.title, b); }

        // 空槽 = 正文还是占位文案的书。
        // (旧版用"有没有 SpineTitle 子物体"判断,但 v6.4 书籍电子屏化后全场已无 SpineTitle,
        //  所有槽位都会被当成空槽,新书就叠到了旧书上 —— 实测 5 格挤两本、5 格空着。)
        var free = allBooks.Where(IsPlaceholder).ToList();
        var eye = free.Where(b => OnShelf(b.transform) && NameLevel(b) <= 1)
                      .OrderBy(b => NameLevel(b)).ThenBy(b => b.slotCode).ToList();
        var rest = free.Except(eye).OrderBy(b => b.slotCode).ToList();
        var slots = eye.Concat(rest).ToList();     // 先视平线,再用尽其余占位槽

        int added = 0, updated = 0, skipped = 0, slotIdx = 0;
        foreach (var path in files)
        {
            string fname = Path.GetFileNameWithoutExtension(path);
            string title = (string.IsNullOrEmpty(titlePrefix) ? "" : titlePrefix)
                + (fname.Length > 3 && char.IsDigit(fname[0]) && fname[1] == '0' && fname[2] == '0' ? fname.Substring(3) : fname);
            string summary = CleanAndPaginate(File.ReadAllText(path, System.Text.Encoding.UTF8));

            LibraryBook target;
            if (byTitle.TryGetValue(title, out target))
            {
                target.summary = summary;             // 只更新正文,不动位置
                EditorUtility.SetDirty(target);
                WriteScreenTitle(target, title);
                updated++;
                continue;
            }
            if (slotIdx >= slots.Count) { skipped++; continue; }

            var slot = slots[slotIdx++];
            slot.title = title;
            slot.author = "VRlibrary 项目组";
            slot.summary = summary;
            WriteScreenTitle(slot, title);            // 电子屏书名(不改 transform:书本来就在格子里)
            VRLibraryFix.FitOne(slot);                // 长书名收进屏面
            EditorUtility.SetDirty(slot);
            added++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log(string.Format("[SolutionDocImporter] 完成:新增 {0} 本,更新 {1} 本,占位槽不足跳过 {2} 本(剩余可用槽 {3})。",
            added, updated, skipped, Mathf.Max(0, slots.Count - slotIdx)));
    }

    /// <summary>把书名写进这本书自己的电子屏 TitleText(每 6 字一行,与全场一致)。</summary>
    static void WriteScreenTitle(LibraryBook b, string title)
    {
        var tm = FindDeep<TextMesh>(b.transform, "TitleText");
        if (tm == null) { Debug.LogWarning("[SolutionDocImporter] " + b.gameObject.name + " 没有 TitleText,屏上不会显示书名"); return; }
        int per = 6;
        var sb = new StringBuilder();
        for (int i = 0; i < title.Length; i++)
        {
            sb.Append(title[i]);
            if ((i + 1) % per == 0 && i < title.Length - 1) sb.Append('\n');
        }
        tm.text = sb.ToString();
        EditorUtility.SetDirty(tm);
    }

    static T FindDeep<T>(Transform root, string name) where T : Component
    {
        foreach (var c in root.GetComponentsInChildren<T>(true)) if (c.gameObject.name == name) return c;
        return null;
    }

    /// <summary>正文仍是占位文案 = 这本书还没被真实馆藏占用(实测精确对应 235 本)。</summary>
    static bool IsPlaceholder(LibraryBook b)
    {
        if (b == null) return false;
        string s = b.summary ?? "";
        return s.StartsWith("占位书籍") || s.StartsWith("二层回廊藏书") || s.StartsWith("详情待录入");
    }

    static bool OnShelf(Transform t)
    {
        while (t != null)
        {
            if (t.name == "Bookshelves" || t.name == "Shelves2F") return true;
            t = t.parent;
        }
        return false;
    }

    /// <summary>Book_&lt;架&gt;_&lt;层&gt;_&lt;序&gt; / Book2F_&lt;区排&gt;_&lt;层&gt;_&lt;序&gt; 里的层号。</summary>
    static int NameLevel(LibraryBook b)
    {
        var p = b.gameObject.name.Split('_');
        int v;
        return p.Length >= 4 && int.TryParse(p[p.Length - 2], out v) ? v : 9;
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
}
