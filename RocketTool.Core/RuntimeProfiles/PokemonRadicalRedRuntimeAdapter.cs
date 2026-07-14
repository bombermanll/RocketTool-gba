namespace RocketTool.Core;

public sealed class PokemonRadicalRedRuntimeAdapter : GameRuntimeAdapterBase
{
    public const string StrategyId = "pokemon-radical-red-41-runtime-v1";

    public PokemonRadicalRedRuntimeAdapter(GameProfile profile) : base(profile) { }

    public override PokemonDataLayout PartyLayout => PokemonDataLayout.UnboundCfruPlainParty;
    public override PokemonDataLayout LiveBoxLayout => PokemonDataLayout.UnboundCfruPlainParty;
    public override IReadOnlyList<int> MoveTypeIds { get; } = [.. Enumerable.Range(0, 18), 23];
    protected override string ExpectedProfileId => "pokemon-radical-red-41-cn";
    protected override string ExpectedRuntimeStrategy => StrategyId;
    protected override string ExpectedPokemonStrategy => "pokemon-radical-red-41-pokemon-v1";
    protected override string ExpectedPartyStrategy => "pokemon-radical-red-41-party-v1";
    protected override string ExpectedBoxStrategy => "pokemon-radical-red-41-box-v1";
    protected override string ExpectedBagStrategy => "pokemon-radical-red-41-bag-v1";
    protected override string ExpectedSaveStrategy => RadicalRedCfruSaveStrategy.StrategyId;
}
