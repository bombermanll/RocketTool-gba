namespace RocketTool.Nds;

public sealed record OriginHeartGoldSaveBlock(
    int Partition,
    string Kind,
    int Offset,
    int Size,
    uint SaveCount,
    uint SizeField,
    uint Magic,
    ushort Unknown,
    ushort StoredCrc,
    ushort CalculatedCrc)
{
    public bool ChecksumValid => StoredCrc == CalculatedCrc && Magic == OriginHeartGoldSave.FooterMagic && SizeField == (uint)Size;
}

public sealed record OriginHeartGoldPartyMon(
    int Slot,
    int Offset,
    uint Pid,
    ushort Checksum,
    bool ChecksumValid,
    ushort Species,
    ushort HeldItem,
    uint Experience,
    byte Level,
    byte AbilityField,
    byte Friendship,
    IReadOnlyList<byte> Pp,
    IReadOnlyList<ushort> Moves,
    IReadOnlyList<byte> Evs,
    IReadOnlyList<int> Ivs,
    int CurrentHp,
    int MaxHp,
    int Attack,
    int Defense,
    int Speed,
    int SpAttack,
    int SpDefense,
    IReadOnlyList<ushort> NicknameCodes,
    IReadOnlyList<ushort> OtCodes);

public sealed record OriginHeartGoldBagSlot(int Offset, ushort ItemId, ushort Quantity);

public sealed record OriginHeartGoldBagPocket(
    string Name,
    int Offset,
    int Capacity,
    IReadOnlyList<OriginHeartGoldBagSlot> Items);

public sealed record OriginHeartGoldBoxMon(
    int Box,
    int Slot,
    int Offset,
    uint Pid,
    ushort Species,
    ushort HeldItem,
    uint Experience,
    byte Friendship,
    byte AbilityField,
    IReadOnlyList<ushort> Moves,
    IReadOnlyList<byte> Pp,
    IReadOnlyList<int> Ivs,
    IReadOnlyList<byte> Evs,
    bool ChecksumValid);

public sealed record OriginHeartGoldSaveProbe(
    string FileName,
    int Size,
    string Sha256,
    OriginHeartGoldSaveBlock ActiveGeneral,
    OriginHeartGoldSaveBlock BackupGeneral,
    OriginHeartGoldSaveBlock ActiveStorage,
    OriginHeartGoldSaveBlock BackupStorage,
    uint TrainerId,
    ushort TrainerTid,
    ushort TrainerSid,
    uint Money,
    uint BackupMoney,
    byte Gender,
    int PlayHours,
    int PlayMinutes,
    int PlaySeconds,
    IReadOnlyList<ushort> TrainerOtCodes,
    int PartyCount,
    IReadOnlyList<OriginHeartGoldPartyMon> Party,
    IReadOnlyList<OriginHeartGoldBagSlot> CandidateItems,
    IReadOnlyList<OriginHeartGoldBagSlot> CandidateBalls,
    IReadOnlyList<OriginHeartGoldBagPocket> BagPockets,
    IReadOnlyList<OriginHeartGoldBoxMon> BoxMons,
    int OccupiedBoxSlots,
    IReadOnlyList<string> Notes);

public static class OriginHeartGoldSave
{
    public const int FileSize = 0x80000;
    public const int PartitionSize = 0x40000;
    public const int GeneralSize = 0xF7CC;
    public const int StorageStart = 0xF800;
    public const int StorageSize = 0x18408;
    public const int FooterSize = 0x10;
    public const uint FooterMagic = 0x20060623;
    public const int TrainerOffset = 0x64;
    public const int PartyCountOffset = 0x94;
    public const int PartyOffset = 0x98;
    public const int PartySlots = 6;
    public const int StoredSize = 136;
    public const int PartySize = 236;
    public const int CandidateItemsOffset = 0x8D8;
    public const int CandidateBallsOffset = 0xDCC;
    public const int BoxCount = 24;
    public const int BoxSlots = 30;
    public const int BoxStride = 0x1000;
    public const int ItemsPocketOffset = 0x644;
    public const int ItemsPocketCapacity = 165;
    public const int KeyPocketOffset = 0x8D8;
    public const int KeyPocketCapacity = 50;
    public const int MachinePocketOffset = 0x9A0;
    public const int MachinePocketCapacity = 151;
    public const int MailPocketOffset = 0xBFC;
    public const int MailPocketCapacity = 12;
    public const int MedicinePocketOffset = 0xC2C;
    public const int MedicinePocketCapacity = 40;
    public const int BerryPocketOffset = 0xCCC;
    public const int BerryPocketCapacity = 64;
    public const int BallPocketOffset = 0xDCC;
    public const int BallPocketCapacity = 24;
    public const int BattlePocketOffset = 0xE2C;
    public const int BattlePocketCapacity = 30;

