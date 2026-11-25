#if UNITY_EDITOR
using System;
using System.Collections.Generic;

/// <summary>
/// 固定台词识别的最小内核:MFCC + DTW 模板匹配。**纯 C#、不碰 Unity API** ——
/// 所以它既能跑在主线程,也能整段丢进工作线程(将来做常开时要用)。
///
/// 2026-11-06 演示裁剪:运行期的语音命令(`LibraryVoiceCommands`)已删除,
/// 现在只剩编辑器探针(`LibraryVoiceProbe` / `VRLibraryVoice`)在用它 ⇒ 整文件加 `#if UNITY_EDITOR`,
/// **不进包**,避免把一段死代码打进 APK。
///
/// 为什么是"模板匹配"而不是 ASR:
///   用户要的只是**十几句固定台词**(打开设置/回到大厅/下一部影片…)。闭集识别的搜索空间
///   只有那几句,不需要语言模型、不需要几十 MB 的模型文件、不需要任何 SDK 与授权,
///   而且**算力几乎白送**:MFCC 约 0.1 ms / 秒音频,DTW 每句约 0.5 ms。
///   真正的代价不在算力,在"麦克风常开"的固定功耗/热预算与音频会话争用 —— 那要真机量。
///
/// 算法(业界最朴素的一套,够用且可解释):
///   预加重 0.97 → 25 ms 汉明窗 / 10 ms 步 → 512 点 FFT → 功率谱 → 26 个三角梅尔滤波
///   → log → DCT-II 取 13 维 → **倒谱均值归一化(CMN)**(抵消音量/音色/麦克风差异)
///   识别:待识句与每句模板做 DTW(带 ±20% Sakoe-Chiba 带),取平均路径距离最小者为命中。
/// </summary>
public static class LibraryVoiceMatch
{
    public const int SampleRate = 16000;
    public const int FrameLen = 400;      // 25 ms
    public const int FrameHop = 160;      // 10 ms
    public const int FftSize = 512;
    public const int MelBands = 26;
    public const int Ceps = 13;

    // ──────────────────────────────────────────────────────────── MFCC

    static float[] hamming, melFb;        // 预计算(懒加载,只算一次)
    static float[][] dctMat;

    static void Prepare()
    {
        if (hamming != null) return;
        hamming = new float[FrameLen];
        for (int i = 0; i < FrameLen; i++)
            hamming[i] = 0.54f - 0.46f * (float)Math.Cos(2.0 * Math.PI * i / (FrameLen - 1));

        // 梅尔三角滤波:0 ~ Nyquist(8 kHz)
        melFb = new float[MelBands * (FftSize / 2 + 1)];
        double lo = 2595.0 * Math.Log10(1.0 + 0.0 / 700.0);
        double hi = 2595.0 * Math.Log10(1.0 + (SampleRate / 2.0) / 700.0);
        var pts = new double[MelBands + 2];
        for (int m = 0; m < pts.Length; m++)
        {
            double mel = lo + (hi - lo) * m / (MelBands + 1);
            pts[m] = 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0) * FftSize / SampleRate;
        }
        for (int m = 0; m < MelBands; m++)
            for (int k = 0; k <= FftSize / 2; k++)
            {
                double v = 0;
                if (k >= pts[m] && k <= pts[m + 1]) v = (k - pts[m]) / Math.Max(1e-9, pts[m + 1] - pts[m]);
                else if (k > pts[m + 1] && k <= pts[m + 2]) v = (pts[m + 2] - k) / Math.Max(1e-9, pts[m + 2] - pts[m + 1]);
                melFb[m * (FftSize / 2 + 1) + k] = (float)v;
            }

