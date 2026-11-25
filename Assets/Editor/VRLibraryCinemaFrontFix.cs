#if UNITY_EDITOR
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 2026-10-19 用户定案(星空观影):
///   ① **强制限制自由走动** —— 删除「要离座吗? 留下继续看 / 离座走动」整条确认流程;
///   ② 删除播控面板上的「退出观影」—— 观影态没有就地解锁入口,唯一出口是「回一层大厅」;
///   ③ 节目目录(自由切换视频的目录)必须摆在**人物正前方**,但"前"以**座位→银幕方向**为准,
///      不跟随头部朝向:目录中心落在座位 forward 轴上,横向偏移归零。
///
/// 本工具改的是**现有场景**;幂等,重复跑结果相同。以后重跑「搭建星空露台」也会得到同样布局。
/// 报告 `Logs/cinema-front-fix.txt`,快照 `Logs/SmartLibrary.pre-cinemafront.unity`。
/// </summary>
public static class VRLibraryCinemaFrontFix
{
    const string kScene = "Assets/_Scenes/SmartLibrary.unity";
    const string kLog = "Logs/cinema-front-fix.txt";
    const string kSnapshot = "Logs/SmartLibrary.pre-cinemafront.unity";
    const float kCatalogForward = 1.08f;   // 与旧"左前方 0.95/0.52"的斜距相同,保持 40° 高 / 35° 宽
    const string kCatalogHint = "选一片开始播放\n右手 Grip 按一下呼出/收起本面板";
    const string kNoticeBody =
        "一、进入观影后身体位置会被锁定，无法走动；转头即可环视；\n"
        + "二、观影期间不提供离座选项，需要离开时请按播控面板上的「回一层大厅」；\n"
        + "三、右手 Grip 按一下呼出/收起目录，左手 Grip 按一下呼出/收起播控；\n"
        + "四、影片源由馆方放入 StreamingAssets 后自动上屏，缺片时屏上会显示提示；\n"
        + "五、关闭影片只退回待机卡，不会解锁站位；回一层大厅后抓书、加速等按键恢复原用法。";

    [MenuItem("VRLibrary/观影·锁定站位与目录朝前(只报告)")]
    public static void Audit() { Run(false); }

    [MenuItem("VRLibrary/观影·锁定站位与目录朝前(改场景)")]
    public static void Fix() { Run(true); }

