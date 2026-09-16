// ============================================================
// CyberNightSkySunSync — 天空盒月亮与"太阳"灯光自动对齐
//
// 作用: 让天空盒("Skybox/CyberNight")中月亮的方位始终与
// RenderSettings.sun(即在 Lighting 窗口里设为 Sun 的 Directional
// Light)保持一致 —— 月亮位于光源反方向(即光线来向)。
// 这样 天空月亮 / 平行光方向 / 阴影方向 三者永远对应,
// 避免"月亮在东边、阴影却朝西边"的穿帮(光影对应)。
//
// 仅在编辑器(Game/Scene 视图)内生效, 运行时不消耗任何性能。
// ============================================================
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace VRLibrary
{
    [InitializeOnLoad]
    public static class CyberNightSkySunSync
    {
        const string kShaderName = "Skybox/CyberNight";
        static readonly int kMoonDirId = Shader.PropertyToID("_MoonDir");

        static CyberNightSkySunSync()
        {
            EditorApplication.update += SyncMoonWithSun;
        }

        static void SyncMoonWithSun()
        {
            // 没有太阳 / 没有使用本天空盒时直接返回, 零开销
            Light sun = RenderSettings.sun;
            if (sun == null) return;

            Material sky = RenderSettings.skybox;
            if (sky == null || sky.shader == null || sky.shader.name != kShaderName) return;

            // 月亮位于太阳光线的反方向(即光源所在方向)
            Vector3 moonDir = -sun.transform.forward;
            Vector4 current = sky.GetVector(kMoonDirId);
            if ((new Vector3(current.x, current.y, current.z) - moonDir).sqrMagnitude > 1e-8f)
            {
                sky.SetVector(kMoonDirId, new Vector4(moonDir.x, moonDir.y, moonDir.z, 0f));
                EditorUtility.SetDirty(sky);
            }
        }
    }
}
#endif