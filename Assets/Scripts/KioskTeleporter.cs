using UnityEngine;
using Unity.XR.CoreUtils;

/// <summary>
/// 智能图书馆 · 点击按钮直接传送玩家。
/// 挂在中央导览台上,由 UI 按钮(如"进入图书馆")调用 TeleportToDestination()。
/// 会自动补偿头显相机在 Rig 内的位置/朝向偏移,让"人"而不是 Rig 原点落到目标点。
/// </summary>
public class KioskTeleporter : MonoBehaviour
{
    [Tooltip("玩家的 XROrigin(XR Interaction Setup 下)")]
    public XROrigin origin;

    [Tooltip("传送目标点(空物体,其 forward 即玩家落地后的朝向)")]
    public Transform destination;

    [Tooltip("勾上=落地高度沿用玩家当前值(同层传送)。跨层必须取消勾选,否则传到 3 m 高的二楼板面会把人落在板子里。")]
    public bool keepRigHeight = true;

    /// <summary>点击按钮调用:传送到 destination。</summary>
    public void TeleportToDestination()
    {
        if (origin == null || destination == null || origin.Camera == null)
        {
            Debug.LogWarning("[KioskTeleporter] 未设置 origin 或 destination,无法传送。");
            return;
        }

        var cam = origin.Camera.transform;

        // 1. 先旋转 Rig:使相机的世界水平朝向 = destination 的朝向
        float camYaw = cam.eulerAngles.y;
        float destYaw = destination.eulerAngles.y;
        float delta = Mathf.DeltaAngle(camYaw, destYaw);
        origin.transform.Rotate(0f, delta, 0f);

        // 2. 再平移 Rig:旋转后再计算相机偏移,让"相机"落到目标点
        Vector3 camOffset = cam.position - origin.transform.position;
        camOffset.y = 0f;
        Vector3 target = destination.position;
        origin.transform.position = new Vector3(target.x - camOffset.x,
            keepRigHeight ? origin.transform.position.y : target.y,
            target.z - camOffset.z);

        Debug.Log($"[KioskTeleporter] 已传送到 {destination.name}({target})");
    }
}
