namespace RocketTool.Core;

public sealed record BagSlot(uint Address, ushort ItemId, ushort Quantity, int Score, string Note);

public sealed record BagRun(uint StartAddress, int SlotCount, int NonEmptyCount, int Score, IReadOnlyList<BagSlot> Slots);

public sealed record BagPocketDefinition(
    int Pocket,
    string Name,
    uint Offset,
    int SlotCount,
    bool QuantityXor,
    ushort QuantityKey);

public sealed record BagPocket(
    int Pocket,
    uint StartAddress,
    int SlotCount,
    int NonEmptyCount,
    int Score,
    IReadOnlyList<BagSlot> Slots);

public sealed record BagSlotChange(
    uint Address,
    ushort BeforeItemId,
    ushort BeforeQuantity,
    ushort AfterItemId,
    ushort AfterQuantity,
    int Score,
    string Note);

public sealed record BagAddTarget(
    uint Address,
    ushort ItemId,
    ushort BeforeQuantity,
    ushort AfterQuantity,
    ushort QuantityKey,
    bool IsExistingItem,
    string Note);

public static class BagScanner
{
    public const int DefaultMaxItemId = 2000;
    public const int DefaultMaxQuantity = 999;
    public const int MaxRunSlots = 80;
    public const uint SaveBlockPointerAddress = 0x0300524C;

    public static readonly IReadOnlyList<BagPocketDefinition> KnownPockets =
    [
        new(1, "普通道具", 0x1E78, 0x78, true, 0x4D23),
        new(2, "回复药品", 0x2884, 0x78, true, 0x6D5F),
        new(3, "精灵球", 0x2348, 0x78, true, 0x698B),
        new(4, "战斗道具", 0x29E0, 0x78, true, 0x3502),
        new(5, "树果", 0x27D0, 0x78, true, 0x5B67),
        new(6, "宝物", 0x2BF0, 0x78, true, 0xB01B),
        new(7, "招式机器/秘传机器", 0x23A0, 0x80, true, 0xFC51),
        new(8, "重要物品", 0x2268, 0x78, true, 0x5145),
    ];

    private static readonly Dictionary<int, int> PocketCapacityHints = new()
    {
        [1] = 0x78,
        [2] = 0x78,
        [3] = 0x78,
        [4] = 0x78,
        [5] = 0x78,
        [6] = 0x78,
        [7] = 0x80,
        [8] = 0x78,
    };

    public static uint LocateSaveBlockBase(MgbaBridgeClient bridge)
    {
        var bytes = bridge.Read(SaveBlockPointerAddress, 4);
        var address = U32(bytes, 0);
        var lastPocket = KnownPockets.MaxBy(p => p.Offset + (uint)p.SlotCount * 4U)!;
        if (address < PartyScanner.EwramBase ||
            address + lastPocket.Offset + (uint)lastPocket.SlotCount * 4U > PartyScanner.EwramBase + PartyScanner.EwramSize)
        {
            throw new InvalidOperationException(
                $"没有定位到有效背包基址：0x{SaveBlockPointerAddress:X8} -> 0x{address:X8}。请先进入游戏存档/背包界面后再试。");
        }

        return address;
    }

    public static IReadOnlyList<BagPocket> ReadKnownPockets(
        MgbaBridgeClient bridge,
        bool includeEmpty = false,
        int maxQuantity = DefaultMaxQuantity)
    {
        var saveBlockBase = LocateSaveBlockBase(bridge);
        var pockets = new List<BagPocket>();
        foreach (var definition in KnownPockets)
        {
            var start = saveBlockBase + definition.Offset;
            var raw = bridge.Read(start, definition.SlotCount * 4);
            var slots = new List<BagSlot>();
            var nonEmpty = 0;
            var score = 0;

            for (var i = 0; i < definition.SlotCount; i++)
            {
                var item = U16(raw, i * 4);
                var rawQuantity = U16(raw, i * 4 + 2);
                var address = start + (uint)i * 4U;
                if (item == 0 && rawQuantity == 0)
                {
                    if (includeEmpty)
                        slots.Add(new BagSlot(address, 0, 0, 1, "空槽"));
                    continue;
                }

                var quantity = DecodeQuantity(rawQuantity, definition.QuantityKey, maxQuantity);
                if (!IsPocketItem(definition.Pocket, item) || quantity is null or 0 or > DefaultMaxQuantity)
                {
                    if (includeEmpty)
                        slots.Add(new BagSlot(address, item, quantity ?? rawQuantity, -20, "不像该口袋的有效槽"));
                    continue;
                }

                nonEmpty++;
                score += 25;
                slots.Add(new BagSlot(address, item, quantity.Value, 25, "按原修改器口袋表解析"));
            }

            pockets.Add(new BagPocket(
                definition.Pocket,
                start,
                definition.SlotCount,
                nonEmpty,
                score,
                slots.ToArray()));
        }

        return pockets;
    }

