using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 智能图书馆 · 归还口
/// 挂在服务台归还感应点(带 XR Socket Interactor + Sphere Collider(Is Trigger))上。
/// 书籍(或任何物体)被 Socket 吸附时计数并更新状态屏文字。
/// </summary>
public class BookReturnSocket : MonoBehaviour
{
    [Tooltip("归还状态屏上的 Text(留空则只打印日志)")]
    public Text statusText;

    private XRSocketInteractor socket;
    private int returnedCount;

    void Start()
    {
        socket = GetComponent<XRSocketInteractor>();
        socket.selectEntered.AddListener(OnSelectEntered);
        socket.selectExited.AddListener(OnSelectExited);
        UpdateText("归还口:将书籍放入发光感应区即可归还");
    }

    void OnDestroy()
    {
        if (socket != null)
        {
            socket.selectEntered.RemoveListener(OnSelectEntered);
            socket.selectExited.RemoveListener(OnSelectExited);
        }
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        returnedCount++;
        var book = args.interactableObject != null
            ? args.interactableObject.transform.GetComponentInParent<LibraryBook>()
            : null;
        UpdateText(book != null
            ? $"已归还《{book.title}》\n累计归还 {returnedCount} 本"
            : $"已放入物品\n累计归还 {returnedCount} 本");
        Debug.Log($"[BookReturnSocket] 归还成功:{(book != null ? book.title : args.interactableObject.transform.name)},累计 {returnedCount} 本");
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        UpdateText($"归还口:累计归还 {returnedCount} 本,期待再次光临");
    }

    private void UpdateText(string content)
    {
        if (statusText != null) statusText.text = content;
    }
}
