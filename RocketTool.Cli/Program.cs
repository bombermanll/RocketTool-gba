using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RocketTool.Core;

static string RootDir()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8; i++)
    {
        var candidate = Path.GetFullPath(Path.Combine(dir, string.Concat(Enumerable.Repeat("../", i))));
        if (Directory.Exists(Path.Combine(candidate, "profiles"))) return candidate;
    }
    throw new DirectoryNotFoundException("Cannot find csharp/profiles. Run from the project tree or publish profiles with the CLI.");
}

static int ParseInt(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
    ? Convert.ToInt32(value[2..], 16)
    : int.Parse(value);

static uint ParseUInt(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
    ? Convert.ToUInt32(value[2..], 16)
    : Convert.ToUInt32(value);

static string GetArg(string[] args, string name, string fallback)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : fallback;
}

static int GetIntArg(string[] args, string name, int fallback)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? ParseInt(args[idx + 1]) : fallback;
}

static uint? GetUIntArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? ParseUInt(args[idx + 1]) : null;
}

static bool HasArg(string[] args, string name) => args.Contains(name);

static string[] ValuesAfter(string[] args, params string[] names)
{
    foreach (var name in names)
    {
        var idx = Array.IndexOf(args, name);
        if (idx < 0) continue;
        return args.Skip(idx + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
    }
    return [];
}

static int RequiredIntArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    if (idx < 0 || idx + 1 >= args.Length) throw new ArgumentException($"Missing {name}");
    return ParseInt(args[idx + 1]);
}

static byte? GetByteArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    if (idx < 0 || idx + 1 >= args.Length) return null;
    var value = ParseInt(args[idx + 1]);
    if (value is < 0 or > 255) throw new ArgumentOutOfRangeException(name, "value must be 0..255");
    return (byte)value;
}

static ushort? GetUShortArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    if (idx < 0 || idx + 1 >= args.Length) return null;
    var value = ParseInt(args[idx + 1]);
    if (value is < 0 or > 65535) throw new ArgumentOutOfRangeException(name, "value must be 0..65535");
    return (ushort)value;
}

static uint? GetUInt32Arg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? ParseUInt(args[idx + 1]) : null;
}

static GameProfile CurrentProfile()
{
    var profiles = GameProfileCatalog.Load(Path.Combine(RootDir(), "profiles"));
    var requested = Environment.GetEnvironmentVariable("ROCKET_TOOL_PROFILE");
    if (!string.IsNullOrWhiteSpace(requested))
        return profiles.FirstOrDefault(profile => string.Equals(profile.Id, requested, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"Unknown ROCKET_TOOL_PROFILE: {requested}");
    if (profiles.Count == 1) return profiles[0];
    throw new InvalidOperationException("Multiple profiles are installed. Set ROCKET_TOOL_PROFILE to the required profile ID.");
}

static ModifierDatabase Db()
{
    var profile = CurrentProfile();
    return new ModifierDatabase(profile.DatabaseDirectory);
}

static IGameRuntimeAdapter CurrentRuntime()
{
    var profile = CurrentProfile();
    return GameRuntimeAdapterCatalog.ForProfile(profile);
}

static PokemonDataLayout CurrentPokemonLayout()
    => CurrentRuntime().PartyLayout;

static MgbaBridgeClient ConnectForProfile(GameProfile profile, string host, int port)
{
    var bridge = MgbaBridgeClient.Connect(host, port);
    try
    {
        GameRuntimeAdapterCatalog.ForProfile(profile).ValidateLiveRom(bridge);
        return bridge;
    }
    catch
    {
        bridge.Dispose();
        throw;
    }
}

static SpeciesStats ReadSpeciesStatsFromDb(ModifierDatabase db, int species)
{
    if (!db.Table("species_stats").TryGetValue(species, out var raw))
        throw new InvalidOperationException($"profile db/species_stats.tsv missing species {species}");
    var parts = raw.Split('\t');
    if (parts.Length < 22)
        throw new InvalidOperationException($"Invalid species_stats row for species {species}");

    byte B(int i) => byte.Parse(parts[i]);
    ushort U(int i) => ushort.Parse(parts[i]);
    ushort? OptionalAbility(int i)
    {
        var ability = U(i);
        return ability == 0 ? null : ability;
    }
    return new SpeciesStats(
        species,
        B(0), B(1), B(2), B(3), B(4), B(5), B(6), B(7),
        U(8), U(9), U(10), U(11), U(12),
        B(13), B(14), B(15), B(16), B(17), B(18),
        U(19), U(20), OptionalAbility(21));
}

static MoveData ReadMoveDataFromDb(ModifierDatabase db, int move)
{
    if (!db.Table("move_data").TryGetValue(move, out var raw))
        throw new InvalidOperationException($"profile db/move_data.tsv missing move {move}");
    var parts = raw.Split('\t');
    if (parts.Length < 10)
        throw new InvalidOperationException($"Invalid move_data row for move {move}");

    ushort U(int i) => ushort.Parse(parts[i]);
    byte B(int i) => byte.Parse(parts[i]);
    sbyte S(int i) => unchecked((sbyte)byte.Parse(parts[i]));
    var effect = parts.Length >= 11 ? B(10) : (byte)0;
    return new MoveData(move, U(0), B(1), B(2), B(3), B(4), U(5), S(6), U(7), B(8), U(9), effect, []);
}

static ItemData ReadItemDataFromDb(ModifierDatabase db, int item)
{
    if (!db.Table("item_data").TryGetValue(item, out var raw))
        throw new InvalidOperationException($"profile db/item_data.tsv missing item {item}");
    var parts = raw.Split('\t');
    if (parts.Length < 13)
        throw new InvalidOperationException($"Invalid item_data row for item {item}");

    ushort U(int i) => ushort.Parse(parts[i]);
    byte B(int i) => byte.Parse(parts[i]);
    uint UI(int i) => uint.Parse(parts[i]);
    return new ItemData(item, U(0), U(1), B(2), B(3), UI(4), B(5), B(6), B(7), B(8), UI(9), B(10), UI(11), UI(12), [], []);
}

static void RecalculateLiveStats(PartyPokemon mon, string[] args)
{
    var db = Db();
    var info = mon.GetInfo();
    mon.RecalculateStats(ReadSpeciesStatsFromDb(db, info.Species));
}

static bool Confirm(string[] args, string summary)
{
    Console.WriteLine(summary);
    if (HasArg(args, "--yes")) return true;
    Console.Write("Type yes to write: ");
    return string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
}

static int ValidateSlot(string[] args)
{
    var slot = RequiredIntArg(args, "--slot");
    if (slot is < 1 or > 6) throw new ArgumentOutOfRangeException("--slot", "slot must be 1..6");
    return slot;
}

static uint SlotAddress(uint baseAddr, int slot) => baseAddr + (uint)((slot - 1) * Gen3Constants.PartyMonSize);

static uint ResolvePartyBase(string[] args, MgbaBridgeClient bridge, bool quiet = false)
{
    var explicitBase = GetUIntArg(args, "--base");
    if (explicitBase is not null) return explicitBase.Value;
    return ScanPartyBase(bridge, quiet);
}

static uint ScanPartyBase(MgbaBridgeClient bridge, bool quiet = false)
{
    var profile = CurrentProfile();
    var layout = CurrentPokemonLayout();
    if (!quiet) Console.WriteLine("Scanning EWRAM for live party...");
    var ewram = PartyScanner.ReadEwram(bridge, profile, quiet ? null : (off, total) =>
    {
        if (off % 0x10000 == 0) Console.WriteLine($"read EWRAM 0x{off:X5}/0x{total:X5}");
    });
    var run = PartyScanner.LocateParty(
                  ewram,
                  profile.Memory.EwramBase,
                  layout,
                  profile.Memory.DefaultPartyBase,
                  profile.Memory.PartyCountOffsetFromPartyBase,
                  profile.Limits.MaxSpecies)
              ?? throw new InvalidOperationException("Could not auto-locate the live party base.");
    if (!quiet) Console.WriteLine($"Auto party base: 0x{run.StartAddress:X8} len={run.Length} score_sum={run.ScoreSum}");
    return run.StartAddress;
}

static uint ResolveWritablePartyBase(string[] args, MgbaBridgeClient bridge, bool quiet = false)
{
    var explicitBase = GetUIntArg(args, "--base");
    var scannedBase = ScanPartyBase(bridge, quiet);
    if (explicitBase is not null && explicitBase.Value != scannedBase)
        throw new InvalidOperationException($"Explicit party base 0x{explicitBase.Value:X8} does not match scanned live party base 0x{scannedBase:X8}.");
    return scannedBase;
}

static (uint BaseAddr, uint Addr, PartyPokemon Mon) ReadWritableMon(MgbaBridgeClient bridge, string[] args, int slot)
{
    var baseAddr = ResolveWritablePartyBase(args, bridge);
    var addr = SlotAddress(baseAddr, slot);
    return (baseAddr, addr, new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize), CurrentPokemonLayout()));
}

static void WriteMon(GameProfile profile, MgbaBridgeClient bridge, uint baseAddr, uint addr, PartyPokemon mon)
{
    var partyEnd = (ulong)baseAddr + Gen3Constants.PartySlots * Gen3Constants.PartyMonSize;
    var ewramEnd = (ulong)profile.Memory.EwramBase + (uint)profile.Memory.EwramSize;
    if (baseAddr < profile.Memory.EwramBase || partyEnd > ewramEnd ||
        addr < baseAddr || (addr - baseAddr) % Gen3Constants.PartyMonSize != 0 ||
        (ulong)addr + Gen3Constants.PartyMonSize > partyEnd)
        throw new InvalidOperationException("Party write address is outside the scanned six-slot party range.");
    bridge.WriteRangeVerified(addr, mon.Raw);
}

static (uint Address, byte Count) ReadLivePartyCount(GameProfile profile, MgbaBridgeClient bridge, uint baseAddr)
{
    var countAddress = (long)baseAddr + profile.Memory.PartyCountOffsetFromPartyBase;
    var ewramEnd = (long)profile.Memory.EwramBase + profile.Memory.EwramSize;
    if (countAddress < profile.Memory.EwramBase || countAddress >= ewramEnd)
        throw new InvalidOperationException("Party count address is outside the current Profile EWRAM range.");
    var count = bridge.Read((uint)countAddress, 1)[0];
    if (count > Gen3Constants.PartySlots)
        throw new InvalidOperationException($"Live party count {count} is outside 0..{Gen3Constants.PartySlots}.");
    return ((uint)countAddress, count);
}

static void WriteLivePartyCount(MgbaBridgeClient bridge, uint address, int count)
{
    if (count is < 0 or > Gen3Constants.PartySlots)
        throw new ArgumentOutOfRangeException(nameof(count), $"party count must be 0..{Gen3Constants.PartySlots}");
    bridge.WriteRangeVerified(address, [(byte)count]);
    var readback = bridge.Read(address, 1)[0];
    if (readback != count)
        throw new InvalidOperationException($"Party count readback mismatch: expected {count}, got {readback}.");
}

static void PrintMon(int slot, uint addr, PartyPokemon mon, ModifierDatabase db)
{
    if (mon.IsEmpty)
    {
        Console.WriteLine($"slot {slot} @ 0x{addr:X8}: empty");
        return;
    }
    var info = mon.GetInfo();
    var speciesName = db.NameOf("species", info.Species);
    var itemName = info.Item == 0 ? "无" : db.NameOf("items", info.Item);
    var moves = string.Join(", ", info.Moves.Select(m => $"{m}({(m == 0 ? "无" : db.NameOf("moves", m))})"));
    var evs = string.Join(", ", Gen3Constants.StatNames.Select((name, i) => $"{name}={info.Evs[i]}"));
    var ivs = string.Join(", ", info.Ivs.Where(kv => kv.Key is not "egg" and not "ability").Select(kv => $"{kv.Key}={kv.Value}"));
    var ok = info.Checksum == info.CalculatedChecksum ? "OK" : "BAD";
    Console.WriteLine($"slot {slot} @ 0x{addr:X8}: {info.Species}({speciesName}) Lv{info.Level} HP {info.Hp}/{info.MaxHp} checksum {ok} 0x{info.Checksum:X4}/0x{info.CalculatedChecksum:X4}");
    Console.WriteLine($"  item {info.Item}({itemName}) exp {info.Exp} friendship {info.Friendship} pp_bonuses=0x{info.PpBonuses:X2} status 0x{info.Status:X8}");
    Console.WriteLine($"  nature_pid={info.Nature} game_nature_code={info.GameNatureCode}");
    Console.WriteLine($"  moves: {moves} pp=[{string.Join(", ", info.Pp)}]");
    Console.WriteLine($"  evs=[{evs}] ev_sum={info.Evs.Sum(x => x)} ivs=[{ivs}] abilitySlot={info.Ivs["ability"]} egg={info.Ivs["egg"]}");
}

