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
        Show("馆藏导览:两侧为智能书架区,可传送或行走浏览;\n射线悬停书籍查看位置,抓取后详情显示在此处。");
    }

    public void OnSearchButton()
    {
        Show("智能检索:输入书名 / 作者 / 书架编号可模糊查询,\n选中结果即传送到该书面前并高亮。");
    }

    public void OnReturnButton()
    {
        Show("归还服务:请前往北侧服务台,\n将书籍放入发光感应区即可自动归还。");
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
