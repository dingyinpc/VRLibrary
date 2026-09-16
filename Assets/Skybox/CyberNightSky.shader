// ============================================================
// CyberNightSky — 赛博之夜 程序化天空盒 (URP / Built-in 通用)
// 特点:
//   · 纯过程化(无贴图), 永无接缝, 移动端 VR 友好(约 150 ALU)
//   · HDR 输出, 与 URP Bloom 泛光配合(月光/霓虹地平线会发光)
//   · 月亮方向 _MoonDir 与场景 Directional Light(太阳)自动对齐:
//     由 Assets/Editor/SkyboxSunSync.cs 在编辑器里保持同步,
//     保证“天空月亮 = 光线来源方向 = 阴影方向”三者一致(光影对应)。
//   · 可调: 天顶/地平线/地面三色、地平线青色辉光、品红地平线
//     (synthwave) 细线、带光晕的月亮、双层星光、紫色星云。
// ============================================================
Shader "Skybox/CyberNight"
{
    Properties
    {
        [Header(Sky Gradient)]
        _Tint            ("Tint", Color) = (1, 1, 1, 1)
        _ZenithColor     ("Zenith Color", Color) = (0.010, 0.020, 0.055, 1)
        _HorizonColor    ("Horizon Color", Color) = (0.020, 0.075, 0.095, 1)
        _GroundColor     ("Ground Color", Color) = (0.013, 0.021, 0.032, 1)
        _HorizonGlow     ("Horizon Glow Strength", Range(0, 3)) = 1.4
        _HorizonGlowColor("Horizon Glow Color", Color) = (0.35, 0.90, 1.05, 1)
        _HorizonFalloff  ("Horizon Falloff", Range(0.5, 8)) = 3.0

        [Header(Synthwave Horizon Line)]
        _HorizonLineColor     ("Horizon Line Color", Color) = (0.85, 0.18, 1.05, 1)
        _HorizonLineWidth     ("Horizon Line Width", Range(0.002, 0.06)) = 0.014
        _HorizonLineIntensity ("Horizon Line Intensity", Range(0, 3)) = 1.2

        [Header(Moon)]
        _MoonDir       ("Moon Direction (world)", Vector) = (0.329, 0.819, -0.47, 0)
        _MoonColor     ("Moon Color", Color) = (0.80, 0.88, 1.00, 1)
        _MoonIntensity ("Moon Intensity", Float) = 2.6
        _MoonSize      ("Moon Size (rad)", Range(0.005, 0.12)) = 0.045
        _MoonSoftness  ("Moon Edge Softness", Range(0.001, 0.05)) = 0.014
        _MoonHalo      ("Moon Halo", Range(0, 2)) = 0.9

        [Header(Stars)]
        _StarScale      ("Star Grid Scale", Range(20, 400)) = 110
        _StarDensity    ("Star Density", Range(0.02, 0.6)) = 0.30
        _StarBrightness ("Star Brightness", Float) = 1.4

        [Header(Nebula)]
        _NebulaIntensity ("Nebula Intensity", Range(0, 1)) = 0.22
        _NebulaColor     ("Nebula Color", Color) = (0.42, 0.22, 0.65, 1)
        _NebulaScale     ("Nebula Scale", Range(0.5, 4)) = 1.2
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float4 _ZenithColor;
                float4 _HorizonColor;
                float4 _GroundColor;
                float  _HorizonGlow;
                float4 _HorizonGlowColor;
                float  _HorizonFalloff;
                float4 _HorizonLineColor;
                float  _HorizonLineWidth;
                float  _HorizonLineIntensity;
                float4 _MoonDir;
                float4 _MoonColor;
                float  _MoonIntensity;
                float  _MoonSize;
                float  _MoonSoftness;
                float  _MoonHalo;
                float  _StarScale;
                float  _StarDensity;
                float  _StarBrightness;
                float  _NebulaIntensity;
                float4 _NebulaColor;
                float  _NebulaScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dir        : TEXCOORD0;   // 天空盒立方体局部方向 == 世界方向
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.dir = input.positionOS.xyz;
                return o;
            }

            // ---------- 哈希与噪声 ----------
            float2 hash22(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.xx + p3.yx) * p3.zy);
            }

            float hash12(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float3 hash13(float3 p3)
            {
                p3 = frac(p3 * 0.1031);
                p3 += dot(p3, p3.zyx + 31.32);
                return frac((p3.xxy + p3.yxx) * p3.zyx);
            }

            // 值噪声(1 阶, 三线性插值)
            float valueNoise(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash13(i + float3(0, 0, 0)).x;
                float n100 = hash13(i + float3(1, 0, 0)).x;
                float n010 = hash13(i + float3(0, 1, 0)).x;
                float n110 = hash13(i + float3(1, 1, 0)).x;
                float n001 = hash13(i + float3(0, 0, 1)).x;
                float n101 = hash13(i + float3(1, 0, 1)).x;
                float n011 = hash13(i + float3(0, 1, 1)).x;
                float n111 = hash13(i + float3(1, 1, 1)).x;
                float x00 = lerp(n000, n100, f.x);
                float x10 = lerp(n010, n110, f.x);
                float x01 = lerp(n001, n101, f.x);
                float x11 = lerp(n011, n111, f.x);
                float y0 = lerp(x00, x10, f.y);
                float y1 = lerp(x01, x11, f.y);
                return lerp(y0, y1, f.z);
            }

            // ---------- 星光: 基于方向的三维哈希网格, 无接缝 ----------
            float3 stars(float3 d)
            {
                float3 cell = floor(d * _StarScale);
                float3 fv = frac(d * _StarScale) - 0.5;

                // 用 3D 格子折叠成 2D 键, 保证不同面不重复
                float2 cellKey = cell.xy + cell.z * 1973.0;

                // 该格子是否出现星星(概率 = _StarDensity)
                float r = hash12(cellKey + 3.7);
                float has = step(1.0 - _StarDensity, r);

                // 星星在格子内的随机位置与大小
                float2 jit = (hash22(cellKey + 9.1) - 0.5) * 0.85;
                float dist = length(fv.xy - jit);
                float size = lerp(0.055, 0.15, hash12(cellKey + 15.3));

                // 软边缘圆点(高斯点状星, 无需贴图)
                float core = exp(-dist * dist * (50.0 + (1.0 - size) * 50.0));
                float star = has * core;

                // 亮度与颜色: 大部分冷白, 少量暖色 / 品红点缀
                float bright = lerp(0.45, 1.0, hash12(cellKey + 21.7));
                float warm = step(0.86, hash12(cellKey + 27.1));
                float magenta = step(0.965, hash12(cellKey + 33.3));
                float3 baseCol = lerp(float3(0.72, 0.82, 1.0), float3(1.0, 0.88, 0.72), warm);
                baseCol = lerp(baseCol, float3(1.0, 0.55, 0.95), magenta);

                return baseCol * star * bright * _StarBrightness;
            }

            // ---------- 星云: 双倍频率值噪声叠加 ----------
            float nebula(float3 d)
            {
                float n = valueNoise(d * 1.3 * _NebulaScale) * 0.62
                        + valueNoise(d * 3.1 * _NebulaScale) * 0.38;
                return smoothstep(0.35, 0.78, n);
            }

            // ---------- 主函数 ----------
            float4 frag(Varyings i) : SV_Target
            {
                float3 d = normalize(i.dir);

                // 1) 基础天空渐变(天顶深蓝 → 地平线青绿 → 地面暗蓝灰)
                float3 skyCol = (d.y >= 0.0)
                    ? lerp(_HorizonColor.rgb, _ZenithColor.rgb, pow(saturate(d.y / 0.7), 1.4))
                    : lerp(_HorizonColor.rgb, _GroundColor.rgb, pow(saturate(-d.y / 0.4), 0.9));

                // 2) 地平线青色辉光(上下对称, 像城市夜空光晕)
                float horiz = exp(-abs(d.y) * _HorizonFalloff);
                skyCol += _HorizonGlowColor.rgb * _HorizonGlow * horiz;

                // 3) 品红色地平线细线(synthwave 味)
                float horizonLine = saturate(1.0 - abs(d.y) / _HorizonLineWidth);
                skyCol += _HorizonLineColor.rgb * (horizonLine * horizonLine) * _HorizonLineIntensity;

                // 4) 紫色星云
                float nb = nebula(d);
                skyCol += _NebulaColor.rgb * nb * _NebulaIntensity;

                // 5) 星光(地平线附近淡出)
                float3 starCol = stars(d);
                float starMask = smoothstep(-0.08, 0.15, d.y);
                skyCol += starCol * starMask;

                // 6) 月亮(盘面 + 辉光, HDR 参与泛光)
                float3 moonDir = normalize(_MoonDir.xyz);
                float angDist = acos(saturate(dot(d, moonDir)));
                float disc = 1.0 - smoothstep(_MoonSize - _MoonSoftness, _MoonSize + _MoonSoftness, angDist);
                float halo = saturate(1.0 - angDist / 0.45);
                skyCol += _MoonColor.rgb * _MoonIntensity * (disc + halo * halo * _MoonHalo * 0.35);

                // 月亮周围星光淡出(避免星点贴着月亮很杂乱)
                float moonMask = smoothstep(0.5, 0.22, angDist);
                skyCol -= starCol * starMask * (1.0 - moonMask);

                skyCol *= _Tint.rgb;
                return float4(skyCol, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
