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
            PokemonShuiyin12RuntimeAdapter.StrategyId => PokemonShuiyin12SaveScenario.Instance,
            PokemonUltimateEmerald55RuntimeAdapter.StrategyId => PokemonUltimateEmerald55SaveScenario.Instance,
            OriginHeartGold40RuntimeAdapter.StrategyId => throw new NotSupportedException("起源心金 4.0 尚未验证存档结构，CLI 存档场景已禁用。"),
            var id => throw new NotSupportedException($"没有与运行时适配器 {id} 配套的 CLI 存档场景。")
        };
}

internal sealed class PokemonUltimateEmerald55SaveScenario : IProfileSaveScenario
{
    public static PokemonUltimateEmerald55SaveScenario Instance { get; } = new();
    public void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo)
    {
        foreach (var (pocket, item) in new[]
                 {
                     (1, (ushort)14),
                     (2, (ushort)3),
                     (3, (ushort)378),
                     (4, (ushort)133),
                     (5, (ushort)119)
                 })
        {
            if (document.CurrentBag.All(entry => entry.ItemId != item))
                document.AddBagItem(pocket, item, pocket == 1 ? (ushort)2 : (ushort)1);
        }

        if (document.Snapshot.Trainer is { } trainer)
        {
            var name = trainer.NameBytes.ToArray();
            if (name.Length > 0)
                name[0] = name[0] == 0xBB ? (byte)0xBC : (byte)0xBB;
            document.ReplaceTrainerName(name);
            document.ReplaceTrainerMoney(
                trainer.Money == 999_999
                    ? trainer.Money - 1
                    : trainer.Money + 1);
        }

        if (document.Snapshot.Boxes.FirstOrDefault() is not { } entry) return;
        var mon = new BoxPokemon(entry.Mon.Raw, entry.Mon.Layout);
        var info = mon.GetInfo();
        mon.SetGrowth(friendship: (byte)(info.Friendship == 255 ? 254 : info.Friendship + 1));
        document.ReplaceBoxPokemon(entry.GlobalSlot, mon);
    }
}

internal sealed class SpanishRocketSaveScenario : IProfileSaveScenario
{
    public static SpanishRocketSaveScenario Instance { get; } = new();
    public void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo)
    {
        if (document.CurrentBag.All(entry => entry.Pocket != 3 || entry.ItemId != 6))
            document.AddBagItem(3, 6, 2);
        if (document.CurrentBag.All(entry => entry.Pocket != 2 || entry.ItemId != 28))
            document.AddBagItem(2, 28, 2);
    }
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

        // Global slot 71 (box 3, slot 11) crosses the first 0xFF0-byte PC
        // section boundary and guards the Unbound-specific split/write path.
        int[] targetSlots = [1, 71, 20 * 30 - 29, 22 * 30, 23 * 30 - 29, 25 * 30 - 29];
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

internal sealed class PokemonShuiyin12SaveScenario : IProfileSaveScenario
{
    public static PokemonShuiyin12SaveScenario Instance { get; } = new();

    public void ApplyVerificationChanges(Gen3SaveDocument document, PartyMonInfo? partyInfo)
    {
        // 各 surface 独立验证：没有队伍时仍然要覆盖训练家、背包和箱子。
        if (document.Snapshot.Trainer is { } trainer)
        {
            var name = trainer.NameBytes.ToArray();
            if (name.Length > 1 && name[1] != 0xFF) name[0] = name[1];
            document.ReplaceTrainerName(name);
            document.ReplaceTrainerMoney(trainer.Money == 999_999 ? trainer.Money - 1 : trainer.Money + 1);
        }
        if (document.CurrentPartyCount > 1)
            document.RemovePartyPokemon(document.CurrentPartyCount);
        if (document.Snapshot.Party.Count > 0)
        {
            var party = new PartyPokemon(document.Snapshot.Party[0].Raw, document.Snapshot.Party[0].Layout);
            var before = party.GetInfo();
            party.SetGrowth(
                item: before.Item == 0 ? (ushort)13 : (ushort)0,
                exp: before.Exp + 1,
                friendship: (byte)(before.Friendship == 255 ? 254 : before.Friendship + 1));
            party.SetMoves([55, 43, 33, 0], [20, 20, 20, 0]);
            party.SetEvs(new Dictionary<string, byte>
            {
                ["hp"] = 7, ["atk"] = 0, ["def"] = 0, ["spe"] = 0, ["spa"] = 0, ["spd"] = 0
            });
            party.SetIvs(new Dictionary<string, int>
            {
                ["hp"] = 10, ["atk"] = 11, ["def"] = 12, ["spe"] = 13, ["spa"] = 14, ["spd"] = 15, ["ability"] = 1
            });
            document.ReplacePartyPokemon(1, party);
        }
        if (document.Snapshot.Bag.All(entry => entry.ItemId != 4))
            document.AddBagItem(3, 4, 2);
        if (document.Snapshot.Bag.All(entry => entry.ItemId != 13))
            document.AddBagItem(1, 13, 3);
        if (document.Snapshot.Bag.All(entry => entry.ItemId != 139))
            document.AddBagItem(5, 139, 2);
        if (document.Snapshot.Bag.All(entry => entry.ItemId != 1))
            document.AddBagItem(3, 1, 4);
        if (document.Snapshot.Bag.All(entry => entry.ItemId != 15))
            document.AddBagItem(1, 15, 2);
        if (document.Snapshot.Bag.All(entry => entry.ItemId != 140))
            document.AddBagItem(5, 140, 5);
        if (document.Snapshot.Bag.All(entry => entry.ItemId != 365))
            document.AddBagItem(2, 365, 1);

        var otId = partyInfo?.OtId ?? document.Snapshot.Trainer?.OtId ?? 0xC77E6D0Bu;
        int[] targetSlots =
        [
            (1 - 1) * 30 + 1,
            (3 - 1) * 30 + 11,   // 跨 0xFF0 的 PC 连续记录边界
            (20 - 1) * 30 + 1,
            (21 - 1) * 30 + 1,
            (22 - 1) * 30 + 1,
            (23 - 1) * 30 + 1,
            (24 - 1) * 30 + 1,
            (25 - 1) * 30 + 30
        ];
        for (var i = 0; i < targetSlots.Length; i++)
        {
            var mon = BoxPokemon.Create(
                0x31415927u + (uint)(i * 2),
                otId,
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
        if (document.Snapshot.Trainer is { } trainer)
        {
            document.ReplaceTrainerName(trainer.NameBytes);
            document.ReplaceTrainerMoney(trainer.Money == 999_999 ? trainer.Money - 1 : trainer.Money + 1);
        }

        if (document.Snapshot.Bag.All(entry => entry.ItemId != 13)) document.AddBagItem(1, 13, 1);
        var replaceable = document.CurrentBag.FirstOrDefault(entry => entry.Pocket == 1 && entry.ItemId is 13 or 14);
        if (replaceable is not null)
        {
            var replacement = replaceable.ItemId == 13 ? (ushort)14 : (ushort)13;
            if (document.CurrentBag.All(entry => entry.Pocket != 1 || entry.ItemId != replacement))
                document.ReplaceBagEntry(replaceable.SaveOffset, replacement, replaceable.Quantity);
        }

        foreach (var (pocket, item) in new[]
                 {
                     (1, (ushort)86),
                     (2, (ushort)352),
                     (3, (ushort)1),
                     (4, (ushort)289),
                     (5, (ushort)133)
                 })
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