    static void Run(bool apply)
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != kScene)
        {
            Debug.LogError("[观影修正] 当前场景不是 SmartLibrary:" + scene.path);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine((apply ? "== 观影·锁定站位与目录朝前(改场景) ==" : "== 观影·锁定站位与目录朝前(只报告) ==")
            + "  " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        var cine = FindT("SmartLibrary/StarryTerrace/CinemaController");
        var seat = FindT("SmartLibrary/StarryTerrace/CinemaSeat");
        if (cine == null || seat == null)
        {
            sb.AppendLine("  !! 找不到 CinemaController 或 CinemaSeat → 先跑「搭建星空露台」");
            Flush(sb);
            Debug.LogWarning("[观影修正] 找不到 CinemaController / CinemaSeat");
            return;
        }

        var comp = cine.GetComponent<TerraceCinema>();
        var catalog = cine.Find("CatalogPanel");
        var transport = cine.Find("TransportPanel");
        var notice = cine.Find("NoticePanel");
        var leave = cine.Find("LeavePanel");
        var exit = transport != null ? transport.Find("Exit") : null;
        var lobby = transport != null ? transport.Find("BackLobby") : null;

        var metrics = Measure(seat, catalog);
        sb.AppendLine(string.Format("  座位 {0} 朝向 {1}", F(seat.position), F(seat.forward)));
        sb.AppendLine(string.Format("  目录 {0} | 沿座位→银幕 {1:0.000} m / 横向 {2:+0.000;-0.000;0.000} m / 朝向差 {3:0.0}°",
            catalog == null ? "(缺)" : F(catalog.position), metrics.along, metrics.lateral, metrics.yawOff));
        sb.AppendLine("  离座面板:" + (leave == null ? "已移除 ✓" : "仍在 !!")
            + "  退出按钮:" + (exit == null ? "已移除 ✓" : "仍在 !!")
            + "  回一层大厅:" + (lobby == null ? "缺失 !!" : "在 ✓"));

        if (!apply)
        {
            bool ok = catalog != null && metrics.along > 0.5f && Mathf.Abs(metrics.lateral) < 0.05f
                      && metrics.yawOff < 1f && leave == null && exit == null && lobby != null;
            sb.AppendLine(ok ? "  结论:已符合定案 ✓" : "  结论:需要跑「改场景」菜单");
            Flush(sb);
            Debug.Log("[观影修正] 只报告 → " + kLog);
            return;
        }

        // ── 快照(只存一次,不覆盖) ────────────────────────────────────
        try
        {
            Directory.CreateDirectory("Logs");
            if (!File.Exists(kSnapshot))
            {
                File.Copy(kScene, kSnapshot);
                sb.AppendLine("  快照:" + kSnapshot);
            }
            else sb.AppendLine("  快照已在(不覆盖):" + kSnapshot);
        }
        catch (System.Exception e)
        {
            sb.AppendLine("  !! 快照失败,先不动场景:" + e.Message);
            Flush(sb);
            Debug.LogError("[观影修正] 快照失败:" + e.Message);
            return;
        }

        int changed = 0;

        // ① 删掉离座确认面板(连同「留下继续看 / 离座走动」两颗按钮)
        if (leave != null)
        {
            Undo.DestroyObjectImmediate(leave.gameObject);
            sb.AppendLine("  删除 LeavePanel(离座确认整条流程)");
            changed++;
        }

        // ② 删掉「退出观影」,把「回一层大厅」扩成整行 —— 它是唯一出口
        if (exit != null)
        {
            Undo.DestroyObjectImmediate(exit.gameObject);
            sb.AppendLine("  删除 Exit「退出观影」");
            changed++;
        }
        if (lobby != null)
        {
            var rt = lobby.GetComponent<RectTransform>();
            if (rt != null)
            {
                Undo.RecordObject(rt, "回一层大厅扩成整行");
                rt.anchorMin = new Vector2(0.05f, 0.05f);
                rt.anchorMax = new Vector2(0.95f, 0.24f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                EditorUtility.SetDirty(rt);
                sb.AppendLine("  BackLobby「回一层大厅」扩成整行(0.05~0.95)");
                changed++;
            }
        }

        // ③ 目录挪到座位正前方:局部 (0, 1.6, 1.08),朝向与座位一致。
        //    1.08 m = 旧"前方 0.95 / 左侧 0.52"的斜距,保持 40° 高 / 35° 宽,标题不会被顶出视野。
        if (catalog != null)
        {
            var rt = catalog.GetComponent<RectTransform>();
            Undo.RecordObject(rt, "目录挪到座位正前方");
            // 坑:RectTransform 的 x/y 真正落盘在 m_AnchoredPosition;只写 localPosition
            // 会让内存里当场正确、重开场景后又被旧的 anchoredPosition 拉回左边。
            rt.anchoredPosition3D = new Vector3(0f, 1.6f, kCatalogForward);
            rt.localRotation = Quaternion.identity;
            EditorUtility.SetDirty(rt);
            sb.AppendLine("  CatalogPanel → 座位正前方 (0, 1.6, " + kCatalogForward.ToString("0.00")
                + "),anchoredPosition3D + 朝向已写盘");
            changed++;
        }

        // ④ 面板文字与新规则对齐(旧文案还写着"按住 Grip""弹出要离座吗")
        var hint = catalog != null ? catalog.Find("Hint") : null;
        var hintText = hint != null ? hint.GetComponent<Text>() : null;
        if (hintText != null && hintText.text != kCatalogHint)
        {
            Undo.RecordObject(hintText, "目录提示改成按一下");
            hintText.text = kCatalogHint;
            EditorUtility.SetDirty(hintText);
            sb.AppendLine("  目录提示改成「Grip 按一下呼出/收起」");
            changed++;
        }
        var body = notice != null ? notice.Find("Body") : null;
        var bodyText = body != null ? body.GetComponent<Text>() : null;
        if (bodyText != null && bodyText.text != kNoticeBody)
        {
            Undo.RecordObject(bodyText, "观影须知改成硬锁规则");
            bodyText.text = kNoticeBody;
            EditorUtility.SetDirty(bodyText);
            sb.AppendLine("  观影须知改成硬锁站位/唯一出口版本");
            changed++;
        }

        // ⑤ 站位判定容差收紧到 1 cm(旧场景值是 5 cm)
        if (comp != null)
        {
            Undo.RecordObject(comp, "收紧站位容差");
            comp.pinEpsilon = 0.01f;
            EditorUtility.SetDirty(comp);
            sb.AppendLine("  pinEpsilon → 0.01 m");
            changed++;
        }

        Physics.SyncTransforms();
        var after = Measure(seat, catalog);
        bool post = catalog != null && after.along > 0.5f && Mathf.Abs(after.lateral) < 0.05f
                    && after.yawOff < 1f
                    && cine.Find("LeavePanel") == null
                    && (transport == null || transport.Find("Exit") == null);
        sb.AppendLine(string.Format("  [自证] 沿前 {0:0.000} m / 横向 {1:+0.000;-0.000;0.000} m / 朝向差 {2:0.0}°"
            + "  LeavePanel={3}  Exit={4}  → {5}",
            after.along, after.lateral, after.yawOff,
            cine.Find("LeavePanel") == null ? "无" : "仍在",
            transport == null || transport.Find("Exit") == null ? "无" : "仍在",
            post ? "通过 ✓" : "!! 未通过"));

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        sb.AppendLine("  改了 " + changed + " 项;保存场景:" + (saved ? "成功" : "失败")
            + "  isDirty(应为 False)=" + scene.isDirty);
        sb.AppendLine("  回退:把 " + kSnapshot + " 复制回 " + kScene + " 后重开场景");
        Flush(sb);
        Debug.Log("[观影修正] 完成:改 " + changed + " 项 → " + kLog);
    }

    struct Metrics
    {
        public float along;
        public float lateral;
        public float yawOff;
    }

    /// <summary>量目录相对"座位→银幕"的几何:沿前距离、横向偏移、朝向差。</summary>
    static Metrics Measure(Transform seat, Transform catalog)
    {
        var m = new Metrics { along = -999f, lateral = 999f, yawOff = 999f };
        if (seat == null || catalog == null) return m;
        var f = seat.forward; f.y = 0f; f.Normalize();
        var to = catalog.position - seat.position; to.y = 0f;
        m.along = Vector3.Dot(to, f);
        m.lateral = Vector3.Dot(to, seat.right);
        m.yawOff = Vector3.Angle(catalog.forward, seat.forward);
        return m;
    }

    static Transform FindT(string path)
    {
        var go = GameObject.Find(path);
        if (go != null) return go.transform;
        string leaf = path.Substring(path.LastIndexOf('/') + 1);
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
            if (t.name == leaf) return t;
        return null;
    }

    static string F(Vector3 v)
    {
        return string.Format("({0:0.000},{1:0.000},{2:0.000})", v.x, v.y, v.z);
    }

    static void Flush(StringBuilder sb)
    {
        try
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(kLog, sb.ToString(), new UTF8Encoding(false));
        }
        catch (System.Exception e) { Debug.LogWarning("[观影修正] 报告落盘失败:" + e.Message); }
    }
}
#endif
