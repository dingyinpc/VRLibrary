using UnityEngine;

/// <summary>
/// 科幻全息环绕特效:让物体绕中心旋转 + 上下浮动。
/// 用于中央导览台上方的环绕光粒/发光块。可随时在 Inspector 调参。
/// </summary>
public class HoloOrbiter : MonoBehaviour
{
    [Tooltip("环绕半径(米)")]
    public float radius = 0.9f;
    [Tooltip("角速度(度/秒)")]
    public float speed = 40f;
    [Tooltip("上下浮动幅度(米)")]
    public float bobAmplitude = 0.08f;
    [Tooltip("浮动频率")]
    public float bobSpeed = 2f;
    [Tooltip("相位偏移(多个环绕体错开用)")]
    public float phase = 0f;

    private Vector3 center;
    private float baseY;

    void Start()
    {
        if (transform.parent != null) center = transform.parent.position;
        baseY = transform.position.y;
    }

    void Update()
    {
        if (transform.parent != null) center = transform.parent.position;
        float angle = (phase + Time.time * speed) * Mathf.Deg2Rad;
        Vector3 p = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        p.y = baseY + Mathf.Sin(Time.time * bobSpeed + phase) * bobAmplitude;
        transform.position = p;
        transform.Rotate(0f, 220f * Time.deltaTime, 0f, Space.Self);
    }
}
