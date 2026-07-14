using System.Text;

namespace RocketTool.Core;

public sealed record PartyCandidate(uint Address, int Score, bool ChecksumOk, ushort Species, byte Level, ushort Hp, ushort MaxHp, uint Pid);

public sealed record PartyRun(uint StartAddress, int Length, int ScoreSum, IReadOnlyList<PartyCandidate> Candidates, int? PartyCount = null);

public static class PartyScanner
{
    public const int ChunkSize = 4096;

    public static byte[] ReadEwram(MgbaBridgeClient bridge, GameProfile profile, Action<int, int>? progress = null)
    {
        var ewram = new byte[profile.Memory.EwramSize];
        for (var off = 0; off < ewram.Length; off += ChunkSize)
        {
            var len = Math.Min(ChunkSize, ewram.Length - off);
            bridge.Read(profile.Memory.EwramBase + (uint)off, len).CopyTo(ewram.AsSpan(off, len));
            progress?.Invoke(off, ewram.Length);
        }
        return ewram;
    }

    public static IReadOnlyList<PartyCandidate> FindCandidates(
        ReadOnlySpan<byte> ewram,
        uint ewramBase,
        PokemonDataLayout layout,
        int minScore,
        int maxSpecies)
    {
        var hits = new List<PartyCandidate>();
        for (var off = 0; off <= ewram.Length - Gen3Constants.PartyMonSize; off += 4)
        {
            var candidate = ScoreMon(ewramBase + (uint)off, ewram.Slice(off, Gen3Constants.PartyMonSize), maxSpecies, layout);
            if (candidate.Score >= minScore && IsStrongPartyCandidate(candidate, maxSpecies)) hits.Add(candidate);
        }
        return hits.OrderByDescending(c => c.Score).ThenBy(c => c.Address).ToArray();
    }

    public static IReadOnlyList<PartyRun> GroupRuns(IEnumerable<PartyCandidate> candidates, bool checksumRequired = false)
    {
        var filtered = candidates.Where(c => !checksumRequired || c.ChecksumOk).ToDictionary(c => c.Address, c => c);
        var used = new HashSet<uint>();
        var runs = new List<PartyRun>();
        foreach (var start in filtered.Keys.OrderBy(x => x))
        {
            if (used.Contains(start)) continue;
            var current = start;
            var run = new List<PartyCandidate>();
            while (filtered.TryGetValue(current, out var candidate))
            {
                run.Add(candidate);
                used.Add(current);
                current += Gen3Constants.PartyMonSize;
            }
            if (run.Count > 0) runs.Add(new PartyRun(start, run.Count, run.Sum(c => c.Score), run));
        }
        return runs.OrderByDescending(r => Math.Min(r.Length, Gen3Constants.PartySlots))
            .ThenByDescending(r => r.ScoreSum)
            .ThenBy(r => r.StartAddress)
            .ToArray();
    }

    public static PartyRun? LocateParty(
        byte[] ewram,
        uint ewramBase,
        PokemonDataLayout layout,
        uint preferredBase,
        int partyCountOffsetFromBase,
        int maxSpecies,
        int minScore = 13)
    {
        var knownBase = TryBuildPartyRunAtBase(ewram, ewramBase, preferredBase, partyCountOffsetFromBase, maxSpecies, layout, minScore);
        if (knownBase is not null) return knownBase;

        var candidates = FindCandidates(ewram, ewramBase, layout, minScore, maxSpecies);
        var strict = GroupRuns(candidates, checksumRequired: true)
            .Where(IsUsablePartyRun)
            .OrderByDescending(r => r.StartAddress == preferredBase)
            .ThenByDescending(r => Math.Min(r.Length, Gen3Constants.PartySlots))
            .ThenByDescending(r => r.ScoreSum)
            .ThenBy(r => r.StartAddress)
            .FirstOrDefault();
        if (strict is not null) return strict;
        return GroupRuns(candidates)
            .Where(IsUsablePartyRun)
            .OrderByDescending(r => r.StartAddress == preferredBase)
            .ThenByDescending(r => Math.Min(r.Length, Gen3Constants.PartySlots))
            .ThenByDescending(r => r.ScoreSum)
            .ThenBy(r => r.StartAddress)
            .FirstOrDefault();
    }

