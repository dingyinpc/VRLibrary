using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using Unity.XR.CoreUtils;

/// <summary>
/// 性能·② 定点实测:把玩家**钉在几个固定机位**,每个机位采 60 帧的
/// DrawCall / 批次 / SetPass / 三角面 / 帧率,写 Logs/perf-viewpoints.txt。
///
/// 为什么不用"帧率"当验收指标:编辑器里帧率被 vsync 和桌面显卡盖住了(实测 71 fps 稳定),
/// 而真机上真正卡住的是**每帧提交的批次数**。批次是确定性的:同一机位、同一场景,前后两次跑
/// 数字应当基本一致 —— 这正是"优化到底有没有用"的验收尺子。
///
/// 由菜单 VRLibrary/性能·② 定点实测(进Play自动跑) 写请求文件触发;没有请求文件就自己退场。
/// </summary>
public class VRLibraryPerfSweep : MonoBehaviour
{
    const string kReq = "Logs/perf-sweep-request.txt";
    const string kRep = "Logs/perf-viewpoints.txt";
    const int Warm = 45;     // 换机位后先丢弃这么多帧:区域剔除是 0.5 s 一拍,预热必须比它长,不然量到的是上一拍的画面
    const int Sample = 60;   // 每个机位采样的帧数
    const int Rounds = 2;    // 整张表跑几轮(取两轮的小值做 A/B,抵消场景里会动的物件带来的偏差)

    // 场景里现成的锚点(全是传送目标,位置不会漂)
    static readonly string[] Pads =
    {
        "Pad_Entry", "Pad_Shelves", "Pad_Reading", "Pad_Service",
        "Pad_2F_N_E", "Pad_2F_Stair", "CinemaSeat",
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (!File.Exists(Abs(kReq))) return;
        var go = new GameObject("[VRLibraryPerfSweep]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryPerfSweep>();
    }

    IEnumerator Start()
    {
        for (int i = 0; i < 20; i++) yield return null;

        var sb = new StringBuilder();
        sb.AppendLine("== 性能·② 定点实测 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + "  机位 " + (Pads.Length + 1) + " 个  每机位 预热 " + Warm + " 帧 / 采样 " + Sample + " 帧");
        sb.AppendLine("画质档 " + QualitySettings.names[QualitySettings.GetQualityLevel()]
            + "  renderScale=" + RenderScale() + "  MSAA=" + QualitySettings.antiAliasing);

        var origin = FindObjectOfType<XROrigin>();
        var cam = Camera.main;
        if (origin == null) { sb.AppendLine("!! 找不到 XROrigin,量不了"); Finish(sb); yield break; }

        // XR Device Simulator 每帧抢着改相机,还会自己画一层 UI → 采样期间关掉(它不属于场景内容)
        var sim = GameObject.Find("XR Device Simulator");
        if (sim != null) { sim.SetActive(false); sb.AppendLine("已临时关掉 XR Device Simulator(避免它抢相机/自带 UI 干扰计数)"); }
        else sb.AppendLine("场上没有 XR Device Simulator");

        var markers = new Markers();
        sb.AppendLine();
        sb.AppendLine("机位                                     DrawCall   批次  SetPass     三角面   有效帧/秒 相机距锚点  文字开  渲染器开  实际可见   灯开  视频  有效帧");
        // 跑两轮:同一机位两轮的数字差多少,就是这次测量的噪声底 —— 场景里有会转/会开合的物件,
        // 一轮的数字会被它们的状态带偏(实测同一机位出现过 1782 与 1594 两个值),
        // **做 A/B 对比时取两轮里小的那个**(状态最"干净"的一次),比平均值可信。
        string shots = ShotTag();     // 环境变量 DSH_VIEW_TAG 能给这一轮截图打标签(before/after),空则不打
        for (int round = 1; round <= Rounds; round++)
        {
            sb.AppendLine("\n---- 第 " + round + " 轮 ----");
            foreach (var name in Pads)
            {
                var t = FindByName(name);
                if (t == null) { sb.AppendLine(name.PadRight(38) + "  !! 场景里找不到这个锚点"); continue; }
                yield return SampleAt(name, origin, cam, t.position, t.rotation, markers, sb,
                    round == 1 ? shots : null);
            }
            var ea = FindByName("Pad_Entry"); var eb = FindByName("Pad_Service");
            if (ea != null && eb != null)
            {
                var dir = eb.position - ea.position; dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    yield return SampleAt("Pad_Entry·朝服务台对角线(最坏)",
                        origin, cam, ea.position, Quaternion.LookRotation(dir.normalized), markers, sb,
                        round == 1 ? shots : null);
            }
        }

        Finish(sb);
    }

    struct Markers
    {
        public ProfilerRecorder draw, batch, setPass, tris;
        public void Init()
        {
            draw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            batch = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        }
        public void Dispose() { draw.Dispose(); batch.Dispose(); setPass.Dispose(); tris.Dispose(); }
    }

    IEnumerator SampleAt(string label, XROrigin origin, Camera cam, Vector3 pos, Quaternion rot,
                         Markers m, StringBuilder sb, string shotSuffix = null)
    {
        origin.transform.SetPositionAndRotation(pos, rot);
        Physics.SyncTransforms();

        for (int i = 0; i < Warm; i++) yield return null;

        // 顺手把这一机位的**Game 视图**存下来:数字能说明省了多少,但"看着还行不行"只能看图。
        // 两轮跑完同一个名字会被覆盖(第一轮写完第二轮再写),所以加轮次后缀。
        if (shotSuffix != null)
        {
            string dir = Abs("Captures/views");
            try { Directory.CreateDirectory(dir); } catch { }
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, Key(label) + shotSuffix + ".png"));
        }

        m.Init();
        float t0 = Time.realtimeSinceStartup;
        // 存整串样本再取**中位数**:编辑器里 Scene 视图偶尔也在画,它的 draw call 会混进
        // Profiler 的 "Draw Calls Count",把平均值抬起来(实测同一机位两轮差 38%,而内容计数一模一样)。
        // 中位数对"少数帧被别的东西污染"是稳的,平均值不是。
        var ld = new List<long>(); var lb = new List<long>(); var ls = new List<long>(); var lt = new List<long>();
        for (int i = 0; i < Sample; i++)
        {
            yield return null;
            // 只收"真的画了这一帧"的样本:编辑器窗口失焦时 Game 视图不重绘,那些帧的计数是 0,
            // 混进来会把中位数直接压成 0(实测 8 个机位里有 3~4 个整段是 0)。
            if (m.draw.Valid && m.draw.LastValue > 0)
            {
                ld.Add(m.draw.LastValue);
                if (m.batch.Valid) lb.Add(m.batch.LastValue);
                if (m.setPass.Valid) ls.Add(m.setPass.LastValue);
                if (m.tris.Valid) lt.Add(m.tris.LastValue);
            }
        }
        float span = Mathf.Max(0.0001f, Time.realtimeSinceStartup - t0);
        float dist = cam != null ? Vector3.Distance(cam.transform.position, pos) : -1f;
        double d = Med(ld), b = Med(lb), s = Med(ls), tr = Med(lt);
        int n = Mathf.Max(1, ld.Count);
        // 再加两个"内容侧"计数:计数只跟场景状态有关,能把"真变了"和"跑起来抖"分开
        int textOn = 0, rendOn = 0, rendVisible = 0;
        foreach (var t in Object.FindObjectsOfType<TextMesh>(true))
        {
            var r = t.GetComponent<Renderer>();
            if (r != null && r.enabled) textOn++;
        }
        foreach (var r in Object.FindObjectsOfType<Renderer>(true))
        {
            if (r.enabled) rendOn++;
            if (r.isVisible) rendVisible++;
        }
        // 灯与视频:这两个是**非此即彼**的状态,一次跑里 1782、下一次 1594 就是它们在翻
        int lit = 0, vid = 0;
        foreach (var l in Object.FindObjectsOfType<Light>(true))
            if (l.type != LightType.Directional && l.enabled) lit++;
        foreach (var v in Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>(true))
            if (v.isPlaying) vid++;
        sb.AppendLine(Pad(label, 38)
            + d.ToString("0").PadLeft(9)
            + b.ToString("0").PadLeft(7)
            + s.ToString("0").PadLeft(9)
            + tr.ToString("0").PadLeft(11)
            + (n / span).ToString("0.0").PadLeft(7)
            + (dist < 0 ? "  ?" : ("  " + dist.ToString("0.00") + " m"))
            + textOn.ToString().PadLeft(7) + rendOn.ToString().PadLeft(8) + rendVisible.ToString().PadLeft(10)
            + lit.ToString().PadLeft(6) + vid.ToString().PadLeft(6)
            + ld.Count.ToString().PadLeft(7));      // 有效帧数:太小说明这段数不可信
        m.Dispose();
    }

