// 全场合理化体检(只报告)。补 VRLibraryAudit A~I 没覆盖的资产层与设置层:
// 材质/贴图导入设置、渲染器材质槽、全部世界空间 Canvas 的正字侧、按名 Find 的重名歧义、
// 运行时写共享资产的回滚风险、碰撞体退化、灯光与 URP/Quality/打包设置。
// 不写任何资产、不改场景;唯一可能的副作用是对"内存里看着不对"的材质强制重导入再量一次(坑 22 的验法)。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class SanityAudit
{
    const string ReportPath = "Logs/sanity-audit.txt";
    static readonly StringBuilder sb = new StringBuilder();
    static int errors, warns;

    [MenuItem("VRLibrary/全场合理化体检(只报告)")]
    public static void Run()
    {
        errors = warns = 0;
        sb.Length = 0;
        sb.AppendLine("== 全场合理化体检 ==");
        sb.AppendLine("时间 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            + "  Unity " + Application.unityVersion
            + "  活动场景 " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);

        Section("J. 材质资产(shader / 发光接线 / 贴图槽)"); Materials();
        Section("K. 贴图导入设置(sRGB / 法线 / mipmap / 各向异性 / 平台覆写)"); Textures();
        Section("L. 渲染器材质槽(空槽与子网格数不匹配 = 洋红)"); RendererSlots();
        Section("M. 世界空间 Canvas 全量(正字侧 = -forward)"); Canvases();
        Section("N. 按名 Find 的重名歧义(同名多节点会抓错物体)"); FindByName();
        Section("O. 运行时写共享资产(退出 Play 不回滚)"); RuntimeAssetWrites();
        Section("P. 碰撞体与静态标记"); Colliders();
        Section("Q. 灯光 / 雾 / URP / Quality"); LightsAndPipeline();
        Section("R. 打包与平台设置"); BuildSettings();

        sb.AppendLine();
        sb.AppendLine("== 汇总:!! " + errors + " 处必须处理,-- " + warns + " 处值得看一眼 ==");
        var text = sb.ToString();
        if (!Directory.Exists("Logs")) Directory.CreateDirectory("Logs");
        File.WriteAllText(ReportPath, text, new UTF8Encoding(true));
        Debug.Log("[SanityAudit] 体检完成 → " + ReportPath + "\n" + text);
    }

    static void Section(string title)
    {
        sb.AppendLine();
        sb.AppendLine("———— " + title + " ————");
    }

    static void Bad(string s) { errors++; sb.AppendLine("  !! " + s); }
    static void Note(string s) { warns++; sb.AppendLine("  -- " + s); }
    static void Ok(string s) { sb.AppendLine("     " + s); }
    static void Say(bool serious, string s) { if (serious) Bad(s); else Note(s); }

    /// <summary>
    /// 只有本工程自产的资产才按"必须修"的标准判;包内示例资源(XR Hands / Starter Assets /
    /// PICO SDK / Virtual Tabletop)的导入设置是官方有意为之(UI 精灵用 Point、遮罩关 mipmap 等),
    /// 按自产标准判会刷出几百条无法处理的噪音,把真问题埋掉。
    /// </summary>
    static bool Ours(string assetPath)
    {
        const string self = "Assets/Materials|Assets/Skybox|Assets/Textures|Assets/_Scenes|"
            + "Assets/Scripts|Assets/Editor|Assets/Settings|Assets/Data|Assets/Prefabs";
        return Regex.IsMatch(assetPath, "^(" + self.Replace("/", "\\/") + ")\\/");
    }

    // 场景里所有根物体(含未激活),审计一律用 this,别用 GameObject.Find(会漏未激活的)。
    static GameObject[] SceneRoots()
    {
        var list = new List<GameObject>();
        foreach (var s in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            list.Add(s);
        return list.ToArray();
    }

    static IEnumerable<T> All<T>(bool includeInactive = true) where T : Component
    {
        foreach (var g in SceneRoots())
            foreach (var c in g.GetComponentsInChildren<T>(includeInactive)) yield return c;
    }

    // ----------------------------------------------------------- J. 材质

    static readonly Dictionary<Material, string> sceneMats = new Dictionary<Material, string>();

    static void Materials()
    {
        sceneMats.Clear();
        foreach (var r in All<Renderer>())
            foreach (var m in r.sharedMaterials)
                if (m != null && !sceneMats.ContainsKey(m)) sceneMats.Add(m, AssetDatabase.GetAssetPath(m));
        // 天空盒与管线资产不挂在 Renderer 上,否则会被误列成"没人用"
        if (RenderSettings.skybox != null && !sceneMats.ContainsKey(RenderSettings.skybox))
            sceneMats.Add(RenderSettings.skybox, AssetDatabase.GetAssetPath(RenderSettings.skybox));

        var paths = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
        int used = 0, emissive = 0;
        var unused = new List<string>();
        foreach (var id in paths)
        {
            var p = AssetDatabase.GUIDToAssetPath(id);
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) { Bad("材质读不出来:" + p); continue; }

            string tag = Path.GetFileNameWithoutExtension(p);
            bool mine = Ours(p);
            bool inScene = sceneMats.ContainsKey(m);
            if (inScene) used++; else if (mine) unused.Add(p);

            if (m.shader == null) { Bad(tag + " 没有 shader(资产损坏,必洋红)"); continue; }
            string sn = m.shader.name;
            if (mine && inScene && (sn.StartsWith("Legacy") || sn == "Standard" || sn.StartsWith("Mobile/")))
                Bad(tag + " 用的是非 URP shader「" + sn + "」→ URP 下渲染成洋红");

            bool hasEmi = m.HasProperty("_EmissionColor");
            var ec = hasEmi ? m.GetColor("_EmissionColor") : Color.black;
            bool hasMap = hasEmi && m.GetTexture("_EmissionMap") != null;
            bool wants = hasEmi && (hasMap || ec.maxColorComponent > 0.001f);
            bool kw = m.IsKeywordEnabled("_EMISSION");
            if (wants) emissive++;
            // Unity 给每张导入材质都留一个白色 _EmissionColor 而没有 _EmissionMap,那是默认值不是意图:
            // 只有"接了发光贴图"或"本场正在用且配了非黑发光色"的自产材质才值得按 !! 判。
            // URP 里 emission = _EmissionColor × _EmissionMap:接了贴图但颜色是黑的,是"地板发光开关"
            // 关掉时的正常状态,不是 bug;只有**颜色非黑**(真有发光意图)却关键字不活才算 serious。
            bool serious = mine && ec.maxColorComponent > 0.001f && (hasMap || inScene);
            if (wants && !kw)
            {
                // 坑 22:Material 在 AwakeFromLoad 里会按 globalIlluminationFlags 重算 _EMISSION,
                // 内存值不可信 —— 强制重导入后重新 Load 再判一次,才是磁盘真相。
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                var m2 = AssetDatabase.LoadAssetAtPath<Material>(p);
                bool kw2 = m2 != null && m2.IsKeywordEnabled("_EMISSION");
                string detail = " 发光色 " + F(ec) + (hasMap ? " +有 _EmissionMap" : " 无发光贴图")
                    + "，重导入后 _EMISSION " + (kw2 ? "活了" : "仍不活")
                    + "(globalIlluminationFlags=" + (m2 == null ? "?" : m2.globalIlluminationFlags.ToString()) + ")";
                if (!kw2 && serious) Bad(tag + " 想发光但关键字没活 → 场上其实是黑的" + detail);
                else if (kw2 && serious) Ok(tag + " 内存里 _EMISSION 关着,重导入后活了(坑 22 的正常形态)");
                else if (!kw2 && mine) Note(tag + " 配了发光色但没发光贴图也没关键字(默认值,可能根本没打算发光)" + detail);
            }
            if (wants && serious && m.globalIlluminationFlags == MaterialGlobalIlluminationFlags.EmissiveIsBlack)
                Bad(tag + " 有发光意图却仍是 EmissiveIsBlack → 下次重载会被摘掉 _EMISSION");

            if (m.HasProperty("_BumpMap") && m.IsKeywordEnabled("_NORMALMAP")
                && m.GetTexture("_BumpMap") == null)
            { if (mine) Bad(tag + " 开了 _NORMALMAP 但 _BumpMap 是空的"); }
            if (m.HasProperty("_MetallicGlossMap") && m.IsKeywordEnabled("_METALLICGLOSSMAP")
                && m.GetTexture("_MetallicGlossMap") == null)
            { if (mine) Bad(tag + " 开了 _METALLICGLOSSMAP 但贴图槽是空的"); }
            foreach (var kwName in new[] { "_EmissionMap", "_DetailAlbedoMap", "_MaskMap" })
                if (mine && m.HasProperty(kwName) && m.GetTexture(kwName) != null
                    && !AssetDatabase.IsSubAsset(m.GetTexture(kwName))
                    && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(m.GetTexture(kwName))))
                    Bad(tag + " 的 " + kwName + " 指向一张不在资产库里的贴图(引用已断)");
        }
        Ok("材质共 " + paths.Length + " 份(自产 " + MineCount(paths) + " 份),当前场景用到 " + used
            + " 份,配了发光相关量的 " + emissive + " 份");
        if (unused.Count > 0)
            Note("自产但场景没直接引用的材质 " + unused.Count + " 份:" + string.Join(" ", unused.Select(Path.GetFileName))
                + "(脚本运行时切 sharedMaterial 的会误报在这里,例如 M_ScreenOn/M_ScreenDark 这种成对切换的)");
        else Ok("自产材质全部被场景引用");
    }

    static int MineCount(string[] paths)
    {
        int n = 0;
        foreach (var id in paths) if (Ours(AssetDatabase.GUIDToAssetPath(id))) n++;
        return n;
    }

    // ----------------------------------------------------------- K. 贴图

    static void Textures()
    {
        var paths = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });
        // 反查"谁用这张贴图":GetDependencies(贴图) 给的是贴图自己的依赖(基本为空),
        // 正解是遍历材质,把每个材质依赖到的贴图计数。
        var users = new Dictionary<string, int>();
        var slots = new Dictionary<string, HashSet<string>>();

        // Sprite 类纹理不走 .mat 这条路:它被 UI 的 Image / SpriteRenderer 引用。
        // 只按 .mat 反查的话每张 UI 小图都会报"白占仓库"(v7.25 装 HUD 时一次冒出 3 条)。
        var uiSprites = new HashSet<string>();
        foreach (var gg in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Image>())
        {
            var sp = gg.sprite;
            if (sp == null) continue;
            string spath = AssetDatabase.GetAssetPath(sp);
            if (!string.IsNullOrEmpty(spath)) uiSprites.Add(spath);
        }
        foreach (var sr in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
        {
            var sp = sr.sprite;
            if (sp == null) continue;
            string spath = AssetDatabase.GetAssetPath(sp);
            if (!string.IsNullOrEmpty(spath)) uiSprites.Add(spath);
        }

        foreach (var mid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(mid));
            if (m == null || m.shader == null) continue;
            for (int i = 0; i < ShaderUtil.GetPropertyCount(m.shader); i++)
            {
                if (ShaderUtil.GetPropertyType(m.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string prop = ShaderUtil.GetPropertyName(m.shader, i);
                var tex = m.GetTexture(prop);
                if (tex == null) continue;
                string tp = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(tp)) continue;
                HashSet<string> s;
                if (!slots.TryGetValue(tp, out s)) { s = new HashSet<string>(); slots[tp] = s; }
                if (s.Add(prop))
                {
                    int n; users.TryGetValue(tp, out n);
                    users[tp] = n + 1;
                }
            }
        }

        long totalUncompressed = 0, pkgBytes = 0;
        var big = new List<string>();
        int noproxy = 0, linearWrong = 0, ours = 0, pkg = 0;
        foreach (var id in paths)
        {
            var p = AssetDatabase.GUIDToAssetPath(id);
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
            if (t == null) continue;
            string tag = Path.GetFileName(p);
            long bytes = (long)t.width * t.height * 4 * 4 / 3;   // RGBA32 + mipmap 约 1.33×
            if (!Ours(p))
            {
                // 包内示例资源(XR Hands / Starter Assets / PICO SDK / Samples):导入设置是官方
                // 有意为之(UI 精灵 Point、遮罩关 mipmap、非 2 的幂都正常),按自产标准判只会刷噪音。
                pkg++; pkgBytes += bytes; continue;
            }
            ours++;
            totalUncompressed += bytes;
            if (bytes > 8 << 20) big.Add(tag + " " + t.width + "×" + t.height + " ≈ " + (bytes >> 20) + " MB 未压缩");

            var ti = AssetImporter.GetAtPath(p) as TextureImporter;
            if (ti == null)
            {
                noproxy++;
                // 脚本生成的纹理没有导入器(遗留 12),值序列化在资产里,只能直接读纹理
                if (t.filterMode == FilterMode.Point) Bad(tag + " 是 Point 过滤 → 斜着看会起锯齿块");
                if (t.mipmapCount <= 1) Note(tag + " 没有 mipmap → 远处闪点(程序化纹理要单独配)");
                if (t.anisoLevel < 4) Note(tag + " 各向异性 " + t.anisoLevel + " → 地面类贴图远处会糊");
                continue;
            }

            int u; users.TryGetValue(p, out u);
            bool live = u > 0;                     // 没有任何材质引用的贴图,设置错了也不影响画面
            string dead = live ? "" : "(且无人引用,只算清理项)";
            bool normal = ti.textureType == TextureImporterType.NormalMap;
            bool srgb = ti.sRGBTexture;
            string lower = tag.ToLower();

            // 判据按**实际插槽**(Unity 自己的规则):颜色族要 sRGB,数据族要线性。
            // 只看文件名会误判 —— T_MetalGradient 名字像金属度,实际接在 _BaseMap 上,sRGB 才是对的。
            HashSet<string> mySlots; slots.TryGetValue(p, out mySlots);
            bool dataSlot = false, colorSlot = false;
            string slotList = mySlots == null ? "" : string.Join(",", mySlots);
            if (mySlots != null)
                foreach (var sn in mySlots)
                {
                    if (Regex.IsMatch(sn, @"Metallic|Mask|Bump|Occlusion|Height|SpecGloss", RegexOptions.IgnoreCase)) dataSlot = true;
                    if (Regex.IsMatch(sn, @"BaseMap|MainTex|Albedo|Emission|Tint", RegexOptions.IgnoreCase)) colorSlot = true;
                }
            if (dataSlot && colorSlot) { Bad(tag + " 同一张贴图既接在颜色插槽又接在数据插槽[" + slotList + "] → 接线错了"); linearWrong++; }
            else if (dataSlot && srgb && !normal) { Bad(tag + " 接在数据插槽[" + slotList + "]却勾了 sRGB → 数值被 gamma 解码,金属度/AO/遮罩会偏 " + dead); linearWrong++; }
            else if (colorSlot && !srgb && !normal) { Bad(tag + " 接在颜色插槽[" + slotList + "]却是线性 → 场上会暗一档 " + dead); linearWrong++; }
            else if (!live)
            {
                bool looksLinear = Regex.IsMatch(lower, @"normal|nrm|mask|metal|smooth|occ|_ao|orm|height|hgt");
                bool looksColor = Regex.IsMatch(lower, @"basecolor|albedo|diffuse|_color|emiss|emission");
                if ((looksLinear && srgb) || (looksColor && !srgb))
                    Note(tag + " 无人引用,按名字猜 sRGB 设置可能不对(不影响画面,算清理项)");
            }
            if (lower.Contains("normal") && !normal && live) Say(live, tag + " 名字像法线但 textureType=" + ti.textureType + " → 光照会错 " + dead);
            if (!ti.mipmapEnabled && t.width >= 256 && ti.textureType != TextureImporterType.Sprite)
                Note(tag + " 关了 mipmap → 远处闪(用在不平铺的大面上尤其明显)");
            if (ti.mipmapEnabled && ti.filterMode == FilterMode.Point) Say(live, tag + " Point 过滤 → 三线性/各向异性都白配 " + dead);
            if (ti.mipmapEnabled && t.width >= 512 && ti.anisoLevel < 4)
                Note(tag + " 各向异性 " + ti.anisoLevel + "(地面/桌面类贴图建议 ≥4,远处清晰度靠它)");
            if (ti.isReadable) Note(tag + " 开了 Read/Write → 内存里两份副本");
            if ((t.width & (t.width - 1)) != 0 || (t.height & (t.height - 1)) != 0)
                Note(tag + " 不是 2 的幂(" + t.width + "×" + t.height + ") → 部分压缩格式会退回 RGBA32");
            if (ti.maxTextureSize < Mathf.Max(t.width, t.height))
                Ok(tag + " 实际 " + t.width + "×" + t.height + " 被 maxSize " + ti.maxTextureSize + " 压小");
            bool usedByUi = ti.textureType == TextureImporterType.Sprite && uiSprites.Contains(p);
            if (!live && !usedByUi) Note(tag + " 没有任何 .mat 引用 → 白占仓库(不被场景依赖就不进 APK)");

            var and = ti.GetPlatformTextureSettings("Android");
            if (and.overridden && and.format == TextureImporterFormat.ETC2_RGBA8 && (normal || dataSlot))
                Note(tag + " Android 覆写 ETC2_RGBA8 用在法线/数据插槽[" + slotList + "] → 通道打包会串味,建议 ASTC 或分离通道");
        }
        Ok("Texture2D 共 " + paths.Length + " 张:自产 " + ours + " 张(未压缩约 " + (totalUncompressed >> 20)
            + " MB,不含 Android 压缩变体;其中无导入器的程序化纹理 " + noproxy + " 张)"
            + ",包内示例 " + pkg + " 张(约 " + (pkgBytes >> 20) + " MB,不参与本次判定,不被场景引用就不进 APK)");
        foreach (var b in big) Note("大贴图:" + b);
        if (linearWrong == 0) Ok("sRGB/线性判定:按文件名能判定的都对上了");
    }

    // ----------------------------------------------------------- L. 渲染器槽

    static void RendererSlots()
    {
        int bad = 0, checked_ = 0, paired = 0;
        foreach (var r in All<Renderer>())
        {
            checked_++;
            var mf = r.GetComponent<MeshFilter>();
            var sm = r is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)r).sharedMesh
                   : mf != null ? mf.sharedMesh : null;
            var mats = r.sharedMaterials;
            // TextMesh / SpriteRenderer / Line / Particle 自带几何,不走 MeshFilter —— 不能按"缺网格"判
            bool selfGeometric = r is TextMesh || r is SpriteRenderer
                                 || r is LineRenderer || r is ParticleSystemRenderer;
            if (sm == null && !selfGeometric)
            {
                if (!r.gameObject.activeInHierarchy) continue;
                // Unity 的 TextMesh 靠**同节点那个 MeshRenderer** 出几何(不需要 MeshFilter)。
                // 实测这些 MeshRenderer 的 bounds 是 (0.0117,0.0994,0.5046) 这种随书名长度变的真实
                // 文字框 —— 所以它不是"空壳",既有审计拿 GetComponent<MeshRenderer>() 量书名是对的。
                bool textPair = r.GetComponent<TextMesh>() != null || r.GetComponent<SpriteRenderer>() != null;
                if (textPair) { paired++; continue; }
                Bad(Node(r.transform) + " 有渲染器却既没 MeshFilter 也没 TextMesh/SpriteRenderer → 画不出东西");
            }
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] == null)
                {
                    Bad(Node(r.transform) + " 材质槽[" + i + "] 是空的 → 运行时洋红"); bad++;
                }
                else if (mats[i].shader == null)
                {
                    Bad(Node(r.transform) + " 材质槽[" + i + "] 的 shader 丢了"); bad++;
                }
            if (sm != null && !selfGeometric && mats.Length != sm.subMeshCount)
            {
                Bad(Node(r.transform) + " 网格有 " + sm.subMeshCount + " 个子网格但材质只有 " + mats.Length
                    + " 槽 → 多出来的子网格不显示"); bad++;
            }
        }
        Ok("渲染器 " + checked_ + " 个:槽位/缺网格问题 " + bad
            + " 处;另有 " + paired + " 个 MeshRenderer 与 TextMesh/SpriteRenderer 同节点(那是文字自己的渲染器,正常)");
    }

    // ----------------------------------------------------------- M. 世界空间屏

    static void Canvases()
    {
        var pads = new List<Transform>();
        // 只数锚点本身:Pad_*_Halo 是锚点底下的光晕子物体,不是传送点(数进去会把 11 个报成 22 个)
        foreach (var t in All<Transform>())
            if (t.name.StartsWith("Pad_") && !t.name.EndsWith("_Halo")) pads.Add(t);
        int ws = 0, ss = 0;
        foreach (var c in All<Canvas>())
        {
            var rt = c.GetComponent<RectTransform>();
            float w = rt == null ? 0 : rt.rect.width * c.transform.localScale.x;
            float h = rt == null ? 0 : rt.rect.height * c.transform.localScale.y;
            if (c.renderMode != RenderMode.WorldSpace)
            {
                ss++;
                if (c.renderMode == RenderMode.ScreenSpaceOverlay)
                    Bad(Node(c.transform) + " 是 ScreenSpaceOverlay → XR 里会盖在两只眼睛前面,应改 WorldSpace");
                continue;
            }
            ws++;
            var f = c.transform.forward;
            var read = -f;                       // UGUI 正字面 = -forward(坑 26)
            float yaw = Mathf.Atan2(read.x, read.z) * Mathf.Rad2Deg;
            string side = Mathf.Abs(read.z) > Mathf.Abs(read.x)
                ? (read.z > 0 ? "+Z" : "-Z") : (read.x > 0 ? "+X" : "-X");
            bool ray = c.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null
                       || c.GetComponent("TrackedDeviceGraphicRaycaster") != null
                       || c.GetComponent("XRGraphicRaycaster") != null;
            int graphics = c.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length;
            var pos = c.transform.position;
            if (graphics == 0) { Bad(Node(c.transform) + " 世界空间 Canvas 里一个 Graphic 都没有 → 这块屏是空的"); continue; }
            if (!ray) Note(Node(c.transform) + " 没有任何 GraphicRaycaster → 看得见点不着(位置 " + pos.ToString("F2") + ")");
            if (w < 0.05f || h < 0.05f) Note(Node(c.transform) + " 世界尺寸 " + w.ToString("0.000") + "×"
                + h.ToString("0.000") + " m,小到读不了字");
            // 挂在屏面组下的 Canvas 是"屏上的图层"(露台影片信息卡按设计铺满整块屏面),不按阅读面板的尺度判
            bool onScreen = c.transform.parent != null && c.transform.parent.name.Contains("Screen");
            if (!onScreen && (w > 8f || h > 8f)) Note(Node(c.transform) + " 世界尺寸 " + w.ToString("0.0") + "×"
                + h.ToString("0.0") + " m 偏大");

            string padInfo = "无传送点可比";
            int readableFrom = 0, padTotal = pads.Count;
            if (pads.Count > 0)
            {
                Transform near = null; float best = 1e9f;
                foreach (var p in pads)
                {
                    float d = (p.position - pos).sqrMagnitude;
                    if (d < best) { best = d; near = p; }
                    if (Vector3.Dot(read, p.position - pos) > 0f) readableFrom++;
                }
                padInfo = "最近落点 " + near.name + " 距 " + Mathf.Sqrt(best).ToString("0.0") + " m;能被 "
                    + readableFrom + "/" + padTotal + " 个落点读到";
            }
            Ok(Node(c.transform) + " 正字侧 " + side + " 可读向量 " + Fv(read) + " 朝向 " + yaw.ToString("0") + "°"
                + " 世界 " + pos.ToString("F2") + " 尺寸 " + w.ToString("0.00") + "×" + h.ToString("0.00") + " m"
                + " Graphic " + graphics + " " + (ray ? "可点" : "不可点")
                + (c.gameObject.activeInHierarchy ? "" : " (未激活)") + " — " + padInfo);
            // 阅读面板是运行时跟随玩家的,场上那个静态朝向没有意义,不参与"面向谁"的判定。
            // 判据用"有没有任何一个落点读得到"而不是"最近的落点读得到":45° 斜置的角牌
            // 本来就不可能正对最近的那个点(拿最近点判会把摆正后的 C3 再误报一次)。
            bool follower = c.name.StartsWith("BookReader");
            if (readableFrom == 0 && padTotal > 0 && !follower)
                Bad(Node(c.transform) + " 馆内 " + padTotal + " 个传送落点没有一个能读到正字 → 游客只能看到背面镜像字"
                    + "(判据:可读面 = -forward)");
        }
        Ok("Canvas:世界空间 " + ws + " 块,其它模式 " + ss + " 块");
    }

    static string Fv(Vector3 v)
    {
        return string.Format("({0:0.00},{1:0.00},{2:0.00})", v.x, v.y, v.z);
    }

    // ----------------------------------------------------------- N. 重名歧义

    static void FindByName()
    {
        var names = new HashSet<string>();
        // 只有 GameObject.Find 是全局按名查找(会命中任意同名节点);
        // transform.Find("Text") 这种是"在自己子树里找",场上有 33 个 Text 也完全正常,不算歧义。
        var re = new Regex("GameObject\\.Find\\(\\s*\"([^\"]+)\"");
        foreach (var f in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories))
        {
            string src;
            try { src = File.ReadAllText(f); } catch { continue; }
            foreach (Match m in re.Matches(src))
            {
                string path = m.Groups[1].Value;
                foreach (var seg in path.Split('/')) if (seg.Length > 1) names.Add(seg);
            }
        }

        var count = new Dictionary<string, int>();
        foreach (var t in All<Transform>())
        {
            int n; count.TryGetValue(t.name, out n);
            count[t.name] = n + 1;
        }

        int risky = 0;
        foreach (var n in names.OrderBy(x => x))
        {
            int c; count.TryGetValue(n, out c);
            if (c > 1)
            {
                risky++;
                if (risky <= 20)
                    Note("代码里按名字找 \"" + n + "\",但场上有 " + c + " 个同名节点 → "
                        + "GameObject.Find 只按深度优先给第一个,可能抓错(改按组件或唯一路径找)");
            }
        }
        Ok("代码里出现的按名查找字面量 " + names.Count + " 个,其中 " + risky + " 个在场上有重名");
    }

    // ----------------------------------------------------------- O. 运行时写共享资产

    static void RuntimeAssetWrites()
    {
        // 只有"改共享材质对象的属性"才会真的动到 .mat 资产且退出 Play 不回滚:
        //   RenderSettings.skybox.SetFloat(...) / sky = RenderSettings.skybox; sky.SetFloat(...)
        //   mr.sharedMaterial.SetFloat(...)
        // 而 `mr.sharedMaterial = 另一个材质` 只是换渲染器槽的引用(Play 退出会回滚,不动资产),
        // `renderer.material` 是每次访问克隆实例(安全但泄漏)。这三种要分开判。
        var direct = new Regex(@"RenderSettings\.skybox\s*\.\s*(Set\w+|EnableKeyword|DisableKeyword)\s*\(");
        var sharedMut = new Regex(@"sharedMaterials?\s*(?:\[[^\]]+\])?\s*\.\s*(Set\w+|EnableKeyword|DisableKeyword)\s*\(");
        var instUse = new Regex(@"\.material\s*\.\s*(Set\w+|EnableKeyword)\s*\(|GetComponent<Renderer>\(\)\.material\s*=|\.material\s*=");

        int bad = 0, inst = 0;
        var allSafe = new HashSet<string>();
        foreach (var f in Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories))
        {
            var raw = File.ReadAllLines(f);
            var lines = new List<int>();          // 非注释行的原始下标,报出去的行号才对得上文件
            var code = new List<string>();
            for (int i = 0; i < raw.Length; i++)
                if (!raw[i].TrimStart().StartsWith("//")) { lines.Add(i); code.Add(raw[i]); }

            // 共享材质会"转手":sky = RenderSettings.skybox(字段) → var skyX = sky(局部) → skyX.SetFloat。
            // 只追一跳会漏(DayNightController 正是这样,第一版就假绿了),所以做不动点传播。
            var known = new HashSet<string> { "RenderSettings.skybox" };
            var safe = new HashSet<string>();      // X = new Material(...) 得到的是运行时实例,写它安全
            for (int pass = 0; pass < 5; pass++)
            {
                int added = 0;
                foreach (var l in code)
                {
                    foreach (Match mm in Regex.Matches(l, @"(\w+)\s*=\s*new Material\s*\("))
                    { safe.Add(mm.Groups[1].Value); allSafe.Add(mm.Groups[1].Value); }
                    foreach (var s in safe.ToArray())
                        if (s != "RenderSettings.skybox"
                            && Regex.IsMatch(l, @"(\w+)\s*=(?!=)[^;]*\b" + Regex.Escape(s) + @"\b"))
                            safe.Add(Regex.Match(l, @"(\w+)\s*=(?!=)[^;]*\b" + Regex.Escape(s) + @"\b").Groups[1].Value);
                    if (l.Contains("new Material")) continue;      // 这一行是克隆,不是转手共享对象
                    foreach (var e in known.ToArray())
                    {
                        string pat = @"(\w+)\s*=(?!=)[^;]*\b" + Regex.Escape(e) + @"\b";
                        var m = Regex.Match(l, pat);
                        if (m.Success && !known.Contains(m.Groups[1].Value) && !safe.Contains(m.Groups[1].Value))
                        { known.Add(m.Groups[1].Value); added++; }
                    }
                }
                if (added == 0) break;
            }

            for (int i = 0; i < code.Count; i++)
            {
                string l = code[i];
                string who = Path.GetFileName(f) + ":" + (lines[i] + 1);
                string hitVar = null;
                foreach (var e in known)
                    if (e != "RenderSettings.skybox" && !safe.Contains(e)
                        && Regex.IsMatch(l, @"\b" + Regex.Escape(e) + @"\s*\.\s*(Set\w+|EnableKeyword|DisableKeyword)\s*\("))
                    { hitVar = e; break; }
                if (direct.IsMatch(l))
                { Bad("直接改共享天空盒属性(动的是 .mat 资产,退出 Play 不回滚):" + who + " " + l.Trim()); bad++; }
                else if (sharedMut.IsMatch(l))
                { Bad("改 sharedMaterial 的属性(同上,不回滚):" + who + " " + l.Trim()); bad++; }
                else if (hitVar != null)
                { Bad("改共享材质属性(" + hitVar + " 是从 RenderSettings.skybox 转手来的,退出 Play 不回滚):"
                    + who + " " + l.Trim()); bad++; }
                if (instUse.IsMatch(l)) { Note("运行时用 .material(每次访问克隆一份实例,容易泄漏):" + who); inst++; }
            }
        }
        if (bad == 0) Ok("没扫到「改共享材质属性」的代码");
        if (allSafe.Count > 0) Ok("按「运行时克隆实例」放行的变量:" + string.Join(",", allSafe)
            + "(写的是实例不是资产,DayNightController 就是这么修的)");
        Ok("共享材质写入 " + bad + " 处,实例化克隆 " + inst + " 处");
    }

    // ----------------------------------------------------------- P. 碰撞体

    static void Colliders()
    {
        int n = 0, bad = 0;
        foreach (var c in All<Collider>())
        {
            n++;
            var b = c as BoxCollider;
            var s = c as SphereCollider;
            var cap = c as CapsuleCollider;
            var mc = c as MeshCollider;
            if (b != null && (b.size.x <= 0.0001f || b.size.y <= 0.0001f || b.size.z <= 0.0001f))
            { Bad(Node(c.transform) + " BoxCollider 有一维为 0 → 物理查询拿不到它"); bad++; }
            if (s != null && s.radius <= 0.0001f) { Bad(Node(c.transform) + " SphereCollider 半径 0"); bad++; }
            if (cap != null && (cap.radius <= 0.0001f || cap.height <= 0.0001f)) { Bad(Node(c.transform) + " CapsuleCollider 尺寸 0"); bad++; }
            if (mc != null && mc.sharedMesh == null) { Bad(Node(c.transform) + " MeshCollider 没有网格"); bad++; continue; }
            if (mc != null && !mc.sharedMesh.isReadable) Note(Node(c.transform) + " MeshCollider 的网格不可读(不影响碰撞,影响运行时改网格)");
            if (!c.enabled && c.isTrigger) Note(Node(c.transform) + " trigger 碰撞体被禁用 → 交互口失效");
        }
        Ok("碰撞体 " + n + " 个,退化 " + bad + " 个");
    }

    // ----------------------------------------------------------- Q. 灯光与管线

    static void LightsAndPipeline()
    {
        int dir = 0, punctual = 0, shadowed = 0;
        foreach (var l in All<Light>())
        {
            if (l.type == LightType.Directional) dir++; else punctual++;
            if (l.shadows != LightShadows.None) shadowed++;
            if (l.type != LightType.Directional && l.range <= 0.01f) Bad(Node(l.transform) + " 点/聚光 range≈0 → 照不到任何东西");
            if (l.intensity > 30f) Note(Node(l.transform) + " 强度 " + l.intensity.ToString("0.0") + " 偏大,容易过曝");
        }
        Ok("灯光:平行光 " + dir + " 盏,点/聚光 " + punctual + " 盏,开阴影的 " + shadowed + " 盏(移动端每盏实时阴影都要一遍渲染)");
        if (dir > 1) Note("平行光 " + dir + " 盏 → 移动端通常只留 1 盏");

        var fog = RenderSettings.fog;
        Ok("雾:" + (fog ? RenderSettings.fogMode + " "
            + (RenderSettings.fogMode == FogMode.Exponential || RenderSettings.fogMode == FogMode.ExponentialSquared
                ? "density=" + RenderSettings.fogDensity.ToString("0.0000")
                : "起始 " + RenderSettings.fogStartDistance.ToString("0.0") + " 结束 " + RenderSettings.fogEndDistance.ToString("0.0"))
            + " 色 " + F(RenderSettings.fogColor) : "关"));
        Ok("天空盒:" + (RenderSettings.skybox == null ? "!! 没有" : AssetDatabase.GetAssetPath(RenderSettings.skybox)));

        var asset = GraphicsSettings.renderPipelineAsset as UniversalRenderPipelineAsset;
        var qAsset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        if (qAsset != null && qAsset != asset)
            Note("QualitySettings 上有管线覆写(生效的是它,不是 GraphicsSettings 那份):" + qAsset.name);
        var pipe = qAsset != null ? qAsset : asset;
        if (pipe == null) { Bad("没有 URP 管线资产 → 全场会渲染成洋红"); return; }
        Ok("管线资产:" + AssetDatabase.GetAssetPath(pipe));
        foreach (var prop in new[] { "shadowDistance", "supportsMainLightShadows", "supportsAdditionalLightShadows",
                                     "additionalLightsShadowmapResolution", "msaaSampleCount", "renderScale",
                                     "supportsDynamicBatching", "srpBatcherSettings", "mainLightShadowmapResolution" })
        {
            var pi = pipe.GetType().GetProperty(prop);
            if (pi == null) continue;
            object val;
            try { val = pi.GetValue(pipe, null); } catch { continue; }
            Ok("  " + prop + " = " + val);
            if (prop == "mainLightShadowmapResolution")
            {
                int px = ToInt(val);
                if (px >= 4096) Note("主光阴影图 " + px + "² → 一份深度附件约 "
                    + ((px * px * 4) >> 20) + " MB,一体机(PICO 4 与 CPU 共享那 6 GB)上偏大,2048 通常够");
            }
            if (prop == "supportsAdditionalLightShadows" && ToBool(val) && shadowed <= dir)
                Note("管线开着额外光阴影,但全场 " + punctual + " 盏点/聚光没有一盏投影 → 白占一张 "
                    + ToInt(Reflect(pipe, "additionalLightsShadowmapResolution")) + "² 阴影图");
            if (prop == "renderScale")
            {
                float rs = ToFloat(val);
                if (rs > 1.01f) Note("renderScale=" + rs.ToString("0.00") + " 已是超采样,再叠 MSAA"
                    + ToInt(Reflect(pipe, "msaaSampleCount")) + "× → 片元开销约 "
                    + ((rs * rs) * ToInt(Reflect(pipe, "msaaSampleCount"))).ToString("0.0")
                    + " 倍,真机帧率必须实测(遗留 4)");
            }
        }

        int qi = QualitySettings.GetQualityLevel();
        Ok("画质档:" + QualitySettings.names[qi] + "(共 " + QualitySettings.names.Length + " 档)"
            + " AA=" + QualitySettings.antiAliasing
            + " 阴影距离=" + QualitySettings.shadowDistance
            + " mipmapLimit=" + QualitySettings.globalTextureMipmapLimit
            + " 实时阴影=" + QualitySettings.shadows);
        if (QualitySettings.globalTextureMipmapLimit > 0)
            Bad("QualitySettings.mipmapLimit=" + QualitySettings.globalTextureMipmapLimit + " → 全场贴图直接降一档,远处糊(与运行时强制 0 的自检冲突)");
    }

    // ----------------------------------------------------------- R. 打包设置

    static void BuildSettings()
    {
        var scenes = EditorBuildSettings.scenes;
        Ok("Build 场景 " + scenes.Length + " 个,活动平台 " + EditorUserBuildSettings.activeBuildTarget);
        foreach (var s in scenes)
        {
            bool exists = File.Exists(s.path);
            if (!exists) Bad("Build 列表里的场景文件不存在:" + s.path);
            else if (!s.enabled) Note("Build 列表里有未勾选的场景:" + s.path);
            else Ok("  " + s.path);
        }
        if (!scenes.Any(s => s.enabled && s.path.Contains("SmartLibrary")))
            Bad("SmartLibrary 没在 Build 场景列表里 → 打出来的 APK 进不了馆");

        var nbt = NamedBuildTarget.Android;
        Ok("脚本后端:" + PlayerSettings.GetScriptingBackend(nbt)
            + "  图形 API:" + string.Join("/", PlayerSettings.GetGraphicsAPIs(BuildTarget.Android))
            + "  IL2CPP 代码生成:" + PlayerSettings.GetIl2CppCodeGeneration(nbt));
        Ok("minSdk:" + PlayerSettings.Android.minSdkVersion
            + "  targetSdk:" + PlayerSettings.Android.targetSdkVersion
            + "  架构:" + PlayerSettings.Android.targetArchitectures);
        if (!PlayerSettings.GetGraphicsAPIs(BuildTarget.Android).Contains(GraphicsDeviceType.OpenGLES3)
            && !PlayerSettings.GetGraphicsAPIs(BuildTarget.Android).Contains(GraphicsDeviceType.Vulkan))
            Note("Android 图形 API 里没有 GLES3/Vulkan → PICO 上可能起不来");
        Ok("API 兼容级别:" + PlayerSettings.GetApiCompatibilityLevel(nbt)
            + "  Android 宏:" + PlayerSettings.GetScriptingDefineSymbols(nbt));
        Ok("Streaming Mipmaps 总开关:" + (QualitySettings.streamingMipmapsActive ? "开" : "关")
            + "  渲染到安全区外:" + PlayerSettings.Android.renderOutsideSafeArea);
    }

    // ----------------------------------------------------------- 小工具

    static string Node(Transform t)
    {
        string s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    static string F(Color c) { return string.Format("({0:0.000},{1:0.000},{2:0.000})", c.r, c.g, c.b); }

    static object Reflect(object o, string prop)
    {
        var pi = o == null ? null : o.GetType().GetProperty(prop);
        if (pi == null) return null;
        try { return pi.GetValue(o, null); } catch { return null; }
    }

    static int ToInt(object v) { return v == null ? 0 : System.Convert.ToInt32(v); }
    static bool ToBool(object v) { return v != null && System.Convert.ToBoolean(v); }
    static float ToFloat(object v) { return v == null ? 0f : System.Convert.ToSingle(v); }
}
