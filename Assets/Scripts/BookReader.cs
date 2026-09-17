using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 智能图书馆 · 阅读面板(含书签系统)
/// - 分页 + 翻页动画;
/// - 关闭时自动保存书签(当前页 + 内容摘要);
/// - 底部"书签"按钮打开书签列表:格式「第X页,"内容摘要…"」,点击直达对应页;
/// - 书签跨会话保留(静态存储),按书名区分。
/// </summary>
public class BookReader : MonoBehaviour
{
    public static BookReader Instance;

    public Text titleText;
    public Text authorText;
    public Text contentText;
    public Text pageText;
    public GameObject panelRoot;
    public Button bookmarkBtn;
    [Header("书签列表面板(编辑期预建,12 个槽位 Button+Text)")]
    public GameObject bookmarkListPanel;
    public GameObject[] bmPool;

    public float distance = 1.55f;
    public float heightOffset = -0.45f;

    public class Bookmark
    {
        public string bookTitle;
        public int page;
        public string snippet;
        public string slotCode;
    }
    public static readonly List<Bookmark> Bookmarks = new List<Bookmark>();

    string[] pages = new string[0];
    int page = 0;
    string currentTitle = "";
    LibraryBook currentBook;
    bool flipping;

    // 书签列表面板(运行时动态构建)

