namespace RocketTool.Core;

public sealed record SpeciesLevelMove(int Species, ushort Level, ushort Move);

public sealed class SpeciesLevelMoveReader
{
    public const int DefaultPointerTableOffset = 0x614AC4;
    public const int DefaultMaxEntriesPerSpecies = 128;
    public const int DefaultEntrySize = 4;
    private const uint GbaRomBase = 0x08000000;

    private readonly byte[] _rom;
    private readonly int _pointerTableOffset;
    private readonly int _maxEntriesPerSpecies;
    private readonly int _entrySize;

    public SpeciesLevelMoveReader(
        string romPath,
        int pointerTableOffset = DefaultPointerTableOffset,
        int maxEntriesPerSpecies = DefaultMaxEntriesPerSpecies,
        int entrySize = DefaultEntrySize)
    {
        _rom = File.ReadAllBytes(romPath);
        _pointerTableOffset = pointerTableOffset;
        _maxEntriesPerSpecies = maxEntriesPerSpecies;
        _entrySize = entrySize;
    }

    public SpeciesLevelMoveReader(string romPath, GameProfile profile, int maxEntriesPerSpecies = DefaultMaxEntriesPerSpecies)
        : this(romPath, profile.RomTables.LevelMoves.Offset, maxEntriesPerSpecies, profile.RomTables.LevelMoves.EntrySize)
    {
    }

    public IReadOnlyList<SpeciesLevelMove> Read(int species)
    {
        if (species < 0) throw new ArgumentOutOfRangeException(nameof(species));
        var ptrOff = _pointerTableOffset + species * 4;
        if (ptrOff < 0 || ptrOff + 4 > _rom.Length) throw new ArgumentOutOfRangeException(nameof(species));

        var pointer = U32(ptrOff);
        if (pointer < GbaRomBase || pointer >= GbaRomBase + _rom.Length)
            throw new InvalidDataException($"Invalid level-up move pointer 0x{pointer:X8} for species {species}.");

        var off = checked((int)(pointer - GbaRomBase));
        var result = new List<SpeciesLevelMove>();
        for (var i = 0; i < _maxEntriesPerSpecies; i++)
        {
            var entryOff = off + i * _entrySize;
            if (entryOff < 0 || entryOff + _entrySize > _rom.Length)
                throw new InvalidDataException($"Level-up move table for species {species} is not terminated.");

            if (_entrySize == 2)
            {
                var packed = U16(entryOff);
                if (packed == 0xFFFF)
                    return result;
                result.Add(new SpeciesLevelMove(species, (ushort)(packed >> 9), (ushort)(packed & 0x01FF)));
            }
            else
            {
                var move = U16(entryOff);
                var level = U16(entryOff + 2);
                if (move == 0xFFFF)
                    return result;
                result.Add(new SpeciesLevelMove(species, level, move));
            }
        }

        throw new InvalidDataException($"Level-up move table for species {species} exceeded {_maxEntriesPerSpecies} entries.");
    }

    private ushort U16(int offset)
        => (ushort)(_rom[offset] | _rom[offset + 1] << 8);

    private uint U32(int offset)
        => (uint)(_rom[offset] | _rom[offset + 1] << 8 | _rom[offset + 2] << 16 | _rom[offset + 3] << 24);
}
