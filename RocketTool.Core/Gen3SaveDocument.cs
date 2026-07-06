namespace RocketTool.Core;

internal sealed record Gen3SaveSectionLayout(int Id, int FileOffset, uint SaveIndex, string ChecksumMode);

public sealed record Gen3SaveWriteResult(
    Gen3SaveReadResult Snapshot,
    string DestinationPath,
    string? BackupPath,
    bool BackupCreated);

public sealed class Gen3SaveDocument
{
    private const int SectionSize = 0x1000;
    private const int SectionDataSize = 0xF80;
    private const int SectionTrailerOffset = 0xFF4;
    private const int SectionChecksumOffset = 0xFF6;
    private const int PcTailChecksumSize = 0x7D0;
    private const int PcBoxesChecksumSize = 0x744;
    private const int SectionsPerSlot = 14;
    private const int SaveBlock1FirstSection = 1;
    private const int PcFirstSection = 5;
    private const int PcStorageOffset = 4;
    private const string UnboundSaveStrategy = "unbound-cfru-save-v1";
    private const int UnboundParasiteOffsetMarker = 0x100000;
    private const int UnboundBoxRecordSize = BoxPokemon.UnboundCompressedSize;

    private readonly byte[] _raw;
    private readonly byte[] _originalRaw;
    private readonly IReadOnlyDictionary<int, Gen3SaveSectionLayout> _sections;
    private readonly int _partyOffset;
    private readonly HashSet<int> _touchedSectionIds = [];
    private readonly Dictionary<int, byte[]> _expectedParty = [];
    private readonly Dictionary<int, byte[]> _expectedBoxes = [];
    private readonly Dictionary<int, (ushort ItemId, ushort Quantity)> _expectedBag = [];
    private byte[]? _expectedTrainerName;
    private uint? _expectedMoney;
    private readonly List<Gen3SaveBagEntry> _currentBag;
    private readonly GameProfile? _profile;
    private readonly IReadOnlyDictionary<int, int> _itemPockets;
    private readonly bool _isUnbound;
    private bool _hasRawChanges;

