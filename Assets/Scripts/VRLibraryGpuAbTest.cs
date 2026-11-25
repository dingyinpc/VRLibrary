#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// 真机 GPU 归因 A/B(仅诊断用)。
///
/// 触发:把任意文件推到
///   /sdcard/Android/data/com.VRLibrary.VRlibrary/files/gpu-ab.txt
/// 再启动应用,就会依次测:
///   baseline / 关 UI 画布 / 关 TextMesh / 关透明材质 / 关天空盒 / renderScale 0.5
/// 每档 3 秒预热 + 5 秒计时,结果写 logcat:
///   adb logcat -s Unity | findstr GpuAB
/// 跑完自动退出应用。没有触发文件时完全不参与运行。
/// </summary>
public class VRLibraryGpuAbTest : MonoBehaviour
{
    static string ReqPath { get { return Path.Combine(Application.persistentDataPath, "gpu-ab.txt"); } }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        try { if (!File.Exists(ReqPath)) return; } catch { return; }
        var go = new GameObject("[GpuAbTest]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryGpuAbTest>();
    }

    IEnumerator Start()
    {
        yield return new WaitForSeconds(3f);

        var sb = new StringBuilder();
        sb.AppendLine("[GpuAB] ==== 开始 ====");
        sb.AppendLine("[GpuAB] 眼缓冲 " + UnityEngine.XR.XRSettings.eyeTextureWidth + "x" + UnityEngine.XR.XRSettings.eyeTextureHeight);

        var canvases = Object.FindObjectsOfType<Canvas>(true);
        var textMeshes = Object.FindObjectsOfType<TextMesh>(true);
        var transparent = new List<Renderer>();
        foreach (var r in Object.FindObjectsOfType<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            if (mats == null) continue;
            foreach (var m in mats)
                if (m != null && m.renderQueue >= 3000) { transparent.Add(r); break; }
        }

        Camera cam = Camera.main;
        var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        yield return Measure("baseline", sb);

        SetCanvasEnabled(canvases, false);
        yield return Measure("UI画布全关(" + canvases.Length + ")", sb);
        SetCanvasEnabled(canvases, true);

        SetRendererEnabled(textMeshes, false);
        yield return Measure("TextMesh全关(" + textMeshes.Length + ")", sb);
        SetRendererEnabled(textMeshes, true);

        SetRenderersEnabled(transparent, false);
        yield return Measure("透明材质全关(" + transparent.Count + ")", sb);
        SetRenderersEnabled(transparent, true);

        CameraClearFlags oldClear = cam != null ? cam.clearFlags : CameraClearFlags.Skybox;
        if (cam != null) cam.clearFlags = CameraClearFlags.SolidColor;
        yield return Measure("天空盒关", sb);
        if (cam != null) cam.clearFlags = oldClear;

        if (urp != null)
        {
            float old = urp.renderScale;
            urp.renderScale = 0.5f;
            yield return Measure("renderScale 0.5", sb);
            urp.renderScale = old;
        }

        sb.AppendLine("[GpuAB] ==== 结束 ====");
        Debug.Log(sb.ToString());

        try { File.Delete(ReqPath); } catch { }
        Application.Quit();
    }

    IEnumerator Measure(string name, StringBuilder sb)
    {
        yield return new WaitForSecondsRealtime(2.0f);
        int frames = 0;
        float t0 = Time.realtimeSinceStartup;
        float minFps = 9999f;
        while (Time.realtimeSinceStartup - t0 < 5f)
        {
            yield return null;
            frames++;
            float dt = Time.unscaledDeltaTime;
            if (dt > 0.0001f)
            {
                float f = 1f / dt;
                if (f < minFps) minFps = f;
            }
        }
        float span = Mathf.Max(0.0001f, Time.realtimeSinceStartup - t0);
        sb.AppendLine("[GpuAB] " + name.PadRight(26) + " fps=" + (frames / span).ToString("0.0")
                      + "  min=" + (minFps > 9000 ? 0 : minFps).ToString("0.0"));
        Debug.Log("[GpuAB] " + name + " fps=" + (frames / span).ToString("0.0"));
    }

    static void SetCanvasEnabled(Canvas[] list, bool on)
    {
        foreach (var c in list) if (c != null) c.enabled = on;
    }

    static void SetRendererEnabled(TextMesh[] list, bool on)
    {
        foreach (var t in list)
        {
            if (t == null) continue;
            var r = t.GetComponent<Renderer>();
            if (r != null) r.enabled = on;
        }
    }

    static void SetRenderersEnabled(List<Renderer> list, bool on)
    {
        foreach (var r in list) if (r != null) r.enabled = on;
    }
}
#endif
