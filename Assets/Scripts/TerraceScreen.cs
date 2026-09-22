using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// 星空露台大屏的播放层:片单数据 + 影片信息卡 + VideoPlayer。
///
/// 片单来自 `Assets/Resources/filmography.json`(用 Resources 是因为 Android 上它能同步读,
/// 不必为 StreamingAssets 走 UnityWebRequest)。影片文件仍放 `Assets/StreamingAssets/`:
/// Android 上 VideoPlayer 只有从 StreamingAssets 取 url 才免转码、免权限。
///
/// 贴图写在 **renderer.material 的实例**上,不碰 .mat 资产:运行时改共享资产会永久留在磁盘上、
/// 退出 Play 不回滚(交接提示词 遗留 14)。信息卡上的 UI 全部由 `VRLibrary/搭建星空露台`
/// 编辑期预建,运行时只改文字与显隐(坑 1)。
/// </summary>
public class TerraceScreen : MonoBehaviour
{
    [System.Serializable]
    public class Film
    {
        public string title;
        public string fileName;
        public int minutes;
        public string genre;
        public string summary;

        /// <summary>片源自带编码(h264 / hevc / vp9 / av1)。留空按 h264 算。</summary>
        public string codec;
    }

    [System.Serializable]
    class Filmography { public Film[] films; }

    static Film[] films;

    [Tooltip("屏面渲染器(VideoSurface 那块承载画面的 Quad)")]
    public Renderer screen;

    [Header("预建的影片信息卡(编辑期摆好,运行时只填字)")]
    public GameObject infoCard;
    public Text cardTitle;
    public Text cardMeta;
    public Text cardSummary;
    public Text cardHint;

    [Tooltip("片单为空时的兜底首选文件名;否则取 StreamingAssets 里第一个 mp4")]
    public string preferredFile = "terrace.mp4";

    [Tooltip("RT 长边上限:按视频真实分辨率建,超这个宽度就等比缩,省一体机显存")]
    public int maxTextureWidth = 1920;

    [Header("解码路线")]
    [Tooltip("设备没有该编码的硬解时,允许用真机自带的平台软解(费电、高分辨率易掉帧)。"
        + "关掉之后没硬解就直接在屏上提示换源")]
    public bool allowSoftwareDecode = true;

    [Tooltip("预建的信息卡解码行")]
    public Text cardDecoder;

    VideoPlayer player;
    RenderTexture rt;
    string pendingFile;
    Action pendingStart;
    float faceW, faceH;              // 屏框可占的最大面积(建屏时 VideoSurface 的 localScale)
    DecoderPlan plan = new DecoderPlan { route = DecodeRoute.Unknown };
    string decodeBrief = "";           // 屏上一行放得下的短版
    double sourceFps;
    float measuredFps;
    float lagSeconds;
    float windowAt;                  // 上一次采样时刻;0 = 窗口未起
    int windowFrame;
    double windowClock;
    float windowReadyAt;             // 起播后先冷启动一段,别把首帧就绪算成掉帧

    /// <summary>片单(露台控制器与冒烟测试都从这里读)。</summary>
    public static Film[] Films
    {
        get
        {
            if (films == null) LoadFilms();
            return films ?? new Film[0];
        }
    }

    static void LoadFilms()
    {
        films = new Film[0];
        var ta = Resources.Load<TextAsset>("filmography");
        if (ta == null) { Debug.LogWarning("[TerraceScreen] 没有 Assets/Resources/filmography.json"); return; }
        try
        {
            var fg = JsonUtility.FromJson<Filmography>(ta.text);
            if (fg != null && fg.films != null) films = fg.films;
            Debug.Log("[TerraceScreen] 片单载入 " + films.Length + " 条");
        }
        catch (Exception e) { Debug.LogError("[TerraceScreen] filmography.json 解析失败:" + e.Message); }
    }

    void Awake()
    {
        LoadFilms();
        if (screen != null)
        {
            var s = screen.transform.localScale;
            faceW = s.x; faceH = s.y;
        }
    }

