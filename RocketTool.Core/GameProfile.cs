using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RocketTool.Core;

public sealed record GameProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = string.Empty;
    public required GameProfileRomIdentity RomIdentity { get; init; }
    public required GameProfileStrategies Strategies { get; init; }
    public required GameProfileMemory Memory { get; init; }
    public required GameProfileRomTables RomTables { get; init; }
    public required GameProfileGraphics Graphics { get; init; }
    public required GameProfileLimits Limits { get; init; }
    public required GameProfileDataVerification DataVerification { get; init; }
    public required GameProfileFeatures Features { get; init; }
    public required GameProfileRuntime Runtime { get; init; }

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
    public required string Runtime { get; init; }
    public required string Pokemon { get; init; }
    public required string PartyScanner { get; init; }
    public required string BoxScanner { get; init; }
    public required string Bag { get; init; }
    public required string Save { get; init; }
}

public sealed record GameProfileRomIdentity
{
    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public required string HeaderTitle { get; init; }
    public required string GameCode { get; init; }
    public int RomSize { get; init; }
    public IReadOnlyList<GameProfileRomFingerprint> LiveFingerprints { get; init; } = [];
}

public sealed record GameProfileRomFingerprint
{
    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int Offset { get; init; }

    public required string Hex { get; init; }
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

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint PcBoxStoragePointerAddress { get; init; }

