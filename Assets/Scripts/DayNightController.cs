using UnityEngine;

/// <summary>
/// 智能图书馆 · 灯光开关 + 日夜切换
/// - ToggleLights():室内人工照明总开关(非平行光的灯全部关/开 + 灯带/吊顶光格自发光同步熄灭/点亮,
///   霓虹装饰条不受影响,熄灯后剩"应急霓虹"氛围);
/// - ToggleDayNight():白天/黑夜环境切换(天空盒三色+星星+星云+月亮、环境光、雾、主光强度全套切换)。
/// 挂在中央导览台上,由导览屏两个按钮调用;夜晚参数在 Awake 自动缓存(以场景原始状态为准)。
/// </summary>
public class DayNightController : MonoBehaviour
{
    public static DayNightController Instance;

    [Header("灯带/吊顶光格材质(开关灯时熄灭/点亮)")]
    public Material lightBarMat;
    [Header("灯带点亮时的自发光颜色(HDR)")]
    public Color lightBarOn = new Color(1.6f, 1.8f, 2.2f);
    public Color lightBarOff = new Color(0.04f, 0.05f, 0.07f);

    public bool lightsOn = true;
    public bool isDay = false;

    // 夜晚缓存
    Color nAmbient, nFogCol, nSunCol;
    float nFogDen, nSunI;
    Color nZenith, nHorizon, nGlowCol, nMoonCol, nLineCol;
    float nGlow, nStars, nStarB, nNebula, nMoonSize, nMoonI;
    Material sky;

    void Awake()
    {
        Instance = this;
        sky = RenderSettings.skybox;
        nAmbient = RenderSettings.ambientLight;
        nFogCol = RenderSettings.fogColor;
        nFogDen = RenderSettings.fogDensity;
        var sun = RenderSettings.sun;
        if (sun != null) { nSunI = sun.intensity; nSunCol = sun.color; }
        if (sky != null) {
            nZenith = sky.GetColor("_ZenithColor");
            nHorizon = sky.GetColor("_HorizonColor");
            nGlowCol = sky.GetColor("_HorizonGlowColor");
            nGlow = sky.GetFloat("_HorizonGlow");
            nStars = sky.GetFloat("_StarDensity");
            nStarB = sky.GetFloat("_StarBrightness");
            nNebula = sky.GetFloat("_NebulaIntensity");
            nMoonSize = sky.GetFloat("_MoonSize");
            nMoonI = sky.GetFloat("_MoonIntensity");
            nMoonCol = sky.GetColor("_MoonColor");
            nLineCol = sky.GetColor("_HorizonLineColor");
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>室内灯光总开关(全部非平行光灯 + 灯带自发光)。</summary>
    public void ToggleLights()
    {
        lightsOn = !lightsOn;
        var lights = FindObjectsOfType<Light>();
        foreach (var l in lights)
            if (l.type != LightType.Directional) l.enabled = lightsOn;
        if (lightBarMat != null)
        {
            lightBarMat.SetColor("_EmissionColor", lightsOn ? lightBarOn : lightBarOff);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(lightBarMat);
#endif
        }
        Debug.Log("[DayNight] 室内灯光 = " + (lightsOn ? "开" : "关"));
    }

    /// <summary>白天/黑夜切换。</summary>
    public void ToggleDayNight()
    {
        isDay = !isDay;
        if (isDay) ApplyDay(); else ApplyNight();
        Debug.Log("[DayNight] 当前时段 = " + (isDay ? "白天" : "夜晚"));
    }

    void ApplyDay()
    {
        RenderSettings.ambientLight = new Color(0.42f, 0.50f, 0.60f);
        RenderSettings.fogColor = new Color(0.55f, 0.65f, 0.78f);
        RenderSettings.fogDensity = 0.005f;
        var sun = RenderSettings.sun;
        if (sun != null) { sun.intensity = 1.2f; sun.color = new Color(1f, 0.97f, 0.9f); }
        if (sky != null)
        {
            sky.SetColor("_ZenithColor", new Color(0.30f, 0.52f, 0.85f));
            sky.SetColor("_HorizonColor", new Color(0.62f, 0.80f, 0.95f));
            sky.SetColor("_HorizonGlowColor", new Color(1f, 1f, 1f));
            sky.SetFloat("_HorizonGlow", 0.4f);
            sky.SetFloat("_StarDensity", 0f);
            sky.SetFloat("_StarBrightness", 0f);
            sky.SetFloat("_NebulaIntensity", 0f);
            sky.SetFloat("_MoonSize", 0.06f);
            sky.SetFloat("_MoonIntensity", 4f);
            sky.SetColor("_MoonColor", new Color(1f, 0.98f, 0.9f));
        }
    }

    void ApplyNight()
    {
        RenderSettings.ambientLight = nAmbient;
        RenderSettings.fogColor = nFogCol;
        RenderSettings.fogDensity = nFogDen;
        var sun = RenderSettings.sun;
        if (sun != null) { sun.intensity = nSunI; sun.color = nSunCol; }
        if (sky != null)
        {
            sky.SetColor("_ZenithColor", nZenith);
            sky.SetColor("_HorizonColor", nHorizon);
            sky.SetColor("_HorizonGlowColor", nGlowCol);
            sky.SetFloat("_HorizonGlow", nGlow);
            sky.SetFloat("_StarDensity", nStars);
            sky.SetFloat("_StarBrightness", nStarB);
            sky.SetFloat("_NebulaIntensity", nNebula);
            sky.SetFloat("_MoonSize", nMoonSize);
            sky.SetFloat("_MoonIntensity", nMoonI);
            sky.SetColor("_MoonColor", nMoonCol);
        }
    }
}
