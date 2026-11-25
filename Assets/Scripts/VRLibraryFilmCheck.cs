#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 真机验片(仅诊断用,平时不参与运行)。
///
/// 触发:把任意文件推到
///   /sdcard/Android/data/com.VRLibrary.VRlibrary/files/film-check.txt
/// 再启动应用,就会把 filmography.json 里的每条片依次播放 5 秒,检查
///   prepared → 真的在播 → 片子时间在走 → RT 平均亮度不为 0(不是黑屏)
/// 结果写 logcat:
///   adb logcat -s Unity | findstr FilmCheck
/// 跑完自动退出。
/// </summary>
public class VRLibraryFilmCheck : MonoBehaviour
{
    static string ReqPath { get { return Path.Combine(Application.persistentDataPath, "film-check.txt"); } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try { if (!File.Exists(ReqPath)) return; } catch { return; }
        var go = new GameObject("[FilmCheck]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryFilmCheck>();
    }

    IEnumerator Start()
    {
        yield return new WaitForSeconds(6f);

        var screen = Object.FindObjectOfType<TerraceScreen>(true);
        if (screen == null)
        {
            Debug.Log("[FilmCheck] !! 场上没有 TerraceScreen");
            Finish();
            yield break;
        }

        var films = TerraceScreen.Films;
        var sb = new StringBuilder();
        sb.AppendLine("[FilmCheck] 片单 " + films.Length + " 条");
        int pass = 0;

        for (int i = 0; i < films.Length; i++)
        {
            var f = films[i];
            screen.PlayFile(f.fileName, null);

            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 10f && !screen.IsPlayingNow) yield return null;
            bool prepared = screen.IsPlayingNow;

            float t1 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t1 < 5f) yield return null;

            double clock = screen.Clock;
            float luma = screen.AverageLuma();
            bool ok = prepared && clock > 0.3 && luma > 0.015f;
            if (ok) pass++;

            sb.AppendLine("[FilmCheck] " + (ok ? "OK  " : "FAIL") + " #" + i + " 《" + f.title + "》"
                          + " file=" + f.fileName
                          + " prepared=" + prepared
                          + " clock=" + clock.ToString("0.00") + "s"
                          + " luma=" + luma.ToString("0.000")
                          + " fps=" + screen.MeasuredFps.ToString("0.0")
                          + " | 音轨=" + screen.VideoAudioTracks
                          + " 声音在响=" + screen.VideoAudioPlaying
                          + " 音量=" + screen.VideoAudioVolume.ToString("0.00")
                          + " | " + screen.DecodeLine);
        }

        sb.AppendLine("[FilmCheck] 结果 " + pass + "/" + films.Length);

        // 2026-11-07:离开影院 → 必须停片(视频+音轨),背景乐淡回 BGM
        var cine = Object.FindObjectOfType<TerraceCinema>(true);
        if (cine != null && films.Length > 0)
        {
            screen.PlayFile(films[0].fileName, null);
            float tw = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - tw < 10f && !screen.IsPlayingNow) yield return null;
            bool wasPlaying = screen.IsPlayingNow;

            cine.Leave();
            yield return new WaitForSecondsRealtime(3f);

            float bgm = LibraryBgm.Instance != null ? LibraryBgm.Instance.CurrentVolume : -1f;
            bool okLeave = wasPlaying && !screen.IsPlayingNow && bgm > 0.04f;
            sb.AppendLine("[FilmCheck] 离开影院测试: 离开前放映中=" + wasPlaying
                          + " 离开后还在放=" + screen.IsPlayingNow
                          + " 背景乐=" + bgm.ToString("0.00")
                          + " → " + (okLeave ? "OK" : "FAIL"));
        }

        Debug.Log(sb.ToString());
        Finish();
    }

    void Finish()
    {
        try { File.Delete(ReqPath); } catch { }
        Application.Quit();
    }
}
#endif
