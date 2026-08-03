namespace RocketTool.Core;

public sealed record SpeciesStats(
    int Species,
    byte Hp,
    byte Attack,
    byte Defense,
    byte Speed,
    byte SpAttack,
    byte SpDefense,
    byte Type1,
    byte Type2,
    ushort CatchRate,
    ushort ExpYield,
    ushort EvYield,
    ushort Item1,
    ushort Item2,
    byte GenderRatio,
    byte EggCycles,
    byte Friendship,
    byte GrowthRate,
    byte EggGroup1,
    byte EggGroup2,
    ushort Ability1,
    ushort Ability2,
    ushort? Ability3)
{
    public int Bst => Hp + Attack + Defense + Speed + SpAttack + SpDefense;
}

public sealed class SpeciesStatsReader
{
    private readonly byte[] _rom;
    private readonly int _tableOffset;
    private readonly int _entrySize;
    private readonly GameProfileRomTable? _abilityTable;

    public SpeciesStatsReader(string romPath, int tableOffset, int entrySize)
    {
        _rom = File.ReadAllBytes(romPath);
        _tableOffset = tableOffset;
        _entrySize = entrySize;
    }

    public SpeciesStatsReader(string romPath, GameProfile profile)
        : this(romPath, profile.RomTables.BaseStats.Offset, profile.RomTables.BaseStats.EntrySize)
    {
        _abilityTable = profile.RomTables.SpeciesAbilities;
    }

    public SpeciesStats Read(int species)
    {
        var off = _tableOffset + species * _entrySize;
        if (off < 0 || off + _entrySize > _rom.Length) throw new ArgumentOutOfRangeException(nameof(species));
        var e = _rom.AsSpan(off, _entrySize);
        var abilities = ReadAbilities(species, e);
        if (_entrySize == 36)
        {
            return new SpeciesStats(
                species, e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7],
                U16(e, 8), U16(e, 10), U16(e, 12), U16(e, 14), U16(e, 16),
                e[18], e[19], e[20], e[21], e[22], e[23], abilities.Ability1, abilities.Ability2, abilities.Ability3);
        }
        return new SpeciesStats(
            species, e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7],
            e[8], e[9], U16(e, 10), U16(e, 12), U16(e, 14),
            e[16], e[17], e[18], e[19], e[20], e[21],
            abilities.Ability1, abilities.Ability2, abilities.Ability3);
    }

    private (ushort Ability1, ushort Ability2, ushort? Ability3) ReadAbilities(int species, ReadOnlySpan<byte> baseStats)
    {
        if (_abilityTable is { } table)
        {
            var offset = table.Offset + checked(species * table.EntrySize);
            if (table.EntrySize < 6 || offset < 0 || offset + 6 > _rom.Length)
                throw new InvalidDataException($"物种 {species} 的独立特性表记录无效。");
            var row = _rom.AsSpan(offset, table.EntrySize);
            var third = U16(row, 4);
            return (U16(row, 0), U16(row, 2), third == 0 ? null : third);
        }

        if (_entrySize == 36)
            return (U16(baseStats, 24), U16(baseStats, 26), U16(baseStats, 28));
        return (baseStats[22], baseStats[23], _entrySize >= 27 ? baseStats[26] : null);
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | data[offset + 1] << 8);
}
