namespace RocketTool.Core;

public sealed record BagSlot(uint Address, ushort ItemId, ushort Quantity, int Score, string Note);
public sealed record BagRun(uint StartAddress, int SlotCount, int NonEmptyCount, int Score, IReadOnlyList<BagSlot> Slots);
public sealed record BagPocketDefinition(int Pocket, string Name, uint Offset, int SlotCount, bool QuantityXor, ushort QuantityKey);
public sealed record BagPocket(int Pocket, uint StartAddress, int SlotCount, int NonEmptyCount, int Score, IReadOnlyList<BagSlot> Slots);
public sealed record BagSlotChange(uint Address, ushort BeforeItemId, ushort BeforeQuantity, ushort AfterItemId, ushort AfterQuantity, int Score, string Note);
public sealed record BagAddTarget(uint Address, ushort ItemId, ushort BeforeQuantity, ushort AfterQuantity, ushort QuantityKey, bool IsExistingItem, string Note);

public static class BagScanner
{
    public const int MaxRunSlots = 254;

    public static uint LocateSaveBlockBase(MgbaBridgeClient bridge, GameProfile profile, IReadOnlyList<BagPocketDefinition> definitions)
    {
        var pointerAddress = profile.Memory.SaveBlock1PointerAddress;
        var bytes = bridge.Read(pointerAddress, 4);
        var address = U32(bytes, 0);
        var requiredLength = definitions.Count == 0
            ? 4U
            : definitions.Max(definition => definition.Offset + checked((uint)definition.SlotCount * 4U));
        var ewramEnd = profile.Memory.EwramBase + checked((uint)profile.Memory.EwramSize);
        if (address < profile.Memory.EwramBase || address + requiredLength > ewramEnd)
        {
            throw new InvalidOperationException(
                $"没有定位到 {profile.DisplayName} 的有效背包基址：0x{pointerAddress:X8} -> 0x{address:X8}。已拒绝使用其他版本的范围。");
        }
        return address;
    }

    public static IReadOnlyList<BagPocket> FindLivePockets(
        ReadOnlySpan<byte> ewram,
        uint ewramBase,
        uint? saveBlockBase,
        IReadOnlyList<int> pocketIds,
        Func<int, int> itemPocket,
        Func<int, bool> isKeyItemPocket,
        Func<int, int> pocketCapacity,
        int maxItemId,
        int maxQuantity)
    {
        var quantityKey = InferQuantityKey(ewram);
        var (startOffset, endOffset) = SearchRange(ewram, ewramBase, saveBlockBase);
        var pockets = new List<BagPocket>();
        foreach (var pocket in pocketIds.Distinct().Order())
        {
            var capacity = Math.Clamp(pocketCapacity(pocket), 1, MaxRunSlots);
            var best = FindBestLivePocket(
                ewram, ewramBase, startOffset, endOffset, pocket, capacity, quantityKey,
                itemPocket, isKeyItemPocket, maxItemId, maxQuantity);
            if (best is not null) pockets.Add(best);
        }
        return pockets;
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
        return counts.Count == 0 ? (ushort)0 : counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
    }

