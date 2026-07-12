namespace RocketTool.Core;

public sealed record Gen3SaveBoxEntry(int GlobalSlot, int BoxNumber, int SlotInBox, int SaveOffset, BoxPokemon Mon);

public sealed record Gen3SaveBagEntry(
    int SaveOffset,
    int Pocket,
    int SlotInPocket,
    ushort ItemId,
    ushort Quantity,
    ushort QuantityKey,
    bool QuantityXor,
    string Note);

public sealed record Gen3SaveTrainerInfo(byte[] NameBytes, uint OtId, uint Money);

public sealed record Gen3SaveReadResult(
    string FileName,
    int FileSize,
    int SaveSlot,
    uint SaveIndex,
    int ValidSectionCount,
    IReadOnlyList<PartyPokemon> Party,
    IReadOnlyList<Gen3SaveBagEntry> Bag,
    IReadOnlyList<Gen3SaveBoxEntry> Boxes,
    Gen3SaveTrainerInfo? Trainer,
    IReadOnlyList<string> Warnings);

public static class Gen3SaveReader
{
    private const int SectionSize = 0x1000;
    private const int SectionDataSize = 0xF80;
    private const int PcTailSectionId = 13;
    private const int PcTailChecksumSize = 0x7D0;
    private const int PcBoxesChecksumSize = 0x744;
    private const int SectionsPerSlot = 14;
    private const int SaveSlotSize = SectionSize * SectionsPerSlot;
    private const uint SectionSignature = 0x08012025;
    private const uint UnboundSectionSignature = 0x01121999;
    private const int UnboundPartyCountOffset = 0x34;
    private const int UnboundPartyOffset = 0x38;
    private const int UnboundBoxRecordSize = BoxPokemon.UnboundCompressedSize;
    private const int UnboundBoxSlots = 30;
    private const int UnboundMainBoxCount = 19;
    private const int UnboundParasiteOffsetMarker = 0x100000;
    private const int UnboundParasiteSize = 0x2E38;
    private const int MaxSpecies = 2000;
    private const int MaxItem = 922;
    private const int StandardPartyCountOffset = 0x234;
    private const int StandardPartyOffset = 0x238;
    private const int StandardPcBoxesOffset = 4;
    private const int DestinyPartyCountOffset = 0x34;
    private const int DestinyPartyOffset = 0x38;
    private const int DestinyMoneyOffset = 0x290;
    private const int SpanishRocketSaveBagMaxQuantity = 255;
    private const int MachinePocket = 7;
    private const int MachineTmStartItem = 592;
    private const int MachineTmCount = 246;
    private const int MachineHmStartItem = 838;
    private const int MachineHmCount = 8;
    internal static readonly Gen3SaveBagPhysicalPocket[] SaveBagPhysicalPockets =
    [
        new("普通道具", 0x04C0, 42, 1, true),
        new("重要物品", 0x08BC, 63, 8, false),
        new("精灵球", 0x09BC, 15, 3, true),
        new("招式/秘传机器", 0x09FC, 254, 7, true),
        new("树果", 0x0DF4, 67, 5, true),
        new("混合口袋A", 0x0F04, 66, null, true),
        new("混合口袋B", 0x1010, 129, null, true),
        new("混合口袋C", 0x1218, 101, null, true),
    ];
    private static readonly (int Start, int End, int Pocket)[] SaveItemPocketRanges =
    [
        (1, 27, 3),
        (28, 54, 2),
        (55, 58, 1),
        (59, 59, 2),
        (60, 64, 1),
        (65, 72, 2),
        (73, 79, 1),
        (80, 100, 2),
        (101, 101, 1),
        (102, 109, 2),
        (110, 121, 1),
        (122, 129, 4),
        (130, 155, 1),
        (156, 156, 6),
        (157, 195, 1),
        (196, 197, 6),
        (198, 227, 1),
        (228, 228, 6),
        (229, 229, 1),
        (230, 238, 6),
        (239, 251, 1),
        (252, 268, 4),
        (269, 291, 1),
        (292, 293, 8),
        (294, 347, 6),
        (348, 348, 1),
        (349, 366, 4),
        (367, 391, 6),
        (392, 401, 1),
        (402, 404, 6),
        (405, 405, 1),
        (406, 406, 6),
        (407, 407, 1),
        (408, 413, 6),
        (414, 422, 4),
        (423, 434, 1),
        (435, 450, 4),
        (451, 451, 6),
        (452, 470, 4),
        (471, 471, 1),
        (472, 472, 4),
        (473, 473, 1),
        (474, 474, 4),
        (475, 475, 6),
        (476, 478, 1),
        (479, 479, 4),
        (480, 480, 1),
        (481, 501, 4),
        (502, 503, 6),
        (504, 523, 4),
        (524, 583, 5),
        (584, 584, 1),
        (585, 591, 5),
        (592, 845, 7),
        (846, 847, 8),
        (848, 850, 1),
        (851, 851, 8),
        (852, 859, 1),
        (860, 861, 8),
        (862, 862, 1),
        (863, 873, 8),
        (874, 874, 1),
        (875, 877, 8),
        (878, 878, 1),
        (879, 913, 8),
        (914, 917, 1),
        (918, 918, 4),
        (919, 920, 6),
        (921, 921, 1),
        (922, 922, 8)
    ];
    private static readonly (string Name, int Offset)[] BagProbeOffsets =
    [
        ("重要物品", 0x2268),
        ("精灵球", 0x2348),
        ("招式&秘传", 0x23A0),
        ("树果", 0x27D0),
        ("道具", 0x1E78),
        ("回复", 0x2884),
        ("战斗道具", 0x29E0),
        ("宝物", 0x2BF0)
    ];

    public static Gen3SaveReadResult Read(string path) => Open(path).Snapshot;

    public static Gen3SaveReadResult Read(string path, GameProfile profile) => Open(path, profile).Snapshot;

    public static Gen3SaveDocument Open(string path, GameProfile profile)
        => Gen3SaveStrategyCatalog.ForProfile(profile).Open(path, profile);

