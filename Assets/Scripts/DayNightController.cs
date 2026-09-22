using System.Collections;
using UnityEngine;

/// <summary>
/// 智能图书馆 · 灯光开关 + 日夜切换(带渐变)
/// - ToggleLights():室内人工照明总开关(灯带自发光同步,霓虹装饰保留应急氛围);
/// - ToggleDayNight():白天/黑夜渐变切换(约 2.5 秒平滑过渡:天空盒三色/星/月、环境光、雾、主光)。
/// </summary>
public class DayNightController : MonoBehaviour
{
    public static DayNightController Instance;

    [Header("灯带材质")]
    public Material lightBarMat;
    public Color lightBarOn = new Color(1.6f, 1.8f, 2.2f);
    public Color lightBarOff = new Color(0.04f, 0.05f, 0.07f);
    public float blendSeconds = 2.5f;

    public bool lightsOn = true;
    public bool isDay = false;

    // 夜(初始)与昼两组参数 + 当前插值缓存
    Color nAmb, nFogC, nSunC, nZen, nHor, nGlowC, nMoonC;
    float nFogD, nSunI, nGlow, nStars, nStarB, nNeb, nMoonS, nMoonI;
    readonly Color dAmb = new Color(0.42f, 0.50f, 0.60f);
    readonly Color dFogC = new Color(0.55f, 0.65f, 0.78f);
    readonly Color dSunC = new Color(1f, 0.97f, 0.9f);
    readonly Color dZen = new Color(0.30f, 0.52f, 0.85f);
    readonly Color dHor = new Color(0.62f, 0.80f, 0.95f);
    readonly Color dGlowC = new Color(1f, 1f, 1f);
    readonly Color dMoonC = new Color(1f, 0.98f, 0.9f);
    const float dFogD = 0.005f, dSunI = 1.2f, dGlow = 0.4f, dStars = 0f, dStarB = 0f, dNeb = 0f, dMoonS = 0.06f, dMoonI = 4f;

    Material sky;
    Coroutine blending;

