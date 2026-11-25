#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 露台 / 二层「世界空间面板标高」体检与修正(2026-10-05)。
///
/// **为什么会有这个工具**:v7.35 的整馆拔高(一层净高 8.00 / 二层 3.60,二层组 +1.423)实现方式是
/// "给带 Renderer 的对象把 localPosition.y 加 1.423"。于是**纯空物体、UI Canvas**(Image/Text 只有
/// CanvasRenderer,拿不到 Renderer)和手摆件全都没动;v7.37 只补抬了 `CinemaSeat` / `TerraceGoMezz`
/// 两个锚点。结果露台地面已到 4.423、座位 4.468、玩家眼高 6.068,而观影面板仍在 4.395~4.645
/// —— 比眼高低 1.42 m(约在小腿高度),`TerraceSign` 更是落到 4.350,**埋进二层楼板里**。
/// 2026-10-19 起离座确认面板已删除,场上只剩目录/播控/须知三块。
/// 玩家看到的症状就是"传送到露台后面板飘得很远、人显得特别高"。
///
/// **判据不写死数字,全部从场上活着的基准对象推**:
///   · `CinemaController` = `Pad_Terrace.y`(它就是面板的父节点,建场时 = seatPos)
///   · `TerracePanel` = `Pad_Terrace.y + 1.38`(TerraceSetup.BuildPanel 原式)
///   · `FilmInfoCard` = `VideoSurface.y`(信息卡与屏面同高、贴屏面)
///   · `TerraceSign` = 二层板面顶 + 1.35(板面顶 = `Pad_2F_*` 锚点 4.468 − 0.045)
///
/// 两个菜单:**体检只读**(先看明白再改)/ **修正**(幂等:只补差额,快照 + 自证 + 保存)。
/// 报告 `Logs/terrace-fix.txt`。
/// </summary>
public static class VRLibraryTerraceFix
{
    const string kScene = "Assets/_Scenes/SmartLibrary.unity";
    const string kLog = "Logs/terrace-fix.txt";
    const string kSnapshot = "Logs/SmartLibrary.pre-terracedy.unity";

    /// <summary>垫面比板面顶高出的量(一层/二层/露台三个 Pad 都是这个数,实测 4.468 vs 板面 4.423)。</summary>
    const float PadAboveDeck = 0.045f;

    class Item
    {
        public string label;        // 报告里显示的名字
        public Transform t;         // 被查/被改的对象
        public float expect;        // 期望世界 y
        public string basis;        // 期望值是怎么来的(写进报告,便于复核)
    }

    [MenuItem("VRLibrary/露台标高·体检(只报告)")]
    public static void Audit() { Run(false); }

    [MenuItem("VRLibrary/露台标高·修正(改场景)")]
    public static void Fix() { Run(true); }