    public static BagAddTarget FindAddTarget(
        ReadOnlySpan<byte> ewram,
        uint ewramBase,
        uint? saveBlockBase,
        IReadOnlyList<BagPocketDefinition> definitions,
        int requestedPocket,
        ushort itemId,
        ushort addQuantity,
        Func<int, int> itemPocket,
        Func<int, bool> isKeyItemPocket,
        Func<int, int> pocketCapacity,
        int maxItemId,
        int maxQuantity)
    {
        if (itemId == 0) throw new InvalidOperationException("不能添加空道具。");
        if (addQuantity == 0) throw new InvalidOperationException("添加数量必须大于 0。");
        var pocket = itemPocket(itemId);
        if (pocket <= 0) throw new InvalidOperationException($"当前 Profile 无法判断道具 {itemId} 所属口袋。");
        if (requestedPocket > 0 && requestedPocket != pocket)
            throw new InvalidOperationException($"该道具属于口袋 {pocket}，不能添加到当前口袋。");

        var quantityKey = InferQuantityKey(ewram);
        var (searchStart, searchEnd) = SearchRange(ewram, ewramBase, saveBlockBase);
        var definition = DefinitionForPocket(definitions, pocket);
        var pocketStart = searchStart;
        var pocketEnd = searchEnd;
        if (saveBlockBase is not null && definition is not null)
        {
            pocketStart = checked((int)(saveBlockBase.Value + definition.Offset - ewramBase));
            pocketEnd = checked(pocketStart + definition.SlotCount * 4);
            if (pocketStart < 0 || pocketEnd > ewram.Length)
                throw new InvalidOperationException("当前 Profile 的背包口袋范围超出 EWRAM，已拒绝写入。");
        }

        for (var off = pocketStart; off <= pocketEnd - 4; off += 4)
        {
            var slot = ParseLiveSlot(ewram, off, ewramBase + (uint)off, quantityKey, itemPocket, isKeyItemPocket, maxItemId, maxQuantity);
            if (slot is null || slot.ItemId != itemId || itemPocket(slot.ItemId) != pocket) continue;
            var quantity = checked((int)slot.Quantity + addQuantity);
            if (quantity > maxQuantity) throw new InvalidOperationException($"已有 {slot.Quantity} 个，追加后会超过上限 {maxQuantity}。");
            return new BagAddTarget(slot.Address, itemId, slot.Quantity, (ushort)quantity, quantityKey, true, "已有同道具，改为累加数量");
        }

        var empty = FindEmptySlotOffset(ewram, pocketStart, pocketEnd, quantityKey);
        if (empty is not null)
            return new BagAddTarget(ewramBase + (uint)empty.Value, itemId, 0, addQuantity, quantityKey, false, "写入当前 Profile 口袋空槽");

        var detected = FindLivePockets(
                ewram, ewramBase, saveBlockBase, [pocket], itemPocket, isKeyItemPocket,
                pocketCapacity, maxItemId, maxQuantity)
            .FirstOrDefault();
        if (detected is null) throw new InvalidOperationException("当前口袋为空或尚未定位；请先在游戏内获得该口袋的一个道具后刷新。");
        throw new InvalidOperationException("当前 Profile 的该口袋没有可安全追加的空槽。");
    }

    public static BagPocketDefinition? DefinitionForPocket(IReadOnlyList<BagPocketDefinition> definitions, int pocket)
        => definitions.FirstOrDefault(definition => definition.Pocket == pocket);

    public static BagPocketDefinition? DefinitionForAddress(IReadOnlyList<BagPocketDefinition> definitions, uint saveBlockBase, uint address)
        => definitions.FirstOrDefault(definition =>
            address >= saveBlockBase + definition.Offset &&
            address < saveBlockBase + definition.Offset + checked((uint)definition.SlotCount * 4U) &&
            ((address - saveBlockBase - definition.Offset) & 3U) == 0);

    public static BagSlot ReadSlot(MgbaBridgeClient bridge, uint address, int maxItemId, int maxQuantity)
    {
        var bytes = bridge.Read(address, 4);
        var item = U16(bytes, 0);
        var quantity = U16(bytes, 2);
        var valid = item == 0 && quantity == 0 || item is > 0 && item <= maxItemId && quantity is > 0 && quantity <= maxQuantity;
        return new BagSlot(address, item, quantity, valid ? 10 : 0, valid ? "按当前 Profile 上限解析" : "不符合当前 Profile 的普通背包槽范围");
    }

    public static BagSlot ReadSlot(MgbaBridgeClient bridge, uint address, BagPocketDefinition definition, int maxQuantity)
    {
        var bytes = bridge.Read(address, 4);
        var item = U16(bytes, 0);
        var rawQuantity = U16(bytes, 2);
        var quantity = definition.QuantityXor ? (ushort)(rawQuantity ^ definition.QuantityKey) : rawQuantity;
        return new BagSlot(address, item, quantity, quantity <= maxQuantity ? 10 : 0, $"按 Profile 口袋 {definition.Name} 解析");
    }

