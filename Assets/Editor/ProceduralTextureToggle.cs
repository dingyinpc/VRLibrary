using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 程序化贴图 一键应用/移除 开关。
/// 「应用」= 给 4 个基础材质挂程序化纹理(砖缝地板/拉丝墙板/微噪书架/拉丝金属),纹理不存在则先生成;
/// 「移除」= 清空 _BaseMap 恢复纯色(纹理资产保留在 Assets/Materials,随时可再应用)。
/// 用途:A/B 对比决定是否采用程序化贴图。
/// </summary>
public static class ProceduralTextureToggle
{
    const string kTexDir = "Assets/Materials";

    [MenuItem("VRLibrary/程序化贴图/应用全部")]
    public static void ApplyAll()
    {
        SetAll(true);
        Debug.Log("[程序化贴图] 已应用:地板砖缝 / 墙面拉丝 / 书架微噪 / 金属拉丝。");
    }

    [MenuItem("VRLibrary/程序化贴图/移除全部(恢复纯色)")]
    public static void RemoveAll()
    {
        SetAll(false);
        Debug.Log("[程序化贴图] 已移除,材质恢复纯色。纹理资产仍保留在 " + kTexDir + "。");
    }

    static void SetAll(bool apply)
    {
        int n = 0;
        n += Apply("Assets/Materials/M_Floor.mat", "T_FloorNoise", new Vector2(10, 10), apply, 0.3f);
        n += Apply("Assets/Materials/M_Wall.mat", "T_WallStreak", new Vector2(10, 1), apply, 0.25f);
        n += Apply("Assets/Materials/M_Shelf.mat", "T_ShelfNoise", new Vector2(2, 1), apply, 0.2f);
        n += Apply("Assets/Materials/M_Metal.mat", "T_MetalBrush", new Vector2(1, 3), apply, 0.55f);
        AssetDatabase.SaveAssets();
        Debug.Log("[程序化贴图] 处理材质数:" + n);
    }

    static int Apply(string matPath, string texName, Vector2 tiling, bool apply, float fallbackSmoothness)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null) { Debug.LogWarning("[程序化贴图] 找不到 " + matPath); return 0; }
        if (apply) {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(kTexDir + "/" + texName + ".asset");
            if (tex == null) tex = Generate(texName, texName);
            mat.SetTexture("_BaseMap", tex);
            mat.SetTextureScale("_BaseMap", tiling);
        } else {
            mat.SetTexture("_BaseMap", null);
            mat.SetTextureScale("_BaseMap", Vector2.one);
            mat.SetFloat("_Smoothness", fallbackSmoothness);
        }
        EditorUtility.SetDirty(mat);
        return 1;
    }

    /// <summary>程序化生成:底色 + 噪声(+ 可选砖缝 / 竖向拉丝)。确定性种子,重复生成结果一致。</summary>
    static Texture2D Generate(string name, string variant)
    {
        int size = 512;
        Color baseCol; float noise; int gridEvery = 0; float gridDark = 1f; bool streaks = false;
        if (name == "T_FloorNoise") { baseCol = new Color(0.05f, 0.06f, 0.08f); noise = 0.10f; gridEvery = 128; gridDark = 0.72f; }
        else if (name == "T_WallStreak") { baseCol = new Color(0.07f, 0.08f, 0.10f); noise = 0.06f; streaks = true; }
        else if (name == "T_ShelfNoise") { baseCol = new Color(0.10f, 0.11f, 0.13f); noise = 0.07f; size = 256; }
        else { baseCol = new Color(0.12f, 0.13f, 0.16f); noise = 0.04f; size = 256; streaks = true; }

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        var rand = new System.Random(7);
        var colNoise = new float[size];
        for (int x = 0; x < size; x++) colNoise[x] = ((x * 7919) % 97) / 97f - 0.5f;
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float d = (float)(rand.NextDouble() - 0.5) * noise;
                float st = streaks ? colNoise[x] * noise * 0.7f : 0f;
                float r = baseCol.r + d + st, g = baseCol.g + d + st, b = baseCol.b + d + st;
                if (gridEvery > 0 && (x % gridEvery < 2 || y % gridEvery < 2)) { r *= gridDark; g *= gridDark; b *= gridDark; }
                tex.SetPixel(x, y, new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f));
            }
        }
        tex.Apply();
        AssetDatabase.CreateAsset(tex, kTexDir + "/" + name + ".asset");
        return tex;
    }
}
