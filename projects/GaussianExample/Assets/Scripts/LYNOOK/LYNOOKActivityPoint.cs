using UnityEngine;

namespace Lynook.DualScreen
{
    /// <summary>
    /// 挂在每个活动点 GameObject 上，记录点位类型（stand / sit / seat / look 等）。
    /// 导出 world_config.json 时读取该字段写入 activityPoints[].type。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LYNOOKActivityPoint : MonoBehaviour
    {
        public string type = LynookActivityTypes.Stand;
    }
}
