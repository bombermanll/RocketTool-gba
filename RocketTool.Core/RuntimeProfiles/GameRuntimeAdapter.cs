using System.Text;

namespace RocketTool.Core;

public enum GameDataSurface
{
    Party,
    Boxes,
    Bag,
    Trainer
}

public interface IGameRuntimeAdapter
{
    GameProfile Profile { get; }
    PokemonDataLayout PartyLayout { get; }
    PokemonDataLayout LiveBoxLayout { get; }
    IReadOnlyList<int> MoveTypeIds { get; }
    bool UsesFixedLiveBag { get; }
    bool UsesScannedLiveBag { get; }
    int MachinePocket { get; }
    string BridgeExperimentProfile { get; }
    string? SpeciesFormLabel(int species);

    void ValidateLiveRom(MgbaBridgeClient bridge);
    bool CanRead(GameDataSurface surface, bool live);
    bool CanWrite(GameDataSurface surface, bool live);
    void EnsureCanRead(GameDataSurface surface, bool live);
    void EnsureCanWrite(GameDataSurface surface, bool live);
    IReadOnlyList<GameProfileBagPocket> BagPockets(bool live);
    bool IsConcreteBagPocket(int pocket, bool live);
    bool IsKeyItemPocket(int pocket, bool live);
    string PocketName(int pocket, bool live);
    int BagBatchCapacity(int pocket, bool live);
    int RemapItemPocket(int pocket);
    int FallbackPocketOfItem(int item);
    GameProfileLiveBagArea? LiveBagArea(int pocket);
    IReadOnlyList<BagPocketDefinition> ScannedBagDefinitions { get; }
}

public abstract class GameRuntimeAdapterBase : IGameRuntimeAdapter
{
    protected GameRuntimeAdapterBase(GameProfile profile)
    {
        Profile = profile;
        ValidateIsolation(profile);
    }

    public GameProfile Profile { get; }
    public abstract PokemonDataLayout PartyLayout { get; }
    public abstract PokemonDataLayout LiveBoxLayout { get; }
    public abstract IReadOnlyList<int> MoveTypeIds { get; }
    protected abstract string ExpectedProfileId { get; }
    protected abstract string ExpectedRuntimeStrategy { get; }
    protected abstract string ExpectedPokemonStrategy { get; }
    protected abstract string ExpectedPartyStrategy { get; }
    protected abstract string ExpectedBoxStrategy { get; }
    protected abstract string ExpectedBagStrategy { get; }
    protected abstract string ExpectedSaveStrategy { get; }

    public bool UsesFixedLiveBag => Profile.Runtime.LiveBag.Mode == "fixed";
    public bool UsesScannedLiveBag => Profile.Runtime.LiveBag.Mode == "scanned";
    public int MachinePocket => Profile.Runtime.Machines.Pocket;
    public string BridgeExperimentProfile => Profile.Runtime.BridgeExperimentProfile;
    public virtual string? SpeciesFormLabel(int species) => null;

    public void ValidateLiveRom(MgbaBridgeClient bridge)
    {
        var identity = Profile.RomIdentity;
        var header = bridge.Read(0x080000A0, 16);
        var title = Encoding.ASCII.GetString(header, 0, 12).TrimEnd('\0', ' ');
        var gameCode = Encoding.ASCII.GetString(header, 12, 4);
        ProfileRomIdentityValidator.Validate(
            Profile,
            title,
            gameCode,
            fingerprint => bridge.Read(
                0x08000000u + checked((uint)fingerprint.Offset),
                Convert.FromHexString(fingerprint.Hex).Length));
    }

    public bool CanRead(GameDataSurface surface, bool live) => Access(surface, live).Read;

    public bool CanWrite(GameDataSurface surface, bool live) => Access(surface, live).Write;

    public void EnsureCanRead(GameDataSurface surface, bool live)
    {
        if (!CanRead(surface, live))
            throw new InvalidOperationException($"版本 {Profile.DisplayName} 未启用{SourceName(live)}{SurfaceName(surface)}读取。");
    }

    public void EnsureCanWrite(GameDataSurface surface, bool live)
    {
        if (!CanWrite(surface, live))
            throw new InvalidOperationException($"版本 {Profile.DisplayName} 未验证或未启用{SourceName(live)}{SurfaceName(surface)}写入。");
    }

    public IReadOnlyList<GameProfileBagPocket> BagPockets(bool live)
        => live ? Profile.Runtime.LiveBagPockets : Profile.Runtime.SaveBagPockets;

    public bool IsConcreteBagPocket(int pocket, bool live)
        => BagPockets(live).Any(candidate => candidate.Id == pocket);

    public bool IsKeyItemPocket(int pocket, bool live)
        => BagPockets(live).FirstOrDefault(candidate => candidate.Id == pocket)?.IsKeyItem == true;

