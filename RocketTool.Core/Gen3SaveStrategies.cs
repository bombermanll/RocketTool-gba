namespace RocketTool.Core;

internal interface IGen3SaveStrategy
{
    string Id { get; }
    Gen3SaveDocument Open(string path, GameProfile? profile);
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
            var id => throw new NotSupportedException($"当前程序不支持存档策略：{id}")
        };
}

internal sealed class SpanishRocketSaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "spanish-rocket-save-v1";
    public static SpanishRocketSaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    private SpanishRocketSaveStrategy()
    {
    }

    public Gen3SaveDocument Open(string path, GameProfile? profile)
        => Gen3SaveReader.OpenSpanishRocket(path, profile);

    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes)
        => throw new InvalidOperationException("当前存档策略尚未验证训练家名字写回。");

    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money)
        => throw new InvalidOperationException("当前存档策略尚未验证金钱写回。");

    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data)
    {
        var saveOffset = Gen3SaveDocument.PcStorageOffset + (globalSlot - 1) * BoxPokemon.Size;
        document.WriteLogicalRange(Gen3SaveDocument.PcFirstSection, saveOffset, data);
    }

    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey)
        => (ushort)((quantity - 1) ^ quantityKey);

    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket)
    {
        foreach (var physical in Gen3SaveReader.SaveBagPhysicalPockets)
        {
            if (physical.FixedPocket == pocket ||
                physical.FixedPocket is null && pocket is >= 1 and <= 6)
                yield return physical;
        }
    }

    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId)
        => Gen3SaveReader.SavePocketOfItem(itemId);

    public string PocketName(int pocket)
        => pocket switch
        {
            1 => "普通道具",
            2 => "回复药品",
            3 => "精灵球",
            4 => "战斗道具",
            5 => "树果",
            6 => "宝物",
            7 => "招式/秘传机器",
            8 => "重要物品",
            _ => $"口袋{pocket}"
        };

    public void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data)
        => document.WriteLogicalRange(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset, data);

    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset)
        => document.ReadLogicalU16(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset);
}

internal sealed class UnboundCfruSaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "unbound-cfru-save-v1";
    public static UnboundCfruSaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    private UnboundCfruSaveStrategy()
    {
    }

    public Gen3SaveDocument Open(string path, GameProfile? profile)
        => profile is null
            ? throw new InvalidOperationException("解放版存档策略需要 Profile。")
            : Gen3SaveReader.OpenUnbound(path, profile);

    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes)
    {
        var profile = document.Profile ?? throw new InvalidOperationException("解放版存档策略需要 Profile。");
        if (document.Snapshot.Trainer is null)
            throw new InvalidOperationException("当前存档没有可写回的训练家信息。");
        if (nameBytes.Length != profile.Memory.PlayerNameLength)
            throw new InvalidOperationException($"训练家名字编码必须为 {profile.Memory.PlayerNameLength} 字节。");

        document.WriteSectionRange(0, 0, nameBytes);
    }

    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money)
    {
        var profile = document.Profile ?? throw new InvalidOperationException("解放版存档策略需要 Profile。");
        if (document.Snapshot.Trainer is null)
            throw new InvalidOperationException("当前存档没有可写回的训练家信息。");
        if (money > 99_999_999)
            throw new InvalidOperationException("金钱必须在 0..99999999 范围内。");

        var key = document.ReadSectionU32(0, (int)profile.Memory.SaveBlock2EncryptionKeyOffset);
        var encrypted = money ^ key;
        Span<byte> value = stackalloc byte[4];
        value[0] = (byte)encrypted;
        value[1] = (byte)(encrypted >> 8);
        value[2] = (byte)(encrypted >> 16);
        value[3] = (byte)(encrypted >> 24);
        document.WriteLogicalRange(Gen3SaveDocument.SaveBlock1FirstSection, (int)profile.Memory.SaveBlock1MoneyOffset, value);
    }

    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data)
        => document.WriteUnboundBox(globalSlot, data);

    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey)
        => (ushort)(quantity ^ quantityKey);

    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket)
    {
        var candidate = pocket switch
        {
            1 => new Gen3SaveBagPhysicalPocket("道具", Gen3SaveDocument.UnboundParasiteOffsetMarker + 0x09AC, 450, 1, true),
            2 => new Gen3SaveBagPhysicalPocket("重要物品", Gen3SaveDocument.UnboundParasiteOffsetMarker + 0x10B4, 75, 2, true),
            3 => new Gen3SaveBagPhysicalPocket("精灵球", Gen3SaveDocument.UnboundParasiteOffsetMarker + 0x11E0, 50, 3, true),
            4 => new Gen3SaveBagPhysicalPocket("招式机器", Gen3SaveDocument.UnboundParasiteOffsetMarker + 0x12A8, 128, 4, true),
            5 => new Gen3SaveBagPhysicalPocket("树果", Gen3SaveDocument.UnboundParasiteOffsetMarker + 0x14A8, 75, 5, true),
            _ => null
        };
        if (candidate is not null) yield return candidate;
    }

    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId)
        => document.ItemPockets.TryGetValue(itemId, out var mappedPocket) ? mappedPocket : null;

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
        => document.WriteUnboundParasiteRange(saveOffset - Gen3SaveDocument.UnboundParasiteOffsetMarker, data);

    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset)
    {
        Span<byte> value = stackalloc byte[2];
        document.ReadUnboundParasiteRange(saveOffset - Gen3SaveDocument.UnboundParasiteOffsetMarker, value);
        return (ushort)(value[0] | value[1] << 8);
    }
}

