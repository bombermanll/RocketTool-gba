namespace RocketTool.Core;

public sealed class OriginHeartGold40RuntimeAdapter : GameRuntimeAdapterBase
{
    public const string StrategyId = "origin-heartgold-40-runtime-v1";

    public OriginHeartGold40RuntimeAdapter(GameProfile profile) : base(profile) { }

    public override PokemonDataLayout PartyLayout => PokemonDataLayout.Disabled;
    public override PokemonDataLayout LiveBoxLayout => PokemonDataLayout.Disabled;
    public override IReadOnlyList<int> MoveTypeIds { get; } = Enumerable.Range(0, 18).ToArray();
    protected override string ExpectedProfileId => "pokemon-origin-heartgold-40-cn";
    protected override string ExpectedRuntimeStrategy => StrategyId;
    protected override string ExpectedPokemonStrategy => "disabled";
    protected override string ExpectedPartyStrategy => "disabled";
    protected override string ExpectedBoxStrategy => "disabled";
    protected override string ExpectedBagStrategy => "disabled";
    protected override string ExpectedSaveStrategy => OriginHeartGold40SaveStrategy.StrategyId;

    public override void ValidateLiveRom(MgbaBridgeClient bridge)
        => throw new InvalidOperationException($"版本 {Profile.DisplayName} 是 NDS 改版，当前不支持 mGBA 实时连接。");
}
