using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 点击⑦:**让射线穿过书架,打到书上本来就有的碰撞体**(2026-09-28 按用户思路重做)。
///
/// 用户的原话点破了我前两版的错:"书上面本来就有碰撞体,射线碰到就该打开书;
/// 你去做的是空气墙,不是固定在每本书上的可点碰撞体"。
/// 前两版给每行书架在**外面**加了不可见的大碰撞体 —— 那东西不仅位置不对(挡住了走路),
/// 而且性质就错了(它不是书)。**全部拆掉,改成不动任何碰撞体、只动"射线看不看得到书架"**:
///
///   书架板子/立柱现在和书同在 Default(0)层,射线遮罩含 Default ⇒ 射线先打到书架板,打不到书。
///   把**书架结构**挪到 layer 2("Ignore Raycast")—— 射线交互器的 raycastMask 只含
///   Default(0)/UI(5)/31,**本来就不含 2** ⇒ 射线直接穿过书架,打在后面的**书自己的碰撞体**上。
///
/// 为什么这个做法是干净的:
///   · **没有新增任何碰撞体** ⇒ 不可能再有空气墙(物理碰撞矩阵一个字都没动);
///   · 书留在 Default 层 ⇒ 打到的永远是**那本书本身**(用户要的"精准到每一本书");
///   · 书架板子还在原层做物理遮挡 ⇒ 人照样穿不过书架(那是合理的墙);
///   · 纯运行期改层,不改场景,删脚本即回退。
///
/// 不动的:书(有 LibraryBook 的物体及其子物体)、UI(第 5 层)、XR 装置子树。
/// </summary>
public class LibraryShelfRayPass : MonoBehaviour
{
    const int ShelfLayer = 2;      // "Ignore Raycast":射线遮罩不含它
    const int UiLayer = 5;

    /// <summary>会挡在书前面的"书库结构"所在的分组。</summary>
    static readonly string[] Groups = { "Bookshelves", "Shelves2F", "Lecterns", "ReadingZone" };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[LibraryShelfRayPass]");
        DontDestroyOnLoad(go);
        go.AddComponent<LibraryShelfRayPass>();
    }

    void Start()
    {
        int moved = 0, keptBooks = 0, keptUi = 0;
        var perGroup = new List<string>();
        var roots = new List<Transform>();
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
            foreach (var g in Groups)
                if (t.name == g && t.parent != null && !roots.Contains(t)) roots.Add(t);

        var done = new HashSet<GameObject>();
        foreach (var root in roots)
        {
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                if (done.Contains(go)) continue;
                done.Add(go);

                if (go.GetComponentInParent<LibraryBook>() != null) { keptBooks++; continue; }   // 书:留在 Default
                if (go.layer == UiLayer) { keptUi++; continue; }                                 // UI:不动
                if (go.layer != ShelfLayer) { go.layer = ShelfLayer; n++; }
            }
            perGroup.Add(root.name + "=" + n);
            moved += n;
        }
        Debug.Log("[RayPass] 书架结构挪到 layer " + ShelfLayer + "(射线遮罩不含它 → 射线直接打到书):"
            + string.Join(" ", perGroup.ToArray())
            + " | 合计 " + moved + " 个;保留书 " + keptBooks + " 个、UI " + keptUi + " 个在原层");
    }
}