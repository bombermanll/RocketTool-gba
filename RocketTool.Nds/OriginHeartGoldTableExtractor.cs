using System.Globalization;
using System.Text;

namespace RocketTool.Nds;

public sealed record OriginHeartGoldExtractResult(
    NdsRomIdentity Identity,
    int SpeciesCount,
    int VisibleSpeciesCount,
    int MoveCount,
    int ItemCount,
    int AbilityCount,
    IReadOnlyList<string> Notes);

public static class OriginHeartGoldTableExtractor
{
    public const string ProfileId = "pokemon-origin-heartgold-40-cn";
    public const string PersonalPath = "a/0/0/2";
    public const string GrowthPath = "a/0/0/3";
    public const string ItemPath = "a/0/1/7";
    public const string MovePath = "extra/new_move_data.narc";
    public const string LevelMovePath = "a/0/3/3";
    public const string EvolutionPath = "a/0/3/4";
    public const string EggMovePath = "data/egg_moves.narc";
    public const string TutorMovePath = "data/tutor_moves.narc";

    public static OriginHeartGoldExtractResult Extract(string romPath, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(romPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var rom = NdsRom.Open(romPath);
        var personal = NarcFile.Parse(rom.ReadFile(PersonalPath));
        var growth = NarcFile.Parse(rom.ReadFile(GrowthPath));
        var items = NarcFile.Parse(rom.ReadFile(ItemPath));
        var moves = NarcFile.Parse(rom.ReadFile(MovePath));
        var levelMoves = NarcFile.Parse(rom.ReadFile(LevelMovePath));
        var evolutions = NarcFile.Parse(rom.ReadFile(EvolutionPath));
        var eggMoves = NarcFile.Parse(rom.ReadFile(EggMovePath));
        var tutorMoves = NarcFile.Parse(rom.ReadFile(TutorMovePath));

        if (personal.Files.Count == 0 || personal.Files[0].Length != 52)
            throw new InvalidDataException("起源心金种族值 NARC 不是 52 字节记录。");
        if (moves.Files.Count == 0 || moves.Files[0].Length != 40)
            throw new InvalidDataException("起源心金招式 NARC 不是 40 字节记录。");
        if (items.Files.Count == 0 || items.Files[0].Length != 34)
            throw new InvalidDataException("起源心金道具 NARC 不是 34 字节记录。");
        if (growth.Files.Count < 6 || growth.Files[0].Length != 404)
            throw new InvalidDataException("起源心金经验表不是 8×101 级布局。");
        if (personal.Files.Count != 1441 || moves.Files.Count != 921 || items.Files.Count != 791)
            throw new InvalidDataException($"起源心金表数量异常：personal={personal.Files.Count} moves={moves.Files.Count} items={items.Files.Count}。");
        if (levelMoves.Files.Count != personal.Files.Count || evolutions.Files.Count != personal.Files.Count)
            throw new InvalidDataException("升级招式或进化 NARC 数量与种族值表不一致。");
        if (eggMoves.Files.Count != personal.Files.Count)
            throw new InvalidDataException($"蛋招式 NARC 数量异常：{eggMoves.Files.Count}。");
        if (tutorMoves.Files.Count != personal.Files.Count)
            throw new InvalidDataException($"教学招式 NARC 数量异常：{tutorMoves.Files.Count}。");
        var bulbEgg = ReadTerminatedU16(eggMoves.Files[1]);
        if (bulbEgg.Count == 0 || bulbEgg[0] != 133)
            throw new InvalidDataException("蛋招式 ID 1 不是预期的妙蛙种子样本（首招 133）。");
        var bulbasaur = personal.Files[1];
        if (bulbasaur[0] != 45 || bulbasaur[1] != 49 || bulbasaur[2] != 49 ||
            bulbasaur[3] != 45 || bulbasaur[4] != 65 || bulbasaur[5] != 65 ||
            bulbasaur[6] != 12 || bulbasaur[7] != 3)
            throw new InvalidDataException("种族值 ID 1 不是预期的妙蛙种子样本（45/49/49/45/65/65 草/毒）。");
        var bulbEvo = evolutions.Files[1];
        if (bulbEvo.Length < 6 ||
            BitConverter.ToUInt16(bulbEvo, 0) != 4 ||
            BitConverter.ToUInt16(bulbEvo, 2) != 16 ||
            BitConverter.ToUInt16(bulbEvo, 4) != 2)
            throw new InvalidDataException("进化表 ID 1 不是预期的 16 级进化到 2。");

        var visibleSpecies = Enumerable.Range(1, personal.Files.Count - 1)
            .Where(id => personal.Files[id].AsSpan(0, 6).ToArray().Any(value => value != 0))
            .ToArray();
        if (visibleSpecies.Length != 1440)
            throw new InvalidDataException($"可见物种数量异常：{visibleSpecies.Length}。");
        var maxAbility = 0;
        foreach (var record in personal.Files)
        {
            maxAbility = Math.Max(maxAbility, BitConverter.ToUInt16(record, 22));
            maxAbility = Math.Max(maxAbility, BitConverter.ToUInt16(record, 24));
            maxAbility = Math.Max(maxAbility, BitConverter.ToUInt16(record, 26));
        }

        var names = OriginHeartGoldText.Decode(rom);
        WriteTable(outputDirectory, "species.tsv", visibleSpecies.Select(id => $"{id}\t{DisplayName(names.Species, id, $"宝可梦#{id:0000}")}"));
        WriteTable(outputDirectory, "species_stats.tsv", visibleSpecies.Select(id => FormatSpeciesStats(id, personal.Files[id])));
        WriteTable(outputDirectory, "species_evolutions.tsv", visibleSpecies.Select(id => FormatEvolutions(id, evolutions.Files[id])));
        WriteTable(outputDirectory, "species_evolutions_raw.tsv", visibleSpecies.Select(id => $"{id}\t{Convert.ToHexString(evolutions.Files[id]).ToLowerInvariant()}"));
        WriteTable(outputDirectory, "species_level_moves.tsv", visibleSpecies.Select(id => FormatLevelMoves(id, levelMoves.Files[id])));
        WriteTable(outputDirectory, "species_egg_moves.tsv", visibleSpecies.Select(id => FormatMoveList(id, eggMoves.Files[id])));
        WriteTable(outputDirectory, "species_tutor_moves.tsv", visibleSpecies.Select(id => FormatIndexedMoveList(id, tutorMoves.Files[id])));
        WriteTable(outputDirectory, "moves.tsv", Enumerable.Range(1, moves.Files.Count - 1).Select(id => $"{id}\t{DisplayName(names.Moves, id, $"招式#{id:0000}")}"));
        WriteTable(outputDirectory, "move_data.tsv", Enumerable.Range(0, moves.Files.Count).Select(id => FormatMove(id, moves.Files[id])));
        WriteTable(outputDirectory, "items.tsv", Enumerable.Range(0, items.Files.Count).Select(id => id == 0 ? "0\t未使用" : $"{id}\t{DisplayName(names.Items, id, $"道具#{id:0000}")}"));
        WriteTable(outputDirectory, "item_data.tsv", Enumerable.Range(0, items.Files.Count).Select(id => FormatItem(id, items.Files[id])));
        WriteTable(outputDirectory, "abilities.tsv", Enumerable.Range(0, maxAbility + 1).Select(id => id == 0 ? "0\t无" : $"{id}\t{DisplayName(names.Abilities, id, $"特性#{id:0000}")}"));
        WriteTable(outputDirectory, "experience.tsv", Enumerable.Range(0, 6).Select(id => FormatExperience(id, growth.Files[id])));
        WriteTable(outputDirectory, "move_effects.tsv", names.MoveEffects.OrderBy(row => row.Key).Select(row => $"{row.Key}\t{SanitizeTsv(row.Value)}"));
        WriteTable(outputDirectory, "item_effects.tsv", names.ItemEffects.OrderBy(row => row.Key).Select(row => $"{row.Key}\t{SanitizeTsv(row.Value)}"));
        var personalFile = rom.RequireFile(PersonalPath);
        var growthFile = rom.RequireFile(GrowthPath);
        var itemFile = rom.RequireFile(ItemPath);
        var moveFile = rom.RequireFile(MovePath);
        var levelMoveFile = rom.RequireFile(LevelMovePath);
        var evolutionFile = rom.RequireFile(EvolutionPath);
        var eggMoveFile = rom.RequireFile(EggMovePath);
        var tutorMoveFile = rom.RequireFile(TutorMovePath);
        var messageFile = rom.RequireFile(OriginHeartGoldText.MessagePath);
        File.WriteAllText(
            Path.Combine(outputDirectory, "manifest.json"),
            $$"""
            {
              "profile": "{{ProfileId}}",
              "romSha256": "{{rom.Identity.Sha256}}",
              "counts": {
                "speciesSlots": {{personal.Files.Count}},
                "visibleSpecies": {{visibleSpecies.Length}},
                "moves": {{moves.Files.Count}},
                "items": {{items.Files.Count}},
                "abilities": {{maxAbility + 1}}
              },
              "narcOffsets": {
                "personal": "0x{{personalFile.Offset:X}}",
                "growth": "0x{{growthFile.Offset:X}}",
                "items": "0x{{itemFile.Offset:X}}",
                "moves": "0x{{moveFile.Offset:X}}",
                "levelMoves": "0x{{levelMoveFile.Offset:X}}",
                "evolutions": "0x{{evolutionFile.Offset:X}}",
                "eggMoves": "0x{{eggMoveFile.Offset:X}}",
                "tutorMoves": "0x{{tutorMoveFile.Offset:X}}",
                "messages": "0x{{messageFile.Offset:X}}"
              },
              "text": {
                "speciesBank": {{OriginHeartGoldText.SpeciesNameBank}},
                "moveBank": {{OriginHeartGoldText.MoveNameBank}},
                "itemBank": {{OriginHeartGoldText.ItemNameBank}},
                "abilityBank": {{OriginHeartGoldText.AbilityNameBank}},
                "mappedGlyphs": {{names.MappedGlyphs}},
                "unresolvedGlyphs": {{names.UnresolvedGlyphs}}
              },
              "evidence": "See ORIGIN_HEARTGOLD_REVERSE_NOTES.md"
            }
            """,
            Encoding.UTF8);

        return new OriginHeartGoldExtractResult(
            rom.Identity,
            personal.Files.Count,
            visibleSpecies.Length,
            moves.Files.Count,
            items.Files.Count,
            maxAbility,
            [
                $"中文名来自消息银行 232/739/219/711，字形 {names.MappedGlyphs}，未映射码点 {names.UnresolvedGlyphs}。",
                ..names.Notes,
                "存档和实时功能保持关闭。"
            ]);
    }

    private static string DisplayName(IReadOnlyDictionary<int, string> names, int id, string fallback)
    {
        if (!names.TryGetValue(id, out var name))
            return fallback;
        name = SanitizeTsv(name).Replace('�', '□');
        if (string.IsNullOrWhiteSpace(name) || name.All(ch => ch is '□' or '?' or '？' or 'ー' or '－'))
            return fallback;
        return name;
    }

    private static string SanitizeTsv(string name)
        => name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string FormatSpeciesStats(int id, byte[] record)
    {
        var ability3 = BitConverter.ToUInt16(record, 26);
        return string.Join('\t',
            id.ToString(CultureInfo.InvariantCulture),
            record[0], record[1], record[2], record[3], record[4], record[5],
            record[6], record[7],
            record[8], record[9],
            BitConverter.ToUInt16(record, 10),
            BitConverter.ToUInt16(record, 12),
            BitConverter.ToUInt16(record, 14),
            record[16], record[17], record[18], record[19], record[20], record[21],
            BitConverter.ToUInt16(record, 22),
            BitConverter.ToUInt16(record, 24),
            ability3);
    }

    private static string FormatMove(int id, byte[] record)
    {
        // extra/new_move_data.narc: type@0, effect@1, category@2 (0 status / 1 phys / 2 spec),
        // power@3, accuracy@4, pp@5, priority@6 as signed byte. UI category is 0 phys / 1 spec / 2 status.
        var category = record[2] switch
        {
            1 => 0,
            2 => 1,
            _ => 2
        };
        return string.Join('\t',
            id.ToString(CultureInfo.InvariantCulture),
            record[3],
            record[0],
            record[4],
            record[5],
            0,
            0,
            unchecked((sbyte)record[6]),
            0,
            category,
            0,
            record[1]);
    }

    private static string FormatItem(int id, byte[] record)
    {
        var price = BitConverter.ToUInt16(record, 0);
        var packed = BitConverter.ToUInt16(record, 8);
        var fieldPocket = (packed >> 7) & 0xF;
        var battlePocket = (packed >> 11) & 0x1F;
        var preventToss = (packed >> 5) & 1;
        var selectable = (packed >> 6) & 1;
        return string.Join('\t',
            id.ToString(CultureInfo.InvariantCulture),
            id,
            price,
            record[2],
            record[3],
            0,
            preventToss,
            selectable,
            fieldPocket,
            battlePocket,
            record[10],
            record[11],
            record[12],
            0);
    }

    private static IReadOnlyList<int> ReadTerminatedU16(byte[] record)
    {
        var values = new List<int>();
        for (var offset = 0; offset + 2 <= record.Length; offset += 2)
        {
            var value = BitConverter.ToUInt16(record, offset);
            if (value == 0xFFFF) break;
            if (value != 0) values.Add(value);
        }

        return values;
    }

    private static string FormatMoveList(int id, byte[] record)
        => $"{id}\t{string.Join(';', ReadTerminatedU16(record))}";

    private static string FormatIndexedMoveList(int id, byte[] record)
        => $"{id}\t{string.Join(';', ReadTerminatedU16(record).Select((move, index) => $"{index}:{move}"))}";

    private static string FormatEvolutions(int id, byte[] record)
    {
        var parts = new List<string>();
        for (var slot = 0; slot + 6 <= record.Length; slot += 6)
        {
            var method = BitConverter.ToUInt16(record, slot);
            var parameter = BitConverter.ToUInt16(record, slot + 2);
            var target = BitConverter.ToUInt16(record, slot + 4);
            if (method == 0 && parameter == 0 && target == 0)
                continue;
            parts.Add($"{method},{parameter},{target},0");
        }

        return $"{id}\t{string.Join(';', parts)}";
    }

    private static string FormatLevelMoves(int id, byte[] record)
    {
        var parts = new List<string>();
        for (var offset = 0; offset + 4 <= record.Length; offset += 4)
        {
            var level = BitConverter.ToUInt16(record, offset);
            var move = BitConverter.ToUInt16(record, offset + 2);
            if (level == 0xFFFF || move == 0xFFFF)
                break;
            parts.Add($"{level}:{move}");
        }

        return $"{id}\t{string.Join(';', parts)}";
    }

    private static string FormatExperience(int growthRate, byte[] record)
    {
        var values = new uint[151];
        for (var level = 0; level <= 100; level++)
            values[level] = BitConverter.ToUInt32(record, level * 4);
        var last = values[100];
        for (var level = 101; level <= 150; level++)
            values[level] = last;
        return $"{growthRate}\t{string.Join('\t', values)}";
    }

    private static void WriteTable(string directory, string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(directory, name);
        var content = new StringBuilder();
        foreach (var line in lines)
            content.Append(line).Append('\n');
        File.WriteAllText(path, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
