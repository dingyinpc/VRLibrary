using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 播片之前先问清楚"这台机器拿什么解码",再把答案变成一条能看懂的路线。
///
/// 四条硬事实决定了这里能做什么、不能做什么:
/// 1. Unity 的 `VideoPlayer` 在 Android 上就是走系统的 `MediaCodec`,**托管层换不掉它**。
///    所以"有硬解就用硬解"这一半是天然成立的;要管的是**没有硬解时怎么办**。
/// 2. 真正会"没有硬解"的是新编码:PICO 4 系列有 H.264/H.265 硬解,但没有 AV1;
///    老一点的一体机也可能缺 HEVC。H.264 反过来在 AOSP 里永远带一个软件解码器
///    (`c2.android.avc.decoder` / `OMX.google.h264.decoder`),所以 H.264 基本不会无解。
/// 3. "在应用里内置解码器"必须是原生插件,C# 里手写不出来。**2026-09-21 已定:不装,残留也清了。**
///    查过唯一像样的候选 `videolan/vlc-unity`(LGPL 2.1 + 专有双许可):仓库**只有源码**
///    (git tree 里没有任何 .so/.aar),Android 要用 `PluginSource` + `cross/android-*-ndk25.txt`
///    自己 meson+NDK25 交叉编译整条 libVLC,而试用版还烧水印(`TrialWatermark.h`);
///    能直接落包的版本在 Videolabs 商店卖。所以"能不能播"由**片源编码**保证
///    (闸门在 `TerraceSetup.CodecGate`:H.264 安全 / HEVC·VP9 警告 / 其余危险)。
/// 4. 2022.3 的托管层**问不到"平台最后挑了哪条通路"**(没有 VideoPlayer.GetTracePath,
///    实测 CS0117)。所以这里只声明两件事:路线是按本机解码器清单推出来的,
///    "到底解不解得动"由 TerraceScreen 的实测吞吐说了算 —— 不编一个通路字符串当证据。
///
/// 所有查询都带 try/catch:解码器列表查不到不是崩溃理由,降级成 `Unknown` 让平台自己决定。
/// </summary>
public enum DecodeRoute
{
    /// <summary>本机没有任何能解这个编码的解码器 —— 别去 Prepare,直接告诉人换源。</summary>
    NoDecoder,
    /// <summary>设备内置硬解。</summary>
    Hardware,
    /// <summary>平台自带软解:通用但费电、高分辨率容易掉帧。</summary>
    PlatformSoftware,
    /// <summary>枚举不到(编辑器/桌面):路线交给平台自己定,不硬吹。</summary>
    Unknown,
}

/// <summary>一次"这个编码在本机怎么解"的结论。</summary>
public struct DecoderPlan
{
    public DecodeRoute route;
    public string mime;
    /// <summary>选中的解码器名字(枚举不到时为 "")。</summary>
    public string decoder;
    /// <summary>本机同编码下各档解码器的数量,用来说明"为什么这么选"。</summary>
    public int hardwareCount;
    public int softwareCount;
    /// <summary>失败原因,仅 route=NoDecoder 时有值。</summary>
    public string reason;

    public bool Playable { get { return route != DecodeRoute.NoDecoder; } }
}

/// <summary>一个设备上的一个编解码器条目。</summary>
public struct DecoderInfo
{
    public string name;
    public bool hardware;
    public bool encoder;
}

// 注:曾考虑过"应用内置软解插件"这条路(ISoftwareDecoder 反射接入点),2026-09-21 定案不装并删干净
// —— 理由见文件头第 3 条。真要接,重新加一个接口 + 反射查找即可,别提前留空壳。

public static class VideoDecodeProbe
{
    public const string MimeH264 = "video/avc";
    public const string MimeH265 = "video/hevc";
    public const string MimeVp9 = "video/x-vnd.on2.vp9";
    public const string MimeAv1 = "video/av01";

    static readonly List<DecoderInfo> deviceDecoders = new List<DecoderInfo>();
    static bool listed;
    static int sdkInt = -1;

    /// <summary>本机解码器清单(Android 枚举得到;编辑器里是空的)。</summary>
    public static IReadOnlyList<DecoderInfo> Decoders
    {
        get { EnsureList(); return deviceDecoders; }
    }

    public static bool CanEnumerate { get { EnsureList(); return deviceDecoders.Count > 0; } }

