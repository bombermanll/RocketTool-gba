namespace RocketTool.Core;

public sealed class SpanishRocket305RuntimeAdapter : GameRuntimeAdapterBase
{
    public const string StrategyId = "spanish-rocket-305-runtime-v1";

    public SpanishRocket305RuntimeAdapter(GameProfile profile) : base(profile) { }

    public bool CanWriteAbilitySlot => true;
    protected override bool SupportsCaughtBall => true;
    public override PokemonDataLayout PartyLayout => PokemonDataLayout.SpanishRocket305Encrypted;
    public override PokemonDataLayout LiveBoxLayout => PokemonDataLayout.SpanishRocket305Encrypted;
    public override IReadOnlyList<int> MoveTypeIds { get; } = Enumerable.Range(0, 19).ToArray();

    public override string? EvolutionMethodKind(int method) => method switch
    {
        1 => "level",
        3 => "item",
        _ => null
    };

    public override IReadOnlyList<BagSlot> ReadLiveBag(MgbaBridgeClient bridge)
    {
        const uint descriptorTable = 0x02006254;
        const uint savePointer = 0x030040EC;
        const int pocketCount = 8;
        const int descriptorSize = 8;
        var saveBase = BitConverter.ToUInt32(bridge.Read(savePointer, 4));
        if (saveBase < Profile.Memory.EwramBase || saveBase + 0x46 > Profile.Memory.EwramBase + Profile.Memory.EwramSize)
            throw new InvalidOperationException("3.0.5 存档块指针没有落在 EWRAM，已拒绝读取背包。");
        var quantityKey = BitConverter.ToUInt16(bridge.Read(saveBase + 0x44, 2));
        var slots = new List<BagSlot>();
        var descriptors = bridge.Read(descriptorTable, pocketCount * descriptorSize);
        var ewramEnd = Profile.Memory.EwramBase + (uint)Profile.Memory.EwramSize;
        for (var pocket = 0; pocket < pocketCount; pocket++)
        {
            var entry = pocket * descriptorSize;
            var pointer = BitConverter.ToUInt32(descriptors, entry);
            var count = BitConverter.ToUInt16(descriptors, entry + 4) & 0x3FF;
            var selector = descriptors[entry + 5];
            if (pointer == 0 || count == 0) continue;
            var useXor = selector <= 0x1F;
            var usePlain = (selector >> 2) == 8;
            if (!useXor && !usePlain) continue;
            var bytes = checked(count * 4);
            if (pointer < Profile.Memory.EwramBase || pointer + (uint)bytes > ewramEnd) continue;
            var raw = bridge.Read(pointer, bytes);
            for (var index = 0; index < count; index++)
            {
                var item = BitConverter.ToUInt16(raw, index * 4);
                if (item == 0 || item > Profile.Limits.MaxItem) continue;
                var stored = BitConverter.ToUInt16(raw, index * 4 + 2);
                var quantity = useXor ? (ushort)(stored ^ quantityKey) : stored;
                if (quantity > Profile.Limits.MaxBagQuantity) continue;
                slots.Add(new BagSlot(
                    pointer + (uint)(index * 4),
                    item,
                    quantity,
                    useXor ? "实时口袋描述表，数量已按存档密钥还原" : "实时口袋描述表，数量为明文",
                    pocket));
            }
        }
        return slots;
    }
    protected override string ExpectedProfileId => "spanish-rocket-305-cn";
    protected override string ExpectedRuntimeStrategy => StrategyId;
    protected override string ExpectedPokemonStrategy => "spanish-rocket-305-pokemon-v1";
    protected override string ExpectedPartyStrategy => "spanish-rocket-305-party-v1";
    protected override string ExpectedBoxStrategy => "spanish-rocket-305-box-v1";
    protected override string ExpectedBagStrategy => "spanish-rocket-305-bag-v1";
    protected override string ExpectedSaveStrategy => SpanishRocket305SaveStrategy.StrategyId;
}
