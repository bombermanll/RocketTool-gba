using System.Buffers.Binary;

namespace RocketTool.Core;

internal sealed record Gen3SaveSectionLayout(int Id, int FileOffset, uint SaveIndex, string ChecksumMode);

public sealed record Gen3SaveWriteResult(
    Gen3SaveReadResult Snapshot,
    string DestinationPath,
    string? BackupPath,
    bool BackupCreated);

public sealed class Gen3SaveDocument
{
    internal const int SectionSize = 0x1000;
    internal const int SectionDataSize = 0xF80;
    private const int SectionTrailerOffset = 0xFF4;
    private const int SectionChecksumOffset = 0xFF6;
    private const int PcTailChecksumSize = 0x7D0;
    private const int PcBoxesChecksumSize = 0x744;
    private const int SectionsPerSlot = 14;
    internal const int SaveBlock1FirstSection = 1;
    internal const int PcFirstSection = 5;
    internal const int PcStorageOffset = 4;
    internal const int UnboundParasiteOffsetMarker = 0x100000;
    internal const int DestinyExtensionOffsetMarker = 0x200000;
    internal const int RadicalRedExtensionOffsetMarker = 0x300000;
    internal const int UnboundBoxRecordSize = BoxPokemon.UnboundCompressedSize;
    private const int MgbaRtcFooterOffset = 0x20000;
    private const int MgbaRtcFooterSize = 16;

    private readonly byte[] _raw;
    private readonly byte[] _originalRaw;
    private readonly IReadOnlyDictionary<int, Gen3SaveSectionLayout> _sections;
    private readonly int _partyCountOffset;
    private readonly int _partyOffset;
    private readonly HashSet<int> _touchedSectionIds = [];
    private readonly Dictionary<int, byte[]> _expectedParty = [];
    private readonly Dictionary<int, byte[]> _expectedBoxes = [];
    private readonly Dictionary<int, (ushort ItemId, ushort Quantity)> _expectedBag = [];
    private byte[]? _expectedTrainerName;
    private uint? _expectedMoney;
    private readonly List<Gen3SaveBagEntry> _currentBag;
    private readonly GameProfile _profile;
    private readonly IReadOnlyDictionary<int, int> _itemPockets;
    private readonly IGen3SaveStrategy _strategy;
    private bool _hasRawChanges;

