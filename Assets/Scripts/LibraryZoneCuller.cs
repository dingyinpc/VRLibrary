using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 性能⑤+⑥:按区域动态剔除 + 文字距离剔除。常驻自建物体,**不改场景**。
///
/// 实测依据(Logs/perf-static.txt / perf-viewpoints*.txt):
///   全场 2352 个渲染器里,**两个书架区就占了 1276 个**(二层 Shelves2F 740 + 一层 Bookshelves 536),
///   而玩家一次只可能处在 **一层 / 二层 / 露台** 三个地方之一。把"不在的那两片"整个关掉,
///   是唯一能动到数量级的优化 —— 这也就是用户说的"动态生成法",只是**不必真去生成**:
///   生成要拆预制体、重接 6 条引用、还要等加载;而**关渲染器是即时的、可逆的、零风险的**,
///   省下的 draw call 完全一样(渲染器不 enabled 就不提交)。
///
/// 为什么关的是 **Renderer.enabled 而不是 GameObject.SetActive**:
///   SetActive 会把脚本、碰撞体、`FindObjectOfType`、射线交互、传送锚点一起关掉 ——
///   冒烟测试第 1 条就是 `FindObjectOfType<TerraceCinema>()`,整片关掉它会直接挂;
///   HUD 里缓存的 cinema 引用、手感、传送目标也都会跟着出问题。
///   只关渲染器:画面上省得一模一样,逻辑侧**什么都没变**。
///
/// 手里正抓着的东西所在区域不会被关(XRGrabInteractable.isSelected),避免"拿着二层书传送走,书没了"。
///
/// 文字剔除(376 个 TextMesh)与区域剔除**同一遍算**,避免两个脚本互相把对方关掉的渲染器又打开:
///   可读距离 = clamp(文字世界高度 × 55, 4, 45) m —— 大牌子留得远,书名只有几米。
///
/// 冒烟测试会把 <see cref="ForceAll"/> 置 true:那时全场全开、一个字都不剔,测的是内容不是剔除。
/// 报告:Logs/zone-cull.txt。
/// </summary>
public class LibraryZoneCuller : MonoBehaviour
{
    const string ReportPath = "Logs/zone-cull.txt";
    const float Interval = 0.5f;           // 判定周期
    const float ReadablePerMeter = 55f;
    const float MinReadable = 4f, MaxReadable = 45f;
    const float TwoFloorY = 2.5f;          // 眼位高于这个高度就是在二层或露台
    const float TerracePad = 1.5f;         // 露台判定在 XZ 上外扩一点

    /// <summary>冒烟测试用:全场强制全开、不做任何剔除。</summary>
    public static bool ForceAll;
    /// <summary>是否在无烘焙遮挡或跨区时启用楼层整区剔除(可由外部关闭仅保留文字距离剔除)。</summary>
    public static bool EnableZoneCull = true;

    /// <summary>验收对照用:Logs/zone-cull-off.txt 存在时整脚本不剔除(仅编辑器)。</summary>
    bool off;
    bool lastForceAll;
    bool lastOff;
    Vector3 lastEvalPos = new Vector3(float.NaN, float.NaN, float.NaN);

    class Zone
    {
        public string name;
        public Transform root;
        public readonly List<Renderer> rends = new List<Renderer>();
        public readonly List<XRGrabInteractable> grabs = new List<XRGrabInteractable>();
        public Bounds xz;                  // 只用来判露台(root 的世界包围盒)
        public bool on = true;
    }

    class Text
    {
        public Renderer rend;
        public Transform tr;
        public XRGrabInteractable grab;
        public Zone zone;                  // null = 不属于任何可关区域(常开)
        public float readable;
    }