    public static int? TryReadPartyCount(ReadOnlySpan<byte> ewram, uint ewramBase, uint partyBase, int partyCountOffsetFromBase)
    {
        var countAddress = (long)partyBase + partyCountOffsetFromBase;
        var offset = countAddress - ewramBase;
        if (offset < 0 || offset >= ewram.Length) return null;

        var count = ewram[(int)offset];
        return count is >= 1 and <= Gen3Constants.PartySlots ? count : null;
    }

    public static PartyRun? TryBuildPartyRunAtBase(
        byte[] ewram,
        uint ewramBase,
        uint partyBase,
        int partyCountOffsetFromBase,
        int maxSpecies,
        PokemonDataLayout layout,
        int minScore = 13)
    {
        var count = TryReadPartyCount(ewram, ewramBase, partyBase, partyCountOffsetFromBase);
        if (count is null) return null;

        if (partyBase < ewramBase) return null;
        var startOffset = checked((int)(partyBase - ewramBase));
        if (startOffset < 0 || startOffset + count.Value * Gen3Constants.PartyMonSize > ewram.Length) return null;

        var candidates = new List<PartyCandidate>();
        for (var slot = 0; slot < count.Value; slot++)
        {
            var offset = startOffset + slot * Gen3Constants.PartyMonSize;
            var candidate = ScoreMon(partyBase + (uint)(slot * Gen3Constants.PartyMonSize), ewram.AsSpan(offset, Gen3Constants.PartyMonSize), maxSpecies, layout);
            if (!candidate.ChecksumOk || candidate.Score < minScore || !IsStrongPartyCandidate(candidate, maxSpecies)) return null;
            candidates.Add(candidate);
        }

        return new PartyRun(partyBase, count.Value, candidates.Sum(c => c.Score), candidates, count);
    }

    private static bool IsUsablePartyRun(PartyRun run)
        => run.Length >= 2;

    private static bool IsStrongPartyCandidate(PartyCandidate candidate, int maxSpecies)
        => candidate.ChecksumOk
           && candidate.Species >= 1 && candidate.Species <= maxSpecies
           && candidate.Level is >= 1
           && candidate.MaxHp is >= 1 and <= 999
           && candidate.Hp <= candidate.MaxHp;

    private static PartyCandidate ScoreMon(uint address, ReadOnlySpan<byte> mon, int maxSpecies, PokemonDataLayout layout)
    {
        if (mon.Length < Gen3Constants.PartyMonSize || IsZero(mon)) return new PartyCandidate(address, 0, false, 0, 0, 0, 0, 0);
        var pokemon = new PartyPokemon(mon, layout);
        if (pokemon.IsEmpty) return new PartyCandidate(address, 0, false, 0, 0, 0, 0, pokemon.Pid);
        var info = pokemon.GetInfo();
        var language = mon[0x12];
        var score = 0;
        var checksumOk = info.Checksum == info.CalculatedChecksum;
        if (checksumOk) score += 6;
        if (1 <= info.Species && info.Species <= maxSpecies) score += 5;
        if (info.Level >= 1) score += 3;
        if (1 <= info.MaxHp && info.MaxHp <= 999 && info.Hp <= info.MaxHp) score += 3;
        if (language is >= 1 and <= 7 or 0x12) score += 1;
        if (HasNickname(mon.Slice(0x08, 0x0A))) score += 1;
        if (info.Exp < 2_000_000) score += 1;
        return new PartyCandidate(address, score, checksumOk, info.Species, info.Level, info.Hp, info.MaxHp, pokemon.Pid);
    }

    private static bool IsZero(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) if (b != 0) return false;
        return true;
    }

    private static bool HasNickname(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b == 0xFF) break;
            if (b != 0) return true;
        }
        return false;
    }

}