internal sealed class DestinyFireRedSaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "pokemon-destiny-save-v1";
    internal const int MachineBoxOffset = 0x310;
    internal const int MachineBoxCapacity = 128;
    internal const int BallPocketExtensionOffset = 0x9EC;
    internal const int BallPocketCapacity = 28;
    internal const int ItemPocketExtensionOffset = 0xA5C;
    internal const int ItemPocketCapacity = (0xFF4 - ItemPocketExtensionOffset) / 4;
    internal const int SafeBoxSlotCount = 408;
    public static DestinyFireRedSaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    private DestinyFireRedSaveStrategy()
    {
    }

    public Gen3SaveDocument Open(string path, GameProfile? profile)
        => profile is null
            ? throw new InvalidOperationException("宝可梦命运存档策略需要 Profile。")
            : Gen3SaveReader.OpenDestiny(path, profile);

    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes)
        => throw new InvalidOperationException("宝可梦命运尚未验证训练家名字写回。");

    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money)
        => throw new InvalidOperationException("宝可梦命运尚未验证训练家/金钱写回 UI。");

    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data)
    {
        if (data.Length != BoxPokemon.Size)
            throw new InvalidOperationException($"宝可梦命运箱子记录必须为 {BoxPokemon.Size} 字节。");
        var profile = document.Profile ?? throw new InvalidOperationException("宝可梦命运箱子写回需要 Profile。");
        var maxSlots = Math.Min(
            SafeBoxSlotCount,
            profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots);
        if (globalSlot < 1 || globalSlot > maxSlots)
            throw new ArgumentOutOfRangeException(nameof(globalSlot));
        document.WriteLogicalRange(
            Gen3SaveDocument.PcFirstSection,
            4 + (globalSlot - 1) * BoxPokemon.Size,
            data);
    }

    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey)
        => quantity;

    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket)
    {
        var candidate = pocket switch
        {
            1 => new Gen3SaveBagPhysicalPocket("道具", Gen3SaveDocument.DestinyExtensionOffsetMarker + ItemPocketExtensionOffset, ItemPocketCapacity, 1, true),
            3 => new Gen3SaveBagPhysicalPocket("精灵球", Gen3SaveDocument.DestinyExtensionOffsetMarker + BallPocketExtensionOffset, BallPocketCapacity, 3, true),
            4 => new Gen3SaveBagPhysicalPocket("招式机盒", MachineBoxOffset, MachineBoxCapacity, 4, true),
            _ => null
        };
        if (candidate is not null) yield return candidate;
    }

    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId)
    {
        if (!document.ItemPockets.TryGetValue(itemId, out var mappedPocket))
            return 1;
        return mappedPocket switch
        {
            3 => 3,
            4 => 4,
            _ => 1
        };
    }

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
    {
        if (saveOffset >= Gen3SaveDocument.DestinyExtensionOffsetMarker)
        {
            var extensionOffset = saveOffset - Gen3SaveDocument.DestinyExtensionOffsetMarker;
            document.WriteLogicalRange(
                Gen3SaveDocument.PcFirstSection,
                8 * Gen3SaveDocument.SectionDataSize + extensionOffset,
                data);
            return;
        }

        document.WriteLogicalRange(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset, data);
    }

    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset)
    {
        if (saveOffset >= Gen3SaveDocument.DestinyExtensionOffsetMarker)
        {
            var extensionOffset = saveOffset - Gen3SaveDocument.DestinyExtensionOffsetMarker;
            return document.ReadLogicalU16(
                Gen3SaveDocument.PcFirstSection,
                8 * Gen3SaveDocument.SectionDataSize + extensionOffset);
        }

        return document.ReadLogicalU16(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset);
    }
}
