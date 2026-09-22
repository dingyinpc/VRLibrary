using UnityEngine;
using UnityEngine.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// 智能图书馆 · 星空露台观影模式控制器。
///
/// 四条已定案的前提(2026-09-21 与用户确认):
///   ① 目录内容读 `Assets/Resources/filmography.json`(片名/文件名/时长/简介/类型),换片单只改那个文件;
///   ② "座位"= 观影模式锁站位:不放实体椅子,进入后把 XR Rig 钉在 `seat` 上(头可以自由转,身体不动),
///      一旦检测到移动(位置被推走)就弹"要离座吗",选离座才真正解锁;
///   ③ 按键:右手 Grip 按住 = 目录,左手 Grip 按住 = 播控面板(暂停 / 倍速 / 后退 5 秒 / 前进 5 秒 /
///      跳过 / 关闭影片 / 退出),观影区内禁用加速(SprintController.suppressed),退出观影态自动恢复原义;
///      目录另有 OpenCatalog()/CloseCatalog() 可编程开关(冒烟测试与"点完自动收起"都靠它);
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
    public GameObject leavePanel;

    [Header("预建按钮")]
    public Button[] filmSlots = new Button[6];
    public Text[] filmLabels = new Text[6];
    public Button ackButton;
    public Button noticeButton;        // 目录里那颗「观影须知」:确认过之后还能回看
    public Button stayButton;
    public Button leaveButton;
    public Button pauseButton;
    public Button speedButton;
    public Button skipButton;
    public Button seekBackButton;
    public Button seekFwdButton;
    public Button stopButton;          // 关闭影片(退回待机卡),不等于退出观影
    public Button exitButton;
    public Button lobbyButton;         // 回一层大厅:就地解锁 + 传送回去(面前就能按,不用转身打背后那块板)
    public Text pauseLabel;
    public Text speedLabel;
    public Text nowPlayingLabel;

    [Header("参数")]
    public string ackKey = "cinema.notice.ack.v1";
    [Tooltip("钉回座位的容差:离座超过这个距离才写回位置(不是「是否想走」的判据)")]
    public float pinEpsilon = 0.05f;
    [Tooltip("左摇杆推到多大算「想走」(0~1)。站姿晃动与追踪漂移通常远小于这个值")]
    public float moveIntent = 0.35f;
    [Tooltip("摇杆要连续推多久才弹离座确认(秒)")]
    public float moveHoldSeconds = 0.25f;
    [Tooltip("瞬移级别的大位移直接当作已经离座(按了回程按钮 / 又被传走)")]
    public float teleportDistance = 2.5f;
    [Tooltip("选完片到自动开播的停顿(秒),让屏上的影片信息被看到")]
    public float cardHoldSeconds = 4f;
    [Tooltip("「前进 / 后退」一颗跳多少秒")]
    public float seekSeconds = 5f;

    static readonly float[] Speeds = { 1f, 1.5f, 2f, 0.5f };

    int speedIndex;
    int current = -1;
    bool seated;
    bool paused;
    bool catalogOpen;            // 目录:按一下右手 Grip 翻转,开了就一直在(不再要求按住)
    bool transportOpen;          // 播控:同上,左手 Grip
    bool gripRWasDown;
    bool gripLWasDown;
    bool prompting;
    float moveHold;
    float cardUntil;

    public bool IsSeated { get { return seated; } }

    void OnEnable()
    {
        SetActive(noticePanel, false);
        SetActive(catalogPanel, false);
        SetActive(transportPanel, false);
        SetActive(leavePanel, false);
        prompting = false;
        catalogOpen = transportOpen = false;
        gripRWasDown = gripLWasDown = false;
        moveHold = 0f;
        cardUntil = 0f;
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
        prompting = false;
        moveHold = 0f;
        SprintController.suppressed = true;              // 观影区内不加速
        PinToSeat(true);
        if (!PlayerPrefs.HasKey(ackKey))
        {
            SetActive(noticePanel, true);                // 没确认过 → 先弹须知
            catalogOpen = false;
            Debug.Log("[Cinema] 进入观影态,等待须知确认");
        }
        else
        {
            SetActive(noticePanel, false);
            catalogOpen = true;                          // 直接给目录,伸手就能点
            Debug.Log("[Cinema] 进入观影态,目录已展开");
        }
        ApplyPanels();
    }

    /// <summary>解锁站位。**就地**解锁,不回钉座位:否则玩家刚按屏上的回程按钮传送走,
    /// 下一帧就被拽回露台(PinToSeat 与"我已经走了"这两件事互相矛盾)。</summary>
    public void Leave()
    {
        seated = false;
        paused = false;
        prompting = false;
        catalogOpen = transportOpen = false;
        SprintController.suppressed = false;             // 恢复加速原义
        SetActive(noticePanel, false);
        SetActive(catalogPanel, false);
        SetActive(transportPanel, false);
        SetActive(leavePanel, false);
        Debug.Log("[Cinema] 退出观影态,就地恢复日常交互");
    }

    // ------------------------------------------------------------ 每帧

    void Update()
    {
        if (!seated) return;

        // **开播计时器必须放在任何早退之前**:上一版它在 prompting 的早退之后,
        // 于是"离座确认"一挡,选完的片子就永远不开播(用户实测的"播放不了"就是这条)
        if (cardUntil > 0f && Time.time >= cardUntil)
        {
            cardUntil = 0f;
            if (screen != null) screen.StartPlayback();
        }

        if (!PlayerPrefs.HasKey(ackKey)) return;         // 须知没确认前是模态的,只允许钉人

        // Grip:**按下那一帧翻转**,开了就一直显示。上一版是"按住才显示",
        // 意味着要用同一只手一直捏着 Grip 再去抠扳机点按钮 —— 用户实测"目录很难正常呼出"
        bool gripR = ReadGrip(true), gripL = ReadGrip(false);
        if (gripR && !gripRWasDown) catalogOpen = !catalogOpen;
        if (gripL && !gripLWasDown) transportOpen = !transportOpen;
        gripRWasDown = gripR; gripLWasDown = gripL;

        // 离座确认:读**摇杆意图**,不读位移。站姿晃动与追踪漂移轻松就超过几厘米,
        // 用位移判"他想走"会一天弹八百次,而且每次弹窗都顺手把目录收掉
        if (!prompting)
        {
            float mv = ReadMoveAxis();
            if (mv > moveIntent)
            {
                moveHold += Time.deltaTime;
                if (moveHold >= moveHoldSeconds) OpenLeavePrompt();
            }
            else moveHold = 0f;
        }

        ApplyPanels();
    }

    /// <summary>面板可见性只由这一处算:开了才显示,须知/离座弹窗在场时让位。</summary>
    void ApplyPanels()
    {
        bool noticeOn = noticePanel != null && noticePanel.activeSelf;
        SetActive(catalogPanel, catalogOpen && !noticeOn && !prompting);
        SetActive(transportPanel, transportOpen && !noticeOn && !prompting);
    }

    /// <summary>
    /// 锁站位。**只负责钉人**;是否"想走"由 Update 里的摇杆意图判。
    /// 位移只留一个传送级兜底(&gt; <see cref="teleportDistance"/> 视为人已经被传走,直接就地解锁),
    /// 早先版本用 6 cm 位移当触发条件,站着不动就会误弹。
    /// </summary>
    void LateUpdate()
    {
        if (!seated || seat == null || origin == null) return;

        var p = origin.transform.position;
        float d = Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(seat.position.x, 0f, seat.position.z));
        if (d > teleportDistance) { Leave(); return; }   // 是传送走的,不是"想离座",别弹窗
        PinToSeat(false);
    }

    void PinToSeat(bool hard)
    {
        if (seat == null || origin == null) return;
        var t = origin.transform;
        if (hard || Vector3.Distance(t.position, seat.position) > pinEpsilon)
        {
            t.position = seat.position;
            t.rotation = seat.rotation;
        }
    }

    // ------------------------------------------------------------ 按钮动作(都绑在预建按钮上)

    public void OnAckNotice()
    {
        PlayerPrefs.SetInt(ackKey, 1);
        PlayerPrefs.Save();
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

    /// <summary>展开目录(右手 Grip 按一下也是这个效果)。</summary>
    public void OpenCatalog() { catalogOpen = true; ApplyPanels(); }

    /// <summary>收起目录。</summary>
    public void CloseCatalog() { catalogOpen = false; ApplyPanels(); }

    /// <summary>弹离座确认。摇杆推够久会调它,冒烟测试也直接调它来验后续状态。</summary>
    public void OpenLeavePrompt()
    {
        if (prompting) return;
        prompting = true;
        moveHold = 0f;
        catalogOpen = false;
        transportOpen = false;
        SetActive(leavePanel, true);
        ApplyPanels();
        Debug.Log("[Cinema] 检测到推摇杆 → 弹离座确认");
    }

    public void OnStaySeated()
    {
        prompting = false;
        SetActive(leavePanel, false);
        PinToSeat(true);
        ApplyPanels();
        Debug.Log("[Cinema] 选择留下,继续观影");
    }

    public void OnLeaveSeat() { Leave(); }

    /// <summary>
    /// 播控面板上那颗「回一层大厅」:先就地解锁再传送。
    /// 身后那块露台面板也能回程,但**转身用控制器射线打背后的板子**是真机实测里最容易失效的一条路,
    /// 所以面前这颗是主出口。
    /// </summary>
    public void OnBackToLobby()
    {
        Leave();
        if (lobbyTeleporter != null) lobbyTeleporter.TeleportToDestination();
        else Debug.LogWarning("[Cinema] 没接 lobbyTeleporter → 只能自己走回露台面板");
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

    /// <summary>关闭影片:只停片、退回待机卡,人还在观影态里(退出观影是另一颗按钮)。</summary>
    public void OnStopFilm()
    {
        if (screen != null) screen.StopFilm();
        paused = false;
        current = -1;
        if (pauseLabel != null) pauseLabel.text = "暂停";
        if (nowPlayingLabel != null) nowPlayingLabel.text = "正在选映：(无)";
    }

    public void OnSkip()
    {
        var films = TerraceScreen.Films;
        if (films == null || films.Length == 0) return;
        Select((current + 1) % films.Length);
    }

    public void OnExitCinema() { Leave(); }

    void Select(int i)
    {
        var films = TerraceScreen.Films;
        if (films == null || i < 0 || i >= films.Length) { Debug.LogWarning("[Cinema] 目录第 " + i + " 条没有内容"); return; }
        current = i;
        var f = films[i];
        paused = false;
        if (pauseLabel != null) pauseLabel.text = "暂停";
        if (screen != null)
        {
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
        return device != null && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out grip) && grip;
    }

    /// <summary>
    /// "他想走"的意图强度(0~1):先看左手柄摇杆(真机),再看键盘 WASD/方向键(编辑器),最后退到 Gamepad。
    /// **不用位移反推** —— 站姿晃动与追踪漂移轻松超过几厘米,用位移判会不停误弹离座确认。
    /// </summary>
    static float ReadMoveAxis()
    {
        var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
        Vector2 ax;
        if (dev != null && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out ax))
            return ax.magnitude;

        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.sKey.isPressed || kb.aKey.isPressed || kb.dKey.isPressed ||
                kb.upArrowKey.isPressed || kb.downArrowKey.isPressed ||
                kb.leftArrowKey.isPressed || kb.rightArrowKey.isPressed) return 1f;
        }

        var gp = UnityEngine.InputSystem.Gamepad.current;
        if (gp != null) return gp.leftStick.ReadValue().magnitude;
        return 0f;
    }

    static void SetActive(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }

    /// <summary>供冒烟测试用:当前目录里有几条、现在选了第几条、四块面板当前显不显示。</summary>
    public int FilmCount { get { return TerraceScreen.Films == null ? 0 : TerraceScreen.Films.Length; } }
    public int CurrentFilm { get { return current; } }
    public bool NoticeVisible { get { return noticePanel != null && noticePanel.activeSelf; } }
    public bool CatalogVisible { get { return catalogPanel != null && catalogPanel.activeSelf; } }
    public bool TransportVisible { get { return transportPanel != null && transportPanel.activeSelf; } }
    public bool LeaveVisible { get { return leavePanel != null && leavePanel.activeSelf; } }
}
