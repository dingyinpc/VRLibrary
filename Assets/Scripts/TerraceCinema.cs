using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// 智能图书馆 · 星空露台观影模式控制器。
///
/// 四条已定案的前提(2026-10-19 修订):
///   ① 目录内容读 `Assets/Resources/filmography.json`(片名/文件名/时长/简介/类型),换片单只改那个文件;
///   ② "座位"= 观影模式**硬锁站位**:不放实体椅子,进入后把 XR Rig 逐帧钉在 `seat` 上
///      (头可以自由转,身体不动);**不再提供「留下继续看 / 离座走动」这类确认**,
///      摇杆推走或瞬移都会被下一帧拽回座位 —— 想离开只能按播控面板上的「回一层大厅」;
///   ③ 按键:右手 Grip 按一下 = 目录,左手 Grip 按一下 = 播控面板(暂停 / 倍速 / 后退 5 秒 /
///      前进 5 秒 / 跳过 / 关闭影片 / 回一层大厅),观影区内禁用加速(SprintController.suppressed),
///      回到一层后自动恢复原义;目录另有 OpenCatalog()/CloseCatalog() 可编程开关;
///      **目录的位置以"座位→银幕"方向为前**,固定摆在人物正前方,不跟随头部朝向;
///   ④ 观影院须知一次性确认:PlayerPrefs 记 `ackKey`,没确认前不给进观影态;确认之后仍可从目录底部
///      那颗「观影须知」回看(OnShowNotice),回看不再卡流程。
///
/// **所有 UI 都是编辑期由 `VRLibrary/搭建星空露台` 预建的**,运行时只切 activeSelf 和改文字 ——
/// Play 模式里 AddComponent<Text> / 取内置字体会 NRE(交接提示词 坑 1)。
/// </summary>
public class TerraceCinema : MonoBehaviour
{
    [Header("锚点与渲染目标")]
    public XROrigin origin;
    public Transform seat;                 // 站位:位置 + 朝向就是"座位"
    public TerraceScreen screen;
    [Tooltip("导览屏那颗按钮用它先把人送到露台上(搭建菜单接好 origin 与 destination)")]
    public KioskTeleporter teleporter;
    [Tooltip("播控面板那颗「回一层大厅」用它把人送回一层(搭建菜单注入露台回程面板上同一份传送器)")]
    public KioskTeleporter lobbyTeleporter;

    [Header("预建面板(编辑期摆好,运行时只显隐)")]
    public GameObject noticePanel;
    public GameObject catalogPanel;
    public GameObject transportPanel;

    [Header("预建按钮")]
    public Button[] filmSlots = new Button[6];
    public Text[] filmLabels = new Text[6];
    public Button ackButton;
    public Button noticeButton;        // 目录里那颗「观影须知」:确认过之后还能回看
    public Button pauseButton;
    public Button speedButton;
    public Button skipButton;
    public Button seekBackButton;
    public Button seekFwdButton;
    public Button stopButton;          // 关闭影片(退回待机卡),不解锁站位
    public Button lobbyButton;         // 唯一的离开方式:解锁 + 传送回一层大厅
    public Text pauseLabel;
    public Text speedLabel;
    public Text nowPlayingLabel;

    [Header("参数")]
    public string ackKey = "cinema.notice.ack.v1";
    [Tooltip("钉回座位的容差:超过这个距离就写回位置。观影态下不提供任何离座确认")]
    public float pinEpsilon = 0.01f;
    [Tooltip("选完片到自动开播的停顿(秒),让屏上的影片信息被看到")]
    public float cardHoldSeconds = 4f;
    [Tooltip("「前进 / 后退」一颗跳多少秒")]
    public float seekSeconds = 5f;

    static readonly float[] Speeds = { 1f, 1.5f, 2f, 0.5f };
    static readonly string[] GripControls = { "gripPressed", "grip" };

    int speedIndex;
    int current = -1;
    bool seated;
    bool paused;
    bool ackConfirmed;
    bool catalogOpen;            // 目录:按一下右手 Grip 翻转,开了就一直在(不再要求按住)
    bool transportOpen;          // 播控:同上,左手 Grip
    bool gripRWasDown;
    bool gripLWasDown;
    float cardUntil;
    Vector3 pinnedRigPos;

    readonly List<KeyValuePair<UnityEngine.XR.Interaction.Toolkit.LocomotionProvider, bool>> suppressedLocomotion
        = new List<KeyValuePair<UnityEngine.XR.Interaction.Toolkit.LocomotionProvider, bool>>();

    public bool IsSeated { get { return seated; } }

    void OnEnable()
    {
        SetActive(noticePanel, false);
        SetActive(catalogPanel, false);
        SetActive(transportPanel, false);
        catalogOpen = transportOpen = false;
        gripRWasDown = gripLWasDown = false;
        cardUntil = 0f;
        ackConfirmed = PlayerPrefs.HasKey(ackKey);
    }