    public static void SaveMoneyAs(string sourcePath, string outputPath, uint money)
        => SaveEdited(sourcePath, outputPath, save => save.Money = money, inPlace: false);

    public static void SaveInPlaceWithBackup(string sourcePath, Action<OriginHeartGoldSaveEditor> edit)
    {
        var backup = sourcePath + ".bak";
        if (!File.Exists(backup))
            File.Copy(sourcePath, backup, overwrite: false);
        SaveEdited(sourcePath, sourcePath, edit, inPlace: true);
    }

    public static byte[] EditCopy(string sourcePath, Action<OriginHeartGoldSaveEditor> edit)
        => EditBytes(File.ReadAllBytes(sourcePath), edit);

    public static byte[] EditBytes(byte[] sourceData, Action<OriginHeartGoldSaveEditor> edit)
    {
        ArgumentNullException.ThrowIfNull(sourceData);
        ArgumentNullException.ThrowIfNull(edit);
        var data = sourceData.ToArray();
        var before = OpenBytes(data);
        var editor = new OriginHeartGoldSaveEditor(data, before);
        edit(editor);
        editor.Commit();
        return data;
    }

    public static OriginHeartGoldSaveProbe OpenBytes(byte[] data)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"xinjin-pending-{Guid.NewGuid():N}.sav");
        File.WriteAllBytes(temp, data);
        try { return Open(temp); }
        finally { File.Delete(temp); }
    }

    public static void SaveEdited(string sourcePath, string outputPath, Action<OriginHeartGoldSaveEditor> edit, bool inPlace)
    {
        if (!inPlace && string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.Ordinal))
            throw new InvalidDataException("另存为不能覆盖原存档。");
        File.WriteAllBytes(outputPath, EditCopy(sourcePath, edit));
        _ = Open(outputPath);
    }

    public sealed class OriginHeartGoldSaveEditor
    {
        private readonly byte[] _data;
        private readonly OriginHeartGoldSaveProbe _source;
        private readonly HashSet<int> _rewrittenParty = [];
        private readonly HashSet<int> _rewrittenBoxes = [];

        internal OriginHeartGoldSaveEditor(byte[] data, OriginHeartGoldSaveProbe source)
        {
            _data = data;
            _source = source;
        }

        public uint Money
        {
            set
            {
                if (value > 999_999)
                    throw new InvalidDataException("金钱必须在 0 到 999999 之间。");
                BitConverter.GetBytes(value).CopyTo(_data, _source.ActiveGeneral.Offset + 0x78);
            }
        }

        public int AppendParty(ushort species, uint experience, byte level, ushort[]? moves = null, byte[]? pp = null, int[]? ivs = null)
        {
            if (_source.PartyCount >= PartySlots)
                throw new InvalidDataException("队伍已满。");
            var slot = _source.PartyCount + 1;
            var offset = _source.ActiveGeneral.Offset + PartyOffset + (slot - 1) * PartySize;
            var record = NewPk4(species, experience, level, party: true, moves, pp, ivs);
            record.CopyTo(_data.AsSpan(offset, PartySize));
            _data[_source.ActiveGeneral.Offset + PartyCountOffset] = (byte)slot;
            return slot;
        }

        public (int Box, int Slot) AppendBox(ushort species, uint experience, ushort[]? moves = null, byte[]? pp = null, int[]? ivs = null)
        {
            for (var box = 1; box <= BoxCount; box++)
            {
                for (var slot = 1; slot <= BoxSlots; slot++)
                {
                    var relative = (box - 1) * BoxStride + (slot - 1) * StoredSize;
                    var offset = _source.ActiveStorage.Offset + relative;
                    if (IsOccupiedPk4(_data.AsSpan(offset, StoredSize))) continue;
                    NewPk4(species, experience, 0, party: false, moves, pp, ivs).CopyTo(_data.AsSpan(offset, StoredSize));
                    _rewrittenBoxes.Add(relative);
                    return (box, slot);
                }
            }
            throw new InvalidDataException("箱子已满。");
        }

        public void RemoveParty(int slot)
        {
            if (slot < 1 || slot > _source.PartyCount)
                throw new InvalidDataException($"队伍没有第 {slot} 只。");
            var start = _source.ActiveGeneral.Offset + PartyOffset;
            for (var i = slot - 1; i < _source.PartyCount - 1; i++)
                _data.AsSpan(start + (i + 1) * PartySize, PartySize).CopyTo(_data.AsSpan(start + i * PartySize, PartySize));
            _data.AsSpan(start + (_source.PartyCount - 1) * PartySize, PartySize).Clear();
            _data[_source.ActiveGeneral.Offset + PartyCountOffset] = (byte)(_source.PartyCount - 1);
        }

        public void RemoveBox(int box, int slot)
        {
            var mon = _source.BoxMons.FirstOrDefault(candidate => candidate.Box == box && candidate.Slot == slot)
                ?? throw new InvalidDataException($"箱子 {box}-{slot} 没有宝可梦。");
            _data.AsSpan(_source.ActiveStorage.Offset + mon.Offset, StoredSize).Clear();
            _rewrittenBoxes.Add(mon.Offset);
        }

        public void SetParty(int slot, Action<byte[]> edit)
            => EditParty(slot, edit);

        public void SetBox(int box, int slot, Action<byte[]> edit)
            => EditBox(box, slot, edit);

        public void SetTrainerName(IReadOnlyList<ushort> codes)
        {
            if (codes.Count == 0 || codes.Count > 7)
                throw new InvalidDataException("训练家姓名必须是 1 到 7 个字形。 ");
            var start = _source.ActiveGeneral.Offset + TrainerOffset;
            _data.AsSpan(start, 16).Clear();
            for (var i = 0; i < codes.Count; i++)
                BitConverter.GetBytes(codes[i]).CopyTo(_data, start + i * 2);
            BitConverter.GetBytes((ushort)0xFFFF).CopyTo(_data, start + codes.Count * 2);
        }

        public void SetBagQuantity(string pocketName, int slot, ushort quantity)
        {
            var item = OccupiedBagSlot(pocketName, slot);
            if (quantity is 0 or > 999)
                throw new InvalidDataException("道具数量必须在 1 到 999 之间。");
            BitConverter.GetBytes(quantity).CopyTo(_data, _source.ActiveGeneral.Offset + item.Offset + 2);
        }

        public void SetBagItem(string pocketName, int slot, ushort itemId, ushort quantity)
        {
            var item = OccupiedBagSlot(pocketName, slot);
            if (itemId == 0 || quantity is 0 or > 999)
                throw new InvalidDataException("替换道具时，道具和数量都不能为空。");
            var at = _source.ActiveGeneral.Offset + item.Offset;
            BitConverter.GetBytes(itemId).CopyTo(_data, at);
            BitConverter.GetBytes(quantity).CopyTo(_data, at + 2);
        }

        public void AddBagItem(string pocketName, ushort itemId, ushort quantity)
        {
            var pocket = Pocket(pocketName);
            if (itemId == 0 || quantity is 0 or > 999)
                throw new InvalidDataException("新增道具时，道具和数量都不能为空。");
            if (pocket.Items.Count >= pocket.Capacity)
                throw new InvalidDataException($"{pocketName} 已满。");
            var at = _source.ActiveGeneral.Offset + pocket.Offset + pocket.Items.Count * 4;
            BitConverter.GetBytes(itemId).CopyTo(_data, at);
            BitConverter.GetBytes(quantity).CopyTo(_data, at + 2);
        }

        public void RemoveBagItem(string pocketName, int slot)
        {
            var pocket = Pocket(pocketName);
            if (slot < 1 || slot > pocket.Items.Count)
                throw new InvalidDataException($"{pocketName} 第 {slot} 格没有已占用道具。");
            var start = _source.ActiveGeneral.Offset + pocket.Offset;
            for (var i = slot - 1; i < pocket.Items.Count - 1; i++)
                _data.AsSpan(start + (i + 1) * 4, 4).CopyTo(_data.AsSpan(start + i * 4, 4));
            _data.AsSpan(start + (pocket.Items.Count - 1) * 4, 4).Clear();
        }

        private OriginHeartGoldBagPocket Pocket(string pocketName)
            => _source.BagPockets.FirstOrDefault(candidate => candidate.Name == pocketName)
               ?? throw new InvalidDataException($"没有名为 {pocketName} 的背包口袋。");

        private OriginHeartGoldBagSlot OccupiedBagSlot(string pocketName, int slot)
        {
            var pocket = Pocket(pocketName);
            if (slot < 1 || slot > pocket.Items.Count)
                throw new InvalidDataException($"{pocketName} 第 {slot} 格没有已占用道具。");
            return pocket.Items[slot - 1];
        }

        public void SetBoxFriendship(int box, int slot, byte friendship)
            => EditBox(box, slot, decrypted => decrypted[0x14] = friendship);

        internal void Commit()
        {
            RefreshFooter(_data, _source.ActiveGeneral);
            if (_rewrittenBoxes.Count > 0)
                RefreshFooter(_data, _source.ActiveStorage);
        }

        private void EditParty(int slot, Action<byte[]> edit)
        {
            var mon = _source.Party.FirstOrDefault(candidate => candidate.Slot == slot)
                ?? throw new InvalidDataException($"队伍没有第 {slot} 只。");
            RewritePk4(_source.ActiveGeneral.Offset + mon.Offset, PartySize, edit);
            _rewrittenParty.Add(slot);
        }

        private void EditBox(int box, int slot, Action<byte[]> edit)
        {
            var mon = _source.BoxMons.FirstOrDefault(candidate => candidate.Box == box && candidate.Slot == slot)
                ?? throw new InvalidDataException($"箱子 {box}-{slot} 没有宝可梦。");
            RewritePk4(_source.ActiveStorage.Offset + mon.Offset, StoredSize, edit);
            _rewrittenBoxes.Add(mon.Offset);
        }

        private void RewritePk4(int absoluteOffset, int size, Action<byte[]> edit)
        {
            var encrypted = _data.AsSpan(absoluteOffset, size);
            var decrypted = DecryptPk4(encrypted);
            if (BitConverter.ToUInt16(decrypted, 6) != StoredChecksum(decrypted))
                throw new InvalidDataException("拒绝改写 checksum 无效的宝可梦记录。");
            edit(decrypted);
            var checksum = StoredChecksum(decrypted);
            BitConverter.GetBytes(checksum).CopyTo(decrypted, 6);
            var rewritten = EncryptPk4(decrypted);
            rewritten.CopyTo(encrypted);
        }
    }

    private static bool IsOccupiedPk4(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.IndexOfAnyExcept((byte)0) < 0) return false;
        var decrypted = DecryptPk4(encrypted);
        var species = BitConverter.ToUInt16(decrypted, 8);
        return BitConverter.ToUInt16(decrypted, 6) == StoredChecksum(decrypted) && species is > 0 and <= 1440;
    }

    public static byte[] NewPk4(ushort species, uint experience, byte level, bool party, ushort[]? moves = null, byte[]? pp = null, int[]? ivs = null)
    {
        var data = new byte[party ? PartySize : StoredSize];
        var pid = 0x12345678u;
        BitConverter.GetBytes(pid).CopyTo(data, 0);
        BitConverter.GetBytes(species).CopyTo(data, 8);
        BitConverter.GetBytes(experience).CopyTo(data, 0x10);
        data[0x14] = 70;
        for (var i = 0; i < 4 && moves is not null; i++)
        {
            if (i < moves.Length) BitConverter.GetBytes(moves[i]).CopyTo(data, 0x28 + i * 2);
            if (pp is not null && i < pp.Length) data[0x30 + i] = pp[i];
        }
        var ivWord = 0u;
        for (var i = 0; i < 6; i++)
        {
            var iv = ivs is not null && i < ivs.Length ? Math.Clamp(ivs[i], 0, 31) : 31;
            ivWord |= (uint)iv << (5 * i);
        }
        BitConverter.GetBytes(ivWord).CopyTo(data, 0x38);
        if (party)
        {
            data[0x8C] = level;
            BitConverter.GetBytes((ushort)Math.Max(1, level * 3)).CopyTo(data, 0x8E);
            BitConverter.GetBytes((ushort)Math.Max(1, level * 3)).CopyTo(data, 0x90);
        }
        return EncryptPk4(data);
    }

    public static byte[] EncryptPk4(ReadOnlySpan<byte> decrypted)
    {
        if (decrypted.Length is not (StoredSize or PartySize))
            throw new ArgumentException("PK4 记录必须是 136 或 236 字节。", nameof(decrypted));
        var result = decrypted.ToArray();
        var pid = BitConverter.ToUInt32(result, 0);
        var checksum = StoredChecksum(result);
        BitConverter.GetBytes(checksum).CopyTo(result, 6);
        var shuffle = (int)((pid >> 13) & 31);
        UnshuffleStoredBlocks(result.AsSpan(8, StoredSize - 8), shuffle);
        CryptArray(result.AsSpan(8, StoredSize - 8), checksum);
        if (decrypted.Length == PartySize)
            CryptArray(result.AsSpan(StoredSize), pid);
        return result;
    }

    public static void RefreshFooter(byte[] data, OriginHeartGoldSaveBlock block)
    {
        var crc = Crc16Ccitt(data.AsSpan(block.Offset, block.Size - FooterSize));
        BitConverter.GetBytes(crc).CopyTo(data, block.Offset + block.Size - 2);
    }

    public static OriginHeartGoldSaveProbe Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var data = File.ReadAllBytes(path);
        if (data.Length != FileSize)
            throw new InvalidDataException($"起源心金存档大小应为 {FileSize}，实际为 {data.Length}。");

        var generalA = ReadBlock(data, 0, GeneralSize, 0, "general");
        var generalB = ReadBlock(data, PartitionSize, GeneralSize, 1, "general");
        var storageA = ReadBlock(data, StorageStart, StorageSize, 0, "storage");
        var storageB = ReadBlock(data, PartitionSize + StorageStart, StorageSize, 1, "storage");
        if (!generalA.ChecksumValid && !generalB.ChecksumValid)
            throw new InvalidDataException("两个 General 块的 CRC16 都不匹配。");
        if (!storageA.ChecksumValid && !storageB.ChecksumValid)
            throw new InvalidDataException("两个 Storage 块的 CRC16 都不匹配。");

        var general = generalA.SaveCount >= generalB.SaveCount ? generalA : generalB;
        var storage = storageA.SaveCount >= storageB.SaveCount ? storageA : storageB;
        var backupGeneral = generalA.Offset == general.Offset ? generalB : generalA;
        var generalBytes = data.AsSpan(general.Offset, general.Size);
        var backupGeneralBytes = data.AsSpan(backupGeneral.Offset, backupGeneral.Size);
        var storageBytes = data.AsSpan(storage.Offset, storage.Size);

        var partyCount = generalBytes[PartyCountOffset];
        if (partyCount > PartySlots)
            throw new InvalidDataException($"队伍数量 {partyCount} 超出 0..{PartySlots}。");

        var party = new List<OriginHeartGoldPartyMon>(partyCount);
        for (var slot = 0; slot < partyCount; slot++)
        {
            var offset = PartyOffset + slot * PartySize;
            party.Add(ReadPartyMon(slot + 1, offset, generalBytes.Slice(offset, PartySize)));
        }

        var boxMons = ReadBoxes(storageBytes);
        var bagPockets = ReadBagPockets(generalBytes);
        return new OriginHeartGoldSaveProbe(
            Path.GetFileName(path),
            data.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(),
            general,
            backupGeneral,
            storage,
            storageA.Offset == storage.Offset ? storageB : storageA,
            BitConverter.ToUInt32(generalBytes[0x74..]),
            BitConverter.ToUInt16(generalBytes[0x74..]),
            BitConverter.ToUInt16(generalBytes[0x76..]),
            BitConverter.ToUInt32(generalBytes[0x78..]),
            BitConverter.ToUInt32(backupGeneralBytes[0x78..]),
            generalBytes[0x7C],
            BitConverter.ToUInt16(generalBytes[0x86..]),
            generalBytes[0x88],
            generalBytes[0x89],
            ReadOtCodes(generalBytes),
            partyCount,
            party,
            ReadBagRun(generalBytes, CandidateItemsOffset, 16),
            ReadBagRun(generalBytes, CandidateBallsOffset, 16),
            bagPockets,
            boxMons,
            boxMons.Count,
            [
                "PK4 解密后 checksum 必须匹配才视为有效队伍或箱子记录。",
                "箱子：24 箱 × 30 槽 × 136 字节，箱跨度 0x1000。样本第 1 箱第 1/30 格、第 24 箱第 1 格 checksum 有效。",
                "背包八口袋从 0x644 铺到 0xEA4，容量与 ROM 0x020FFF9C 的 165/50/151/12/40/64/24/30 闭合；空口袋没有占用样本，但边界不再是猜测。",
                "金钱 u32@0x78 为明文。本样本活动分区与备份分区可对照。",
                "OT 码点在 General 0x64，样本为 0x0399 + FFFF；不是银行 232 物种名。",
                "队伍 AbilityField@0x15 是 PK4 特性槽（0/1），不是种族值特性 ID。",
                "本解析只读，不刷新 CRC，不写回。"
            ]);
    }

    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    public static byte[] DecryptPk4(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length is not (StoredSize or PartySize))
            throw new ArgumentException("PK4 记录必须是 136 或 236 字节。", nameof(encrypted));

        var result = encrypted.ToArray();
        var pid = BitConverter.ToUInt32(result, 0);
        var checksum = BitConverter.ToUInt16(result, 6);
        var shuffle = (int)((pid >> 13) & 31);
        CryptArray(result.AsSpan(8, StoredSize - 8), checksum);
        ShuffleStoredBlocks(result.AsSpan(8, StoredSize - 8), shuffle);
        if (encrypted.Length == PartySize)
            CryptArray(result.AsSpan(StoredSize), pid);
        return result;
    }

    public static ushort StoredChecksum(ReadOnlySpan<byte> decrypted)
    {
        ushort sum = 0;
        for (var i = 8; i < StoredSize; i += 2)
            sum += BitConverter.ToUInt16(decrypted[i..]);
        return sum;
    }

    private static OriginHeartGoldSaveBlock ReadBlock(byte[] data, int offset, int size, int partition, string kind)
    {
        if (offset < 0 || offset + size > data.Length)
            throw new InvalidDataException($"{kind} 块越界。");
        var footer = data.AsSpan(offset + size - FooterSize, FooterSize);
        var saveCount = BitConverter.ToUInt32(footer);
        var sizeField = BitConverter.ToUInt32(footer[4..]);
        var magic = BitConverter.ToUInt32(footer[8..]);
        var unknown = BitConverter.ToUInt16(footer[12..]);
        var storedCrc = BitConverter.ToUInt16(footer[14..]);
        var calculated = Crc16Ccitt(data.AsSpan(offset, size - FooterSize));
        return new OriginHeartGoldSaveBlock(partition, kind, offset, size, saveCount, sizeField, magic, unknown, storedCrc, calculated);
    }

    private static OriginHeartGoldPartyMon ReadPartyMon(int slot, int offset, ReadOnlySpan<byte> encrypted)
    {
        var decrypted = DecryptPk4(encrypted);
        var checksum = BitConverter.ToUInt16(decrypted, 6);
        var valid = checksum == StoredChecksum(decrypted);
        var ivWord = BitConverter.ToUInt32(decrypted, 0x38);
        var ivs = Enumerable.Range(0, 6).Select(i => (int)((ivWord >> (5 * i)) & 31)).ToArray();
        return new OriginHeartGoldPartyMon(
            slot,
            offset,
            BitConverter.ToUInt32(decrypted, 0),
            checksum,
            valid,
            BitConverter.ToUInt16(decrypted, 8),
            BitConverter.ToUInt16(decrypted, 0x0A),
            BitConverter.ToUInt32(decrypted, 0x10),
            decrypted[0x8C],
            decrypted[0x15],
            decrypted[0x14],
            decrypted.AsSpan(0x30, 4).ToArray(),
            Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt16(decrypted, 0x28 + i * 2)).ToArray(),
            decrypted.AsSpan(0x18, 6).ToArray(),
            ivs,
            BitConverter.ToUInt16(decrypted, 0x8E),
            BitConverter.ToUInt16(decrypted, 0x90),
            BitConverter.ToUInt16(decrypted, 0x92),
            BitConverter.ToUInt16(decrypted, 0x94),
            BitConverter.ToUInt16(decrypted, 0x96),
            BitConverter.ToUInt16(decrypted, 0x98),
            BitConverter.ToUInt16(decrypted, 0x9A),
            Enumerable.Range(0, 11).Select(i => BitConverter.ToUInt16(decrypted, 0x48 + i * 2)).ToArray(),
            Enumerable.Range(0, 8).Select(i => BitConverter.ToUInt16(decrypted, 0x68 + i * 2)).ToArray());
    }

    private static ushort[] ReadOtCodes(ReadOnlySpan<byte> general)
    {
        var codes = new ushort[8];
        for (var i = 0; i < codes.Length; i++)
            codes[i] = BitConverter.ToUInt16(general[(TrainerOffset + i * 2)..]);
        return codes;
    }

    private static IReadOnlyList<OriginHeartGoldBagSlot> ReadBagRun(ReadOnlySpan<byte> general, int offset, int maxSlots)
    {
        var slots = new List<OriginHeartGoldBagSlot>();
        for (var i = 0; i < maxSlots; i++)
        {
            var at = offset + i * 4;
            var item = BitConverter.ToUInt16(general[at..]);
            var quantity = BitConverter.ToUInt16(general[(at + 2)..]);
            if (item == 0) break;
            slots.Add(new OriginHeartGoldBagSlot(at, item, quantity));
        }
        return slots;
    }

    private static IReadOnlyList<OriginHeartGoldBagPocket> ReadBagPockets(ReadOnlySpan<byte> general)
        =>
        [
            ReadPocket("道具", ItemsPocketOffset, ItemsPocketCapacity, general),
            ReadPocket("重要物品", KeyPocketOffset, KeyPocketCapacity, general),
            ReadPocket("技能机", MachinePocketOffset, MachinePocketCapacity, general),
            ReadPocket("信件", MailPocketOffset, MailPocketCapacity, general),
            ReadPocket("回复", MedicinePocketOffset, MedicinePocketCapacity, general),
            ReadPocket("树果", BerryPocketOffset, BerryPocketCapacity, general),
            ReadPocket("精灵球", BallPocketOffset, BallPocketCapacity, general),
            ReadPocket("战斗用品", BattlePocketOffset, BattlePocketCapacity, general)
        ];

    private static OriginHeartGoldBagPocket ReadPocket(string name, int offset, int capacity, ReadOnlySpan<byte> general)
        => new(name, offset, capacity, ReadBagRun(general, offset, capacity));

    private static IReadOnlyList<OriginHeartGoldBoxMon> ReadBoxes(ReadOnlySpan<byte> storage)
    {
        var occupied = new List<OriginHeartGoldBoxMon>();
        var limit = storage.Length - FooterSize;
        for (var box = 0; box < BoxCount; box++)
        {
            for (var slot = 0; slot < BoxSlots; slot++)
            {
                var offset = box * BoxStride + slot * StoredSize;
                if (offset + StoredSize > limit) return occupied;
                var record = storage.Slice(offset, StoredSize);
                if (record.IndexOfAnyExcept((byte)0) < 0) continue;
                var decrypted = DecryptPk4(record);
                var valid = BitConverter.ToUInt16(decrypted, 6) == StoredChecksum(decrypted);
                var species = BitConverter.ToUInt16(decrypted, 8);
                if (!valid || species is 0 or > 1440) continue;
                var ivWord = BitConverter.ToUInt32(decrypted, 0x38);
                occupied.Add(new OriginHeartGoldBoxMon(
                    box + 1,
                    slot + 1,
                    offset,
                    BitConverter.ToUInt32(decrypted, 0),
                    species,
                    BitConverter.ToUInt16(decrypted, 0x0A),
                    BitConverter.ToUInt32(decrypted, 0x10),
                    decrypted[0x14],
                    decrypted[0x15],
                    Enumerable.Range(0, 4).Select(i => BitConverter.ToUInt16(decrypted, 0x28 + i * 2)).ToArray(),
                    decrypted.AsSpan(0x30, 4).ToArray(),
                    Enumerable.Range(0, 6).Select(i => (int)((ivWord >> (5 * i)) & 31)).ToArray(),
                    decrypted.AsSpan(0x18, 6).ToArray(),
                    valid));
            }
        }
        return occupied;
    }

    private static int CountOccupiedBoxes(ReadOnlySpan<byte> storage)
    {
        var occupied = 0;
        var limit = Math.Min(storage.Length - FooterSize, 24 * 0x1000);
        for (var box = 0; box < 24 && box * 0x1000 + 30 * StoredSize <= limit; box++)
        {
            for (var slot = 0; slot < 30; slot++)
            {
                var offset = box * 0x1000 + slot * StoredSize;
                var record = storage.Slice(offset, StoredSize);
                if (record.IndexOfAnyExcept((byte)0) < 0) continue;
                var decrypted = DecryptPk4(record);
                if (BitConverter.ToUInt16(decrypted, 6) != StoredChecksum(decrypted)) continue;
                var species = BitConverter.ToUInt16(decrypted, 8);
                if (species is > 0 and <= 1440) occupied++;
            }
        }
        return occupied;
    }

    private static readonly int[] BlockPosition =
    [
        0, 1, 2, 3, 0, 1, 3, 2, 0, 2, 1, 3, 0, 3, 1, 2,
        0, 2, 3, 1, 0, 3, 2, 1, 1, 0, 2, 3, 1, 0, 3, 2,
        2, 0, 1, 3, 3, 0, 1, 2, 2, 0, 3, 1, 3, 0, 2, 1,
        1, 2, 0, 3, 1, 3, 0, 2, 2, 1, 0, 3, 3, 1, 0, 2,
        2, 3, 0, 1, 3, 2, 0, 1, 1, 2, 3, 0, 1, 3, 2, 0,
        2, 1, 3, 0, 3, 1, 2, 0, 2, 3, 1, 0, 3, 2, 1, 0,
        0, 1, 2, 3, 0, 1, 3, 2, 0, 2, 1, 3, 0, 3, 1, 2,
        0, 2, 3, 1, 0, 3, 2, 1, 1, 0, 2, 3, 1, 0, 3, 2
    ];

    private static void CryptArray(Span<byte> data, uint seed)
    {
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            seed = (0x41C64E6Du * seed + 0x00006073u);
            var xor = (ushort)(seed >> 16);
            var value = (ushort)(data[i] | data[i + 1] << 8);
            value ^= xor;
            data[i] = (byte)value;
            data[i + 1] = (byte)(value >> 8);
        }
    }

    private static void ShuffleStoredBlocks(Span<byte> stored, int shuffle)
    {
        var desired = BlockPosition.AsSpan(shuffle * 4, 4);
        Span<byte> copy = stackalloc byte[StoredSize - 8];
        stored.CopyTo(copy);
        for (var i = 0; i < 4; i++)
            copy.Slice(desired[i] * 32, 32).CopyTo(stored.Slice(i * 32, 32));
    }

    private static void UnshuffleStoredBlocks(Span<byte> stored, int shuffle)
    {
        var desired = BlockPosition.AsSpan(shuffle * 4, 4);
        Span<byte> copy = stackalloc byte[StoredSize - 8];
        stored.CopyTo(copy);
        for (var i = 0; i < 4; i++)
            copy.Slice(i * 32, 32).CopyTo(stored.Slice(desired[i] * 32, 32));
    }
}
