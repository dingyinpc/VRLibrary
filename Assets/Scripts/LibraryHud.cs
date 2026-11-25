using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.XR;
using Unity.XR.CoreUtils;

/// <summary>
/// 智能图书馆 · HUD:菜单(侧边栏三处传送 + 设置/关机)与**右前下方**的楼层指北小地图。
/// 所有 UI 由菜单 VRLibrary/装小地图与菜单 在编辑期建好,运行时只切显隐与数值(坑 1)。
/// 挂法:v7.28 起是**头锁世界空间画布**(挂在 Main Camera 下、眼前 1.20 m、横 ±25° / 竖 ±14.7°)。
/// 上一版是 uGUI Overlay:在编辑器里看着"贴角",到 PICO 真机上因为眼缓冲近正方形,顶边被推到约 +45° ⇒ 实测"太上"。
/// **呼出键(2026-09-27 真机反馈后加)**:真机 = 右手 **A 键**开合菜单、**B 键**收放小地图;
/// 编辑器 = **M / N**。原因:Overlay 画布在头显里的落点由"眼缓冲宽高比"决定(PICO 每眼近正方形),
/// 角上的按钮既够不准也不该逼玩家抬手去点 —— 与 SprintController(左 Grip/左 Shift)、
/// TerraceCinema(右 Ctrl)同一套"legacy XR + 键盘兜底"读法。角上的菜单键仍然保留可用。
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
    public GameObject minimap;                  // 小地图根(可一键收起)
    public GameObject quitConfirm;
    public Slider volumeSlider;
    public Text volumeLabel;
    public KioskTeleporter[] teleporters = new KioskTeleporter[3];

    public const string VolumeKey = "vrlib.masterVolume";
    public const float DefaultVolume = 0.5f;
    /// <summary>建筑轮廓半边(外墙 ±19.8 m)。rig 在这圈之外就算在露台上 —— 二层板面与露台板面同为 4.423 m,y 分不开这两层。</summary>
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
    bool menuKeyWasDown, mapKeyWasDown;
    bool mapVisible = true;
    string lastFloorName;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureSavedVolume()
    {
        AudioListener.volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumeKey, DefaultVolume));
    }

    public bool IsMenuOpen { get { return open; } }
    public bool IsMinimapVisible { get { return mapVisible; } }
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
        // 2026-11-07:背景乐改由常驻的 LibraryBgm 统一管理(20% 常态 / 视频时淡出暂停)。
        // 这里保留原逻辑只是兜底,避免两套各播一份。
        if (LibraryBgm.Instance != null)
        {
            Debug.Log("[LibraryHud] 背景乐已由 LibraryBgm 接管,不再重复播放");
            return;
        }
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

        // 呼出键先读:菜单开合 / 小地图收放(按下那一帧翻转,不要求按住 —— 见文档坑 32)
        // A/X/菜单键 = 菜单；B/Y = 小地图。左右手都收(哪个手顺手都行)。
        bool menuKey = ReadButton(true, MenuControls, Key.M) || ReadButton(false, PrimaryOnly, Key.M);
        if (menuKey && !menuKeyWasDown) ToggleMenu();
        menuKeyWasDown = menuKey;
        bool mapKey = ReadButton(true, SecondaryOnly, Key.N) || ReadButton(false, SecondaryOnly, Key.N);
        if (mapKey && !mapKeyWasDown) SetMinimapVisible(!mapVisible);
        mapKeyWasDown = mapKey;

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
            // ⚠ 2026-10-15 修:这里原来只收 alpha / blocksRaycasts,**漏了 interactable**。
            // 而搭建脚本(LibraryHudSetup)把 MenuPanel 的 CanvasGroup 初值设成了 interactable=false
            // (那是"收起"的静态初值,靠 alpha 收,不靠 SetActive)—— 于是菜单打开后,MenuPanel 里
            // 所有 Button 永远不可交互:面板中部的三个传送按钮、下方齿轮/电源全都点不动。
            menuGroup.interactable = menuGroup.blocksRaycasts = openT > 0.5f;
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
        if (floorLabel != null)
        {
            string fn = FloorNameAt(p);
            if (fn != lastFloorName)
            {
                lastFloorName = fn;
                floorLabel.text = "当前位置 · " + fn;
            }
        }
        // 指北罗盘：dial 的指针永远指向真实北方（-Z），所以当相机转向时 dail 要反向转动来抵消。
        // 这跟"指针"无关：指针是你瞄准的点，指北针是真实北方的基准，两者不应该混用。
        if (cam != null && dial != null)
        {
            Vector3 f = cam.transform.forward;
            f.y = 0f; // 忽略上下俯仰，只算水平面内的转角
            if (f.sqrMagnitude > 1e-4f)
            {
                float angle = Vector3.SignedAngle(Vector3.back, f, Vector3.up);
                dial.localRotation = Quaternion.Euler(0f, 0f, -angle);
                // Debug.Log("[LibraryHud] Compass: angle=" + angle.ToString("F1") + ", dial rotation=" + dial.localRotation.eulerAngles.z.ToString("F1"));
            }
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

    /// <summary>小地图收放(呼出键或程序都可调;收起只是 SetActive(false),不动任何数据)。</summary>
    public void SetMinimapVisible(bool on)
    {
        mapVisible = on;
        if (minimap != null && minimap.activeSelf != on) minimap.SetActive(on);
    }

    /// <summary>
    /// 呼出键读法:真机走 legacy XR(PICO/OpenXR 都支持),编辑器走键盘 —— 与 SprintController、
    /// TerraceCinema 同一套;两者取"或",所以 XR Device Simulator 里按 M/N 就能测。
    /// </summary>
    static readonly string[] MenuControls = { "primaryButton", "menu" };
    static readonly string[] PrimaryOnly = { "primaryButton" };
    static readonly string[] SecondaryOnly = { "secondaryButton" };

    /// <summary>
    /// 读手柄按键(键盘键仅编辑器调试用)。
    /// 2026-11-09:PICO Neo 3 的旧 XR 输入(InputDevices + CommonUsages)读不到 A/B,
    /// 统一改走 InputSystem(见 VRLibraryXRInput)。
    /// </summary>
    static bool ReadButton(bool rightHand, string[] controlNames, Key key)
    {
        var kb = Keyboard.current;
        if (kb != null && kb[key].isPressed) return true;
        return VRLibraryXRInput.Button(rightHand, controlNames);
    }

    public void OpenMenu()
    {
        open = true;
        if (menuGroup != null) menuGroup.interactable = true;   // 立刻可点,不等下一帧淡入
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
        if (menuGroup != null) menuGroup.interactable = true;
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
