using RocketTool.Core;

static string RootDir()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8; i++)
    {
        var candidate = Path.GetFullPath(Path.Combine(dir, string.Concat(Enumerable.Repeat("../", i))));
        if (Directory.Exists(Path.Combine(candidate, "modifier_db"))) return candidate;
    }
    return "/Users/bombermanll/Downloads/mgba_bridge_prototype_2026-06-06";
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

static string DefaultRom()
{
    var downloads = "/Users/bombermanll/Downloads";
    return Directory.GetFiles(downloads, "西班牙火箭队*.gba").OrderBy(x => x).FirstOrDefault() ?? string.Empty;
}

static ModifierDatabase Db() => new(Path.Combine(RootDir(), "modifier_db"));

static void RecalculateLiveStats(PartyPokemon mon, string[] args)
{
    var rom = GetArg(args, "--rom", DefaultRom());
    if (string.IsNullOrEmpty(rom)) throw new InvalidOperationException("No ROM supplied; cannot recalculate live stats");
    var info = mon.GetInfo();
    var reader = new SpeciesStatsReader(rom);
    mon.RecalculateStats(reader.Read(info.Species));
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
    if (!quiet) Console.WriteLine("No --base supplied; scanning EWRAM for live party...");
    var ewram = PartyScanner.ReadEwram(bridge, quiet ? null : (off, total) =>
    {
        if (off % 0x10000 == 0) Console.WriteLine($"read EWRAM 0x{off:X5}/0x{total:X5}");
    });
    var run = PartyScanner.LocateParty(ewram) ?? throw new InvalidOperationException("Could not auto-locate party base; pass --base 0x...");
    if (!quiet) Console.WriteLine($"Auto party base: 0x{run.StartAddress:X8} len={run.Length} score_sum={run.ScoreSum}");
    return run.StartAddress;
}

static (uint BaseAddr, uint Addr, PartyPokemon Mon) ReadMon(MgbaBridgeClient bridge, string[] args, int slot, bool quietScan = false)
{
    var baseAddr = ResolvePartyBase(args, bridge, quietScan);
    var addr = SlotAddress(baseAddr, slot);
    return (baseAddr, addr, new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize)));
}

static void WriteMon(MgbaBridgeClient bridge, uint addr, PartyPokemon mon) => bridge.WriteRangeVerified(addr, mon.Raw);

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
    Console.WriteLine($"  moves: {moves} pp=[{string.Join(", ", info.Pp)}]");
    Console.WriteLine($"  evs=[{evs}] ev_sum={info.Evs.Sum(x => x)} ivs=[{ivs}] abilitySlot={info.Ivs["ability"]} egg={info.Ivs["egg"]}");
}

static int PartyDump(string[] args)
{
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    var baseAddr = ResolvePartyBase(args, bridge);
    var slots = GetUIntArg(args, "--slot") is uint slot ? [checked((int)slot)] : Enumerable.Range(1, Gen3Constants.PartySlots).ToArray();
    foreach (var s in slots)
    {
        if (s is < 1 or > 6) throw new ArgumentOutOfRangeException("--slot", "slot must be 1..6");
        var addr = SlotAddress(baseAddr, s);
        PrintMon(s, addr, new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize)), db);
    }
    return 0;
}

static int PartyScan(string[] args)
{
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var minScore = GetIntArg(args, "--min-score", 13);
    var limit = GetIntArg(args, "--limit", 30);
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    var ewram = PartyScanner.ReadEwram(bridge, (off, total) =>
    {
        if (off % 0x10000 == 0) Console.WriteLine($"read EWRAM 0x{off:X5}/0x{total:X5}");
    });
    var hits = PartyScanner.FindCandidates(ewram, minScore);
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
    var best = PartyScanner.LocateParty(ewram, minScore);
    if (best is not null) Console.WriteLine($"\nBest live party base: 0x{best.StartAddress:X8}");
    return 0;
}