static int PartyDump(string[] args)
{
    var profile = CurrentProfile();
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    runtime.EnsureCanRead(GameDataSurface.Party, live: true);
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    var baseAddr = ResolvePartyBase(args, bridge);
    var layout = CurrentPokemonLayout();
    var slots = GetUIntArg(args, "--slot") is uint slot ? [checked((int)slot)] : Enumerable.Range(1, Gen3Constants.PartySlots).ToArray();
    foreach (var s in slots)
    {
        if (s is < 1 or > 6) throw new ArgumentOutOfRangeException("--slot", "slot must be 1..6");
        var addr = SlotAddress(baseAddr, s);
        PrintMon(s, addr, new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize), layout), db);
    }
    return 0;
}

static int PartyScan(string[] args)
{
    var profile = CurrentProfile();
    GameRuntimeAdapterCatalog.ForProfile(profile).EnsureCanRead(GameDataSurface.Party, live: true);
    var layout = CurrentPokemonLayout();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var minScore = GetIntArg(args, "--min-score", 13);
    var limit = GetIntArg(args, "--limit", 30);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    var ewram = PartyScanner.ReadEwram(bridge, profile, (off, total) =>
    {
        if (off % 0x10000 == 0) Console.WriteLine($"read EWRAM 0x{off:X5}/0x{total:X5}");
    });
    var hits = PartyScanner.FindCandidates(ewram, profile.Memory.EwramBase, layout, minScore, profile.Limits.MaxSpecies);
    Console.WriteLine($"\nCandidates: {hits.Count} with score >= {minScore}");
    foreach (var c in hits.Take(limit))
    {
        Console.WriteLine($"0x{c.Address:X8} score={c.Score:00} species={c.Species} lv={c.Level} hp={c.Hp}/{c.MaxHp} checksum={(c.ChecksumOk ? "ok" : "bad")} pid=0x{c.Pid:X8}");
    }
    Console.WriteLine("\nConsecutive checksum-ok runs:");
    foreach (var run in PartyScanner.GroupRuns(hits, checksumRequired: true).Take(10))
    {
        var addrs = string.Join(", ", run.Candidates.Take(6).Select(c => $"0x{c.Address:X8}"));
        Console.WriteLine($"len={run.Length} score_sum={run.ScoreSum} start=0x{run.StartAddress:X8} :: {addrs}");
    }
    var best = PartyScanner.LocateParty(
        ewram,
        profile.Memory.EwramBase,
        layout,
        profile.Memory.DefaultPartyBase,
        profile.Memory.PartyCountOffsetFromPartyBase,
        profile.Limits.MaxSpecies,
        minScore);
    if (best is not null) Console.WriteLine($"\nBest live party base: 0x{best.StartAddress:X8}");
    return 0;
}

static int BoxScan(string[] args)
{
    var profile = CurrentProfile();
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    runtime.EnsureCanRead(GameDataSurface.Boxes, live: true);
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var minScore = GetIntArg(args, "--min-score", 12);
    var limit = GetIntArg(args, "--limit", 30);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    var ewram = PartyScanner.ReadEwram(bridge, profile, (off, total) =>
    {
        if (off % 0x10000 == 0) Console.WriteLine($"read EWRAM 0x{off:X5}/0x{total:X5}");
    });
    if (profile.Memory.PcBoxStoragePointerAddress != 0)
    {
        var pointerRaw = bridge.Read(profile.Memory.PcBoxStoragePointerAddress, 4);
        var storageBase = (uint)(pointerRaw[0] | pointerRaw[1] << 8 | pointerRaw[2] << 16 | pointerRaw[3] << 24);
        var recordsBase = storageBase + checked((uint)profile.Memory.PcBoxDataOffset);
        var totalSlots = profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots;
        var byteLength = checked((uint)(totalSlots * profile.Memory.PcBoxRecordSize));
        var ewramEnd = profile.Memory.EwramBase + checked((uint)profile.Memory.EwramSize);
        if (storageBase < profile.Memory.EwramBase || recordsBase + byteLength > ewramEnd)
            throw new InvalidOperationException($"PC 箱子指针 0x{profile.Memory.PcBoxStoragePointerAddress:X8} 的值 0x{storageBase:X8} 不在完整 EWRAM 箱子范围内。");

        var counts = new int[profile.Memory.PcBoxCount];
        var shown = 0;
        for (var slot = 1; slot <= totalSlots; slot++)
        {
            var address = recordsBase + checked((uint)((slot - 1) * profile.Memory.PcBoxRecordSize));
            var offset = checked((int)(address - profile.Memory.EwramBase));
            var mon = new BoxPokemon(ewram.AsSpan(offset, profile.Memory.PcBoxRecordSize), runtime.LiveBoxLayout);
            if (mon.IsEmpty || !mon.HasValidHeader(profile.Limits.MaxSpecies)) continue;
            counts[(slot - 1) / profile.Memory.PcBoxSlots]++;
            if (shown++ < limit)
            {
                var info = mon.GetInfo();
                var box = (slot - 1) / profile.Memory.PcBoxSlots + 1;
                var slotInBox = (slot - 1) % profile.Memory.PcBoxSlots + 1;
                Console.WriteLine($"box{box:00}-{slotInBox:00} 0x{address:X8} species={info.Species}({db.NameOf("species", info.Species)}) pid=0x{info.Pid:X8}");
            }
        }
        Console.WriteLine($"\nProfile PC storage pointer: 0x{profile.Memory.PcBoxStoragePointerAddress:X8} -> 0x{storageBase:X8}; records=0x{recordsBase:X8}; non_empty={counts.Sum()}/{totalSlots}");
        for (var box = 1; box <= counts.Length; box++)
            Console.WriteLine($"  box{box:00}: {counts[box - 1]}");
        return 0;
    }
    if (profile.Memory.PcBoxRegions.Count > 0)
    {
        var counts = new int[profile.Memory.PcBoxCount];
        var shown = 0;
        var totalSlots = profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots;
        for (var globalSlot = 1; globalSlot <= totalSlots; globalSlot++)
        {
            var box = (globalSlot - 1) / profile.Memory.PcBoxSlots + 1;
            var slotInBox = (globalSlot - 1) % profile.Memory.PcBoxSlots;
            var region = profile.Memory.PcBoxRegions.Single(candidate =>
                box >= candidate.FirstBox && box < candidate.FirstBox + candidate.BoxCount);
            var recordIndex = (box - region.FirstBox) * profile.Memory.PcBoxSlots + slotInBox;
            var address = region.Address + checked((uint)(recordIndex * profile.Memory.PcBoxRecordSize));
            var offset = checked((int)(address - profile.Memory.EwramBase));
            var mon = new BoxPokemon(ewram.AsSpan(offset, profile.Memory.PcBoxRecordSize), runtime.LiveBoxLayout);
            if (mon.IsEmpty || !mon.HasValidHeader(profile.Limits.MaxSpecies)) continue;
            counts[box - 1]++;
            if (shown++ < limit)
            {
                var info = mon.GetInfo();
                Console.WriteLine($"box{box:00}-{slotInBox + 1:00} 0x{address:X8} species={info.Species}({db.NameOf("species", info.Species)}) pid=0x{info.Pid:X8}");
            }
        }

        Console.WriteLine($"\nProfile PC regions: {profile.Memory.PcBoxRegions.Count}; non_empty={counts.Sum()}/{totalSlots}");
        for (var box = 1; box <= counts.Length; box++)
            Console.WriteLine($"  box{box:00}: {counts[box - 1]}");
        return 0;
    }
    var hits = BoxScanner.FindCandidates(ewram, profile.Memory.EwramBase, runtime.LiveBoxLayout, minScore, profile.Limits.MaxSpecies);
    Console.WriteLine($"\nBox candidates: {hits.Count} with score >= {minScore}");
    foreach (var c in hits.Take(limit))
        Console.WriteLine($"0x{c.Address:X8} score={c.Score:00} species={c.Species}({db.NameOf("species", c.Species)}) checksum={(c.ChecksumOk ? "ok" : "bad")} pid=0x{c.Pid:X8}");

    Console.WriteLine("\nConsecutive checksum-ok box runs:");
    foreach (var run in BoxScanner.GroupRuns(hits, profile.Memory.PcBoxRecordSize, checksumRequired: true).Take(10))
    {
        var addrs = string.Join(", ", run.Candidates.Take(6).Select(c => $"0x{c.Address:X8}"));
        Console.WriteLine($"len={run.Length} score_sum={run.ScoreSum} start=0x{run.StartAddress:X8} :: {addrs}");
    }
    var best = BoxScanner.LocateBestRun(ewram, profile.Memory.EwramBase, profile.Limits.MaxSpecies, runtime.LiveBoxLayout);
    if (best is not null) Console.WriteLine($"\nBest live box base: 0x{best.StartAddress:X8} len={best.Length}");

    var storage = BoxScanner.LocatePcStorage(
        ewram,
        profile.Memory.EwramBase,
        runtime.LiveBoxLayout,
        minScore,
        profile.Limits.MaxSpecies,
        profile.Memory.PcBoxCount,
        profile.Memory.PcBoxSlots);
    if (storage is not null)
    {
        Console.WriteLine($"\nBest PC storage base: 0x{storage.StartAddress:X8} non_empty={storage.NonEmptyCount}/{profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots} score_sum={storage.ScoreSum} boundary={storage.BoundaryScore}");
        for (var box = 1; box <= profile.Memory.PcBoxCount; box++)
        {
            var start = (box - 1) * profile.Memory.PcBoxSlots + 1;
            var end = start + profile.Memory.PcBoxSlots - 1;
            var count = storage.CandidatesBySlot.Keys.Count(slot => slot >= start && slot <= end);
            Console.WriteLine($"  box{box:00}: {count}");
        }
    }
    else
    {
        Console.WriteLine("\nBest PC storage base: not found");
    }
    return 0;
}

static uint ConfiguredBoxAddress(GameProfile profile, MgbaBridgeClient bridge, int globalSlot)
{
    var totalSlots = profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots;
    if (globalSlot < 1 || globalSlot > totalSlots)
        throw new ArgumentOutOfRangeException(nameof(globalSlot), $"box slot must be 1..{totalSlots}");

    if (profile.Memory.PcBoxStoragePointerAddress != 0)
    {
        var storageBase = BinaryPrimitives.ReadUInt32LittleEndian(bridge.Read(profile.Memory.PcBoxStoragePointerAddress, 4));
        return storageBase + checked((uint)profile.Memory.PcBoxDataOffset) +
               checked((uint)((globalSlot - 1) * profile.Memory.PcBoxRecordSize));
    }

    var box = (globalSlot - 1) / profile.Memory.PcBoxSlots + 1;
    var slotInBox = (globalSlot - 1) % profile.Memory.PcBoxSlots;
    var region = profile.Memory.PcBoxRegions.SingleOrDefault(candidate =>
        box >= candidate.FirstBox && box < candidate.FirstBox + candidate.BoxCount)
        ?? throw new InvalidOperationException($"Profile does not contain a configured region for box {box}.");
    var recordIndex = (box - region.FirstBox) * profile.Memory.PcBoxSlots + slotInBox;
    return region.Address + checked((uint)(recordIndex * profile.Memory.PcBoxRecordSize));
}

static uint NewNonShinyImportPid(uint otId)
{
    uint pid;
    do
    {
        pid = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1);
    } while (PartyPokemon.IsShinyPid(pid, otId));
    return pid;
}

static (ushort[] Moves, byte[] Pp) BuildDexImportMoves(ModifierDatabase db, int species, int level)
{
    var selected = new List<ushort>();
    if (db.Table("species_level_moves").TryGetValue(species, out var rawLevelMoves))
    {
        foreach (var entry in rawLevelMoves.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length != 2 || !ushort.TryParse(parts[0], out var learnedAt) || !ushort.TryParse(parts[1], out var move))
                throw new InvalidOperationException($"species {species} has an invalid level-up move row.");
            if (learnedAt > level || move == 0) continue;
            selected.Remove(move);
            selected.Add(move);
            if (selected.Count > 4) selected.RemoveAt(0);
        }
    }

    var moves = new ushort[4];
    var pp = new byte[4];
    for (var i = 0; i < selected.Count; i++)
    {
        moves[i] = selected[i];
        pp[i] = ReadMoveDataFromDb(db, selected[i]).Pp;
    }
    return (moves, pp);
}

