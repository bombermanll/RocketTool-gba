namespace RocketTool.Core;

internal sealed class SpanishRocket305SaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "spanish-rocket-305-save-v1";
    public static SpanishRocket305SaveStrategy Instance { get; } = new();
    public string Id => StrategyId;
    public int ExpectedSectionCount => 28;

    public Gen3SaveDocument Open(string path, GameProfile profile) => SpanishRocket305SaveReader.Open(path, profile);

    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes)
    {
        if (nameBytes.Length == 0 || nameBytes.Length > 7)
            throw new InvalidOperationException("3.0.5 训练家名字必须是 1 到 7 字节。");
        var buffer = new byte[7];
        Array.Fill(buffer, (byte)0xFF);
        nameBytes.CopyTo(buffer);
        document.WriteSectionRange(0, 0, buffer);
    }

    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money)
    {
        var profile = document.Profile;
        if (money > profile.Runtime.MaxTrainerMoney)
            throw new InvalidOperationException($"金钱必须在 0..{profile.Runtime.MaxTrainerMoney}。");
        var key = document.ReadSectionU32(0, (int)profile.Memory.SaveBlock2EncryptionKeyOffset);
        var encrypted = money ^ key;
        Span<byte> bytes = stackalloc byte[4];
        bytes[0] = (byte)encrypted;
        bytes[1] = (byte)(encrypted >> 8);
        bytes[2] = (byte)(encrypted >> 16);
        bytes[3] = (byte)(encrypted >> 24);
        document.WriteLogicalRange(Gen3SaveDocument.SaveBlock1FirstSection, (int)profile.Memory.SaveBlock1MoneyOffset, bytes);
    }

    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data)
    {
        if (data.Length != BoxPokemon.Size)
            throw new InvalidOperationException("3.0.5 箱子记录必须是 80 字节。");
        var offset = document.Profile.Memory.PcBoxDataOffset + (globalSlot - 1) * BoxPokemon.Size;
        document.WriteLogicalRange(Gen3SaveDocument.PcFirstSection, offset, data);
    }

    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey) => (ushort)(quantity ^ quantityKey);
    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket) => [];
    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId)
        => document.ItemPockets.TryGetValue(itemId, out var pocket) ? pocket : null;
    public string PocketName(int pocket) => $"口袋{pocket}";
    public void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data)
        => document.WriteLogicalRange(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset, data);

    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset)
        => document.ReadLogicalU16(Gen3SaveDocument.SaveBlock1FirstSection, saveOffset);
}

internal static class SpanishRocket305SaveReader
{
    private const int SectionSize = 0x1000;
    private const int SectionPayload = 0xFF4;
    private const int SectionCount = 28;
    private const uint Signature = 0x08012025;
    private const int SaveBlock2Size = 0x9CC;
    private const int SaveBlock1Tail = 0x9C8;
    private const int PcChunkCount = 14;
    private const int PcTail = 1068;
    private const int PartyCountOffset = 0x234;
    private const int PartyOffset = 0x238;
    private const int BoxSlots = 30;

    private static readonly (int Pocket, int Offset, int Capacity)[] BagPockets =
    [
        (0, 0x4C0, 210),
        (1, 0xE98, 65),
        (2, 0x990, 254),
        (3, 0x808, 70),
        (4, 0x920, 28),
        (5, 0xF9C, 210),
        (6, 0xC88, 68),
        (7, 0x130C, 150),
    ];

