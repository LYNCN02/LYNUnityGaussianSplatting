using System;

namespace Lynook.DualScreen
{
    /// <summary>
    /// 活动点类型常量：LYNOOKActivityPoint.type、World Studio 类型下拉框、
    /// 导出 world_config.json 的 activityPoints[].type 共用的唯一来源。
    /// </summary>
    public static class LynookActivityTypes
    {
        public const string Stand = "stand";
        public const string Sit = "sit";
        public const string Seat = "seat";
        public const string Look = "look";
        public const string Walk = "walk";
        public const string Interact = "interact";

        /// <summary>全部合法活动点类型，顺序即 UI 下拉顺序。</summary>
        public static readonly string[] All =
        {
            Stand, Sit, Seat, Look, Walk, Interact,
        };

        public static bool IsValid(string value) =>
            !string.IsNullOrEmpty(value) && Array.IndexOf(All, value) >= 0;

        /// <summary>空值或非法值回退为 <see cref="Stand"/>，与旧数据的默认类型一致。</summary>
        public static string Normalize(string value) => IsValid(value) ? value : Stand;
    }
}