    public static IReadOnlyList<BagPocket> FindLivePockets(
        ReadOnlySpan<byte> ewram,
        uint? saveBlockBase,
        Func<int, int> itemPocket,
        int maxQuantity = DefaultMaxQuantity)
    {
        var quantityKey = InferQuantityKey(ewram);
        var startOffset = 0;
        var endOffset = ewram.Length;
        if (saveBlockBase is >= PartyScanner.EwramBase and < PartyScanner.EwramBase + PartyScanner.EwramSize)
        {
            startOffset = Math.Max(0, (int)(saveBlockBase.Value - PartyScanner.EwramBase));
            endOffset = Math.Min(ewram.Length, startOffset + 0x4000);
        }

        var pockets = new List<BagPocket>();
        for (var pocket = 1; pocket <= 8; pocket++)
        {
            var best = FindBestLivePocket(ewram, startOffset, endOffset, pocket, quantityKey, itemPocket, maxQuantity);
            if (best is not null)
                pockets.Add(best);
        }

        return pockets.OrderBy(p => p.Pocket).ToArray();
    }

    public static ushort InferQuantityKey(ReadOnlySpan<byte> ewram)
    {
        var counts = new Dictionary<ushort, int>();
        for (var off = 0; off <= ewram.Length - 4; off += 4)
        {
            var item = U16(ewram, off);
            var rawQuantity = U16(ewram, off + 2);
            if (item != 0 || rawQuantity is 0 or 0xFFFF) continue;
            counts[rawQuantity] = counts.GetValueOrDefault(rawQuantity) + 1;
        }

        return counts.Count == 0
            ? (ushort)0
            : counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    public static BagAddTarget FindAddTarget(
        ReadOnlySpan<byte> ewram,
        uint? saveBlockBase,
        int pocket,
        ushort itemId,
        ushort addQuantity,
        Func<int, int> itemPocket,
        int maxQuantity = DefaultMaxQuantity)
    {
        if (itemId == 0) throw new InvalidOperationException("不能添加空道具。");
        if (addQuantity == 0) throw new InvalidOperationException("添加数量必须大于 0。");
        var actualPocket = itemPocket(itemId);
        if (actualPocket is < 1 or > 8)
            throw new InvalidOperationException($"无法判断道具 {itemId} 所属口袋。");
        if (pocket is > 0 && pocket != actualPocket)
            throw new InvalidOperationException($"该道具属于 {DefinitionForPocket(actualPocket)?.Name ?? $"口袋{actualPocket}"}，不能添加到当前口袋。");

        pocket = actualPocket;
        var quantityKey = InferQuantityKey(ewram);
        var startOffset = 0;
        var endOffset = ewram.Length;
        int? knownPocketStart = null;
        int? knownPocketEnd = null;
        if (saveBlockBase is >= PartyScanner.EwramBase and < PartyScanner.EwramBase + PartyScanner.EwramSize)
        {
            startOffset = Math.Max(0, (int)(saveBlockBase.Value - PartyScanner.EwramBase));
            endOffset = Math.Min(ewram.Length, startOffset + 0x4000);
            if (DefinitionForPocket(pocket) is { } definition)
            {
                var pocketStart = startOffset + checked((int)definition.Offset);
                var pocketEnd = pocketStart + definition.SlotCount * 4;
                if (pocketStart >= 0 && pocketStart < ewram.Length && pocketEnd <= ewram.Length)
                {
                    knownPocketStart = pocketStart;
                    knownPocketEnd = pocketEnd;
                }
            }
        }

        var existingStart = knownPocketStart ?? startOffset;
        var existingEnd = knownPocketEnd ?? endOffset;
        for (var off = existingStart; off <= existingEnd - 4; off += 4)
        {
            var slot = ParseLiveSlot(ewram, off, PartyScanner.EwramBase + (uint)off, quantityKey, itemPocket, maxQuantity);
            if (slot is null || slot.ItemId != itemId || itemPocket(slot.ItemId) != pocket) continue;
            var newQuantity = checked((int)slot.Quantity + addQuantity);
            if (newQuantity > maxQuantity)
                throw new InvalidOperationException($"已有 {slot.Quantity} 个，追加后会超过上限 {maxQuantity}。");
            return new BagAddTarget(slot.Address, itemId, slot.Quantity, (ushort)newQuantity, quantityKey, true, "已有同道具，改为累加数量");
        }

        var pocketRun = FindLivePockets(ewram, saveBlockBase, itemPocket, maxQuantity)
            .FirstOrDefault(p => p.Pocket == pocket);

        if (knownPocketStart is not null && knownPocketEnd is not null)
        {
            var preferredStart = pocketRun?.Slots.Count > 0
                ? Math.Min(knownPocketEnd.Value, checked((int)(pocketRun.Slots.Max(s => s.Address) + 4U - PartyScanner.EwramBase)))
                : knownPocketStart.Value;
            var emptyOffset = FindEmptySlotOffset(ewram, preferredStart, knownPocketEnd.Value, quantityKey)
                              ?? FindEmptySlotOffset(ewram, knownPocketStart.Value, preferredStart, quantityKey);
            if (emptyOffset is not null)
            {
                return new BagAddTarget(
                    PartyScanner.EwramBase + (uint)emptyOffset.Value,
                    itemId,
                    0,
                    addQuantity,
                    quantityKey,
                    false,
                    "写入当前口袋空槽");
            }
        }

        if (pocketRun is null || pocketRun.Slots.Count == 0)
            throw new InvalidOperationException("当前口袋为空或尚未定位。请先点击“读取背包”，或在游戏内获得该口袋任意一个道具后再添加。");

        var appendAddress = pocketRun.Slots.Max(s => s.Address) + 4;
        var appendOffset = checked((int)(appendAddress - PartyScanner.EwramBase));
        var scanEnd = Math.Min(endOffset, checked((int)(pocketRun.StartAddress - PartyScanner.EwramBase)) + PocketCapacityHints.GetValueOrDefault(pocket, MaxRunSlots) * 4);
        if (appendOffset >= startOffset && appendOffset + 4 <= scanEnd &&
            FindEmptySlotOffset(ewram, appendOffset, scanEnd, quantityKey) is { } fallbackOffset)
        {
            return new BagAddTarget(
                PartyScanner.EwramBase + (uint)fallbackOffset,
                itemId,
                0,
                addQuantity,
                quantityKey,
                false,
                "写入当前口袋后续空槽");
        }

        throw new InvalidOperationException("没有找到可追加的安全空槽。请先点击“读取背包”刷新列表；如果仍失败，说明当前口袋位置需要重新校准。");
    }

    private static int? FindEmptySlotOffset(ReadOnlySpan<byte> ewram, int startOffset, int endOffset, ushort quantityKey)
    {
        startOffset = Math.Max(0, startOffset);
        endOffset = Math.Min(ewram.Length, endOffset);
        for (var off = startOffset; off <= endOffset - 4; off += 4)
        {
            var rawItem = U16(ewram, off);
            var rawQuantity = U16(ewram, off + 2);
            if (rawItem == 0 && (rawQuantity == quantityKey || rawQuantity == 0))
                return off;
        }

        return null;
    }

    private static BagPocket? FindBestLivePocket(
        ReadOnlySpan<byte> ewram,
        int startOffset,
        int endOffset,
        int pocket,
        ushort quantityKey,
        Func<int, int> itemPocket,
        int maxQuantity)
    {
        BagPocket? best = null;
        for (var off = startOffset; off <= endOffset - 4; off += 4)
        {
            var first = ParseLiveSlot(ewram, off, PartyScanner.EwramBase + (uint)off, quantityKey, itemPocket, maxQuantity);
            if (first is null || first.ItemId == 0 || itemPocket(first.ItemId) != pocket)
                continue;

            var slots = new List<BagSlot> { first };
            var nonEmpty = 1;
            var score = 30 + first.Score;
            for (var i = 1; i < MaxRunSlots && off + i * 4 + 4 <= endOffset; i++)
            {
                var slotOff = off + i * 4;
                var slot = ParseLiveSlot(ewram, slotOff, PartyScanner.EwramBase + (uint)slotOff, quantityKey, itemPocket, maxQuantity);
                if (slot is null) break;
                if (slot.ItemId != 0)
                {
                    if (itemPocket(slot.ItemId) != pocket) break;
                    nonEmpty++;
                    score += 30 + slot.Score;
                }
                else
                {
                    break;
                }

                slots.Add(slot);
            }

            // Keep the visible list compact; real pocket storage is contiguous and
            // the remaining empty slots follow the last item.
            var visibleSlots = slots.Where(s => s.ItemId != 0).ToArray();
            if (visibleSlots.Length == 0) continue;
            score += Math.Min(visibleSlots.Length, 12) * 5;
            var candidate = new BagPocket(
                pocket,
                visibleSlots[0].Address,
                slots.Count,
                nonEmpty,
                score,
                visibleSlots);
            if (best is null || candidate.Score > best.Score)
                best = candidate;
        }

        return best;
    }

    private static BagSlot? ParseLiveSlot(
        ReadOnlySpan<byte> ewram,
        int offset,
        uint address,
        ushort quantityKey,
        Func<int, int> itemPocket,
        int maxQuantity)
    {
        var item = U16(ewram, offset);
        var rawQuantity = U16(ewram, offset + 2);
        if (item == 0)
        {
            if (rawQuantity == 0 || rawQuantity == quantityKey)
                return new BagSlot(address, 0, 0, 1, "空槽");
            return null;
        }

        if (item > DefaultMaxItemId) return null;
        var quantity = DecodeQuantity(rawQuantity, quantityKey, maxQuantity);
        if (quantity is null or 0) return null;
        var pocket = itemPocket(item);
        if (pocket is < 1 or > 8) return null;
        var score = 8;
        if (rawQuantity == (ushort)(quantity.Value ^ quantityKey)) score += 8;
        return new BagSlot(address, item, quantity.Value, score, $"自动定位；数量密钥 0x{quantityKey:X4}");
    }

    public static BagPocketDefinition? DefinitionForPocket(int pocket)
        => KnownPockets.FirstOrDefault(p => p.Pocket == pocket);

    public static BagPocketDefinition? DefinitionForAddress(uint saveBlockBase, uint address)
        => KnownPockets.FirstOrDefault(p =>
            address >= saveBlockBase + p.Offset &&
            address < saveBlockBase + p.Offset + (uint)p.SlotCount * 4U &&
            ((address - saveBlockBase - p.Offset) & 3U) == 0);

    public static IReadOnlyList<BagRun> FindRuns(
        ReadOnlySpan<byte> ewram,
        int maxItemId = DefaultMaxItemId,
        int maxQuantity = DefaultMaxQuantity,
        int minNonEmpty = 2)
    {
        var runs = new List<BagRun>();
        for (var off = 0; off <= ewram.Length - 4; off += 4)
        {
            var slots = new List<BagSlot>();
            var nonEmpty = 0;
            for (var slot = 0; slot < MaxRunSlots && off + slot * 4 + 4 <= ewram.Length; slot++)
            {
                var slotOff = off + slot * 4;
                var parsed = ParseSlot(ewram, slotOff, maxItemId, maxQuantity);
                if (parsed is null) break;
                if (parsed.ItemId != 0) nonEmpty++;
                slots.Add(parsed with { Address = PartyScanner.EwramBase + (uint)slotOff });
            }

            if (nonEmpty < minNonEmpty || slots.Count < nonEmpty) continue;

            // Real bag pockets usually contain several valid slots followed by zero terminators.
            var leadingUseful = slots.TakeWhile(s => s.ItemId != 0).Count();
            var score = nonEmpty * 10 + Math.Min(slots.Count - nonEmpty, 8) + leadingUseful;
            runs.Add(new BagRun(PartyScanner.EwramBase + (uint)off, slots.Count, nonEmpty, score, slots.ToArray()));
        }

        return runs
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.NonEmptyCount)
            .ThenBy(r => r.StartAddress)
            .ToArray();
    }