    static void Run(bool apply)
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != kScene)
        {
            Debug.LogError("[露台标高] 当前场景不是 SmartLibrary:" + scene.path);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine((apply ? "== 露台标高·修正(改场景) ==" : "== 露台标高·体检(只读) ==")
            + "  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        // ── 基准对象:缺一个就不动场景(宁可不改,也不要拿错基准去改)
        var padT = FindT("SmartLibrary/StarryTerrace/Pad_Terrace");
        var pad2F = FindT("SmartLibrary/Pad_2F_N_E");
        var vsurf = FindT("SmartLibrary/StarryTerrace/TerraceScreenGroup/VideoSurface");
        var missing = "";
        if (padT == null) missing += " Pad_Terrace";
        if (pad2F == null) missing += " Pad_2F_N_E";
        if (vsurf == null) missing += " VideoSurface";
        if (missing.Length > 0)
        {
            sb.AppendLine("  !! 基准对象找不到:" + missing + " → 不体检也不改(名字变了?先跑性能·露台观影子树账看清单)");
            Flush(sb);
            Debug.LogWarning("[露台标高] 基准缺失:" + missing);
            return;
        }

        float deckTop2F = pad2F.position.y - PadAboveDeck;
        sb.AppendLine(string.Format("  基准:露台垫面 y={0:0.000} / 二层板面顶 y={1:0.000} / 屏面 y={2:0.000}",
            padT.position.y, deckTop2F, vsurf.position.y));
        sb.AppendLine();

        var items = new Item[]
        {
            new Item { label = "CinemaController(三块面板的父节点)",
                       t = FindT("SmartLibrary/StarryTerrace/CinemaController"),
                       expect = padT.position.y, basis = "= 露台垫面 y" },
            new Item { label = "TerracePanel(露台回程面板)",
                       t = FindT("SmartLibrary/StarryTerrace/TerracePanel"),
                       expect = padT.position.y + 1.38f, basis = "= 露台垫面 + 1.38(BuildPanel 原式)" },
            new Item { label = "FilmInfoCard(影片信息卡)",
                       t = FindT("SmartLibrary/StarryTerrace/TerraceScreenGroup/FilmInfoCard"),
                       expect = vsurf.position.y, basis = "= 屏面 VideoSurface y(与屏同高)" },
            new Item { label = "TerraceSign(二层楼梯口指示牌)",
                       t = FindT("SmartLibrary/Mezzanine/TerraceSign"),
                       expect = deckTop2F + 1.35f, basis = "= 二层板面顶 + 1.35(BuildCorridorSign 原式)" },
        };

        if (!apply)
        {
            int bad = 0;
            foreach (var it in items)
            {
                if (it.t == null) { sb.AppendLine("  !! " + it.label + " 不在场上"); bad++; continue; }
                float cur = it.t.position.y;
                float d = cur - it.expect;
                bool ok = Mathf.Abs(d) < 0.02f;
                if (!ok) bad++;
                sb.AppendLine(string.Format("  {0,-38} 现 y={1,8:0.000}  应 y={2,8:0.000}  Δ={3,+0.000;-0.000;0.000}  {4}   [{5}]",
                    it.label, cur, it.expect, d, ok ? "✓" : "!! 偏低/偏高", it.basis));
            }
            sb.AppendLine();
            sb.AppendLine("  未过 " + bad + " 项" + (bad > 0 ? " → 跑「VRLibrary/露台标高·修正(改场景)」" : ""));
            Flush(sb);
            Debug.Log("[露台标高] 体检完成 → " + kLog + "(未过 " + bad + " 项)");
            return;
        }

        // ── 修正:先快照,再逐个补差额,最后自证
        if (!Snapshot()) return;

        int changed = 0;
        sb.AppendLine("  [修正]");
        foreach (var it in items)
        {
            if (it.t == null) { sb.AppendLine("      !! " + it.label + " 不在场上,跳过"); continue; }
            var p = it.t.position;
            float d = it.expect - p.y;
            if (Mathf.Abs(d) < 0.0005f)
            {
                sb.AppendLine(string.Format("      {0,-38} 已在 {1:0.000},不动", it.label, p.y));
                continue;
            }
            Undo.RecordObject(it.t, "修正露台面板标高");
            // 用 position 写:UI(RectTransform)会把结果落到 m_AnchoredPosition,这正是磁盘上的真相(坑 23/46)
            it.t.position = new Vector3(p.x, it.expect, p.z);
            EditorUtility.SetDirty(it.t);
            changed++;
            sb.AppendLine(string.Format("      {0,-38} {1:0.000} → {2:0.000}(Δ{3:+0.000;-0.000})", it.label, p.y, it.expect, d));
        }
        Physics.SyncTransforms();

        sb.AppendLine();
        sb.AppendLine("  [自证:改完再量一遍]");
        int still = 0;
        foreach (var it in items)
        {
            if (it.t == null) continue;
            float cur = it.t.position.y;
            bool ok = Mathf.Abs(cur - it.expect) < 0.02f;
            if (!ok) still++;
            sb.AppendLine(string.Format("      {0,-38} y={1,8:0.000}  应 {2,8:0.000}  {3}",
                it.label, cur, it.expect, ok ? "✓" : "!! 还是不对"));
        }
        // 面板必须落在"眼高带"里,才算真的修好(座位眼高 = seat.y + 1.60)
        var seat = FindT("SmartLibrary/StarryTerrace/CinemaSeat");
        if (seat != null)
        {
            float eye = seat.position.y + 1.60f;
            foreach (var nm in new[] { "CatalogPanel", "TransportPanel", "NoticePanel" })
            {
                var pt = FindT("SmartLibrary/StarryTerrace/CinemaController/" + nm);
                if (pt == null) { sb.AppendLine("      !! 找不到面板 " + nm); still++; continue; }
                float d = pt.position.y - eye;
                bool ok = d > -0.60f && d < 0.40f;      // 面板中心应落在眼位附近
                if (!ok) still++;
                sb.AppendLine(string.Format("      {0,-38} y={1,8:0.000}  与眼位差 {2,+0.000;-0.000} m  {3}",
                    nm, pt.position.y, d, ok ? "✓" : "!! 不在眼高带"));
            }
            // 目录必须沿"座位→银幕"方向摆在正前方(2026-10-19 定案),不跟随头部朝向
            var cat = FindT("SmartLibrary/StarryTerrace/CinemaController/CatalogPanel");
            if (cat != null)
            {
                var f = seat.forward; f.y = 0f; f.Normalize();
                var to = cat.position - seat.position; to.y = 0f;
                float along = Vector3.Dot(to, f);
                float lateral = Vector3.Dot(to, seat.right);
                bool ok = along > 0.5f && Mathf.Abs(lateral) < 0.05f;
                if (!ok) still++;
                sb.AppendLine(string.Format("      {0,-38} 沿前 {1:0.00} m / 横向 {2:+0.00;-0.00;0.00} m  {3}",
                    "目录·座位→银幕方向", along, lateral, ok ? "✓ 正前方" : "!! 没摆正"));
            }
        }

        sb.AppendLine();
        sb.AppendLine(string.Format("  改了 {0} 件;自证未过 {1} 项", changed, still));
        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        sb.AppendLine("  保存场景:" + (saved ? "成功" : "失败") + "  isDirty(应为 False)=" + scene.isDirty);
        sb.AppendLine("  回退:把 " + kSnapshot + " 复制回 " + kScene + " 后重开场景");
        Flush(sb);
        Debug.Log("[露台标高] 修正完成:改 " + changed + " 件,自证未过 " + still + " 项 → " + kLog);
    }

    static bool Snapshot()
    {
        try
        {
            Directory.CreateDirectory("Logs");
            if (!File.Exists(kSnapshot))
            {
                File.Copy(kScene, kSnapshot);
                Debug.Log("[露台标高] 快照已存 " + kSnapshot);
            }
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError("[露台标高] 快照失败,先不动场景:" + e.Message);
            return false;
        }
    }

    /// <summary>按路径找 Transform;找不到再按名字在整场搜一遍(名字在就是路径变了)。</summary>
    static Transform FindT(string path)
    {
        var go = GameObject.Find(path);
        if (go != null) return go.transform;
        string leaf = path.Substring(path.LastIndexOf('/') + 1);
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
            if (t.name == leaf) return t;
        return null;
    }

    static void Flush(StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(kLog, sb.ToString(), new UTF8Encoding(false));
        }
        catch (System.Exception e) { Debug.LogWarning("[露台标高] 报告落盘失败:" + e.Message); }
    }
}
#endif
