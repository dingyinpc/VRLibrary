using UnityEditor;
using UnityEngine;

// 域重载探针:验证 [InitializeOnLoad] 是否运行、AutoStart 开关是否生效。可随时删除。
[InitializeOnLoad]
static class ReloadProbe
{
    static ReloadProbe()
    {
        string msg = "<RELOAD_PROBE> reload at " + System.DateTime.Now.ToString("HH:mm:ss")
            + " AutoStart=" + EditorPrefs.GetBool("MCPForUnity.AutoStartOnLoad", false)
            + " UseHttp=" + EditorPrefs.GetBool("MCPForUnity.UseHttpTransport", false);
        Debug.Log(msg);
    }
}