    internal static Gen3SaveDocument OpenUnbound(string path, GameProfile profile)
    {
        var raw = File.ReadAllBytes(path);
        var warnings = new List<string>();
        var slots = FindUnboundSlots(raw).ToArray();
        if (slots.Length == 0)
            throw new InvalidOperationException("没有识别到有效的《宝可梦解放》存档 section（签名 0x01121999）。");

        var best = slots.OrderByDescending(slot => slot.ValidSections.Count)
            .ThenByDescending(slot => slot.SaveIndex)
            .First();
        if (best.ValidSections.Count < SectionsPerSlot)
            warnings.Add($"只识别到 {best.ValidSections.Count}/{SectionsPerSlot} 个有效 section，已禁用安全写回。");

        var section0 = RequiredUnboundSection(best, 0);
        var saveBlock2 = section0.Data.AsSpan(0, 0xF24).ToArray();
        var saveBlock1 = new byte[0x3D68];
        CopyUnboundSection(best, 1, saveBlock1, 0x0000, 0xF80, warnings);
        CopyUnboundSection(best, 2, saveBlock1, 0x0F80, 0xF80, warnings);
        CopyUnboundSection(best, 3, saveBlock1, 0x1F00, 0xF80, warnings);
        CopyUnboundSection(best, 4, saveBlock1, 0x2E80, 0xD98, warnings);

        var pcStorage = new byte[0x83D0];
        for (var id = 5; id <= 12; id++)
            CopyUnboundSection(best, id, pcStorage, (id - 5) * SectionDataSize, SectionDataSize, warnings);
        CopyUnboundSection(best, 13, pcStorage, 8 * SectionDataSize, 0x7D0, warnings);

        var parasite = BuildUnboundParasite(raw, best);
        var party = ReadUnboundParty(saveBlock1, profile, warnings);
        var boxes = ReadUnboundBoxes(saveBlock2, saveBlock1, pcStorage, parasite, profile, warnings);
        var itemPockets = ReadProfileItemPockets(profile);
        var bag = ReadUnboundBag(parasite, saveBlock2, itemPockets, profile.Limits.MaxItem, profile.Limits.MaxBagQuantity, warnings);
        var trainerName = saveBlock2.AsSpan(0, profile.Memory.PlayerNameLength).ToArray();
        var trainerOtId = ReadU32(saveBlock2, profile.Memory.SaveBlock2PlayerOtIdOffset);
        var moneyKey = ReadU32(saveBlock2, (int)profile.Memory.SaveBlock2EncryptionKeyOffset);
        var money = ReadU32(saveBlock1, (int)profile.Memory.SaveBlock1MoneyOffset) ^ moneyKey;
        if (money > 99_999_999)
            throw new InvalidOperationException($"解放版存档金钱字段解密为 {money}，超出已确认范围。已停止读取，避免误写。");
        var trainer = new Gen3SaveTrainerInfo(trainerName, trainerOtId, money);

        warnings.Add($"解放版存档：使用 save index {best.SaveIndex}；队伍为 100 字节明文结构，箱子为 58 字节 CFRU 压缩结构，共 {profile.Memory.PcBoxCount} 箱。");
        warnings.Add("PC storage 已按 section 5-13 重组；section 13 checksum 只覆盖 0x450，但 PC 尾段读取保留到 0x7D0。");
        warnings.Add("扩展数据已按 CFRU parasite 映射从 section 0/4/13、原始扇区 30/31 和 section 1 尾部重组。");
        var snapshot = new Gen3SaveReadResult(
            Path.GetFileName(path), raw.Length, best.Ordinal + 1, best.SaveIndex,
            best.ValidSections.Count, party, bag, boxes, trainer, warnings);
        var sections = best.ValidSections.ToDictionary(
            pair => pair.Key,
            pair => new Gen3SaveSectionLayout(pair.Key, pair.Value.Offset, pair.Value.SaveIndex, pair.Value.ChecksumMode));
        return new Gen3SaveDocument(path, raw, sections, UnboundPartyOffset, snapshot, profile, itemPockets, UnboundCfruSaveStrategy.Instance);
    }

    private static IEnumerable<SaveSlot> FindUnboundSlots(byte[] raw)
    {
        var sections = new List<SaveSection>();
        for (var offset = 0; offset + SectionSize <= raw.Length; offset += SectionSize)
        {
            var data = raw.AsSpan(offset, SectionSize);
            var id = ReadU16(data, 0xFF4);
            if (id >= SectionsPerSlot || ReadU32(data, 0xFF8) != UnboundSectionSignature) continue;
            var length = UnboundChecksumLength(id);
            if (Checksum(data[..length]) != ReadU16(data, 0xFF6)) continue;
            sections.Add(new SaveSection(id, offset, ReadU32(data, 0xFFC), data[..0xFF4].ToArray(), $"unbound-{length:X}"));
        }

        var ordinal = 0;
        foreach (var group in sections.GroupBy(section => section.SaveIndex).OrderBy(group => group.Min(section => section.Offset)))
        {
            var byId = group.GroupBy(section => section.Id)
                .ToDictionary(sameId => sameId.Key, sameId => sameId.OrderBy(section => section.Offset).First());
            if (byId.Count > 0)
                yield return new SaveSlot(ordinal++, byId.Values.Min(section => section.Offset), byId);
        }
    }

    private static int UnboundChecksumLength(int sectionId) => sectionId switch
    {
        0 => 0xF24,
        1 => 0xFF4,
        3 => 0xFF4,
        4 => 0xD98,
        5 => 0xFF4,
        13 => 0x450,
        _ => SectionDataSize
    };

    private static SaveSection RequiredUnboundSection(SaveSlot slot, int id)
        => slot.ValidSections.TryGetValue(id, out var section)
            ? section
            : throw new InvalidOperationException($"解放版存档缺少 section {id}。");

    private static void CopyUnboundSection(SaveSlot slot, int id, Span<byte> destination, int destinationOffset, int count)
        => RequiredUnboundSection(slot, id).Data.AsSpan(0, count).CopyTo(destination[destinationOffset..]);

    private static void CopyUnboundSection(SaveSlot slot, int id, Span<byte> destination, int destinationOffset, int count, List<string> warnings)
    {
        if (!slot.ValidSections.TryGetValue(id, out var section))
        {
            warnings.Add($"解放版存档缺少 section {id}，对应区域已按空数据只读解析；保存写回已禁用。");
            destination.Slice(destinationOffset, count).Clear();
            return;
        }

        section.Data.AsSpan(0, count).CopyTo(destination[destinationOffset..]);
    }

    private static byte[] BuildUnboundParasite(byte[] raw, SaveSlot slot)
    {
        if (raw.Length < 0x20000)
            throw new InvalidOperationException("解放版存档缺少 CFRU 扩展扇区 30/31。");
        var parasite = new byte[UnboundParasiteSize];
        RequiredUnboundSection(slot, 0).Data.AsSpan(0xEB4, 0xCC).CopyTo(parasite.AsSpan(0x0000));
        RequiredUnboundSection(slot, 4).Data.AsSpan(0xD28, 0x258).CopyTo(parasite.AsSpan(0x00CC));
        RequiredUnboundSection(slot, 13).Data.AsSpan(0x3E0, 0xBA0).CopyTo(parasite.AsSpan(0x0324));
        raw.AsSpan(0x1E000, 0xF80).CopyTo(parasite.AsSpan(0x0EC4));
        raw.AsSpan(0x1F000, 0xF80).CopyTo(parasite.AsSpan(0x1E44));
        RequiredUnboundSection(slot, 1).Data.AsSpan(0xF80, 0x74).CopyTo(parasite.AsSpan(0x2DC4));
        return parasite;
    }

    private static IReadOnlyList<PartyPokemon> ReadUnboundParty(ReadOnlySpan<byte> saveBlock1, GameProfile profile, List<string> warnings)
    {
        var count = saveBlock1[UnboundPartyCountOffset];
        if (count > Gen3Constants.PartySlots)
        {
            warnings.Add($"解放版队伍数量异常：{count}。");
            return [];
        }
        var result = new List<PartyPokemon>();
        for (var slot = 0; slot < count; slot++)
        {
            var mon = new PartyPokemon(saveBlock1.Slice(UnboundPartyOffset + slot * PartyPokemon.Size, PartyPokemon.Size), PokemonDataLayout.UnboundCfruPlainParty);
            var info = mon.GetInfo();
            if (mon.IsEmpty || info.Species == 0 || info.Species > profile.Limits.MaxSpecies || info.Level is < 1 or > 250)
            {
                warnings.Add($"解放版队伍槽 {slot + 1} 数据无效，已停止读取后续槽位。");
                break;
            }
            result.Add(mon);
        }
        return result;
    }