    internal Gen3SaveDocument(
        string sourcePath,
        byte[] raw,
        IReadOnlyDictionary<int, Gen3SaveSectionLayout> sections,
        int partyOffset,
        Gen3SaveReadResult snapshot,
        GameProfile? profile = null,
        IReadOnlyDictionary<int, int>? itemPockets = null)
    {
        SourcePath = Path.GetFullPath(sourcePath);
        _originalRaw = raw.ToArray();
        _raw = raw.ToArray();
        _sections = sections;
        _partyOffset = partyOffset;
        Snapshot = snapshot;
        _currentBag = snapshot.Bag.ToList();
        _profile = profile;
        _itemPockets = itemPockets ?? new Dictionary<int, int>();
        _isUnbound = string.Equals(profile?.Strategies.Save, UnboundSaveStrategy, StringComparison.Ordinal);

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
    public int ModifiedPartyCount => _expectedParty.Count;
    public int ModifiedBoxCount => _expectedBoxes.Count;
    public int ModifiedBagCount => _expectedBag.Count;
    public bool TrainerModified => _expectedTrainerName is not null || _expectedMoney.HasValue;
    public IReadOnlyList<Gen3SaveBagEntry> CurrentBag => _currentBag;

    public void ReplaceTrainerName(ReadOnlySpan<byte> nameBytes)
    {
        EnsureWritable();
        if (!_isUnbound || _profile is null || Snapshot.Trainer is null)
            throw new InvalidOperationException("当前存档策略尚未验证训练家名字写回。");
        if (nameBytes.Length != _profile.Memory.PlayerNameLength)
            throw new InvalidOperationException($"训练家名字编码必须为 {_profile.Memory.PlayerNameLength} 字节。");

        WriteUnboundSectionRange(0, 0, nameBytes);
        _expectedTrainerName = nameBytes.ToArray();
    }

    public void ReplaceTrainerMoney(uint money)
    {
        EnsureWritable();
        if (!_isUnbound || _profile is null || Snapshot.Trainer is null)
            throw new InvalidOperationException("当前存档策略尚未验证金钱写回。");
        if (money > 99_999_999)
            throw new InvalidOperationException("金钱必须在 0..99999999 范围内。");

        var key = ReadUnboundSectionU32(0, (int)_profile.Memory.SaveBlock2EncryptionKeyOffset);
        var encrypted = money ^ key;
        Span<byte> value = stackalloc byte[4];
        value[0] = (byte)encrypted;
        value[1] = (byte)(encrypted >> 8);
        value[2] = (byte)(encrypted >> 16);
        value[3] = (byte)(encrypted >> 24);
        WriteLogicalRange(SaveBlock1FirstSection, (int)_profile.Memory.SaveBlock1MoneyOffset, value);
        _expectedMoney = money;
    }

    public int PartySaveOffset(int slot)
    {
        if (_partyOffset < 0)
            throw new InvalidOperationException("存档队伍仅通过不确定结构读取，不能定位写回位置。");
        if (slot < 1 || slot > Snapshot.Party.Count)
            throw new ArgumentOutOfRangeException(nameof(slot), $"队伍槽位必须在 1..{Snapshot.Party.Count} 之间。");
        return _partyOffset + (slot - 1) * PartyPokemon.Size;
    }

    public void ReplacePartyPokemon(int slot, PartyPokemon mon)
    {
        EnsureWritable();
        ValidatePartyPokemon(mon);
        var saveOffset = PartySaveOffset(slot);
        WriteLogicalRange(SaveBlock1FirstSection, saveOffset, mon.Raw);
        _expectedParty[slot] = mon.Raw.ToArray();
    }

    public void ReplaceBoxPokemon(int globalSlot, BoxPokemon mon)
    {
        EnsureWritable();
        var boxCount = _profile?.Memory.PcBoxCount ?? BoxScanner.MaxBoxes;
        var boxSlots = _profile?.Memory.PcBoxSlots ?? BoxScanner.BoxSlots;
        if (globalSlot < 1 || globalSlot > boxCount * boxSlots)
            throw new ArgumentOutOfRangeException(nameof(globalSlot), "箱子槽位超出范围。");
        ValidateBoxPokemon(mon);
        if (_isUnbound)
            WriteUnboundBox(globalSlot, mon.Raw);
        else
        {
            var saveOffset = PcStorageOffset + (globalSlot - 1) * BoxPokemon.Size;
            WriteLogicalRange(PcFirstSection, saveOffset, mon.Raw);
        }
        _expectedBoxes[globalSlot] = mon.Raw.ToArray();
    }

    public Gen3SaveBagEntry? ReplaceBagEntry(int saveOffset, ushort itemId, ushort quantity)
    {
        EnsureWritable();
        var index = _currentBag.FindIndex(entry => entry.SaveOffset == saveOffset);
        if (index < 0)
            throw new InvalidOperationException($"存档背包中找不到偏移 0x{saveOffset:X} 的道具格。请重新读取存档。");

        var current = _currentBag[index];
        ValidateBagValue(current.Pocket, itemId, quantity, allowEmpty: true);
        if (itemId == 0)
        {
            WriteBagRange(saveOffset, new byte[4]);
            _currentBag.RemoveAt(index);
            _expectedBag[saveOffset] = (0, 0);
            ReindexBagEntries();
            return null;
        }

        var storedQuantity = current.QuantityXor
            ? (ushort)((_isUnbound ? quantity : quantity - 1) ^ current.QuantityKey)
            : (ushort)0;
        WriteBagRecord(saveOffset, itemId, storedQuantity);
        var updated = current with { ItemId = itemId, Quantity = current.QuantityXor ? quantity : (ushort)1 };
        _currentBag[index] = updated;
        _expectedBag[saveOffset] = (updated.ItemId, updated.Quantity);
        return updated;
    }

    public Gen3SaveBagEntry AddBagItem(int pocket, ushort itemId, ushort quantity)
    {
        EnsureWritable();
        ValidateBagValue(pocket, itemId, quantity, allowEmpty: false);

        var existing = _currentBag.FirstOrDefault(entry => entry.Pocket == pocket && entry.ItemId == itemId);
        if (existing is not null)
        {
            var after = existing.QuantityXor
                ? (ushort)Math.Min(255, existing.Quantity + quantity)
                : (ushort)1;
            return ReplaceBagEntry(existing.SaveOffset, itemId, after)
                   ?? throw new InvalidOperationException("更新现有背包道具失败。");
        }

        if (pocket == 7)
            return InsertMachine(itemId, quantity);

        var physicalPockets = CandidatePhysicalPockets(pocket).ToArray();
        if (physicalPockets.Length == 0)
            throw new InvalidOperationException($"存档中的口袋 {pocket} 没有可用物理区域。");

        foreach (var physical in physicalPockets)
        {
            for (var i = 0; i < physical.Capacity; i++)
            {
                var offset = physical.Offset + i * 4;
                if (ReadBagU16(offset) != 0) continue;

                var quantityXor = physical.HasQuantity && pocket != 8;
                var quantityKey = quantityXor ? InferQuantityKey() : (ushort)0;
                var storedQuantity = quantityXor ? (ushort)((_isUnbound ? quantity : quantity - 1) ^ quantityKey) : (ushort)0;
                WriteBagRecord(offset, itemId, storedQuantity);
                var entry = new Gen3SaveBagEntry(
                    offset,
                    pocket,
                    0,
                    itemId,
                    quantityXor ? quantity : (ushort)1,
                    quantityKey,
                    quantityXor,
                    $"存档新增；{physical.Name} 0x{physical.Offset:X}+{i}");
                _currentBag.Add(entry);
                _expectedBag[offset] = (entry.ItemId, entry.Quantity);
                ReindexBagEntries();
                return _currentBag.First(candidate => candidate.SaveOffset == offset);
            }
        }

        throw new InvalidOperationException($"{SavePocketName(pocket)}没有空格，无法添加新道具。");
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

        var temporary = CreateVerifiedTemporary(destination);
        try
        {
            File.Move(temporary, destination, overwrite: true);
            return _profile is null ? Gen3SaveReader.Read(destination) : Gen3SaveReader.Read(destination, _profile);
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
            var snapshot = _profile is null ? Gen3SaveReader.Read(SourcePath) : Gen3SaveReader.Read(SourcePath, _profile);
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

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, output);
            var verified = _profile is null ? Gen3SaveReader.Open(temporary) : Gen3SaveReader.Open(temporary, _profile);
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

    private void WriteLogicalRange(int firstSectionId, int logicalOffset, ReadOnlySpan<byte> data)
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

    private void WriteUnboundBox(int globalSlot, ReadOnlySpan<byte> data)
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
            WriteUnboundSectionRange(0, 0xB0 + slot * UnboundBoxRecordSize, data);
            return;
        }
        throw new InvalidOperationException($"解放版没有箱子 {box}。");
    }