    void OnDisable()
    {
        if (seated)
        {
            seated = false;
            SprintController.suppressed = false;
            RestoreLocomotion();
        }
    }

    void Start()
    {
        // 面板是编辑期建的,片名只能运行时灌进去(槽位数量固定 6 个,多出来的隐藏)
        var f = TerraceScreen.Films;
        for (int i = 0; i < filmSlots.Length; i++)
        {
            if (filmSlots[i] == null) continue;
            if (i < f.Length)
            {
                if (filmLabels[i] != null)
                    filmLabels[i].text = "《" + f[i].title + "》 · " + f[i].minutes + " 分钟";
                filmSlots[i].gameObject.SetActive(true);
            }
            else filmSlots[i].gameObject.SetActive(false);
        }
        if (nowPlayingLabel != null && f.Length > 0) nowPlayingLabel.text = "正在选映：" + f[0].title;
    }

    // ------------------------------------------------------------ 对外入口

    /// <summary>导览屏那颗"星空观影"按钮调这个:先传送到露台,再进观影态。</summary>
    public void EnterFromKiosk()
    {
        if (teleporter != null) teleporter.TeleportToDestination();
        Enter();
    }

    public void Enter()
    {
        if (seat == null || origin == null)
        {
            Debug.LogWarning("[Cinema] 没接 seat 或 origin,观影模式不可用");
            return;
        }
        seated = true;
        SprintController.suppressed = true;              // 观影区内不加速
        SuppressLocomotion();
        ackConfirmed = PlayerPrefs.HasKey(ackKey);
        PinToSeat(true);
        if (!ackConfirmed)
        {
            SetActive(noticePanel, true);                // 没确认过 → 先弹须知
            catalogOpen = transportOpen = false;
            Debug.Log("[Cinema] 进入观影态,等待须知确认");
        }
        else
        {
            SetActive(noticePanel, false);
            catalogOpen = true;                          // 直接给目录,伸手就能点
            transportOpen = false;
            Debug.Log("[Cinema] 进入观影态,目录已展开(座位正前方 / 朝向银幕)");
        }
        ApplyPanels();
    }

    /// <summary>
    /// 退出观影态。**只有「回一层大厅」会调它**:先解锁再传送,否则下一帧 LateUpdate
    /// 又会把人钉回座位。观影期间不提供任何就地解锁/离座入口。
    /// </summary>
    public void Leave()
    {
        // 2026-11-07:离开影院 = 停止放映(视频 + 音轨 + 信息卡回待机)。
        // 视频一停 IsPlayingNow 变 false,背景乐 LibraryBgm 会自动淡回 0.08。
        if (screen != null)
        {
            screen.StopFilm();
            screen.SetPlaybackSpeed(1f);
            Debug.Log("[Cinema] 离开影院 → 已停止放映;背景乐自动恢复");
        }

        seated = false;
        paused = false;
        speedIndex = 0;
        current = -1;
        catalogOpen = transportOpen = false;
        if (pauseLabel != null) pauseLabel.text = "暂停";
        if (speedLabel != null) speedLabel.text = "倍速 1.0×";
        if (nowPlayingLabel != null) nowPlayingLabel.text = "正在选映：(无)";
        SprintController.suppressed = false;             // 恢复加速原义
        RestoreLocomotion();
        SetActive(noticePanel, false);
        SetActive(catalogPanel, false);
        SetActive(transportPanel, false);
        Debug.Log("[Cinema] 退出观影态");
    }

    // ------------------------------------------------------------ 每帧

    void Update()
    {
        if (!seated) return;

        // **开播计时器必须放在任何早退之前**:上一版它排在"离座确认"的早退之后,
        // 一弹窗选完的片子就永远不开播(用户实测的"播放不了"就是这条)。
        // 现在离座流程已删除,但这条顺序仍然保留,避免以后再插模态逻辑时重蹈覆辙。
        if (cardUntil > 0f && Time.time >= cardUntil)
        {
            cardUntil = 0f;
            if (screen != null) screen.StartPlayback();
        }

        if (!ackConfirmed) return;         // 须知没确认前是模态的,只允许钉人

        // Grip:**按下那一帧翻转**,开了就一直显示。上一版是"按住才显示",
        // 意味着要用同一只手一直捏着 Grip 再去抠扳机点按钮 —— 用户实测"目录很难正常呼出"
        // 目录与播控互斥:目录现在摆在正前方,同时开播控会叠在一起
        bool gripR = ReadGrip(true), gripL = ReadGrip(false);
        if (gripR && !gripRWasDown)
        {
            catalogOpen = !catalogOpen;
            if (catalogOpen) transportOpen = false;
        }
        if (gripL && !gripLWasDown)
        {
            transportOpen = !transportOpen;
            if (transportOpen) catalogOpen = false;
        }
        gripRWasDown = gripR; gripLWasDown = gripL;

        ApplyPanels();
    }

