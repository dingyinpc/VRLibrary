using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 智能图书馆 · 书籍组件(**点击阅读版**,2026-09-28 用户定案)。
///
/// 改动理由(性能账见知识库/性能优化总账.md):
///   旧版是"可抓取"——要抓就必须挂 Rigidbody,有刚体就会被判成"会动的东西",
///   于是**整片书永远进不了静态合批**,只能一本书一个 draw call。而书架两区 1276 个渲染器里
///   大部分就是书 —— 那是"批次拥挤"里最大的一块。
///   用户定案:**书只需要点击就能看,不需要每本都能单独抓**。
///   ⇒ 去掉 Rigidbody 与 XRGrabInteractable,改成 XRSimpleInteractable(点一下就开阅读面板),
///     书从此全程不动 → 可以整片 BatchingStatic。
///
/// 两点必须一起做的配套:
///   ① **去掉了原来的悬停放大**(localScale 一变,静态合批烘死的变换就对不上,缩放会失效);
///      悬停反馈改由 HUD 的射线提示负责(HudHoverTip,冒烟里有 "HUD 悬停提示" 这一项)。
///   ② 保留"点空白/射线"老流程:点击 → 先让馆藏信息面板显示,再打开阅读面板。
///
/// 兼容性:事件从 XRBaseInteractable 取 —— XRSimpleInteractable(现在)与 XRGrabInteractable(旧场景)
/// 都派生自它,所以老场景里即使没换组件也不会报错。
/// </summary>
public class LibraryBook : MonoBehaviour
{
    [Header("书籍信息(等待馆藏数据录入)")]
    public string title = "未命名图书";
    public string author = "佚名";
    [TextArea] public string summary = "详情待录入:馆藏数据后续由使用者提供。";
    [Tooltip("书架槽位编号,如 A1-2-05(区-架-层-槽)")]
    public string slotCode = "";

    private XRBaseInteractable interactable;

    void OnEnable()
    {
        interactable = GetComponent<XRBaseInteractable>();
        if (interactable == null)
        {
            Debug.LogWarning("[LibraryBook] 缺少可交互组件(需要 XRSimpleInteractable),跳过事件绑定:" + gameObject.name);
            return;
        }
        interactable.selectEntered.AddListener(OnSelectEntered);
    }

    void OnDisable()
    {
        if (interactable == null) return;
        interactable.selectEntered.RemoveListener(OnSelectEntered);
    }

    /// <summary>点一下 = 看这本书。</summary>
    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (LibraryUIController.Instance != null)
            LibraryUIController.Instance.ShowBookInfo(this);
        OpenReader();
    }

    /// <summary>打开阅读面板。兜底:面板未激活时 Instance 为空,从场景中找回(含未激活对象)。</summary>
    public void OpenReader()
    {
        var reader = BookReader.Instance;
        if (reader == null)
        {
            var found = FindObjectOfType<BookReader>(true);
            if (found == null) { Debug.LogWarning("[LibraryBook] 场景中没有 BookReader 组件,无法打开阅读面板"); return; }
            BookReader.Instance = found;   // 公开静态字段,直接接管
        }
        reader.Open(this);
    }
}