static int PartySetBasic(string[] args)
{
    var db = Db();
    var slot = ValidateSlot(args);
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    var (_, addr, mon) = ReadMon(bridge, args, slot);
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
        WriteMon(bridge, addr, mon);
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
    var slot = ValidateSlot(args);
    var moveValues = ValuesAfter(args, "--moves");
    var ppValues = ValuesAfter(args, "--pp");
    if (moveValues.Length == 0 && ppValues.Length == 0) throw new ArgumentException("Supply --moves and/or --pp");
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    var (_, addr, mon) = ReadMon(bridge, args, slot);
    var before = mon.GetInfo();
    mon.SetMoves(moveValues.Length == 0 ? null : ParseNullableUShortList(moveValues, 4), ppValues.Length == 0 ? null : ParseNullableByteList(ppValues, 4));
    var after = mon.GetInfo();
    var summary = $"Will write slot {slot} moves at 0x{addr:X8}\n" +
                  $"  before moves=[{string.Join(", ", before.Moves)}] pp=[{string.Join(", ", before.Pp)}]\n" +
                  $"  after  moves=[{string.Join(", ", after.Moves)}] pp=[{string.Join(", ", after.Pp)}]";
    if (Confirm(args, summary))
    {
        WriteMon(bridge, addr, mon);
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
    var slot = ValidateSlot(args);
    var values = ByteStatArgs(args);
    if (values.Count == 0) throw new ArgumentException("Supply at least one EV field");
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    var (_, addr, mon) = ReadMon(bridge, args, slot);
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
        WriteMon(bridge, addr, mon);
        Console.WriteLine($"Wrote slot {slot}");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static int PartySetIvs(string[] args)
{
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
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    var (_, addr, mon) = ReadMon(bridge, args, slot);
    var before = mon.GetInfo();
    mon.SetIvs(values);
    RecalculateLiveStats(mon, args);
    var after = mon.GetInfo();
    var summary = $"Will write slot {slot} IVs at 0x{addr:X8}\n" +
                  $"  before ivs=[{string.Join(", ", before.Ivs.Select(kv => $"{kv.Key}={kv.Value}"))}]\n" +
                  $"  after  ivs=[{string.Join(", ", after.Ivs.Select(kv => $"{kv.Key}={kv.Value}"))}] stats HP {after.Hp}/{after.MaxHp} Atk {after.Attack} Def {after.Defense} Spe {after.Speed} SpA {after.SpAttack} SpD {after.SpDefense}";
    if (Confirm(args, summary))
    {
        WriteMon(bridge, addr, mon);
        Console.WriteLine($"Wrote slot {slot}");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static int PartyHeal(string[] args)
{
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    var baseAddr = ResolvePartyBase(args, bridge);
    var slots = GetUIntArg(args, "--slot") is uint slot ? [checked((int)slot)] : Enumerable.Range(1, Gen3Constants.PartySlots).ToArray();
    var changed = new List<(int Slot, uint Addr, PartyPokemon Mon, ushort OldHp, ushort MaxHp, uint Status)>();
    foreach (var s in slots)
    {
        if (s is < 1 or > 6) throw new ArgumentOutOfRangeException("--slot", "slot must be 1..6");
        var addr = SlotAddress(baseAddr, s);
        var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
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
        foreach (var c in changed) WriteMon(bridge, c.Addr, c.Mon);
        Console.WriteLine($"Wrote {changed.Count} slot(s)");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

static int SpeciesStats(string[] args)
{
    var db = Db();
    var rom = GetArg(args, "--rom", DefaultRom());
    if (string.IsNullOrEmpty(rom)) throw new InvalidOperationException("No ROM supplied");
    var offset = GetIntArg(args, "--offset", SpeciesStatsReader.DefaultBaseStatsOffset);
    var entrySize = GetIntArg(args, "--entry-size", SpeciesStatsReader.DefaultEntrySize);
    var reader = new SpeciesStatsReader(rom, offset, entrySize);
    foreach (var text in ValuesAfter(args, "--species", "--ids"))
    {
        var id = ParseInt(text);
        var s = reader.Read(id);
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
    var rom = GetArg(args, "--rom", DefaultRom());
    if (string.IsNullOrEmpty(rom)) throw new InvalidOperationException("No ROM supplied");
    var reader = new MoveDataReader(rom, GetIntArg(args, "--offset", MoveDataReader.DefaultMoveTableOffset), GetIntArg(args, "--entry-size", MoveDataReader.DefaultEntrySize));
    foreach (var text in ValuesAfter(args, "--moves", "--ids"))
    {
        var id = ParseInt(text);
        var m = reader.Read(id);
        Console.WriteLine($"Move {id} ({db.NameOf("moves", id)})");
        Console.WriteLine($"  power {m.Power} type {m.Type}({MoveDataReader.TypeName(m.Type)}) accuracy {m.Accuracy} pp {m.Pp} category {m.Category}({MoveDataReader.CategoryName(m.Category)}) priority {m.Priority}");
        Console.WriteLine($"  chance {m.SecondaryEffectChance} target=0x{m.Target:X4} flags=0x{m.Flags:X4} zPower={m.ZMovePower} raw={Convert.ToHexString(m.Raw)}");
    }
    return 0;
}

static int ItemStats(string[] args)
{
    var db = Db();
    var rom = GetArg(args, "--rom", DefaultRom());
    if (string.IsNullOrEmpty(rom)) throw new InvalidOperationException("No ROM supplied");
    var reader = new ItemDataReader(rom, GetIntArg(args, "--offset", ItemDataReader.DefaultItemTableOffset), GetIntArg(args, "--entry-size", ItemDataReader.DefaultEntrySize));
    foreach (var text in ValuesAfter(args, "--items", "--ids"))
    {
        var id = ParseInt(text);
        var item = reader.Read(id);
        var effectName = item.HoldEffect == 0 ? "无" : db.NameOf("item_effects", item.HoldEffect);
        Console.WriteLine($"Item {id} ({db.NameOf("items", id)})");
        Console.WriteLine($"  itemId {item.ItemId} price {item.Price} holdEffect {item.HoldEffect}({effectName}) holdParam {item.HoldEffectParam}");
        Console.WriteLine($"  pocket {item.Pocket}({ItemDataReader.PocketName(item.Pocket)}) type {item.Type} importance {item.Importance} exitsBag {item.ExitsBagOnUse} battleUsage {item.BattleUsage}");
        Console.WriteLine($"  desc=0x{item.DescriptionPointer:X8} fieldUse=0x{item.FieldUseFunction:X8} battleUse=0x{item.BattleUseFunction:X8} secondary=0x{item.SecondaryId:X8}");
    }
    return 0;
}

static int ShopProbe(string[] args)
{
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    var s = LiveMemoryProbe.ReadShopSnapshot(bridge);
    Console.WriteLine($"Shop price/current lock field @ 0x{LiveMemoryAddresses.ShopPriceAddress:X8}: {s.ShopPrice}");
    Console.WriteLine($"Shop first item field      @ 0x{LiveMemoryAddresses.ShopFirstItemAddress:X8}: {s.ShopFirstItem}({(s.ShopFirstItem == 0 ? "无" : db.NameOf("items", s.ShopFirstItem))})");
    Console.WriteLine($"Sell price primary         @ 0x{LiveMemoryAddresses.SellPricePrimaryAddress:X8}: {s.SellPricePrimary}");
    Console.WriteLine($"Sell price fallback        @ 0x{LiveMemoryAddresses.SellPriceFallbackAddress:X8}: {s.SellPriceFallback}");
    return 0;
}

static int BagScan(string[] args)
{
    var db = Db();
    var rom = GetArg(args, "--rom", DefaultRom());
    var itemReader = string.IsNullOrEmpty(rom) ? null : new ItemDataReader(rom);
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var limit = GetIntArg(args, "--limit", 12);
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    Console.WriteLine($"Game code: {bridge.GameCode()}");
    uint? bagBase = null;
    try { bagBase = BagScanner.LocateSaveBlockBase(bridge); }
    catch { }
    var ewram = PartyScanner.ReadEwram(bridge, (off, total) =>
    {
        if (off % 0x10000 == 0) Console.WriteLine($"read EWRAM 0x{off:X5}/0x{total:X5}");
    });
    var quantityKey = BagScanner.InferQuantityKey(ewram);
    var pockets = BagScanner.FindLivePockets(ewram, bagBase, PocketOfItem).ToArray();
    Console.WriteLine($"\n背包口袋: {pockets.Length}，base={(bagBase is null ? "unknown" : $"0x{bagBase:X8}")} qtyKey=0x{quantityKey:X4}");
    foreach (var pocket in pockets)
    {
        Console.WriteLine($"{PocketNameZh(pocket.Pocket)} 0x{pocket.StartAddress:X8} nonEmpty={pocket.NonEmptyCount} score={pocket.Score}");
        foreach (var slot in pocket.Slots.Take(limit))
        {
            Console.WriteLine($"  0x{slot.Address:X8}: {slot.ItemId}({db.NameOf("items", slot.ItemId)}) x{slot.Quantity}");
        }
    }
    return 0;

    int PocketOfItem(int item)
    {
        if (item <= 0) return -1;
        if (item is >= 1 and <= 27) return 3;
        if (item is >= 28 and <= 48 or 921) return 2;
        if (item is >= 512 and <= 591) return 5;
        if (item is >= 592 and <= 837) return 7;
        if (item is >= 838 and <= 845) return 7;
        if (itemReader is null) return -1;
        try { return itemReader.Read(item).Pocket; }
        catch { return -1; }
    }
}

static int BagRead(string[] args)
{
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var addr = GetUIntArg(args, "--addr") ?? throw new ArgumentException("Missing --addr 0x...");
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    var slot = BagScanner.ReadSlot(bridge, addr);
    Console.WriteLine($"0x{slot.Address:X8}: item={slot.ItemId}({(slot.ItemId == 0 ? "无" : db.NameOf("items", slot.ItemId))}) qty={slot.Quantity} score={slot.Score} {slot.Note}");
    return 0;
}

static string PocketNameZh(int pocket) => pocket switch
{
    1 => "普通道具",
    2 => "回复药品",
    3 => "精灵球",
    4 => "战斗道具",
    5 => "树果",
    6 => "宝物",
    7 => "招式机器/秘传机器",
    8 => "重要物品",
    _ => $"#{pocket}"
};

static int BagSet(string[] args)
{
    var db = Db();
    var host = GetArg(args, "--host", "127.0.0.1");
    var port = GetIntArg(args, "--port", 8765);
    var addr = GetUIntArg(args, "--addr") ?? throw new ArgumentException("Missing --addr 0x...");
    var item = RequiredIntArg(args, "--item");
    var qty = RequiredIntArg(args, "--qty");
    if (item is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException("--item", "item must be 0..65535");
    if (qty is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException("--qty", "qty must be 0..65535");
    using var bridge = MgbaBridgeClient.Connect(host, port);
    Console.WriteLine(bridge.Welcome);
    var before = BagScanner.ReadSlot(bridge, addr);
    var summary = $"将写入背包槽 0x{addr:X8}\n" +
                  $"  原来: {before.ItemId}({(before.ItemId == 0 ? "无" : db.NameOf("items", before.ItemId))}) x{before.Quantity}\n" +
                  $"  新值: {item}({(item == 0 ? "无" : db.NameOf("items", item))}) x{qty}\n" +
                  "提示：当前按普通 Gen3 {u16道具,u16数量} 写入；如果该改版对数量加密，请先用少量测试。";
    if (Confirm(args, summary))
    {
        bridge.WriteRangeVerified(addr, BagScanner.EncodeSlot((ushort)item, (ushort)qty));
        var after = BagScanner.ReadSlot(bridge, addr);
        Console.WriteLine($"已写入: {after.ItemId}({(after.ItemId == 0 ? "无" : db.NameOf("items", after.ItemId))}) x{after.Quantity}");
    }
    else Console.WriteLine("Cancelled");
    return 0;
}

if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  RocketTool.Cli species --species 42 229 [--rom path]");
    Console.WriteLine("  RocketTool.Cli move --moves 17 44 305 [--rom path]");
    Console.WriteLine("  RocketTool.Cli item --items 1 44 231 [--rom path]");
    Console.WriteLine("  RocketTool.Cli party-scan [--host 127.0.0.1 --port 8765]");
    Console.WriteLine("  RocketTool.Cli party-dump [--base 0x02025170] [--slot 1]");
    Console.WriteLine("  RocketTool.Cli party-set-basic --slot 1 [--species N --item N --exp N --friendship N --pp-bonuses N --level N --hp N|max --max-hp N --status N] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-set-moves --slot 1 [--moves a b c d] [--pp a b c d] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-set-evs --slot 1 [--hp N --atk N --def N --spe N --spa N --spd N] [--force] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-set-ivs --slot 1 [--hp N --atk N --def N --spe N --spa N --spd N --ability 0|1|2 --egg 0|1] [--yes]");
    Console.WriteLine("  RocketTool.Cli party-heal [--slot 1] [--yes]");
    Console.WriteLine("  RocketTool.Cli shop-probe");
    Console.WriteLine("  RocketTool.Cli bag-scan [--limit 12 --min-items 2]");
    Console.WriteLine("  RocketTool.Cli bag-read --addr 0x02000000");
    Console.WriteLine("  RocketTool.Cli bag-set --addr 0x02000000 --item N --qty N [--yes]");
    return 1;
}

return args[0] switch
{
    "party-dump" => PartyDump(args[1..]),
    "party-scan" => PartyScan(args[1..]),
    "party-set-basic" => PartySetBasic(args[1..]),
    "party-set-moves" => PartySetMoves(args[1..]),
    "party-set-evs" => PartySetEvs(args[1..]),
    "party-set-ivs" => PartySetIvs(args[1..]),
    "party-heal" => PartyHeal(args[1..]),
    "species" => SpeciesStats(args[1..]),
    "move" or "moves" => MoveStats(args[1..]),
    "item" or "items" => ItemStats(args[1..]),
    "shop-probe" => ShopProbe(args[1..]),
    "bag-scan" => BagScan(args[1..]),
    "bag-read" => BagRead(args[1..]),
    "bag-set" => BagSet(args[1..]),
    _ => throw new ArgumentException($"Unknown command: {args[0]}")
};