        dctMat = new float[Ceps][];
        for (int c = 0; c < Ceps; c++)
        {
            dctMat[c] = new float[MelBands];
            for (int m = 0; m < MelBands; m++)
                dctMat[c][m] = (float)Math.Cos(Math.PI * c * (m + 0.5) / MelBands);
        }
    }

    /// <summary>一段 16 kHz 单声道音频 → MFCC 序列(每行 Ceps 维,已做 CMN)。</summary>
    public static float[][] Mfcc(float[] audio, int count)
    {
        Prepare();
        int frames = 1 + (count - FrameLen) / FrameHop;
        if (frames <= 0) return new float[0][];

        var outp = new float[frames][];
        var re = new float[FftSize];
        var im = new float[FftSize];
        var pow = new float[FftSize / 2 + 1];
        var mel = new float[MelBands];

        for (int f = 0; f < frames; f++)
        {
            int off = f * FrameHop;
            for (int i = 0; i < FftSize; i++)
            {
                float s = 0f;
                if (i < FrameLen && off + i < count)
                {
                    s = audio[off + i] - (off + i > 0 ? 0.97f * audio[off + i - 1] : 0f);
                    s *= hamming[i];
                }
                re[i] = s; im[i] = 0f;
            }
            Fft(re, im);
            for (int k = 0; k <= FftSize / 2; k++) pow[k] = re[k] * re[k] + im[k] * im[k];
            for (int m = 0; m < MelBands; m++)
            {
                float acc = 0f;
                int baseIdx = m * (FftSize / 2 + 1);
                for (int k = 0; k <= FftSize / 2; k++) acc += melFb[baseIdx + k] * pow[k];
                mel[m] = (float)Math.Log(Math.Max(1e-10, acc));
            }
            var c = new float[Ceps];
            for (int ci = 0; ci < Ceps; ci++)
            {
                float acc = 0f;
                for (int m = 0; m < MelBands; m++) acc += dctMat[ci][m] * mel[m];
                c[ci] = acc;
            }
            outp[f] = c;
        }
        Cmn(outp);
        return outp;
    }

    /// <summary>倒谱均值归一化:每个维度减去自己的均值 —— 音量/麦克风差异基本被抹掉。</summary>
    static void Cmn(float[][] m)
    {
        if (m.Length == 0) return;
        var mean = new float[Ceps];
        for (int f = 0; f < m.Length; f++)
            for (int c = 0; c < Ceps; c++) mean[c] += m[f][c];
        for (int c = 0; c < Ceps; c++) mean[c] /= m.Length;
        for (int f = 0; f < m.Length; f++)
            for (int c = 0; c < Ceps; c++) m[f][c] -= mean[c];
    }

    static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { float t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1f, ci = 0f;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    float nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────── DTW

    /// <summary>两条 MFCC 序列的平均路径距离(越小越像);带 ±20% 的 Sakoe-Chiba 带。</summary>
    public static float Dtw(float[][] a, float[][] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0) return float.MaxValue;
        int band = Math.Max(8, (int)(0.2f * Math.Max(n, m)));
        var prev = new float[m + 1];
        var cur = new float[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = float.MaxValue;
        prev[0] = 0f;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 0; j <= m; j++) cur[j] = float.MaxValue;
            int j0 = Math.Max(1, i - band), j1 = Math.Min(m, i + band);
            for (int j = j0; j <= j1; j++)
            {
                float cost = Dist(a[i - 1], b[j - 1]);
                float best = Math.Min(prev[j], Math.Min(cur[j - 1], prev[j - 1]));
                if (best < float.MaxValue) cur[j] = cost + best;
            }
            var t = prev; prev = cur; cur = t;
        }
        return prev[m] / (n + m);      // 归一化成"每帧平均距离",句长不同也能比
    }

    static float Dist(float[] x, float[] y)
    {
        float s = 0f;
        for (int i = 0; i < Ceps; i++) { float d = x[i] - y[i]; s += d * d; }
        return (float)Math.Sqrt(s);
    }

    /// <summary>峰值电平(0~1),用来判"这句到底有没有说话"。</summary>
    public static float Peak(float[] audio, int count)
    {
        float p = 0f;
        for (int i = 0; i < count; i++) { float v = audio[i] < 0 ? -audio[i] : audio[i]; if (v > p) p = v; }
        return p;
    }
}
#endif