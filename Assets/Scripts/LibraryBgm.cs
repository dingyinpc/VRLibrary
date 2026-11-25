using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// 图书馆背景音乐(2026-11-07 加)。
///
/// 需求:
///   · 常态音量 **20%**,循环播放;
///   · **视频播放时**:缓慢淡到 0 后暂停;
///   · **视频停止时**:缓慢回到 20%;
///   · 视频自己的音轨默认 **70%**(在 TerraceScreen.videoVolume 里设)。
///
/// 实现:运行时自建常驻物体,不需要往场景里挂组件,也不依赖被禁用的 LibraryHud。
/// 片源固定读 `Assets/Resources/bgm.*`(现在是 bgm.mp3 = Golden_Hour_Pages)。
/// 是否"正在放视频"直接读 `TerraceScreen.IsPlayingNow`,所以暂停/停止/换片都会自动跟随。
/// </summary>
public class LibraryBgm : MonoBehaviour
{
    public static LibraryBgm Instance;

    /// <summary>常态音量(需求:约 20%)。2026-11-07 实测偏大,降到 0.08(约 -8 dB)。</summary>
    public const float NormalVolume = 0.08f;
    /// <summary>淡入/淡出时长(秒)。</summary>
    public const float FadeSeconds = 1.5f;

    AudioSource src;
    TerraceScreen screen;
    bool screenSearched;
    bool ducked;
    float normalVolume = NormalVolume;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    float nextVolCheck;
    System.DateTime lastVolWrite = System.DateTime.MinValue;
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (Instance != null) return;
        var go = new GameObject("[LibraryBgm]");
        DontDestroyOnLoad(go);
        go.AddComponent<LibraryBgm>();
    }

    void Awake()
    {
        Instance = this;

        src = gameObject.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = true;
        src.spatialBlend = 0f;          // 2D,不受位置影响
        src.volume = normalVolume;

        // 现场调音量:把 `0.05` 这种纯数字推到
        //   /sdcard/Android/data/com.VRLibrary.VRlibrary/files/bgm-volume.txt
        // 1 秒内自动生效(热更新,不用重启、不用重新打包)。
        normalVolume = ReadVolumeOverride(normalVolume);
        src.volume = normalVolume;

        var clip = Resources.Load<AudioClip>("bgm");
        if (clip == null)
        {
            Debug.LogWarning("[Bgm] 没找到 Resources/bgm(支持的扩展名 mp3/wav/ogg),背景音乐留空");
            return;
        }
        src.clip = clip;
        src.Play();
        Debug.Log("[Bgm] 背景音乐开始 《" + clip.name + "》 音量 " + normalVolume
                  + " 时长 " + clip.length.ToString("0.0") + "s 循环=" + src.loop);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (src == null || src.clip == null) return;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (Time.unscaledTime >= nextVolCheck)
        {
            nextVolCheck = Time.unscaledTime + 1f;
            normalVolume = ReadVolumeOverride(normalVolume);
        }
#endif

        if (screen == null && !screenSearched)
        {
            screenSearched = true;
            screen = FindObjectOfType<TerraceScreen>(true);
        }
        bool videoPlaying = screen != null && screen.IsPlayingNow;

        if (videoPlaying != ducked)
        {
            ducked = videoPlaying;
            Debug.Log("[Bgm] " + (videoPlaying
                ? "视频开播 → 背景乐淡出到 0 并暂停"
                : "视频停止 → 背景乐淡入回 " + normalVolume));
        }

        float step = normalVolume / FadeSeconds * Time.unscaledDeltaTime;

        if (videoPlaying)
        {
            if (src.isPlaying)
            {
                src.volume = Mathf.MoveTowards(src.volume, 0f, step);
                if (src.volume <= 0.0005f)
                {
                    src.volume = 0f;
                    src.Pause();
                    Debug.Log("[Bgm] 已淡到 0,背景乐暂停");
                }
            }
        }
        else
        {
            if (!src.isPlaying) src.UnPause();
            src.volume = Mathf.MoveTowards(src.volume, normalVolume, step);
        }
    }

    /// <summary>读 `files/bgm-volume.txt` 里的单个数覆盖音量;文件没变就不重复读,没有就用 fallback。</summary>
    float ReadVolumeOverride(float fallback)
    {
        try
        {
            string p = Path.Combine(Application.persistentDataPath, "bgm-volume.txt");
            if (!File.Exists(p)) return fallback;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var w = File.GetLastWriteTimeUtc(p);
            if (w == lastVolWrite) return normalVolume;   // 没改过,沿用当前值
            lastVolWrite = w;
#endif

            string s = File.ReadAllText(p).Trim();
            float v;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                v = Mathf.Clamp01(v);
                Debug.Log("[Bgm] 音量更新为 " + v + "(来自 bgm-volume.txt,热更新)");
                return v;
            }
            Debug.LogWarning("[Bgm] bgm-volume.txt 内容不是数字(" + s + "),用当前值 " + fallback);
        }
        catch { }
        return fallback;
    }

    /// <summary>调试/其它脚本用:当前真实音量。</summary>
    public float CurrentVolume { get { return src == null ? 0f : src.volume; } }
    public bool IsPaused { get { return src != null && !src.isPlaying; } }
}
