namespace RocketTool.Core;

internal sealed class SpanishRocketSaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "spanish-rocket-save-v1";
    public static SpanishRocketSaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    public Gen3SaveDocument Open(string path, GameProfile profile) => SpanishRocketSaveReader.Open(path, profile);
    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes) => throw new InvalidOperationException("当前存档策略尚未验证训练家名字写回。");
    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money) => throw new InvalidOperationException("当前存档策略尚未验证金钱写回。");

    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data)
        => document.WriteLogicalRange(Gen3SaveDocument.PcFirstSection, Gen3SaveDocument.PcStorageOffset + (globalSlot - 1) * BoxPokemon.Size, data);

    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey) => (ushort)((quantity - 1) ^ quantityKey);

    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket)
    {
        foreach (var physical in Gen3SaveReader.SaveBagPhysicalPockets)
            if (physical.FixedPocket == pocket || physical.FixedPocket is null && pocket is >= 1 and <= 6)
                yield return physical;
    }

    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId) => Gen3SaveReader.SavePocketOfItem(itemId);
    public string PocketName(int pocket) => pocket switch
    {
        1 => "普通道具", 2 => "回复药品", 3 => "精灵球", 4 => "战斗道具",
        5 => "树果", 6 => "宝物", 7 => "招式/秘传机器", 8 => "重要物品", _ => $"口袋{pocket}"
    };
    public void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data) => document.WriteLogicalRange(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset, data);
    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset) => document.ReadLogicalU16(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset);
}