    public int PcBoxDataOffset { get; init; }
    public int LivePcBoxWritableSlotCount { get; init; }
    public int SavePcBoxWritableSlotCount { get; init; }
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
    public GameProfileRomTable? MachineMoves { get; init; }
    public GameProfileRomTable? TutorMoves { get; init; }
    public GameProfileRomTable? MachineCompatibility { get; init; }
    public GameProfileRomTable? TutorCompatibility { get; init; }
    public GameProfileRomTable? EggMoves { get; init; }
    public GameProfileRomTable? WildEncounters { get; init; }
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
    public string IconAssetRoot { get; init; } = "";

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int FrontSpriteTableOffset { get; init; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int IconSpriteTableOffset { get; init; }

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

public sealed record GameProfileDataVerification
{
    public int VisibleSpeciesCount { get; init; }
    public int VerifiedMenuIconCount { get; init; }
}

public sealed record GameProfileFeatures
{
    public bool LiveEditing { get; init; }
    public bool SaveEditing { get; init; }
    public required GameProfileFeatureAccess LiveParty { get; init; }
    public required GameProfileFeatureAccess SaveParty { get; init; }
    public required GameProfileFeatureAccess LiveBoxes { get; init; }
    public required GameProfileFeatureAccess SaveBoxes { get; init; }
    public required GameProfileFeatureAccess LiveBag { get; init; }
    public required GameProfileFeatureAccess SaveBag { get; init; }
    public required GameProfileFeatureAccess LiveTrainer { get; init; }
    public required GameProfileFeatureAccess SaveTrainer { get; init; }
    public bool BuiltInDex { get; init; }
    public bool MoveDex { get; init; }
    public bool ImportToParty { get; init; }
    public bool ImportToBoxes { get; init; }
    public bool ImportToSaveParty { get; init; }
    public bool ImportToSaveBoxes { get; init; }
    public bool Experiments { get; init; }

    [JsonIgnore]
    public bool Party => LiveParty.Read || SaveParty.Read;

    [JsonIgnore]
    public bool Boxes => LiveBoxes.Read || SaveBoxes.Read;

    [JsonIgnore]
    public bool Bag => LiveBag.Read || SaveBag.Read;

    [JsonIgnore]
    public bool Trainer => LiveTrainer.Read || SaveTrainer.Read;
}

public sealed record GameProfileFeatureAccess
{
    public bool Read { get; init; }
    public bool Write { get; init; }
}

public sealed record GameProfileRuntime
{
    public required GameProfileLiveBag LiveBag { get; init; }
    public required IReadOnlyList<GameProfileBagPocket> LiveBagPockets { get; init; }
    public required IReadOnlyList<GameProfileBagPocket> SaveBagPockets { get; init; }
    public IReadOnlyList<GameProfileItemPocketRange> ItemPocketFallbackRanges { get; init; } = [];
    public Dictionary<int, int> ItemPocketRemap { get; init; } = [];
    public int DefaultItemPocket { get; init; } = -1;
    public required GameProfileMachineRules Machines { get; init; }
    public required GameProfileShopProbe ShopProbe { get; init; }
    public required string BridgeExperimentProfile { get; init; }
    public bool ExperimentNeedsSaveBaseForNoEncounter { get; init; }
    public bool ExperimentBattleAssist { get; init; }
    public bool ExperimentTeleport { get; init; }
    public bool ExperimentNoEncounter { get; init; }
    public bool ExperimentWalkThroughWalls { get; init; }
    public int MaxTrainerMoney { get; init; } = 99_999_999;
    public int BagBatchQuantity { get; init; } = 240;
}

public sealed record GameProfileShopProbe
{
    public bool Enabled { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint ShopPriceAddress { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint ShopFirstItemAddress { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint SellPricePrimaryAddress { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint SellPriceFallbackAddress { get; init; }
}

public sealed record GameProfileItemPocketRange
{
    public int Start { get; init; }
    public int End { get; init; }
    public int Pocket { get; init; }
}

public sealed record GameProfileLiveBag
{
    public required string Mode { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint BaseAddress { get; init; }

    public required string QuantityKeyMode { get; init; }
    public IReadOnlyList<GameProfileLiveBagArea> Areas { get; init; } = [];
    public IReadOnlyList<GameProfileScannedBagArea> ScannedAreas { get; init; } = [];
}

public sealed record GameProfileScannedBagArea
{
    public int Pocket { get; init; }
    public required string Name { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint Offset { get; init; }

    public int Capacity { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint QuantityKey { get; init; }
}

public sealed record GameProfileLiveBagArea
{
    public int Pocket { get; init; }
    public required string Name { get; init; }

    [JsonConverter(typeof(FlexibleUInt32JsonConverter))]
    public uint Address { get; init; }

    public int Capacity { get; init; }
}

public sealed record GameProfileBagPocket
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public bool IsKeyItem { get; init; }
    public int BatchCapacity { get; init; }
}

public sealed record GameProfileMachineRules
{
    public int Pocket { get; init; }
    public required string Mode { get; init; }
    public int TmStartItem { get; init; }
    public int TmCount { get; init; }
    public int HmStartItem { get; init; }
    public int HmCount { get; init; }
    public int HmMoveStartIndex { get; init; }
    public IReadOnlyList<int> FallbackMoveIds { get; init; } = [];
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
        if (profile.RomIdentity is null || profile.Strategies is null || profile.Memory is null || profile.RomTables is null || profile.Graphics is null || profile.Limits is null || profile.DataVerification is null || profile.Features is null || profile.Runtime is null)
            throw new InvalidOperationException($"版本配置 {profile.Id} 缺少必需的配置分组。");
        if (profile.DataVerification.VisibleSpeciesCount <= 0 || profile.DataVerification.VisibleSpeciesCount > profile.Limits.MaxSpecies)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的可见物种校验数量无效。");
        if (profile.DataVerification.VerifiedMenuIconCount < 0 || profile.DataVerification.VerifiedMenuIconCount > profile.DataVerification.VisibleSpeciesCount)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的菜单图标校验数量无效。");
        if (profile.RomIdentity.RomSize <= 0 ||
            profile.RomIdentity.Sha256.Length != 64 ||
            string.IsNullOrWhiteSpace(profile.RomIdentity.HeaderTitle) ||
            profile.RomIdentity.GameCode.Length != 4 ||
            profile.RomIdentity.LiveFingerprints.Count == 0)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的 ROM 身份无效。");
        foreach (var fingerprint in profile.RomIdentity.LiveFingerprints)
        {
            if (fingerprint.Offset < 0 || fingerprint.Hex.Length == 0 || (fingerprint.Hex.Length & 1) != 0 ||
                fingerprint.Offset + fingerprint.Hex.Length / 2 > profile.RomIdentity.RomSize ||
                !fingerprint.Hex.All(Uri.IsHexDigit))
                throw new InvalidOperationException($"版本配置 {profile.Id} 的实时 ROM 指纹无效。");
        }
        if (profile.Memory.EwramSize <= 0 || profile.Memory.DefaultPartyBase == 0)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的实时内存参数无效。");
        if (profile.Memory.PcBoxCount <= 0 || profile.Memory.PcBoxSlots <= 0 || profile.Memory.PcBoxRecordSize <= 0)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的箱子参数无效。");
        foreach (var region in profile.Memory.PcBoxRegions)
        {
            var regionBytes = checked((uint)(region.BoxCount * profile.Memory.PcBoxSlots * profile.Memory.PcBoxRecordSize));
            var regionEwramEnd = profile.Memory.EwramBase + checked((uint)profile.Memory.EwramSize);
            if (region.FirstBox <= 0 || region.BoxCount <= 0 ||
                region.FirstBox + region.BoxCount - 1 > profile.Memory.PcBoxCount ||
                region.Address < profile.Memory.EwramBase ||
                (ulong)region.Address + regionBytes > regionEwramEnd)
                throw new InvalidOperationException($"版本配置 {profile.Id} 的箱子区域无效。");
        }
        if (profile.Features.LiveBoxes.Read &&
            profile.Memory.PcBoxStoragePointerAddress == 0 &&
            profile.Memory.PcBoxRegions.Count > 0)
        {
            var coveredBoxes = profile.Memory.PcBoxRegions
                .SelectMany(region => Enumerable.Range(region.FirstBox, region.BoxCount))
                .OrderBy(box => box)
                .ToArray();
            var expectedBoxes = Enumerable.Range(1, profile.Memory.PcBoxCount).ToArray();
            if (!coveredBoxes.SequenceEqual(expectedBoxes))
                throw new InvalidOperationException($"版本配置 {profile.Id} 已启用实时箱子读取，但箱子区域没有无重叠地覆盖全部箱子。");
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
        foreach (var (name, access) in new[]
                 {
                     ("liveParty", profile.Features.LiveParty),
                     ("saveParty", profile.Features.SaveParty),
                     ("liveBoxes", profile.Features.LiveBoxes),
                     ("saveBoxes", profile.Features.SaveBoxes),
                     ("liveBag", profile.Features.LiveBag),
                     ("saveBag", profile.Features.SaveBag),
                     ("liveTrainer", profile.Features.LiveTrainer),
                     ("saveTrainer", profile.Features.SaveTrainer)
                 })
        {
            if (access is null || access.Write && !access.Read)
                throw new InvalidOperationException($"版本配置 {profile.Id} 的功能能力 {name} 无效：写入必须建立在已验证读取上。");
        }
        if (!profile.Features.LiveEditing &&
            new[] { profile.Features.LiveParty, profile.Features.LiveBoxes, profile.Features.LiveBag, profile.Features.LiveTrainer }.Any(access => access.Read || access.Write))
            throw new InvalidOperationException($"版本配置 {profile.Id} 已关闭实时模式，但仍启用了实时能力。");
        if (!profile.Features.SaveEditing &&
            new[] { profile.Features.SaveParty, profile.Features.SaveBoxes, profile.Features.SaveBag, profile.Features.SaveTrainer }.Any(access => access.Read || access.Write))
            throw new InvalidOperationException($"版本配置 {profile.Id} 已关闭存档模式，但仍启用了存档能力。");
        if (profile.Features.ImportToParty && !profile.Features.LiveParty.Write)
            throw new InvalidOperationException($"版本配置 {profile.Id} 启用了导入队伍，但未启用实时队伍写入。");
        if (profile.Features.ImportToBoxes && !profile.Features.LiveBoxes.Write)
            throw new InvalidOperationException($"版本配置 {profile.Id} 启用了导入箱子，但未启用实时箱子写入。");
        if (profile.Features.ImportToSaveParty && !profile.Features.SaveParty.Write)
            throw new InvalidOperationException($"版本配置 {profile.Id} 启用了存档导入队伍，但未启用存档队伍写入。");
        if (profile.Features.ImportToSaveBoxes && !profile.Features.SaveBoxes.Write)
            throw new InvalidOperationException($"版本配置 {profile.Id} 启用了存档导入箱子，但未启用存档箱子写入。");
        if (profile.Features.Experiments &&
            string.Equals(profile.Runtime.BridgeExperimentProfile, "disabled", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"版本配置 {profile.Id} 启用了实验功能，但 bridgeExperimentProfile 仍为 disabled。");
        if (profile.Features.Experiments &&
            !profile.Runtime.ExperimentBattleAssist &&
            !profile.Runtime.ExperimentTeleport &&
            !profile.Runtime.ExperimentNoEncounter &&
            !profile.Runtime.ExperimentWalkThroughWalls)
            throw new InvalidOperationException($"版本配置 {profile.Id} 启用了实验功能，但没有声明任何已验证的实验能力。");
        if (profile.Strategies.Save == DisabledSaveStrategy.StrategyId && profile.Features.SaveEditing)
            throw new InvalidOperationException($"版本配置 {profile.Id} 使用 disabled 存档策略时不能启用存档模式。");
        if (profile.Graphics.SpritesVerified &&
            (string.IsNullOrWhiteSpace(profile.Graphics.SpriteAssetRoot) ||
             profile.Graphics.FrontSpriteTableOffset <= 0 ||
             profile.Graphics.NormalPaletteTableOffset <= 0))
            throw new InvalidOperationException($"版本配置 {profile.Id} 的精灵图片配置无效。");
        foreach (var (name, value) in new[]
                 {
                     ("runtime", profile.Strategies.Runtime),
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

        if (profile.Runtime.LiveBag is null || profile.Runtime.LiveBagPockets is null || profile.Runtime.SaveBagPockets is null || profile.Runtime.Machines is null || profile.Runtime.ShopProbe is null)
            throw new InvalidOperationException($"版本配置 {profile.Id} 缺少运行时隔离参数。");
        if (profile.Runtime.LiveBag.Mode is not ("disabled" or "scanned" or "fixed"))
            throw new InvalidOperationException($"版本配置 {profile.Id} 的 liveBag.mode 无效。");
        if (profile.Runtime.LiveBag.Mode == "fixed" &&
            (profile.Runtime.LiveBag.BaseAddress == 0 || profile.Runtime.LiveBag.Areas.Count == 0))
            throw new InvalidOperationException($"版本配置 {profile.Id} 的固定实时背包区域为空。");
        if (profile.Runtime.LiveBag.Mode == "scanned" && profile.Runtime.LiveBag.ScannedAreas.Count == 0)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的扫描实时背包区域为空。");
        if (profile.Runtime.LiveBag.ScannedAreas.Any(area => area.Pocket <= 0 || area.Capacity <= 0 || area.QuantityKey > ushort.MaxValue))
            throw new InvalidOperationException($"版本配置 {profile.Id} 的扫描背包口袋定义无效。");
        var ewramEnd = profile.Memory.EwramBase + checked((uint)profile.Memory.EwramSize);
        var totalBoxSlots = checked(profile.Memory.PcBoxCount * profile.Memory.PcBoxSlots);
        if (profile.Memory.PcBoxStoragePointerAddress != 0 &&
            profile.Memory.PcBoxStoragePointerAddress is < 0x03000000 or >= 0x03008000)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的 PC 箱子指针地址不在 IWRAM 范围内。");
        if (profile.Memory.PcBoxDataOffset < 0 ||
            profile.Memory.LivePcBoxWritableSlotCount is < 0 ||
            profile.Memory.LivePcBoxWritableSlotCount > totalBoxSlots ||
            profile.Memory.SavePcBoxWritableSlotCount is < 0 ||
            profile.Memory.SavePcBoxWritableSlotCount > totalBoxSlots)
            throw new InvalidOperationException($"版本配置 {profile.Id} 的 PC 箱子可写槽位范围无效。");
        if (profile.Runtime.LiveBag.Areas.Any(area =>
                area.Pocket <= 0 || area.Capacity <= 0 || area.Address < profile.Memory.EwramBase ||
                area.Address + checked((uint)area.Capacity * 4U) > ewramEnd))
            throw new InvalidOperationException($"版本配置 {profile.Id} 的固定背包区域超出 EWRAM。");
        if (profile.Runtime.LiveBagPockets.Concat(profile.Runtime.SaveBagPockets).Any(pocket => pocket.Id <= 0 || string.IsNullOrWhiteSpace(pocket.Name)))
            throw new InvalidOperationException($"版本配置 {profile.Id} 的背包口袋定义无效。");
        if (profile.Runtime.ItemPocketFallbackRanges.Any(range => range.Start <= 0 || range.End < range.Start || range.Pocket <= 0))
            throw new InvalidOperationException($"版本配置 {profile.Id} 的道具口袋回退范围无效。");
        if (profile.Runtime.ShopProbe.Enabled &&
            new[] { profile.Runtime.ShopProbe.ShopPriceAddress, profile.Runtime.ShopProbe.ShopFirstItemAddress, profile.Runtime.ShopProbe.SellPricePrimaryAddress, profile.Runtime.ShopProbe.SellPriceFallbackAddress }.Any(address => address == 0))
            throw new InvalidOperationException($"版本配置 {profile.Id} 已启用商店探测，但地址不完整。");
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