    private static IReadOnlyList<Gen3SaveBoxEntry> ReadUnboundBoxes(
        ReadOnlySpan<byte> saveBlock2,
        ReadOnlySpan<byte> saveBlock1,
        ReadOnlySpan<byte> pcStorage,
        ReadOnlySpan<byte> parasite,
        GameProfile profile,
        List<string> warnings)
    {
        var entries = new List<Gen3SaveBoxEntry>();
        for (var box = 1; box <= profile.Memory.PcBoxCount; box++)
        {
            ReadOnlySpan<byte> region;
            var syntheticOffset = box * 0x10000;
            if (box <= UnboundMainBoxCount)
            {
                var offset = 4 + (box - 1) * UnboundBoxSlots * UnboundBoxRecordSize;
                region = pcStorage.Slice(offset, UnboundBoxSlots * UnboundBoxRecordSize);
                syntheticOffset = offset;
            }
            else if (box <= 22)
            {
                var offset = box switch { 20 => 0x19D0, 21 => 0x209C, _ => 0x2768 };
                region = parasite.Slice(offset, UnboundBoxSlots * UnboundBoxRecordSize);
                syntheticOffset = UnboundParasiteOffsetMarker + offset;
            }
            else if (box <= 24)
            {
                var offset = box == 23 ? 0x1F08 : 0x25D4;
                region = saveBlock1.Slice(offset, UnboundBoxSlots * UnboundBoxRecordSize);
                syntheticOffset = 0x200000 + offset;
            }
            else
            {
                region = saveBlock2.Slice(0xB0, UnboundBoxSlots * UnboundBoxRecordSize);
                syntheticOffset = 0x3000B0;
            }

            for (var slot = 0; slot < UnboundBoxSlots; slot++)
            {
                var mon = new BoxPokemon(region.Slice(slot * UnboundBoxRecordSize, UnboundBoxRecordSize), PokemonDataLayout.UnboundCfruPlainParty);
                if (mon.IsEmpty) continue;
                var info = mon.GetInfo();
                if (!mon.HasValidHeader(profile.Limits.MaxSpecies))
                {
                    warnings.Add($"解放版箱{box:00}-{slot + 1:00}存在非空填充数据（物种 {info.Species}），已按头部标记忽略。");
                    continue;
                }
                var globalSlot = (box - 1) * UnboundBoxSlots + slot + 1;
                entries.Add(new Gen3SaveBoxEntry(globalSlot, box, slot + 1, syntheticOffset + slot * UnboundBoxRecordSize, mon));
            }
        }
        return entries;
    }

    private static IReadOnlyDictionary<int, int> ReadProfileItemPockets(GameProfile profile)
    {
        var db = new ModifierDatabase(profile.DatabaseDirectory);
        return db.Table("item_pockets")
            .Select(pair => (pair.Key, Value: int.TryParse(pair.Value, out var value) ? value : 0))
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static IReadOnlyList<Gen3SaveBagEntry> ReadUnboundBag(
        ReadOnlySpan<byte> parasite,
        ReadOnlySpan<byte> saveBlock2,
        IReadOnlyDictionary<int, int> itemPockets,
        int maxItem,
        int maxQuantity,
        List<string> warnings)
    {
        var quantityKey = (ushort)(ReadU32(saveBlock2, 0xF20) & 0xFFFF);
        var physical = new[]
        {
            (Pocket: 1, Offset: 0x09AC, Capacity: 450, Name: "道具"),
            (Pocket: 2, Offset: 0x10B4, Capacity: 75, Name: "重要物品"),
            (Pocket: 3, Offset: 0x11E0, Capacity: 50, Name: "精灵球"),
            (Pocket: 4, Offset: 0x12A8, Capacity: 128, Name: "招式机器"),
            (Pocket: 5, Offset: 0x14A8, Capacity: 75, Name: "树果")
        };
        var entries = new List<Gen3SaveBagEntry>();
        foreach (var area in physical)
        {
            var displaySlot = 0;
            for (var i = 0; i < area.Capacity; i++)
            {
                var offset = area.Offset + i * 4;
                var item = ReadU16(parasite, offset);
                if (item == 0) continue;
                if (item > maxItem)
                {
                    warnings.Add($"解放版{area.Name}槽 {i + 1} 的道具 {item} 超过当前 Profile 上限 {maxItem}，已忽略。");
                    continue;
                }
                if (itemPockets.TryGetValue(item, out var mappedPocket) && mappedPocket != area.Pocket)
                    warnings.Add($"解放版{area.Name}槽 {i + 1} 的道具 {item} 按物理口袋读取；数据表口袋为 {mappedPocket}。");
                var quantity = (ushort)(ReadU16(parasite, offset + 2) ^ quantityKey);
                if (quantity == 0) quantity = 1;
                if (quantity > maxQuantity)
                {
                    warnings.Add($"解放版{area.Name}槽 {i + 1} 的数量 {quantity} 超过已确认上限 {maxQuantity}，已忽略。");
                    continue;
                }
                entries.Add(new Gen3SaveBagEntry(
                    UnboundParasiteOffsetMarker + offset, area.Pocket, ++displaySlot,
                    item, quantity, quantityKey, true, $"CFRU扩展区；{area.Name} {i + 1}"));
            }
        }
        return entries;
    }

    public static Gen3SaveDocument Open(string path)
        => SpanishRocketSaveStrategy.Instance.Open(path, null);

    internal static Gen3SaveDocument OpenDestiny(string path, GameProfile profile)
    {
        var raw = File.ReadAllBytes(path);
        var warnings = new List<string>();
        var slots = FindSlots(raw).ToArray();
        if (slots.Length == 0)
            throw new InvalidOperationException("没有识别到有效的《宝可梦命运》Gen 3 存档 section。请确认文件是原始 .sav/.srm 电池存档。");

        var best = slots
            .OrderByDescending(s => s.ValidSections.Count)
            .ThenByDescending(s => s.SaveIndex)
            .First();
        best = SupplementMissingSections(raw, best, warnings);

        var f24ChecksumSections = best.ValidSections.Values
            .Where(s => s.ChecksumMode == "f24")
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArray();
        if (f24ChecksumSections.Length > 0)
            warnings.Add($"存档校验：section {string.Join(",", f24ChecksumSections)} 使用 checksum 范围 0xF24。");

        var d98ChecksumSections = best.ValidSections.Values
            .Where(s => s.ChecksumMode == "d98")
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArray();
        if (d98ChecksumSections.Length > 0)
            warnings.Add($"存档校验：section {string.Join(",", d98ChecksumSections)} 使用 checksum 范围 0xD98。");

        var extendedChecksumSections = best.ValidSections.Values
            .Where(s => s.ChecksumMode == "extended")
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArray();
        if (extendedChecksumSections.Length > 0)
            warnings.Add($"存档校验：section {string.Join(",", extendedChecksumSections)} 使用扩展 checksum 范围 0xFF4。");

        warnings.Add("命运存档副本候选：" + string.Join("；", slots
            .OrderByDescending(s => s.ValidSections.Count)
            .ThenByDescending(s => s.SaveIndex)
            .Take(8)
            .Select(s => $"offset=0x{s.Offset:X}, sections={s.ValidSections.Count}/{SectionsPerSlot}, index={s.SaveIndex}, ids={SectionIdList(s)}")));
        if (best.ValidSections.Count < SectionsPerSlot)
            warnings.Add($"只识别到 {best.ValidSections.Count}/{SectionsPerSlot} 个有效 section，已禁用安全写回。");

        var saveBlock1 = BuildBlock(best, 1, 4, warnings, "SaveBlock1");
        var pcStorage = BuildBlock(best, 5, 13, warnings, "PC 箱子");
        var party = ReadDestinyParty(saveBlock1, profile, warnings);
        var itemPockets = ReadProfileItemPockets(profile);
        var bag = ReadDestinyBag(saveBlock1, pcStorage, itemPockets, profile.Limits.MaxItem, profile.Limits.MaxBagQuantity, warnings);
        var boxes = ReadDestinyBoxes(pcStorage, profile, warnings);

        if (party.Count == 0)
            warnings.Add("命运存档队伍数量为 0；队伍结构偏移已定位，但当前样本不能验证非空队伍 round-trip。");
        if (boxes.Count == 0)
            warnings.Add("命运存档标准 PC 区域当前没有非空宝可梦；箱子写回保持禁用。");

        var money = ReadU32(saveBlock1, DestinyMoneyOffset);
        warnings.Add($"命运存档金钱线索：SaveBlock1+0x{DestinyMoneyOffset:X}= {money}（当前仅记录，不启用训练家页写回）。");
        warnings.Add("命运存档：道具/精灵球位于 section 13 扩展区，招式机盒位于 SaveBlock1+0x310；0x298 是电脑道具区域。");

        var snapshot = new Gen3SaveReadResult(
            Path.GetFileName(path),
            raw.Length,
            best.Ordinal + 1,
            best.SaveIndex,
            best.ValidSections.Count,
            party,
            bag,
            boxes,
            null,
            warnings);
        var sections = best.ValidSections.ToDictionary(
            pair => pair.Key,
            pair => new Gen3SaveSectionLayout(pair.Key, pair.Value.Offset, pair.Value.SaveIndex, pair.Value.ChecksumMode));
        return new Gen3SaveDocument(path, raw, sections, DestinyPartyOffset, snapshot, profile, itemPockets, DestinyFireRedSaveStrategy.Instance);
    }

    internal static Gen3SaveDocument OpenSpanishRocket(string path, GameProfile? profile)
    {
        var raw = File.ReadAllBytes(path);
        var warnings = new List<string>();
        var slots = FindSlots(raw).ToArray();
        if (slots.Length == 0)
            throw new InvalidOperationException("没有识别到有效的 Gen 3 存档 section。请确认文件是原始 .sav/.srm 电池存档。");

        var best = slots
            .OrderByDescending(s => s.ValidSections.Count)
            .ThenByDescending(s => s.SaveIndex)
            .First();
        best = SupplementMissingSections(raw, best, warnings);
        var extendedChecksumSections = best.ValidSections.Values
            .Where(s => s.ChecksumMode == "extended")
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArray();
        if (extendedChecksumSections.Length > 0)
            warnings.Add($"存档校验：section {string.Join(",", extendedChecksumSections)} 使用扩展 checksum 范围 0xFF4。");
        var pcTailChecksumSections = best.ValidSections.Values
            .Where(s => s.ChecksumMode == "pc-tail")
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArray();
        if (pcTailChecksumSections.Length > 0)
            warnings.Add($"存档校验：section {string.Join(",", pcTailChecksumSections)} 使用 PC 尾段 checksum 范围 0x{PcTailChecksumSize:X}。");
        var pcBoxesChecksumSections = best.ValidSections.Values
            .Where(s => s.ChecksumMode == "pc-boxes")
            .Select(s => s.Id)
            .OrderBy(id => id)
            .ToArray();
        if (pcBoxesChecksumSections.Length > 0)
            warnings.Add($"存档校验：section {string.Join(",", pcBoxesChecksumSections)} 使用纯箱子数据 checksum 范围 0x{PcBoxesChecksumSize:X}。");

        warnings.Add("存档副本候选：" + string.Join("；", slots
            .OrderByDescending(s => s.ValidSections.Count)
            .ThenByDescending(s => s.SaveIndex)
            .Take(8)
            .Select(s => $"offset=0x{s.Offset:X}, sections={s.ValidSections.Count}/{SectionsPerSlot}, index={s.SaveIndex}, ids={SectionIdList(s)}")));
        if (best.Offset != 0 && best.Offset != SaveSlotSize)
            warnings.Add($"检测到存档副本起始偏移为 0x{best.Offset:X}，文件可能包含模拟器附加数据；已按该偏移读取。");
        if (best.ValidSections.Count < SectionsPerSlot)
            warnings.Add($"只识别到 {best.ValidSections.Count}/{SectionsPerSlot} 个有效 section，读取结果可能不完整。");

        var saveBlock1 = BuildBlock(best, 1, 4, warnings, "SaveBlock1");
        var pcStorage = BuildBlock(best, 5, 13, warnings, "PC 箱子");
        var partyRead = ReadParty(saveBlock1, warnings);
        var bag = ReadBag(saveBlock1, warnings);
        var boxes = ReadBoxes(pcStorage, warnings);

        if (partyRead.Party.Count == 0)
            warnings.Add("没有在存档中识别到队伍宝可梦。");
        if (boxes.Count == 0)
            warnings.Add("没有在标准 PC 箱子区域识别到非空宝可梦。");

        var snapshot = new Gen3SaveReadResult(
            Path.GetFileName(path),
            raw.Length,
            best.Ordinal + 1,
            best.SaveIndex,
            best.ValidSections.Count,
            partyRead.Party,
            bag,
            boxes,
            null,
            warnings);
        var sections = best.ValidSections.ToDictionary(
            pair => pair.Key,
            pair => new Gen3SaveSectionLayout(pair.Key, pair.Value.Offset, pair.Value.SaveIndex, pair.Value.ChecksumMode));
        return new Gen3SaveDocument(path, raw, sections, partyRead.PartyOffset, snapshot, profile, strategy: SpanishRocketSaveStrategy.Instance);
    }

    private static IReadOnlyList<SaveSlot> FindSlots(byte[] raw)
    {
        if (raw.Length < SectionSize) return [];

        var allSections = ReadAllSections(raw);
        var result = new List<SaveSlot>();
        var ordinal = 0;
        foreach (var group in allSections
                     .GroupBy(s => s.SaveIndex)
                     .OrderBy(g => g.Min(s => s.Offset)))
        {
            var sections = group
                .GroupBy(s => s.Id)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(s => s.Offset).First());
            if (sections.Count == 0) continue;
            result.Add(new SaveSlot(ordinal++, sections.Values.Min(s => s.Offset), sections));
        }

        return result;
    }