    internal Gen3SaveDocument(
        string sourcePath,
        byte[] raw,
        IReadOnlyDictionary<int, Gen3SaveSectionLayout> sections,
        int partyCountOffset,
        int partyOffset,
        Gen3SaveReadResult snapshot,
        GameProfile profile,
        IReadOnlyDictionary<int, int>? itemPockets,
        IGen3SaveStrategy strategy)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        _originalRaw = raw.ToArray();
        _raw = raw.ToArray();
        _sections = sections;
        _partyCountOffset = partyCountOffset;
        _partyOffset = partyOffset;
        Snapshot = snapshot;
        CurrentPartyCount = snapshot.Party.Count;
        _currentBag = snapshot.Bag.ToList();
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _itemPockets = itemPockets ?? new Dictionary<int, int>();
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));

        var missing = Enumerable.Range(0, SectionsPerSlot).Where(id => !_sections.ContainsKey(id)).ToArray();
        var relaxed = _sections.Values.Where(s => s.ChecksumMode == "relaxed").Select(s => s.Id).OrderBy(id => id).ToArray();
        if (missing.Length > 0)
            WriteBlockReason = $"存档缺少 section {string.Join(",", missing)}。";
        else if (relaxed.Length > 0)
            WriteBlockReason = $"section {string.Join(",", relaxed)} 仅通过宽松模式读取，不能安全写回。";
    }

    public string SourcePath { get; }
    public Gen3SaveReadResult Snapshot { get; }
    public bool CanWrite => string.IsNullOrEmpty(WriteBlockReason);
    public string? WriteBlockReason { get; }
    public bool HasChanges => _touchedSectionIds.Count > 0 || _hasRawChanges;
    public int CurrentPartyCount { get; private set; }
    public int ModifiedPartyCount => _expectedParty.Count;
    public int ModifiedBoxCount => _expectedBoxes.Count;
    public int ModifiedBagCount => _expectedBag.Count;
    public bool TrainerModified => _expectedTrainerName is not null || _expectedMoney.HasValue;
    public IReadOnlyList<Gen3SaveBagEntry> CurrentBag => _currentBag;
    internal GameProfile Profile => _profile;
    internal IReadOnlyDictionary<int, int> ItemPockets => _itemPockets;

    public void ReplaceTrainerName(ReadOnlySpan<byte> nameBytes)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Trainer);
        _strategy.ReplaceTrainerName(this, nameBytes);
        _expectedTrainerName = nameBytes.ToArray();
    }

    public void ReplaceTrainerMoney(uint money)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Trainer);
        _strategy.ReplaceTrainerMoney(this, money);
        _expectedMoney = money;
    }

    public int PartySaveOffset(int slot)
    {
        if (_partyOffset < 0)
            throw new InvalidOperationException("存档队伍仅通过不确定结构读取，不能定位写回位置。");
        if (slot < 1 || slot > CurrentPartyCount)
            throw new ArgumentOutOfRangeException(nameof(slot), $"队伍槽位必须在 1..{CurrentPartyCount} 之间。");
        return _partyOffset + (slot - 1) * PartyPokemon.Size;
    }

    public void ReplacePartyPokemon(int slot, PartyPokemon mon)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Party);
        ValidatePartyPokemon(mon);
        var saveOffset = PartySaveOffset(slot);
        WriteLogicalRange(SaveBlock1FirstSection, saveOffset, mon.Raw);
        _expectedParty[slot] = mon.Raw.ToArray();
    }

    public int AppendPartyPokemon(PartyPokemon mon)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Party);
        if (_partyCountOffset < 0 || _partyOffset < 0)
            throw new InvalidOperationException("存档队伍没有已验证的数量和数组偏移，不能新增宝可梦。");
        if (CurrentPartyCount >= Gen3Constants.PartySlots)
            throw new InvalidOperationException("队伍已满，无法导入新的宝可梦。");

        ValidatePartyPokemon(mon);
        var slot = CurrentPartyCount + 1;
        var saveOffset = _partyOffset + (slot - 1) * PartyPokemon.Size;
        WriteLogicalRange(SaveBlock1FirstSection, saveOffset, mon.Raw);
        WriteLogicalRange(SaveBlock1FirstSection, _partyCountOffset, [(byte)slot]);
        _expectedParty[slot] = mon.Raw.ToArray();
        CurrentPartyCount = slot;
        return slot;
    }

    public void RemovePartyPokemon(int slot)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Party);
        if (_partyCountOffset < 0 || _partyOffset < 0)
            throw new InvalidOperationException("存档队伍没有已验证的数量和数组偏移，不能删除宝可梦。");
        if (slot < 1 || slot > CurrentPartyCount)
            throw new ArgumentOutOfRangeException(nameof(slot), $"队伍槽位必须在 1..{CurrentPartyCount} 之间。");

        for (var sourceSlot = slot + 1; sourceSlot <= CurrentPartyCount; sourceSlot++)
        {
            var raw = new byte[PartyPokemon.Size];
            ReadLogicalRange(
                SaveBlock1FirstSection,
                _partyOffset + (sourceSlot - 1) * PartyPokemon.Size,
                raw);
            WriteLogicalRange(
                SaveBlock1FirstSection,
                _partyOffset + (sourceSlot - 2) * PartyPokemon.Size,
                raw);
            _expectedParty[sourceSlot - 1] = raw;
        }

        var oldLastSlot = CurrentPartyCount;
        WriteLogicalRange(
            SaveBlock1FirstSection,
            _partyOffset + (oldLastSlot - 1) * PartyPokemon.Size,
            new byte[PartyPokemon.Size]);
        var newCount = oldLastSlot - 1;
        WriteLogicalRange(SaveBlock1FirstSection, _partyCountOffset, [(byte)newCount]);
        _expectedParty[oldLastSlot] = new byte[PartyPokemon.Size];
        CurrentPartyCount = newCount;
    }

    public void ReplaceBoxPokemon(int globalSlot, BoxPokemon mon)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Boxes);
        var boxCount = _profile.Memory.PcBoxCount;
        var boxSlots = _profile.Memory.PcBoxSlots;
        if (globalSlot < 1 || globalSlot > boxCount * boxSlots)
            throw new ArgumentOutOfRangeException(nameof(globalSlot), "箱子槽位超出范围。");
        ValidateBoxPokemon(mon);
        _strategy.WriteBoxPokemon(this, globalSlot, mon.Raw);
        _expectedBoxes[globalSlot] = mon.Raw.ToArray();
    }

    public void ClearBoxPokemon(int globalSlot)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Boxes);
        var totalSlots = _profile.Memory.PcBoxCount * _profile.Memory.PcBoxSlots;
        if (globalSlot < 1 || globalSlot > totalSlots)
            throw new ArgumentOutOfRangeException(nameof(globalSlot), "箱子槽位超出范围。");
        var empty = new byte[_profile.Memory.PcBoxRecordSize];
        _strategy.WriteBoxPokemon(this, globalSlot, empty);
        _expectedBoxes[globalSlot] = empty;
    }

    public Gen3SaveBagEntry? ReplaceBagEntry(int saveOffset, ushort itemId, ushort quantity)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Bag);
        var index = _currentBag.FindIndex(entry => entry.SaveOffset == saveOffset);
        if (index < 0)
            throw new InvalidOperationException($"存档背包中找不到偏移 0x{saveOffset:X} 的道具格。请重新读取存档。");

        var current = _currentBag[index];
        if (itemId == 0)
        {
            ValidateBagValue(current.Pocket, itemId, quantity, allowEmpty: true);
            WriteBagRange(saveOffset, new byte[4]);
            _currentBag.RemoveAt(index);
            _expectedBag[saveOffset] = (0, 0);
            ReindexBagEntries();
            return null;
        }

        if (itemId == current.ItemId)
            ValidateBagQuantity(current.Pocket, quantity);
        else
            ValidateBagValue(current.Pocket, itemId, quantity, allowEmpty: false);

        var storedQuantity = current.QuantityXor ? _strategy.EncodeStoredQuantity(quantity, current.QuantityKey) : (ushort)0;
        WriteBagRecord(saveOffset, itemId, storedQuantity);
        var updated = current with { ItemId = itemId, Quantity = current.QuantityXor ? quantity : (ushort)1 };
        _currentBag[index] = updated;
        _expectedBag[saveOffset] = (updated.ItemId, updated.Quantity);
        return updated;
    }

    public Gen3SaveBagEntry AddBagItem(int pocket, ushort itemId, ushort quantity)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Bag);
        ValidateBagValue(pocket, itemId, quantity, allowEmpty: false);

        var existing = _currentBag.FirstOrDefault(entry => entry.Pocket == pocket && entry.ItemId == itemId);
        if (existing is not null)
        {
            var maxQuantity = MaxBagQuantityForPocket(pocket);
            var after = existing.QuantityXor
                ? (ushort)Math.Min(maxQuantity, existing.Quantity + quantity)
                : (ushort)1;
            return ReplaceBagEntry(existing.SaveOffset, itemId, after)
                   ?? throw new InvalidOperationException("更新现有背包道具失败。");
        }

        if (pocket == 7)
            return InsertMachine(itemId, quantity);

        var physicalPockets = _strategy.CandidatePhysicalPockets(pocket).ToArray();
        if (physicalPockets.Length == 0)
            throw new InvalidOperationException($"存档中的口袋 {pocket} 没有可用物理区域。");

        foreach (var physical in physicalPockets)
        {
            var slotIndex = FindAppendBagSlotIndex(physical);
            if (slotIndex < 0) continue;

            var offset = physical.Offset + slotIndex * 4;
            var quantityXor = physical.HasQuantity && pocket != 8;
            var quantityKey = quantityXor ? InferQuantityKey() : (ushort)0;
            var storedQuantity = quantityXor ? _strategy.EncodeStoredQuantity(quantity, quantityKey) : (ushort)0;
            WriteBagRecord(offset, itemId, storedQuantity);
            var entry = new Gen3SaveBagEntry(
                offset,
                pocket,
                0,
                itemId,
                quantityXor ? quantity : (ushort)1,
                quantityKey,
                quantityXor,
                $"存档新增；{physical.Name} 0x{physical.Offset:X}+{slotIndex}");
            _currentBag.Add(entry);
            _expectedBag[offset] = (entry.ItemId, entry.Quantity);
            ReindexBagEntries();
            return _currentBag.First(candidate => candidate.SaveOffset == offset);
        }

        throw new InvalidOperationException($"{_strategy.PocketName(pocket)}没有空格，无法添加新道具。");
    }

    private int FindAppendBagSlotIndex(Gen3SaveBagPhysicalPocket physical)
    {
        var lastUsedIndex = _currentBag
            .Where(entry => entry.SaveOffset >= physical.Offset &&
                            entry.SaveOffset < physical.Offset + physical.Capacity * 4)
            .Select(entry => (entry.SaveOffset - physical.Offset) / 4)
            .DefaultIfEmpty(-1)
            .Max();

        for (var i = lastUsedIndex + 1; i < physical.Capacity; i++)
        {
            var offset = physical.Offset + i * 4;
            if (ReadBagU16(offset) == 0) return i;
        }

        return -1;
    }

    public Gen3SaveBagEntry SetBagItem(int pocket, ushort itemId, ushort quantity)
    {
        var existing = _currentBag.FirstOrDefault(entry => entry.Pocket == pocket && entry.ItemId == itemId);
        return existing is null
            ? AddBagItem(pocket, itemId, quantity)
            : ReplaceBagEntry(existing.SaveOffset, itemId, pocket == 8 ? (ushort)1 : quantity)
              ?? throw new InvalidOperationException("设置背包道具失败。");
    }

    public (int Added, int Updated) SetBagItems(int pocket, IEnumerable<ushort> itemIds, ushort quantity)
    {
        EnsureWritable();
        EnsureProfileWrite(GameDataSurface.Bag);
        var items = itemIds.Distinct().ToArray();
        var rawBackup = _raw.ToArray();
        var bagBackup = _currentBag.ToList();
        var expectedBackup = _expectedBag.ToDictionary(entry => entry.Key, entry => entry.Value);
        var touchedBackup = _touchedSectionIds.ToHashSet();
        var rawChangesBackup = _hasRawChanges;
        var existingItems = _currentBag.Where(entry => entry.Pocket == pocket).Select(entry => entry.ItemId).ToHashSet();
        try
        {
            foreach (var itemId in items)
                SetBagItem(pocket, itemId, quantity);
            return (items.Count(item => !existingItems.Contains(item)), items.Count(existingItems.Contains));
        }
        catch
        {
            rawBackup.CopyTo(_raw, 0);
            _currentBag.Clear();
            _currentBag.AddRange(bagBackup);
            _expectedBag.Clear();
            foreach (var entry in expectedBackup) _expectedBag[entry.Key] = entry.Value;
            _touchedSectionIds.Clear();
            foreach (var id in touchedBackup) _touchedSectionIds.Add(id);
            _hasRawChanges = rawChangesBackup;
            throw;
        }
    }

    public Gen3SaveReadResult SaveAs(string outputPath)
    {
        EnsureWritable();
        if (!HasChanges)
            throw new InvalidOperationException("当前存档没有待导出的修改。");

        var destination = Path.GetFullPath(outputPath);
        if (string.Equals(destination, SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能覆盖原存档，请选择新的文件名。");

        var sourceTimestamps = File.Exists(SourcePath) ? FileTimestamps.Capture(SourcePath) : null;
        var temporary = CreateVerifiedTemporary(destination);
        try
        {
            File.Move(temporary, destination, overwrite: true);
            sourceTimestamps?.TryApply(destination);
            return _strategy.Open(destination, _profile).Snapshot;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public Gen3SaveWriteResult SaveInPlaceWithBackup()
    {
        EnsureWritable();
        if (!HasChanges)
            throw new InvalidOperationException("当前存档没有待保存的修改。");
        if (!File.Exists(SourcePath))
            throw new FileNotFoundException("原存档文件已不存在，已取消保存。", SourcePath);

        var currentSource = File.ReadAllBytes(SourcePath);
        var sourceTimestamps = FileTimestamps.Capture(SourcePath);
        if (!currentSource.AsSpan().SequenceEqual(_originalRaw))
            throw new InvalidOperationException("原存档在读取后已被其他程序修改。为避免覆盖新数据，请重新读取存档后再保存。");

        var temporary = CreateVerifiedTemporary(SourcePath);
        var backupPath = SourcePath + ".bak";
        var backupTemporary = Path.Combine(
            Path.GetDirectoryName(SourcePath) ?? throw new DirectoryNotFoundException("原存档目录不存在。"),
            $".{Path.GetFileName(backupPath)}.{Guid.NewGuid():N}.tmp");
        var backupCreated = false;
        try
        {
            if (!File.Exists(backupPath))
            {
                File.WriteAllBytes(backupTemporary, currentSource);
                if (!File.ReadAllBytes(backupTemporary).AsSpan().SequenceEqual(currentSource))
                    throw new IOException("原始备份写入校验失败，已取消保存。");
                File.Move(backupTemporary, backupPath, overwrite: false);
                backupCreated = true;
            }

            File.Move(temporary, SourcePath, overwrite: true);
            sourceTimestamps.TryApply(SourcePath);
            var snapshot = _strategy.Open(SourcePath, _profile).Snapshot;
            return new Gen3SaveWriteResult(snapshot, SourcePath, backupPath, backupCreated);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            if (File.Exists(backupTemporary))
                File.Delete(backupTemporary);
        }
    }

    private string CreateVerifiedTemporary(string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("存档目录不存在。");

        var output = _raw.ToArray();
        foreach (var id in _touchedSectionIds)
            UpdateSectionChecksum(output, _sections[id]);
        RefreshMgbaRtcFooterIfPresent(output);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, output);
            var verified = _strategy.Open(temporary, _profile);
            VerifyExport(verified);
            return temporary;
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    private static void RefreshMgbaRtcFooterIfPresent(byte[] raw)
    {
        if (raw.Length < MgbaRtcFooterOffset + MgbaRtcFooterSize)
            return;

        var footer = raw.AsSpan(MgbaRtcFooterOffset, MgbaRtcFooterSize);
        if (!LooksLikeMgbaRtcFooter(footer))
            return;

        var now = DateTimeOffset.Now;
        footer[0] = ToBcd(now.Year % 100);
        footer[1] = ToBcd(now.Month);
        footer[2] = ToBcd(now.Day);
        footer[3] = ToBcd((int)now.DayOfWeek);
        footer[4] = ToBcd(now.Hour);
        footer[5] = ToBcd(now.Minute);
        footer[6] = ToBcd(now.Second);
        if (footer[7] == 0xFF)
            footer[7] = 0x40;
        BinaryPrimitives.WriteInt64LittleEndian(footer[8..16], now.ToUnixTimeSeconds());
    }

    private static bool LooksLikeMgbaRtcFooter(ReadOnlySpan<byte> footer)
    {
        if (footer.Length < MgbaRtcFooterSize)
            return false;

        if (!TryFromBcd(footer[0], 0, 99, out _))
            return false;
        if (!TryFromBcd(footer[1], 1, 12, out _))
            return false;
        if (!TryFromBcd(footer[2], 1, 31, out _))
            return false;
        if (!TryFromBcd(footer[3], 0, 6, out _))
            return false;
        if (!TryFromBcd(footer[4], 0, 23, out _))
            return false;
        if (!TryFromBcd(footer[5], 0, 59, out _))
            return false;
        if (!TryFromBcd(footer[6], 0, 59, out _))
            return false;

        var lastLatch = BinaryPrimitives.ReadUInt64LittleEndian(footer[8..16]);
        return lastLatch != 0 && lastLatch != ulong.MaxValue;
    }

    private static bool TryFromBcd(byte value, int min, int max, out int result)
    {
        var high = value >> 4;
        var low = value & 0x0F;
        if (high > 9 || low > 9)
        {
            result = 0;
            return false;
        }

        result = high * 10 + low;
        return result >= min && result <= max;
    }

    private static byte ToBcd(int value)
    {
        return (byte)(((value / 10) << 4) | (value % 10));
    }

    internal void WriteLogicalRange(int firstSectionId, int logicalOffset, ReadOnlySpan<byte> data)
    {
        var sourceOffset = 0;
        while (sourceOffset < data.Length)
        {
            var sectionIndex = logicalOffset / SectionDataSize;
            var sectionId = firstSectionId + sectionIndex;
            var sectionRelativeOffset = logicalOffset % SectionDataSize;
            if (!_sections.TryGetValue(sectionId, out var section))
                throw new InvalidOperationException($"写回需要 section {sectionId}，但当前存档中不存在。");

            var count = Math.Min(data.Length - sourceOffset, SectionDataSize - sectionRelativeOffset);
            data.Slice(sourceOffset, count).CopyTo(_raw.AsSpan(section.FileOffset + sectionRelativeOffset, count));
            _touchedSectionIds.Add(sectionId);
            logicalOffset += count;
            sourceOffset += count;
        }
    }

    internal void WriteUnboundBox(int globalSlot, ReadOnlySpan<byte> data)
    {
        if (data.Length != UnboundBoxRecordSize)
            throw new InvalidOperationException($"解放版箱子记录必须为 {UnboundBoxRecordSize} 字节。");
        var box = (globalSlot - 1) / 30 + 1;
        var slot = (globalSlot - 1) % 30;
        if (box <= 19)
        {
            var logicalOffset = PcStorageOffset + (box - 1) * 30 * UnboundBoxRecordSize + slot * UnboundBoxRecordSize;
            WriteLogicalRange(PcFirstSection, logicalOffset, data);
            return;
        }
        if (box <= 22)
        {
            var boxOffset = box switch { 20 => 0x19D0, 21 => 0x209C, _ => 0x2768 };
            WriteUnboundParasiteRange(boxOffset + slot * UnboundBoxRecordSize, data);
            return;
        }
        if (box <= 24)
        {
            var boxOffset = box == 23 ? 0x1F08 : 0x25D4;
            WriteLogicalRange(SaveBlock1FirstSection, boxOffset + slot * UnboundBoxRecordSize, data);
            return;
        }
        if (box == 25)
        {
            WriteSectionRange(0, 0xB0 + slot * UnboundBoxRecordSize, data);
            return;
        }
        throw new InvalidOperationException($"解放版没有箱子 {box}。");
    }

    internal void WriteRadicalRedBox(int globalSlot, ReadOnlySpan<byte> data)
    {
        if (data.Length != UnboundBoxRecordSize)
            throw new InvalidOperationException($"激进红箱子记录必须为 {UnboundBoxRecordSize} 字节。");
        var box = (globalSlot - 1) / 30 + 1;
        var slot = (globalSlot - 1) % 30;
        if (box <= 19)
        {
            var logicalOffset = PcStorageOffset + (box - 1) * 30 * UnboundBoxRecordSize + slot * UnboundBoxRecordSize;
            WriteRadicalRedLogicalRange(PcFirstSection, logicalOffset, data);
            return;
        }
        if (box <= 22)
        {
            var boxOffset = box switch { 20 => 0x19D0, 21 => 0x209C, _ => 0x2768 };
            WriteRadicalRedExtensionRange(boxOffset + slot * UnboundBoxRecordSize, data);
            return;
        }
        if (box <= 24)
        {
            var boxOffset = box == 23 ? 0x1F08 : 0x25D4;
            WriteRadicalRedLogicalRange(SaveBlock1FirstSection, boxOffset + slot * UnboundBoxRecordSize, data);
            return;
        }
        if (box == 25)
        {
            WriteSectionRange(0, 0xB0 + slot * UnboundBoxRecordSize, data);
            return;
        }
        throw new InvalidOperationException($"激进红没有箱子 {box}。");
    }

    internal void WriteRadicalRedLogicalRange(int firstSectionId, int logicalOffset, ReadOnlySpan<byte> data)
    {
        const int sectionDataSize = 0xFF0;
        var sourceOffset = 0;
        while (sourceOffset < data.Length)
        {
            var sectionIndex = logicalOffset / sectionDataSize;
            var sectionId = firstSectionId + sectionIndex;
            var sectionRelativeOffset = logicalOffset % sectionDataSize;
            if (!_sections.TryGetValue(sectionId, out var section))
                throw new InvalidOperationException($"激进红写回需要 section {sectionId}，但当前存档中不存在。");

            var count = Math.Min(data.Length - sourceOffset, sectionDataSize - sectionRelativeOffset);
            data.Slice(sourceOffset, count).CopyTo(_raw.AsSpan(section.FileOffset + sectionRelativeOffset, count));
            _touchedSectionIds.Add(sectionId);
            logicalOffset += count;
            sourceOffset += count;
        }
    }

    internal void WriteUnboundParasiteRange(int parasiteOffset, ReadOnlySpan<byte> data)
        => WriteCfruExtensionRange(parasiteOffset, data);

    internal void WriteRadicalRedExtensionRange(int extensionOffset, ReadOnlySpan<byte> data)
    {
        var sourceOffset = 0;
        while (sourceOffset < data.Length)
        {
            var current = extensionOffset + sourceOffset;
            int length;
            if (current < 0xCC)
            {
                length = Math.Min(data.Length - sourceOffset, 0xCC - current);
                WriteSectionRange(0, 0xF24 + current, data.Slice(sourceOffset, length));
            }
            else if (current < 0x324)
            {
                length = Math.Min(data.Length - sourceOffset, 0x324 - current);
                WriteSectionRange(4, 0xD98 + current - 0xCC, data.Slice(sourceOffset, length));
            }
            else if (current < 0xEC4)
            {
                length = Math.Min(data.Length - sourceOffset, 0xEC4 - current);
                WriteSectionRange(13, 0x450 + current - 0x324, data.Slice(sourceOffset, length));
            }
            else if (current < 0x1EB4)
            {
                length = Math.Min(data.Length - sourceOffset, 0x1EB4 - current);
                data.Slice(sourceOffset, length).CopyTo(_raw.AsSpan(0x1E000 + current - 0xEC4, length));
                _hasRawChanges = true;
            }
            else if (current < 0x2EA4)
            {
                length = Math.Min(data.Length - sourceOffset, 0x2EA4 - current);
                data.Slice(sourceOffset, length).CopyTo(_raw.AsSpan(0x1F000 + current - 0x1EB4, length));
                _hasRawChanges = true;
            }
            else
            {
                throw new InvalidOperationException($"激进红 CFRU 扩展区偏移 0x{current:X} 超出已验证范围。");
            }
            sourceOffset += length;
        }
    }

    private void WriteCfruExtensionRange(int parasiteOffset, ReadOnlySpan<byte> data)
    {
        var sourceOffset = 0;
        while (sourceOffset < data.Length)
        {
            var current = parasiteOffset + sourceOffset;
            int length;
            if (current < 0xCC)
            {
                length = Math.Min(data.Length - sourceOffset, 0xCC - current);
                WriteSectionRange(0, 0xEB4 + current, data.Slice(sourceOffset, length));
            }
            else if (current < 0x324)
            {
                length = Math.Min(data.Length - sourceOffset, 0x324 - current);
                WriteSectionRange(4, 0xD28 + current - 0xCC, data.Slice(sourceOffset, length));
            }
            else if (current < 0xEC4)
            {
                length = Math.Min(data.Length - sourceOffset, 0xEC4 - current);
                WriteSectionRange(13, 0x3E0 + current - 0x324, data.Slice(sourceOffset, length));
            }
            else if (current < 0x1E44)
            {
                length = Math.Min(data.Length - sourceOffset, 0x1E44 - current);
                data.Slice(sourceOffset, length).CopyTo(_raw.AsSpan(0x1E000 + current - 0xEC4, length));
                _hasRawChanges = true;
            }
            else if (current < 0x2DC4)
            {
                length = Math.Min(data.Length - sourceOffset, 0x2DC4 - current);
                data.Slice(sourceOffset, length).CopyTo(_raw.AsSpan(0x1F000 + current - 0x1E44, length));
                _hasRawChanges = true;
            }
            else if (current < 0x2E38)
            {
                length = Math.Min(data.Length - sourceOffset, 0x2E38 - current);
                WriteSectionRange(1, 0xF80 + current - 0x2DC4, data.Slice(sourceOffset, length));
            }
            else
            {
                throw new InvalidOperationException($"CFRU 扩展区偏移 0x{current:X} 超出已验证范围。");
            }
            sourceOffset += length;
        }
    }

    internal void WriteSectionRange(int sectionId, int relativeOffset, ReadOnlySpan<byte> data)
    {
        if (!_sections.TryGetValue(sectionId, out var section))
            throw new InvalidOperationException($"写回需要 section {sectionId}，但当前存档中不存在。");
        if (relativeOffset < 0 || relativeOffset + data.Length > SectionTrailerOffset)
            throw new InvalidOperationException($"section {sectionId} 写入范围无效。");
        data.CopyTo(_raw.AsSpan(section.FileOffset + relativeOffset, data.Length));
        _touchedSectionIds.Add(sectionId);
    }

    internal uint ReadSectionU32(int sectionId, int relativeOffset)
    {
        if (!_sections.TryGetValue(sectionId, out var section))
            throw new InvalidOperationException($"读取需要 section {sectionId}，但当前存档中不存在。");
        if (relativeOffset < 0 || relativeOffset + 4 > SectionTrailerOffset)
            throw new InvalidOperationException($"section {sectionId} 读取范围无效。");
        return ReadU32(_raw, section.FileOffset + relativeOffset);
    }

    private void VerifyExport(Gen3SaveDocument verified)
    {
        if (!verified.CanWrite || verified.Snapshot.ValidSectionCount != SectionsPerSlot)
            throw new InvalidOperationException($"导出后的 section 校验失败：{verified.WriteBlockReason ?? "有效 section 不足 14 个"}");

        foreach (var (slot, expected) in _expectedParty)
        {
            if (expected.All(value => value == 0))
            {
                if (slot <= verified.Snapshot.Party.Count)
                    throw new InvalidOperationException($"导出后的队伍槽位 {slot} 未正确删除。");
                continue;
            }
            if (slot > verified.Snapshot.Party.Count ||
                !verified.Snapshot.Party[slot - 1].Raw.SequenceEqual(expected))
                throw new InvalidOperationException($"导出后的队伍槽位 {slot} 校验失败。");
        }

        foreach (var (globalSlot, expected) in _expectedBoxes)
        {
            var actual = verified.Snapshot.Boxes.FirstOrDefault(entry => entry.GlobalSlot == globalSlot);
            if (expected.All(value => value == 0))
            {
                if (actual is not null)
                    throw new InvalidOperationException($"导出后的箱子槽位 {globalSlot} 未正确清空。");
                continue;
            }
            if (actual is null || !actual.Mon.Raw.SequenceEqual(expected))
            {
                var detail = actual is null
                    ? "读回为空或头部无效"
                    : $"读回物种 {actual.Mon.GetInfo().Species}，raw={Convert.ToHexString(actual.Mon.Raw)}";
                throw new InvalidOperationException(
                    $"导出后的箱子槽位 {globalSlot} 校验失败：{detail}；expected={Convert.ToHexString(expected)}。");
            }
        }

        var verifiedBag = verified.Snapshot.Bag.ToDictionary(entry => entry.SaveOffset);
        foreach (var (saveOffset, expected) in _expectedBag)
        {
            if (expected.ItemId == 0)
            {
                if (verifiedBag.ContainsKey(saveOffset))
                    throw new InvalidOperationException($"导出后的背包偏移 0x{saveOffset:X} 未正确清空。");
                continue;
            }

            if (!verifiedBag.TryGetValue(saveOffset, out var actual) ||
                actual.ItemId != expected.ItemId || actual.Quantity != expected.Quantity)
                throw new InvalidOperationException($"导出后的背包偏移 0x{saveOffset:X} 校验失败。");
        }


        if (_expectedTrainerName is not null)
        {
            if (verified.Snapshot.Trainer is null ||
                !verified.Snapshot.Trainer.NameBytes.SequenceEqual(_expectedTrainerName))
                throw new InvalidOperationException("导出后的训练家名字校验失败。");
        }
        if (_expectedMoney.HasValue && verified.Snapshot.Trainer?.Money != _expectedMoney.Value)
            throw new InvalidOperationException("导出后的金钱校验失败。");
    }

    private Gen3SaveBagEntry InsertMachine(ushort itemId, ushort quantity)
    {
        var physical = Gen3SaveReader.SaveBagPhysicalPockets.Single(entry => entry.FixedPocket == 7);
        var records = new List<(ushort ItemId, ushort RawQuantity)>();
        for (var i = 0; i < physical.Capacity; i++)
        {
            var offset = physical.Offset + i * 4;
            var currentItem = ReadLogicalU16(SaveBlock1FirstSection, offset);
            if (currentItem == 0) continue;
            if (Gen3SaveReader.SavePocketOfItem(currentItem) != 7)
                throw new InvalidOperationException($"机器口袋 0x{offset:X} 存在无法识别的道具 {currentItem}，已取消写入。");
            records.Add((currentItem, ReadLogicalU16(SaveBlock1FirstSection, offset + 2)));
        }

        if (records.Count >= physical.Capacity)
            throw new InvalidOperationException("招式/秘传机器口袋已满。");
        if (records.Select(record => record.ItemId).Distinct().Count() != records.Count)
            throw new InvalidOperationException("机器口袋存在重复编号，已取消自动重排。");

        var quantityKey = InferQuantityKey();
        records.Add((itemId, (ushort)((quantity - 1) ^ quantityKey)));
        records.Sort((left, right) => left.ItemId.CompareTo(right.ItemId));

        _currentBag.RemoveAll(entry => entry.Pocket == 7);
        Gen3SaveBagEntry? inserted = null;
        for (var i = 0; i < physical.Capacity; i++)
        {
            var offset = physical.Offset + i * 4;
            if (i >= records.Count)
            {
                if (ReadLogicalU16(SaveBlock1FirstSection, offset) != 0)
                {
                    _expectedBag[offset] = (0, 0);
                    WriteLogicalRange(SaveBlock1FirstSection, offset, new byte[4]);
                }
                continue;
            }

            var record = records[i];
            WriteBagRecord(offset, record.ItemId, record.RawQuantity);
            var decodedQuantity = (ushort)((record.RawQuantity ^ quantityKey) + 1);
            var entry = new Gen3SaveBagEntry(
                offset,
                7,
                i + 1,
                record.ItemId,
                decodedQuantity,
                quantityKey,
                true,
                $"存档机器口袋；{physical.Name} 0x{physical.Offset:X}+{i}");
            _currentBag.Add(entry);
            _expectedBag[offset] = (entry.ItemId, entry.Quantity);
            if (record.ItemId == itemId) inserted = entry;
        }

        ReindexBagEntries();
        return inserted ?? throw new InvalidOperationException("插入机器道具失败。");
    }

    private ushort InferQuantityKey()
        => _currentBag.FirstOrDefault(entry => entry.QuantityXor)?.QuantityKey ?? 0;

    private void WriteBagRecord(int saveOffset, ushort itemId, ushort storedQuantity)
    {
        Span<byte> record = stackalloc byte[4];
        WriteU16(record, 0, itemId);
        WriteU16(record, 2, storedQuantity);
        WriteBagRange(saveOffset, record);
    }

    private void WriteBagRange(int saveOffset, ReadOnlySpan<byte> data)
        => _strategy.WriteBagRange(this, saveOffset, data);

    private ushort ReadBagU16(int saveOffset)
        => _strategy.ReadBagU16(this, saveOffset);

    internal void ReadUnboundParasiteRange(int parasiteOffset, Span<byte> destination)
        => ReadCfruExtensionRange(parasiteOffset, destination);

    internal void ReadRadicalRedExtensionRange(int extensionOffset, Span<byte> destination)
    {
        var destinationOffset = 0;
        while (destinationOffset < destination.Length)
        {
            var current = extensionOffset + destinationOffset;
            int length;
            if (current < 0xCC)
            {
                length = Math.Min(destination.Length - destinationOffset, 0xCC - current);
                ReadSectionRange(0, 0xF24 + current, destination.Slice(destinationOffset, length));
            }
            else if (current < 0x324)
            {
                length = Math.Min(destination.Length - destinationOffset, 0x324 - current);
                ReadSectionRange(4, 0xD98 + current - 0xCC, destination.Slice(destinationOffset, length));
            }
            else if (current < 0xEC4)
            {
                length = Math.Min(destination.Length - destinationOffset, 0xEC4 - current);
                ReadSectionRange(13, 0x450 + current - 0x324, destination.Slice(destinationOffset, length));
            }
            else if (current < 0x1EB4)
            {
                length = Math.Min(destination.Length - destinationOffset, 0x1EB4 - current);
                _raw.AsSpan(0x1E000 + current - 0xEC4, length).CopyTo(destination.Slice(destinationOffset, length));
            }
            else if (current < 0x2EA4)
            {
                length = Math.Min(destination.Length - destinationOffset, 0x2EA4 - current);
                _raw.AsSpan(0x1F000 + current - 0x1EB4, length).CopyTo(destination.Slice(destinationOffset, length));
            }
            else
            {
                throw new InvalidOperationException("激进红 CFRU 扩展区读取范围无效。");
            }
            destinationOffset += length;
        }
    }

    private void ReadCfruExtensionRange(int parasiteOffset, Span<byte> destination)
    {
        var destinationOffset = 0;
        while (destinationOffset < destination.Length)
        {
            var current = parasiteOffset + destinationOffset;
            int length;
            if (current < 0xCC)
            {
                length = Math.Min(destination.Length - destinationOffset, 0xCC - current);
                ReadSectionRange(0, 0xEB4 + current, destination.Slice(destinationOffset, length));
            }
            else if (current < 0x324)
            {
                length = Math.Min(destination.Length - destinationOffset, 0x324 - current);
                ReadSectionRange(4, 0xD28 + current - 0xCC, destination.Slice(destinationOffset, length));
            }
            else if (current < 0xEC4)
            {
                length = Math.Min(destination.Length - destinationOffset, 0xEC4 - current);
                ReadSectionRange(13, 0x3E0 + current - 0x324, destination.Slice(destinationOffset, length));
            }
            else if (current < 0x1E44)
            {
                length = Math.Min(destination.Length - destinationOffset, 0x1E44 - current);
                _raw.AsSpan(0x1E000 + current - 0xEC4, length).CopyTo(destination.Slice(destinationOffset, length));
            }
            else if (current < 0x2DC4)
            {
                length = Math.Min(destination.Length - destinationOffset, 0x2DC4 - current);
                _raw.AsSpan(0x1F000 + current - 0x1E44, length).CopyTo(destination.Slice(destinationOffset, length));
            }
            else if (current < 0x2E38)
            {
                length = Math.Min(destination.Length - destinationOffset, 0x2E38 - current);
                ReadSectionRange(1, 0xF80 + current - 0x2DC4, destination.Slice(destinationOffset, length));
            }
            else throw new InvalidOperationException("CFRU 扩展区读取范围无效。");
            destinationOffset += length;
        }
    }

    internal void ReadSectionRange(int sectionId, int relativeOffset, Span<byte> destination)
    {
        if (!_sections.TryGetValue(sectionId, out var section))
            throw new InvalidOperationException($"读取需要 section {sectionId}，但当前存档中不存在。");
        _raw.AsSpan(section.FileOffset + relativeOffset, destination.Length).CopyTo(destination);
    }

    internal ushort ReadLogicalU16(int firstSectionId, int logicalOffset)
    {
        Span<byte> value = stackalloc byte[2];
        ReadLogicalRange(firstSectionId, logicalOffset, value);
        return (ushort)(value[0] | value[1] << 8);
    }

    internal void ReadLogicalRange(int firstSectionId, int logicalOffset, Span<byte> destination)
    {
        var destinationOffset = 0;
        while (destinationOffset < destination.Length)
        {
            var sectionIndex = logicalOffset / SectionDataSize;
            var sectionId = firstSectionId + sectionIndex;
            var sectionRelativeOffset = logicalOffset % SectionDataSize;
            if (!_sections.TryGetValue(sectionId, out var section))
                throw new InvalidOperationException($"读取需要 section {sectionId}，但当前存档中不存在。");
            var count = Math.Min(destination.Length - destinationOffset, SectionDataSize - sectionRelativeOffset);
            _raw.AsSpan(section.FileOffset + sectionRelativeOffset, count)
                .CopyTo(destination.Slice(destinationOffset, count));
            logicalOffset += count;
            destinationOffset += count;
        }
    }

    private void ReindexBagEntries()
    {
        _currentBag.Sort((left, right) => left.SaveOffset.CompareTo(right.SaveOffset));
        var slots = new Dictionary<int, int>();
        for (var i = 0; i < _currentBag.Count; i++)
        {
            var entry = _currentBag[i];
            var slot = slots.GetValueOrDefault(entry.Pocket) + 1;
            slots[entry.Pocket] = slot;
            _currentBag[i] = entry with { SlotInPocket = slot };
        }
    }

    private void ValidateBagValue(int pocket, ushort itemId, ushort quantity, bool allowEmpty)
    {
        if (itemId == 0)
        {
            if (!allowEmpty || quantity != 0)
                throw new InvalidOperationException("空背包格的道具和数量都必须为 0。");
            return;
        }
        var maxItem = _profile?.Limits.MaxItem ?? 922;
        if (itemId > maxItem)
            throw new InvalidOperationException($"存档道具 ID 必须在 1..{maxItem} 范围内。");
        var actualPocket = _strategy.PocketOfItem(this, itemId);
        if (actualPocket != pocket)
            throw new InvalidOperationException($"道具 {itemId} 不属于{_strategy.PocketName(pocket)}。");
        var maxQuantity = MaxBagQuantityForPocket(pocket);
        if (quantity < 1 || quantity > maxQuantity)
            throw new InvalidOperationException($"存档道具数量必须在 1..{maxQuantity} 范围内。");
    }

    private void ValidateBagQuantity(int pocket, ushort quantity)
    {
        var maxQuantity = MaxBagQuantityForPocket(pocket);
        if (quantity < 1 || quantity > maxQuantity)
            throw new InvalidOperationException($"存档道具数量必须在 1..{maxQuantity} 范围内。");
    }

    private ushort MaxBagQuantityForPocket(int pocket)
        => (ushort)_profile.Limits.MaxBagQuantityForPocket(pocket);

    private static void UpdateSectionChecksum(Span<byte> raw, Gen3SaveSectionLayout section)
    {
        if (section.FileOffset < 0 || section.FileOffset + SectionSize > raw.Length)
            throw new InvalidOperationException($"section {section.Id} 的文件范围无效。");
        var checksumLength = section.ChecksumMode switch
        {
            "standard" => SectionDataSize,
            "pc-tail" => PcTailChecksumSize,
            "pc-boxes" => PcBoxesChecksumSize,
            "d98" => 0xD98,
            "f24" => 0xF24,
            var mode when mode.StartsWith("radical-red-", StringComparison.Ordinal) =>
                Convert.ToInt32(mode["radical-red-".Length..], 16),
            "extended" => SectionTrailerOffset,
            var mode when mode.StartsWith("unbound-", StringComparison.Ordinal) =>
                Convert.ToInt32(mode["unbound-".Length..], 16),
            _ => throw new InvalidOperationException($"section {section.Id} 使用不支持的校验模式 {section.ChecksumMode}。")
        };
        var checksum = CalculateChecksum(raw.Slice(section.FileOffset, checksumLength));
        WriteU16(raw, section.FileOffset + SectionChecksumOffset, checksum);
    }

    private static ushort CalculateChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 3 < data.Length; i += 4)
            sum += ReadU32(data, i);
        return (ushort)(((sum >> 16) + (sum & 0xFFFF)) & 0xFFFF);
    }

    private static void ValidatePartyPokemon(PartyPokemon mon)
    {
        if (mon.IsEmpty)
            throw new InvalidOperationException("不能用空数据覆盖现有队伍槽位。");
        var info = mon.GetInfo();
        if (info.Checksum != info.CalculatedChecksum)
            throw new InvalidOperationException("队伍宝可梦单体校验不正确，已取消写回。");
    }

    private static void ValidateBoxPokemon(BoxPokemon mon)
    {
        if (mon.IsEmpty)
            throw new InvalidOperationException("不能用空数据覆盖现有箱子槽位。");
        var info = mon.GetInfo();
        if (info.Checksum != info.CalculatedChecksum)
            throw new InvalidOperationException("箱子宝可梦单体校验不正确，已取消写回。");
    }

    private void EnsureWritable()
    {
        if (!CanWrite)
            throw new InvalidOperationException(WriteBlockReason ?? "当前存档不能安全写回。");
    }

    private void EnsureProfileWrite(GameDataSurface surface)
    {
        var profile = _profile ?? throw new InvalidOperationException("存档操作必须显式绑定一个 Profile，禁止使用跨版本默认策略。");
        GameRuntimeAdapterCatalog.ForProfile(profile).EnsureCanWrite(surface, live: false);
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static void WriteU16(Span<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private sealed record FileTimestamps(DateTime CreationUtc, DateTime LastAccessUtc, DateTime LastWriteUtc)
    {
        public static FileTimestamps Capture(string path)
            => new(File.GetCreationTimeUtc(path), File.GetLastAccessTimeUtc(path), File.GetLastWriteTimeUtc(path));

        public void TryApply(string path)
        {
            try
            {
                File.SetCreationTimeUtc(path, CreationUtc);
                File.SetLastAccessTimeUtc(path, LastAccessUtc);
                File.SetLastWriteTimeUtc(path, LastWriteUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Timestamp preservation is best-effort; save bytes and checksums remain verified.
            }
        }
    }
}
