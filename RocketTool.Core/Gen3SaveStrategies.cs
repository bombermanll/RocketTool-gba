namespace RocketTool.Core;

internal interface IGen3SaveStrategy
{
    string Id { get; }
    Gen3SaveDocument Open(string path, GameProfile profile);
    void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes);
    void ReplaceTrainerMoney(Gen3SaveDocument document, uint money);
    void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data);
    ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey);
    IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket);
    int? PocketOfItem(Gen3SaveDocument document, ushort itemId);
    string PocketName(int pocket);
    void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data);
    ushort ReadBagU16(Gen3SaveDocument document, int saveOffset);
}

internal static class Gen3SaveStrategyCatalog
{
    public static IGen3SaveStrategy ForProfile(GameProfile profile)
        => profile.Strategies.Save switch
        {
            SpanishRocketSaveStrategy.StrategyId => SpanishRocketSaveStrategy.Instance,
            UnboundCfruSaveStrategy.StrategyId => UnboundCfruSaveStrategy.Instance,
            DestinyFireRedSaveStrategy.StrategyId => DestinyFireRedSaveStrategy.Instance,
            RadicalRedCfruSaveStrategy.StrategyId => RadicalRedCfruSaveStrategy.Instance,
            DisabledSaveStrategy.StrategyId => DisabledSaveStrategy.Instance,
            var id => throw new NotSupportedException($"当前程序不支持存档策略：{id}")
        };
}
