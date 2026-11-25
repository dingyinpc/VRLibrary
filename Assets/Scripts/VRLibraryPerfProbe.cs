#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;
using UnityEngine.XR;
using Unity.Profiling;

/// <summary>
/// 帧率体检探针(真机可读)。**不改场景**:RuntimeInitializeOnLoadMethod 自己起一个常驻物体。
///
/// 为什么必须有它:一体机上的"糊"有两个完全相反的成因 ——
///   ① 帧率不够 → PICO 的合成器把画面按上一帧重投影(reprojection),转头就有拖影/发虚;
///   ② 分辨率不够 → 眼缓冲本身不足,锐化也救不回来。
/// 这两者只能靠 <b>CPU/GPU 帧时间</b> 分开:GPU 时间贴着刷新周期(72Hz=13.9ms)就是①,让像素变少;
/// GPU 时间宽松但画面还糊就是②,让眼缓冲变大。
/// 日志行同时在编辑器 Play 与真机 logcat 里出现,真机命令:
///   adb logcat -s Unity | findstr PerfProbe
/// </summary>
public class VRLibraryPerfProbe : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        var go = new GameObject("[VRLibraryPerfProbe]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryPerfProbe>();
    }

    ProfilerRecorder drawCalls, setPass, batches, tris, mainMs;
    readonly FrameTiming[] timings = new FrameTiming[16];

    float winStart, sumFps, minFps = 9999f;
    int frames, lastCpuMs, lastGpuMs, lastPresent;
    long peakDraw, peakTris;

    void OnEnable()
    {
        drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        mainMs = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 30);
        winStart = Time.realtimeSinceStartup;
    }

    void OnDisable()
    {
        drawCalls.Dispose(); setPass.Dispose(); batches.Dispose(); tris.Dispose(); mainMs.Dispose();
    }

    void Update()
    {
        FrameTimingManager.CaptureFrameTimings();
        frames++;
        float fps = Time.unscaledDeltaTime > 0.00001f ? 1f / Time.unscaledDeltaTime : 0f;
        sumFps += fps; if (fps < minFps) minFps = fps;
        if (drawCalls.Valid && drawCalls.LastValue > peakDraw) peakDraw = drawCalls.LastValue;
        if (tris.Valid && tris.LastValue > peakTris) peakTris = tris.LastValue;

        uint n = FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
        if (n > 0)
        {
            double c = 0, g = 0; int cn = 0, gn = 0;
            for (int i = 0; i < n; i++)
            {
                if (timings[i].cpuFrameTime > 0.01) { c += timings[i].cpuFrameTime; cn++; }
                if (timings[i].gpuFrameTime > 0.01) { g += timings[i].gpuFrameTime; gn++; }
                lastPresent = (int)timings[i].cpuMainThreadPresentWaitTime;
            }
            if (cn > 0) lastCpuMs = (int)c / cn;
            if (gn > 0) lastGpuMs = (int)g / gn;
        }

        float span = Time.realtimeSinceStartup - winStart;
        if (span >= 8f)
        {
            float avg = frames / span;
            float budget = 1000f / Mathf.Max(60f, RefreshHz());
            Debug.Log("[PerfProbe] 实测帧率 avg=" + avg.ToString("0.0") + "/s 最低=" + minFps.ToString("0.0")
                + "/s | CPU主线程 " + lastCpuMs + "ms  GPU " + lastGpuMs + "ms  预算 " + budget.ToString("0.0") + "ms"
                + (lastGpuMs >= budget ? "  ⚠ GPU 顶到预算 → 该减像素(renderScale/MSAA/FFR)" : "")
                + (lastCpuMs >= budget ? "  ⚠ CPU 顶到预算 → 该减 DrawCall/脚本" : "")
                + " | DrawCall " + (drawCalls.Valid ? drawCalls.LastValue.ToString() : "?")
                + "(峰值 " + peakDraw + ")  SetPass " + (setPass.Valid ? setPass.LastValue.ToString() : "?")
                + "  批次 " + (batches.Valid ? batches.LastValue.ToString() : "?")
                + "  三角面 " + (tris.Valid ? tris.LastValue.ToString() : "?")
                + " | 眼缓冲 " + XRSettings.eyeTextureWidth + "x" + XRSettings.eyeTextureHeight
                + " viewportScale=" + XRSettings.renderViewportScale.ToString("0.00")
                + " 刷新率=" + RefreshHz() + " 档=" + QualitySettings.names[QualitySettings.GetQualityLevel()]);
            winStart = Time.realtimeSinceStartup; frames = 0; sumFps = 0; minFps = 9999f;
            peakDraw = 0; peakTris = 0;
        }
    }

    // 不用 XRDevice.refreshRate(2022 里已废弃,再去碰它是自找编译警告):真机上
    // Screen.currentResolution.refreshRate 报的就是面板刷新率(72/90),拿不到才退回 72。
    static float RefreshHz()
    {
        int r = Screen.currentResolution.refreshRate;
        return r > 1 ? r : 72f;
    }
}
#endif