    private static IReadOnlyList<SaveSection> ReadAllSections(byte[] raw)
    {
        var sections = new List<SaveSection>();
        for (var offset = 0; offset + SectionSize <= raw.Length; offset += SectionSize)
        {
            var section = raw.AsSpan(offset, SectionSize);
            var id = ReadU16(section, 0xFF4);
            var checksum = ReadU16(section, 0xFF6);
            var signature = ReadU32(section, 0xFF8);
            var saveIndex = ReadU32(section, 0xFFC);
            if (id >= SectionsPerSlot || signature != SectionSignature) continue;
            var standard = Checksum(section[..SectionDataSize]);
            var pcTail = id == PcTailSectionId ? Checksum(section[..PcTailChecksumSize]) : (ushort?)null;
            var pcBoxes = id == PcTailSectionId ? Checksum(section[..PcBoxesChecksumSize]) : (ushort?)null;
            var d98 = id == 4 ? Checksum(section[..0xD98]) : (ushort?)null;
            var f24 = id == 0 ? Checksum(section[..0xF24]) : (ushort?)null;
            var extended = Checksum(section[..0xFF4]);
            if (standard == checksum)
                sections.Add(new SaveSection((int)id, offset, saveIndex, section[..SectionDataSize].ToArray(), "standard"));
            else if (pcTail == checksum)
                sections.Add(new SaveSection((int)id, offset, saveIndex, section[..SectionDataSize].ToArray(), "pc-tail"));
            else if (pcBoxes == checksum)
                sections.Add(new SaveSection((int)id, offset, saveIndex, section[..SectionDataSize].ToArray(), "pc-boxes"));
            else if (d98 == checksum)
                sections.Add(new SaveSection((int)id, offset, saveIndex, section[..SectionDataSize].ToArray(), "d98"));
            else if (f24 == checksum)
                sections.Add(new SaveSection((int)id, offset, saveIndex, section[..SectionDataSize].ToArray(), "f24"));
            else if (extended == checksum)
                sections.Add(new SaveSection((int)id, offset, saveIndex, section[..SectionDataSize].ToArray(), "extended"));
        }

        return sections;
    }

