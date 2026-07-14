namespace RocketTool.Core;

public sealed class PokemonDestinyRuntimeAdapter : GameRuntimeAdapterBase
{
    public const string StrategyId = "pokemon-destiny-runtime-v1";

    public PokemonDestinyRuntimeAdapter(GameProfile profile) : base(profile) { }

    public override PokemonDataLayout PartyLayout => PokemonDataLayout.UnboundCfruPlainParty;
    public override PokemonDataLayout LiveBoxLayout => PokemonDataLayout.DestinyCfruPlainBox;
    public override IReadOnlyList<int> MoveTypeIds { get; } = [.. Enumerable.Range(0, 18), 23];
    protected override string ExpectedProfileId => "pokemon-destiny-training-house-fix-cn";
    protected override string ExpectedRuntimeStrategy => StrategyId;
    protected override string ExpectedPokemonStrategy => "pokemon-destiny-pokemon-v1";
    protected override string ExpectedPartyStrategy => "pokemon-destiny-party-v1";
    protected override string ExpectedBoxStrategy => "pokemon-destiny-box-v1";
    protected override string ExpectedBagStrategy => "pokemon-destiny-bag-v1";
    protected override string ExpectedSaveStrategy => DestinyFireRedSaveStrategy.StrategyId;
}