    void Start()
    {
        // 没人选片时:有片单就先把第一条的信息卡挂上(等人在露台里选),
        // 否则退回"随便找个 mp4 直接播"的老行为
        var f = Films;
        if (f.Length > 0) ShowInfoCard(f[0]);
        else
        {
            var loose = FindLooseVideo();
            if (!string.IsNullOrEmpty(loose)) PlayFile(loose, null);
        }
    }

    void OnDisable() { if (player != null && player.isPlaying) player.Pause(); }

    void OnDestroy()
    {
        if (player != null)
        {
            player.prepareCompleted -= OnPrepared;
            player.Stop();
            player.url = string.Empty;
        }
        if (rt != null) { rt.Release(); Destroy(rt); rt = null; }
    }

    /// <summary>
    /// 冒烟测试用:**时钟在走**才算过。
    /// 别用 `frameCount` —— 它是"这条片总共有多少帧"(60 s/30 fps 一上来就 1800),证明不了任何进度。
    /// </summary>
    public bool Decoding { get { return player != null && player.isPlaying && player.time > 0.05; } }
    public float FaceWidth { get { return screen != null ? screen.transform.localScale.x : 0f; } }
    public float FaceHeight { get { return screen != null ? screen.transform.localScale.y : 0f; } }
    /// <summary>屏框原尺寸(建屏时那块面的大小);FitFace 只缩画面,不动这个。</summary>
    public float PanelWidth { get { return faceW; } }
    public float PanelHeight { get { return faceH; } }
    /// <summary>当前那块 RT,给编辑器侧取证用(导 PNG、查关键字)。</summary>
    public RenderTexture Target { get { return rt; } }
    public long FrameIndex { get { return player == null ? -1L : (long)player.frame; } }