    public static Gen3SaveDocument Open(string path, GameProfile profile)
    {
        var raw = File.ReadAllBytes(path);
        var sections = ReadSections(raw);
        if (sections.Count != SectionCount)
            throw new InvalidOperationException($"只识别到 {sections.Count}/{SectionCount} 个 3.0.5 存档 section，已停止读取。");

        var saveBlock2 = sections[0].Data.AsSpan(0, SaveBlock2Size).ToArray();
        var saveBlock1 = new byte[SectionPayload * 3 + SaveBlock1Tail];
        sections[1].Data.AsSpan(0, SectionPayload).CopyTo(saveBlock1.AsSpan(0, SectionPayload));
        sections[2].Data.AsSpan(0, SectionPayload).CopyTo(saveBlock1.AsSpan(SectionPayload, SectionPayload));
        sections[3].Data.AsSpan(0, SectionPayload).CopyTo(saveBlock1.AsSpan(SectionPayload * 2, SectionPayload));
        sections[4].Data.AsSpan(0, SaveBlock1Tail).CopyTo(saveBlock1.AsSpan(SectionPayload * 3, SaveBlock1Tail));

        var pc = new byte[PcChunkCount * SectionPayload + PcTail];
        for (var id = 0; id < PcChunkCount; id++)
            sections[5 + id].Data.AsSpan(0, SectionPayload).CopyTo(pc.AsSpan(id * SectionPayload, SectionPayload));
        sections[19].Data.AsSpan(0, PcTail).CopyTo(pc.AsSpan(PcChunkCount * SectionPayload, PcTail));

        var key = ReadU32(saveBlock2, (int)profile.Memory.SaveBlock2EncryptionKeyOffset);
        var money = ReadU32(saveBlock1, (int)profile.Memory.SaveBlock1MoneyOffset) ^ key;
        if (money > profile.Runtime.MaxTrainerMoney)
            throw new InvalidOperationException($"3.0.5 存档金钱解密为 {money}，超出已确认上限。已停止读取。");
        var coins = (ushort)(ReadU16(saveBlock1, (int)profile.Memory.SaveBlock1CoinsOffset) ^ (ushort)key);
        if (coins > profile.Runtime.MaxTrainerCoins)
            throw new InvalidOperationException($"3.0.5 存档游戏币解密为 {coins}，超出已确认上限。已停止读取。");

        var partyCount = saveBlock1[PartyCountOffset];
        if (partyCount > 6)
            throw new InvalidOperationException($"3.0.5 存档队伍数量是 {partyCount}，不在 0 到 6 之间。已停止读取。");
        var party = new List<PartyPokemon>();
        for (var slot = 0; slot < partyCount; slot++)
        {
            var rec = saveBlock1.AsSpan(PartyOffset + slot * PartyPokemon.Size, PartyPokemon.Size);
            var mon = new PartyPokemon(rec, PokemonDataLayout.SpanishRocket305Encrypted);
            if (mon.IsEmpty || mon.GetInfo().Checksum != mon.GetInfo().CalculatedChecksum)
                throw new InvalidOperationException($"3.0.5 存档队伍第 {slot + 1} 格校验失败，已停止读取。");
            party.Add(mon);
        }

        var boxes = new List<Gen3SaveBoxEntry>();
        var corrupt = new List<Gen3SaveCorruptBoxEntry>();
        var boxCount = Math.Min(profile.Memory.PcBoxCount, 24);
        for (var box = 0; box < boxCount; box++)
        {
            for (var slot = 0; slot < BoxSlots; slot++)
            {
                var offset = profile.Memory.PcBoxDataOffset + (box * BoxSlots + slot) * BoxPokemon.Size;
                if (offset + BoxPokemon.Size > pc.Length) continue;
                var rec = pc.AsSpan(offset, BoxPokemon.Size);
                if (IsEmptyBox(rec)) continue;
                var mon = new BoxPokemon(rec, PokemonDataLayout.SpanishRocket305Encrypted);
                var info = mon.GetInfo();
                var global = box * BoxSlots + slot + 1;
                if (info.Checksum != info.CalculatedChecksum || info.Species < 1 || info.Species > profile.Limits.MaxSpecies)
                {
                    corrupt.Add(new Gen3SaveCorruptBoxEntry(global, box + 1, slot + 1, offset, "校验或种族编号未通过"));
                    continue;
                }
                boxes.Add(new Gen3SaveBoxEntry(global, box + 1, slot + 1, offset, mon));
            }
        }

        var quantityKey = (ushort)key;
        var bag = new List<Gen3SaveBagEntry>();
        foreach (var (pocket, offset, capacity) in BagPockets)
        {
            var shown = 0;
            for (var slot = 0; slot < capacity; slot++)
            {
                var at = offset + slot * 4;
                var item = ReadU16(saveBlock1, at);
                if (item == 0 || item > profile.Limits.MaxItem) continue;
                var quantity = (ushort)(ReadU16(saveBlock1, at + 2) ^ quantityKey);
                if (quantity == 0 || quantity > profile.Limits.MaxBagQuantity) continue;
                shown++;
                bag.Add(new Gen3SaveBagEntry(at, pocket, shown, item, quantity, quantityKey, true, "存档块 1 背包槽，数量已按密钥还原"));
            }
        }

        var trainer = new Gen3SaveTrainerInfo(
            saveBlock2.AsSpan(0, profile.Memory.PlayerNameLength).ToArray(),
            ReadU32(saveBlock2, profile.Memory.SaveBlock2PlayerOtIdOffset),
            money,
            Coins: coins);
        var warnings = new List<string>
        {
            "3.0.5 存档按 28 个 section、0xFF4 校验读取。写入仍关闭。",
            "队伍在存档块 1 +0x238，箱子从 section 5 起，背包偏移已用这份存档的珍珠、果汁牛奶和离洞绳对过。"
        };
        var snapshot = new Gen3SaveReadResult(
            Path.GetFileName(path), raw.Length, 1, sections[0].SaveIndex, sections.Count,
            party, bag, boxes, trainer, warnings)
        {
            CorruptBoxes = corrupt
        };
        var layouts = sections.ToDictionary(
            pair => pair.Key,
            pair => new Gen3SaveSectionLayout(pair.Key, pair.Value.FileOffset, pair.Value.SaveIndex, "ff4"));
        return new Gen3SaveDocument(
            path, raw, layouts, PartyCountOffset, PartyOffset, snapshot, profile,
            ReadItemPockets(profile), SpanishRocket305SaveStrategy.Instance);
    }

