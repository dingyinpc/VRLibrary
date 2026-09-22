using UnityEngine;
using UnityEngine.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// 智能图书馆 · 跟随头显的 HUD:右上角小地图(楼层 + 指北罗盘)、左上角菜单(侧边栏三处传送)、
/// 侧边栏最下方的设置(音量)与关机(退出应用)。
/// 所有 UI 由菜单 VRLibrary/装小地图与菜单 在编辑期建好,运行时只切显隐与数值(坑 1)。
/// 挂法:uGUI 屏幕空间 Overlay ⇒ 画布就是整块眼缓冲,左上菜单键与右上小地图真的贴在屏幕角上。
/// 侧边栏按锚点占"屏幕宽的 1/3 × 上下满",收合距离运行时取面板自己的宽度(不在搭建期写死像素)。
/// </summary>
public class LibraryHud : MonoBehaviour
{
    public static LibraryHud Instance;

    [Header("接线(搭建菜单填)")]
    public XROrigin origin;
    public CanvasGroup hudGroup;
    public CanvasGroup menuGroup;
    public RectTransform menuPanel;
    public GameObject backdrop;
    public GameObject pageHome;
    public GameObject pageSettings;
    public Text floorLabel;
    public RectTransform dial;
    public GameObject quitConfirm;
    public Slider volumeSlider;
    public Text volumeLabel;
    public KioskTeleporter[] teleporters = new KioskTeleporter[3];

    public const string VolumeKey = "vrlib.masterVolume";
    public const float DefaultVolume = 0.5f;
    /// <summary>建筑轮廓半边(外墙 ±19.8 m)。rig 在这圈之外就算在露台上 —— 二层板面与露台板面同为 3.0 m,y 分不开这两层。</summary>
    public const float WallEdge = 19.8f;

    const float SlideSeconds = 0.18f;
    const float QuitArmSeconds = 4f;

    float openT;
    bool open;
    float slideDist, lastW;
    float quitArmedAt = -99f;
    TerraceCinema cinema;
    Camera cam;
    AudioSource bgmSource;

    public bool IsMenuOpen { get { return open; } }
    public string FloorText { get { return floorLabel == null ? "" : floorLabel.text; } }
    public bool BgmPlaying { get { return bgmSource != null && bgmSource.isPlaying; } }

    void Awake()
    {
        Instance = this;
        cinema = FindObjectOfType<TerraceCinema>();
        // Overlay 画布不在相机层级下,不能再用 GetComponentInParent<Camera>
        cam = origin != null ? origin.Camera : null;
        if (cam == null) cam = Camera.main;
    }

    void OnDestroy()
    { if (Instance == this) Instance = null; }