    public static string PlatformDescription
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            EnsureList();
            return "Android API " + SdkInt + "(" + SystemInfo.deviceModel + "),解码器 " + deviceDecoders.Count + " 个";
#elif UNITY_EDITOR
            return "编辑器(" + SystemInfo.operatingSystem + "),Unity 走系统播放器,枚举不到解码器清单";
#else
            return SystemInfo.operatingSystem + ",Unity 走系统播放器,枚举不到解码器清单";
#endif
        }
    }

    /// <summary>片单里写的 codec 标签 → Android MIME。空/不认识一律按 H.264。</summary>
    public static string MimeOf(string codec)
    {
        if (string.IsNullOrEmpty(codec)) return MimeH264;
        string c = codec.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(".", "");
        if (c == "h264" || c == "avc" || c == "x264") return MimeH264;
        if (c == "h265" || c == "hevc" || c == "x265") return MimeH265;
        if (c == "vp9") return MimeVp9;
        if (c == "av1") return MimeAv1;
        if (c == "vp8") return "video/x-vnd.on2.vp8";
        if (c.Contains("/")) return codec.Trim();       // 直接写了 MIME 就照收
        Debug.LogWarning("[DecodeProbe] 不认识的 codec=\"" + codec + "\",按 H.264 处理");
        return MimeH264;
    }

    public static string CodecLabel(string mime)
    {
        if (mime == MimeH264) return "H.264";
        if (mime == MimeH265) return "H.265/HEVC";
        if (mime == MimeVp9) return "VP9";
        if (mime == MimeAv1) return "AV1";
        return string.IsNullOrEmpty(mime) ? "—" : mime;
    }

    /// <summary>
    /// 决定这条片在本机走哪条解码路线。
    /// **用户定的优先级:真机自带的优先** —— 设备硬解 → 平台软解(受 allowSoftwareDecode 管) → 无解。
    /// </summary>
    public static DecoderPlan PlanFor(string codec, bool allowSoftwareDecode)
    {
        string mime = MimeOf(codec);
        var p = new DecoderPlan { mime = mime };

        var hw = Pick(mime, true);
        var sw = Pick(mime, false);
        foreach (var d in Decoders)
        {
            if (d.encoder) continue;
            if (!Supports(d, mime)) continue;
            if (d.hardware) p.hardwareCount++; else p.softwareCount++;
        }

        if (hw.name != null)
        {
            p.route = DecodeRoute.Hardware;
            p.decoder = hw.name;
        }
        else if (sw.name != null && allowSoftwareDecode)
        {
            p.route = DecodeRoute.PlatformSoftware;
            p.decoder = sw.name;
        }
        else if (sw.name != null)
        {
            p.route = DecodeRoute.NoDecoder;
            p.reason = "本机只有平台软解 " + sw.name + "，但设置里关掉了「平台软解回退」";
        }
        else if (CanEnumerate)
        {
            p.route = DecodeRoute.NoDecoder;
            p.reason = "本机枚举到 " + Decoders.Count + " 个解码器,没有一个支持 " + CodecLabel(mime);
        }
        else
        {
            p.route = DecodeRoute.Unknown;
        }
        return p;
    }

    /// <summary>拼成屏上/日志用的一行字。</summary>
    public static string Report(DecoderPlan p)
    {
        string label = CodecLabel(p.mime);
        switch (p.route)
        {
            case DecodeRoute.Hardware:
                return "解码:" + label + " 走设备内置硬解 " + p.decoder
                       + "(同编码硬解 " + p.hardwareCount + " / 软解 " + p.softwareCount + " 个)";
            case DecodeRoute.PlatformSoftware:
                return "解码:" + label + " 本机无硬解,退回平台软解 " + p.decoder
                       + " → 高分辨率可能掉帧";
            case DecodeRoute.NoDecoder:
                return "解码:本机解不了 " + label + " —— " + p.reason;
            default:
                return "解码:" + label + " 交给系统播放器(此平台枚举不到解码器清单)";
        }
    }

    /// <summary>屏上用的短版:一行放得下;长版 <see cref="Report"/> 留给日志与复核输出。</summary>
    public static string Brief(DecoderPlan p)
    {
        string label = CodecLabel(p.mime);
        switch (p.route)
        {
            case DecodeRoute.Hardware: return "解码:" + label + " 走设备硬解 " + p.decoder;
            case DecodeRoute.PlatformSoftware: return "解码:" + label + " 本机无硬解,退平台软解 " + p.decoder;
            case DecodeRoute.NoDecoder: return "解码:本机解不了 " + label;
            default: return "解码:" + label + " 交给系统播放器";
        }
    }

    /// <summary>屏上那一行用的短标签:只讲"靠什么解 + 跟不跟得上"。</summary>
    public static string ShortReport(DecoderPlan p, float measuredFps, double sourceFps)
    {
        string head;
        switch (p.route)
        {
            case DecodeRoute.Hardware: head = "设备硬解"; break;
            case DecodeRoute.PlatformSoftware: head = "平台软解"; break;
            case DecodeRoute.NoDecoder: head = "无解码器"; break;
            default: head = "系统播放器"; break;
        }
        string who = string.IsNullOrEmpty(p.decoder) ? "(" + CodecLabel(p.mime) + ")" : "(" + p.decoder + ")";
        if (sourceFps <= 0.0 || measuredFps <= 0f) return head + who;
        return head + who + string.Format(" 实测 {0:0.0} / 源 {1:0.0} fps", measuredFps, sourceFps);
    }

    // ------------------------------------------------------------ 解码器清单

    static void EnsureList()
    {
        if (listed) return;
        listed = true;
#if UNITY_ANDROID && !UNITY_EDITOR
        TryEnumerateAndroid();
#endif
    }

    static int SdkInt
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (sdkInt < 0)
            {
                try
                {
                    using (var v = new AndroidJavaClass("android.os.Build$VERSION"))
                        sdkInt = v.GetStatic<int>("SDK_INT");
                }
                catch (Exception e) { Debug.LogWarning("[DecodeProbe] 读 SDK_INT 失败:" + e.Message); sdkInt = 0; }
            }
            return sdkInt;
