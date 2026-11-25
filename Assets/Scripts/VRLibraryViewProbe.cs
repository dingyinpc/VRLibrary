#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// 真机“黑屏”定位:从相机按视口坐标发射射线,用 Renderer.bounds 反查
/// 挡在画面里的物体(纯黑面片到底是哪个)。
/// 同时打印场景里所有 Canvas 的渲染模式/世界相机。
/// </summary>
public class VRLibraryViewProbe : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[ViewProbe]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryViewProbe>();
    }

    IEnumerator Start()
    {
        yield return new WaitForSecondsRealtime(6f);
        Dump();
    }

    static void Dump()
    {
        var cam = Camera.main;
        var sb = new StringBuilder();
        if (cam == null) { Debug.Log("[ViewProbe] 没有 Main Camera"); return; }

        sb.Append("[ViewProbe] cam=" + cam.name
                  + " pos=" + cam.transform.position.ToString("F2")
                  + " fwd=" + cam.transform.forward.ToString("F2")
                  + " fov=" + cam.fieldOfView
                  + " near=" + cam.nearClipPlane
                  + " clear=" + cam.clearFlags
                  + " mask=" + cam.cullingMask);

        var points = new[]
        {
            new Vector3(0.30f, 0.30f, 0),   // 黑块中心(左下)
            new Vector3(0.20f, 0.15f, 0),   // 黑块更靠左下
            new Vector3(0.50f, 0.50f, 0),   // 画面正中(对照)
            new Vector3(0.70f, 0.45f, 0),   // 右侧可见区(对照)
        };

        var all = Object.FindObjectsOfType<Renderer>();
        foreach (var vp in points)
        {
            var ray = cam.ViewportPointToRay(vp);
            var hits = new List<KeyValuePair<float, Renderer>>();
            foreach (var r in all)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.bounds.IntersectRay(ray, out float d) && d > 0f && d < 30f)
                    hits.Add(new KeyValuePair<float, Renderer>(d, r));
            }
            hits.Sort((a, b) => a.Key.CompareTo(b.Key));
            sb.Append("\n-- ray(" + vp.x.ToString("0.00") + "," + vp.y.ToString("0.00") + ") 命中 " + hits.Count + " --");
            for (int i = 0; i < Mathf.Min(4, hits.Count); i++)
                sb.Append("\n   " + hits[i].Key.ToString("F2") + "m " + PathOf(hits[i].Value.transform)
                          + " | " + MatOf(hits[i].Value));
        }

        sb.Append("\n-- Canvas --");
        foreach (var c in Object.FindObjectsOfType<Canvas>())
        {
            if (c == null) continue;
            sb.Append("\n   " + PathOf(c.transform)
                      + " mode=" + c.renderMode
                      + " worldCam=" + (c.worldCamera != null ? c.worldCamera.name : "null")
                      + " order=" + c.sortingOrder
                      + " scale=" + c.scaleFactor.ToString("0.00")
                      + " enabled=" + c.enabled);
        }

        Debug.Log(sb.ToString());
    }

    static string MatOf(Renderer r)
    {
        var m = r.sharedMaterial;
        if (m == null) return "mat=null";
        string col = m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "-";
        string bc = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString() : "-";
        return "mat=" + m.name + " shader=" + (m.shader != null ? m.shader.name : "null") + " color=" + col + " base=" + bc;
    }

    static string PathOf(Transform t)
    {
        var parts = new List<string>();
        while (t != null) { parts.Add(t.name); t = t.parent; }
        parts.Reverse();
        return string.Join("/", parts);
    }
}
#endif