    private static SaveSlot SupplementMissingSections(byte[] raw, SaveSlot slot, List<string> warnings)
    {
        var missingIds = Enumerable.Range(0, SectionsPerSlot)
            .Where(id => !slot.ValidSections.ContainsKey(id))
            .ToArray();
        if (missingIds.Length == 0) return slot;

        var relaxed = ReadRelaxedSectionHeaders(raw)
            .Where(s => missingIds.Contains(s.Id))
            .OrderBy(s => s.Offset)
            .ToArray();
        foreach (var id in missingIds)
        {
            var seen = relaxed.Where(s => s.Id == id).ToArray();
            if (seen.Length == 0)
            {
                warnings.Add($"宽松探测：没有找到 section {id} 的候选块。");
                continue;
            }

            warnings.Add($"宽松探测：section {id} 候选：" + string.Join("；", seen.Take(8).Select(s =>
                $"offset=0x{s.Offset:X}, index={s.SaveIndex}, sig=0x{s.Signature:X8}, checksum=0x{s.StoredChecksum:X4}/calc=0x{s.CalculatedChecksum:X4}")));
        }

        var supplemented = new Dictionary<int, SaveSection>(slot.ValidSections);
        foreach (var id in missingIds)
        {
            var candidates = relaxed
                .Where(s => s.Id == id && s.SaveIndex == slot.SaveIndex && s.Signature == SectionSignature)
                .ToArray();
            if (candidates.Length != 1) continue;

            var candidate = candidates[0];
            supplemented[id] = new SaveSection(id, candidate.Offset, candidate.SaveIndex, candidate.Data, "relaxed");
            warnings.Add($"宽松读取：section {id} 使用 offset=0x{candidate.Offset:X} 的同 index 候选，checksum 不匹配，当前仅用于只读解析。");
        }

        return supplemented.Count == slot.ValidSections.Count
            ? slot
            : new SaveSlot(slot.Ordinal, supplemented.Values.Min(s => s.Offset), supplemented);
    }

    private static IReadOnlyList<RelaxedSectionHeader> ReadRelaxedSectionHeaders(byte[] raw)
    {
        var sections = new List<RelaxedSectionHeader>();
        for (var offset = 0; offset + SectionSize <= raw.Length; offset += SectionSize)
        {
            var section = raw.AsSpan(offset, SectionSize);
            var id = ReadU16(section, 0xFF4);
            if (id >= SectionsPerSlot) continue;
            var storedChecksum = ReadU16(section, 0xFF6);
            var signature = ReadU32(section, 0xFF8);
            var saveIndex = ReadU32(section, 0xFFC);
            var calculatedChecksum = Checksum(section[..SectionDataSize]);
            sections.Add(new RelaxedSectionHeader(
                (int)id,
                offset,
                saveIndex,
                signature,
                storedChecksum,
                calculatedChecksum,
                section[..SectionDataSize].ToArray()));
        }

        return sections;
    }

    private static byte[] BuildBlock(SaveSlot slot, int firstSection, int lastSection, List<string> warnings, string label)
    {
        var output = new byte[(lastSection - firstSection + 1) * SectionDataSize];
        for (var id = firstSection; id <= lastSection; id++)
        {
            if (!slot.ValidSections.TryGetValue(id, out var section))
            {
                warnings.Add($"{label} 缺少 section {id}。");
                continue;
            }

            section.Data.CopyTo(output.AsSpan((id - firstSection) * SectionDataSize));
        }

        return output;
    }

    private static IReadOnlyList<PartyPokemon> ReadDestinyParty(ReadOnlySpan<byte> saveBlock1, GameProfile profile, List<string> warnings)
    {
        var count = saveBlock1[DestinyPartyCountOffset];
        warnings.Add($"命运队伍偏移：count@0x{DestinyPartyCountOffset:X}=0x{count:X2}，party@0x{DestinyPartyOffset:X}，槽大小 100。");
        if (count > Gen3Constants.PartySlots)
        {
            warnings.Add($"命运队伍数量异常：{count}。");
            return [];
        }

        var result = new List<PartyPokemon>();
        for (var slot = 0; slot < count; slot++)
        {
            var mon = new PartyPokemon(saveBlock1.Slice(DestinyPartyOffset + slot * PartyPokemon.Size, PartyPokemon.Size), PokemonDataLayout.UnboundCfruPlainParty);
            if (mon.IsEmpty)
            {
                warnings.Add($"命运队伍槽 {slot + 1} 为空，已停止读取后续槽位。");
                break;
            }
            var info = mon.GetInfo();
            if (info.Species == 0 || info.Species > profile.Limits.MaxSpecies || info.Level is < 1 or > 250)
            {
                warnings.Add($"命运队伍槽 {slot + 1} 数据无效（species={info.Species}, level={info.Level}），已停止读取后续槽位。");
                break;
            }
            result.Add(mon);
        }
        return result;
    }

    private static IReadOnlyList<Gen3SaveBagEntry> ReadDestinyBag(
        ReadOnlySpan<byte> saveBlock1,
        ReadOnlySpan<byte> pcStorage,
        IReadOnlyDictionary<int, int> itemPockets,
        int maxItem,
        int maxQuantity,
        List<string> warnings)
    {
        var entries = new List<Gen3SaveBagEntry>();
        var section13Base = 8 * SectionDataSize;
        ReadDestinyPocket(
            pcStorage,
            section13Base + DestinyFireRedSaveStrategy.ItemPocketExtensionOffset,
            Gen3SaveDocument.DestinyExtensionOffsetMarker + DestinyFireRedSaveStrategy.ItemPocketExtensionOffset,
            DestinyFireRedSaveStrategy.ItemPocketCapacity,
            1,
            "道具",
            itemPockets,
            maxItem,
            maxQuantity,
            entries,
            warnings);
        ReadDestinyPocket(
            pcStorage,
            section13Base + DestinyFireRedSaveStrategy.BallPocketExtensionOffset,
            Gen3SaveDocument.DestinyExtensionOffsetMarker + DestinyFireRedSaveStrategy.BallPocketExtensionOffset,
            DestinyFireRedSaveStrategy.BallPocketCapacity,
            3,
            "精灵球",
            itemPockets,
            maxItem,
            maxQuantity,
            entries,
            warnings);
        ReadDestinyPocket(
            saveBlock1,
            DestinyFireRedSaveStrategy.MachineBoxOffset,
            DestinyFireRedSaveStrategy.MachineBoxOffset,
            DestinyFireRedSaveStrategy.MachineBoxCapacity,
            4,
            "招式机盒",
            itemPockets,
            maxItem,
            maxQuantity,
            entries,
            warnings);

        if (entries.Count == 0)
            warnings.Add("命运背包读取：三个已确认区域中没有识别到可显示道具。");
        return entries;
    }

    private static void ReadDestinyPocket(
        ReadOnlySpan<byte> data,
        int dataOffset,
        int saveOffset,
        int capacity,
        int pocket,
        string name,
        IReadOnlyDictionary<int, int> itemPockets,
        int maxItem,
        int maxQuantity,
        List<Gen3SaveBagEntry> entries,
        List<string> warnings)
    {
        var displayed = 0;
        for (var i = 0; i < capacity; i++)
        {
            var offset = dataOffset + i * 4;
            if (offset + 4 > data.Length) break;
            var item = ReadU16(data, offset);
            if (item == 0) break;
            var quantity = ReadU16(data, offset + 2);
            if (item > maxItem || quantity == 0 || quantity > maxQuantity)
            {
                warnings.Add($"命运{name}槽 {i + 1} 数据异常（item={item}, quantity={quantity}）。");
            }
            else if (itemPockets.TryGetValue(item, out var mappedPocket) && mappedPocket != pocket)
            {
                warnings.Add($"命运{name}槽 {i + 1} 含错误分类道具 {item}；保留显示以便修复。");
            }

            entries.Add(new Gen3SaveBagEntry(
                saveOffset + i * 4,
                pocket,
                ++displayed,
                item,
                quantity,
                0,
                true,
                $"命运存档；{name}槽 {i + 1}"));
        }

        warnings.Add($"命运背包读取：{name}显示 {displayed} 格，容量 {capacity} 格。");
    }

