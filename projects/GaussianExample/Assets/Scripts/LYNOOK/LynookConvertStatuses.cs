using System;

namespace Lynook.DualScreen
{
    /// <summary>
    /// scenes 表 convert_status 取值常量：C# 批量录制状态回写与
    /// scene-update.mjs（Tools/schemas/scene-update.schema.json）共用的唯一来源。
    /// </summary>
    public static class LynookConvertStatuses
    {
        public const string Ready = "ready";
        public const string Failed = "failed";

        public static readonly string[] All = { Ready, Failed };

        public static bool IsValid(string value) =>
            !string.IsNullOrEmpty(value) && Array.IndexOf(All, value) >= 0;
    }
}
