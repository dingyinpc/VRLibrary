#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// 固定台词识别探针(**仅编辑器存在,不进包**)。
///
/// 目的:把"在 PICO 上到底行不行"变成数字,而不是感觉。一次 Play 跑完五件事:
///   ① 麦克风授权与设备列表        —— 真机上最常见的第一道坎
///   ② 逐句**登记**(默认 3 遍/句) —— 模板 = 中位(medoid,与其它重复平均距离最小的那条)
///   ③ **区分度预检**:句与句的 5×5 距离矩阵 —— 易混对在**做功能之前**就暴露出来
///      (判据:跨句距离 < 1.3 × 两边类内距离 ⇒ 这两句太像)
///   ④ **自测**:每句再说 N 遍 → 命中 / 错判 / 拒识 + 混淆矩阵
///   ⑤ **性能**:MFCC 毫秒/秒音频、单次匹配毫秒 → 直接推算"常开要占单核多少"
///
/// 用户 2026-09-30 定案:不用常开,用**按住手柄键说话**(避免与场景音乐争音频会话);
/// 台词 5 句:打开菜单 / 关闭菜单 / 去一楼 / 去二楼 / 去影院。
/// 为什么台词要短:`传送图书馆一楼`/`二楼` 共享 6 个字前缀,DTW 按整句平均距离打分 ⇒ 极易混;
/// 改成 2~3 字且韵母差异大(i / ar / ing)才稳。
///
/// 录制窗口不要求卡点:一次录 3 s,再自动取**能量最高**的一段 1.8 s ⇒ 蜂鸣后随口说就行。
/// </summary>
public class LibraryVoiceProbe : MonoBehaviour
{
    const string kRequest = "Logs/voice-request.txt";
    const string kReport = "Logs/voice-test.txt";

    [Header("可选:场景里已有的世界空间 Text(运行时不新建 UI)")]
    public Text promptText;

    public string[] phrases = { "打开菜单", "关闭菜单", "去一楼", "去二楼", "去影院" };
    public int enrollReps = 3;             // 每句登记几遍
    public int testReps = 3;               // 每句自测几遍
    public float recordSeconds = 3.0f;     // 每次录多久
    public float windowSeconds = 1.8f;     // 从录音里取多长一段
    public float silencePeak = 0.015f;     // 峰值低于此 = 没说话

    static StringBuilder rep;
    AudioSource pump;