    readonly List<Zone> zones = new List<Zone>();
    readonly List<Text> texts = new List<Text>();
    Camera cam;
    float next;
    string report;
    Unity.XR.CoreUtils.XROrigin origin;   // 用 Rig 基点判楼层:眼高会被 Camera Offset 整体抬高,不能拿它判层

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[LibraryZoneCuller]");
        DontDestroyOnLoad(go);
        go.AddComponent<LibraryZoneCuller>();
    }

    void Start()
    {
        origin = FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
        // 三个区域根:名字写死会被场景改名搞坏,所以三个都找不到就整脚本退场(宁可不优化,不要乱关)
        AddZone("一层藏书区", "Bookshelves");
        AddZone("二层藏书区", "Shelves2F");
        AddZone("星空露台影院", "StarryTerrace");
        if (zones.Count == 0) { Debug.LogWarning("[ZoneCull] 一个区域根都没找到,整脚本退场"); enabled = false; return; }

        foreach (var t in FindObjectsOfType<TextMesh>(true))
        {
            var r = t.GetComponent<Renderer>();
            if (r == null) continue;
            float h = Mathf.Max(0.01f, r.bounds.size.y);
            texts.Add(new Text
            {
                rend = r,
                tr = t.transform,
                grab = t.GetComponentInParent<XRGrabInteractable>(),
                zone = ZoneOf(t.transform),
                readable = Mathf.Clamp(h * ReadablePerMeter, MinReadable, MaxReadable),
            });
        }
        Debug.Log("[ZoneCull] 接管 " + zones.Count + " 个区域 / " + texts.Count + " 个 TextMesh;"
            + "区域渲染器 " + CountZoneRends() + "(全场 2348)");
        Evaluate(true);
    }

    void AddZone(string label, string rootName)
    {
        var t = FindByName(rootName);
        if (t == null) { Debug.LogWarning("[ZoneCull] 找不到区域根 " + rootName + ",跳过"); return; }
        var z = new Zone { name = label, root = t };
        foreach (var r in t.GetComponentsInChildren<Renderer>(true)) z.rends.Add(r);
        foreach (var g in t.GetComponentsInChildren<XRGrabInteractable>(true)) z.grabs.Add(g);
        if (z.rends.Count > 0)
        {
            var b = z.rends[0].bounds;
            for (int i = 1; i < z.rends.Count; i++) b.Encapsulate(z.rends[i].bounds);
            z.xz = b;
        }
        zones.Add(z);
    }

    void Update()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + Interval;
#if UNITY_EDITOR
        off = File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs/zone-cull-off.txt"));