static PartyPokemon BuildDexImportParty(
    GameProfile profile,
    ModifierDatabase db,
    Gen3SaveTrainerInfo trainer,
    int species,
    int level = 5)
{
    if (species < 1 || species > profile.Limits.MaxSpecies)
        throw new ArgumentOutOfRangeException(nameof(species), $"species must be 1..{profile.Limits.MaxSpecies}");

    var stats = ReadSpeciesStatsFromDb(db, species);
    if (!db.Table("species_nickname_bytes").TryGetValue(species, out var nicknameHex) &&
        !db.Table("species_name_bytes").TryGetValue(species, out nicknameHex))
        throw new InvalidOperationException($"species {species} is missing current-Profile nickname bytes.");
    var nickname = Convert.FromHexString(nicknameHex.Trim());
    if (nickname.Length < 10)
        throw new InvalidOperationException($"species {species} nickname entry is shorter than 10 bytes.");

    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    var experience = new PokemonExperienceTable(db).ExperienceForLevel(level, stats.GrowthRate);
    var (moves, pp) = BuildDexImportMoves(db, species, level);
    var mon = PartyPokemon.Create(NewNonShinyImportPid(trainer.OtId), trainer.OtId, runtime.PartyLayout);
    mon.SetOtName(trainer.NameBytes);
    mon.SetNicknameFromSpeciesNameEntry(nickname);
    mon.SetGrowth((ushort)species, item: 0, exp: experience, friendship: stats.Friendship, ppBonuses: 0);
    mon.SetGameNatureCode(26);
    mon.SetMoves(moves.Select(move => (ushort?)move).ToArray(), pp.Select(value => (byte?)value).ToArray());
    mon.SetIvs(new Dictionary<string, int>
    {
        ["hp"] = 31, ["atk"] = 31, ["def"] = 31,
        ["spe"] = 31, ["spa"] = 31, ["spd"] = 31, ["ability"] = 0
    });
    mon.SetEvs(new Dictionary<string, byte>
    {
        ["hp"] = 0, ["atk"] = 0, ["def"] = 0,
        ["spe"] = 0, ["spa"] = 0, ["spd"] = 0
    });
    mon.SetUnencrypted(status: 0, level: (byte)level);
    mon.RecalculateStats(stats);
    return mon;
}

static int BoxVerifyCreate(string[] args)
{
    var profile = CurrentProfile();
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    runtime.EnsureCanWrite(GameDataSurface.Boxes, live: true);
    var box = GetIntArg(args, "--box", 1);
    var slot = GetIntArg(args, "--slot", 2);
    var species = GetIntArg(args, "--species", 1);
    if (box < 1 || box > profile.Memory.PcBoxCount)
        throw new ArgumentOutOfRangeException("--box", $"box must be 1..{profile.Memory.PcBoxCount}");
    if (slot < 1 || slot > profile.Memory.PcBoxSlots)
        throw new ArgumentOutOfRangeException("--slot", $"slot must be 1..{profile.Memory.PcBoxSlots}");
    if (species < 1 || species > profile.Limits.MaxSpecies)
        throw new ArgumentOutOfRangeException("--species", $"species must be 1..{profile.Limits.MaxSpecies}");

    using var bridge = ConnectForProfile(profile, GetArg(args, "--host", "127.0.0.1"), GetIntArg(args, "--port", 8765));
    var globalSlot = (box - 1) * profile.Memory.PcBoxSlots + slot;
    var address = ConfiguredBoxAddress(profile, bridge, globalSlot);
    var ewramEnd = profile.Memory.EwramBase + checked((uint)profile.Memory.EwramSize);
    if (address < profile.Memory.EwramBase ||
        (ulong)address + (uint)profile.Memory.PcBoxRecordSize > ewramEnd)
        throw new InvalidOperationException("Configured box slot is outside the current Profile EWRAM range.");

    var original = bridge.Read(address, profile.Memory.PcBoxRecordSize);
    if (original.Any(value => value != 0))
        throw new InvalidOperationException($"box{box:00}-{slot:00} is not an all-zero empty slot; refusing the create probe.");

    var db = Db();
    var trainer = ReadLiveTrainer(bridge, profile);
    var stats = ReadSpeciesStatsFromDb(db, species);
    if (!db.Table("species_nickname_bytes").TryGetValue(species, out var nicknameHex) &&
        !db.Table("species_name_bytes").TryGetValue(species, out nicknameHex))
        throw new InvalidOperationException($"species {species} is missing Profile nickname bytes.");
    var nickname = Convert.FromHexString(nicknameHex.Trim());
    if (nickname.Length < 10)
        throw new InvalidOperationException($"species {species} nickname entry is shorter than 10 bytes.");

    var selectedMoves = new List<ushort>();
    if (db.Table("species_level_moves").TryGetValue(species, out var rawLevelMoves))
    {
        foreach (var entry in rawLevelMoves.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length != 2 || !ushort.TryParse(parts[0], out var level) || !ushort.TryParse(parts[1], out var move))
                throw new InvalidOperationException($"species {species} has an invalid level-up move row.");
            if (level > 5 || move == 0) continue;
            selectedMoves.Remove(move);
            selectedMoves.Add(move);
            if (selectedMoves.Count > 4) selectedMoves.RemoveAt(0);
        }
    }

    var moves = new ushort[4];
    for (var i = 0; i < selectedMoves.Count; i++) moves[i] = selectedMoves[i];
    var experience = new PokemonExperienceTable(db).ExperienceForLevel(5, stats.GrowthRate);
    var mon = BoxPokemon.Create(NewNonShinyImportPid(trainer.OtId), trainer.OtId, runtime.LiveBoxLayout);
    mon.SetOtName(trainer.NameBytes);
    mon.SetNicknameFromSpeciesNameEntry(nickname);
    mon.SetGrowth((ushort)species, item: 0, exp: experience, friendship: stats.Friendship, ppBonuses: 0);
    mon.SetGameNatureCode(26);
    mon.SetMoves(moves.Select(move => (ushort?)move).ToArray(), [null, null, null, null]);
    mon.SetIvs(new Dictionary<string, int>
    {
        ["hp"] = 31, ["atk"] = 31, ["def"] = 31,
        ["spe"] = 31, ["spa"] = 31, ["spd"] = 31, ["ability"] = 0
    });
    mon.SetEvs(new Dictionary<string, byte>
    {
        ["hp"] = 0, ["atk"] = 0, ["def"] = 0,
        ["spe"] = 0, ["spa"] = 0, ["spd"] = 0
    });

    var intended = mon.Raw.ToArray();
    var intendedInfo = mon.GetInfo();
    if (!mon.HasValidHeader(profile.Limits.MaxSpecies) || intendedInfo.Species != species ||
        intendedInfo.OtId != trainer.OtId || intendedInfo.Exp != experience)
        throw new InvalidOperationException("Constructed box record failed pre-write validation.");
    if (!Confirm(args, $"将在箱{box:00}-{slot:00}临时新建 {db.NameOf("species", species)}，完整读回后恢复全零槽。请先创建 mGBA 即时存档。"))
        return 0;

    try
    {
        bridge.WriteRangeVerified(address, intended);
        var readback = bridge.Read(address, intended.Length);
        if (!readback.AsSpan().SequenceEqual(intended))
            throw new InvalidOperationException("Created box record did not match the complete intended byte sequence.");
        var parsed = new BoxPokemon(readback, runtime.LiveBoxLayout);
        var info = parsed.GetInfo();
        if (!parsed.HasValidHeader(profile.Limits.MaxSpecies) || info.Species != species ||
            info.OtId != trainer.OtId || info.Exp != experience || info.Friendship != stats.Friendship ||
            !info.Moves.SequenceEqual(moves) || info.Ivs.Where(pair => Gen3Constants.StatNames.Contains(pair.Key)).Any(pair => pair.Value != 31))
            throw new InvalidOperationException("Created box record semantic readback did not match the requested defaults.");
        Console.WriteLine($"Created readback OK: box{box:00}-{slot:00} address=0x{address:X8} species={info.Species} exp={info.Exp} friendship={info.Friendship} moves={string.Join(',', info.Moves)}");
    }
    finally
    {
        bridge.WriteRangeVerified(address, original);
    }

    var restored = bridge.Read(address, original.Length);
    if (!restored.AsSpan().SequenceEqual(original))
        throw new InvalidOperationException("Empty target slot restore did not match its original bytes.");
    Console.WriteLine("Restore readback OK: target slot is the original 58 zero bytes.");
    return 0;
}