#else
            return 0;
#endif
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    // 这段在编辑器里**不参与编译**,写错要等到打包才炸;改完把本文件 4 处 #if 临时改成
    // `#if UNITY_ANDROID` 跑一次 compile-check 再改回来(2026-09-21 就是这样抓到 3 个 IntPtr 类型错)。
    static void TryEnumerateAndroid()
    {
        try
        {
            // MediaCodecList(REGULAR_CODECS):别用 ALL_HW_CODECS,那个会把别名和厂商禁用项混进来
            using (var list = new AndroidJavaObject("android.media.MediaCodecList", 0))
            using (var infos = list.Call<AndroidJavaObject>("getCodecInfos"))
            {
                if (infos == null) return;
                System.IntPtr raw = infos.GetRawObject();
                int n = AndroidJNI.GetArrayLength(raw);
                for (int i = 0; i < n; i++)
                {
                    System.IntPtr e = AndroidJNI.GetObjectArrayElement(raw, i);
                    if (e == System.IntPtr.Zero) continue;
                    using (var info = new AndroidJavaObject(e))
                    {
                        var di = new DecoderInfo
                        {
                            name = info.Call<string>("getName"),
                            encoder = CallBool(info, "isEncoder"),
                        };
                        bool? hwAcc = TryBool(info, "isHardwareAccelerated");
                        bool? swOnly = TryBool(info, "isSoftwareOnly");
                        bool hw = hwAcc.HasValue ? hwAcc.Value
                                  : (swOnly.HasValue ? !swOnly.Value : !LooksSoftware(di.name));
                        di.hardware = hw;
                        deviceDecoders.Add(di);
                    }
                }
            }
            Debug.Log("[DecodeProbe] 本机解码器 " + deviceDecoders.Count + " 个,SDK " + SdkInt + ":");
            foreach (var d in deviceDecoders)
                Debug.Log("  " + (d.encoder ? "编码 " : "解码 ") + (d.hardware ? "硬 " : "软 ") + d.name);
        }
        catch (Exception e) { Debug.LogWarning("[DecodeProbe] 枚举 MediaCodecList 失败:" + e.Message); }
    }

    /// <summary>isHardwareAccelerated/isSoftwareOnly 是 API 29 才有的,低版本调会抛异常。</summary>
    static bool? TryBool(AndroidJavaObject o, string method)
    {
        if (SdkInt < 29) return null;
        try { return CallBool(o, method); }
        catch (Exception) { return null; }
    }

    static bool CallBool(AndroidJavaObject o, string method)
    {
        try { return o.Call<bool>(method); }
        catch (Exception) { return false; }
    }
#endif

    static bool LooksSoftware(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        return n.StartsWith("c2.android.") || n.StartsWith("omx.google.")
            || n.Contains(".sw.") || n.EndsWith("_sw") || n.Contains("swiftshader");
    }

    static DecoderInfo Pick(string mime, bool wantHardware)
    {
        foreach (var d in Decoders)
        {
            if (d.encoder) continue;
            if (d.hardware != wantHardware) continue;
            if (Supports(d, mime)) return d;
        }
        return new DecoderInfo();
    }

    /// <summary>
    /// 名字里带 AV/H264、名字里带 HEVC/H265 —— 这是不查 getSupportedTypes() 的近似判断。
    /// 查类型要再套一层数组遍历,而 AV1 的名字各家写法太散,宁可在这里保守匹配、
    /// 上面再按枚举结果数量给"整机支持数",不让单个错名决定路线。
    /// </summary>
    static bool Supports(DecoderInfo d, string mime)
    {
        if (string.IsNullOrEmpty(d.name)) return false;
        string n = d.name.ToUpperInvariant();
        if (mime == MimeH264) return n.Contains("AVC") || n.Contains("H264");
        if (mime == MimeH265) return n.Contains("HEVC") || n.Contains("H265");
        if (mime == MimeVp9) return n.Contains("VP9");
        if (mime == MimeAv1) return n.Contains("AV1");
        if (mime == "video/x-vnd.on2.vp8") return n.Contains("VP8");
        return false;
    }
}
