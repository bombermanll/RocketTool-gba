namespace RocketTool.Core;

public sealed record MoveData(
    int Move,
    ushort Power,
    byte Type,
    byte Accuracy,
    byte Pp,
    byte SecondaryEffectChance,
    ushort Target,
    sbyte Priority,
    ushort Flags,
    byte Category,
    ushort ZMovePower,
    byte[] Raw);

public sealed class MoveDataReader
{
    public const int DefaultMoveTableOffset = 0x5ACD5E;
    public const int DefaultEntrySize = 20;
    private readonly byte[] _rom;
    private readonly int _tableOffset;
    private readonly int _entrySize;

    public MoveDataReader(string romPath, int tableOffset = DefaultMoveTableOffset, int entrySize = DefaultEntrySize)
    {
        _rom = File.ReadAllBytes(romPath);
        _tableOffset = tableOffset;
        _entrySize = entrySize;
    }

    public MoveDataReader(string romPath, GameProfile profile)
        : this(romPath, profile.RomTables.Moves.Offset, profile.RomTables.Moves.EntrySize)
    {
    }

    public MoveData Read(int move)
    {
        var off = _tableOffset + move * _entrySize;
        if (off < 0 || off + _entrySize > _rom.Length) throw new ArgumentOutOfRangeException(nameof(move));
        var e = _rom.AsSpan(off, _entrySize);
        return new MoveData(
            move,
            U16(e, 0),
            e[2],
            e[3],
            e[4],
            e[5],
            U16(e, 6),
            unchecked((sbyte)e[8]),
            U16(e, 10),
            e[14],
            U16(e, 16),
            e.ToArray());
    }

    public static string TypeName(int type) => type switch
    {
        0 => "Normal", 1 => "Fighting", 2 => "Flying", 3 => "Poison", 4 => "Ground", 5 => "Rock", 6 => "Bug", 7 => "Ghost",
        8 => "Steel", 10 => "Fire", 11 => "Water", 12 => "Grass", 13 => "Electric", 14 => "Psychic", 15 => "Ice", 16 => "Dragon", 17 => "Dark",
        _ => $"#{type}"
    };

    public static string CategoryName(int category) => category switch
    {
        0 => "Physical",
        1 => "Special",
        2 => "Status",
        _ => $"#{category}"
    };

    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | data[offset + 1] << 8);
}
