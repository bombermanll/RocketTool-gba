using System.Text;

namespace RocketTool.Core;

public sealed record PartyCandidate(uint Address, int Score, bool ChecksumOk, ushort Species, byte Level, ushort Hp, ushort MaxHp, uint Pid);

public sealed record PartyRun(uint StartAddress, int Length, int ScoreSum, IReadOnlyList<PartyCandidate> Candidates, int? PartyCount = null);

public static class PartyScanner
{
    public const uint EwramBase = 0x02000000;
    public const int EwramSize = 0x40000;
    public const int ChunkSize = 4096;
    public const int MaxSpecies = 1394;

    public static byte[] ReadEwram(MgbaBridgeClient bridge, Action<int, int>? progress = null)
    {
        var ewram = new byte[EwramSize];
        for (var off = 0; off < EwramSize; off += ChunkSize)
        {
            var len = Math.Min(ChunkSize, EwramSize - off);
            bridge.Read(EwramBase + (uint)off, len).CopyTo(ewram.AsSpan(off, len));
            progress?.Invoke(off, EwramSize);
        }
        return ewram;
    }

    public static IReadOnlyList<PartyCandidate> FindCandidates(ReadOnlySpan<byte> ewram, int minScore = 13)
    {
        var hits = new List<PartyCandidate>();
        for (var off = 0; off <= ewram.Length - Gen3Constants.PartyMonSize; off += 4)
        {
            var candidate = ScoreMon(EwramBase + (uint)off, ewram.Slice(off, Gen3Constants.PartyMonSize));
            if (candidate.Score >= minScore && IsStrongPartyCandidate(candidate)) hits.Add(candidate);
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

    public static PartyRun? LocateParty(byte[] ewram, int minScore = 13)
    {
        var knownBase = TryLocatePartyAtKnownBase(ewram, minScore);
        if (knownBase is not null) return knownBase;

        var candidates = FindCandidates(ewram, minScore);
        var strict = GroupRuns(candidates, checksumRequired: true)
            .Where(IsUsablePartyRun)
            .OrderByDescending(r => r.StartAddress == Gen3Constants.DefaultPartyBase)
            .ThenByDescending(r => Math.Min(r.Length, Gen3Constants.PartySlots))
            .ThenByDescending(r => r.ScoreSum)
            .ThenBy(r => r.StartAddress)
            .FirstOrDefault();
        if (strict is not null) return strict;
        return GroupRuns(candidates)
            .Where(IsUsablePartyRun)
            .OrderByDescending(r => r.StartAddress == Gen3Constants.DefaultPartyBase)
            .ThenByDescending(r => Math.Min(r.Length, Gen3Constants.PartySlots))
            .ThenByDescending(r => r.ScoreSum)
            .ThenBy(r => r.StartAddress)
            .FirstOrDefault();
    }

    public static int? TryReadPartyCount(ReadOnlySpan<byte> ewram, uint partyBase)
    {
        var countAddress = (long)partyBase + Gen3Constants.PartyCountOffsetFromPartyBase;
        var offset = countAddress - EwramBase;
        if (offset < 0 || offset >= ewram.Length) return null;

        var count = ewram[(int)offset];
        return count is >= 1 and <= Gen3Constants.PartySlots ? count : null;
    }

    public static PartyRun? TryLocatePartyAtKnownBase(byte[] ewram, int minScore = 13)
        => TryBuildPartyRunAtBase(ewram, Gen3Constants.DefaultPartyBase, minScore);

    public static PartyRun? TryBuildPartyRunAtBase(byte[] ewram, uint partyBase, int minScore = 13)
    {
        var count = TryReadPartyCount(ewram, partyBase);
        if (count is null) return null;

        if (partyBase < EwramBase) return null;
        var startOffset = checked((int)(partyBase - EwramBase));
        if (startOffset < 0 || startOffset + count.Value * Gen3Constants.PartyMonSize > ewram.Length) return null;

        var candidates = new List<PartyCandidate>();
        for (var slot = 0; slot < count.Value; slot++)
        {
            var offset = startOffset + slot * Gen3Constants.PartyMonSize;
            var candidate = ScoreMon(partyBase + (uint)(slot * Gen3Constants.PartyMonSize), ewram.AsSpan(offset, Gen3Constants.PartyMonSize));
            if (!candidate.ChecksumOk || candidate.Score < minScore || !IsStrongPartyCandidate(candidate)) return null;
            candidates.Add(candidate);
        }

        return new PartyRun(partyBase, count.Value, candidates.Sum(c => c.Score), candidates, count);
    }

    private static bool IsUsablePartyRun(PartyRun run)
        => run.Length >= 2;

    private static bool IsStrongPartyCandidate(PartyCandidate candidate)
        => candidate.ChecksumOk
           && candidate.Species is >= 1 and <= MaxSpecies
           && candidate.Level is >= 1
           && candidate.MaxHp is >= 1 and <= 999
           && candidate.Hp <= candidate.MaxHp;

    private static PartyCandidate ScoreMon(uint address, ReadOnlySpan<byte> mon)
    {
        if (mon.Length < Gen3Constants.PartyMonSize || IsZero(mon)) return new PartyCandidate(address, 0, false, 0, 0, 0, 0, 0);
        var pokemon = new PartyPokemon(mon);
        if (pokemon.IsEmpty) return new PartyCandidate(address, 0, false, 0, 0, 0, 0, pokemon.Pid);
        var info = pokemon.GetInfo();
        var language = mon[0x12];
        var score = 0;
        var checksumOk = info.Checksum == info.CalculatedChecksum;
        if (checksumOk) score += 6;
        if (1 <= info.Species && info.Species <= MaxSpecies) score += 5;
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
