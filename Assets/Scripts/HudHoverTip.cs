using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 图标/按钮上的一行小白字提示:手柄射线指到它时显、移开就收。
/// 编辑期把 tip 指到那块文字(它自己默认 SetActive(false))。
/// </summary>
[RequireComponent(typeof(Button))]
public class HudHoverTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public GameObject tip;

    void Awake()
    {
        if (tip != null) tip.SetActive(false);
    }

    public void OnPointerEnter(PointerEventData e)
    {
        if (tip != null) tip.SetActive(true);
    }

    public void OnPointerExit(PointerEventData e)
    {
        if (tip != null) tip.SetActive(false);
    }
}
