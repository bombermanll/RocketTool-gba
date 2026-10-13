namespace RocketTool.Core;

public sealed class PokemonShuiyin12RuntimeAdapter : GameRuntimeAdapterBase
{
    public const string StrategyId = "pokemon-shuiyin-12-runtime-v1";

    public PokemonShuiyin12RuntimeAdapter(GameProfile profile) : base(profile) { }

    public override PokemonDataLayout PartyLayout => PokemonDataLayout.UnboundCfruPlainParty;
    public override PokemonDataLayout LiveBoxLayout => PokemonDataLayout.UnboundCfruPlainParty;
    protected override bool SupportsCaughtBall => true;
    public override IReadOnlyList<int> MoveTypeIds { get; } = [.. Enumerable.Range(0, 18)];
    protected override string ExpectedProfileId => "pokemon-shuiyin-12-cn";
    protected override string ExpectedRuntimeStrategy => StrategyId;
    protected override string ExpectedPokemonStrategy => "pokemon-shuiyin-12-pokemon-v1";
    protected override string ExpectedPartyStrategy => "pokemon-shuiyin-12-party-v1";
    protected override string ExpectedBoxStrategy => "pokemon-shuiyin-12-box-v1";
    protected override string ExpectedBagStrategy => "pokemon-shuiyin-12-bag-v1";
    protected override string ExpectedSaveStrategy => Shuiyin12SaveStrategy.StrategyId;
}
