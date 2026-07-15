namespace RocketTool.Core;

internal sealed class RadicalRedCfruSaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "pokemon-radical-red-41-save-v1";
    public static RadicalRedCfruSaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    public Gen3SaveDocument Open(string path, GameProfile profile)
        => Gen3SaveReader.OpenRadicalRed(path, profile);

    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes)
    {
        var profile = document.Profile;
        if (document.Snapshot.Trainer is null)
            throw new InvalidOperationException("当前激进红存档没有可写回的训练家信息。");
        if (nameBytes.Length != profile.Memory.PlayerNameLength)
            throw new InvalidOperationException($"训练家名字编码必须为 {profile.Memory.PlayerNameLength} 字节。");
        document.WriteSectionRange(0, 0, nameBytes);
    }

    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money)
    {
        var profile = document.Profile;
        if (document.Snapshot.Trainer is null)
            throw new InvalidOperationException("当前激进红存档没有可写回的训练家信息。");
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
        => document.WriteRadicalRedBox(globalSlot, data);

    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey)
        => (ushort)(quantity ^ quantityKey);

    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket)
    {
        var marker = Gen3SaveDocument.RadicalRedExtensionOffsetMarker;
        var candidate = pocket switch
        {
            1 => new Gen3SaveBagPhysicalPocket("道具", marker + 0x09AC, 450, 1, true),
            2 => new Gen3SaveBagPhysicalPocket("重要物品", marker + 0x10B4, 75, 2, true),
            3 => new Gen3SaveBagPhysicalPocket("精灵球", marker + 0x11E0, 50, 3, true),
            4 => new Gen3SaveBagPhysicalPocket("招式机器", marker + 0x12A8, 128, 4, true),
            5 => new Gen3SaveBagPhysicalPocket("树果", marker + 0x14A8, 75, 5, true),
            _ => null
        };
        if (candidate is not null) yield return candidate;
    }

    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId)
        => document.ItemPockets.TryGetValue(itemId, out var pocket) ? pocket : null;

    public string PocketName(int pocket)
        => pocket switch
        {
            1 => "道具",
            2 => "重要物品",
            3 => "精灵球",
            4 => "招式机器",
            5 => "树果",
            _ => $"口袋{pocket}"
        };

    public void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data)
        => document.WriteRadicalRedExtensionRange(saveOffset - Gen3SaveDocument.RadicalRedExtensionOffsetMarker, data);

    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset)
    {
        Span<byte> value = stackalloc byte[2];
        document.ReadRadicalRedExtensionRange(saveOffset - Gen3SaveDocument.RadicalRedExtensionOffsetMarker, value);
        return (ushort)(value[0] | value[1] << 8);
    }
}
