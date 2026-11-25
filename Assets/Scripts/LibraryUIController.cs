using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 智能图书馆 · 中央信息屏控制器
/// 挂在中央导览台(InfoCanvas 所在物体)上。
/// 三个按钮(馆藏导览 / 智能检索 / 归还服务)通过 onClick 持久化事件绑定到 OnXxxButton;
/// 书籍被抓取时由 LibraryBook 调用 ShowBookInfo。
/// </summary>
public class LibraryUIController : MonoBehaviour
{
    public static LibraryUIController Instance;

    [Tooltip("信息屏状态文本")]
    public Text statusText;

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void OnGuideButton()
    {
        Show("馆藏导览:两侧为智能书架区,可传送或行走浏览;\n射线指向书籍扣动扳机即可在阅读面板查看正文。");
    }

    public void OnSearchButton()
    {
        Show("智能检索:输入书名 / 作者 / 书架编号可模糊查询,\n选中结果即传送到该书面前并高亮。");
    }

    /// <summary>2026-11-06 演示裁剪:归还服务已整体移除(书不可抓,该功能实际不可用)。</summary>

    // ── 退出图书馆(2026-11-07 加) ─────────────────────────────────
    // 入口导览屏(InfoCanvas)最下面那颗通栏按钮绑这里。
    // 两下确认:第一下只在状态栏提示,4 秒内再按一次才真正退出,避免射线误触把演示关掉。
    const float ExitArmSeconds = 4f;
    float exitArmedAt = -99f;

    public void OnExitLibraryButton()
    {
        if (Time.unscaledTime - exitArmedAt > ExitArmSeconds)
        {
            exitArmedAt = Time.unscaledTime;
            Show("再按一次「退出图书馆」即关闭应用\n(4 秒内有效)");
            Debug.Log("[LibraryUI] 退出待确认:再按一次「退出图书馆」");
            return;
        }

        Debug.Log("[LibraryUI] 退出图书馆 → 关闭应用");
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }


    public void ShowBookInfo(LibraryBook book)
    {
        // 状态栏只有两三行的高度:方案书的 summary 是整篇 md(几千字),整段灌进来会把导览屏
        // 撑成一团叠字(2026-10-03 实测"关书回到入口面板,字全串起来")。这里只放书目信息
        // 和 60 字摘要,正文交给阅读面板显示。
        string brief = (book.summary ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        if (brief.Length > 60) brief = brief.Substring(0, 60) + "…";
        string slot = string.IsNullOrEmpty(book.slotCode) ? "" : " [" + book.slotCode + "]";
        Show($"《{book.title}》{slot}\n作者:{book.author}\n{brief}");
    }

    public void Show(string message)
    {
        if (statusText != null) statusText.text = message;
        Debug.Log($"[LibraryUI] {message.Replace("\n", " ")}");
    }
}