    private static IReadOnlyList<Gen3SaveBoxEntry> ReadDestinyBoxes(ReadOnlySpan<byte> pcStorage, GameProfile profile, List<string> warnings)
    {
        var entries = new List<Gen3SaveBoxEntry>();
        var required = StandardPcBoxesOffset + profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots * BoxPokemon.Size;
        if (pcStorage.Length < required)
        {
            warnings.Add("命运 PC 箱子 section 数据长度不足。");
            return entries;
        }

        for (var globalSlot = 0; globalSlot < profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots; globalSlot++)
        {
            var offset = StandardPcBoxesOffset + globalSlot * BoxPokemon.Size;
            var mon = new BoxPokemon(pcStorage.Slice(offset, BoxPokemon.Size));
            if (mon.IsEmpty) continue;
            if (!IsValidBoxMon(mon))
            {
                var boxNumber = globalSlot / profile.Memory.PcBoxSlots + 1;
                var slotInBox = globalSlot % profile.Memory.PcBoxSlots + 1;
                warnings.Add($"命运箱{boxNumber:00}-{slotInBox:00}存在非空但未通过标准 80 字节结构校验的数据，箱子功能保持禁用。");
                continue;
            }
            entries.Add(new Gen3SaveBoxEntry(
                globalSlot + 1,
                globalSlot / profile.Memory.PcBoxSlots + 1,
                globalSlot % profile.Memory.PcBoxSlots + 1,
                offset,
                mon));
        }
        return entries;
    }

    private static PartyReadResult ReadParty(ReadOnlySpan<byte> saveBlock1, List<string> warnings)
    {
        if (saveBlock1.Length > StandardPartyOffset)
        {
            var count = saveBlock1[StandardPartyCountOffset];
            var bytes = HexBytes(saveBlock1.Slice(StandardPartyCountOffset, Math.Min(8, saveBlock1.Length - StandardPartyCountOffset)));
            warnings.Add($"队伍标准偏移探测：count@0x{StandardPartyCountOffset:X}=0x{count:X2}，附近字节={bytes}。");
        }

        var standard = TryReadPartyAt(saveBlock1, StandardPartyCountOffset, StandardPartyOffset);
        if (standard.Count > 0)
            return new PartyReadResult(standard, StandardPartyCountOffset, StandardPartyOffset);

        var best = new List<PartyPokemon>();
        var bestCountOffset = -1;
        for (var countOffset = 0; countOffset + 4 + Gen3Constants.PartyMonSize <= saveBlock1.Length; countOffset++)
        {
            var count = saveBlock1[countOffset];
            if (count is < 1 or > Gen3Constants.PartySlots) continue;
            var candidate = TryReadPartyAt(saveBlock1, countOffset, countOffset + 4);
            if (candidate.Count > best.Count)
            {
                best = candidate.ToList();
                bestCountOffset = countOffset;
            }
            if (best.Count == Gen3Constants.PartySlots)
                break;
        }

        if (best.Count > 0)
            warnings.Add($"队伍不是在标准 Emerald 偏移读取到的，已使用只读扫描结果：count@0x{bestCountOffset:X}，party@0x{bestCountOffset + 4:X}。");
        else
            ProbePartyLikeMons(saveBlock1, warnings);
        return new PartyReadResult(best, bestCountOffset, bestCountOffset < 0 ? -1 : bestCountOffset + 4);
    }

    private static IReadOnlyList<PartyPokemon> TryReadPartyAt(ReadOnlySpan<byte> data, int countOffset, int partyOffset)
    {
        if (countOffset < 0 || countOffset >= data.Length) return [];
        var count = data[countOffset];
        if (count is < 1 or > Gen3Constants.PartySlots) return [];
        if (partyOffset < 0 || partyOffset + count * Gen3Constants.PartyMonSize > data.Length) return [];

        var rows = new List<PartyPokemon>();
        for (var i = 0; i < count; i++)
        {
            var mon = new PartyPokemon(data.Slice(partyOffset + i * Gen3Constants.PartyMonSize, Gen3Constants.PartyMonSize));
            if (!IsValidPartyMon(mon)) return [];
            rows.Add(mon);
        }

        return rows;
    }

    private static IReadOnlyList<Gen3SaveBoxEntry> ReadBoxes(ReadOnlySpan<byte> pcStorage, List<string> warnings)
    {
        var required = StandardPcBoxesOffset + BoxScanner.MaxBoxes * BoxScanner.BoxSlots * BoxPokemon.Size;
        if (pcStorage.Length < required)
        {
            warnings.Add("PC 箱子 section 数据长度不足。");
            return [];
        }

        var entries = new List<Gen3SaveBoxEntry>();
        var rejected = new List<string>();
        for (var globalSlot = 0; globalSlot < BoxScanner.MaxBoxes * BoxScanner.BoxSlots; globalSlot++)
        {
            var offset = StandardPcBoxesOffset + globalSlot * BoxPokemon.Size;
            var mon = new BoxPokemon(pcStorage.Slice(offset, BoxPokemon.Size));
            if (mon.IsEmpty) continue;
            var boxNumber = globalSlot / BoxScanner.BoxSlots + 1;
            var slotInBox = globalSlot % BoxScanner.BoxSlots + 1;
            if (!IsValidBoxMon(mon))
            {
                rejected.Add(BoxRejectReason(mon, boxNumber, slotInBox));
                continue;
            }
            entries.Add(new Gen3SaveBoxEntry(globalSlot + 1, boxNumber, slotInBox, offset, mon));
        }

        if (rejected.Count > 0)
            warnings.Add($"箱子读取：发现 {rejected.Count} 个非空但无效的槽位：{string.Join("；", rejected.Take(20))}" +
                         (rejected.Count > 20 ? "；其余已省略。" : ""));

        return entries;
    }

    private static string BoxRejectReason(BoxPokemon mon, int boxNumber, int slotInBox)
    {
        try
        {
            var info = mon.GetInfo();
            var reasons = new List<string>();
            if (info.Checksum != info.CalculatedChecksum) reasons.Add("单体checksum不匹配");
            if (info.Species is < 1 or > MaxSpecies) reasons.Add($"物种={info.Species}");
            if (reasons.Count == 0) reasons.Add("结构异常");
            return $"箱{boxNumber:00}-{slotInBox:00}({string.Join(",", reasons)})";
        }
        catch (Exception ex)
        {
            return $"箱{boxNumber:00}-{slotInBox:00}(解析失败:{ex.GetType().Name})";
        }
    }