    void Awake()
    {
        Instance = this;
        // RenderSettings.skybox 是 .mat **资产本身**,在它上面 SetColor/SetFloat 会直接改磁盘那份,
        // 而 Play 里对资产的修改退出后不回滚(实测跑一次冒烟测试就把天空盒永久留在"白天")。
        // 所以:克隆一份运行时实例挂回去,原始资产永不被动。
        var src = RenderSettings.skybox;
        if (src != null)
        {
            sky = new Material(src) { name = src.name + " (runtime)" };
            RenderSettings.skybox = sky;
        }
        nAmb = RenderSettings.ambientLight;
        nFogC = RenderSettings.fogColor;
        nFogD = RenderSettings.fogDensity;
        var sun = RenderSettings.sun;
        if (sun != null) { nSunI = sun.intensity; nSunC = sun.color; }
        if (sky != null)
        {
            nZen = sky.GetColor("_ZenithColor"); nHor = sky.GetColor("_HorizonColor");
            nGlowC = sky.GetColor("_HorizonGlowColor"); nGlow = sky.GetFloat("_HorizonGlow");
            nStars = sky.GetFloat("_StarDensity"); nStarB = sky.GetFloat("_StarBrightness");
            nNeb = sky.GetFloat("_NebulaIntensity"); nMoonS = sky.GetFloat("_MoonSize");
            nMoonI = sky.GetFloat("_MoonIntensity"); nMoonC = sky.GetColor("_MoonColor");
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (sky != null) Destroy(sky);        // Awake 里克隆的那份运行时实例,别留孤儿
    }

    public void ToggleLights()
    {
        lightsOn = !lightsOn;
        foreach (var l in FindObjectsOfType<Light>())
            if (l.type != LightType.Directional) l.enabled = lightsOn;
        if (lightBarMat != null)
            lightBarMat.SetColor("_EmissionColor", lightsOn ? lightBarOn : lightBarOff);
        Debug.Log("[DayNight] 室内灯光 = " + (lightsOn ? "开" : "关"));
    }

    public void ToggleDayNight()
    {
        isDay = !isDay;
        if (blending != null) StopCoroutine(blending);
        blending = StartCoroutine(Blend(isDay));
        Debug.Log("[DayNight] 切换 → " + (isDay ? "白天" : "夜晚") + "(渐变 " + blendSeconds + "s)");
    }

    /// <summary>渐变:从"当前实际状态"平滑过渡到目标时段(中途再切也无缝)。</summary>
    IEnumerator Blend(bool toDay)
    {
        // 捕捉当前实际值作为起点
        Color sAmb = RenderSettings.ambientLight, sFogC = RenderSettings.fogColor;
        float sFogD = RenderSettings.fogDensity;
        var sun = RenderSettings.sun;
        Color sSunC = sun != null ? sun.color : Color.white;
        float sSunI = sun != null ? sun.intensity : 1f;
        float sZen = 0f;
        var skyX = sky;
        Color cZen = skyX != null ? skyX.GetColor("_ZenithColor") : Color.black;
        Color cHor = skyX != null ? skyX.GetColor("_HorizonColor") : Color.black;
        Color cGlowC = skyX != null ? skyX.GetColor("_HorizonGlowColor") : Color.black;
        float cGlow = skyX != null ? skyX.GetFloat("_HorizonGlow") : 0f;
        float cStars = skyX != null ? skyX.GetFloat("_StarDensity") : 0f;
        float cStarB = skyX != null ? skyX.GetFloat("_StarBrightness") : 0f;
        float cNeb = skyX != null ? skyX.GetFloat("_NebulaIntensity") : 0f;
        float cMoonS = skyX != null ? skyX.GetFloat("_MoonSize") : 0f;
        float cMoonI = skyX != null ? skyX.GetFloat("_MoonIntensity") : 0f;
        Color cMoonC = skyX != null ? skyX.GetColor("_MoonColor") : Color.black;

        // 目标值
        Color tAmb = toDay ? dAmb : nAmb;
        Color tFogC = toDay ? dFogC : nFogC;
        float tFogD = toDay ? dFogD : nFogD;
        Color tSunC = toDay ? dSunC : nSunC;
        float tSunI = toDay ? dSunI : nSunI;
        Color tZen = toDay ? dZen : nZen;
        Color tHor = toDay ? dHor : nHor;
        Color tGlowC = toDay ? dGlowC : nGlowC;
        float tGlow = toDay ? dGlow : nGlow;
        float tStars = toDay ? dStars : nStars;
        float tStarB = toDay ? dStarB : nStarB;
        float tNeb = toDay ? dNeb : nNeb;
        float tMoonS = toDay ? dMoonS : nMoonS;
        float tMoonI = toDay ? dMoonI : nMoonI;
        Color tMoonC = toDay ? dMoonC : nMoonC;

        float t = 0f;
        while (t < 1f)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / Mathf.Max(0.05f, blendSeconds));
            float k = Mathf.SmoothStep(0f, 1f, t);
            RenderSettings.ambientLight = Color.Lerp(sAmb, tAmb, k);
            RenderSettings.fogColor = Color.Lerp(sFogC, tFogC, k);
            RenderSettings.fogDensity = Mathf.Lerp(sFogD, tFogD, k);
            if (sun != null)
            {
                sun.intensity = Mathf.Lerp(sSunI, tSunI, k);
                sun.color = Color.Lerp(sSunC, tSunC, k);
            }
            if (skyX != null)
            {
                skyX.SetColor("_ZenithColor", Color.Lerp(cZen, tZen, k));
                skyX.SetColor("_HorizonColor", Color.Lerp(cHor, tHor, k));
                skyX.SetColor("_HorizonGlowColor", Color.Lerp(cGlowC, tGlowC, k));
                skyX.SetFloat("_HorizonGlow", Mathf.Lerp(cGlow, tGlow, k));
                skyX.SetFloat("_StarDensity", Mathf.Lerp(cStars, tStars, k));
                skyX.SetFloat("_StarBrightness", Mathf.Lerp(cStarB, tStarB, k));
                skyX.SetFloat("_NebulaIntensity", Mathf.Lerp(cNeb, tNeb, k));
                skyX.SetFloat("_MoonSize", Mathf.Lerp(cMoonS, tMoonS, k));
                skyX.SetFloat("_MoonIntensity", Mathf.Lerp(cMoonI, tMoonI, k));
                skyX.SetColor("_MoonColor", Color.Lerp(cMoonC, tMoonC, k));
            }
            yield return null;
        }
        blending = null;
    }
}
