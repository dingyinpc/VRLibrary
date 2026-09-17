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
        sky = RenderSettings.skybox;
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

    void OnDestroy() { if (Instance == this) Instance = null; }

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
        blending = StartCoroutine(Blend(isDay ? 0f : 1f, isDay ? 1f : 0f));
        Debug.Log("[DayNight] 切换 → " + (isDay ? "白天" : "夜晚") + "(渐变 " + blendSeconds + "s)");
    }

    /// <summary>nightT: 0=白天 1=夜晚,从当前状态渐变到目标。</summary>
    IEnumerator Blend(float from, float to)
    {
        // 记住起点实际值(渐变中途再切也平滑)
        Color sAmb = RenderSettings.ambientLight, sFogC = RenderSettings.fogColor, sSunC = nSunC;
        float sSunI = nSunI;
        var sun = RenderSettings.sun;
        if (sun != null) { sSunI = sun.intensity; sSunC = sun.color; }
        float sFogD = RenderSettings.fogDensity;
        float t = 0f;
        while (t < 1f)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / Mathf.Max(0.05f, blendSeconds));
            float k = Mathf.SmoothStep(from, to, t);
            RenderSettings.ambientLight = Color.Lerp(dAmb, nAmb, k);
            RenderSettings.fogColor = Color.Lerp(dFogC, nFogC, k);
            RenderSettings.fogDensity = Mathf.Lerp(dFogD, nFogD, k);
            if (sun != null)
            {
                sun.intensity = Mathf.Lerp(dSunI, nSunI, k);
                sun.color = Color.Lerp(dSunC, nSunC, k);
            }
            if (sky != null)
            {
                sky.SetColor("_ZenithColor", Color.Lerp(dZen, nZen, k));
                sky.SetColor("_HorizonColor", Color.Lerp(dHor, nHor, k));
                sky.SetColor("_HorizonGlowColor", Color.Lerp(dGlowC, nGlowC, k));
                sky.SetFloat("_HorizonGlow", Mathf.Lerp(dGlow, nGlow, k));
                sky.SetFloat("_StarDensity", Mathf.Lerp(dStars, nStars, k));
                sky.SetFloat("_StarBrightness", Mathf.Lerp(dStarB, nStarB, k));
                sky.SetFloat("_NebulaIntensity", Mathf.Lerp(dNeb, nNeb, k));
                sky.SetFloat("_MoonSize", Mathf.Lerp(dMoonS, nMoonS, k));
                sky.SetFloat("_MoonIntensity", Mathf.Lerp(dMoonI, nMoonI, k));
                sky.SetColor("_MoonColor", Color.Lerp(dMoonC, nMoonC, k));
            }
            yield return null;
        }
        blending = null;
    }
}