    private static IReadOnlyList<Gen3SaveBagEntry> ReadBag(ReadOnlySpan<byte> saveBlock1, List<string> warnings)
    {
        var entries = new List<Gen3SaveBagEntry>();
        var displaySlots = new Dictionary<int, int>();
        var quantityKey = InferSaveBagQuantityKey(saveBlock1);
        warnings.Add($"背包读取：使用 ROM 背包物理表，数量密钥 0x{quantityKey:X4}，数量=(raw^key)+1。");

        foreach (var physical in SaveBagPhysicalPockets)
        {
            var displayed = 0;
            ushort lastMachineItem = 0;
            for (var i = 0; i < physical.Capacity; i++)
            {
                var offset = physical.Offset + i * 4;
                if (offset + 4 > saveBlock1.Length) break;
                var item = ReadU16(saveBlock1, offset);
                if (item is 0 or > MaxItem) continue;

                var pocket = physical.FixedPocket ?? SavePocketOfItem(item);
                if (pocket is null or < 1 or > 8) continue;
                if (physical.FixedPocket == MachinePocket)
                {
                    if (!IsMachineItem(item)) continue;
                    if (lastMachineItem != 0 && item <= lastMachineItem) break;
                    lastMachineItem = item;
                }

                if (!physical.HasQuantity || pocket.Value == 8)
                {
                    var slotInDisplayPocket = NextDisplaySlot(displaySlots, pocket.Value);
                    displayed++;
                    entries.Add(new Gen3SaveBagEntry(
                        offset,
                        pocket.Value,
                        slotInDisplayPocket,
                        item,
                        1,
                        0,
                        false,
                        $"ROM背包表；{physical.Name} 0x{physical.Offset:X}+{i}"));
                    continue;
                }

                var rawQty = ReadU16(saveBlock1, offset + 2);
                var decoded = rawQty ^ quantityKey;
                if (decoded >= SpanishRocketSaveBagMaxQuantity) continue;
                var displaySlot = NextDisplaySlot(displaySlots, pocket.Value);
                displayed++;
                entries.Add(new Gen3SaveBagEntry(
                    offset,
                    pocket.Value,
                    displaySlot,
                    item,
                    (ushort)(decoded + 1),
                    quantityKey,
                    true,
                    $"ROM背包表；{physical.Name} 0x{physical.Offset:X}+{i}"));
            }

            if (displayed > 0)
                warnings.Add($"背包读取：{physical.Name} offset=0x{physical.Offset:X}，容量 {physical.Capacity} 格，显示 {displayed} 格。");
        }

        if (entries.Count == 0)
            warnings.Add("背包读取：ROM 背包物理表中没有识别到可显示道具。");
        return entries;
    }

    private static int NextDisplaySlot(Dictionary<int, int> displaySlots, int pocket)
    {
        var slot = displaySlots.GetValueOrDefault(pocket) + 1;
        displaySlots[pocket] = slot;
        return slot;
    }

    private static ushort InferSaveBagQuantityKey(ReadOnlySpan<byte> data)
    {
        var values = new Dictionary<ushort, int>();
        foreach (var physical in SaveBagPhysicalPockets.Where(p => p.HasQuantity && p.FixedPocket != MachinePocket))
        {
            for (var i = 0; i < physical.Capacity && physical.Offset + i * 4 + 4 <= data.Length; i++)
            {
                var offset = physical.Offset + i * 4;
                if (ReadU16(data, offset) is 0 or > MaxItem) continue;
                var raw = ReadU16(data, offset + 2);
                values[raw] = values.GetValueOrDefault(raw) + 1;
            }
        }

        if (values.Count > 0)
            return values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;

        var machine = SaveBagPhysicalPockets.First(p => p.FixedPocket == MachinePocket);
        return InferMostCommonRawQuantity(data, machine.Offset, machine.Capacity, nonEmptyOnly: true);
    }

    private static IReadOnlyList<SaveBagRun> FindDecodedBagRuns(
        ReadOnlySpan<byte> data,
        ushort quantityKey,
        HashSet<int> occupiedOffsets)
    {
        var runs = new List<SaveBagRun>();
        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            if (occupiedOffsets.Contains(offset) || IsPartyDataOffset(offset)) continue;
            var slots = new List<SaveBagSlot>();
            var cursor = offset;
            int? pocket = null;
            while (cursor + 4 <= data.Length &&
                   !occupiedOffsets.Contains(cursor) &&
                   !IsPartyDataOffset(cursor) &&
                   TryParseSaveBagSlot(data, cursor, quantityKey, out var slot))
            {
                var slotPocket = SavePocketOfItem(slot.Item);
                if (slotPocket is null or 7 or 8) break;
                if (pocket is null)
                    pocket = slotPocket.Value;
                else if (pocket.Value != slotPocket.Value)
                    break;

                slots.Add(slot);
                cursor += 4;
            }

            if (slots.Count >= 2 && pocket is not null && !LooksLikeSequentialIndexTable(slots))
            {
                var note = $"数量密钥 0x{quantityKey:X4}，数量=(raw^key)+1";
                runs.Add(new SaveBagRun(offset, pocket.Value, slots.ToArray(), note));
                offset = cursor - 4;
            }
        }

