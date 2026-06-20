namespace RocketTool.Core;

public sealed record BoxCandidate(uint Address, int Score, bool ChecksumOk, ushort Species, uint Pid);

public sealed record BoxRun(uint StartAddress, int Length, int ScoreSum, IReadOnlyList<BoxCandidate> Candidates);

public sealed record BoxStorageRun(uint StartAddress, int NonEmptyCount, int ScoreSum, int BoundaryScore, IReadOnlyDictionary<int, BoxCandidate> CandidatesBySlot);

public static class BoxScanner
{
    public const int BoxSlots = 30;
    public const int MaxBoxes = 14;
    public const int TotalSlots = BoxSlots * MaxBoxes;
    public const int StorageSize = TotalSlots * BoxPokemon.Size;

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

    public static BoxStorageRun? LocatePcStorage(ReadOnlySpan<byte> ewram, int minScore = 12)
    {
        var candidates = FindCandidates(ewram, minScore)
            .Where(c => c.ChecksumOk && c.Species is >= 1 and <= PartyScanner.MaxSpecies)
            .GroupBy(c => c.Address)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Score).First());
        if (candidates.Count == 0) return null;

        var ewramStart = PartyScanner.EwramBase;
        var ewramEnd = ewramStart + (uint)ewram.Length;
        var scoredBases = new Dictionary<uint, (int Count, int Score, int BoundaryScore)>();
        foreach (var candidate in candidates.Values)
        {
            for (var slot = 0; slot < TotalSlots; slot++)
            {
                var slotOffset = checked((uint)(slot * BoxPokemon.Size));
                if (candidate.Address < ewramStart + slotOffset) continue;
                var baseAddress = candidate.Address - slotOffset;
                if (baseAddress < ewramStart) continue;
                if (baseAddress + (uint)StorageSize > ewramEnd) continue;

                if (scoredBases.ContainsKey(baseAddress)) continue;
                var count = 0;
                var score = 0;
                for (var i = 0; i < TotalSlots; i++)
                {
                    var address = baseAddress + checked((uint)(i * BoxPokemon.Size));
                    if (!candidates.TryGetValue(address, out var hit)) continue;
                    count++;
                    score += hit.Score;
                }

                var boundaryScore = ScoreBoxBoundaries(baseAddress, candidates);
                scoredBases[baseAddress] = (count, score, boundaryScore);
            }
        }

        if (scoredBases.Count == 0) return null;
        var best = scoredBases
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.BoundaryScore)
            .ThenByDescending(kv => kv.Value.Score)
            .ThenBy(kv => kv.Key)
            .First();
        if (best.Value.Count < 2) return null;

        var bySlot = new Dictionary<int, BoxCandidate>();
        for (var i = 0; i < TotalSlots; i++)
        {
            var address = best.Key + checked((uint)(i * BoxPokemon.Size));
            if (candidates.TryGetValue(address, out var hit))
                bySlot[i + 1] = hit;
        }

        return new BoxStorageRun(best.Key, best.Value.Count, best.Value.Score, best.Value.BoundaryScore, bySlot);
    }

    private static int ScoreBoxBoundaries(uint baseAddress, IReadOnlyDictionary<uint, BoxCandidate> candidates)
    {
        var score = 0;
        for (var box = 0; box < MaxBoxes; box++)
        {
            var firstSlot = BoxSlots;
            var count = 0;
            var contiguous = 0;
            var stillContiguous = true;
            for (var slot = 0; slot < BoxSlots; slot++)
            {
                var address = baseAddress + checked((uint)((box * BoxSlots + slot) * BoxPokemon.Size));
                var occupied = candidates.ContainsKey(address);
                if (occupied)
                {
                    count++;
                    firstSlot = Math.Min(firstSlot, slot);
                    if (stillContiguous) contiguous++;
                }
                else if (count > 0)
                {
                    stillContiguous = false;
                }
            }

            if (count == 0) continue;

            // PC boxes commonly fill from the first slot. This resolves bases that
            // have the same total hits but shift every visible box by several slots.
            score += BoxSlots - firstSlot;
            if (firstSlot == 0) score += 30;
            score += Math.Min(contiguous, BoxSlots);
        }

        return score;
    }

    private static BoxCandidate ScoreBoxMon(uint address, ReadOnlySpan<byte> mon)
    {
        if (mon.Length < BoxPokemon.Size || IsZero(mon)) return new BoxCandidate(address, 0, false, 0, 0);
        var pokemon = new BoxPokemon(mon);
        if (pokemon.IsEmpty) return new BoxCandidate(address, 0, false, 0, pokemon.Pid);
        var info = pokemon.GetInfo();
        var language = mon[0x12];

        var score = 0;
        var checksumOk = info.Checksum == info.CalculatedChecksum;
        if (checksumOk) score += 6;
        if (info.Species is >= 1 and <= PartyScanner.MaxSpecies) score += 5;
        if (language is >= 1 and <= 7) score += 1;
        if (HasNickname(mon.Slice(0x08, 0x0A))) score += 1;
        if (info.Exp < 2_000_000) score += 1;
        return new BoxCandidate(address, score, checksumOk, info.Species, pokemon.Pid);
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

}
