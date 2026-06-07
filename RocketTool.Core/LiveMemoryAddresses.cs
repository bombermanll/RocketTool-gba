namespace RocketTool.Core;

public static class LiveMemoryAddresses
{
    public const uint ShopPriceAddress = 0x020051B0;
    public const uint ShopFirstItemAddress = 0x02005274;
    public const uint SellPricePrimaryAddress = 0x030052D8;
    public const uint SellPriceFallbackAddress = 0x020052D8;
}

public sealed record ShopMemorySnapshot(
    ushort ShopPrice,
    ushort ShopFirstItem,
    ushort SellPricePrimary,
    ushort SellPriceFallback);

public static class LiveMemoryProbe
{
    public static ShopMemorySnapshot ReadShopSnapshot(MgbaBridgeClient bridge)
        => new(
            ReadU16(bridge, LiveMemoryAddresses.ShopPriceAddress),
            ReadU16(bridge, LiveMemoryAddresses.ShopFirstItemAddress),
            ReadU16(bridge, LiveMemoryAddresses.SellPricePrimaryAddress),
            ReadU16(bridge, LiveMemoryAddresses.SellPriceFallbackAddress));

    private static ushort ReadU16(MgbaBridgeClient bridge, uint address)
    {
        var data = bridge.Read(address, 2);
        return (ushort)(data[0] | data[1] << 8);
    }
}
