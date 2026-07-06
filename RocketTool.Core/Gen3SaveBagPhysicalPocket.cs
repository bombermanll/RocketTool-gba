namespace RocketTool.Core;

internal sealed record Gen3SaveBagPhysicalPocket(
    string Name,
    int Offset,
    int Capacity,
    int? FixedPocket,
    bool HasQuantity);