#endif
        if (cam == null) cam = Camera.main;
        bool stateChanged = (ForceAll != lastForceAll) || (off != lastOff) || float.IsNaN(lastEvalPos.x);
        if (!stateChanged && cam != null && (cam.transform.position - lastEvalPos).sqrMagnitude < 0.0625f)
            return; // 相机移动 < 0.25 m 且开关未变,跳过每 0.5s 全量遍历
        Evaluate(false);
    }

    void Evaluate(bool first)
    {
        if (zones.Count == 0) return;   // Start 尚未完成时 Update 直接跳过,别在 zones[0] 上越界刷异常(会触发 Error Pause 冻住整轮测试)
        if (cam == null) cam = Camera.main;
        if (cam == null) return;
        Vector3 p = cam.transform.position;
        lastEvalPos = p;
        lastForceAll = ForceAll;
        lastOff = off;
        // 判"在哪一层"用**脚底**高度 = Rig 基点(传送会把整台 Rig 挪到目标层),而不是眼高 ——
        // 眼高会被 Camera Offset / 追踪原点模式整体抬高(真机曾出现眼高 >2.5 ⇒ 站在一层被误判成
        // 二层 ⇒ 一层书整片被剔除:看不见但仍能点)。找不到 Rig 才退回眼高。
        float standY = origin != null ? origin.transform.position.y : p.y;

        // ---- 当前在哪个区域
        Zone here = zones[0];
        bool onTerrace = false;
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (z.name.StartsWith("星空露台"))
            {
                bool inside = p.x >= z.xz.min.x - TerracePad && p.x <= z.xz.max.x + TerracePad
                           && p.z >= z.xz.min.z - TerracePad && p.z <= z.xz.max.z + TerracePad;
                if (inside && standY > TwoFloorY - 1f) { here = z; onTerrace = true; }
            }
            else if (z.name.StartsWith("二层") && !onTerrace && standY > TwoFloorY) here = z;
            else if (z.name.StartsWith("一层") && !onTerrace && standY <= TwoFloorY) here = z;
        }

        bool changed = first;
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            if (ForceAll || off || !EnableZoneCull) { if (!z.on) { z.on = true; changed = true; } continue; }
            bool want = (z == here);
            if (want == z.on) continue;
            // 手里还抓着一件这片区域的东西 → 这一拍先不关(下一拍再看)
            if (!want && HeldIn(z)) continue;
            z.on = want; changed = true;
        }

        // ---- 渲染器开合 + 文字距离剔除(同一遍,谁也别覆盖谁)
        int textOn = 0;
        if (changed)
            foreach (var z in zones) foreach (var r in z.rends) if (r != null && r.enabled != z.on) r.enabled = z.on;
        for (int i = 0; i < texts.Count; i++)
        {
            var t = texts[i];
            if (t.rend == null) continue;
            bool show;
            if (ForceAll || off) show = true;
            else if (t.zone != null && !t.zone.on) show = false;
            else if (t.grab != null && t.grab.isSelected) show = true;
            else show = (t.tr.position - p).sqrMagnitude <= t.readable * t.readable;
            if (t.rend.enabled != show) t.rend.enabled = show;
            if (show) textOn++;
        }

        if (changed || first)
        {
            string line = "当前区域 " + here.name + "  脚底y " + standY.ToString("0.00") + " 眼高 " + p.y.ToString("0.00")
                + "  区域开合 " + States()
                + "  文字可见 " + textOn + "/" + texts.Count
                + (ForceAll ? "  **ForceAll(冒烟模式:全开)**" : "");
            Debug.Log("[ZoneCull] " + line);
            report = line;
            Dump(p, here);
        }
    }

    bool HeldIn(Zone z)
    {
        for (int i = 0; i < z.grabs.Count; i++)
            if (z.grabs[i] != null && z.grabs[i].isSelected) return true;
        return false;
    }

    string States()
    {
        var sb = new StringBuilder();
        foreach (var z in zones) sb.Append(z.name).Append(z.on ? "=开 " : "=关 ");
        return sb.ToString().TrimEnd();
    }

    int CountZoneRends() { int n = 0; foreach (var z in zones) n += z.rends.Count; return n; }

    Zone ZoneOf(Transform t)
    {
        foreach (var z in zones)
            if (t == z.root || t.IsChildOf(z.root)) return z;
        return null;
    }

    static Transform FindByName(string name)
    {
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    void Dump(Vector3 p, Zone here)
    {
#if UNITY_EDITOR
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("== 性能⑤⑥ 区域剔除 + 文字剔除 ==");
            sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("眼位 " + p.ToString("0.00") + "  当前区域 " + here.name + (ForceAll ? "  (ForceAll)" : ""));
            sb.AppendLine("\n---- 区域账 ----");
            foreach (var z in zones)
                sb.AppendLine("  " + z.name.PadRight(16) + " 渲染器 " + z.rends.Count.ToString().PadLeft(5)
                    + "  可抓件 " + z.grabs.Count.ToString().PadLeft(4) + "  " + (z.on ? "开" : "关")
                    + "  根 " + z.root.name);
            sb.AppendLine("  三区合计 " + CountZoneRends() + " 个渲染器(全场 2348)");
            sb.AppendLine("\n---- 判定规则 ----");
            sb.AppendLine("  露台:XZ 落在 StarryTerrace 包围盒外扩 " + TerracePad + " m 内且眼位 > 1.5 m");
            sb.AppendLine("  二层:眼位 y > " + TwoFloorY + " m;一层:其余");
            sb.AppendLine("  文字:可读距离 = clamp(世界高度 × " + ReadablePerMeter + ", " + MinReadable + ", " + MaxReadable + ") m,手里抓着的不剔");
            Directory.CreateDirectory("Logs");
            File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (System.Exception e) { Debug.LogWarning("[ZoneCull] 报告失败:" + e.Message); }
#endif
    }
}