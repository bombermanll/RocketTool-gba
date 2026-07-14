namespace RocketTool.Core;

public sealed class PokemonUnboundRuntimeAdapter : GameRuntimeAdapterBase
{
    public const string StrategyId = "pokemon-unbound-211-runtime-v1";

    public PokemonUnboundRuntimeAdapter(GameProfile profile) : base(profile) { }

    public override PokemonDataLayout PartyLayout => PokemonDataLayout.UnboundCfruPlainParty;
    public override PokemonDataLayout LiveBoxLayout => PokemonDataLayout.UnboundCfruPlainParty;
    public override IReadOnlyList<int> MoveTypeIds { get; } = [.. Enumerable.Range(0, 18), 23];
    protected override string ExpectedProfileId => "pokemon-unbound-211-cn";
    protected override string ExpectedRuntimeStrategy => StrategyId;
    protected override string ExpectedPokemonStrategy => "pokemon-unbound-211-pokemon-v1";
    protected override string ExpectedPartyStrategy => "pokemon-unbound-211-party-v1";
    protected override string ExpectedBoxStrategy => "pokemon-unbound-211-box-v1";
    protected override string ExpectedBagStrategy => "pokemon-unbound-211-bag-v1";
    protected override string ExpectedSaveStrategy => UnboundCfruSaveStrategy.StrategyId;
}
