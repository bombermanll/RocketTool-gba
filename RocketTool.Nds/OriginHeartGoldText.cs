using System.Globalization;
using System.Reflection;
using System.Text;

namespace RocketTool.Nds;

public sealed record OriginHeartGoldDecodedNames(
    IReadOnlyDictionary<int, string> Species,
    IReadOnlyDictionary<int, string> Moves,
    IReadOnlyDictionary<int, string> Items,
    IReadOnlyDictionary<int, string> Abilities,
    IReadOnlyDictionary<int, string> Categories,
    IReadOnlyDictionary<int, string> MoveEffects,
    IReadOnlyDictionary<int, string> ItemEffects,
    IReadOnlyDictionary<int, string> AbilityDescriptions,
    int MappedGlyphs,
    int UnresolvedGlyphs,
    IReadOnlyList<string> Notes);

public static class OriginHeartGoldText
{
    public const string MessagePath = "a/0/2/7";
    public const string CharmapResource = "RocketTool.Nds.gen4-revised-charmap.tsv";
    public const int SpeciesNameBank = 232;
    public const int MoveNameBank = 739;
    public const int ItemNameBank = 219;
    public const int AbilityNameBank = 711;
    public const int AbilityDescriptionBank = 712;
    public const int CategoryBank = 803;
    public const int MoveEffectBank = 738;
    public const int ItemEffectBank = 218;

    private static readonly IReadOnlyDictionary<int, string> Charmap = LoadCharmap();

    public static OriginHeartGoldDecodedNames Decode(NdsRom rom)
    {
        var banks = NarcFile.Parse(rom.ReadFile(MessagePath)).Files;
        if (banks.Count <= CategoryBank)
            throw new InvalidDataException($"消息银行数量异常：{banks.Count}。");

        var speciesCodes = DecryptBank(banks[SpeciesNameBank]);
        var moveCodes = DecryptBank(banks[MoveNameBank]);
        var itemCodes = DecryptBank(banks[ItemNameBank]);
        var abilityCodes = DecryptBank(banks[AbilityNameBank]);
        var abilityDescriptionCodes = DecryptBank(banks[AbilityDescriptionBank]);
        var categoryCodes = DecryptBank(banks[CategoryBank]);
        var moveEffectCodes = DecryptBank(banks[MoveEffectBank]);
        var itemEffectCodes = DecryptBank(banks[ItemEffectBank]);

        if (speciesCodes.Count < 494)
            throw new InvalidDataException($"物种名银行 232 条数异常：{speciesCodes.Count}。");
        if (speciesCodes[25] is not [2419, 869, 525])
            throw new InvalidDataException("物种名银行 232 的 ID 25 不是存档验证过的皮卡丘码点 2419,869,525。");
        if (categoryCodes.Count < 4 ||
            categoryCodes[1] is not [2524, 1266, 2603, 2221])
            throw new InvalidDataException("分类银行 803 的 ID 1 不是验证过的种子精灵码点。");
        if (abilityDescriptionCodes.Count < 10)
            throw new InvalidDataException($"特性说明银行 712 条数异常：{abilityDescriptionCodes.Count}。");

        var map = Charmap;
        if (DecodeCodes(speciesCodes[25], map) != "皮卡丘" ||
            DecodeCodes(categoryCodes[1], map) != "种子精灵" ||
            !map.TryGetValue(0x0399, out var leaf) || leaf != "叶" ||
            !map.TryGetValue(1235, out var miao) || miao != "妙" ||
            !map.TryGetValue(844, out var spoon) || spoon != "匙")
            throw new InvalidDataException("第四世代修正字库没有通过本 ROM 已证明字形校验。");
        if (!DecodeCodes(abilityDescriptionCodes[9], map).Contains('麻', StringComparison.Ordinal))
            throw new InvalidDataException("特性说明银行 712 的 ID 9 没有解出静电说明中的「麻痹」。");

        var unresolved = new HashSet<int>();
        return new OriginHeartGoldDecodedNames(
            DecodeTable(speciesCodes, map, unresolved, skipZero: true),
            DecodeTable(moveCodes, map, unresolved, skipZero: true),
            DecodeTable(itemCodes, map, unresolved, skipZero: false),
            DecodeTable(abilityCodes, map, unresolved, skipZero: false),
            DecodeTable(categoryCodes, map, unresolved, skipZero: true),
            DecodeTable(moveEffectCodes, map, unresolved, skipZero: true),
            DecodeTable(itemEffectCodes, map, unresolved, skipZero: true),
            DecodeTable(abilityDescriptionCodes, map, unresolved, skipZero: false),
            map.Count,
            unresolved.Count,
            [
                "物种名来自消息银行 232，不是分类银行 803。",
                "显示名使用第四世代修正字库按本 ROM 码点解码，不再使用官方名长度推测。",
                "字库已用存档皮卡丘码点、分类银行种子精灵、OT「叶」和招式码点 844「匙」校验。"
            ]);
    }

    public static IReadOnlyList<IReadOnlyList<int>> DecryptBank(byte[] raw)
    {
        if (raw.Length < 4)
            return [];
        var count = BitConverter.ToUInt16(raw, 0);
        var names = new List<IReadOnlyList<int>>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = BitConverter.ToInt32(raw, 4 + i * 8);
            var key = (0x91BD3 * (i + 1)) & 0xFFFF;
            var codes = new List<int>();
            var pos = offset;
            while (pos + 2 <= raw.Length)
            {
                var code = BitConverter.ToUInt16(raw, pos) ^ key;
                key = (key + 0x493D) & 0xFFFF;
                pos += 2;
                if (code == 0xFFFF) break;
                codes.Add(code);
            }
            names.Add(codes);
        }
        return names;
    }

    public static string DecodeCodes(IReadOnlyList<int> codes, IReadOnlyDictionary<int, string> map, ISet<int>? unresolved = null)
    {
        if (codes.Count == 0) return "";
        var text = new StringBuilder();
        foreach (var code in codes)
        {
            if (code is 0xE000 or 0x25BC) { text.Append('\n'); continue; }
            if (code is 0x25BD) { text.Append('\f'); continue; }
            if (code >= 0xFF00) continue;
            // 0x1DE is an empty slot in every Gen 4 chart and only trails a few item descriptions.
            if (code == 0x1DE) continue;
            if (map.TryGetValue(code, out var ch))
            {
                text.Append(ch);
                continue;
            }
            unresolved?.Add(code);
            text.Append('�');
        }
        return text.ToString();
    }

    private static Dictionary<int, string> DecodeTable(
        IReadOnlyList<IReadOnlyList<int>> bank,
        IReadOnlyDictionary<int, string> map,
        ISet<int> unresolved,
        bool skipZero)
    {
        var table = new Dictionary<int, string>();
        for (var i = 0; i < bank.Count; i++)
        {
            if (skipZero && i == 0) continue;
            table[i] = DecodeCodes(bank[i], map, unresolved);
        }
        return table;
    }

    private static Dictionary<int, string> LoadCharmap()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CharmapResource)
            ?? throw new InvalidDataException($"缺少字库资源 {CharmapResource}。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var map = new Dictionary<int, string>();
        while (reader.ReadLine() is { } line)
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
                continue;
            var tab = text.IndexOf('\t');
            if (tab <= 0)
                continue;
            if (!int.TryParse(text[..tab], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                continue;
            map[code] = text[(tab + 1)..];
        }

        if (map.Count < 7000 || !map.ContainsKey(7323))
            throw new InvalidDataException($"第四世代修正字库条目异常：{map.Count}。");
        return map;
    }
}