    private static Dictionary<int, int> ReadItemPockets(GameProfile profile)
    {
        var table = new ModifierDatabase(profile.DatabaseDirectory).Table("item_pockets");
        var pockets = new Dictionary<int, int>();
        foreach (var (item, text) in table)
        {
            if (int.TryParse(text, out var pocket) && pocket >= 0)
                pockets[item] = pocket;
        }
        return pockets;
    }

    private static Dictionary<int, SaveSection> ReadSections(byte[] raw)
    {
        var sections = new Dictionary<int, SaveSection>();
        for (var offset = 0; offset + SectionSize <= raw.Length; offset += SectionSize)
        {
            var section = raw.AsSpan(offset, SectionSize);
            if (ReadU32(section, 0xFF8) != Signature) continue;
            var id = ReadU16(section, 0xFF4);
            if (id >= SectionCount || sections.ContainsKey(id)) continue;
            var payload = section[..SectionPayload];
            if (Checksum(payload) != ReadU16(section, 0xFF6)) continue;
            sections[id] = new SaveSection(id, offset, ReadU32(section, 0xFFC), payload.ToArray());
        }
        return sections;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint total = 0;
        for (var i = 0; i + 3 < data.Length; i += 4)
            total = (total + ReadU32(data, i)) & 0xFFFFFFFF;
        return (ushort)(((total >> 16) + (total & 0xFFFF)) & 0xFFFF);
    }

    private static bool IsEmptyBox(ReadOnlySpan<byte> rec)
        => ReadU32(rec, 0) == 0 && ReadU32(rec, 4) == 0;

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | data[offset + 1] << 8);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private sealed record SaveSection(int Id, int FileOffset, uint SaveIndex, byte[] Data);
}
