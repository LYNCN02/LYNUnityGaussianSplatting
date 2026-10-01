using System;

namespace Lynook.DualScreen
{
    /// <summary>
    /// 房间类型常量：World Studio 下拉框、world_draft.json、scene-queue 契约
    /// （Tools/schemas/scene-queue.schema.json 的 roomType enum）共用的唯一来源。
    /// 新增类型时只需在此处追加，并同步 schema 枚举。
    /// </summary>
    public static class LynookRoomTypes
    {
        public const string Bedroom = "bedroom";
        public const string LivingRoom = "living_room";
        public const string Kitchen = "kitchen";
        public const string Bathroom = "bathroom";
        public const string DiningRoom = "dining_room";
        public const string Office = "office";
        public const string Study = "study";
        public const string Balcony = "balcony";
        public const string Corridor = "corridor";
        public const string Other = "other";

        /// <summary>全部合法房间类型，顺序即 UI 下拉顺序。</summary>
        public static readonly string[] All =
        {
            Bedroom, LivingRoom, Kitchen, Bathroom, DiningRoom,
            Office, Study, Balcony, Corridor, Other,
        };

        public static bool IsValid(string value) =>
            !string.IsNullOrEmpty(value) && Array.IndexOf(All, value) >= 0;

        /// <summary>空值或非法值一律归一化为 <see cref="Bedroom"/>，与既有导入行为一致。</summary>
        public static string Normalize(string value) => IsValid(value) ? value : Bedroom;
    }
}