    public static IReadOnlyList<BagPocket> FindPockets(
        ReadOnlySpan<byte> ewram,
        Func<int, int> itemPocket,
        int maxItemId = DefaultMaxItemId,
        int maxQuantity = DefaultMaxQuantity)
    {
        var runs = FindRuns(ewram, maxItemId, maxQuantity, minNonEmpty: 1);
        var best = new Dictionary<int, BagPocket>();

        foreach (var run in runs)
        {
            for (var pocket = 1; pocket <= 6; pocket++)
            {
                var candidate = ScorePocketRun(run, pocket, itemPocket);
                if (candidate is null) continue;
                if (!best.TryGetValue(pocket, out var old) || candidate.Score > old.Score)
                    best[pocket] = candidate;
            }
        }

        return best.Values
            .OrderBy(p => p.Pocket)
            .ToArray();
    }

    public static IReadOnlyList<BagSlotChange> CompareSnapshots(
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after,
        int maxItemId = DefaultMaxItemId,
        int maxQuantity = DefaultMaxQuantity)
    {
        var length = Math.Min(before.Length, after.Length);
        var changes = new List<BagSlotChange>();
        for (var off = 0; off <= length - 4; off += 2)
        {
            var bi = U16(before, off);
            var bq = U16(before, off + 2);
            var ai = U16(after, off);
            var aq = U16(after, off + 2);
            if (bi == ai && bq == aq) continue;

            var beforeValid = IsPlausibleSlot(bi, bq, maxItemId, maxQuantity);
            var afterValid = IsPlausibleSlot(ai, aq, maxItemId, maxQuantity);
            var beforeDecoded = bq;
            var afterDecoded = aq;
            var knownNote = "";
            if (!beforeValid && TryDecodeKnownSlot(bi, bq, maxQuantity, out var bKnownQty, out var bPocket))
            {
                beforeValid = true;
                beforeDecoded = bKnownQty;
                knownNote = $"{DefinitionForPocket(bPocket)?.Name}密钥";
            }
            if (!afterValid && TryDecodeKnownSlot(ai, aq, maxQuantity, out var aKnownQty, out var aPocket))
            {
                afterValid = true;
                afterDecoded = aKnownQty;
                knownNote = $"{DefinitionForPocket(aPocket)?.Name}密钥";
            }
            if (!beforeValid && !afterValid) continue;

            var score = 0;
            var notes = new List<string>();
            if (afterValid) score += 5;
            if (ai is > 0 && ai <= maxItemId) score += 3;
            if (afterDecoded > 0 && afterDecoded <= maxQuantity) score += 3;
            if (bi == ai && beforeDecoded != afterDecoded) { score += 4; notes.Add("数量变化"); }
            if (bi == 0 && ai != 0) { score += 4; notes.Add("新增道具"); }
            if ((off & 3) == 0) { score += 2; notes.Add("4字节对齐"); }
            if (knownNote.Length > 0) { score += 4; notes.Add(knownNote); }

            changes.Add(new BagSlotChange(
                PartyScanner.EwramBase + (uint)off,
                bi,
                beforeDecoded,
                ai,
                afterDecoded,
                score,
                notes.Count == 0 ? "疑似背包槽" : string.Join("，", notes)));
        }

        return changes
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Address)
            .ToArray();
    }

    public static BagSlot ReadSlot(MgbaBridgeClient bridge, uint address, int maxItemId = DefaultMaxItemId, int maxQuantity = DefaultMaxQuantity)
    {
        var bytes = bridge.Read(address, 4);
        var item = U16(bytes, 0);
        var qty = U16(bytes, 2);
        var parsed = ParseSlot(bytes, 0, maxItemId, maxQuantity);
        return parsed is null
            ? new BagSlot(address, item, qty, 0, "不像普通背包槽")
            : parsed with { Address = address };
    }

    public static BagSlot ReadSlot(MgbaBridgeClient bridge, uint address, BagPocketDefinition definition, int maxQuantity = DefaultMaxQuantity)
    {
        var bytes = bridge.Read(address, 4);
        var item = U16(bytes, 0);
        var rawQty = U16(bytes, 2);
        var qty = DecodeQuantity(rawQty, definition.QuantityKey, maxQuantity) ?? rawQty;
        var note = IsPocketItem(definition.Pocket, item) && qty <= maxQuantity
            ? "按原修改器口袋表解析"
            : "不像该口袋的有效槽";
        return new BagSlot(address, item, qty, 0, note);
    }

    public static byte[] EncodeSlot(ushort itemId, ushort quantity, ushort quantityKey = 0, bool quantityXor = false)
    {
        var bytes = new byte[4];
        Put16(bytes, 0, itemId);
        var rawQuantity = itemId != 0 && quantityXor ? (ushort)(quantity ^ quantityKey) : quantity;
        Put16(bytes, 2, rawQuantity);
        return bytes;
    }

    private static ushort? DecodeQuantity(ushort rawQuantity, ushort quantityKey, int maxQuantity)
    {
        if (rawQuantity <= maxQuantity) return rawQuantity;
        var decoded = (ushort)(rawQuantity ^ quantityKey);
        return decoded <= maxQuantity ? decoded : null;
    }

    private static bool TryDecodeKnownSlot(ushort item, ushort rawQuantity, int maxQuantity, out ushort quantity, out int pocket)
    {
        foreach (var definition in KnownPockets)
        {
            if (!IsPocketItem(definition.Pocket, item)) continue;
            var decoded = DecodeQuantity(rawQuantity, definition.QuantityKey, maxQuantity);
            if (decoded is null or 0) continue;
            quantity = decoded.Value;
            pocket = definition.Pocket;
            return true;
        }

        quantity = rawQuantity;
        pocket = -1;
        return false;
    }

    private static bool IsPocketItem(int pocket, ushort item)
    {
        if (item == 0) return true;
        return pocket switch
        {
            1 => item is < 592 or > 911,
            2 => item is >= 28 and <= 48 or 921,
            3 => item is >= 1 and <= 27,
            5 => item is >= 512 and <= 591,
            7 => item is >= 592 and <= 767,
            8 => item >= 0x300,
            _ => item <= 0x400,
        };
    }

    private static BagSlot? ParseSlot(ReadOnlySpan<byte> data, int offset, int maxItemId, int maxQuantity)
    {
        var item = U16(data, offset);
        var qty = U16(data, offset + 2);
        if (!IsPlausibleSlot(item, qty, maxItemId, maxQuantity)) return null;
        if (item == 0) return new BagSlot(0, 0, 0, 1, "空槽");
        var score = 8;
        if (qty is > 0 and <= 99) score += 2;
        if (item <= 1000) score += 1;
        return new BagSlot(0, item, qty, score, "疑似道具槽");
    }

    private static bool IsPlausibleSlot(ushort item, ushort qty, int maxItemId, int maxQuantity)
        => (item == 0 && qty == 0) || (item <= maxItemId && item > 0 && qty > 0 && qty <= maxQuantity);

    private static BagPocket? ScorePocketRun(BagRun run, int pocket, Func<int, int> itemPocket)
    {
        var firstMatch = -1;
        var lastMatch = -1;
        var matches = 0;
        var foreignBefore = 0;
        var foreignInside = 0;

        for (var i = 0; i < run.Slots.Count; i++)
        {
            var slot = run.Slots[i];
            if (slot.ItemId == 0) continue;

            var slotPocket = itemPocket(slot.ItemId);
            if (slotPocket == pocket)
            {
                firstMatch = firstMatch < 0 ? i : firstMatch;
                lastMatch = i;
                matches++;
            }
            else if (firstMatch < 0)
            {
                foreignBefore++;
            }
            else
            {
                foreignInside++;
            }
        }

        if (matches == 0 || firstMatch < 0 || foreignBefore > 2) return null;

        var endExclusive = run.Slots.Count;
        for (var i = firstMatch; i < run.Slots.Count; i++)
        {
            var slot = run.Slots[i];
            if (slot.ItemId == 0) continue;
            var slotPocket = itemPocket(slot.ItemId);
            if (slotPocket != pocket)
            {
                endExclusive = i;
                break;
            }
        }

        var hintedCapacity = PocketCapacityHints.GetValueOrDefault(pocket, MaxRunSlots);
        var minRequired = Math.Max(lastMatch - firstMatch + 1, 1);
        var capacity = Math.Max(hintedCapacity, minRequired);
        var slotCount = Math.Clamp(endExclusive - firstMatch, minRequired, capacity);
        var slots = run.Slots
            .Skip(firstMatch)
            .Take(slotCount)
            .ToArray();
        var score = matches * 100
                    + Math.Min(slotCount, hintedCapacity)
                    - foreignBefore * 40
                    - foreignInside * 20
                    - firstMatch * 2;

        return new BagPocket(
            pocket,
            slots[0].Address,
            slots.Length,
            matches,
            score,
            slots);
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | data[offset + 1] << 8);

    private static uint U32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static void Put16(Span<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }
}
