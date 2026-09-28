using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 点击⑥:给每本书在**朝外一侧**补一块"点击板",让射线一定打得到。
///
/// 为什么要这个(2026-09-28 实测):
///   把书改成"点击即读"之后,用户反馈**点不开**。冒烟测试新增的可达性抽样给出了原因:
///   逐本"站到书正前方 1.2m"打射线,**262 本里只有 20 本直达**,其余第一阻挡是书架自己的板子
///   (`被 Top@1.00m 挡住`)。原因很直白 —— **抓取走的是手碰碰撞体(overlap),根本不需要射线**,
///   所以"以前能抓"完全不代表"现在射线打得到";书是嵌在架子里的,rapped 前沿/挡板先挡住射线。
///
/// 做法(纯运行期,不改场景,删掉脚本即回退):
///   ① 按"同一父节点"把书分行分组;
///   ② 每组的**朝外方向** = 该组书心到组质心的水平方向(不用书自己的 forward —— 实测书的朝向不可靠);
///   ③ 每本书在 `书心 + 朝外 × (半厚 + 2cm)` 处挂一块薄板(BoxCollider,非 trigger),
///      板子朝外的法线对准该方向,尺寸取书的正投影(略小一圈,避免相邻书互相压住);
///   ④ 板上挂 XRSimpleInteractable + 本组件:`selectEntered` 时用射线命中点找**最近的那本书**并打开它。
///      拿不到命中点(比如 poke)就退化为"打开这一格的书"。
///
/// 板子放在 Layer 0(Default):射线交互器的 raycastMask 含 Default ✓,而传送射线的层是 31,
/// 所以板子**不会挡住传送**。
/// </summary>
public class LibraryShelfClick : MonoBehaviour
{
    const float PlateDepth = 0.04f;     // 板厚
    const float OutwardGap = 0.02f;     // 板与书面的间隙
    const string PlateName = "ClickPlate";

    /// <summary>每本书的"朝外方向"(由行质心算得)。冒烟测试要按**真实过道方向**验可达性,
    /// 而不是沿书自己的 forward —— 实测这两者不重合,照 forward 测会误报"被挡住"。</summary>
    public static readonly Dictionary<LibraryBook, Vector3> Outward = new Dictionary<LibraryBook, Vector3>();

    /// <summary>已经补上点击板的书。</summary>
    public static readonly HashSet<LibraryBook> Covered = new HashSet<LibraryBook>();

    /// <summary>板上挂的这个小组件:选中时打开最近的书。</summary>
    public class Plate : MonoBehaviour
    {
        public LibraryShelfClick owner;
        public List<LibraryBook> row;
        public Vector3 rowCenter;
        public Vector3 outward;

        void OnEnable()
        {
            var it = GetComponent<XRBaseInteractable>();
            if (it != null) it.selectEntered.AddListener(OnSelect);
        }
        void OnDisable()
        {
            var it = GetComponent<XRBaseInteractable>();
            if (it != null) it.selectEntered.RemoveListener(OnSelect);
        }

        /// <summary>这块板覆盖的一行书里,离某个点最近的那本。</summary>
        public LibraryBook Nearest(Vector3 point)
        {
            LibraryBook best = null; float bestD = float.MaxValue;
            foreach (var b in row)
            {
                if (b == null) continue;
                float d = Vector3.Distance(b.transform.position, point);
                if (d < bestD) { bestD = d; best = b; }
            }
            return best;
        }

        void OnSelect(SelectEnterEventArgs args)
        {
            Vector3 hit = rowCenter; bool hasHit = false;
            var ray = args.interactorObject as XRRayInteractor;
            RaycastHit rh;
            if (ray != null && ray.TryGetCurrent3DRaycastHit(out rh) && rh.collider != null && rh.collider.gameObject == gameObject)
            { hit = rh.point; hasHit = true; }
            var best = Nearest(hasHit ? hit : rowCenter);
            if (best == null) return;
            if (LibraryUIController.Instance != null) LibraryUIController.Instance.ShowBookInfo(best);
            best.OpenReader();
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[LibraryShelfClick]");
        DontDestroyOnLoad(go);
        go.AddComponent<LibraryShelfClick>();
    }

    void Start()
    {
        var books = FindObjectsOfType<LibraryBook>(true);
        Outward.Clear(); Covered.Clear();
        // 按父节点分行
        var rows = new Dictionary<Transform, List<LibraryBook>>();
        foreach (var b in books)
        {
            if (b.GetComponent<Collider>() == null) continue;
            var key = b.transform.parent != null ? b.transform.parent : b.transform;
            List<LibraryBook> list;
            if (!rows.TryGetValue(key, out list)) { list = new List<LibraryBook>(); rows[key] = list; }
            list.Add(b);
        }

        int plates = 0, skipped = 0;
        foreach (var kv in rows)
        {
            var row = kv.Value;
            if (row.Count == 0) continue;

            // ⚠ 第一版按"书心到行质心的方向"贴板,实测不可靠:书是**二维网格**排布的,
            // 边缘那几本的"朝外方向"会指向隔壁的书(反例:被 Book_2_0_7 挡住)。
            // 改成不猜方向:取**整行(含书架板)的包围盒**,在 4 个侧面各贴一块整行大的板 ——
            // 板必然在书架体积之外,过道里任何方向都能打到。
            var covers = new List<Renderer>();
            var parent = kv.Key;
            foreach (var r in parent.GetComponentsInChildren<Renderer>(true)) covers.Add(r);
            foreach (var b in row) { var r = b.GetComponent<Renderer>(); if (r != null && !covers.Contains(r)) covers.Add(r); }
            if (covers.Count == 0) { skipped += row.Count; continue; }
            var box = covers[0].bounds;
            for (int i = 1; i < covers.Count; i++) box.Encapsulate(covers[i].bounds);

            Vector3 center = box.center;
            // 4 个水平侧面:X 两侧、Z 两侧
            var sides = new[]
            {
                new { dir = Vector3.right,   ext = box.extents.x, w = box.size.z },
                new { dir = Vector3.left,    ext = box.extents.x, w = box.size.z },
                new { dir = Vector3.forward, ext = box.extents.z, w = box.size.x },
                new { dir = Vector3.back,    ext = box.extents.z, w = box.size.x },
            };
            int rowPlates = 0;
            foreach (var s in sides)
            {
                var plate = new GameObject(PlateName);
                plate.layer = row[0].gameObject.layer;
                plate.transform.SetParent(parent, true);
                plate.transform.SetPositionAndRotation(center + s.dir * (s.ext + 0.05f),
                    Quaternion.LookRotation(s.dir, Vector3.up));
                var col = plate.AddComponent<BoxCollider>();
                col.size = new Vector3(s.w * 1.02f, box.size.y * 1.02f, PlateDepth);
                plate.AddComponent<XRSimpleInteractable>();
                var p = plate.AddComponent<Plate>();
                p.owner = this; p.row = row; p.rowCenter = center; p.outward = s.dir;
                plates++; rowPlates++;
            }
            foreach (var b in row) { Outward[b] = Vector3.zero; Covered.Add(b); }
        }
        Debug.Log("[ShelfClick] 书 " + books.Length + " 本 / " + rows.Count + " 行 → 补点击板 " + plates
            + " 块(每行 4 个侧面),跳过 " + skipped + " 本");
    }
}