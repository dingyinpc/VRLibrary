using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 智能图书馆 · 馆藏数据批量导入工具。
/// 用法:
/// 1. 把书籍清单放到 Assets/Data/bookdata.json(格式见下方示例);
/// 2. 菜单 VRLibrary/导入馆藏数据(JSON),自动按顺序填入场景中的 LibraryBook;
///    或 batch 模式 -executeMethod BookDataImporter.ImportFromMenu。
/// JSON 格式(数组,顺序即场景书籍顺序 Book_0_0_0 → Book_3_2_7):
/// [
///   { "title": "三体", "author": "刘慈欣", "summary": "科幻长篇。" },
///   ...
/// ]
/// 也可用 "slot": "Book_0_0_3" 指定某本书;未提供的字段保持原值。
/// </summary>
public static class BookDataImporter
{
    [Serializable]
    private class BookEntry
    {
        public string slot;      // 可选:目标书名(如 Book_0_0_3)
        public string title;
        public string author;
        public string summary;
    }

    [MenuItem("VRLibrary/导入馆藏数据(JSON)")]
    public static void ImportFromMenu()
    {
        Import();
    }

    public static void Import()
    {
        const string dataPath = "Assets/Data/bookdata.json";
        if (!File.Exists(dataPath))
        {
            Debug.LogError($"[BookDataImporter] 找不到 {dataPath},请先放入书籍清单 JSON。");
            return;
        }

        List<BookEntry> entries;
        try
        {
            entries = JsonUtilityArrayWrapper.FromJson<BookEntry>(File.ReadAllText(dataPath));
        }
        catch (Exception e)
        {
            Debug.LogError("[BookDataImporter] JSON 解析失败:" + e.Message);
            return;
        }
        if (entries == null || entries.Count == 0)
        {
            Debug.LogError("[BookDataImporter] JSON 中没有书籍条目。");
            return;
        }

        // 若当前打开的不是 SmartLibrary 场景则先打开
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != "Assets/_Scenes/SmartLibrary.unity")
            scene = EditorSceneManager.OpenScene("Assets/_Scenes/SmartLibrary.unity", OpenSceneMode.Single);

        var books = UnityEngine.Object.FindObjectsOfType<LibraryBook>()
            .OrderBy(b => b.name, StringComparer.Ordinal)
            .ToList();
        var byName = books.ToDictionary(b => b.name, b => b);

        int applied = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            LibraryBook target = null;
            var entry = entries[i];

            if (!string.IsNullOrEmpty(entry.slot))
                byName.TryGetValue(entry.slot, out target);
            else if (i < books.Count)
                target = books[i];

            if (target == null)
            {
                Debug.LogWarning($"[BookDataImporter] 第 {i + 1} 条数据找不到目标书籍(slot={entry.slot ?? $"按顺序:{i}"}),已跳过。");
                continue;
            }

            if (!string.IsNullOrEmpty(entry.title)) target.title = entry.title;
            if (!string.IsNullOrEmpty(entry.author)) target.author = entry.author;
            if (!string.IsNullOrEmpty(entry.summary)) target.summary = entry.summary;
            if (!string.IsNullOrEmpty(entry.title) && target.name.StartsWith("Book_"))
                target.gameObject.name = entry.title; // 用书名命名便于管理
            EditorUtility.SetDirty(target);
            applied++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[BookDataImporter] 导入完成:共 {entries.Count} 条,成功应用 {applied} 条,场景已保存。");
    }
}

/// <summary>JsonUtility 不直接支持顶层数组,用包装类解决。</summary>
internal static class JsonUtilityArrayWrapper
{
    [Serializable]
    private class Wrapper<T>
    {
        public T[] items;
    }

    public static List<T> FromJson<T>(string json)
    {
        var wrapped = JsonUtility.FromJson<Wrapper<T>>("{\"items\":" + json + "}");
        return wrapped != null && wrapped.items != null ? wrapped.items.ToList() : new List<T>();
    }
}
