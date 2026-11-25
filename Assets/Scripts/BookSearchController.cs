using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.CoreUtils;

/// <summary>
/// 智能图书馆 · 打字检索 + 传送到书前(对象池版)
/// 结果条目在编辑期预建(运行时 AddComponent&lt;Text&gt; 会因内置字体问题 NRE,故禁用)。
/// 面板结构(编辑期建好):SearchPanel → Input / BtnSearch / BtnClose / Items(含 Result_0..5)。
/// </summary>
public class BookSearchController : MonoBehaviour
{
    public const string Ver = "v4c-pool";

    public static BookSearchController Instance;

    public InputField inputField;
    public GameObject panelRoot;
    public GameObject[] resultPool;   // 预建的 6 条结果条目(激活/停用复用)
    public XROrigin origin;
    [Tooltip("打开检索时临时隐藏的画布(如导览屏),避免视觉重叠")]
    public GameObject canvasToHide;

    int usedResults;
    LibraryBook[] cachedBooks;
    GameObject activeMarker;
    Material markerMat;

    void Awake()
    {
        Instance = this;
        if (panelRoot == null) panelRoot = gameObject;
        panelRoot.SetActive(false);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (activeMarker != null) Destroy(activeMarker);
        if (markerMat != null) Destroy(markerMat);
    }

    public void Show()
    {
        panelRoot.SetActive(true);
        if (canvasToHide != null) canvasToHide.SetActive(false);
        if (inputField != null) inputField.text = "";
        ClearResults();
    }
    public void Hide()
    {
        panelRoot.SetActive(false);
        if (canvasToHide != null) canvasToHide.SetActive(true);
    }

    /// <summary>搜索按钮调用。</summary>
    public void DoSearch()
    {
        string q = inputField != null ? inputField.text.Trim() : "";
        if (q.Length == 0 || resultPool == null || resultPool.Length == 0) return;
        q = q.ToLower();
        if (cachedBooks == null || cachedBooks.Length == 0)
            cachedBooks = FindObjectsOfType<LibraryBook>(true);
        var hits = new List<LibraryBook>();
        foreach (var b in cachedBooks)
        {
            if (b == null || string.IsNullOrEmpty(b.title)) continue;
            if (b.title.ToLower().Contains(q) || (b.author != null && b.author.ToLower().Contains(q))
                || (!string.IsNullOrEmpty(b.slotCode) && b.slotCode.ToLower().Contains(q)))
            {
                hits.Add(b);
                if (hits.Count >= resultPool.Length) break;
            }
        }
        ShowResults(hits);
        Debug.Log("[检索] \"" + q + "\" 命中 " + hits.Count + " 本");
    }

    void ShowResults(List<LibraryBook> hits)
    {
        ClearResults();
        if (hits.Count == 0) { FillItem("未找到\"" + inputField.text + "\"", null); return; }
        foreach (var b in hits) FillItem("《" + b.title + "》" + (string.IsNullOrEmpty(b.slotCode) ? "" : " [" + b.slotCode + "]"), b);
    }

    void ClearResults()
    {
        if (resultPool == null) return;
        foreach (var go in resultPool)
        {
            if (go == null) continue;
            go.SetActive(false);
            var btn = go.GetComponent<Button>();
            if (btn != null) btn.onClick.RemoveAllListeners();
        }
        usedResults = 0;
    }

    void FillItem(string label, LibraryBook book)
    {
        if (resultPool == null || usedResults >= resultPool.Length) return;
        var go = resultPool[usedResults++];
        go.SetActive(true);
        var t = go.GetComponentInChildren<Text>(true);
        if (t != null) t.text = label;
        var btn = go.GetComponent<Button>();
        if (btn != null && book != null)
        {
            var captured = book;
            btn.onClick.AddListener(delegate { GoToBook(captured); });
        }
    }

    /// <summary>传送到书前 + 发光标记。</summary>
    void GoToBook(LibraryBook book)
    {
        Hide();
        if (origin == null || book == null) return;

        var shelfT = book.transform.parent != null ? book.transform.parent : book.transform;
        Vector3 fwd = shelfT.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 destPos = book.transform.position + fwd * 1.15f;
        // 二层书:落到回廊地面(v7.35 拔高后二层板面顶 y=4.43,与各传送锚点 4.47 同代);一层书:落到一层地面 y=0
        destPos.y = book.transform.position.y > 2.5f ? 4.43f : 0f;

        Vector3 toBook = book.transform.position - destPos; toBook.y = 0f;
        float yaw = toBook.sqrMagnitude > 0.001f ? Mathf.Atan2(toBook.x, toBook.z) * Mathf.Rad2Deg : 0f;

        var cam = origin.Camera != null ? origin.Camera.transform : null;
        if (cam != null)
        {
            float delta = Mathf.DeltaAngle(cam.eulerAngles.y, yaw);
            origin.transform.Rotate(0f, delta, 0f);
            Vector3 camOff = cam.position - origin.transform.position; camOff.y = 0f;
            origin.transform.position = new Vector3(destPos.x - camOff.x, destPos.y, destPos.z - camOff.z);
            Physics.SyncTransforms();
        }

        if (activeMarker != null) Destroy(activeMarker);
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "SearchMarker";
        var col = marker.GetComponent<Collider>();
        if (col != null) { col.enabled = false; Destroy(col); }
        marker.transform.position = book.transform.position + Vector3.up * 0.55f;
        marker.transform.localScale = Vector3.one * 0.14f;
        var mr = marker.GetComponent<MeshRenderer>();
        if (markerMat == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader != null)
            {
                markerMat = new Material(shader);
                markerMat.SetColor("_BaseColor", Color.black);
                markerMat.EnableKeyword("_EMISSION");
                // 极光蓝 rgb(120,199,210) 按原发光量放大;改了要同步 VRLibrary/灯色 那套目标色
                markerMat.SetColor("_EmissionColor", new Color(1.27f, 2.10f, 2.22f));
            }
        }
        if (mr != null && markerMat != null) mr.sharedMaterial = markerMat;
        activeMarker = marker;
        Destroy(marker, 10f);

        Debug.Log("[检索] 已传送到《" + book.title + "》面前");
    }
}
