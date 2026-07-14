namespace RocketTool.Core;

internal sealed class DisabledSaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "disabled";
    public static DisabledSaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    private static InvalidOperationException Disabled()
        => new("当前 Profile 未验证存档结构，存档读取和写入均已禁用。");

    public Gen3SaveDocument Open(string path, GameProfile profile) => throw Disabled();
    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes) => throw Disabled();
    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money) => throw Disabled();
    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data) => throw Disabled();
    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey) => throw Disabled();
    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket) => [];
    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId) => null;
    public string PocketName(int pocket) => $"口袋{pocket}";
    public void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data) => throw Disabled();
    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset) => throw Disabled();
}
