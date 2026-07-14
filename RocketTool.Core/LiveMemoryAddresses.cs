namespace RocketTool.Core;

public sealed record ShopMemorySnapshot(
    ushort ShopPrice,
    ushort ShopFirstItem,
    ushort SellPricePrimary,
    ushort SellPriceFallback);

public static class LiveMemoryProbe
{
    public static ShopMemorySnapshot ReadShopSnapshot(MgbaBridgeClient bridge, GameProfileShopProbe probe)
    {
        if (!probe.Enabled)
            throw new InvalidOperationException("当前 Profile 未验证商店内存探测，已拒绝读取。");

        return new ShopMemorySnapshot(
            ReadU16(bridge, probe.ShopPriceAddress),
            ReadU16(bridge, probe.ShopFirstItemAddress),
            ReadU16(bridge, probe.SellPricePrimaryAddress),
            ReadU16(bridge, probe.SellPriceFallbackAddress));
    }

    private static ushort ReadU16(MgbaBridgeClient bridge, uint address)
    {
        var data = bridge.Read(address, 2);
        return (ushort)(data[0] | data[1] << 8);
    }
}
