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

    public static IReadOnlyList<BoxCandidate> FindCandidates(
        ReadOnlySpan<byte> ewram,
        uint ewramBase,
        PokemonDataLayout layout,
        int minScore,
        int maxSpecies)
    {
        var recordSize = RecordSize(layout);
        var hits = new List<BoxCandidate>();
        for (var off = 0; off <= ewram.Length - recordSize; off += 4)
        {
            var candidate = ScoreBoxMon(ewramBase + (uint)off, ewram.Slice(off, recordSize), maxSpecies, layout);
            if (candidate.Score >= minScore)
                hits.Add(candidate);
        }

        return hits.OrderBy(c => c.Address).ToArray();
    }

    public static IReadOnlyList<BoxRun> GroupRuns(IEnumerable<BoxCandidate> candidates, int recordSize, bool checksumRequired = true)
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
                current += checked((uint)recordSize);
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

    public static BoxRun? LocateBestRun(ReadOnlySpan<byte> ewram, uint ewramBase, int maxSpecies, PokemonDataLayout layout)
    {
        var recordSize = RecordSize(layout);
        var candidates = FindCandidates(ewram, ewramBase, layout, 12, maxSpecies);
        return GroupRuns(candidates, recordSize).FirstOrDefault(r => r.Length >= 2)
               ?? GroupRuns(candidates, recordSize, checksumRequired: false).FirstOrDefault(r => r.Length >= 2)
               ?? GroupRuns(candidates, recordSize).FirstOrDefault();
    }

    public static BoxStorageRun? LocatePcStorage(
        ReadOnlySpan<byte> ewram,
        uint ewramBase,
        PokemonDataLayout layout,
        int minScore,
        int maxSpecies,
        int maxBoxes,
        int slotsPerBox)
    {
        var recordSize = RecordSize(layout);
        var totalSlots = slotsPerBox * maxBoxes;
        var storageSize = totalSlots * recordSize;
        var candidates = FindCandidates(ewram, ewramBase, layout, minScore, maxSpecies)
            .Where(c => c.ChecksumOk && c.Species >= 1 && c.Species <= maxSpecies)
            .GroupBy(c => c.Address)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Score).First());
        if (candidates.Count == 0) return null;

        var ewramStart = ewramBase;
        var ewramEnd = ewramStart + (uint)ewram.Length;
        var scoredBases = new Dictionary<uint, (int Count, int Score, int BoundaryScore)>();
        foreach (var candidate in candidates.Values)
        {
            for (var slot = 0; slot < totalSlots; slot++)
            {
                var slotOffset = checked((uint)(slot * recordSize));
                if (candidate.Address < ewramStart + slotOffset) continue;
                var baseAddress = candidate.Address - slotOffset;
                if (baseAddress < ewramStart) continue;
                if (baseAddress + (uint)storageSize > ewramEnd) continue;

                if (scoredBases.ContainsKey(baseAddress)) continue;
                var count = 0;
                var score = 0;
                for (var i = 0; i < totalSlots; i++)
                {
                    var address = baseAddress + checked((uint)(i * recordSize));
                    if (!candidates.TryGetValue(address, out var hit)) continue;
                    count++;
                    score += hit.Score;
                }

                var boundaryScore = ScoreBoxBoundaries(baseAddress, candidates, maxBoxes, slotsPerBox, recordSize);
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
        for (var i = 0; i < totalSlots; i++)
        {
            var address = best.Key + checked((uint)(i * recordSize));
            if (candidates.TryGetValue(address, out var hit))
                bySlot[i + 1] = hit;
        }

        return new BoxStorageRun(best.Key, best.Value.Count, best.Value.Score, best.Value.BoundaryScore, bySlot);
    }

    private static int ScoreBoxBoundaries(uint baseAddress, IReadOnlyDictionary<uint, BoxCandidate> candidates, int maxBoxes, int slotsPerBox, int recordSize)
    {
        var score = 0;
        for (var box = 0; box < maxBoxes; box++)
        {
            var firstSlot = slotsPerBox;
            var count = 0;
            var contiguous = 0;
            var stillContiguous = true;
            for (var slot = 0; slot < slotsPerBox; slot++)
            {
                var address = baseAddress + checked((uint)((box * slotsPerBox + slot) * recordSize));
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
            score += slotsPerBox - firstSlot;
            if (firstSlot == 0) score += 30;
            score += Math.Min(contiguous, slotsPerBox);
        }

        return score;
    }

    private static BoxCandidate ScoreBoxMon(uint address, ReadOnlySpan<byte> mon, int maxSpecies, PokemonDataLayout layout)
    {
        if (mon.Length < RecordSize(layout) || IsZero(mon)) return new BoxCandidate(address, 0, false, 0, 0);
        var pokemon = new BoxPokemon(mon, layout);
        if (pokemon.IsEmpty) return new BoxCandidate(address, 0, false, 0, pokemon.Pid);
        var info = pokemon.GetInfo();
        var language = mon[0x12];

        var score = 0;
        var checksumOk = info.Checksum == info.CalculatedChecksum;
        if (checksumOk) score += 6;
        if (info.Species >= 1 && info.Species <= maxSpecies) score += 5;
        if (language is >= 1 and <= 7 or 0x12) score += 1;
        if (HasNickname(mon.Slice(0x08, 0x0A))) score += 1;
        if (info.Exp < 2_000_000) score += 1;
        return new BoxCandidate(address, score, checksumOk, info.Species, pokemon.Pid);
    }

    private static int RecordSize(PokemonDataLayout layout)
        => layout == PokemonDataLayout.UnboundCfruPlainParty ? BoxPokemon.UnboundCompressedSize : BoxPokemon.Size;

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