    public string PocketName(int pocket, bool live)
        => BagPockets(live).FirstOrDefault(candidate => candidate.Id == pocket)?.Name
           ?? (pocket == -1 ? "未知/候选" : $"#{pocket}");

    public int BagBatchCapacity(int pocket, bool live)
        => BagPockets(live).FirstOrDefault(candidate => candidate.Id == pocket)?.BatchCapacity ?? 0;

    public int RemapItemPocket(int pocket)
        => Profile.Runtime.ItemPocketRemap.TryGetValue(pocket, out var remapped)
            ? remapped
            : IsConcreteBagPocket(pocket, true) || IsConcreteBagPocket(pocket, false)
                ? pocket
                : Profile.Runtime.DefaultItemPocket;

    public int FallbackPocketOfItem(int item)
        => Profile.Runtime.ItemPocketFallbackRanges
               .FirstOrDefault(range => item >= range.Start && item <= range.End)?.Pocket
           ?? Profile.Runtime.DefaultItemPocket;

    public GameProfileLiveBagArea? LiveBagArea(int pocket)
        => Profile.Runtime.LiveBag.Areas.FirstOrDefault(area => area.Pocket == pocket);

    public IReadOnlyList<BagPocketDefinition> ScannedBagDefinitions
        => Profile.Runtime.LiveBag.ScannedAreas
            .Select(area => new BagPocketDefinition(area.Pocket, area.Name, area.Offset, area.Capacity, true, checked((ushort)area.QuantityKey)))
            .ToArray();

    private GameProfileFeatureAccess Access(GameDataSurface surface, bool live)
        => (surface, live) switch
        {
            (GameDataSurface.Party, true) => Profile.Features.LiveParty,
            (GameDataSurface.Party, false) => Profile.Features.SaveParty,
            (GameDataSurface.Boxes, true) => Profile.Features.LiveBoxes,
            (GameDataSurface.Boxes, false) => Profile.Features.SaveBoxes,
            (GameDataSurface.Bag, true) => Profile.Features.LiveBag,
            (GameDataSurface.Bag, false) => Profile.Features.SaveBag,
            (GameDataSurface.Trainer, true) => Profile.Features.LiveTrainer,
            (GameDataSurface.Trainer, false) => Profile.Features.SaveTrainer,
            _ => throw new ArgumentOutOfRangeException(nameof(surface))
        };

    private void ValidateIsolation(GameProfile profile)
    {
        var actual = new[]
        {
            profile.Id,
            profile.Strategies.Runtime,
            profile.Strategies.Pokemon,
            profile.Strategies.PartyScanner,
            profile.Strategies.BoxScanner,
            profile.Strategies.Bag,
            profile.Strategies.Save
        };
        var expected = new[]
        {
            ExpectedProfileId,
            ExpectedRuntimeStrategy,
            ExpectedPokemonStrategy,
            ExpectedPartyStrategy,
            ExpectedBoxStrategy,
            ExpectedBagStrategy,
            ExpectedSaveStrategy
        };
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException($"版本 {profile.Id} 的策略组合不是独立适配器 {ExpectedRuntimeStrategy} 所要求的组合，已拒绝加载。");
        var expectedBoxSize = LiveBoxLayout == PokemonDataLayout.UnboundCfruPlainParty
            ? BoxPokemon.UnboundCompressedSize
            : BoxPokemon.Size;
        if (profile.Memory.PcBoxRecordSize != expectedBoxSize)
            throw new InvalidOperationException($"版本 {profile.Id} 的箱子记录大小与其专属适配器布局不一致，已拒绝加载。");
    }

    private static string SurfaceName(GameDataSurface surface) => surface switch
    {
        GameDataSurface.Party => "队伍",
        GameDataSurface.Boxes => "箱子",
        GameDataSurface.Bag => "背包",
        GameDataSurface.Trainer => "训练家",
        _ => surface.ToString()
    };

    private static string SourceName(bool live) => live ? "实时" : "存档";
}

public static class GameRuntimeAdapterCatalog
{
    public static IGameRuntimeAdapter ForProfile(GameProfile profile)
        => profile.Strategies.Runtime switch
        {
            SpanishRocketRuntimeAdapter.StrategyId => new SpanishRocketRuntimeAdapter(profile),
            PokemonUnboundRuntimeAdapter.StrategyId => new PokemonUnboundRuntimeAdapter(profile),
            PokemonDestinyRuntimeAdapter.StrategyId => new PokemonDestinyRuntimeAdapter(profile),
            PokemonRadicalRedRuntimeAdapter.StrategyId => new PokemonRadicalRedRuntimeAdapter(profile),
            var id => throw new NotSupportedException($"当前程序不支持运行时策略：{id}")
        };
}
