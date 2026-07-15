namespace RocketTool.Core;

internal sealed class DestinyFireRedSaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "pokemon-destiny-save-v1";
    internal const int MachineBoxOffset = 0x310;
    internal const int MachineBoxCapacity = 128;
    internal const int BerryPocketExtensionOffset = 0x7D0;
    internal const int BerryPocketCapacity = 75;
    internal const int KeyItemPocketExtensionOffset = 0x8FC;
    internal const int KeyItemPocketCapacity = 60;
    internal const int BallPocketExtensionOffset = 0x9EC;
    internal const int BallPocketCapacity = 28;
    internal const int ItemPocketExtensionOffset = 0xA5C;
    internal const int ItemPocketCapacity = (0xFF4 - ItemPocketExtensionOffset) / 4;
    internal const int SafeBoxSlotCount = 408;
    public static DestinyFireRedSaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    public Gen3SaveDocument Open(string path, GameProfile profile) => PokemonDestinySaveReader.Open(path, profile);
    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes)
    {
        var profile = document.Profile ?? throw new InvalidOperationException("宝可梦命运存档策略需要 Profile。");
        if (document.Snapshot.Trainer is null) throw new InvalidOperationException("当前存档没有可写回的训练家信息。");
        if (nameBytes.Length != profile.Memory.PlayerNameLength)
            throw new InvalidOperationException($"训练家名字编码必须为 {profile.Memory.PlayerNameLength} 字节。");
        document.WriteSectionRange(0, 0, nameBytes);
    }

    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money)
    {
        var profile = document.Profile ?? throw new InvalidOperationException("宝可梦命运存档策略需要 Profile。");
        if (document.Snapshot.Trainer is null) throw new InvalidOperationException("当前存档没有可写回的训练家信息。");
        if (money > profile.Runtime.MaxTrainerMoney)
            throw new InvalidOperationException($"金钱必须在 0..{profile.Runtime.MaxTrainerMoney} 范围内。");

        var encrypted = money ^ document.ReadSectionU32(0, (int)profile.Memory.SaveBlock2EncryptionKeyOffset);
        Span<byte> value = stackalloc byte[4];
        value[0] = (byte)encrypted;
        value[1] = (byte)(encrypted >> 8);
        value[2] = (byte)(encrypted >> 16);
        value[3] = (byte)(encrypted >> 24);
        document.WriteLogicalRange(Gen3SaveDocument.SaveBlock1FirstSection, (int)profile.Memory.SaveBlock1MoneyOffset, value);
    }
    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data)
    {
        if (data.Length != BoxPokemon.Size) throw new InvalidOperationException($"宝可梦命运箱子记录必须为 {BoxPokemon.Size} 字节。");
        var profile = document.Profile ?? throw new InvalidOperationException("宝可梦命运箱子写回需要 Profile。");
        var configuredSlots = profile.Memory.SavePcBoxWritableSlotCount > 0
            ? profile.Memory.SavePcBoxWritableSlotCount
            : SafeBoxSlotCount;
        var maxSlots = Math.Min(configuredSlots, profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots);
        if (globalSlot < 1 || globalSlot > maxSlots) throw new ArgumentOutOfRangeException(nameof(globalSlot));
        document.WriteLogicalRange(Gen3SaveDocument.PcFirstSection, 4 + (globalSlot - 1) * BoxPokemon.Size, data);
    }
    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey) => quantity;
    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket)
    {
        var candidate = pocket switch
        {
            1 => new Gen3SaveBagPhysicalPocket("道具", Gen3SaveDocument.DestinyExtensionOffsetMarker + ItemPocketExtensionOffset, ItemPocketCapacity, 1, true),
            2 => new Gen3SaveBagPhysicalPocket("重要物品", Gen3SaveDocument.DestinyExtensionOffsetMarker + KeyItemPocketExtensionOffset, KeyItemPocketCapacity, 2, true),
            3 => new Gen3SaveBagPhysicalPocket("精灵球", Gen3SaveDocument.DestinyExtensionOffsetMarker + BallPocketExtensionOffset, BallPocketCapacity, 3, true),
            4 => new Gen3SaveBagPhysicalPocket("招式机盒", MachineBoxOffset, MachineBoxCapacity, 4, true),
            5 => new Gen3SaveBagPhysicalPocket("树果", Gen3SaveDocument.DestinyExtensionOffsetMarker + BerryPocketExtensionOffset, BerryPocketCapacity, 5, true),
            _ => null
        };
        if (candidate is not null) yield return candidate;
    }
    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId)
        => document.ItemPockets.TryGetValue(itemId, out var pocket) && pocket is >= 1 and <= 5 ? pocket : null;
    public string PocketName(int pocket) => pocket switch { 1 => "道具", 2 => "重要物品", 3 => "精灵球", 4 => "招式机器", 5 => "树果", _ => $"口袋{pocket}" };
    public void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data)
    {
        if (saveOffset >= Gen3SaveDocument.DestinyExtensionOffsetMarker)
        {
            document.WriteLogicalRange(Gen3SaveDocument.PcFirstSection, 8 * Gen3SaveDocument.SectionDataSize + saveOffset - Gen3SaveDocument.DestinyExtensionOffsetMarker, data);
            return;
        }
        document.WriteLogicalRange(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset, data);
    }
    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset)
        => saveOffset >= Gen3SaveDocument.DestinyExtensionOffsetMarker
            ? document.ReadLogicalU16(Gen3SaveDocument.PcFirstSection, 8 * Gen3SaveDocument.SectionDataSize + saveOffset - Gen3SaveDocument.DestinyExtensionOffsetMarker)
            : document.ReadLogicalU16(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset);
}