    private void WriteUnboundParasiteRange(int parasiteOffset, ReadOnlySpan<byte> data)
    {
        var sourceOffset = 0;
        while (sourceOffset < data.Length)
        {
            var current = parasiteOffset + sourceOffset;
            int length;
            if (current < 0xCC)
            {
                length = Math.Min(data.Length - sourceOffset, 0xCC - current);
                WriteUnboundSectionRange(0, 0xEB4 + current, data.Slice(sourceOffset, length));
            }
            else if (current < 0x324)
            {
                length = Math.Min(data.Length - sourceOffset, 0x324 - current);
                WriteUnboundSectionRange(4, 0xD28 + current - 0xCC, data.Slice(sourceOffset, length));
            }
            else if (current < 0xEC4)
            {
                length = Math.Min(data.Length - sourceOffset, 0xEC4 - current);
                WriteUnboundSectionRange(13, 0x3E0 + current - 0x324, data.Slice(sourceOffset, length));
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
                WriteUnboundSectionRange(1, 0xF80 + current - 0x2DC4, data.Slice(sourceOffset, length));
            }
            else
            {
                throw new InvalidOperationException($"CFRU 扩展区偏移 0x{current:X} 超出已验证范围。");
            }
            sourceOffset += length;
        }
    }

    private void WriteUnboundSectionRange(int sectionId, int relativeOffset, ReadOnlySpan<byte> data)
    {
        if (!_sections.TryGetValue(sectionId, out var section))
            throw new InvalidOperationException($"写回需要 section {sectionId}，但当前存档中不存在。");
        if (relativeOffset < 0 || relativeOffset + data.Length > SectionTrailerOffset)
            throw new InvalidOperationException($"section {sectionId} 写入范围无效。");
        data.CopyTo(_raw.AsSpan(section.FileOffset + relativeOffset, data.Length));
        _touchedSectionIds.Add(sectionId);
    }

