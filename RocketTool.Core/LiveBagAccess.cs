namespace RocketTool.Core;

public sealed record BagSlot(uint Address, ushort ItemId, ushort Quantity, string Note);
public sealed record BagPocketDefinition(int Pocket, string Name, uint Offset, int SlotCount, bool QuantityXor, ushort QuantityKey);

public static class LiveBagAccess
{
    public static uint ResolveBaseAddress(MgbaBridgeClient bridge, GameProfile profile)
    {
        var bag = profile.Runtime.LiveBag;
        if (bag.Mode == "fixed") return bag.BaseAddress;
        if (bag.Mode != "save-block1")
            throw new InvalidOperationException($"当前 Profile 的实时背包模式 {bag.Mode} 没有固定基址。");

        var pointerBytes = bridge.Read(profile.Memory.SaveBlock1PointerAddress, 4);
        var baseAddress = ReadU32(pointerBytes, 0);
        var requiredLength = bag.Areas.Max(area =>
            area.Offset + checked((uint)area.Capacity * 4U));
        var ewramEnd = profile.Memory.EwramBase + checked((uint)profile.Memory.EwramSize);
        if (baseAddress < profile.Memory.EwramBase ||
            (ulong)baseAddress + requiredLength > ewramEnd)
        {
            throw new InvalidOperationException(
                $"SaveBlock1 指针 0x{profile.Memory.SaveBlock1PointerAddress:X8} -> 0x{baseAddress:X8} " +
                "没有覆盖当前 Profile 的完整实时背包区域。");
        }
        return baseAddress;
    }

    public static uint ResolveAreaAddress(
        MgbaBridgeClient bridge,
        GameProfile profile,
        GameProfileLiveBagArea area)
        => ResolveAreaAddress(profile, area, ResolveBaseAddress(bridge, profile));

    public static uint ResolveAreaAddress(
        GameProfile profile,
        GameProfileLiveBagArea area,
        uint baseAddress)
        => profile.Runtime.LiveBag.Mode switch
        {
            "fixed" => area.Address,
            "save-block1" => checked(baseAddress + area.Offset),
            _ => throw new InvalidOperationException(
                $"当前 Profile 的实时背包模式 {profile.Runtime.LiveBag.Mode} 没有固定口袋区域。")
        };

    public static ushort DecodeQuantity(
        GameProfile profile,
        ushort itemId,
        ushort rawQuantity,
        ushort quantityKey,
        bool isKeyItem)
    {
        if (itemId == 0) return 0;
        if (isKeyItem) return 1;
        var decoded = (ushort)(rawQuantity ^ quantityKey);
        if (profile.Runtime.LiveBag.QuantityEncoding != "minus-one-xor")
            return decoded == 0 ? (ushort)1 : decoded;
        if (decoded == ushort.MaxValue)
            throw new InvalidOperationException("实时背包数量解码溢出，已拒绝使用该槽位。");
        return (ushort)(decoded + 1);
    }

    public static byte[] EncodeSlot(
        GameProfile profile,
        ushort itemId,
        ushort quantity,
        ushort quantityKey,
        bool isKeyItem)
    {
        ushort rawQuantity;
        if (itemId == 0)
        {
            rawQuantity = profile.Runtime.LiveBag.QuantityEncoding == "minus-one-xor"
                ? quantityKey
                : (ushort)0;
        }
        else if (isKeyItem)
        {
            rawQuantity = 0;
        }
        else
        {
            if (quantity == 0)
                throw new InvalidOperationException("非空背包道具数量必须大于 0。");
            var plain = profile.Runtime.LiveBag.QuantityEncoding == "minus-one-xor"
                ? (ushort)(quantity - 1)
                : quantity;
            rawQuantity = (ushort)(plain ^ quantityKey);
        }

        return
        [
            (byte)itemId,
            (byte)(itemId >> 8),
            (byte)rawQuantity,
            (byte)(rawQuantity >> 8)
        ];
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] |
                  data[offset + 1] << 8 |
                  data[offset + 2] << 16 |
                  data[offset + 3] << 24);
}