    List<float[][]>[] enrollList;
    float[][][] medoid;                    // [句] → 模板(帧 × 13 维倒谱)
    float[] intra, thresh;
    double mfccMs, dtwMs, audioSec;
    int matched, wrong, rejected, silentAll;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        string req = Abs(kRequest);
        if (!File.Exists(req)) return;
        File.Delete(req);                          // 一次性
        rep = new StringBuilder();
        var go = new GameObject("__VoiceProbe__");
        DontDestroyOnLoad(go);
        // 编辑器失焦会停游戏循环(冒烟测试踩过):语音会话全靠等秒,停了就全乱
        Application.runInBackground = true;
        var p = go.AddComponent<LibraryVoiceProbe>();
        p.StartCoroutine(p.Run());
    }

    IEnumerator Run()
    {
        Line("固定台词识别探针  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Line("台词(" + phrases.Length + " 句):" + string.Join(" / ", phrases));
        Line("每句登记 " + enrollReps + " 遍、自测 " + testReps + " 遍;录音 " + recordSeconds
             + " s,自动取能量最高的一段 " + windowSeconds + " s");
        Line("");
        Flush();

        yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
        Line("麦克风授权:" + Application.HasUserAuthorization(UserAuthorization.Microphone));
        Line("麦克风设备数:" + Microphone.devices.Length
             + (Microphone.devices.Length > 0 ? "   首个=" + Microphone.devices[0] : "   !! 一个都没有"));
        Flush();
        if (Microphone.devices.Length == 0)
        {
            Finish("没有可用麦克风 —— 真机上多半是没声明 RECORD_AUDIO,或被用户在系统里拒了");
            yield break;
        }

        // ── ② 登记
        Line("");
        Line("── 登记(蜂鸣后随口说那一句就行,不用卡点)──");
        enrollList = new List<float[][]>[phrases.Length];
        for (int k = 0; k < phrases.Length; k++)
        {
            enrollList[k] = new List<float[][]>();
            int tries = 0;
            while (enrollList[k].Count < enrollReps && tries < enrollReps + 3)
            {
                tries++;
                Prompt("请说:" + phrases[k] + "  (" + (enrollList[k].Count + 1) + "/" + enrollReps + ")");
                Buzz();
                yield return new WaitForSeconds(0.8f);
                float[] d = null;
                yield return Record(x => d = x);
                float[] win = Cut(d);
                float pk = LibraryVoiceMatch.Peak(win, win.Length);
                if (pk < silencePeak)
                {
                    silentAll++;
                    Line("    !! " + phrases[k] + " 第 " + tries + " 次:峰值 " + pk.ToString("0.000") + " 太低,当成没说话");
                    continue;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var m = LibraryVoiceMatch.Mfcc(win, win.Length);
                sw.Stop();
                mfccMs += sw.Elapsed.TotalMilliseconds;
                audioSec += win.Length / (double)LibraryVoiceMatch.SampleRate;
                enrollList[k].Add(m);
                Line("    " + phrases[k] + " 第 " + enrollList[k].Count + " 遍:峰值 " + pk.ToString("0.000")
                     + " 帧数 " + m.Length);
            }
            if (enrollList[k].Count == 0) { Finish("第 " + (k + 1) + " 句一遍都没录到(麦克风没数据?)"); yield break; }
        }

        // ── 模板 = 中位(medoid);阈值 = 类内最大距离 × 1.6
        medoid = new float[phrases.Length][][];
        intra = new float[phrases.Length];
        thresh = new float[phrases.Length];
        for (int k = 0; k < phrases.Length; k++)
        {
            var list = enrollList[k];
            int bestI = 0; float bestSum = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                float s = 0f;
                for (int j = 0; j < list.Count; j++) if (i != j) s += LibraryVoiceMatch.Dtw(list[i], list[j]);
                if (s < bestSum) { bestSum = s; bestI = i; }
            }
            medoid[k] = list[bestI];
            float mx = 0f;
            for (int j = 0; j < list.Count; j++) if (j != bestI) mx = Mathf.Max(mx, LibraryVoiceMatch.Dtw(medoid[k], list[j]));
            intra[k] = mx;
            thresh[k] = Mathf.Max(0.35f, mx * 1.6f);
        }

        // ── ③ 区分度预检
        Line("");
        Line("── 区分度预检:句与句的平均距离(越小越像;易混对 = 跨句 < 1.3 × 两边类内)──");
        var conf = new List<string>();
        for (int a = 0; a < phrases.Length; a++)
        {
            var row = new StringBuilder("    " + phrases[a].PadRight(6));
            for (int b = 0; b < phrases.Length; b++)
            {
                float d = a == b ? intra[a] : LibraryVoiceMatch.Dtw(medoid[a], medoid[b]);
                row.Append(d.ToString("0.000").PadLeft(9));
                if (a < b && d < 1.3f * Mathf.Max(intra[a], intra[b]))
                    conf.Add(phrases[a] + " ↔ " + phrases[b] + " (跨句 " + d.ToString("0.000")
                             + " vs 类内 " + intra[a].ToString("0.000") + "/" + intra[b].ToString("0.000") + ")");
            }
            Line(row.ToString());
        }
        Line("    易混对:" + (conf.Count == 0 ? "无 ✓" : string.Join(" | ", conf.ToArray())));
        Flush();

        // ── ④ 自测
        Line("");
        Line("── 自测(每句 " + testReps + " 遍;拒识 = 分数不过阈值或与第二名太近)──");
        var cm = new int[phrases.Length, phrases.Length];
        for (int k = 0; k < phrases.Length; k++)
        {
            for (int r = 0; r < testReps; r++)
            {
                Prompt("请说:" + phrases[k] + "  (测 " + (r + 1) + "/" + testReps + ")");
                Buzz();
                yield return new WaitForSeconds(0.8f);
                float[] d = null;
                yield return Record(x => d = x);
                float[] win = Cut(d);
                float pk = LibraryVoiceMatch.Peak(win, win.Length);
                if (pk < silencePeak) { Line("    !! " + phrases[k] + ":没说话(峰值 " + pk.ToString("0.000") + ")"); continue; }
                var m = LibraryVoiceMatch.Mfcc(win, win.Length);
                float bd, sd; int bi = Match(m, out bd, out sd);
                bool decide = bd <= thresh[bi] && bd < sd * 0.92f;
                string verdict = !decide ? "拒识" : (bi == k ? "命中" : "错判");
                if (!decide) rejected++; else if (bi == k) matched++; else wrong++;
                if (decide) cm[k, bi]++;
                Line("    " + phrases[k].PadRight(6) + " → " + phrases[bi].PadRight(6)
                     + " 距离 " + bd.ToString("0.000") + "(阈值 " + thresh[bi].ToString("0.000") + ")"
                     + " 次名 " + sd.ToString("0.000") + "  → " + verdict);
            }
        }

        // ── ⑤ 性能
        Line("");
        Line("── 性能(真机数字由你打包后看这一节)──");
        double perSec = audioSec > 0 ? mfccMs / audioSec : 0;
        double matchOnce = 0;
        if (medoid != null && medoid[0] != null)
        {
            var probe = LibraryVoiceMatch.Mfcc(new float[LibraryVoiceMatch.SampleRate * 2], LibraryVoiceMatch.SampleRate * 2);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float dummy1, dummy2; Match(probe, out dummy1, out dummy2);
            sw.Stop();
            matchOnce = sw.Elapsed.TotalMilliseconds;
        }
        Line("    MFCC:" + mfccMs.ToString("0.0") + " ms / " + audioSec.ToString("0.0") + " s 音频 = "
             + perSec.ToString("0.00") + " ms 每秒音频");
        Line("    一次匹配(" + phrases.Length + " 句模板):" + matchOnce.ToString("0.0") + " ms(含全部 DTW)");
        Line("    常开推算:每秒 MFCC " + perSec.ToString("0.00") + " ms + 每 0.5 s 匹配一次 "
             + (matchOnce * 2).ToString("0.0") + " ms ⇒ 约 "
             + (perSec + matchOnce * 2).ToString("0.0") + " ms/秒 = 单核 "
             + ((perSec + matchOnce * 2) / 10.0).ToString("0.00") + "%");
        Line("    (按需模式:只说 2 s/次 ⇒ 单次成本 " + (perSec * 2 + matchOnce).ToString("0.0") + " ms,且只在松手后算一次)");
        Line("");
        Line("── 汇总 ──");
        Line("    命中 " + matched + " / 错判 " + wrong + " / 拒识 " + rejected + " / 没说话 " + silentAll);
        var mtx = new StringBuilder("    混淆矩阵(行=说的,列=系统判定):\n         ");
        for (int b = 0; b < phrases.Length; b++) mtx.Append(phrases[b].PadLeft(8));
        Line(mtx.ToString());
        for (int a = 0; a < phrases.Length; a++)
        {
            var row = new StringBuilder("    " + phrases[a].PadRight(6) + " ");
            for (int b = 0; b < phrases.Length; b++) row.Append(cm[a, b].ToString().PadLeft(8));
            Line(row.ToString());
        }
        Finish(null);
    }

    // ──────────────────────────────────────────────────────────── 采集

    IEnumerator Record(Action<float[]> onDone)
    {
        string dev = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
        int len = Mathf.CeilToInt(recordSeconds) + 1;
        var clip = Microphone.Start(dev, false, len, LibraryVoiceMatch.SampleRate);
        // ⚠ 实测坑:安卓上**只 Start 不挂 AudioSource.Play,有些设备根本不往 clip 里填数据**
        if (pump == null) { pump = gameObject.AddComponent<AudioSource>(); pump.volume = 0f; pump.loop = false; }
        pump.clip = clip; pump.Play();
        yield return new WaitForSeconds(recordSeconds);
        int n = Mathf.Min(clip.samples, Mathf.RoundToInt(recordSeconds * LibraryVoiceMatch.SampleRate));
        var data = new float[n];
        clip.GetData(data, 0);
        Microphone.End(dev);
        pump.Stop();
        onDone(data);
    }

    /// <summary>从整段录音里取"能量最高"的一段(不要求卡点说话)。</summary>
    float[] Cut(float[] d)
    {
        int want = Mathf.RoundToInt(windowSeconds * LibraryVoiceMatch.SampleRate);
        if (d == null || d.Length <= want) return d ?? new float[0];
        int step = LibraryVoiceMatch.SampleRate / 50;            // 20 ms 一格
        int bestI = 0; float bestE = -1f;
        for (int i = 0; i + step <= d.Length; i += step)
        {
            float e = 0f;
            for (int j = i; j < i + step; j++) e += d[j] * d[j];
            if (e > bestE) { bestE = e; bestI = i; }
        }
        int start = Mathf.Clamp(bestI - Mathf.RoundToInt(0.3f * LibraryVoiceMatch.SampleRate), 0, d.Length - want);
        var w = new float[want];
        Array.Copy(d, start, w, 0, want);
        return w;
    }

    int Match(float[][] x, out float bestD, out float secondD)
    {
        bestD = float.MaxValue; secondD = float.MaxValue; int best = -1;
        for (int k = 0; k < medoid.Length; k++)
        {
            if (medoid[k] == null) continue;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float dd = LibraryVoiceMatch.Dtw(x, medoid[k]);
            sw.Stop(); dtwMs += sw.Elapsed.TotalMilliseconds;
            if (dd < bestD) { secondD = bestD; bestD = dd; best = k; }
            else if (dd < secondD) secondD = dd;
        }
        return best < 0 ? 0 : best;
    }

    // ──────────────────────────────────────────────────────────── 提示 / 报告

    void Prompt(string s) { if (promptText != null) promptText.text = s; Line("  " + s); Flush(); }

    /// <summary>没有 UI 时的"该你说了"信号:手柄短震。真机戴着头显时这条最有用。</summary>
    void Buzz()
    {
        try
        {
            var d = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (d.isValid) d.SendHapticImpulse(0u, 0.7f, 0.15f);
        }
        catch { /* 编辑器里没有手柄设备是正常的 */ }
    }

    void Finish(string fatal)
    {
        if (fatal != null) { Line(""); Line("!! " + fatal); }
        Line("");
        Line("报告就到这里。下一步:打包到 PICO 上再跑一遍同一套(真机数字才算数)。");
        Flush();
        Debug.Log("[VoiceProbe] → " + kReport + (fatal != null ? "  致命:" + fatal : ""));
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    static void Line(string x) { if (rep != null) rep.AppendLine(x); }
    static void Flush()
    {
        if (rep == null) return;
        File.WriteAllText(Abs(kReport), rep.ToString(), Encoding.UTF8);
    }
    static string Abs(string rel)
    {
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), rel.Replace('/', Path.DirectorySeparatorChar));
    }
}
#endif