    /// <summary>中位数(样本为空返回 0)。</summary>
    static double Med(List<long> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v[v.Count / 2];
    }

    // ------------------------------------------------------------ 小工具

    static Transform FindByName(string name)
    {
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    static string Pad(string s, int w)
    {
        int width = 0;
        foreach (var ch in s) width += ch > 127 ? 2 : 1;      // 中文按两格算,列才对得齐
        return width >= w ? s : s + new string(' ', w - width);
    }

    static string RenderScale()
    {
        var p = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as
            UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
        if (p == null) return "?";
        var pi = p.GetType().GetProperty("renderScale");
        return pi == null ? "?" : pi.GetValue(p, null).ToString();
    }

    static string Abs(string rel)
    {
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), rel.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>截图标签:读 Logs/view-tag.txt(一行 before/after)。不用环境变量 —— 编辑器是别人启动的,拿不到。</summary>
    static string ShotTag()
    {
        try
        {
            string f = Abs("Logs/view-tag.txt");
            if (!File.Exists(f)) return null;
            string t = File.ReadAllText(f).Trim();
            return string.IsNullOrEmpty(t) ? null : "-" + t;
        }
        catch { return null; }
    }

    static string Key(string label)
    {
        var sb = new StringBuilder();
        foreach (var ch in label)
        {
            if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
            else if (ch == '·' || ch == '-' || ch == '_') sb.Append('-');
        }
        return sb.ToString();
    }

    void Finish(StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Abs(kRep)));
            File.WriteAllText(Abs(kRep), sb.ToString(), new UTF8Encoding(false));
            File.Delete(Abs(kReq));
        }
        catch (System.Exception e) { Debug.LogWarning("[PerfSweep] 落盘失败:" + e.Message); }
        Debug.Log(sb.ToString());
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}