    /// <summary>面板可见性只由这一处算:开了才显示,须知在场时让位。</summary>
    void ApplyPanels()
    {
        bool noticeOn = noticePanel != null && noticePanel.activeSelf;
        SetActive(catalogPanel, catalogOpen && !noticeOn);
        SetActive(transportPanel, transportOpen && !noticeOn);
    }

    /// <summary>
    /// 硬锁站位:只要还在观影态,每帧都把 XR Rig 拉回座位。
    /// 不判"想不想走"、不做大位移解锁 —— 摇杆、瞬移、房间尺度走动统统会被下一帧拽回来。
    /// 朝向不强制:玩家仍可转头/转身环视,但目录与播控固定在座位上(以座位→银幕为前)。
    /// </summary>
    void LateUpdate()
    {
        if (!seated) return;
        PinToSeat(false);
    }

    void PinToSeat(bool hard)
    {
        if (seat == null || origin == null) return;
        var t = origin.transform;
        if (hard)
        {
            var cam = origin.Camera != null ? origin.Camera.transform : null;
            if (cam != null)
            {
                float delta = Mathf.DeltaAngle(cam.eulerAngles.y, seat.eulerAngles.y);
                t.Rotate(0f, delta, 0f);
                Vector3 camOff = cam.position - t.position;
                camOff.y = 0f;
                pinnedRigPos = new Vector3(seat.position.x - camOff.x, seat.position.y, seat.position.z - camOff.z);
            }
            else
            {
                t.rotation = seat.rotation;
                pinnedRigPos = seat.position;
            }
            t.position = pinnedRigPos;
            return;
        }
        if (Vector3.Distance(t.position, pinnedRigPos) > pinEpsilon)
        {
            t.position = pinnedRigPos;
        }
    }

    void SuppressLocomotion()
    {
        RestoreLocomotion();
        if (origin == null) return;
        var root = origin.transform.parent != null ? origin.transform.parent : origin.transform;
        foreach (var lp in root.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.LocomotionProvider>(true))
        {
            if (lp == null) continue;
            string n = lp.GetType().Name;
            // 只停移动/传送,保留 Turn 以便观影时可微调或自由转头
            if (n.Contains("Move") || n.Contains("Teleport"))
            {
                suppressedLocomotion.Add(new KeyValuePair<UnityEngine.XR.Interaction.Toolkit.LocomotionProvider, bool>(lp, lp.enabled));
                lp.enabled = false;
            }
        }
    }

    void RestoreLocomotion()
    {
        for (int i = 0; i < suppressedLocomotion.Count; i++)
        {
            var kv = suppressedLocomotion[i];
            if (kv.Key != null) kv.Key.enabled = kv.Value;
        }
        suppressedLocomotion.Clear();
    }

    // ------------------------------------------------------------ 按钮动作(都绑在预建按钮上)

    public void OnAckNotice()
    {
        PlayerPrefs.SetInt(ackKey, 1);
        PlayerPrefs.Save();
        ackConfirmed = true;
        SetActive(noticePanel, false);
        OpenCatalog();
        Debug.Log("[Cinema] 须知已确认,目录展开");
    }

    /// <summary>目录里那颗「观影须知」:确认过之后随时还能回看。回看不该再卡住流程,所以只是把面板摆出来。</summary>
    public void OnShowNotice()
    {
        catalogOpen = false;
        transportOpen = false;
        SetActive(noticePanel, true);
        ApplyPanels();
        Debug.Log("[Cinema] 回看观影须知");
    }

    /// <summary>展开目录(右手 Grip 按一下也是这个效果)。目录与播控互斥。</summary>
    public void OpenCatalog() { catalogOpen = true; transportOpen = false; ApplyPanels(); }

    /// <summary>收起目录。</summary>
    public void CloseCatalog() { catalogOpen = false; ApplyPanels(); }

    /// <summary>
    /// 播控面板上那颗「回一层大厅」:观影态**唯一的离开方式**。先解锁再传送,
    /// 否则下一帧 LateUpdate 会把玩家拽回座位。
    /// </summary>
    public void OnBackToLobby()
    {
        Leave();
        if (lobbyTeleporter != null) lobbyTeleporter.TeleportToDestination();
        else Debug.LogWarning("[Cinema] 没接 lobbyTeleporter → 没有离开观影区的入口");
    }

