using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 2026-11-09:防眩晕环(隧道遮罩)自动接线。
///
/// 现象:Main Camera/TunnelingVignette 上有 TunnelingVignetteController,
/// 但它的 `locomotionVignetteProviders` 列表是空的 → 转向/移动时永远不会淡入。
/// (XRI 2.6 需要把具体的 LocomotionProvider 一条条加进这个列表才会触发。)
///
/// 本脚本在场景加载后:
///   1) 把默认参数调成"转向时收窄视场"的舒适值;
///   2) 把 Turn(连续/吸附)、Move、Teleport 三类 provider 自动挂到遮罩上。
///
/// 只影响舒适度,不改任何玩法逻辑;不需要改 prefab/场景。
/// </summary>
public class VRLibraryComfort : MonoBehaviour
{
    // 场景内已按用户体验调整过防眩晕环外观(aperture/alpha),默认不再强制覆写数值
    public bool overrideVignetteParameters = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var go = new GameObject("[Comfort]");
        DontDestroyOnLoad(go);
        go.AddComponent<VRLibraryComfort>();
    }

    IEnumerator Start()
    {
        // 等 XR Origin / provider 初始化完
        yield return null;
        yield return null;

        var vig = FindObjectOfType<TunnelingVignetteController>();
        if (vig == null)
        {
            Debug.Log("[Comfort] 没找到 TunnelingVignetteController，跳过眩晕环配置");
            yield break;
        }

        if (overrideVignetteParameters)
        {
            var p = vig.defaultParameters;
            p.apertureSize = 0.70f;
            p.featheringEffect = 0.25f;
            p.easeInTime = 0.15f;
            p.easeOutTime = 0.25f;
            p.easeInTimeLock = false;
            p.easeOutDelayTime = 0f;
            p.vignetteColor = new Color(0f, 0f, 0f, 0.85f);
            p.vignetteColorBlend = Color.black;
            p.apertureVerticalPosition = 0f;
            vig.defaultParameters = p;
        }

        var list = vig.locomotionVignetteProviders;
        var have = new HashSet<LocomotionProvider>();
        foreach (var e in list)
            if (e != null && e.locomotionProvider != null) have.Add(e.locomotionProvider);

        int added = 0;
        foreach (var lp in FindObjectsOfType<LocomotionProvider>(true))
        {
            string n = lp.GetType().Name;
            bool want = n.Contains("Turn") || n.Contains("Move") || n.Contains("Teleport");
            if (!want || have.Contains(lp)) continue;

            var entry = new LocomotionVignetteProvider();
            entry.locomotionProvider = lp;
            entry.enabled = true;
            entry.overrideDefaultParameters = false;
            list.Add(entry);
            added++;
            have.Add(lp);
        }

        var boundNames = new List<string>();
        foreach (var lp in have) boundNames.Add(lp.GetType().Name);
        var dp = vig.defaultParameters;
        Debug.Log("[Comfort] 眩晕环已启用 aperture=" + dp.apertureSize.ToString("0.00")
                  + " feather=" + dp.featheringEffect.ToString("0.00") + "，绑定 " + added
                  + " 个 provider：" + string.Join(", ", boundNames.ToArray()));
    }
}