        return runs
            .OrderBy(r => r.Pocket)
            .ThenBy(r => r.Offset)
            .ToArray();
    }

    private static bool TryParseSaveBagSlot(ReadOnlySpan<byte> data, int offset, ushort quantityKey, out SaveBagSlot slot)
    {
        var item = ReadU16(data, offset);
        var rawQuantity = ReadU16(data, offset + 2);
        slot = new SaveBagSlot(0, 0, 0, 0, 0, false);
        if (item is 0 or > MaxItem) return false;

        var quantityXor = false;
        ushort quantity;
        if (quantityKey != 0)
        {
            var decoded = (ushort)(rawQuantity ^ quantityKey);
            if (decoded >= SpanishRocketSaveBagMaxQuantity) return false;
            quantity = (ushort)(decoded + 1);
            quantityXor = true;
        }
        else if (rawQuantity is > 0 and <= SpanishRocketSaveBagMaxQuantity)
        {
            quantity = rawQuantity;
        }
        else
        {
            return false;
        }

        slot = new SaveBagSlot(offset, item, rawQuantity, quantity, quantityKey, quantityXor);
        return true;
    }

    private static bool LooksLikeSequentialIndexTable(IReadOnlyList<SaveBagSlot> slots)
    {
        if (slots.Count < 8) return false;
        var sequential = 0;
        for (var i = 1; i < slots.Count; i++)
        {
            if (slots[i].Item == slots[i - 1].Item + 1)
                sequential++;
        }

        return sequential >= slots.Count - 1;
    }

    private static bool IsPartyDataOffset(int offset)
        => offset >= StandardPartyOffset && offset < StandardPartyOffset + Gen3Constants.PartySlots * Gen3Constants.PartyMonSize;

    internal static int? SavePocketOfItem(int item)
    {
        if (item is <= 0 or > MaxItem) return null;
        foreach (var (start, end, pocket) in SaveItemPocketRanges)
        {
            if (item >= start && item <= end)
                return pocket;
        }

        return null;
    }

    private static string SavePocketName(int pocket) => pocket switch
    {
        1 => "普通道具",
        2 => "回复药品",
        3 => "精灵球",
        4 => "战斗道具",
        5 => "树果",
        6 => "宝物",
        7 => "招式机器/秘传机器",
        8 => "重要物品",
        _ => $"口袋{pocket}"
    };

    private static (int Offset, int Count)? FindBestMachineRun(ReadOnlySpan<byte> data)
    {
        (int Offset, int Count)? bestFromFirstTm = null;
        (int Offset, int Count)? best = null;
        for (var offset = 0; offset + 8 <= data.Length; offset += 4)
        {
            var first = ReadU16(data, offset);
            if (!IsMachineItem(first)) continue;
            var count = 1;
            while (offset + (count + 1) * 4 <= data.Length)
            {
                var item = ReadU16(data, offset + count * 4);
                var previous = ReadU16(data, offset + (count - 1) * 4);
                if (!IsMachineItem(item) || item <= previous) break;
                count++;
            }

            if (count < 4) continue;
            if (first == MachineTmStartItem && (bestFromFirstTm is null || count > bestFromFirstTm.Value.Count))
                bestFromFirstTm = (offset, count);
            if (best is null || count > best.Value.Count)
                best = (offset, count);
        }

        return bestFromFirstTm ?? best;
    }

    private static bool IsMachineItem(int item)
        => item is >= MachineTmStartItem and < MachineTmStartItem + MachineTmCount ||
           item is >= MachineHmStartItem and < MachineHmStartItem + MachineHmCount;

    private static (int Offset, IReadOnlyList<SaveKeyItemSlot> Items)? FindBestKeyItemRun(ReadOnlySpan<byte> data, HashSet<int> occupiedOffsets)
    {
        (int Offset, IReadOnlyList<SaveKeyItemSlot> Items)? best = null;
        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            if (occupiedOffsets.Contains(offset) || IsPartyDataOffset(offset)) continue;
            var items = new List<SaveKeyItemSlot>();
            var cursor = offset;
            while (cursor + 4 <= data.Length && !occupiedOffsets.Contains(cursor) && !IsPartyDataOffset(cursor))
            {
                var item = ReadU16(data, cursor);
                if (SavePocketOfItem(item) != 8) break;
                items.Add(new SaveKeyItemSlot(cursor, item));
                cursor += 4;
            }

            if (items.Count >= 2 && (best is null || ScoreKeyItemRun(items) > ScoreKeyItemRun(best.Value.Items)))
                best = (offset, items.ToArray());
        }

        return best;
    }

    private static int ScoreKeyItemRun(IReadOnlyList<SaveKeyItemSlot> items)
    {
        var score = items.Count * 10;
        if (items[0].Item == 899) score += 100;
        if (items[^1].Item == 846) score += 100;
        return score;
    }

    private static ushort InferMostCommonRawQuantity(ReadOnlySpan<byte> data, int offset, int count, bool nonEmptyOnly = false)
    {
        var values = new Dictionary<ushort, int>();
        for (var i = 0; i < count && offset + i * 4 + 4 <= data.Length; i++)
        {
            if (nonEmptyOnly && ReadU16(data, offset + i * 4) == 0) continue;
            var raw = ReadU16(data, offset + i * 4 + 2);
            values[raw] = values.GetValueOrDefault(raw) + 1;
        }

        return values.Count == 0
            ? (ushort)0
            : values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    private static void ProbePartyLikeMons(ReadOnlySpan<byte> saveBlock1, List<string> warnings)
    {
        var candidates = new List<string>();
        for (var offset = 0; offset + Gen3Constants.PartyMonSize <= saveBlock1.Length; offset += 4)
        {
            var mon = new PartyPokemon(saveBlock1.Slice(offset, Gen3Constants.PartyMonSize));
            if (!IsValidPartyMon(mon)) continue;
            var info = mon.GetInfo();
            candidates.Add($"0x{offset:X}: species={info.Species}, level={info.Level}, hp={info.Hp}/{info.MaxHp}");
            if (candidates.Count >= 8) break;
        }

        if (candidates.Count == 0)
            warnings.Add("队伍探测：SaveBlock1 中没有找到可单独解析的 party-mon 结构。");
        else
            warnings.Add("队伍探测：找到可单独解析的 party-mon 候选：" + string.Join("；", candidates));
    }

    private static void ProbeBagCandidates(ReadOnlySpan<byte> saveBlock1, List<string> warnings)
    {
        foreach (var (name, offset) in BagProbeOffsets)
        {
            if (offset + 4 > saveBlock1.Length) continue;
            var sample = BagSlotSample(saveBlock1, offset, 6);
            warnings.Add($"背包固定偏移探测：{name}@0x{offset:X} {sample}。");
        }

        var runs = new List<(int Offset, int Count, string Sample)>();
        for (var offset = 0; offset + 4 <= saveBlock1.Length; offset += 4)
        {
            var count = 0;
            while (offset + (count + 1) * 4 <= saveBlock1.Length)
            {
                var item = ReadU16(saveBlock1, offset + count * 4);
                var qtyRaw = ReadU16(saveBlock1, offset + count * 4 + 2);
                if (item == 0) break;
                if (item > MaxItem || qtyRaw == 0) break;
                count++;
                if (count >= 120) break;
            }

            if (count >= 3)
                runs.Add((offset, count, BagSlotSample(saveBlock1, offset, Math.Min(4, count))));
        }

        foreach (var run in runs
                     .OrderByDescending(r => r.Count)
                     .ThenBy(r => r.Offset)
                     .Take(8))
            warnings.Add($"背包连续槽候选：offset=0x{run.Offset:X}，连续 {run.Count} 格，{run.Sample}。");

        if (runs.Count == 0)
            warnings.Add("背包连续槽探测：没有找到明显的 {道具,数量raw} 连续候选。");
    }

    private static string BagSlotSample(ReadOnlySpan<byte> data, int offset, int maxSlots)
    {
        var parts = new List<string>();
        for (var i = 0; i < maxSlots && offset + i * 4 + 4 <= data.Length; i++)
        {
            var item = ReadU16(data, offset + i * 4);
            var qtyRaw = ReadU16(data, offset + i * 4 + 2);
            parts.Add($"[{i + 1}]item={item},raw=0x{qtyRaw:X4}");
        }

        return string.Join(" ", parts);
    }

    private static string HexBytes(ReadOnlySpan<byte> data)
    {
        var parts = new string[data.Length];
        for (var i = 0; i < data.Length; i++)
            parts[i] = data[i].ToString("X2");
        return string.Join(" ", parts);
    }

    private static string SectionIdList(SaveSlot slot)
        => string.Join(",", slot.ValidSections.Keys.OrderBy(id => id));

    private static bool IsValidPartyMon(PartyPokemon mon)
    {
        if (mon.IsEmpty) return false;
        try
        {
            var info = mon.GetInfo();
            return info.Checksum == info.CalculatedChecksum &&
                   info.Species is >= 1 and <= MaxSpecies &&
                   info.Level is > 0 and <= 250;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidBoxMon(BoxPokemon mon)
    {
        try
        {
            var info = mon.GetInfo();
            return info.Checksum == info.CalculatedChecksum &&
                   info.Species is >= 1 and <= MaxSpecies;
        }
        catch
        {
            return false;
        }
    }

    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 3 < data.Length; i += 4)
            sum += ReadU32(data, i);
        return (ushort)(((sum >> 16) + (sum & 0xFFFF)) & 0xFFFF);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | data[offset + 1] << 8);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private sealed record SaveSection(int Id, int Offset, uint SaveIndex, byte[] Data, string ChecksumMode);

    private sealed record RelaxedSectionHeader(
        int Id,
        int Offset,
        uint SaveIndex,
        uint Signature,
        ushort StoredChecksum,
        ushort CalculatedChecksum,
        byte[] Data);

    private sealed record SaveBagSlot(
        int Offset,
        ushort Item,
        ushort RawQuantity,
        ushort Quantity,
        ushort QuantityKey,
        bool QuantityXor);

    private sealed record SaveBagRun(int Offset, int Pocket, IReadOnlyList<SaveBagSlot> Slots, string Note);

    private sealed record SaveKeyItemSlot(int Offset, ushort Item);

    private sealed record PartyReadResult(IReadOnlyList<PartyPokemon> Party, int CountOffset, int PartyOffset);

    private sealed record SaveSlot(int Ordinal, int Offset, IReadOnlyDictionary<int, SaveSection> ValidSections)
    {
        public uint SaveIndex => ValidSections.Count == 0 ? 0 : ValidSections.Values.Max(s => s.SaveIndex);
    }
}