    private uint ReadUnboundSectionU32(int sectionId, int relativeOffset)
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
            if (slot > verified.Snapshot.Party.Count ||
                !verified.Snapshot.Party[slot - 1].Raw.SequenceEqual(expected))
                throw new InvalidOperationException($"导出后的队伍槽位 {slot} 校验失败。");
        }

        foreach (var (globalSlot, expected) in _expectedBoxes)
        {
            var actual = verified.Snapshot.Boxes.FirstOrDefault(entry => entry.GlobalSlot == globalSlot);
            if (actual is null || !actual.Mon.Raw.SequenceEqual(expected))
                throw new InvalidOperationException($"导出后的箱子槽位 {globalSlot} 校验失败。");
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

    private IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket)
    {
        if (_isUnbound)
        {
            var candidate = pocket switch
            {
                1 => new Gen3SaveBagPhysicalPocket("道具", UnboundParasiteOffsetMarker + 0x09AC, 450, 1, true),
                2 => new Gen3SaveBagPhysicalPocket("重要物品", UnboundParasiteOffsetMarker + 0x10B4, 75, 2, true),
                3 => new Gen3SaveBagPhysicalPocket("精灵球", UnboundParasiteOffsetMarker + 0x11E0, 50, 3, true),
                4 => new Gen3SaveBagPhysicalPocket("招式机器", UnboundParasiteOffsetMarker + 0x12A8, 128, 4, true),
                5 => new Gen3SaveBagPhysicalPocket("树果", UnboundParasiteOffsetMarker + 0x14A8, 75, 5, true),
                _ => null
            };
            if (candidate is not null) yield return candidate;
            yield break;
        }

        foreach (var physical in Gen3SaveReader.SaveBagPhysicalPockets)
        {
            if (physical.FixedPocket == pocket ||
                physical.FixedPocket is null && pocket is >= 1 and <= 6)
                yield return physical;
        }
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
    {
        if (_isUnbound)
            WriteUnboundParasiteRange(saveOffset - UnboundParasiteOffsetMarker, data);
        else
            WriteLogicalRange(SaveBlock1FirstSection, saveOffset, data);
    }

    private ushort ReadBagU16(int saveOffset)
    {
        if (!_isUnbound) return ReadLogicalU16(SaveBlock1FirstSection, saveOffset);
        Span<byte> value = stackalloc byte[2];
        ReadUnboundParasiteRange(saveOffset - UnboundParasiteOffsetMarker, value);
        return (ushort)(value[0] | value[1] << 8);
    }

    private void ReadUnboundParasiteRange(int parasiteOffset, Span<byte> destination)
    {
        var destinationOffset = 0;
        while (destinationOffset < destination.Length)
        {
            var current = parasiteOffset + destinationOffset;
            int length;
            if (current < 0xCC)
            {
                length = Math.Min(destination.Length - destinationOffset, 0xCC - current);
                ReadUnboundSectionRange(0, 0xEB4 + current, destination.Slice(destinationOffset, length));
            }
            else if (current < 0x324)
            {
                length = Math.Min(destination.Length - destinationOffset, 0x324 - current);
                ReadUnboundSectionRange(4, 0xD28 + current - 0xCC, destination.Slice(destinationOffset, length));
            }
            else if (current < 0xEC4)
            {
                length = Math.Min(destination.Length - destinationOffset, 0xEC4 - current);
                ReadUnboundSectionRange(13, 0x3E0 + current - 0x324, destination.Slice(destinationOffset, length));
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
                ReadUnboundSectionRange(1, 0xF80 + current - 0x2DC4, destination.Slice(destinationOffset, length));
            }
            else throw new InvalidOperationException("CFRU 扩展区读取范围无效。");
            destinationOffset += length;
        }
    }

    private void ReadUnboundSectionRange(int sectionId, int relativeOffset, Span<byte> destination)
    {
        if (!_sections.TryGetValue(sectionId, out var section))
            throw new InvalidOperationException($"读取需要 section {sectionId}，但当前存档中不存在。");
        _raw.AsSpan(section.FileOffset + relativeOffset, destination.Length).CopyTo(destination);
    }

    private ushort ReadLogicalU16(int firstSectionId, int logicalOffset)
    {
        Span<byte> value = stackalloc byte[2];
        ReadLogicalRange(firstSectionId, logicalOffset, value);
        return (ushort)(value[0] | value[1] << 8);
    }

    private void ReadLogicalRange(int firstSectionId, int logicalOffset, Span<byte> destination)
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
        var actualPocket = _isUnbound
            ? (_itemPockets.TryGetValue(itemId, out var mappedPocket) ? mappedPocket : null)
            : Gen3SaveReader.SavePocketOfItem(itemId);
        if (actualPocket != pocket)
            throw new InvalidOperationException($"道具 {itemId} 不属于{SavePocketName(pocket)}。");
        if (quantity is < 1 or > 255)
            throw new InvalidOperationException("存档道具数量必须在 1..255 范围内。");
    }

    private string SavePocketName(int pocket)
        => _isUnbound
            ? pocket switch
            {
                1 => "道具",
                2 => "重要物品",
                3 => "精灵球",
                4 => "招式机器",
                5 => "树果",
                _ => $"口袋{pocket}"
            }
            : pocket switch
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

    private static void UpdateSectionChecksum(Span<byte> raw, Gen3SaveSectionLayout section)
    {
        if (section.FileOffset < 0 || section.FileOffset + SectionSize > raw.Length)
            throw new InvalidOperationException($"section {section.Id} 的文件范围无效。");
        var checksumLength = section.ChecksumMode switch
        {
            "standard" => SectionDataSize,
            "pc-tail" => PcTailChecksumSize,
            "pc-boxes" => PcBoxesChecksumSize,
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

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static void WriteU16(Span<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }
}