    /// <summary>
    /// RT 的平均亮度(0~1),取不到返回 -1。**"出帧"不等于"屏上有画面"**:
    /// targetTexture 绑定时机写错时 frameCount 照样涨、屏上全黑,只有读回像素能戳穿。
    /// </summary>
    public float AverageLuma()
    {
        if (rt == null) return -1f;
        var probe = RenderTexture.GetTemporary(32, 18, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(rt, probe);
        var tex = new Texture2D(32, 18, TextureFormat.RGBA32, false);
        var prev = RenderTexture.active;
        RenderTexture.active = probe;
        tex.ReadPixels(new Rect(0, 0, 32, 18), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(probe);

        var px = tex.GetPixels32();
        Destroy(tex);
        float sum = 0f;
        for (int i = 0; i < px.Length; i++)
            sum += 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
        return sum / px.Length / 255f;
    }

    // ------------------------------------------------------------ 信息卡

    public void ShowInfoCard(Film f)
    {
        if (f == null) return;
        pendingFile = f.fileName;
        Probe(f.codec);
        if (cardTitle != null) cardTitle.text = f.title;
        if (cardMeta != null) cardMeta.text = f.genre + " · " + f.minutes + " 分钟";
        if (cardSummary != null) cardSummary.text = f.summary;
        if (cardHint != null) cardHint.text = "即将开始 · 右手按住 Grip 看目录,左手按住 Grip 看播控";
        if (infoCard != null) infoCard.SetActive(true);
        Debug.Log("[TerraceScreen] 信息卡:《" + f.title + "》" + f.minutes + " 分钟 文件 " + f.fileName
            + " → " + DecodeLine);
    }

    /// <summary>信息卡停顿到点,由 TerraceCinema 调:开始真正播放。</summary>
    public void StartPlayback()
    {
        if (string.IsNullOrEmpty(pendingFile)) { Debug.LogWarning("[TerraceScreen] 没选片,忽略开播"); return; }
        PlayFile(pendingFile, () => { if (infoCard != null) infoCard.SetActive(false); });
    }

    public void SetPaused(bool on)
    {
        if (player == null) return;
        if (on) player.Pause(); else player.Play();
    }

    public void SetPlaybackSpeed(float s)
    {
        if (player != null) player.playbackSpeed = Mathf.Clamp(s, 0.25f, 4f);
    }

    /// <summary>前后跳若干秒(负数往回)。没准备好或没片子就什么都不做。</summary>
    public void SeekBy(float seconds)
    {
        if (player == null || !player.isPrepared) return;
        double len = player.length;
        double t = player.time + seconds;
        if (len > 0.0) t = System.Math.Clamp(t, 0.0, len);
        player.time = t;
        ResetWindow();          // 播放时钟被人为跳了一段,拿它量解码吞吐会算出假掉帧
        Debug.Log("[TerraceScreen] 跳转 " + (seconds > 0 ? "+" : "") + seconds + " s → " + player.time.ToString("0.0") + " s");
    }

    /// <summary>关闭影片:停在待机卡上,不留最后一帧在屏上,也不算退出观影。</summary>
    public void StopFilm()
    {
        if (player != null) { player.Stop(); player.time = 0f; }
        ShowStandby();
        Debug.Log("[TerraceScreen] 已关闭影片,屏退回待机卡");
    }

    void ShowStandby()
    {
        pendingFile = null;
        if (cardTitle != null) cardTitle.text = "(未选片)";
        if (cardMeta != null) cardMeta.text = "— · — 分钟";
        if (cardSummary != null) cardSummary.text = "右手按住 Grip 打开目录,或在目录里点「观影须知」回看规则。";
        if (cardHint != null) cardHint.text = "等待选片";
        if (cardDecoder != null) cardDecoder.text = decodeBrief;
        if (infoCard != null) infoCard.SetActive(true);
    }

    public bool IsPlayingNow { get { return player != null && player.isPrepared && player.isPlaying; } }
    public double Clock { get { return player == null ? -1.0 : player.time; } }
    public double ClipLength { get { return player == null ? -1.0 : player.length; } }

    // ------------------------------------------------------------ 播放

    string FindLooseVideo()
    {
        string dir = Application.streamingAssetsPath;
        try
        {
            if (Directory.Exists(dir))
            {
                var all = Directory.GetFiles(dir, "*.mp4");
                foreach (var f in all)
                    if (Path.GetFileName(f).ToLower() == preferredFile.ToLower()) return f;
                if (all.Length > 0) return all[0];
            }
        }
        catch { /* Android 上目录不可枚举是正常的,交给 url 分支 */ }
        return null;
    }

    /// <summary>按 StreamingAssets 里的文件名播放;找不到就保留信息卡并把提示改成缺片警告。</summary>
    public void PlayFile(string fileNameOrPath, Action onStarted)
    {
        if (screen == null) { Debug.LogWarning("[TerraceScreen] 没接屏面渲染器"); return; }
        string url = ResolveUrl(fileNameOrPath);
        if (string.IsNullOrEmpty(url))
        {
            if (cardHint != null)
                cardHint.text = "片源未就位:请把 " + fileNameOrPath + " 放进 Assets/StreamingAssets/ 后重进";
            if (infoCard != null) infoCard.SetActive(true);
            Debug.LogWarning("[TerraceScreen] 找不到片源 " + fileNameOrPath + " → 继续显示信息卡");
            return;
        }

        var p = Probe(CodecOf(fileNameOrPath));
        if (!p.Playable)
        {
            if (cardHint != null)
                cardHint.text = "本机放不了这条片:" + p.reason
                    + "\n改源办法:转成 H.264 再放进 StreamingAssets";
            if (infoCard != null) infoCard.SetActive(true);
            Debug.LogWarning("[TerraceScreen] " + DecodeLine + " → 不去 Prepare,免得屏上全黑 " + url);
            return;
        }
        ResetWindow();

        var m = screen.material;                       // 实例,不改资产
        m.EnableKeyword("_EMISSION");
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

        // RT 必须在 Prepare 之前建好绑上:放到 prepareCompleted 里再赋 targetTexture,
        // 解码器不会往这块 RT 写,屏上全黑,而 frameCount 照样涨 —— 只测"出帧"会假绿。
        if (rt == null)
        {
            rt = new RenderTexture(maxTextureWidth, Mathf.Max(2, maxTextureWidth * 9 / 16), 0,
                                   RenderTextureFormat.ARGB32) { name = "TerraceScreenRT" };
            rt.Create();
        }

        player = GetComponent<VideoPlayer>();
        if (player == null) player = gameObject.AddComponent<VideoPlayer>();
        player.playOnAwake = false;
        player.waitForFirstFrame = true;
        player.isLooping = true;
        player.audioOutputMode = VideoAudioOutputMode.AudioSource;
        if (GetComponent<AudioSource>() == null)
        {
            var a = gameObject.AddComponent<AudioSource>();
            a.playOnAwake = false;
            a.rolloffMode = AudioRolloffMode.Linear;
            a.maxDistance = 26f;
        }
        player.renderMode = VideoRenderMode.RenderTexture;
        player.targetTexture = rt;
        player.url = url;
        player.playbackSpeed = 1f;
        m.SetTexture("_BaseMap", rt);
        m.SetTexture("_EmissionMap", rt);
        m.SetColor("_EmissionColor", Color.white);     // 视频自带亮度,不再加倍率

        // 每次播放都只留一个监听器:上一版在这里 += 了 onStarted 的 lambda,
        // 选三部片就有三个残留回调,后面每次 prepare 都会被前面那几部片的回调踩一遍
        player.prepareCompleted -= OnPrepared;
        player.prepareCompleted += OnPrepared;
        pendingStart = onStarted;
        player.Prepare();
        Debug.Log("[TerraceScreen] 播放 " + url + "\n  " + DecodeLine
            + "\n  本机:" + VideoDecodeProbe.PlatformDescription);
    }

    void OnPrepared(VideoPlayer vp)
    {
        sourceFps = vp.frameRate;
        windowReadyAt = Time.unscaledTime + 1.0f;     // 首帧就绪/缓冲那一下不算掉帧
        ResetWindow();
        FitFace(vp.width, vp.height);
        vp.Play();
        if (pendingStart != null) { var c = pendingStart; pendingStart = null; c(); }
        Debug.Log(string.Format("[TerraceScreen] 起播 {0}×{1} @ {2:0.0} fps,{3}",
            vp.width, vp.height, vp.frameRate, DecodeLine));
    }

    /// <summary>
    /// 把画面缩成"落在屏框里的最大等比矩形",屏面 ScreenFace 当黑边。
    /// RT 会被拉伸铺满 quad,所以**不变形的条件是 quad 的宽高比 = 片子的宽高比**,
    /// 与 RT 自己的分辨率无关 —— 因此不必按片子重建 RT,更不必二次 Prepare。
    /// </summary>
    void FitFace(uint vw, uint vh)
    {
        if (vw == 0 || vh == 0 || faceW <= 0f || faceH <= 0f) return;
        float a = (float)vw / vh, fa = faceW / faceH;
        float fw = a >= fa ? faceW : faceH * a;
        float fh = a >= fa ? faceW / a : faceH;
        screen.transform.localScale = new Vector3(fw, fh, 1f);
        Debug.Log(string.Format("[TerraceScreen] 片子 {0}×{1}(比例 {2:0.00}) → 屏框 {3:0.00}×{4:0.00} m 里占 {5:0.00}×{6:0.00} m",
            vw, vh, a, faceW, faceH, fw, fh));
    }

    /// <summary>裸文件名 → StreamingAssets 下的 url(编辑器要求文件真存在,真机走 jar url)。</summary>
    string ResolveUrl(string nameOrPath)
    {
        if (string.IsNullOrEmpty(nameOrPath)) return null;
        if (nameOrPath.Contains("://") || Path.IsPathRooted(nameOrPath)) return nameOrPath;
        string full = Path.Combine(Application.streamingAssetsPath, nameOrPath).Replace('\\', '/');
#if UNITY_EDITOR || UNITY_STANDALONE
        if (!File.Exists(full)) return null;
#endif
        return full;
    }

    // ------------------------------------------------------------ 解码路线

    /// <summary>本次播片选定的解码路线(屏上那行字与冒烟测试都读它)。</summary>
    public DecoderPlan Plan { get { return plan; } }
    public string DecodeLine { get; private set; } = "";
    /// <summary>片子自己的帧率,VideoPlayer 从容器里读出来的。</summary>
    public double SourceFps { get { return sourceFps; } }
    /// <summary>实测解码吞吐;0 表示还没量出来,别拿它当"没掉帧"。</summary>
    public float MeasuredFps { get { return measuredFps; } }
    /// <summary>最近一个采样窗口里落下的秒数(墙上时间 − 播放时钟)。</summary>
    public float LagSeconds { get { return lagSeconds; } }
    /// <summary>还没量出来时算跟得上 —— 宁可漏报也不给冒烟测试一个假失败。</summary>
    public bool KeepingUp
    {
        get { return measuredFps <= 0f || (lagSeconds < 0.35f && measuredFps >= sourceFps * 0.85f); }
    }

    /// <summary>问本机拿什么解这个编码,结论同步到屏上的解码行。播片前调,一次性的能力查询。</summary>
    DecoderPlan Probe(string codec)
    {
        var p = VideoDecodeProbe.PlanFor(codec, allowSoftwareDecode);
        plan = p;
        DecodeLine = VideoDecodeProbe.Report(p);
        decodeBrief = VideoDecodeProbe.Brief(p);
        if (cardDecoder != null && cardDecoder.text != decodeBrief) cardDecoder.text = decodeBrief;
        return p;
    }

    /// <summary>裸文件名 → 片单里登记的编码;不在片单里的散片按 H.264 问(返回 null 即此意)。</summary>
    string CodecOf(string fileNameOrPath)
    {
        if (string.IsNullOrEmpty(fileNameOrPath)) return null;
        string name = Path.GetFileName(fileNameOrPath);
        var f = Films;
        for (int i = 0; i < f.Length; i++)
            if (string.Equals(f[i].fileName, name, StringComparison.OrdinalIgnoreCase)) return f[i].codec;
        return null;
    }

    void ResetWindow()
    {
        windowAt = 0f;
        measuredFps = 0f;
        lagSeconds = 0f;
        lagging = false;
    }

    bool lagging;

    /// <summary>
    /// 解码吞吐看门狗:每个 1 s 窗口把"墙上走了多久"和"片子走了多久"对一下。
    /// 只量一次不顶用 —— 路线对不对是本机属性,跟不跟得上是负载属性,会在跑动中变。
    /// </summary>
    void Update()
    {
        if (player == null || !player.isPlaying || sourceFps <= 0.0) return;
        if (Time.unscaledTime < windowReadyAt) { windowAt = 0f; return; }

        if (windowAt <= 0f)
        {
            windowAt = Time.unscaledTime;
            windowFrame = (int)player.frame;
            windowClock = player.time;
            return;
        }

        float dt = Time.unscaledTime - windowAt;
        if (dt < 1f) return;
        double adv = player.time - windowClock;
        if (adv < 0.0 || adv > dt + 1.0) { ResetWindow(); return; }    // 有人跳了片,窗口作废

        measuredFps = ((int)player.frame - windowFrame) / dt;
        lagSeconds = dt - (float)adv;
        windowAt = Time.unscaledTime;
        windowFrame = (int)player.frame;
        windowClock = player.time;

        bool nowLagging = lagSeconds >= 0.35f || measuredFps < sourceFps * 0.85f;
        if (nowLagging && !lagging)
        {
            lagging = true;
            Debug.LogWarning(string.Format(
                "[TerraceScreen] 解码跟不上:1 s 里片子只走了 {0:0.00} s,实测 {1:0.0} fps / 源 {2:0.0} fps → {3}"
                + "\n  换低分辨率/低码率片源;这条路线已经是本机最好的解码器了",
                adv, measuredFps, sourceFps, DecodeLine));
        }
        else if (!nowLagging && lagging)
        {
            lagging = false;
            Debug.Log(string.Format("[TerraceScreen] 解码恢复跟得上:实测 {0:0.0} fps / 源 {1:0.0} fps",
                measuredFps, sourceFps));
        }
        if (cardDecoder != null)
        {
            string line = VideoDecodeProbe.ShortReport(plan, measuredFps, sourceFps);
            if (cardDecoder.text != line) cardDecoder.text = line;
        }
    }
}
