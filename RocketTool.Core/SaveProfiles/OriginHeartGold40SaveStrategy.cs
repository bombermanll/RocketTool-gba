namespace RocketTool.Core;

// NDS save editing is implemented by RocketTool.Nds and gated by the NDS profile.
// This adapter exists only to keep the profile strategy combination isolated from Gen3.
internal sealed class OriginHeartGold40SaveStrategy : IGen3SaveStrategy
{
    public const string StrategyId = "origin-heartgold-40-save-v1";
    public static OriginHeartGold40SaveStrategy Instance { get; } = new();
    public string Id => StrategyId;

    private static InvalidOperationException Unsupported()
        => new("起源心金存档请通过 NDS 专用存档编辑器处理，不能走 GBA Gen3 存档入口。");

    public Gen3SaveDocument Open(string path, GameProfile profile) => throw Unsupported();
    public void ReplaceTrainerName(Gen3SaveDocument document, ReadOnlySpan<byte> nameBytes) => throw Unsupported();
    public void ReplaceTrainerMoney(Gen3SaveDocument document, uint money) => throw Unsupported();
    public void WriteBoxPokemon(Gen3SaveDocument document, int globalSlot, ReadOnlySpan<byte> data) => throw Unsupported();
    public ushort EncodeStoredQuantity(ushort quantity, ushort quantityKey) => throw Unsupported();
    public IEnumerable<Gen3SaveBagPhysicalPocket> CandidatePhysicalPockets(int pocket) => [];
    public int? PocketOfItem(Gen3SaveDocument document, ushort itemId) => null;
    public string PocketName(int pocket) => $"口袋{pocket}";
    public void WriteBagRange(Gen3SaveDocument document, int saveOffset, ReadOnlySpan<byte> data) => throw Unsupported();
    public ushort ReadBagU16(Gen3SaveDocument document, int saveOffset) => throw Unsupported();
}
