namespace RocketTool.Core;

internal sealed class UltimateEmerald55SaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "pokemon-ultimate-emerald-55-save-v1";
    public static UltimateEmerald55SaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    public Gen3SaveDocument Open(string path, GameProfile profile)
        => Gen3SaveReader.OpenUltimateEmerald55(path, profile);

    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes)
    {
        var profile = document.Profile;
        if (document.Snapshot.Trainer is null)
            throw new InvalidOperationException("当前究极绿宝石5.5存档没有可写回的玩家资料。");
        if (nameBytes.Length != profile.Memory.PlayerNameLength)
            throw new InvalidOperationException(
                $"玩家名字编码必须为 {profile.Memory.PlayerNameLength} 字节。");
        document.WriteSectionRange(0, 0, nameBytes);
    }

    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money)
    {
        var profile = document.Profile;
        if (document.Snapshot.Trainer is null)
            throw new InvalidOperationException("当前究极绿宝石5.5存档没有可写回的玩家资料。");
        if (money > profile.Runtime.MaxTrainerMoney)
            throw new InvalidOperationException(
                $"金钱必须在 0..{profile.Runtime.MaxTrainerMoney} 范围内。");

        var key = document.ReadSectionU32(
            0,
            (int)profile.Memory.SaveBlock2EncryptionKeyOffset);
        var encrypted = money ^ key;
        Span<byte> value = stackalloc byte[4];
        value[0] = (byte)encrypted;
        value[1] = (byte)(encrypted >> 8);
        value[2] = (byte)(encrypted >> 16);
        value[3] = (byte)(encrypted >> 24);
        document.WriteLogicalRange(
            Gen3SaveDocument.SaveBlock1FirstSection,
            (int)profile.Memory.SaveBlock1MoneyOffset,
            value);
    }

    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data)
    {
        if (data.Length != BoxPokemon.Size)
            throw new InvalidOperationException($"究极绿宝石5.5箱子记录必须为 {BoxPokemon.Size} 字节。");
        var profile = document.Profile;
        var maxSlots = Math.Min(
            profile.Memory.SavePcBoxWritableSlotCount,
            profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots);
        if (globalSlot < 1 || globalSlot > maxSlots)
            throw new ArgumentOutOfRangeException(nameof(globalSlot));
        document.WriteLogicalRange(
            Gen3SaveDocument.PcFirstSection,
            Gen3SaveDocument.PcStorageOffset + (globalSlot - 1) * BoxPokemon.Size,
            data);
    }

    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey)
        => (ushort)(quantity ^ quantityKey);

    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket)
    {
        var marker = Gen3SaveDocument.UltimateEmerald55BagOffsetMarker;
        var candidate = pocket switch
        {
            1 => new Gen3SaveBagPhysicalPocket("道具", marker + 0x000, 100, 1, true),
            2 => new Gen3SaveBagPhysicalPocket("精灵球", marker + 0x258, 32, 2, true),
            3 => new Gen3SaveBagPhysicalPocket("招式机器", marker + 0x2D8, 130, 3, true),
            4 => new Gen3SaveBagPhysicalPocket("树果", marker + 0x4E0, 50, 4, true),
            5 => new Gen3SaveBagPhysicalPocket("重要物品", marker + 0x190, 50, 5, true),
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
            2 => "精灵球",
            3 => "招式机器",
            4 => "树果",
            5 => "重要物品",
            _ => $"口袋{pocket}"
        };

    public void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data)
        => document.WriteUltimateEmerald55BagRange(
            saveOffset - Gen3SaveDocument.UltimateEmerald55BagOffsetMarker,
            data);

    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset)
    {
        Span<byte> value = stackalloc byte[2];
        document.ReadUltimateEmerald55BagRange(
            saveOffset - Gen3SaveDocument.UltimateEmerald55BagOffsetMarker,
            value);
        return (ushort)(value[0] | value[1] << 8);
    }
}
