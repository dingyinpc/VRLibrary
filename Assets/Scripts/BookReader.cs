using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 智能图书馆 · 阅读面板
/// 抓取书籍后扣扳机(LibraryBook 的 activated 事件)打开:
/// - 在玩家面前展开一块阅读屏(标题/作者/正文/页码 + 上一页/下一页/关闭);
/// - 翻页带"翻页动画"(内容横向收拢再展开,模拟翻页);
/// - 正文分页规则:summary 里用 \f 手动分页;没有则按长度自动切分;
///   只有 1 页时会自动补"第二章/第三章"占位页,方便演示翻页(馆藏数据导入后自然被替换)。
/// </summary>
public class BookReader : MonoBehaviour
{
    public static BookReader Instance;

    [Header("UI 引用(场景构建时自动赋值)")]
    public Text titleText;
    public Text authorText;
    public Text contentText;
    public Text pageText;
    public GameObject panelRoot;

    [Header("面板位置")]
    public float distance = 1.15f;
    public float heightOffset = 0f;

    private string[] pages = new string[0];
    private int page = 0;
    private string currentTitle = "";
    private bool flipping = false;

    void Awake()
    {
        Instance = this;
        if (panelRoot == null) panelRoot = gameObject;
        panelRoot.SetActive(false);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void Open(LibraryBook book)
    {
        if (book == null) return;
        currentTitle = book.title;
        if (titleText != null) titleText.text = "《" + book.title + "》";
        if (authorText != null) authorText.text = "作者:" + book.author;

        pages = BuildPages(book.summary);
        page = 0;
        if (panelRoot != null) panelRoot.SetActive(true);
        PositionInFront();
        Refresh(false);
    }

    public void NextPage()
    {
        if (page < pages.Length - 1) { page++; Refresh(true); }
    }

    public void PrevPage()
    {
        if (page > 0) { page--; Refresh(true); }
    }

    public void Close()
    {
        if (panelRoot != null) panelRoot.SetActive(false);
    }

    void Update()
    {
        // 面板打开期间持续面向玩家、保持距离(轻跟随)
        if (panelRoot != null && panelRoot.activeSelf && Camera.main != null)
        {
            var camT = Camera.main.transform;
            Vector3 target = camT.position + camT.forward * distance;
            target.y = camT.position.y + heightOffset;
            transform.position = Vector3.Lerp(transform.position, target, Time.deltaTime * 6f);
            Vector3 dir = transform.position - camT.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
            {
                Quaternion look = Quaternion.LookRotation(dir);
                transform.rotation = Quaternion.Slerp(transform.rotation, look, Time.deltaTime * 6f);
            }
        }
    }

    private void PositionInFront()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var camT = cam.transform;
        Vector3 pos = camT.position + camT.forward * distance;
        pos.y = camT.position.y + heightOffset;
        transform.position = pos;
        Vector3 dir = pos - camT.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(dir);
    }

    private string[] BuildPages(string summary)
    {
        if (string.IsNullOrEmpty(summary)) summary = "(此书暂无内容)";
        string[] parts;
        if (summary.Contains("\f"))
        {
            parts = summary.Split('\f');
        }
        else
        {
            var list = new System.Collections.Generic.List<string>();
            for (int i = 0; i < summary.Length; i += 160)
            {
                int len = Mathf.Min(160, summary.Length - i);
                list.Add(summary.Substring(i, len));
            }
            parts = list.ToArray();
        }
        if (parts.Length < 3)
        {
            var list2 = new System.Collections.Generic.List<string>(parts);
            while (list2.Count < 3)
                list2.Add("\n\n(第" + (list2.Count + 1) + " 页 · 本章内容待录入\n\n馆藏数据导入后将显示真实正文。)");
            parts = list2.ToArray();
        }
        return parts;
    }

    private void Refresh(bool animate)
    {
        if (contentText == null) return;
        if (pageText != null) pageText.text = (page + 1) + " / " + pages.Length;
        if (!animate) { contentText.text = pages[page]; return; }
        if (!flipping) StartCoroutine(FlipRoutine());
    }

    /// <summary>翻页动画:正文横向收拢 → 换页 → 展开,模拟翻页。</summary>
    private IEnumerator FlipRoutine()
    {
        flipping = true;
        var rt = contentText.rectTransform;
        float t = 0f;
        Vector3 full = new Vector3(1f, 1f, 1f);
        while (t < 0.12f)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / 0.12f);
            rt.localScale = new Vector3(1f - k, 1f, 1f);
            yield return null;
        }
        contentText.text = pages[page];
        t = 0f;
        while (t < 0.12f)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / 0.12f);
            rt.localScale = new Vector3(k, 1f, 1f);
            yield return null;
        }
        rt.localScale = full;
        flipping = false;
    }
}
