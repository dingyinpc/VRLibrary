using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 智能图书馆 · 书籍组件
/// 挂在书本(占位/真实模型均可)上,需同物体带 XR Grab Interactable。
/// - 悬停时轻微放大提示可交互;
/// - 抓取(选中)时把书籍信息推送给 LibraryUIController 显示。
/// 后续提供真实馆藏数据后,直接在 Inspector 填写 title / author / summary 即可。
/// </summary>
public class LibraryBook : MonoBehaviour
{
    [Header("书籍信息(等待馆藏数据录入)")]
    public string title = "未命名图书";
    public string author = "佚名";
    [TextArea] public string summary = "详情待录入:馆藏数据后续由使用者提供。";

    private XRGrabInteractable grab;
    private Vector3 baseScale;

    void Awake()
    {
        grab = GetComponent<XRGrabInteractable>();
        baseScale = transform.localScale;
    }

    void OnEnable()
    {
        if (grab == null) grab = GetComponent<XRGrabInteractable>();
        grab.hoverEntered.AddListener(OnHoverEntered);
        grab.hoverExited.AddListener(OnHoverExited);
        grab.selectEntered.AddListener(OnSelectEntered);
        grab.activated.AddListener(OnActivated);
    }

    void OnDisable()
    {
        grab.hoverEntered.RemoveListener(OnHoverEntered);
        grab.hoverExited.RemoveListener(OnHoverExited);
        grab.selectEntered.RemoveListener(OnSelectEntered);
        grab.activated.RemoveListener(OnActivated);
    }

    private void OnHoverEntered(HoverEnterEventArgs args)
    {
        transform.localScale = baseScale * 1.15f;
    }

    private void OnHoverExited(HoverExitEventArgs args)
    {
        transform.localScale = baseScale;
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (LibraryUIController.Instance != null)
            LibraryUIController.Instance.ShowBookInfo(this);
    }

    /// <summary>抓取状态下扣扳机 = 打开阅读面板。</summary>
    private void OnActivated(ActivateEventArgs args)
    {
        if (BookReader.Instance != null)
            BookReader.Instance.Open(this);
    }
}
