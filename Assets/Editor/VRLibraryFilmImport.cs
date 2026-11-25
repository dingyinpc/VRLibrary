#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 2026-11-07:把露台影院片单换成 4 份真实作品视频(第二版)。
///
/// 片单在 `Assets/Resources/filmography.json`,片源在 `Assets/StreamingAssets/`。
/// 本次片单:
///   比赛作品.mp4             → bisai_zuopin.mp4                 (H.264 1920×1080,132s)
///   星核防线2.mp4            → xinghe_fangxian2.mp4            (H.264 1526×854, 56s)
///   寻找.迷失的我.mp4         → xunzhao_mishi_de_wo.mp4          (H.264 1280×720, 60s)
///   寻找.迷失的我宣传片.mp4    → xunzhao_mishi_de_wo_trailer.mp4  (H.264 1280×720, 40s)
/// 注意:`星核防线.mp4`(第一部,HEVC)按用户要求**不入片单**,已从 StreamingAssets 删除;
/// 旧的示例占位片 film1.mp4 也已删除。
///
/// 本工具负责:把场景里 TerraceScreen.preferredFile 指到第一部真片(兜底用),
/// 并复核「片单条目 ↔ StreamingAssets 文件」一一对应、目录槽位数够用。
/// </summary>
public static class VRLibraryFilmImport
{
    const string ReportPath = "Logs/film-import.txt";

    [MenuItem("VRLibrary/体验·⑭ 放映片单接线(改场景)", false, 530)]
    public static void Apply()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 放映片单接线 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var films = TerraceScreen.Films;
        sb.AppendLine("片单条数 = " + films.Length);
        if (films.Length == 0) { sb.AppendLine("!! 片单为空,先检查 Assets/Resources/filmography.json"); Flush(sb); return; }

        var screens = Object.FindObjectsOfType<TerraceScreen>(true);
        sb.AppendLine("场上 TerraceScreen = " + screens.Length + " 个");
        foreach (var s in screens)
        {
            string before = s.preferredFile;
            s.preferredFile = films[0].fileName;
            s.videoVolume = 0.50f;                 // 需求:视频音轨 50%(2026-11-07 由 70% 下调)
            EditorUtility.SetDirty(s);
            sb.AppendLine("  " + s.gameObject.name + ": preferredFile " + before + " → " + s.preferredFile
                          + " , videoVolume → " + s.videoVolume);
        }

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        sb.AppendLine("场景已保存 = " + EditorSceneManager.SaveScene(scene));

        sb.AppendLine();
        sb.AppendLine(VerifyText());
        Flush(sb);
        Debug.Log(sb.ToString());
    }

    [MenuItem("VRLibrary/体验·⑭ 复核(只报告)", false, 531)]
    public static void Verify()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 放映片单 · 复核 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine(VerifyText());
        Flush(sb);
        Debug.Log(sb.ToString());
    }

    static string VerifyText()
    {
        var sb = new StringBuilder();
        string dir = Path.Combine(Application.dataPath, "StreamingAssets");

        var films = TerraceScreen.Films;
        sb.AppendLine("-- 片单 ↔ 片源 --");
        int ok = 0;
        foreach (var f in films)
        {
            string p = Path.Combine(dir, f.fileName);
            bool exists = File.Exists(p);
            if (exists) ok++;
            long mb = exists ? new FileInfo(p).Length / (1024 * 1024) : 0;
            sb.AppendLine("  " + (exists ? "✓" : "✗") + " 《" + f.title + "》 " + f.fileName
                          + "  " + mb + " MB  codec=" + (string.IsNullOrEmpty(f.codec) ? "h264(默认)" : f.codec)
                          + "  " + f.genre + " · " + f.minutes + " 分钟");
        }
        sb.AppendLine("  合计 " + ok + "/" + films.Length + " 条片源在位");

        sb.AppendLine();
        sb.AppendLine("-- StreamingAssets 里多余的 mp4(应为 0)--");
        int extra = 0;
        foreach (var p in Directory.GetFiles(dir, "*.mp4"))
        {
            string n = Path.GetFileName(p);
            bool used = false;
            foreach (var f in films) if (string.Equals(f.fileName, n, System.StringComparison.OrdinalIgnoreCase)) { used = true; break; }
            if (!used) { extra++; sb.AppendLine("  ✗ 未在片单中: " + n); }
        }
        if (extra == 0) sb.AppendLine("  (无)");

        sb.AppendLine();
        sb.AppendLine("-- 目录槽位 --");
        var cine = Object.FindObjectOfType<TerraceCinema>(true);
        if (cine == null) sb.AppendLine("  !! 场上没有 TerraceCinema");
        else
        {
            int slots = cine.filmSlots == null ? 0 : cine.filmSlots.Length;
            sb.AppendLine("  filmSlots 数组长度 = " + slots + "  片单条数 = " + films.Length
                          + "  → 第 " + films.Length + " 槽之后运行期自动隐藏");
            sb.AppendLine("  OnSelectFilm 绑定 = OnSelectFilm0..5(第 0~" + (films.Length - 1) + " 条可点)");
        }
        return sb.ToString().TrimEnd();
    }

    static void Flush(StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (System.Exception e) { Debug.LogWarning("[FilmImport] 报告落盘失败:" + e.Message); }
    }
}
#endif
