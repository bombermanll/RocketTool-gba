namespace RocketTool.Core;

public sealed record SpeciesLevelMove(int Species, ushort Level, ushort Move);

public sealed class SpeciesLevelMoveReader
{
    public const int DefaultPointerTableOffset = 0x614AC4;
    public const int DefaultMaxEntriesPerSpecies = 128;
    private const uint GbaRomBase = 0x08000000;

    private readonly byte[] _rom;
    private readonly int _pointerTableOffset;
    private readonly int _maxEntriesPerSpecies;

    public SpeciesLevelMoveReader(
        string romPath,
        int pointerTableOffset = DefaultPointerTableOffset,
        int maxEntriesPerSpecies = DefaultMaxEntriesPerSpecies)
    {
        _rom = File.ReadAllBytes(romPath);
        _pointerTableOffset = pointerTableOffset;
        _maxEntriesPerSpecies = maxEntriesPerSpecies;
    }

    public SpeciesLevelMoveReader(string romPath, GameProfile profile, int maxEntriesPerSpecies = DefaultMaxEntriesPerSpecies)
        : this(romPath, profile.RomTables.LevelMoves.Offset, maxEntriesPerSpecies)
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
            var entryOff = off + i * 4;
            if (entryOff < 0 || entryOff + 4 > _rom.Length)
                throw new InvalidDataException($"Level-up move table for species {species} is not terminated.");

            var move = U16(entryOff);
            var level = U16(entryOff + 2);
            if (move == 0xFFFF)
                return result;
            result.Add(new SpeciesLevelMove(species, level, move));
        }

        throw new InvalidDataException($"Level-up move table for species {species} exceeded {_maxEntriesPerSpecies} entries.");
    }

    private ushort U16(int offset)
        => (ushort)(_rom[offset] | _rom[offset + 1] << 8);

    private uint U32(int offset)
        => (uint)(_rom[offset] | _rom[offset + 1] << 8 | _rom[offset + 2] << 16 | _rom[offset + 3] << 24);
}
