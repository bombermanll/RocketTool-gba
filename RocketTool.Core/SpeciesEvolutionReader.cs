namespace RocketTool.Core;

public sealed record SpeciesEvolution(
    int SourceSpecies,
    int Slot,
    ushort Method,
    ushort Parameter,
    ushort TargetSpecies,
    ushort Extra);

public sealed class SpeciesEvolutionReader
{
    public const int DefaultEvolutionTableOffset = 0x5F96D4;
    public const int DefaultEntriesPerSpecies = 10;
    public const int DefaultEntrySize = 8;

    private readonly byte[] _rom;
    private readonly int _tableOffset;
    private readonly int _entriesPerSpecies;
    private readonly int _entrySize;

    public SpeciesEvolutionReader(
        string romPath,
        int tableOffset = DefaultEvolutionTableOffset,
        int entriesPerSpecies = DefaultEntriesPerSpecies,
        int entrySize = DefaultEntrySize)
    {
        _rom = File.ReadAllBytes(romPath);
        _tableOffset = tableOffset;
        _entriesPerSpecies = entriesPerSpecies;
        _entrySize = entrySize;
    }

    public IReadOnlyList<SpeciesEvolution> Read(int species)
    {
        if (species < 0) throw new ArgumentOutOfRangeException(nameof(species));
        var start = _tableOffset + species * _entriesPerSpecies * _entrySize;
        var end = start + _entriesPerSpecies * _entrySize;
        if (start < 0 || end > _rom.Length) throw new ArgumentOutOfRangeException(nameof(species));

        var result = new List<SpeciesEvolution>();
        for (var slot = 0; slot < _entriesPerSpecies; slot++)
        {
            var off = start + slot * _entrySize;
            var method = U16(off);
            var param = U16(off + 2);
            var target = U16(off + 4);
            var extra = U16(off + 6);
            if (method == 0 && param == 0 && target == 0 && extra == 0)
                continue;
            result.Add(new SpeciesEvolution(species, slot, method, param, target, extra));
        }
        return result;
    }

    private ushort U16(int offset)
        => (ushort)(_rom[offset] | _rom[offset + 1] << 8);
}