    void Awake()
    {
        Instance = this;
        if (panelRoot == null) panelRoot = gameObject;
        panelRoot.SetActive(false);
        if (bookmarkBtn != null)
            bookmarkBtn.onClick.AddListener(ToggleBookmarkList);
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    public void Open(LibraryBook book)
    {
        if (book == null) return;
        currentBook = book;
        currentTitle = book.title;
        if (titleText != null) titleText.text = "《" + book.title + "》" + (string.IsNullOrEmpty(book.slotCode) ? "" : "  [" + book.slotCode + "]");
        if (authorText != null) authorText.text = "作者:" + book.author;

        pages = BuildPages(book.summary);
        // 恢复书签到上次阅读页
        var bm = Bookmarks.Find(b => b.bookTitle == currentTitle);
        page = bm != null ? Mathf.Clamp(bm.page, 0, pages.Length - 1) : 0;
        if (bookmarkListPanel != null) bookmarkListPanel.SetActive(false);
        panelRoot.SetActive(true);
        PositionInFront();
        Refresh(false);
        UpdateBookmarkButton();
    }

    public void NextPage() { if (page < pages.Length - 1) { page++; Refresh(true); } }
    public void PrevPage() { if (page > 0) { page--; Refresh(true); } }

    public void Close()
    {
        SaveBookmark();
        if (bookmarkListPanel != null) bookmarkListPanel.SetActive(false);
        panelRoot.SetActive(false);
    }

    // ------------------------------------------------ 书签

    void SaveBookmark()
    {
        if (currentBook == null || pages.Length == 0) return;
        var bm = Bookmarks.Find(b => b.bookTitle == currentTitle);
        if (bm == null) { bm = new Bookmark(); Bookmarks.Add(bm); }
        bm.bookTitle = currentTitle;
        bm.page = page;
        bm.slotCode = currentBook.slotCode;
        string plain = pages[page].Replace("\n", " ").Trim();
        bm.snippet = plain.Length > 18 ? plain.Substring(0, 18) + "…" : plain;
        UpdateBookmarkButton();
        Debug.Log("[书签] 已保存:" + currentTitle + " 第" + (page + 1) + "页");
    }

    void UpdateBookmarkButton()
    {
        if (bookmarkBtn == null) return;
        if (bookmarkBtn != null)
        {
            var t = bookmarkBtn.GetComponentInChildren<Text>();
            if (t != null) t.text = "书签 (" + Bookmarks.Count + ")";
        }
    }

    void ToggleBookmarkList()
    {
        if (bookmarkListPanel == null) { Debug.LogWarning("[书签] 未配置书签列表面板"); return; }
        bool show = !bookmarkListPanel.activeSelf;
        bookmarkListPanel.SetActive(show);
        if (show) RefreshList();
    }

    void RefreshList()
    {
        if (bmPool == null) return;
        foreach (var slot in bmPool)
        {
            if (slot == null) continue;
            slot.SetActive(false);
            var btn = slot.GetComponent<Button>();
            if (btn != null) btn.onClick.RemoveAllListeners();
        }
        if (Bookmarks.Count == 0)
        {
            if (bmPool.Length > 0 && bmPool[0] != null)
            {
                bmPool[0].SetActive(true);
                var t = bmPool[0].GetComponentInChildren<Text>(true);
                if (t != null) t.text = "暂无书签 · 关闭任意一本书会自动保存";
            }
            return;
        }
        // 最新在前,最多填满槽位
        int n = Mathf.Min(Bookmarks.Count, bmPool.Length);
        for (int k = 0; k < n; k++)
        {
            var bm = Bookmarks[Bookmarks.Count - 1 - k];
            var slot = bmPool[k];
            if (slot == null) continue;
            slot.SetActive(true);
            var t = slot.GetComponentInChildren<Text>(true);
            if (t != null) t.text = "第" + (bm.page + 1) + "页,\"" + bm.snippet + "\"";
            var btn = slot.GetComponent<Button>();
            if (btn != null)
            {
                var captured = Bookmarks.Count - 1 - k;
                btn.onClick.AddListener(delegate { JumpToBookmark(captured); });
            }
        }
    }

    void JumpToBookmark(int index)
    {
        var bm = Bookmarks[index];
        if (bm.bookTitle != currentTitle)
        {
            var all = FindObjectsOfType<LibraryBook>();
            foreach (var b in all) if (b.title == bm.bookTitle) { Open(b); break; }
        }
        page = Mathf.Clamp(bm.page, 0, pages.Length - 1);
        Refresh(true);
        if (bookmarkListPanel != null) bookmarkListPanel.SetActive(false);
    }

    // ------------------------------------------------ 面板行为

    void Update()
    {
        if (panelRoot != null && panelRoot.activeSelf && Camera.main != null)
        {
            var camT = Camera.main.transform;
            Vector3 target = camT.position + camT.forward * distance;
            target.y = camT.position.y + heightOffset + 0.75f;
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

    void PositionInFront()
    {
        var cam = Camera.main;
        if (cam == null) return;
        var camT = cam.transform;
        Vector3 pos = camT.position + camT.forward * distance;
        pos.y = camT.position.y + heightOffset + 0.75f;
        transform.position = pos;
        Vector3 dir = pos - camT.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(dir);
    }

    string[] BuildPages(string summary)
    {
        if (string.IsNullOrEmpty(summary)) summary = "(此书暂无内容)";
        string[] parts;
        if (summary.Contains("\f")) parts = summary.Split('\f');
        else
        {
            var list = new List<string>();
            for (int i = 0; i < summary.Length; i += 160)
                list.Add(summary.Substring(i, Mathf.Min(160, summary.Length - i)));
            parts = list.ToArray();
        }
        return parts;
    }

    void Refresh(bool animate)
    {
        if (contentText == null) return;
        if (pageText != null) pageText.text = (page + 1) + " / " + pages.Length;
        if (!animate) { contentText.text = pages[page]; return; }
        if (!flipping) StartCoroutine(FlipRoutine());
    }

    IEnumerator FlipRoutine()
    {
        flipping = true;
        var rt = contentText.rectTransform;
        while (true)
        {
            float t = 0f;
            while (t < 0.12f) { t += Time.deltaTime; rt.localScale = new Vector3(Mathf.Clamp01(1f - t / 0.12f), 1f, 1f); yield return null; }
            contentText.text = pages[page];
            t = 0f;
            while (t < 0.12f) { t += Time.deltaTime; rt.localScale = new Vector3(Mathf.Clamp01(t / 0.12f), 1f, 1f); yield return null; }
            rt.localScale = Vector3.one;
            break;
        }
        flipping = false;
    }

}
