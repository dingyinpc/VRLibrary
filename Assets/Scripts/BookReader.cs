using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;

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

    [Header("指外即关(面板开着时,指针不在面板上点一下 = 关书)")]
    public float missMargin = 0.08f;   // 面板矩形边缘的判定余量(米),防"贴边点按钮被判成点外面"
    public float openGrace = 0.35f;    // 开书保护期(秒):开书那一下扳机本来就按着,不算点外面

    readonly List<XRBaseController> missCtrls = new List<XRBaseController>();
    readonly List<bool> missHeld = new List<bool>();
    readonly List<XRRayInteractor> missRays = new List<XRRayInteractor>();
    RectTransform panelRT;
    float lastOpenTime = -99f;

    public class Bookmark
    {
        public string bookTitle;
        public int page;
        public string snippet;
        public string slotCode;
    }
    public static readonly List<Bookmark> Bookmarks = new List<Bookmark>();

    const string kPrefPrefix = "BM_";
    /// <summary>启动时从 PlayerPrefs 恢复书签(跨应用启动保留)。</summary>
    void LoadBookmarks()
    {
        for (int i = 0; i < 200; i++)
        {
            string v = PlayerPrefs.GetString(kPrefPrefix + i, "");
            if (v.Length == 0) break;
            var parts = v.Split('|');
            if (parts.Length < 2) continue;
            var bm = new Bookmark();
            int.TryParse(parts[0], out bm.page);
            bm.bookTitle = parts[1];
            bm.slotCode = parts.Length > 2 ? parts[2] : "";
            bm.snippet = parts.Length > 3 ? parts[3] : "";
            Bookmarks.Add(bm);
        }
    }
    void SaveBookmarkToPrefs(Bookmark bm)
    {
        // 找到该书签在列表中的位置作为槽位;重写全部书签保持顺序稳定
        int idx = Bookmarks.IndexOf(bm);
        for (int i = 0; i < 200; i++)
        {
            if (i < Bookmarks.Count)
            {
                var b = Bookmarks[i];
                PlayerPrefs.SetString(kPrefPrefix + i, b.page + "|" + b.bookTitle + "|" + b.slotCode + "|" + b.snippet);
            }
            else if (PlayerPrefs.GetString(kPrefPrefix + i, "").Length > 0)
            {
                PlayerPrefs.DeleteKey(kPrefPrefix + i);
            }
        }
        PlayerPrefs.Save();
    }

    string[] pages = new string[0];
    int page = 0;
    string currentTitle = "";
    LibraryBook currentBook;
    bool flipping;

    // 书签列表面板(运行时动态构建)

    void Awake()
    {
        Instance = this;
        LoadBookmarks();
        if (panelRoot == null) panelRoot = gameObject;
        panelRT = panelRoot.transform as RectTransform;
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
        // 指外即关:重记基线(此刻扳机多半还按着)与保护期
        lastOpenTime = Time.unscaledTime;
        CollectMissWiring();
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
        SaveBookmarkToPrefs(bm);
        Debug.Log("[书签] 已保存:" + currentTitle + " 第" + (page + 1) + "页");
    }

    void UpdateBookmarkButton()
    {
        if (bookmarkBtn == null) return;
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
        PollMissClose();
    }

    // ------------------------------------------------ 指外即关

    void CollectMissWiring()
    {
        missCtrls.Clear(); missHeld.Clear(); missRays.Clear();
        foreach (var c in FindObjectsOfType<XRBaseController>())
        {
            missCtrls.Add(c);
            missHeld.Add(c.selectInteractionState.active);   // 预热基线:开书瞬间按住的扳机不算点击
        }
        foreach (var r in FindObjectsOfType<XRRayInteractor>())
            if (r != null && r.isActiveAndEnabled) missRays.Add(r);
    }

    void PollMissClose()
    {
        if (panelRoot == null || !panelRoot.activeSelf || missCtrls.Count == 0) return;
        for (int i = 0; i < missCtrls.Count; i++)
        {
            var c = missCtrls[i];
            bool held = c != null && c.selectInteractionState.active;
            bool pressed = held && !missHeld[i];
            missHeld[i] = held;
            if (!pressed) continue;
            if (Time.unscaledTime - lastOpenTime < openGrace) continue;   // 开书那一下不算

            var ray = FindRayFor(c);
            if (ray == null) continue;   // 该手没有射线交互器,不管
            if (ShouldCloseForRay(ray)) { Close(); return; }
        }
    }

    /// <summary>这条射线当前指着的落点上"点一下"是否意味着"关书":没打中任何东西,或打中的既不是阅读面板也不是另一本书。</summary>
    public bool ShouldCloseForRay(XRRayInteractor ray)
    {
        if (ray == null) return false;
        Vector3 pos; GameObject obj;
        if (!TryGetHit(ray, out pos, out obj)) return true;                              // 指着空处点 → 关
        if (obj != null && panelRT != null && obj.transform.IsChildOf(panelRT)) return false;   // 点在面板/面板按钮上
        if (panelRT != null && InPanelRect(pos)) return false;                            // 点在面板表面
        if (obj != null && obj.GetComponentInParent<LibraryBook>() != null) return false; // 点另一本书 → 让它换书,不关
        return true;
    }

    XRRayInteractor FindRayFor(XRBaseController c)
    {
        for (int i = 0; i < missRays.Count; i++)
        {
            var r = missRays[i];
            if (r != null && r.isActiveAndEnabled && r.xrController == c) return r;
        }
        return null;
    }

    /// <summary>当前帧这条射线打中了什么:UI 优先(面板按钮都是 UI),其次 3D 碰撞体,最后射线端点。</summary>
    bool TryGetHit(XRRayInteractor r, out Vector3 pos, out GameObject obj)
    {
        pos = default(Vector3); obj = null;
        RaycastResult ui;
        if (r.TryGetCurrentUIRaycastResult(out ui) && ui.gameObject != null)
        {
            pos = ui.worldPosition; obj = ui.gameObject; return true;
        }
        RaycastHit rh;
        if (r.TryGetCurrent3DRaycastHit(out rh) && rh.collider != null)
        {
            pos = rh.point; obj = rh.collider.gameObject; return true;
        }
        Vector3 nrm; int idx; bool valid;
        if (r.TryGetHitInfo(out pos, out nrm, out idx, out valid) && valid) return true;
        return false;
    }

    /// <summary>世界坐标是否落在阅读面板矩形内(按画布单位算,边缘留 missMargin 换算余量)。</summary>
    bool InPanelRect(Vector3 worldPos)
    {
        if (panelRT == null) return false;
        var local = panelRT.InverseTransformPoint(worldPos);
        var center = new Vector2((0.5f - panelRT.pivot.x) * panelRT.rect.width, (0.5f - panelRT.pivot.y) * panelRT.rect.height);
        float marginU = missMargin / Mathf.Max(1e-5f, Mathf.Abs(panelRT.lossyScale.x));
        return Mathf.Abs(local.x - center.x) <= panelRT.rect.width * 0.5f + marginU
            && Mathf.Abs(local.y - center.y) <= panelRT.rect.height * 0.5f + marginU
            && Mathf.Abs(local.z) <= 0.6f / Mathf.Max(1e-5f, Mathf.Abs(panelRT.lossyScale.z));
    }

    void PositionInFront()
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