static int PartySetBasic(string[] args)
{
    var profile = CurrentProfile();
    GameRuntimeAdapterCatalog.ForProfile(profile).EnsureCanWrite(GameDataSurface.Party, live: true);
    var db = Db();
    var slot = ValidateSlot(args);
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    var (baseAddr, addr, mon) = ReadWritableMon(bridge, args, slot);
    var before = mon.GetInfo();
    var hpArg = GetArg(args, "--hp", "");
    ushort? hp = hpArg == "" ? null : (hpArg.Equals("max", StringComparison.OrdinalIgnoreCase) ? before.MaxHp : (ushort)ParseInt(hpArg));
    mon.SetGrowth(GetUShortArg(args, "--species"), GetUShortArg(args, "--item"), GetUInt32Arg(args, "--exp"), GetByteArg(args, "--friendship"), GetByteArg(args, "--pp-bonuses"));
    mon.SetUnencrypted(hp, GetUShortArg(args, "--max-hp"), GetUInt32Arg(args, "--status"), GetByteArg(args, "--level"));
    var after = mon.GetInfo();
    var summary = $"Will write slot {slot} basic fields at 0x{addr:X8}\n" +
                  $"  before species/item/exp/friendship/hp={before.Species}({db.NameOf("species", before.Species)})/{before.Item}/{before.Exp}/{before.Friendship}/{before.Hp}\n" +
                  $"  after  species/item/exp/friendship/hp={after.Species}({db.NameOf("species", after.Species)})/{after.Item}/{after.Exp}/{after.Friendship}/{after.Hp}";
    if (Confirm(args, summary))
    {
        WriteMon(profile, bridge, baseAddr, addr, mon);
        Console.WriteLine($"Wrote slot {slot}");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static ushort?[] ParseNullableUShortList(string[] values, int count)
{
    var result = Enumerable.Repeat<ushort?>(null, count).ToArray();
    for (var i = 0; i < Math.Min(count, values.Length); i++)
    {
        if (values[i] is "-" or "keep" or "KEEP") continue;
        result[i] = (ushort)ParseInt(values[i]);
    }
    return result;
}

static byte?[] ParseNullableByteList(string[] values, int count)
{
    var result = Enumerable.Repeat<byte?>(null, count).ToArray();
    for (var i = 0; i < Math.Min(count, values.Length); i++)
    {
        if (values[i] is "-" or "keep" or "KEEP") continue;
        var value = ParseInt(values[i]);
        if (value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(values), "value must be 0..255");
        result[i] = (byte)value;
    }
    return result;
}

static int PartySetMoves(string[] args)
{
    var profile = CurrentProfile();
    GameRuntimeAdapterCatalog.ForProfile(profile).EnsureCanWrite(GameDataSurface.Party, live: true);
    var slot = ValidateSlot(args);
    var moveValues = ValuesAfter(args, "--moves");
    var ppValues = ValuesAfter(args, "--pp");
    if (moveValues.Length == 0 && ppValues.Length == 0) throw new ArgumentException("Supply --moves and/or --pp");
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    var (baseAddr, addr, mon) = ReadWritableMon(bridge, args, slot);
    var before = mon.GetInfo();
    mon.SetMoves(moveValues.Length == 0 ? null : ParseNullableUShortList(moveValues, 4), ppValues.Length == 0 ? null : ParseNullableByteList(ppValues, 4));
    var after = mon.GetInfo();
    var summary = $"Will write slot {slot} moves at 0x{addr:X8}\n" +
                  $"  before moves=[{string.Join(", ", before.Moves)}] pp=[{string.Join(", ", before.Pp)}]\n" +
                  $"  after  moves=[{string.Join(", ", after.Moves)}] pp=[{string.Join(", ", after.Pp)}]";
    if (Confirm(args, summary))
    {
        WriteMon(profile, bridge, baseAddr, addr, mon);
        Console.WriteLine($"Wrote slot {slot}");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static Dictionary<string, byte> ByteStatArgs(string[] args)
{
    var values = new Dictionary<string, byte>();
    foreach (var name in Gen3Constants.StatNames)
    {
        var value = GetByteArg(args, "--" + name);
        if (value is not null) values[name] = value.Value;
    }
    return values;
}

static int PartySetEvs(string[] args)
{
    var profile = CurrentProfile();
    GameRuntimeAdapterCatalog.ForProfile(profile).EnsureCanWrite(GameDataSurface.Party, live: true);
    var slot = ValidateSlot(args);
    var values = ByteStatArgs(args);
    if (values.Count == 0) throw new ArgumentException("Supply at least one EV field");
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    var (baseAddr, addr, mon) = ReadWritableMon(bridge, args, slot);
    var before = mon.GetInfo();
    var newSum = before.Evs.Select((v, i) => values.TryGetValue(Gen3Constants.StatNames[i], out var replacement) ? replacement : v).Sum(x => x);
    if (newSum > 510 && !HasArg(args, "--force")) throw new InvalidOperationException($"EV sum would be {newSum}; use --force to exceed 510");
    mon.SetEvs(values);
    RecalculateLiveStats(mon, args);
    var after = mon.GetInfo();
    var summary = $"Will write slot {slot} EVs at 0x{addr:X8}\n" +
                  $"  before evs=[{string.Join(", ", before.Evs)}] sum={before.Evs.Sum(x => x)}\n" +
                  $"  after  evs=[{string.Join(", ", after.Evs)}] sum={after.Evs.Sum(x => x)} stats HP {after.Hp}/{after.MaxHp} Atk {after.Attack} Def {after.Defense} Spe {after.Speed} SpA {after.SpAttack} SpD {after.SpDefense}";
    if (Confirm(args, summary))
    {
        WriteMon(profile, bridge, baseAddr, addr, mon);
        Console.WriteLine($"Wrote slot {slot}");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static int PartySetIvs(string[] args)
{
    var profile = CurrentProfile();
    GameRuntimeAdapterCatalog.ForProfile(profile).EnsureCanWrite(GameDataSurface.Party, live: true);
    var slot = ValidateSlot(args);
    var values = new Dictionary<string, int>();
    foreach (var name in Gen3Constants.StatNames)
    {
        var idx = Array.IndexOf(args, "--" + name);
        if (idx >= 0 && idx + 1 < args.Length)
        {
            var value = ParseInt(args[idx + 1]);
            if (value is < 0 or > 31) throw new ArgumentOutOfRangeException(name, "IV must be 0..31");
            values[name] = value;
        }
    }
    foreach (var name in new[] { "ability", "egg" })
    {
        var idx = Array.IndexOf(args, "--" + name);
        if (idx >= 0 && idx + 1 < args.Length)
        {
            var value = ParseInt(args[idx + 1]);
            if (name == "ability")
            {
                if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(name, "ability slot must be 0..2");
            }
            else if (value is not (0 or 1)) throw new ArgumentOutOfRangeException(name, "value must be 0 or 1");
            values[name] = value;
        }
    }
    if (values.Count == 0) throw new ArgumentException("Supply at least one IV field");
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    var (baseAddr, addr, mon) = ReadWritableMon(bridge, args, slot);
    var before = mon.GetInfo();
    mon.SetIvs(values);
    RecalculateLiveStats(mon, args);
    var after = mon.GetInfo();
    var summary = $"Will write slot {slot} IVs at 0x{addr:X8}\n" +
                  $"  before ivs=[{string.Join(", ", before.Ivs.Select(kv => $"{kv.Key}={kv.Value}"))}]\n" +
                  $"  after  ivs=[{string.Join(", ", after.Ivs.Select(kv => $"{kv.Key}={kv.Value}"))}] stats HP {after.Hp}/{after.MaxHp} Atk {after.Attack} Def {after.Defense} Spe {after.Speed} SpA {after.SpAttack} SpD {after.SpDefense}";
    if (Confirm(args, summary))
    {
        WriteMon(profile, bridge, baseAddr, addr, mon);
        Console.WriteLine($"Wrote slot {slot}");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static int PartyHeal(string[] args)
{
    var profile = CurrentProfile();
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    runtime.EnsureCanWrite(GameDataSurface.Party, live: true);
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    var baseAddr = ResolveWritablePartyBase(args, bridge);
    var slots = GetUIntArg(args, "--slot") is uint slot ? [checked((int)slot)] : Enumerable.Range(1, Gen3Constants.PartySlots).ToArray();
    var changed = new List<(int Slot, uint Addr, PartyPokemon Mon, ushort OldHp, ushort MaxHp, uint Status)>();
    foreach (var s in slots)
    {
        if (s is < 1 or > 6) throw new ArgumentOutOfRangeException("--slot", "slot must be 1..6");
        var addr = SlotAddress(baseAddr, s);
        var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize), runtime.PartyLayout);
        if (mon.IsEmpty) continue;
        var info = mon.GetInfo();
        if (info.MaxHp == 0 || info.MaxHp > 999) continue;
        mon.SetUnencrypted(info.MaxHp, null, 0, null);
        changed.Add((s, addr, mon, info.Hp, info.MaxHp, info.Status));
    }
    if (changed.Count == 0)
    {
        Console.WriteLine("No valid slots to heal");
        return 0;
    }
    var summary = "Will heal:\n" + string.Join("\n", changed.Select(c => $"  slot {c.Slot}: HP {c.OldHp}/{c.MaxHp}, status 0x{c.Status:X8} -> 0"));
    if (Confirm(args, summary))
    {
        foreach (var c in changed) WriteMon(profile, bridge, baseAddr, c.Addr, c.Mon);
        Console.WriteLine($"Wrote {changed.Count} slot(s)");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static int SpeciesStats(string[] args)
{
    var db = Db();
    foreach (var text in ValuesAfter(args, "--species", "--ids"))
    {
        var id = ParseInt(text);
        var s = ReadSpeciesStatsFromDb(db, id);
        Console.WriteLine($"Species {id} ({db.NameOf("species", id)})");
        Console.WriteLine($"  stats HP {s.Hp} / Atk {s.Attack} / Def {s.Defense} / Spe {s.Speed} / SpA {s.SpAttack} / SpD {s.SpDefense} / BST {s.Bst}");
        Console.WriteLine($"  abilities {s.Ability1}({db.NameOf("abilities", s.Ability1)}) / {s.Ability2}({db.NameOf("abilities", s.Ability2)})" + (s.Ability3 is null ? "" : $" / {s.Ability3}({db.NameOf("abilities", s.Ability3.Value)})"));
        Console.WriteLine($"  items {s.Item1}({(s.Item1 == 0 ? "无" : db.NameOf("items", s.Item1))}) / {s.Item2}({(s.Item2 == 0 ? "无" : db.NameOf("items", s.Item2))})");
    }
    return 0;
}

static int MoveStats(string[] args)
{
    var db = Db();
    foreach (var text in ValuesAfter(args, "--moves", "--ids"))
    {
        var id = ParseInt(text);
        var m = ReadMoveDataFromDb(db, id);
        Console.WriteLine($"Move {id} ({db.NameOf("moves", id)})");
        Console.WriteLine($"  power {m.Power} type {m.Type}({MoveDataReader.TypeName(m.Type)}) accuracy {m.Accuracy} pp {m.Pp} category {m.Category}({MoveDataReader.CategoryName(m.Category)}) priority {m.Priority}");
        Console.WriteLine($"  effect {m.Effect}({db.NameOf("move_effects", m.Effect)}) chance {m.SecondaryEffectChance} target=0x{m.Target:X4} flags=0x{m.Flags:X4} zPower={m.ZMovePower} raw={Convert.ToHexString(m.Raw)}");
    }
    return 0;
}

static int ItemStats(string[] args)
{
    var db = Db();
    foreach (var text in ValuesAfter(args, "--items", "--ids"))
    {
        var id = ParseInt(text);
        var item = ReadItemDataFromDb(db, id);
        var effectName = item.HoldEffect == 0 ? "无" : db.NameOf("item_effects", item.HoldEffect);
        Console.WriteLine($"Item {id} ({db.NameOf("items", id)})");
        Console.WriteLine($"  itemId {item.ItemId} price {item.Price} holdEffect {item.HoldEffect}({effectName}) holdParam {item.HoldEffectParam}");
        Console.WriteLine($"  pocket {item.Pocket}({ItemDataReader.PocketName(item.Pocket)}) type {item.Type} importance {item.Importance} exitsBag {item.ExitsBagOnUse} battleUsage {item.BattleUsage}");
        Console.WriteLine($"  desc=0x{item.DescriptionPointer:X8} fieldUse=0x{item.FieldUseFunction:X8} battleUse=0x{item.BattleUseFunction:X8} secondary=0x{item.SecondaryId:X8}");
    }
    return 0;
}

static int ProfileSelfTest()
{
    var profile = CurrentProfile();
    ProfileIsolationSelfTest.Verify(profile);
    Console.WriteLine($"Profile isolation OK: {profile.Id}");
    return 0;
}

static int ProfileDataVerify()
{
    var profile = CurrentProfile();
    var db = Db();
    var errors = new List<string>();
    var warnings = new List<string>();

    var romPath = Path.GetFullPath(Path.Combine(RootDir(), "..", "rom", profile.RomIdentity.FileName));
    if (!File.Exists(romPath))
    {
        errors.Add($"source ROM missing: {romPath}");
    }
    else
    {
        var rom = File.ReadAllBytes(romPath);
        var sha256 = Convert.ToHexString(SHA256.HashData(rom)).ToLowerInvariant();
        var title = Encoding.ASCII.GetString(rom, 0xA0, 12).TrimEnd('\0', ' ');
        var gameCode = Encoding.ASCII.GetString(rom, 0xAC, 4);
        Console.WriteLine($"  source ROM                   {rom.Length} bytes / {sha256}");
        if (rom.Length != profile.RomIdentity.RomSize) errors.Add($"ROM size: expected {profile.RomIdentity.RomSize}, got {rom.Length}");
        if (!string.Equals(sha256, profile.RomIdentity.Sha256, StringComparison.OrdinalIgnoreCase)) errors.Add("ROM SHA-256 does not match profile");
        if (!string.Equals(title, profile.RomIdentity.HeaderTitle, StringComparison.Ordinal)) errors.Add($"ROM title: expected {profile.RomIdentity.HeaderTitle}, got {title}");
        if (!string.Equals(gameCode, profile.RomIdentity.GameCode, StringComparison.Ordinal)) errors.Add($"ROM game code: expected {profile.RomIdentity.GameCode}, got {gameCode}");
        foreach (var fingerprint in profile.RomIdentity.LiveFingerprints)
        {
            var expected = Convert.FromHexString(fingerprint.Hex);
            if (fingerprint.Offset < 0 || fingerprint.Offset + expected.Length > rom.Length ||
                !rom.AsSpan(fingerprint.Offset, expected.Length).SequenceEqual(expected))
                errors.Add($"ROM fingerprint mismatch at 0x{fingerprint.Offset:X}");
        }
    }

    void RequireCount(string table, int expected)
    {
        var actual = db.Table(table).Count;
        Console.WriteLine($"  {table,-28} {actual,5} / {expected}");
        if (actual != expected) errors.Add($"{table}: expected {expected}, got {actual}");
    }

    var speciesRows = db.Table("species");
    RequireCount("species", profile.DataVerification.VisibleSpeciesCount);
    foreach (var id in speciesRows.Keys.Where(id => id < 1 || id > profile.Limits.MaxSpecies))
        errors.Add($"species: id {id} is outside 1..{profile.Limits.MaxSpecies}");

    var namedStatsIds = db.Table("species_stats").Where(row =>
    {
        var fields = row.Value.Split('\t');
        return fields.Length >= 6 && fields.Take(6).Any(field => int.TryParse(field, out var value) && value > 0);
    }).Select(row => row.Key);
    foreach (var id in namedStatsIds.Where(id => !speciesRows.ContainsKey(id)))
        errors.Add($"species: nonzero species_stats row {id} has no display name");
    RequireCount("moves", profile.Limits.MaxMove);
    RequireCount("items", profile.Limits.MaxItem + 1);
    RequireCount("abilities", profile.Limits.MaxAbility + 1);
    RequireCount("move_data", profile.RomTables.Moves.Count);
    RequireCount("item_data", profile.RomTables.Items.Count);
    RequireCount("move_descriptions", profile.Limits.MaxMove);
    RequireCount("item_descriptions", profile.RomTables.Items.Count);
    RequireCount("ability_descriptions", profile.Limits.MaxAbility + 1);
    RequireCount("species_evolutions", profile.Limits.MaxSpecies);
    RequireCount("species_level_moves", profile.Limits.MaxSpecies);
    RequireCount("experience", profile.RomTables.Experience.Count);
    if (profile.RomTables.MachineMoves is not null)
        RequireCount("machine_moves", profile.RomTables.MachineMoves.Count);
    if (profile.RomTables.MachineCompatibility is not null)
    {
        RequireCount("species_machine_moves", profile.Limits.MaxSpecies);
        RequireCount("species_machine_compatibility", profile.Limits.MaxSpecies);
    }
    if (profile.RomTables.TutorMoves is not null)
        RequireCount("tutor_moves", profile.RomTables.TutorMoves.Count);
    if (profile.RomTables.TutorCompatibility is not null)
    {
        RequireCount("species_tutor_moves", profile.Limits.MaxSpecies);
        RequireCount("species_tutor_compatibility", profile.Limits.MaxSpecies);
    }
    if (profile.RomTables.EggMoves is not null)
        RequireCount("species_egg_moves", profile.Limits.MaxSpecies);
    if (profile.RomTables.WildEncounters is not null)
    {
        RequireCount("species_encounters", profile.Limits.MaxSpecies);
        var encounters = db.Table("wild_encounters").Count;
        Console.WriteLine($"  {"wild_encounters",-28} {encounters,5} rows");
        if (encounters == 0) errors.Add("wild_encounters: missing or empty");
    }

    if (profile.Id == "pokemon-radical-red-41-cn")
    {
        void RequireValue(string table, int id, string expected)
        {
            var actual = db.NameOf(table, id);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                errors.Add($"{table}[{id}]: expected {expected}, got {actual}");
        }
        RequireValue("species_evolutions", 1, "4,16,2,0");
        RequireValue("species_evolutions", 6, "254,534,870,0;254,535,871,0");
        RequireValue("species_evolutions_raw", 869, "254,0,3,0");
        RequireValue("species_level_moves", 1, "1:33;3:45;7:73;9:345;13:77;13:79;15:124;19:72;21:36;25:74;27:38;31:475;33:235;37:396");
        RequireValue("species_forms", 869, "Mega");
        RequireValue("species_forms", 1020, "阿罗拉形态");
        RequireValue("species_forms", 1233, "超极巨化");
        RequireValue("move_rom_names", 848, "Z招式");
        RequireValue("moves", 848, "通用Z·一般（物理）");
        RequireValue("moves", 594, "磨砺");
        if (db.Table("moves").GroupBy(row => row.Value).Any(group => group.Count() > 1))
            errors.Add("moves: semantic display names are not unique");
    }

    foreach (var table in new[] { "species", "moves", "items", "abilities" })
    {
        var placeholders = db.Table(table)
            .Where(row => row.Value.StartsWith('#') || row.Value.Contains('□'))
            .Select(row => row.Key)
            .ToArray();
        if (placeholders.Length > 0)
            errors.Add($"{table}: unresolved display rows {string.Join(',', placeholders.Take(12))}{(placeholders.Length > 12 ? "..." : "")}");
    }

    foreach (var table in new[] { "move_effects", "item_effects", "species_national_dex", "species_forms", "maps", "map_sections", "map_warps" })
    {
        var lineCount = db.Lines(table).Count(line => !string.IsNullOrWhiteSpace(line));
        Console.WriteLine($"  {table,-28} {lineCount,5} rows");
        if (lineCount == 0) errors.Add($"{table}: missing or empty");
    }

    if (profile.Graphics.SpritesVerified)
    {
        var assetRoot = Path.Combine(RootDir(), "RocketTool.Avalonia", "Assets", profile.Graphics.SpriteAssetRoot.Replace('/', Path.DirectorySeparatorChar));
        var iconRoot = Path.Combine(RootDir(), "RocketTool.Avalonia", "Assets", profile.Graphics.IconAssetRoot.Replace('/', Path.DirectorySeparatorChar));
        var sprites = Directory.Exists(assetRoot) ? Directory.EnumerateFiles(assetRoot, "*.png").Count() : 0;
        var icons = Directory.Exists(iconRoot) ? Directory.EnumerateFiles(iconRoot, "*.png").Count() : 0;
        Console.WriteLine($"  front sprites                {sprites,5}");
        Console.WriteLine($"  menu icons                   {icons,5}");
        if (sprites == 0) errors.Add("verified front sprite asset directory is missing or empty");
        var numericIcons = Directory.Exists(iconRoot)
            ? Directory.EnumerateFiles(iconRoot, "*.png").Count(path => int.TryParse(Path.GetFileNameWithoutExtension(path), out _))
            : 0;
        if (profile.DataVerification.VerifiedMenuIconCount > 0 && numericIcons != profile.DataVerification.VerifiedMenuIconCount)
            errors.Add($"menu icons: expected {profile.DataVerification.VerifiedMenuIconCount} verified profile icons, got {numericIcons}");
        var egg = Path.Combine(assetRoot, $"{profile.Graphics.EggSpeciesId:0000}.png");
        if (!File.Exists(egg)) errors.Add($"egg front sprite missing: {profile.Graphics.EggSpeciesId}");
    }

    if (db.Table("map_sections").Values.Any(name => name.StartsWith("区域", StringComparison.Ordinal)))
        warnings.Add("current ROM has no active name pointers for expanded map ids; neutral region labels are expected");

    foreach (var warning in warnings) Console.WriteLine("WARN: " + warning);
    if (errors.Count > 0)
        throw new InvalidOperationException("Profile database verification failed:\n  " + string.Join("\n  ", errors));

    Console.WriteLine($"Profile database OK: {profile.Id}");
    return 0;
}

static (uint SaveBlock1, uint SaveBlock2, byte[] NameBytes, uint OtId, uint MoneyKey, uint EncryptedMoney, uint Money) ReadLiveTrainer(
    MgbaBridgeClient bridge,
    GameProfile profile)
{
    static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
    bool Ewram(uint address, int length)
        => address >= profile.Memory.EwramBase &&
           (ulong)address + (uint)length <= (ulong)profile.Memory.EwramBase + (uint)profile.Memory.EwramSize;

    var saveBlock1 = U32(bridge.Read(profile.Memory.SaveBlock1PointerAddress, 4));
    var saveBlock2 = U32(bridge.Read(profile.Memory.SaveBlock2PointerAddress, 4));
    if (!Ewram(saveBlock1, checked((int)profile.Memory.SaveBlock1MoneyOffset + 4)))
        throw new InvalidOperationException("SaveBlock1 pointer or money field is outside current Profile EWRAM.");
    if (!Ewram(saveBlock2, checked((int)profile.Memory.SaveBlock2EncryptionKeyOffset + 4)))
        throw new InvalidOperationException("SaveBlock2 pointer or encryption key is outside current Profile EWRAM.");

    var header = bridge.Read(saveBlock2, profile.Memory.SaveBlock2HeaderLength);
    var name = header.AsSpan(0, profile.Memory.PlayerNameLength).ToArray();
    var otId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(profile.Memory.SaveBlock2PlayerOtIdOffset, 4));
    if (otId == 0 || !name.Contains((byte)0xFF))
        throw new InvalidOperationException("SaveBlock2 trainer header failed the current Profile safety checks.");

    var key = U32(bridge.Read(saveBlock2 + profile.Memory.SaveBlock2EncryptionKeyOffset, 4));
    var encrypted = U32(bridge.Read(saveBlock1 + profile.Memory.SaveBlock1MoneyOffset, 4));
    var money = encrypted ^ key;
    if (money > profile.Runtime.MaxTrainerMoney)
        throw new InvalidOperationException($"Decrypted money {money} exceeds Profile limit {profile.Runtime.MaxTrainerMoney}.");
    return (saveBlock1, saveBlock2, name, otId, key, encrypted, money);
}

static string DecodeGameText(ReadOnlySpan<byte> raw, ModifierDatabase db)
{
    var map = db.Table("game_text_chars");
    var output = new StringBuilder();
    for (var i = 0; i < raw.Length;)
    {
        var value = raw[i];
        if (value is 0 or 0xFF) break;
        if (i + 1 < raw.Length && raw[i + 1] != 0xFF && map.TryGetValue(value << 8 | raw[i + 1], out var pair))
        {
            output.Append(pair);
            i += 2;
        }
        else if (value is >= 0xA1 and <= 0xAA)
        {
            output.Append((char)('0' + value - 0xA1));
            i++;
        }
        else if (value is >= 0xBB and <= 0xD4)
        {
            output.Append((char)('A' + value - 0xBB));
            i++;
        }
        else if (value == 0xBA)
        {
            output.Append('-');
            i++;
        }
        else
        {
            output.Append('□');
            i++;
        }
    }
    return output.ToString();
}

static byte[] EncodeGameText(string text, int length, ModifierDatabase db)
{
    if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("名字不能为空。");
    var encode = db.Table("game_text_chars")
        .Where(row => row.Value.Length == 1 && row.Key is >= 0 and <= 0xFFFF)
        .GroupBy(row => row.Value[0])
        .ToDictionary(group => group.Key, group => (ushort)group.First().Key);
    var output = new List<byte>(length);
    foreach (var ch in text.Trim().ToUpperInvariant())
    {
        if (ch is >= '0' and <= '9') output.Add((byte)(0xA1 + ch - '0'));
        else if (ch is >= 'A' and <= 'Z') output.Add((byte)(0xBB + ch - 'A'));
        else if (ch == '-') output.Add(0xBA);
        else if (encode.TryGetValue(ch, out var code))
        {
            output.Add((byte)(code >> 8));
            output.Add((byte)code);
        }
        else throw new InvalidOperationException($"当前 Profile 字库不能安全编码“{ch}”。");
        if (output.Count > length - 1)
            throw new InvalidOperationException($"名字内部编码超过 {length - 1} 字节，必须保留结束符。");
    }
    var result = Enumerable.Repeat((byte)0xFF, length).ToArray();
    output.CopyTo(result);
    return result;
}

static int TrainerProbe(string[] args)
{
    var profile = CurrentProfile();
    var runtime = CurrentRuntime();
    runtime.EnsureCanRead(GameDataSurface.Trainer, live: true);
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = ConnectForProfile(profile, host, port);
    var value = ReadLiveTrainer(bridge, profile);
    Console.WriteLine($"Trainer: {DecodeGameText(value.NameBytes, Db())}");
    Console.WriteLine($"  rawName={Convert.ToHexString(value.NameBytes)} publicId={(ushort)value.OtId} secretId={(ushort)(value.OtId >> 16)}");
    Console.WriteLine($"  saveBlock1=0x{value.SaveBlock1:X8} saveBlock2=0x{value.SaveBlock2:X8}");
    Console.WriteLine($"  money={value.Money} encrypted=0x{value.EncryptedMoney:X8} key=0x{value.MoneyKey:X8}");
    return 0;
}

static int TrainerSetMoney(string[] args)
{
    var profile = CurrentProfile();
    var runtime = CurrentRuntime();
    runtime.EnsureCanWrite(GameDataSurface.Trainer, live: true);
    var money = RequiredIntArg(args, "--money");
    if (money < 0 || money > profile.Runtime.MaxTrainerMoney)
        throw new ArgumentOutOfRangeException("--money", $"money must be 0..{profile.Runtime.MaxTrainerMoney}");
    using var bridge = ConnectForProfile(profile, GetArg(args, "--host", "127.0.0.1"), GetIntArg(args, "--port", 8765));
    var before = ReadLiveTrainer(bridge, profile);
    if (!Confirm(args, $"将金钱从 {before.Money} 改为 {money}。首次验证前请先创建 mGBA 即时存档。")) return 0;
    var encoded = (uint)money ^ before.MoneyKey;
    var raw = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(raw, encoded);
    bridge.WriteRangeVerified(before.SaveBlock1 + profile.Memory.SaveBlock1MoneyOffset, raw);
    var after = ReadLiveTrainer(bridge, profile);
    if (after.Money != money) throw new InvalidOperationException("金钱写入后的解密回读不一致。");
    Console.WriteLine($"Verified money write: {before.Money} -> {after.Money}");
    return 0;
}

static int TrainerSetName(string[] args)
{
    var profile = CurrentProfile();
    var runtime = CurrentRuntime();
    runtime.EnsureCanWrite(GameDataSurface.Trainer, live: true);
    var name = GetArg(args, "--name", "");
    var db = Db();
    var encoded = EncodeGameText(name, profile.Memory.PlayerNameLength, db);
    using var bridge = ConnectForProfile(profile, GetArg(args, "--host", "127.0.0.1"), GetIntArg(args, "--port", 8765));
    var before = ReadLiveTrainer(bridge, profile);
    if (!Confirm(args, $"将训练家名字从 {DecodeGameText(before.NameBytes, db)} 改为 {name}。首次验证前请先创建 mGBA 即时存档。")) return 0;
    bridge.WriteRangeVerified(before.SaveBlock2, encoded);
    var after = ReadLiveTrainer(bridge, profile);
    if (!after.NameBytes.AsSpan().SequenceEqual(encoded)) throw new InvalidOperationException("训练家名字写入回读不一致。");
    Console.WriteLine($"Verified trainer name write: {DecodeGameText(before.NameBytes, db)} -> {DecodeGameText(after.NameBytes, db)}");
    return 0;
}

static int TrainerVerifyWrite(string[] args)
{
    var profile = CurrentProfile();
    if (!string.Equals(profile.Id, "pokemon-radical-red-41-cn", StringComparison.Ordinal))
        throw new InvalidOperationException("trainer-verify-write is currently scoped to the independently reversed Radical Red 4.1 CN Profile.");
    CurrentRuntime().EnsureCanRead(GameDataSurface.Trainer, live: true);
    if (!HasArg(args, "--yes"))
        throw new InvalidOperationException("该命令会临时修改并恢复训练家名字和金钱；请先创建 mGBA 即时存档，确认后加 --yes。");

    using var bridge = ConnectForProfile(profile, GetArg(args, "--host", "127.0.0.1"), GetIntArg(args, "--port", 8765));
    var db = Db();
    var before = ReadLiveTrainer(bridge, profile);
    var originalName = before.NameBytes.ToArray();
    var originalMoneyRaw = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(originalMoneyRaw, before.EncryptedMoney);
    var testName = EncodeGameText("CROSQ", profile.Memory.PlayerNameLength, db);
    if (testName.AsSpan().SequenceEqual(originalName))
        testName = EncodeGameText("CROSR", profile.Memory.PlayerNameLength, db);
    var testMoney = before.Money == profile.Runtime.MaxTrainerMoney ? before.Money - 1 : before.Money + 1;
    var testMoneyRaw = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(testMoneyRaw, testMoney ^ before.MoneyKey);

    Exception? failure = null;
    try
    {
        bridge.WriteRangeVerified(before.SaveBlock2, testName);
        bridge.WriteRangeVerified(before.SaveBlock1 + profile.Memory.SaveBlock1MoneyOffset, testMoneyRaw);
        var changed = ReadLiveTrainer(bridge, profile);
        if (!changed.NameBytes.AsSpan().SequenceEqual(testName) || changed.Money != testMoney)
            throw new InvalidOperationException("Trainer verification write did not read back the intended name and money.");
        Console.WriteLine($"Verified temporary trainer write: {DecodeGameText(originalName, db)}/{before.Money} -> {DecodeGameText(changed.NameBytes, db)}/{changed.Money}");
    }
    catch (Exception ex)
    {
        failure = ex;
    }
    finally
    {
        bridge.WriteRangeVerified(before.SaveBlock2, originalName);
        bridge.WriteRangeVerified(before.SaveBlock1 + profile.Memory.SaveBlock1MoneyOffset, originalMoneyRaw);
    }

    var restored = ReadLiveTrainer(bridge, profile);
    if (!restored.NameBytes.AsSpan().SequenceEqual(originalName) || restored.EncryptedMoney != before.EncryptedMoney || restored.Money != before.Money)
        throw new InvalidOperationException("Trainer verification restore did not match the complete original name/money bytes.", failure);
    if (failure is not null) throw new InvalidOperationException("Trainer verification failed; original fields were restored.", failure);
    Console.WriteLine($"Verified trainer restore: {DecodeGameText(restored.NameBytes, db)} money={restored.Money}; original bytes match.");
    return 0;
}

static int PartyVerifyCreate(string[] args)
{
    var profile = CurrentProfile();
    if (!string.Equals(profile.Id, "pokemon-radical-red-41-cn", StringComparison.Ordinal))
        throw new InvalidOperationException("party-verify-create is currently scoped to the independently reversed Radical Red 4.1 CN Profile.");
    var runtime = CurrentRuntime();
    runtime.EnsureCanWrite(GameDataSurface.Party, live: true);
    if (!HasArg(args, "--yes"))
        throw new InvalidOperationException("该命令会临时清出一个队伍槽、创建图鉴宝可梦并完整恢复；请先创建 mGBA 即时存档，确认后加 --yes。");
    var species = GetIntArg(args, "--species", 1);

    using var bridge = ConnectForProfile(profile, GetArg(args, "--host", "127.0.0.1"), GetIntArg(args, "--port", 8765));
    var baseAddr = ResolveWritablePartyBase(args, bridge);
    var (countAddress, originalCount) = ReadLivePartyCount(profile, bridge, baseAddr);
    if (originalCount == 0)
        throw new InvalidOperationException("当前队伍为空，无法用已有队伍定位证据执行安全创建测试。");
    var targetSlot = originalCount == Gen3Constants.PartySlots ? Gen3Constants.PartySlots : originalCount + 1;
    var targetAddress = SlotAddress(baseAddr, targetSlot);
    var originalRecord = bridge.Read(targetAddress, Gen3Constants.PartyMonSize);
    if (originalCount < Gen3Constants.PartySlots && originalRecord.Any(value => value != 0))
        throw new InvalidOperationException($"队伍槽位 {targetSlot} 不为空，拒绝覆盖。");

    var trainer = ReadLiveTrainer(bridge, profile);
    var trainerInfo = new Gen3SaveTrainerInfo(trainer.NameBytes, trainer.OtId, trainer.Money);
    var db = Db();
    var stats = ReadSpeciesStatsFromDb(db, species);
    var experience = new PokemonExperienceTable(db).ExperienceForLevel(5, stats.GrowthRate);
    var (expectedMoves, expectedPp) = BuildDexImportMoves(db, species, 5);
    var created = BuildDexImportParty(profile, db, trainerInfo, species);
    var intended = created.Raw.ToArray();
    Exception? failure = null;

    try
    {
        if (originalCount == Gen3Constants.PartySlots)
        {
            WriteMon(profile, bridge, baseAddr, targetAddress, new PartyPokemon(new byte[Gen3Constants.PartyMonSize], runtime.PartyLayout));
            WriteLivePartyCount(bridge, countAddress, originalCount - 1);
        }
        WriteMon(profile, bridge, baseAddr, targetAddress, created);
        WriteLivePartyCount(bridge, countAddress, targetSlot);
        var readback = bridge.Read(targetAddress, Gen3Constants.PartyMonSize);
        if (!readback.AsSpan().SequenceEqual(intended))
            throw new InvalidOperationException("Created live party record did not match the complete intended 100-byte record.");
        var info = new PartyPokemon(readback, runtime.PartyLayout).GetInfo();
        if (info.Species != species || info.Level != 5 || info.OtId != trainer.OtId ||
            info.Exp != experience || info.Friendship != stats.Friendship || info.Item != 0 || info.Status != 0 ||
            !info.Moves.SequenceEqual(expectedMoves) || !info.Pp.SequenceEqual(expectedPp) ||
            info.Ivs.Where(pair => Gen3Constants.StatNames.Contains(pair.Key)).Any(pair => pair.Value != 31) ||
            info.Ivs["ability"] != 0 || info.Evs.Any(value => value != 0) || info.MaxHp == 0)
            throw new InvalidOperationException("Created live party record semantic readback did not match current-Profile defaults.");
        Console.WriteLine($"Verified temporary live party import: slot={targetSlot} species={info.Species} level={info.Level} exp={info.Exp} moves={string.Join(',', info.Moves)} pp={string.Join(',', info.Pp)} hp={info.Hp}/{info.MaxHp}");
    }
    catch (Exception ex)
    {
        failure = ex;
    }
    finally
    {
        WriteMon(profile, bridge, baseAddr, targetAddress, new PartyPokemon(originalRecord, runtime.PartyLayout));
        WriteLivePartyCount(bridge, countAddress, originalCount);
    }

    var restoredRecord = bridge.Read(targetAddress, Gen3Constants.PartyMonSize);
    var restoredCount = bridge.Read(countAddress, 1)[0];
    if (!restoredRecord.AsSpan().SequenceEqual(originalRecord) || restoredCount != originalCount)
        throw new InvalidOperationException("Live party verification restore did not match the original record/count bytes.", failure);
    if (failure is not null) throw new InvalidOperationException("Live party import verification failed; original record/count were restored.", failure);
    Console.WriteLine($"Verified live party restore: slot={targetSlot} count={restoredCount}; original bytes match.");
    return 0;
}

static int ShopProbe(string[] args)
{
    var profile = CurrentProfile();
    var probe = profile.Runtime.ShopProbe;
    if (!probe.Enabled)
        throw new InvalidOperationException($"版本 {profile.DisplayName} 未验证商店内存探测，不能借用其他 Profile 的地址。");
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    var s = LiveMemoryProbe.ReadShopSnapshot(bridge, probe);
    Console.WriteLine($"Shop price/current lock field @ 0x{probe.ShopPriceAddress:X8}: {s.ShopPrice}");
    Console.WriteLine($"Shop first item field      @ 0x{probe.ShopFirstItemAddress:X8}: {s.ShopFirstItem}({(s.ShopFirstItem == 0 ? "无" : db.NameOf("items", s.ShopFirstItem))})");
    Console.WriteLine($"Sell price primary         @ 0x{probe.SellPricePrimaryAddress:X8}: {s.SellPricePrimary}");
    Console.WriteLine($"Sell price fallback        @ 0x{probe.SellPriceFallbackAddress:X8}: {s.SellPriceFallback}");
    return 0;
}

static int BagScan(string[] args)
{
    var profile = CurrentProfile();
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    runtime.EnsureCanRead(GameDataSurface.Bag, live: true);
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var limit = GetIntArg(args, "--limit", 12);
    var singleLimit = GetIntArg(args, "--single-limit", 12);
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    if (runtime.UsesFixedLiveBag)
    {
        var fixedQuantityKey = ReadLiveBagQuantityKey(bridge, profile);
        Console.WriteLine($"\n{profile.DisplayName} 固定背包口袋，qtyKey=0x{fixedQuantityKey:X4}");
        foreach (var area in profile.Runtime.LiveBag.Areas)
        {
            var raw = bridge.Read(area.Address, area.Capacity * 4);
            var printedHeader = false;
            var displaySlot = 0;
            for (var i = 0; i < area.Capacity; i++)
            {
                var item = U16(raw, i * 4);
                if (item == 0 || item > profile.Limits.MaxItem) continue;
                var quantity = (ushort)(U16(raw, i * 4 + 2) ^ fixedQuantityKey);
                if (quantity == 0) quantity = 1;
                if (!printedHeader)
                {
                    Console.WriteLine($"{area.Name} 0x{area.Address:X8} capacity={area.Capacity}");
                    printedHeader = true;
                }
                displaySlot++;
                Console.WriteLine($"  {displaySlot:00}. 0x{area.Address + (uint)(i * 4):X8}: {item}({db.NameOf("items", item)}) x{quantity}");
                if (displaySlot >= limit) break;
            }
            if (!printedHeader)
                Console.WriteLine($"{area.Name} 0x{area.Address:X8} capacity={area.Capacity}: empty");
        }
        return 0;
    }
    uint? bagBase = null;
    try { bagBase = BagScanner.LocateSaveBlockBase(bridge, profile, runtime.ScannedBagDefinitions); }
    catch { }
    var ewram = PartyScanner.ReadEwram(bridge, profile, (off, total) =>
    {
        if (off % 0x10000 == 0) Console.WriteLine($"read EWRAM 0x{off:X5}/0x{total:X5}");
    });
    var quantityKey = BagScanner.InferQuantityKey(ewram);
    var pockets = BagScanner.FindLivePockets(
        ewram, profile.Memory.EwramBase, bagBase,
        runtime.BagPockets(true).Select(candidate => candidate.Id).ToArray(),
        PocketOfItem, candidate => runtime.IsKeyItemPocket(candidate, true),
        candidate => runtime.BagBatchCapacity(candidate, true),
        profile.Limits.MaxItem, profile.Limits.MaxBagQuantity).ToArray();
    Console.WriteLine($"\n背包口袋: {pockets.Length}，base={(bagBase is null ? "unknown" : $"0x{bagBase:X8}")} qtyKey=0x{quantityKey:X4}");
    foreach (var pocket in pockets)
    {
        Console.WriteLine($"{runtime.PocketName(pocket.Pocket, live: true)} 0x{pocket.StartAddress:X8} nonEmpty={pocket.NonEmptyCount} score={pocket.Score}");
        foreach (var slot in pocket.Slots.Take(limit))
        {
            Console.WriteLine($"  0x{slot.Address:X8}: {slot.ItemId}({db.NameOf("items", slot.ItemId)}) x{slot.Quantity}");
        }
    }
    if (bagBase is not null)
    {
        var baseOffset = checked((int)(bagBase.Value - profile.Memory.EwramBase));
        var endOffset = Math.Min(ewram.Length - 8, baseOffset + 0x4000);
        var singles = new List<(uint Address, ushort Item, ushort Quantity, int Pocket, int Offset)>();
        for (var off = baseOffset; off <= endOffset; off += 4)
        {
            var item = U16(ewram, off);
            var quantity = U16(ewram, off + 2);
            var nextItem = U16(ewram, off + 4);
            var nextQuantity = U16(ewram, off + 6);
            if (item == 0 ||
                item > profile.Limits.MaxItem ||
                quantity == 0 ||
                quantity > profile.Limits.MaxBagQuantity ||
                nextItem != 0 ||
                nextQuantity != 0)
            {
                continue;
            }
            singles.Add((profile.Memory.EwramBase + (uint)off, item, quantity, PocketOfItem(item), off - baseOffset));
        }

        if (singles.Count > 0)
        {
            Console.WriteLine($"\n一格背包候选: {singles.Count}（用于空背包/单一道具样本对照，不作为写入证据）");
            foreach (var slot in singles.Take(singleLimit))
            {
                Console.WriteLine($"  +0x{slot.Offset:X4} 0x{slot.Address:X8}: {slot.Item}({db.NameOf("items", slot.Item)}) x{slot.Quantity} pocket={slot.Pocket}");
            }
        }
    }
    return 0;

    static ushort U16(byte[] data, int offset) => (ushort)(data[offset] | data[offset + 1] << 8);

    int PocketOfItem(int item)
    {
        if (item <= 0) return -1;
        if (db.Table("item_pockets").TryGetValue(item, out var pocket) && int.TryParse(pocket, out var pocketId))
            return runtime.RemapItemPocket(pocketId);
        try { return runtime.RemapItemPocket(ReadItemDataFromDb(db, item).Pocket); }
        catch { return runtime.FallbackPocketOfItem(item); }
    }
}

static ushort ReadLiveBagQuantityKey(MgbaBridgeClient bridge, GameProfile profile)
{
    if (profile.Runtime.LiveBag.QuantityKeyMode != "save-block2-key-low16")
        throw new InvalidOperationException($"Profile {profile.Id} does not use a fixed live bag quantity key.");
    var saveBlock2 = BitConverter.ToUInt32(bridge.Read(profile.Memory.SaveBlock2PointerAddress, 4));
    return (ushort)(BitConverter.ToUInt32(bridge.Read(saveBlock2 + profile.Memory.SaveBlock2EncryptionKeyOffset, 4)) & 0xFFFF);
}

static int BagRead(string[] args)
{
    var profile = CurrentProfile();
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    runtime.EnsureCanRead(GameDataSurface.Bag, live: true);
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var addr = GetUIntArg(args, "--addr") ?? throw new ArgumentException("Missing --addr 0x...");
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    var (definition, maxQuantity) = ResolveLiveBagSlot(profile, runtime, bridge, addr);
    var slot = BagScanner.ReadSlot(bridge, addr, definition, maxQuantity);
    Console.WriteLine($"0x{slot.Address:X8}: item={slot.ItemId}({(slot.ItemId == 0 ? "无" : db.NameOf("items", slot.ItemId))}) qty={slot.Quantity} score={slot.Score} {slot.Note}");
    return 0;
}

static int BagSet(string[] args)
{
    var profile = CurrentProfile();
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    runtime.EnsureCanWrite(GameDataSurface.Bag, live: true);
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var addr = GetUIntArg(args, "--addr") ?? throw new ArgumentException("Missing --addr 0x...");
    var item = RequiredIntArg(args, "--item");
    var qty = RequiredIntArg(args, "--qty");
    if (item < 0 || item > profile.Limits.MaxItem) throw new ArgumentOutOfRangeException("--item", $"item must be 0..{profile.Limits.MaxItem}");
    using var bridge = ConnectForProfile(profile, host, port);
    Console.WriteLine(bridge.Welcome);
    var (definition, maxQuantity) = ResolveLiveBagSlot(profile, runtime, bridge, addr);
    if (qty < 0 || qty > maxQuantity) throw new ArgumentOutOfRangeException("--qty", $"qty must be 0..{maxQuantity}");
    var before = BagScanner.ReadSlot(bridge, addr, definition, maxQuantity);
    var summary = $"将写入背包槽 0x{addr:X8}\n" +
                  $"  原来: {before.ItemId}({(before.ItemId == 0 ? "无" : db.NameOf("items", before.ItemId))}) x{before.Quantity}\n" +
                  $"  新值: {item}({(item == 0 ? "无" : db.NameOf("items", item))}) x{qty}\n" +
                  $"编码: {profile.DisplayName}/{definition.Name}，数量{(definition.QuantityXor ? "使用 Profile 密钥" : "不加密")}。";
    if (Confirm(args, summary))
    {
        bridge.WriteRangeVerified(addr, BagScanner.EncodeSlot((ushort)item, (ushort)qty, definition.QuantityKey, definition.QuantityXor));
        var after = BagScanner.ReadSlot(bridge, addr, definition, maxQuantity);
        Console.WriteLine($"已写入: {after.ItemId}({(after.ItemId == 0 ? "无" : db.NameOf("items", after.ItemId))}) x{after.Quantity}");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static (BagPocketDefinition Definition, int MaxQuantity) ResolveLiveBagSlot(
    GameProfile profile,
    IGameRuntimeAdapter runtime,
    MgbaBridgeClient bridge,
    uint address)
{
    if ((address & 3) != 0) throw new InvalidOperationException("背包槽地址必须按 4 字节对齐。");
    if (runtime.UsesFixedLiveBag)
    {
        var area = profile.Runtime.LiveBag.Areas.FirstOrDefault(candidate =>
            address >= candidate.Address && address < candidate.Address + checked((uint)candidate.Capacity * 4U));
        if (area is null) throw new InvalidOperationException("地址不属于当前 Profile 的任何实时背包口袋，已拒绝访问。");
        var key = ReadLiveBagQuantityKey(bridge, profile);
        return (new BagPocketDefinition(area.Pocket, area.Name, 0, area.Capacity, true, key), profile.Limits.MaxBagQuantityForPocket(area.Pocket));
    }

    var baseAddress = BagScanner.LocateSaveBlockBase(bridge, profile, runtime.ScannedBagDefinitions);
    var definition = BagScanner.DefinitionForAddress(runtime.ScannedBagDefinitions, baseAddress, address)
                     ?? throw new InvalidOperationException("地址不属于当前 Profile 已验证的扫描背包口袋，已拒绝访问。");
    return (definition, profile.Limits.MaxBagQuantityForPocket(definition.Pocket));
}

static int SaveProbe(string[] args)
{
    var db = Db();
    var path = GetArg(args, "--save", "");
    if (string.IsNullOrWhiteSpace(path))
        path = ValuesAfter(args, "--path").FirstOrDefault() ?? "";
    if (string.IsNullOrWhiteSpace(path))
        throw new ArgumentException("Missing --save path");

    var profile = CurrentProfile();
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    if (!profile.Features.SaveEditing)
        throw new InvalidOperationException($"版本 {profile.DisplayName} 未启用存档读取。");
    var result = Gen3SaveReader.Read(path, profile);
    Console.WriteLine($"Save: {result.FileName}");
    Console.WriteLine($"  size={result.FileSize} slot={result.SaveSlot} saveIndex={result.SaveIndex} sections={result.ValidSectionCount}/14");
    Console.WriteLine($"  party={result.Party.Count} bag={result.Bag.Count} boxes={result.Boxes.Count}");
    if (result.Trainer is { } trainer)
        Console.WriteLine($"  trainerId={(ushort)trainer.OtId} secretId={(ushort)(trainer.OtId >> 16)} money={trainer.Money}");

    Console.WriteLine("\nWarnings/probes:");
    foreach (var warning in result.Warnings)
        Console.WriteLine("  " + warning);

    Console.WriteLine("\nParty:");
    for (var i = 0; i < result.Party.Count; i++)
    {
        var info = result.Party[i].GetInfo();
        Console.WriteLine($"  {i + 1}. {info.Species}({db.NameOf("species", info.Species)}) Lv{info.Level} item={info.Item}({(info.Item == 0 ? "无" : db.NameOf("items", info.Item))})");
        Console.WriteLine("     moves=" + string.Join(", ", info.Moves.Where(move => move != 0).Select(move => $"{move}({db.NameOf("moves", move)})")));
    }

    Console.WriteLine("\nBag:");
    foreach (var group in result.Bag.GroupBy(b => b.Pocket).OrderBy(g => g.Key))
    {
        Console.WriteLine($"  {runtime.PocketName(group.Key, live: false)} ({group.Count()}):");
        foreach (var entry in group.Take(80))
        {
            var qty = entry.Pocket == 8 ? "" : $" x{entry.Quantity}";
            Console.WriteLine($"    {entry.SlotInPocket:00}. off=0x{entry.SaveOffset:X} item={entry.ItemId}({db.NameOf("items", entry.ItemId)}){qty} {entry.Note}");
        }
    }

    Console.WriteLine("\nBoxes:");
    Console.WriteLine("  " + string.Join("；", result.Boxes
        .GroupBy(entry => entry.BoxNumber)
        .OrderBy(group => group.Key)
        .Select(group => $"箱{group.Key:00}={group.Count()}只")));
    foreach (var entry in result.Boxes)
    {
        var info = entry.Mon.GetInfo();
        Console.WriteLine($"  box{entry.BoxNumber:00}-{entry.SlotInBox:00} off=0x{entry.SaveOffset:X} {info.Species}({db.NameOf("species", info.Species)}) item={info.Item}({(info.Item == 0 ? "无" : db.NameOf("items", info.Item))})");
    }

    return 0;
}

static int SaveVerifyWrite(string[] args)
{
    var path = GetArg(args, "--save", "");
    var output = GetArg(args, "--output", "");
    var inPlace = HasArg(args, "--in-place");
    if (string.IsNullOrWhiteSpace(path) || !inPlace && string.IsNullOrWhiteSpace(output))
        throw new ArgumentException("Missing --save, or --output when --in-place is not used");
    if (!HasArg(args, "--yes"))
        throw new InvalidOperationException("该命令会生成测试存档副本；确认后请加 --yes。");

    var profile = CurrentProfile();
    if (!profile.Features.SaveEditing)
        throw new InvalidOperationException($"版本 {profile.DisplayName} 未启用存档读写。");
    var document = Gen3SaveReader.Open(path, profile);
    PartyMonInfo? info = null;
    if (document.Snapshot.Party.Count > 0)
    {
        var source = document.Snapshot.Party[0];
        var clone = new PartyPokemon(source.Raw, source.Layout);
        info = clone.GetInfo();
        clone.SetGrowth(friendship: (byte)(info.Friendship == 255 ? 254 : info.Friendship + 1));
        document.ReplacePartyPokemon(1, clone);
    }

    if (document.Snapshot.Bag.FirstOrDefault() is { } bag)
    {
        var targetQuantity = bag.QuantityXor && bag.Quantity < profile.Limits.MaxBagQuantityForPocket(bag.Pocket)
            ? (ushort)(bag.Quantity + 1)
            : bag.Quantity;
        document.ReplaceBagEntry(bag.SaveOffset, bag.ItemId, targetQuantity);
    }

    ProfileSaveScenarioCatalog.ForProfile(profile).ApplyVerificationChanges(document, info);

    if (!document.HasChanges)
        throw new InvalidOperationException("测试存档没有可验证的队伍、背包、箱子或训练家字段。");

    if (inPlace)
    {
        var writeResult = document.SaveInPlaceWithBackup();
        var result = writeResult.Snapshot;
        Console.WriteLine($"Verified in-place write: {result.FileName} backup={writeResult.BackupPath} created={writeResult.BackupCreated} sections={result.ValidSectionCount}/14 party={result.Party.Count} bag={result.Bag.Count} boxes={result.Boxes.Count} trainer={(result.Trainer is null ? "n/a" : "ok")}");
    }
    else
    {
        var result = document.SaveAs(output);
        Console.WriteLine($"Verified write: {result.FileName} sections={result.ValidSectionCount}/14 party={result.Party.Count} bag={result.Bag.Count} boxes={result.Boxes.Count} trainer={(result.Trainer is null ? "n/a" : "ok")}");
    }
    return 0;
}

static int SaveVerifyImportBox(string[] args)
{
    var path = GetArg(args, "--save", "");
    var output = GetArg(args, "--output", "");
    var species = GetIntArg(args, "--species", 1);
    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(output))
        throw new ArgumentException("Missing --save or --output path");
    if (!HasArg(args, "--yes"))
        throw new InvalidOperationException("该命令会生成包含新箱子宝可梦的测试存档副本；确认后请加 --yes。");

    var profile = CurrentProfile();
    if (!profile.Features.ImportToSaveBoxes || !profile.Features.SaveBoxes.Write)
        throw new InvalidOperationException($"版本 {profile.DisplayName} 未启用存档图鉴导入箱子。");
    var document = Gen3SaveReader.Open(path, profile);
    var trainer = document.Snapshot.Trainer
                  ?? throw new InvalidOperationException("存档没有玩家名字和 OT ID，不能构造新宝可梦。");
    var maxSlots = profile.Memory.SavePcBoxWritableSlotCount > 0
        ? profile.Memory.SavePcBoxWritableSlotCount
        : profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots;
    var occupied = document.Snapshot.Boxes.Select(entry => entry.GlobalSlot).ToHashSet();
    var targetSlot = Enumerable.Range(1, maxSlots).FirstOrDefault(slot => !occupied.Contains(slot));
    if (targetSlot == 0) throw new InvalidOperationException("已验证的存档箱子槽位已满。");

    var db = Db();
    if (!db.Table("species_nickname_bytes").TryGetValue(species, out var nicknameHex) &&
        !db.Table("species_name_bytes").TryGetValue(species, out nicknameHex))
        throw new InvalidOperationException($"物种 {species} 缺少当前 Profile 的昵称字节。");
    var nickname = Convert.FromHexString(nicknameHex);
    var runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
    var mon = BoxPokemon.Create(0x12345678u, trainer.OtId, runtime.LiveBoxLayout);
    mon.SetOtName(trainer.NameBytes);
    mon.SetNicknameFromSpeciesNameEntry(nickname);
    mon.SetGrowth((ushort)species, item: 0, exp: 125, friendship: 70, ppBonuses: 0);
    mon.SetGameNatureCode(26);
    mon.SetMoves([(ushort?)33, null, null, null], [(byte?)35, null, null, null]);
    mon.SetIvs(new Dictionary<string, int>
    {
        ["hp"] = 31, ["atk"] = 31, ["def"] = 31,
        ["spe"] = 31, ["spa"] = 31, ["spd"] = 31, ["ability"] = 0
    });
    mon.SetEvs(new Dictionary<string, byte>
    {
        ["hp"] = 0, ["atk"] = 0, ["def"] = 0,
        ["spe"] = 0, ["spa"] = 0, ["spd"] = 0
    });
    document.ReplaceBoxPokemon(targetSlot, mon);
    var result = document.SaveAs(output);
    var imported = result.Boxes.Single(entry => entry.GlobalSlot == targetSlot).Mon.GetInfo();
    Console.WriteLine($"Verified box import: slot={targetSlot} species={imported.Species} sections={result.ValidSectionCount}/14 boxes={result.Boxes.Count}");
    return 0;
}

static int SaveVerifyImportParty(string[] args)
{
    var path = GetArg(args, "--save", "");
    var output = GetArg(args, "--output", "");
    var species = GetIntArg(args, "--species", 1);
    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(output))
        throw new ArgumentException("Missing --save or --output path");
    if (!HasArg(args, "--yes"))
        throw new InvalidOperationException("该命令会生成包含新增队伍宝可梦的测试存档副本；确认后请加 --yes。");

    var profile = CurrentProfile();
    if (!profile.Features.ImportToSaveParty || !profile.Features.SaveParty.Write)
        throw new InvalidOperationException($"版本 {profile.DisplayName} 未启用存档图鉴导入队伍。");
    var document = Gen3SaveReader.Open(path, profile);
    if (document.Snapshot.Party.Count >= Gen3Constants.PartySlots)
        throw new InvalidOperationException("测试存档队伍已满，不能验证追加导入。");
    var trainer = document.Snapshot.Trainer
                  ?? throw new InvalidOperationException("存档没有玩家名字和 OT ID，不能构造新宝可梦。");
    var db = Db();
    var stats = ReadSpeciesStatsFromDb(db, species);
    var experience = new PokemonExperienceTable(db).ExperienceForLevel(5, stats.GrowthRate);
    var (expectedMoves, expectedPp) = BuildDexImportMoves(db, species, 5);
    var mon = BuildDexImportParty(profile, db, trainer, species);
    var intended = mon.Raw.ToArray();
    var targetSlot = document.AppendPartyPokemon(mon);
    var result = document.SaveAs(output);
    if (result.ValidSectionCount != 14 || result.Party.Count != targetSlot)
        throw new InvalidOperationException("Imported save did not reopen with the expected sections or party count.");
    var importedMon = result.Party[targetSlot - 1];
    if (!importedMon.Raw.SequenceEqual(intended))
        throw new InvalidOperationException("Imported party record did not match the complete intended 100-byte record.");
    var imported = importedMon.GetInfo();
    if (imported.Checksum != imported.CalculatedChecksum ||
        imported.Species != species || imported.Level != 5 || imported.OtId != trainer.OtId ||
        imported.Exp != experience || imported.Friendship != stats.Friendship ||
        imported.Item != 0 || imported.Status != 0 || imported.GameNatureCode != 26 ||
        !imported.Moves.SequenceEqual(expectedMoves) || !imported.Pp.SequenceEqual(expectedPp) ||
        imported.Ivs.Where(pair => Gen3Constants.StatNames.Contains(pair.Key)).Any(pair => pair.Value != 31) ||
        imported.Ivs["ability"] != 0 || imported.Evs.Any(value => value != 0) || imported.MaxHp == 0)
        throw new InvalidOperationException("Imported party record semantic readback did not match the current-Profile defaults.");
    Console.WriteLine($"Verified party import: slot={targetSlot} species={imported.Species} level={imported.Level} exp={imported.Exp} friendship={imported.Friendship} moves={string.Join(',', imported.Moves)} pp={string.Join(',', imported.Pp)} hp={imported.Hp}/{imported.MaxHp} sections={result.ValidSectionCount}/14 party={result.Party.Count}");
    return 0;
}

static int SaveDestinyRepairBag(string[] args)
{
    var path = GetArg(args, "--save", "");
    if (string.IsNullOrWhiteSpace(path))
        throw new ArgumentException("Missing --save path");
    if (!HasArg(args, "--yes"))
        throw new InvalidOperationException("该命令会备份并修复命运存档的招式机盒；确认后请加 --yes。");

    var profile = CurrentProfile();
    if (!profile.Features.SaveEditing || !profile.Features.SaveBag.Write)
        throw new InvalidOperationException($"版本 {profile.DisplayName} 未启用存档背包写入。");
    var result = PokemonDestinySaveScenario.RepairMachineBag(path, profile);
    Console.WriteLine($"Repaired Destiny machine box: {result.DestinationPath}");
    Console.WriteLine($"  backup={result.BackupPath} created={result.BackupCreated}");
    Console.WriteLine($"  sections={result.Snapshot.ValidSectionCount}/14 bag={result.Snapshot.Bag.Count}");
    return 0;
}

if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  RocketTool.Cli species --species 42 229");
    Console.WriteLine("  RocketTool.Cli move --moves 17 44 305");
    Console.WriteLine("  RocketTool.Cli item --items 1 44 231");
    Console.WriteLine("  RocketTool.Cli profile-self-test");
    Console.WriteLine("  RocketTool.Cli profile-data-verify");
    Console.WriteLine("  RocketTool.Cli party-scan [--host 127.0.0.1 --port 8765]");
    Console.WriteLine("  RocketTool.Cli party-dump [--base 0x02025170] [--slot 1]");
    Console.WriteLine("  RocketTool.Cli box-scan [--limit 30 --min-score 12]");
    Console.WriteLine("  RocketTool.Cli box-verify-create [--box 1 --slot 2 --species 1] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-set-basic --slot 1 [--species N --item N --exp N --friendship N --pp-bonuses N --level N --hp N|max --max-hp N --status N] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-set-moves --slot 1 [--moves a b c d] [--pp a b c d] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-set-evs --slot 1 [--hp N --atk N --def N --spe N --spa N --spd N] [--force] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-set-ivs --slot 1 [--hp N --atk N --def N --spe N --spa N --spd N --ability 0|1|2 --egg 0|1] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-heal [--slot 1] [--yes]");
    Console.WriteLine("  RocketTool.Cli shop-probe");
    Console.WriteLine("  RocketTool.Cli bag-scan [--limit 12] [--single-limit 12]");
    Console.WriteLine("  RocketTool.Cli bag-read --addr 0x02000000");
    Console.WriteLine("  RocketTool.Cli bag-set --addr 0x02000000 --item N --qty N [--yes]");
    Console.WriteLine("  RocketTool.Cli trainer-probe");
    Console.WriteLine("  RocketTool.Cli trainer-set-money --money N [--yes]");
    Console.WriteLine("  RocketTool.Cli trainer-set-name --name NAME [--yes]");
    Console.WriteLine("  RocketTool.Cli trainer-verify-write --yes");
    Console.WriteLine("  RocketTool.Cli party-verify-create [--species N] --yes");
    Console.WriteLine("  RocketTool.Cli save-probe --save path/to/file.sav");
    Console.WriteLine("  RocketTool.Cli save-verify-write --save input.sav [--output output.sav | --in-place] --yes");
    Console.WriteLine("  RocketTool.Cli save-verify-import-box --save input.sav --output output.sav [--species N] --yes");
    Console.WriteLine("  RocketTool.Cli save-verify-import-party --save input.sav --output output.sav [--species N] --yes");
    Console.WriteLine("  RocketTool.Cli save-destiny-repair-bag --save input.srm --yes");
    return 1;
}

return args[0] switch
{
    "party-dump" => PartyDump(args[1..]),
    "party-scan" => PartyScan(args[1..]),
    "box-scan" => BoxScan(args[1..]),
    "box-verify-create" => BoxVerifyCreate(args[1..]),
    "party-set-basic" => PartySetBasic(args[1..]),
    "party-set-moves" => PartySetMoves(args[1..]),
    "party-set-evs" => PartySetEvs(args[1..]),
    "party-set-ivs" => PartySetIvs(args[1..]),
    "party-heal" => PartyHeal(args[1..]),
    "species" => SpeciesStats(args[1..]),
    "move" or "moves" => MoveStats(args[1..]),
    "item" or "items" => ItemStats(args[1..]),
    "profile-self-test" => ProfileSelfTest(),
    "profile-data-verify" => ProfileDataVerify(),
    "shop-probe" => ShopProbe(args[1..]),
    "bag-scan" => BagScan(args[1..]),
    "bag-read" => BagRead(args[1..]),
    "bag-set" => BagSet(args[1..]),
    "trainer-probe" => TrainerProbe(args[1..]),
    "trainer-set-money" => TrainerSetMoney(args[1..]),
    "trainer-set-name" => TrainerSetName(args[1..]),
    "trainer-verify-write" => TrainerVerifyWrite(args[1..]),
    "party-verify-create" => PartyVerifyCreate(args[1..]),
    "save-probe" => SaveProbe(args[1..]),
    "save-verify-write" => SaveVerifyWrite(args[1..]),
    "save-verify-import-box" => SaveVerifyImportBox(args[1..]),
    "save-verify-import-party" => SaveVerifyImportParty(args[1..]),
    "save-destiny-repair-bag" => SaveDestinyRepairBag(args[1..]),
    _ => throw new ArgumentException($"Unknown command: {args[0]}")
};
