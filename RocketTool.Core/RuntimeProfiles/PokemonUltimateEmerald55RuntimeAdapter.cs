namespace RocketTool.Core;

public sealed class PokemonUltimateEmerald55RuntimeAdapter : GameRuntimeAdapterBase
{
    public const string StrategyId = "pokemon-ultimate-emerald-55-runtime-v1";

    public PokemonUltimateEmerald55RuntimeAdapter(GameProfile profile) : base(profile) { }

    public override PokemonDataLayout PartyLayout => PokemonDataLayout.UltimateEmerald55CfruPlainParty;
    public override PokemonDataLayout LiveBoxLayout => PokemonDataLayout.UltimateEmerald55CfruPlainBox;
    public override IReadOnlyList<int> MoveTypeIds { get; } = [.. Enumerable.Range(0, 19), 23];
    protected override string ExpectedProfileId => "pokemon-ultimate-emerald-55-cn";
    protected override string ExpectedRuntimeStrategy => StrategyId;
    protected override string ExpectedPokemonStrategy => "pokemon-ultimate-emerald-55-pokemon-v1";
    protected override string ExpectedPartyStrategy => "pokemon-ultimate-emerald-55-party-v1";
    protected override string ExpectedBoxStrategy => "pokemon-ultimate-emerald-55-box-v1";
    protected override string ExpectedBagStrategy => "pokemon-ultimate-emerald-55-bag-v1";
    protected override string ExpectedSaveStrategy => UltimateEmerald55SaveStrategy.StrategyId;
    protected override bool SupportsCaughtBall => true;

    public override void InitializeImportedParty(PartyPokemon mon, byte level, byte trainerGender)
        => mon.SetUltimateEmerald55OriginInfo(level, trainerGender);

    public override void InitializeImportedBox(BoxPokemon mon, byte level, byte trainerGender)
        => mon.SetUltimateEmerald55OriginInfo(level, trainerGender);
}
