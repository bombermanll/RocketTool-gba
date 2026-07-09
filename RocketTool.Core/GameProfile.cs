using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RocketTool.Core;

public sealed record GameProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = string.Empty;
    public required GameProfileStrategies Strategies { get; init; }
    public required GameProfileMemory Memory { get; init; }
    public required GameProfileRomTables RomTables { get; init; }
    public required GameProfileGraphics Graphics { get; init; }
    public required GameProfileLimits Limits { get; init; }
    public required GameProfileFeatures Features { get; init; }

    [JsonIgnore]
    public string ProfileDirectory { get; init; } = string.Empty;

    [JsonIgnore]
    public string DatabaseDirectory => Path.Combine(ProfileDirectory, "db");

    [JsonIgnore]
    public string DatabaseResourcePrefix => $"profiles.{Id}.db";

    public override string ToString() => DisplayName;
}

public sealed record GameProfileStrategies
{
    public required string Pokemon { get; init; }
    public required string PartyScanner { get; init; }
    public required string BoxScanner { get; init; }
    public required string Bag { get; init; }
    public required string Save { get; init; }
}

public sealed record GameProfileMemory
{
    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint EwramBase { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int EwramSize { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint DefaultPartyBase { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int PartyCountOffsetFromPartyBase { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint SaveBlock1PointerAddress { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint SaveBlock2PointerAddress { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint SaveBlock1MoneyOffset { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint SaveBlock2EncryptionKeyOffset { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int SaveBlock2PlayerOtIdOffset { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int SaveBlock2HeaderLength { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int PlayerNameLength { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int PokemonOtNameOffset { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int PokemonOtNameLength { get; init; }

    public int PcBoxCount { get; init; } = 14;
    public int PcBoxSlots { get; init; } = 30;
    public int PcBoxRecordSize { get; init; } = BoxPokemon.Size;
    public IReadOnlyList<GameProfileBoxRegion> PcBoxRegions { get; init; } = [];
}

public sealed record GameProfileBoxRegion
{
    public int FirstBox { get; init; }
    public int BoxCount { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint Address { get; init; }
}

public sealed record GameProfileRomTables
{
    public required GameProfileRomTable BaseStats { get; init; }
    public required GameProfileRomTable Moves { get; init; }
    public required GameProfileRomTable Items { get; init; }
    public required GameProfileRomTable Evolutions { get; init; }
    public required GameProfileRomTable LevelMoves { get; init; }
    public required GameProfileRomTable Experience { get; init; }
}

public sealed record GameProfileRomTable
{
    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int Offset { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int EntrySize { get; init; }

    public int Count { get; init; }

    public int EntriesPerRecord { get; init; } = 1;
}

public sealed record GameProfileGraphics
{
    public bool SpritesVerified { get; init; }
    public required string SpriteAssetRoot { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int FrontSpriteTableOffset { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int NormalPaletteTableOffset { get; init; }

    public int SpriteIndexAdjustment { get; init; }
    public int PaletteIndexAdjustment { get; init; }
    public int EggSpeciesId { get; init; }
}

public sealed record GameProfileLimits
{
    public int MaxSpecies { get; init; }
    public int MaxMove { get; init; }
    public int MaxItem { get; init; }
    public int MaxAbility { get; init; }
    public int MaxLevel { get; init; }
    public int MaxBagQuantity { get; init; } = 255;
    public Dictionary<int, int> MaxBagQuantityByPocket { get; init; } = [];

    public int MaxBagQuantityForPocket(int pocket)
        => MaxBagQuantityByPocket.TryGetValue(pocket, out var max) ? max : MaxBagQuantity;
}

public sealed record GameProfileFeatures
{
    public bool LiveEditing { get; init; }
    public bool SaveEditing { get; init; }
    public bool Party { get; init; }
    public bool Boxes { get; init; }
    public bool Bag { get; init; }
    public bool Trainer { get; init; }
    public bool Experiments { get; init; }
}

public static class GameProfileCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<GameProfile> Load(string profilesDirectory, Assembly? resourceAssembly = null)
    {
        var profiles = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);

        if (resourceAssembly is not null)
        {
            foreach (var resourceName in resourceAssembly.GetManifestResourceNames()
                         .Where(name => NormalizeResourceName(name).StartsWith("profiles.", StringComparison.OrdinalIgnoreCase))
                         .Where(name => NormalizeResourceName(name).EndsWith(".profile.json", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = resourceAssembly.GetManifestResourceStream(resourceName)
                                   ?? throw new InvalidOperationException($"无法读取内嵌版本配置：{resourceName}");
                var parsed = ReadProfile(stream, $"内嵌资源 {resourceName}");
                var profile = parsed with { ProfileDirectory = Path.Combine(profilesDirectory, parsed.Id) };
                Validate(profile);
                profiles[profile.Id] = profile;
            }
        }

        if (Directory.Exists(profilesDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(profilesDirectory, "profile.json", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                var profile = ReadProfile(stream, path) with
                {
                    ProfileDirectory = Path.GetDirectoryName(path) ?? profilesDirectory
                };
                Validate(profile);
                profiles[profile.Id] = profile;
            }
        }

        if (profiles.Count == 0)
            throw new InvalidOperationException("没有找到任何游戏版本配置。请确认 profiles 目录或内嵌 profile.json 完整。");

        return profiles.Values.OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static GameProfile ReadProfile(Stream stream, string source)
    {
        try
        {
            return JsonSerializer.Deserialize<GameProfile>(stream, JsonOptions)
                   ?? throw new InvalidOperationException("配置内容为空。");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidOperationException($"版本配置解析失败：{source}：{ex.Message}", ex);
        }
    }

    private static void Validate(GameProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new InvalidOperationException("版本配置 ID 只能包含英文字母、数字、短横线和下划线。");
        if (string.IsNullOrWhiteSpace(profile.DisplayName))
            throw new InvalidOperationException($"版本配置 {profile.Id} 缺少 displayName。");
        if (profile.Strategies is null || profile.Memory is null || profile.RomTables is null || profile.Graphics is null || profile.Limits is null || profile.Features is null)
            throw new InvalidOperationException($"版本配置 {profile.Id} 缺少必需的配置分组。");
        if (profile.Memory.EwramSize <= 0 || profile.Memory.DefaultPartyBase == 0)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的实时内存参数无效。");
        if (profile.Memory.PcBoxCount <= 0 || profile.Memory.PcBoxSlots <= 0 || profile.Memory.PcBoxRecordSize <= 0)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的箱子参数无效。");
        foreach (var region in profile.Memory.PcBoxRegions)
        {
            if (region.FirstBox <= 0 || region.BoxCount <= 0 || region.Address < profile.Memory.EwramBase)
                throw new InvalidOperationException($"版本配置 {profile.Id} 的箱子区域无效。");
        }
        if (profile.Limits.MaxSpecies <= 0 || profile.Limits.MaxMove <= 0 || profile.Limits.MaxItem <= 0)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的数据上限无效。");
        if (profile.Limits.MaxBagQuantity <= 0 || profile.Limits.MaxBagQuantity > ushort.MaxValue)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的背包数量上限无效。");
        foreach (var (pocket, max) in profile.Limits.MaxBagQuantityByPocket)
        {
            if (pocket <= 0 || max <= 0 || max > ushort.MaxValue)
                throw new InvalidOperationException($"版本配置 {profile.Id} 的口袋 {pocket} 背包数量上限无效。");
        }
        if (!profile.Features.LiveEditing && !profile.Features.SaveEditing)
            throw new InvalidOperationException($"版本配置 {profile.Id} 没有启用任何编辑模式。");
        if (profile.Graphics.SpritesVerified &&
            (string.IsNullOrWhiteSpace(profile.Graphics.SpriteAssetRoot) ||
             profile.Graphics.FrontSpriteTableOffset <= 0 ||
             profile.Graphics.NormalPaletteTableOffset <= 0))
            throw new InvalidOperationException($"版本配置 {profile.Id} 的精灵图片配置无效。");
        foreach (var (name, value) in new[]
                 {
                     ("pokemon", profile.Strategies.Pokemon),
                     ("partyScanner", profile.Strategies.PartyScanner),
                     ("boxScanner", profile.Strategies.BoxScanner),
                     ("bag", profile.Strategies.Bag),
                     ("save", profile.Strategies.Save)
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"版本配置 {profile.Id} 缺少策略 {name}。");
        }

        foreach (var (name, table) in new[]
                 {
                     ("baseStats", profile.RomTables.BaseStats),
                     ("moves", profile.RomTables.Moves),
                     ("items", profile.RomTables.Items),
                     ("evolutions", profile.RomTables.Evolutions),
                     ("levelMoves", profile.RomTables.LevelMoves),
                     ("experience", profile.RomTables.Experience)
                 })
        {
            if (table is null || table.Offset < 0 || table.EntrySize <= 0 || table.Count <= 0 || table.EntriesPerRecord <= 0)
                throw new InvalidOperationException($"版本配置 {profile.Id} 的 ROM 表 {name} 无效。");
        }
    }

    private static string NormalizeResourceName(string name) => name.Replace('\\', '.').Replace('/', '.');
}

public sealed class FlexibleUInt32JsonConverter : JsonConverter<uint>
{
    public override uint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetUInt32(),
            JsonTokenType.String => Parse(reader.GetString()),
            _ => throw new JsonException("应为十进制数字或 0x 十六进制字符串。")
        };

    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options)
        => writer.WriteStringValue($"0x{value:X}");

    private static uint Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new JsonException("数值不能为空。");
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(value[2..], 16)
            : Convert.ToUInt32(value);
    }
}

public sealed class FlexibleInt32JsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String => Parse(reader.GetString()),
            _ => throw new JsonException("应为十进制数字或 0x 十六进制字符串。")
        };

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteStringValue(value < 0 ? value.ToString() : $"0x{value:X}");

    private static int Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new JsonException("数值不能为空。");
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(value[2..], 16)
            : Convert.ToInt32(value);
    }
}