    public void OnSelectFilm0() { Select(0); }
    public void OnSelectFilm1() { Select(1); }
    public void OnSelectFilm2() { Select(2); }
    public void OnSelectFilm3() { Select(3); }
    public void OnSelectFilm4() { Select(4); }
    public void OnSelectFilm5() { Select(5); }

    public void OnPauseToggle()
    {
        if (screen == null) return;
        paused = !paused;
        screen.SetPaused(paused);
        if (pauseLabel != null) pauseLabel.text = paused ? "继续播放" : "暂停";
    }

    public void OnCycleSpeed()
    {
        if (screen == null) return;
        speedIndex = (speedIndex + 1) % Speeds.Length;
        screen.SetPlaybackSpeed(Speeds[speedIndex]);
        if (speedLabel != null) speedLabel.text = "倍速 " + Speeds[speedIndex].ToString("0.0") + "×";
    }

    public void OnSeekBack() { if (screen != null) screen.SeekBy(-seekSeconds); }
    public void OnSeekForward() { if (screen != null) screen.SeekBy(seekSeconds); }

    /// <summary>关闭影片:只停片、退回待机卡;站位仍锁着,不是离开观影区。</summary>
    public void OnStopFilm()
    {
        if (screen != null)
        {
            screen.StopFilm();
            screen.SetPlaybackSpeed(1f);
        }
        paused = false;
        speedIndex = 0;
        current = -1;
        if (pauseLabel != null) pauseLabel.text = "暂停";
        if (speedLabel != null) speedLabel.text = "倍速 1.0×";
        if (nowPlayingLabel != null) nowPlayingLabel.text = "正在选映：(无)";
    }

    public void OnSkip()
    {
        var films = TerraceScreen.Films;
        if (films == null || films.Length == 0) return;
        Select((current + 1) % films.Length);
    }

    void Select(int i)
    {
        var films = TerraceScreen.Films;
        if (films == null || i < 0 || i >= films.Length) { Debug.LogWarning("[Cinema] 目录第 " + i + " 条没有内容"); return; }
        current = i;
        var f = films[i];
        paused = false;
        speedIndex = 0;
        if (pauseLabel != null) pauseLabel.text = "暂停";
        if (speedLabel != null) speedLabel.text = "倍速 1.0×";
        if (screen != null)
        {
            screen.SetPlaybackSpeed(1f);
            screen.ShowInfoCard(f);                     // 屏上先出影片信息
            cardUntil = Time.time + cardHoldSeconds;    // 停顿后自动开播
        }
        if (nowPlayingLabel != null) nowPlayingLabel.text = "正在选映：" + f.title;
        catalogOpen = false;                              // 点完自动收起
        ApplyPanels();
        Debug.Log("[Cinema] 选片 #" + i + "《" + f.title + "》" + f.minutes + " 分钟,文件 " + f.fileName);
    }

    // ------------------------------------------------------------ 输入

    static bool ReadGrip(bool right)
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null)
        {
            if (right && kb.rightCtrlKey.isPressed) return true;
            if (!right && kb.fKey.isPressed) return true;
        }
        var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(
            right ? UnityEngine.XR.XRNode.RightHand : UnityEngine.XR.XRNode.LeftHand);
        bool grip;
        if (device != null && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out grip) && grip)
            return true;

        // 2026-11-09:PICO Neo 3 的旧 XR 输入读不到 Grip,补一条 InputSystem 路径
        return VRLibraryXRInput.Button(right, GripControls);
    }

    static void SetActive(GameObject go, bool on)
    {
        if (go == null || go.activeSelf == on) return;
        try { go.SetActive(on); }
        catch (KeyNotFoundException e)
        {
            // XR Device Simulator 在输入回调里换设备/切 UI 悬停时,面板画布的 TrackedDeviceGraphicRaycaster
            // 可能在"注册表还没建好"的窗口期被关掉 → 包内 OnDisable 弹 KeyNotFoundException 并沿
            // SetActive 一路抛上来,谁调谁死(冒烟测试的运行协程就是这么被杀的)。这种一次性时序毛刺
            // 无害,吞掉;正常注册路径不受影响。
            Debug.LogWarning("[Cinema] 面板 " + go.name + " 开合撞上射线注册毛刺(已忽略): " + e.Message);
        }
    }

    /// <summary>供冒烟测试用:当前目录里有几条、现在选了第几条、三块面板当前显不显示。</summary>
    public int FilmCount { get { return TerraceScreen.Films == null ? 0 : TerraceScreen.Films.Length; } }
    public int CurrentFilm { get { return current; } }
    public bool NoticeVisible { get { return noticePanel != null && noticePanel.activeSelf; } }
    public bool CatalogVisible { get { return catalogPanel != null && catalogPanel.activeSelf; } }
    public bool TransportVisible { get { return transportPanel != null && transportPanel.activeSelf; } }
}
