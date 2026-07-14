using RocketTool.Core;

internal interface IProfileSaveScenario
{
    void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo);
}

internal static class ProfileSaveScenarioCatalog
{
    public static IProfileSaveScenario ForProfile(GameProfile profile)
        => profile.Strategies.Runtime switch
        {
            SpanishRocketRuntimeAdapter.StrategyId => SpanishRocketSaveScenario.Instance,
            PokemonUnboundRuntimeAdapter.StrategyId => PokemonUnboundSaveScenario.Instance,
            PokemonDestinyRuntimeAdapter.StrategyId => PokemonDestinySaveScenario.Instance,
            PokemonRadicalRedRuntimeAdapter.StrategyId => PokemonRadicalRedSaveScenario.Instance,
            var id => throw new NotSupportedException($"没有与运行时适配器 {id} 配套的 CLI 存档场景。")
        };
}

internal sealed class SpanishRocketSaveScenario : IProfileSaveScenario
{
    public static SpanishRocketSaveScenario Instance { get; } = new();
    public void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo) { }
}

internal sealed class DisabledSaveScenario : IProfileSaveScenario
{
    public static DisabledSaveScenario Instance { get; } = new();
    public void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo)
        => throw new InvalidOperationException("当前 Profile 的存档场景已禁用。");
}

internal sealed class PokemonUnboundSaveScenario : IProfileSaveScenario
{
    public static PokemonUnboundSaveScenario Instance { get; } = new();

    public void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo)
    {
        if (partyInfo is null) return;
        if (document.Snapshot.Trainer is { } trainer)
        {
            document.ReplaceTrainerName(trainer.NameBytes);
            document.ReplaceTrainerMoney(trainer.Money == 99_999_999 ? trainer.Money - 1 : trainer.Money + 1);
        }

        int[] targetSlots = [1, 20 * 30 - 29, 22 * 30, 23 * 30 - 29, 25 * 30 - 29];
        for (var i = 0; i < targetSlots.Length; i++)
        {
            var box = BoxPokemon.Create(0x12345679u + (uint)(i * 2), partyInfo.OtId, PokemonDataLayout.UnboundCfruPlainParty);
            box.SetGrowth(species: (ushort)(i + 1), item: 0, exp: 0, friendship: 70, ppBonuses: 0);
            box.SetIvs(new Dictionary<string, int> { ["hp"] = 1, ["atk"] = 2, ["def"] = 3, ["spe"] = 4, ["spa"] = 5, ["spd"] = 6 });
            document.ReplaceBoxPokemon(targetSlots[i], box);
        }
    }
}

internal sealed class PokemonRadicalRedSaveScenario : IProfileSaveScenario
{
    public static PokemonRadicalRedSaveScenario Instance { get; } = new();

    public void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo)
    {
        if (partyInfo is null) return;
        foreach (var (pocket, item) in new[]
                 {
                     (1, (ushort)14),
                     (2, (ushort)259),
                     (3, (ushort)2),
                     (4, (ushort)289),
                     (5, (ushort)133)
                 })
        {
            if (document.CurrentBag.All(entry => entry.ItemId != item))
                document.AddBagItem(pocket, item, 1);
        }

        if (document.Snapshot.Trainer is { } trainer)
        {
            var name = trainer.NameBytes.ToArray();
            if (name.Length > 1 && name[1] != 0xFF) name[0] = name[1];
            document.ReplaceTrainerName(name);
            document.ReplaceTrainerMoney(trainer.Money == 999_999_999 ? trainer.Money - 1 : trainer.Money + 1);
        }
        if (document.CurrentPartyCount > 1)
            document.RemovePartyPokemon(document.CurrentPartyCount);

        int[] targetSlots =
        [
            (20 - 1) * 30 + 1,
            (22 - 1) * 30 + 1,
            (23 - 1) * 30 + 1,
            (24 - 1) * 30 + 1,
            (25 - 1) * 30 + 1
        ];
        document.ClearBoxPokemon(1);
        for (var i = 0; i < targetSlots.Length; i++)
        {
            var mon = BoxPokemon.Create(
                0x22334451u + (uint)(i * 2),
                partyInfo.OtId,
                PokemonDataLayout.UnboundCfruPlainParty);
            mon.SetGrowth((ushort)(i + 1), item: 0, exp: 0, friendship: 70, ppBonuses: 0);
            mon.SetIvs(new Dictionary<string, int>
            {
                ["hp"] = 1, ["atk"] = 2, ["def"] = 3,
                ["spe"] = 4, ["spa"] = 5, ["spd"] = 6, ["ability"] = 0
            });
            document.ReplaceBoxPokemon(targetSlots[i], mon);
        }
    }
}

internal sealed class PokemonDestinySaveScenario : IProfileSaveScenario
{
    public static PokemonDestinySaveScenario Instance { get; } = new();

    public void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo)
    {
        if (document.Snapshot.Bag.All(entry => entry.ItemId != 13)) document.AddBagItem(1, 13, 1);
        var replaceable = document.CurrentBag.FirstOrDefault(entry => entry.Pocket == 1 && entry.ItemId is 13 or 14);
        if (replaceable is not null)
        {
            var replacement = replaceable.ItemId == 13 ? (ushort)14 : (ushort)13;
            if (document.CurrentBag.All(entry => entry.Pocket != 1 || entry.ItemId != replacement))
                document.ReplaceBagEntry(replaceable.SaveOffset, replacement, replaceable.Quantity);
        }

        foreach (var (pocket, item) in new[] { (1, (ushort)86), (3, (ushort)1), (4, (ushort)289) })
            if (document.CurrentBag.All(entry => entry.ItemId != item)) document.AddBagItem(pocket, item, 1);

        if (document.Snapshot.Boxes.FirstOrDefault() is { } boxEntry)
        {
            var box = new BoxPokemon(boxEntry.Mon.Raw, boxEntry.Mon.Layout);
            var boxInfo = box.GetInfo();
            box.SetGrowth(friendship: (byte)(boxInfo.Friendship == 255 ? 254 : boxInfo.Friendship + 1));
            document.ReplaceBoxPokemon(boxEntry.GlobalSlot, box);
        }
    }

    public static Gen3SaveWriteResult RepairMachineBag(string path, GameProfile profile)
    {
        if (profile.Strategies.Runtime != PokemonDestinyRuntimeAdapter.StrategyId)
            throw new InvalidOperationException("该修复命令只支持宝可梦命运 Profile。");
        var document = Gen3SaveReader.Open(path, profile);
        var machineEntries = document.CurrentBag.Where(entry => entry.Pocket == 4 && entry.SaveOffset < 0x10000).OrderBy(entry => entry.SaveOffset).ToArray();
        ushort[] expectedMachines = [305, 336];
        if (machineEntries.Length < expectedMachines.Length || expectedMachines.Any(item => machineEntries.All(entry => entry.ItemId != item)))
            throw new InvalidOperationException("当前招式机盒中没有同时找到 TM017 和 TM048，已取消自动修复。");
        for (var i = 0; i < machineEntries.Length; i++)
        {
            var entry = machineEntries[i];
            document.ReplaceBagEntry(entry.SaveOffset, i < expectedMachines.Length ? expectedMachines[i] : (ushort)0, i < expectedMachines.Length ? (ushort)1 : (ushort)0);
        }
        return document.SaveInPlaceWithBackup();
    }
}
