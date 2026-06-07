namespace RocketTool.Core;

public sealed record ItemData(
    int Item,
    ushort ItemId,
    ushort Price,
    byte HoldEffect,
    byte HoldEffectParam,
    uint DescriptionPointer,
    byte Importance,
    byte ExitsBagOnUse,
    byte Pocket,
    byte Type,
    uint FieldUseFunction,
    byte BattleUsage,
    uint BattleUseFunction,
    uint SecondaryId,
    byte[] RawName,
    byte[] Raw);

public sealed class ItemDataReader
{
    public const int DefaultItemTableOffset = 0xC3D558;
    public const int DefaultEntrySize = 44;
    private readonly byte[] _rom;
    private readonly int _tableOffset;
    private readonly int _entrySize;

    public ItemDataReader(string romPath, int tableOffset = DefaultItemTableOffset, int entrySize = DefaultEntrySize)
    {
        _rom = File.ReadAllBytes(romPath);
        _tableOffset = tableOffset;
        _entrySize = entrySize;
    }

    public ItemData Read(int item)
    {
        var off = _tableOffset + item * _entrySize;
        if (off < 0 || off + _entrySize > _rom.Length) throw new ArgumentOutOfRangeException(nameof(item));
        var e = _rom.AsSpan(off, _entrySize);
        return new ItemData(
            item,
            U16(e, 14),
            U16(e, 16),
            e[18],
            e[19],
            U32(e, 20),
            e[24],
            e[25],
            e[26],
            e[27],
            U32(e, 28),
            e[32],
            U32(e, 36),
            U32(e, 40),
            e[..14].ToArray(),
            e.ToArray());
    }

    public static string PocketName(int pocket) => pocket switch
    {
        1 => "Items",
        2 => "Medicine",
        3 => "Poke Balls",
        4 => "Berries",
        5 => "TMs/HMs",
        6 => "Battle/Hold",
        _ => $"#{pocket}"
    };

    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | data[offset + 1] << 8);

    private static uint U32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
}