    void Start()
    {
        float v = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumeKey, DefaultVolume));
        AudioListener.volume = v;
        if (volumeSlider != null) volumeSlider.SetValueWithoutNotify(v);
        if (volumeLabel != null) volumeLabel.text = "总音量 " + Mathf.RoundToInt(v * 100f) + "%";
        StartBgm();
    }

    /// <summary>BGM 是个空槽:片源由用户后补。文件名按 Resources/bgm 找,找不到就留静音并说明。</summary>
    void StartBgm()
    {
        bgmSource = GetComponentInChildren<AudioSource>();
        if (bgmSource == null) return;
        AudioClip clip = null;
        foreach (var n in new[] { "bgm", "Bgm", "BGM" })
        {
            clip = Resources.Load<AudioClip>(n);
            if (clip != null) break;
        }
        if (clip == null)
        {
            Debug.Log("[LibraryHud] 没找到 Resources/bgm 音频资产 → BGM 留空槽静音(文件放进 Assets/Resources/bgm.mp3|ogg|wav 即自动接上)");
            return;
        }
        bgmSource.clip = clip;
        bgmSource.loop = true;
        bgmSource.Play();
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;

        // 观影态下这块会糊在片子上,所以整块淡掉
        bool seated = cinema != null && cinema.IsSeated;
        float targetA = seated ? 0f : 1f;
        if (hudGroup != null)
        {
            hudGroup.alpha = Mathf.MoveTowards(hudGroup.alpha, targetA, dt * 2.5f);
            hudGroup.interactable = hudGroup.blocksRaycasts = hudGroup.alpha > 0.5f;
        }
        if (seated && open) CloseMenu();

        openT = Mathf.MoveTowards(openT, open ? 1f : 0f, dt / SlideSeconds);
        if (menuGroup != null)
        {
            menuGroup.alpha = openT;
            menuGroup.blocksRaycasts = openT > 0.5f;
        }
        if (menuPanel != null)
        {
            // 收合距离 = 面板自己的宽度:Overlay 下它是"屏幕宽的 1/3"锚出来的,
            // 搭建期写死像素会在别的分辨率上露出一条边或留一段空档。
            if (slideDist <= 0f || Mathf.Abs(slideDist - lastW) > 0.01f)
            {
                lastW = menuPanel.rect.width;
                slideDist = lastW;
            }
            var pos = menuPanel.anchoredPosition;
            pos.x = Mathf.Lerp(-slideDist, 0f, EaseOut(openT));
            menuPanel.anchoredPosition = pos;
        }

        if (quitConfirm != null && quitConfirm.activeSelf && Time.unscaledTime - quitArmedAt > QuitArmSeconds)
            quitConfirm.SetActive(false);
    }

    void LateUpdate()
    {
        Vector3 p = origin != null ? origin.transform.position : Vector3.zero;
        if (floorLabel != null) floorLabel.text = "当前位置 · " + FloorNameAt(p);
        if (cam != null && dial != null)
        {
            Vector3 f = cam.transform.forward; f.y = 0f;
            if (f.sqrMagnitude > 1e-4f)
                dial.localRotation = Quaternion.Euler(0f, 0f, -Vector3.SignedAngle(Vector3.back, f, Vector3.up));
        }
    }

    static float EaseOut(float t) { return 1f - (1f - t) * (1f - t); }

    /// <summary>本工程"北 = -Z"(北侧服务台 z=-12.5、北走道 z=-18.2、露台在东北 x/z 同号外扩)。</summary>
    public static string FloorNameAt(Vector3 p)
    {
        if (p.y < 2f) return "图书馆 1 楼";
        if (Mathf.Abs(p.x) > WallEdge || Mathf.Abs(p.z) > WallEdge) return "星空影台";
        return "图书馆 2 楼";
    }

    // ── 菜单开合 ────────────────────────────────────────────────────
    public void ToggleMenu() { if (open) CloseMenu(); else OpenMenu(); }

    public void OpenMenu()
    {
        open = true;
        if (pageHome != null) pageHome.SetActive(true);
        if (pageSettings != null) pageSettings.SetActive(false);
        if (backdrop != null) backdrop.SetActive(true);
    }

    /// <summary>侧边栏里点"点空白处"也走这里:backdrop 那颗按钮的 onClick 就绑这个。</summary>
    public void CloseMenu()
    {
        open = false;
        if (backdrop != null) backdrop.SetActive(false);
        if (quitConfirm != null) quitConfirm.SetActive(false);
    }

    public void OpenSettings()
    {
        open = true;
        if (pageHome != null) pageHome.SetActive(false);
        if (pageSettings != null) pageSettings.SetActive(true);
        if (backdrop != null) backdrop.SetActive(true);
    }

    // ── 传送 ────────────────────────────────────────────────────────
    public void TeleportFloor1() { TeleportByIndex(0); }
    public void TeleportFloor2() { TeleportByIndex(1); }
    public void TeleportTerrace() { TeleportByIndex(2); }

    void TeleportByIndex(int i)
    {
        if (teleporters == null || i < 0 || i >= teleporters.Length || teleporters[i] == null)
        {
            Debug.LogWarning("[LibraryHud] 第 " + i + " 颗传送按钮没接 KioskTeleporter");
            return;
        }
        if (teleporters[i].destination == null)
        {
            Debug.LogWarning("[LibraryHud] 第 " + i + " 颗传送按钮没接落点");
            return;
        }
        teleporters[i].TeleportToDestination();
        CloseMenu();
        Debug.Log("[LibraryHud] 传送到 " + teleporters[i].destination.name
                  + " → 楼层判读:" + FloorNameAt(origin.transform.position));
    }

    // ── 音量 ────────────────────────────────────────────────────────
    public void SetVolume(float v)
    {
        v = Mathf.Clamp01(v);
        AudioListener.volume = v;
        PlayerPrefs.SetFloat(VolumeKey, v);
        if (volumeLabel != null) volumeLabel.text = "总音量 " + Mathf.RoundToInt(v * 100f) + "%";
    }

    // ── 退出应用 ────────────────────────────────────────────────────
    /// <summary>两下才走:第一下只把"再按一次确认退出"那行显出来,4 秒内不再按就作废。</summary>
    public void RequestQuit()
    {
        if (Time.unscaledTime - quitArmedAt > QuitArmSeconds)
        {
            quitArmedAt = Time.unscaledTime;
            if (quitConfirm != null) quitConfirm.SetActive(true);
            Debug.Log("[LibraryHud] 退出待确认:再按一次关机图标才退出应用");
            return;
        }
        if (quitConfirm != null) quitConfirm.SetActive(false);
        Debug.Log("[LibraryHud] 退出图书馆");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