    public static byte[] EncodeSlot(ushort itemId, ushort quantity, ushort quantityKey = 0, bool quantityXor = false)
    {
        var bytes = new byte[4];
        Put16(bytes, 0, itemId);
        Put16(bytes, 2, itemId != 0 && quantityXor ? (ushort)(quantity ^ quantityKey) : quantity);
        return bytes;
    }

    private static BagPocket? FindBestLivePocket(
        ReadOnlySpan<byte> ewram,
        uint ewramBase,
        int startOffset,
        int endOffset,
        int pocket,
        int capacity,
        ushort quantityKey,
        Func<int, int> itemPocket,
        Func<int, bool> isKeyItemPocket,
        int maxItemId,
        int maxQuantity)
    {
        BagPocket? best = null;
        for (var off = startOffset; off <= endOffset - 4; off += 4)
        {
            var first = ParseLiveSlot(ewram, off, ewramBase + (uint)off, quantityKey, itemPocket, isKeyItemPocket, maxItemId, maxQuantity);
            if (first is null || first.ItemId == 0 || itemPocket(first.ItemId) != pocket) continue;
            var slots = new List<BagSlot> { first };
            var score = 30 + first.Score;
            for (var i = 1; i < capacity && off + i * 4 + 4 <= endOffset; i++)
            {
                var slotOffset = off + i * 4;
                var slot = ParseLiveSlot(ewram, slotOffset, ewramBase + (uint)slotOffset, quantityKey, itemPocket, isKeyItemPocket, maxItemId, maxQuantity);
                if (slot is null || slot.ItemId == 0 || itemPocket(slot.ItemId) != pocket) break;
                slots.Add(slot);
                score += 30 + slot.Score;
            }
            var candidate = new BagPocket(pocket, slots[0].Address, slots.Count, slots.Count, score, slots);
            if (best is null || candidate.Score > best.Score) best = candidate;
        }
        return best;
    }

    private static BagSlot? ParseLiveSlot(
        ReadOnlySpan<byte> ewram,
        int offset,
        uint address,
        ushort quantityKey,
        Func<int, int> itemPocket,
        Func<int, bool> isKeyItemPocket,
        int maxItemId,
        int maxQuantity)
    {
        var item = U16(ewram, offset);
        var rawQuantity = U16(ewram, offset + 2);
        if (item == 0) return rawQuantity == 0 || rawQuantity == quantityKey ? new BagSlot(address, 0, 0, 1, "空槽") : null;
        if (item > maxItemId) return null;
        var pocket = itemPocket(item);
        if (pocket <= 0) return null;
        if (isKeyItemPocket(pocket) && rawQuantity == 0) return new BagSlot(address, item, 1, 18, "Profile 标记的重要物品槽");
        var quantity = rawQuantity <= maxQuantity ? rawQuantity : (ushort)(rawQuantity ^ quantityKey);
        if (quantity is 0 || quantity > maxQuantity) return null;
        return new BagSlot(address, item, quantity, 16, $"自动定位；数量密钥 0x{quantityKey:X4}");
    }

    private static (int Start, int End) SearchRange(ReadOnlySpan<byte> ewram, uint ewramBase, uint? saveBlockBase)
    {
        if (saveBlockBase is null || saveBlockBase < ewramBase || saveBlockBase >= ewramBase + ewram.Length)
            return (0, ewram.Length);
        var start = checked((int)(saveBlockBase.Value - ewramBase));
        return (start, Math.Min(ewram.Length, start + 0x4000));
    }

    private static int? FindEmptySlotOffset(ReadOnlySpan<byte> ewram, int startOffset, int endOffset, ushort quantityKey)
    {
        for (var off = Math.Max(0, startOffset); off <= Math.Min(ewram.Length, endOffset) - 4; off += 4)
            if (U16(ewram, off) == 0 && U16(ewram, off + 2) is var raw && (raw == 0 || raw == quantityKey))
                return off;
        return null;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => (ushort)(data[offset] | data[offset + 1] << 8);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
    private static void Put16(Span<byte> data, int offset, ushort value) { data[offset] = (byte)value; data[offset + 1] = (byte)(value >> 8); }
}
