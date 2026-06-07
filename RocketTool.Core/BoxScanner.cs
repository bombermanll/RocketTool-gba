namespace RocketTool.Core;

public sealed record BoxCandidate(uint Address, int Score, bool ChecksumOk, ushort Species, uint Pid);

public sealed record BoxRun(uint StartAddress, int Length, int ScoreSum, IReadOnlyList<BoxCandidate> Candidates);

public static class BoxScanner
{
    public const int BoxSlots = 30;
    public const int MaxBoxes = 14;

    public static IReadOnlyList<BoxCandidate> FindCandidates(ReadOnlySpan<byte> ewram, int minScore = 12)
    {
        var hits = new List<BoxCandidate>();
        for (var off = 0; off <= ewram.Length - BoxPokemon.Size; off += 4)
        {
            var candidate = ScoreBoxMon(PartyScanner.EwramBase + (uint)off, ewram.Slice(off, BoxPokemon.Size));
            if (candidate.Score >= minScore)
                hits.Add(candidate);
        }

        return hits.OrderBy(c => c.Address).ToArray();
    }

    public static IReadOnlyList<BoxRun> GroupRuns(IEnumerable<BoxCandidate> candidates, bool checksumRequired = true)
    {
        var filtered = candidates.Where(c => !checksumRequired || c.ChecksumOk).ToDictionary(c => c.Address, c => c);
        var used = new HashSet<uint>();
        var runs = new List<BoxRun>();
        foreach (var start in filtered.Keys.OrderBy(x => x))
        {
            if (used.Contains(start)) continue;
            var current = start;
            var run = new List<BoxCandidate>();
            while (filtered.TryGetValue(current, out var candidate))
            {
                run.Add(candidate);
                used.Add(current);
                current += BoxPokemon.Size;
            }

            if (run.Count > 0)
                runs.Add(new BoxRun(start, run.Count, run.Sum(c => c.Score), run));
        }

        return runs
            .OrderByDescending(r => r.Length)
            .ThenByDescending(r => r.ScoreSum)
            .ThenBy(r => r.StartAddress)
            .ToArray();
    }

    public static BoxRun? LocateBestRun(ReadOnlySpan<byte> ewram)
    {
        var candidates = FindCandidates(ewram);
        return GroupRuns(candidates).FirstOrDefault(r => r.Length >= 2)
               ?? GroupRuns(candidates, checksumRequired: false).FirstOrDefault(r => r.Length >= 2)
               ?? GroupRuns(candidates).FirstOrDefault();
    }

    private static BoxCandidate ScoreBoxMon(uint address, ReadOnlySpan<byte> mon)
    {
        if (mon.Length < BoxPokemon.Size || IsZero(mon)) return new BoxCandidate(address, 0, false, 0, 0);
        var pid = U32(mon, 0);
        var otid = U32(mon, 4);
        if (pid == 0 || otid == 0) return new BoxCandidate(address, 0, false, 0, pid);
        var storedChecksum = U16(mon, 0x1C);
        var decrypted = Decrypt(mon, pid ^ otid);
        var calculatedChecksum = PartyPokemon.ChecksumDecrypted(decrypted);
        var growth = Subblock(pid, decrypted, 0);
        var species = U16(growth, 0);
        var exp = U32(growth, 4);
        var language = mon[0x12];

        var score = 0;
        var checksumOk = storedChecksum == calculatedChecksum;
        if (checksumOk) score += 6;
        if (species is >= 1 and <= PartyScanner.MaxSpecies) score += 5;
        if (language is >= 1 and <= 7) score += 1;
        if (HasNickname(mon.Slice(0x08, 0x0A))) score += 1;
        if (exp < 2_000_000) score += 1;
        return new BoxCandidate(address, score, checksumOk, species, pid);
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> mon, uint key)
    {
        var output = new byte[48];
        for (var i = 0; i < 48; i += 4)
            WriteU32(output, i, U32(mon, 0x20 + i) ^ key);
        return output;
    }

    private static ReadOnlySpan<byte> Subblock(uint pid, byte[] decrypted, int substructure)
    {
        var offset = Array.IndexOf(Gen3Constants.SubstructureOrders[pid % 24], substructure) * 12;
        return decrypted.AsSpan(offset, 12);
    }

    private static bool IsZero(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            if (b != 0) return false;
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

    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | data[offset + 1] << 8);

    private static uint U32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static void WriteU32(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
