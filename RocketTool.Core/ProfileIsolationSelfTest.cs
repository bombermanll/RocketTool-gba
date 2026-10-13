namespace RocketTool.Core;

public static class ProfileIsolationSelfTest
{
    public static void Verify(GameProfile profile)
    {
        VerifyMovePriorityParsing();
        VerifyExperienceTable(profile);
        VerifySpanishRocketSparseBagQuantityKey();
        VerifyShuiyinPlainBagQuantity(profile);
        VerifyShuiyinPhysicalPocketField(profile);
        VerifySpanishRocketFixedLiveLayouts(profile);
        VerifyUnboundSection2ChecksumVariants();
        var adapter = GameRuntimeAdapterCatalog.ForProfile(profile);

        var mixed = profile with
        {
            Strategies = profile.Strategies with { PartyScanner = profile.Strategies.PartyScanner + "-foreign" }
        };
        try
        {
            GameRuntimeAdapterCatalog.ForProfile(mixed);
            throw new InvalidOperationException("错误策略组合未被版本适配器拒绝。");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("策略组合", StringComparison.Ordinal))
        {
        }

        ProfileRomIdentityValidator.Validate(
            profile,
            profile.RomIdentity.HeaderTitle,
            profile.RomIdentity.GameCode,
            fingerprint => Convert.FromHexString(fingerprint.Hex));
        try
        {
            ProfileRomIdentityValidator.Validate(
                profile,
                profile.RomIdentity.HeaderTitle + "X",
                profile.RomIdentity.GameCode,
                fingerprint => Convert.FromHexString(fingerprint.Hex));
            throw new InvalidOperationException("错误 ROM header 未被拒绝。");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ROM 身份不匹配", StringComparison.Ordinal))
        {
        }

        if (profile.Strategies.Save == DisabledSaveStrategy.StrategyId &&
            (profile.Features.SaveEditing ||
             !profile.IsNds && (
                 adapter.CanRead(GameDataSurface.Party, false) ||
                 adapter.CanRead(GameDataSurface.Boxes, false) ||
                 adapter.CanRead(GameDataSurface.Bag, false) ||
                 adapter.CanRead(GameDataSurface.Trainer, false))))
            throw new InvalidOperationException("disabled 存档策略仍暴露了存档能力。");

        if (adapter.PartyLayout != PokemonDataLayout.Disabled)
        {
            VerifyCaughtBallIsolation(profile, adapter);
            VerifyImportInitialization(adapter);
        }
        else if (profile.Features.CaughtBall.LiveParty.Read ||
                 profile.Features.CaughtBall.SaveParty.Read ||
                 profile.Features.CaughtBall.LiveBoxes.Read ||
                 profile.Features.CaughtBall.SaveBoxes.Read)
        {
            throw new InvalidOperationException("未验证宝可梦布局的 Profile 仍暴露了球种能力。");
        }
    }

    private static void VerifyMovePriorityParsing()
    {
        if (MoveDataReader.ParsePriority("-6") != -6 ||
            MoveDataReader.ParsePriority("250") != -6 ||
            MoveDataReader.ParsePriority("0") != 0 ||
            MoveDataReader.ParsePriority("7") != 7)
            throw new InvalidOperationException("招式优先度有符号/原始字节解析回归失败。");
    }

    private static void VerifyExperienceTable(GameProfile profile)
    {
        if (profile.Id != "pokemon-shuiyin-12-cn") return;
        var table = new PokemonExperienceTable(new ModifierDatabase(profile.DatabaseDirectory));
        if (table.MaxLevelValue != 100 || table.ExperienceForLevel(2, 2) != 4)
            throw new InvalidOperationException("水银 101 项经验表回归失败。");
    }

    private static void VerifyShuiyinPlainBagQuantity(GameProfile profile)
    {
        if (profile.Id != "pokemon-shuiyin-12-cn") return;
        const ushort key = 0xBEEF;
        var encoded = LiveBagAccess.EncodeSlot(profile, 4, 11, key, isKeyItem: false);
        var raw = (ushort)(encoded[2] | encoded[3] << 8);
        if (raw != 11 || LiveBagAccess.DecodeQuantity(profile, 4, raw, key, false) != 11)
            throw new InvalidOperationException("水银实时背包明文数量编解码回归失败。");
    }

    private static void VerifyShuiyinPhysicalPocketField(GameProfile profile)
    {
        if (profile.Id != "pokemon-shuiyin-12-cn") return;
        var pockets = new ModifierDatabase(profile.DatabaseDirectory).Table("item_pockets");
        var itemData = new ModifierDatabase(profile.DatabaseDirectory).Table("item_data");
        foreach (var (item, pocketText) in pockets)
        {
            if (!itemData.TryGetValue(item, out var raw))
                throw new InvalidOperationException($"水银道具 {item} 缺少 item_data 行。");
            var fields = raw.Split('\t');
            if (fields.Length <= 7 || !int.TryParse(fields[7], out var physicalPocket) ||
                !int.TryParse(pocketText, out var configuredPocket) ||
                physicalPocket != configuredPocket)
                throw new InvalidOperationException($"水银道具 {item} 的物理口袋未按 ROM +0x19 配置。");
        }
    }

    private static void VerifySpanishRocketSparseBagQuantityKey()
    {
        const ushort quantityKey = 0x4D23;
        var last = Gen3SaveReader.SaveBagPhysicalPockets.Max(pocket => pocket.Offset + pocket.Capacity * 4);
        var saveBlock1 = new byte[last];
        var emptyStoredQuantity = (ushort)(quantityKey ^ 1);
        foreach (var pocket in Gen3SaveReader.SaveBagPhysicalPockets.Where(pocket => pocket.HasQuantity && pocket.FixedPocket != 7))
        {
            for (var slot = 0; slot < pocket.Capacity; slot++)
            {
                var offset = pocket.Offset + slot * 4 + 2;
                saveBlock1[offset] = (byte)(emptyStoredQuantity & 0xFF);
                saveBlock1[offset + 1] = (byte)(emptyStoredQuantity >> 8);
            }
        }

        var ball = Gen3SaveReader.SaveBagPhysicalPockets.Single(pocket => pocket.FixedPocket == 3);
        saveBlock1[ball.Offset] = 6;
        var storedQuantity = (ushort)(quantityKey ^ 1); // displayed quantity x2
        saveBlock1[ball.Offset + 2] = (byte)(storedQuantity & 0xFF);
        saveBlock1[ball.Offset + 3] = (byte)(storedQuantity >> 8);

        if (Gen3SaveReader.InferSaveBagQuantityKey(saveBlock1) != quantityKey)
            throw new InvalidOperationException("西火稀疏存档背包的空槽数量密钥推断回归失败。");
    }

    private static void VerifyUnboundSection2ChecksumVariants()
    {
        var section = new byte[0x1000];

        section[0] = 1;
        section[0xF80] = 1;
        if (!Gen3SaveReader.TryMatchUnboundChecksum(section, 2, 1, out var f80Length) ||
            f80Length != 0xF80)
            throw new InvalidOperationException("解放版 section 2 的 0xF80 checksum 范围回归失败。");

        section.AsSpan().Clear();
        section[0xF80] = 1;
        if (!Gen3SaveReader.TryMatchUnboundChecksum(section, 2, 1, out var ff4Length) ||
            ff4Length != 0xFF4)
            throw new InvalidOperationException("解放版 section 2 的 0xFF4 checksum 范围回归失败。");

        section[0xFF0] = 1;
        if (!Gen3SaveReader.TryMatchUnboundChecksum(section, 2, 1, out var ff0Length) ||
            ff0Length != 0xFF0)
            throw new InvalidOperationException("解放版 section 2 的 0xFF0 checksum 范围回归失败。");
    }

    private static void VerifySpanishRocketFixedLiveLayouts(GameProfile profile)
    {
        if (profile.Id != "spanish-rocket-current") return;
        if (profile.Runtime.LiveBag.Mode != "save-block1" ||
            profile.Runtime.LiveBag.QuantityEncoding != "minus-one-xor" ||
            profile.Memory.PcBoxStoragePointerAddress != 0x03005254 ||
            profile.Memory.PcBoxDataOffset != 4)
            throw new InvalidOperationException("西火实时背包或箱子没有使用当前 ROM 已确认的固定入口。");

        var expectedAreas = new[]
        {
            (Pocket: 1, Offset: 0x04C0u, Capacity: 42),
            (Pocket: 8, Offset: 0x08BCu, Capacity: 63),
            (Pocket: 3, Offset: 0x09BCu, Capacity: 15),
            (Pocket: 7, Offset: 0x09FCu, Capacity: 254),
            (Pocket: 5, Offset: 0x0DF4u, Capacity: 67),
            (Pocket: 2, Offset: 0x0F04u, Capacity: 38),
            (Pocket: 4, Offset: 0x0F9Cu, Capacity: 28),
            (Pocket: 4, Offset: 0x1010u, Capacity: 101),
            (Pocket: 6, Offset: 0x11A4u, Capacity: 28),
            (Pocket: 6, Offset: 0x1218u, Capacity: 101)
        };
        foreach (var expected in expectedAreas)
        {
            if (!profile.Runtime.LiveBag.Areas.Any(area =>
                    area.Pocket == expected.Pocket &&
                    area.Offset == expected.Offset &&
                    area.Capacity == expected.Capacity))
                throw new InvalidOperationException($"西火实时背包缺少口袋 {expected.Pocket} 的物理区域。");
        }
        foreach (var (pocket, capacity) in new[] { (2, 38), (4, 129), (6, 129) })
        {
            if (profile.Runtime.LiveBag.Areas.Where(area => area.Pocket == pocket).Sum(area => area.Capacity) != capacity ||
                profile.Runtime.LiveBagPockets.Single(candidate => candidate.Id == pocket).BatchCapacity != capacity)
                throw new InvalidOperationException($"西火实时背包口袋 {pocket} 的跨段容量不一致。");
        }

        const ushort key = 0x8535;
        var encoded = LiveBagAccess.EncodeSlot(profile, 4, 11, key, isKeyItem: false);
        var rawQuantity = (ushort)(encoded[2] | encoded[3] << 8);
        if (rawQuantity != (ushort)(10 ^ key) ||
            LiveBagAccess.DecodeQuantity(profile, 4, rawQuantity, key, isKeyItem: false) != 11)
            throw new InvalidOperationException("西火实时背包 (quantity-1) XOR key 编解码回归失败。");

        var empty = LiveBagAccess.EncodeSlot(profile, 0, 0, key, isKeyItem: false);
        if ((ushort)(empty[2] | empty[3] << 8) != key)
            throw new InvalidOperationException("西火实时背包空槽数量标记回归失败。");
        var keyItem = LiveBagAccess.EncodeSlot(profile, 899, 1, key, isKeyItem: true);
        if (keyItem[2] != 0 || keyItem[3] != 0)
            throw new InvalidOperationException("西火实时重要物品数量字段回归失败。");
    }

    private static void VerifyCaughtBallIsolation(GameProfile profile, IGameRuntimeAdapter adapter)
    {
        var configured = new[]
        {
            profile.Features.CaughtBall.LiveParty,
            profile.Features.CaughtBall.SaveParty,
            profile.Features.CaughtBall.LiveBoxes,
            profile.Features.CaughtBall.SaveBoxes
        }.Any(access => access.Read || access.Write);
        if (!configured)
        {
            if (adapter.CanReadCaughtBall(GameDataSurface.Party, true) ||
                adapter.CanReadCaughtBall(GameDataSurface.Party, false) ||
                adapter.CanReadCaughtBall(GameDataSurface.Boxes, true) ||
                adapter.CanReadCaughtBall(GameDataSurface.Boxes, false))
                throw new InvalidOperationException("未配置球种能力的 Profile 仍暴露了球种读取。");
            return;
        }

        const uint pid = 0x12345679;
        const uint otId = 0x10203040;
        var choices = profile.Features.CaughtBall.Choices;
        var partyCode = checked((byte)choices[^1].StoredValue);
        var boxCode = checked((byte)choices[0].StoredValue);
        var party = PartyPokemon.Create(pid, otId, adapter.PartyLayout);
        if (adapter.PartyLayout == PokemonDataLayout.SpanishRocketEncrypted)
            party.SetGameNatureCode(31);
        var partyUltimateBallByte = adapter.PartyLayout == PokemonDataLayout.UltimateEmerald55CfruPlainParty
            ? party.Raw[0x47]
            : (byte?)null;
        party.SetCaughtBallCode(partyCode);
        var partyInfo = party.GetInfo();
        if (partyInfo.CaughtBallCode != partyCode ||
            adapter.PartyLayout == PokemonDataLayout.SpanishRocketEncrypted &&
            (partyInfo.GameNatureCode != 31 || partyInfo.Checksum != partyInfo.CalculatedChecksum))
            throw new InvalidOperationException("队伍球种位域没有保留相邻性格位或没有刷新校验。");
        if (partyUltimateBallByte is { } partyBefore &&
            (party.Raw[0x47] & ~0x7C) != (partyBefore & ~0x7C))
            throw new InvalidOperationException("究极绿宝石 5.5 队伍球种写入破坏了相邻标志位。");

        var box = BoxPokemon.Create(pid, otId, adapter.LiveBoxLayout);
        if (adapter.LiveBoxLayout == PokemonDataLayout.SpanishRocketEncrypted)
            box.SetGameNatureCode(31);
        var boxUltimateBallByte = adapter.LiveBoxLayout == PokemonDataLayout.UltimateEmerald55CfruPlainBox
            ? box.Raw[0x47]
            : (byte?)null;
        box.SetCaughtBallCode(boxCode);
        var boxInfo = box.GetInfo();
        if (boxInfo.CaughtBallCode != boxCode ||
            adapter.LiveBoxLayout == PokemonDataLayout.SpanishRocketEncrypted &&
            (boxInfo.GameNatureCode != 31 || boxInfo.Checksum != boxInfo.CalculatedChecksum))
            throw new InvalidOperationException("箱子球种位域没有保留相邻性格位或没有刷新校验。");
        if (boxUltimateBallByte is { } boxBefore &&
            (box.Raw[0x47] & ~0x7C) != (boxBefore & ~0x7C))
            throw new InvalidOperationException("究极绿宝石 5.5 箱子球种写入破坏了相邻标志位。");
    }

    private static void VerifyImportInitialization(IGameRuntimeAdapter adapter)
    {
        if (adapter.PartyLayout != PokemonDataLayout.UltimateEmerald55CfruPlainParty)
            return;

        const uint pid = 0x12345679;
        const uint otId = 0x10203040;
        var party = PartyPokemon.Create(pid, otId, adapter.PartyLayout);
        party.SetCaughtBallCode(7);
        party.SetAbilitySlot(2);
        adapter.InitializeImportedParty(party, 5, 1);
        if (party.Raw[0x46] != 5 || party.Raw[0x47] != 0x9F ||
            (party.Raw[0x3C] & 1) == 0 || party.GetInfo().Ivs["ability"] != 2)
            throw new InvalidOperationException("究极绿宝石 5.5 队伍新建记录的来源字段或特性槽初始化回归失败。");
        party.SetEvs(new Dictionary<string, byte> { ["spa"] = 0 });
        if ((party.Raw[0x3C] & 1) == 0 || party.GetInfo().Ivs["ability"] != 2)
            throw new InvalidOperationException("究极绿宝石 5.5 队伍努力值写入破坏了隐藏特性标志。");
        party.SetAbilitySlot(1);
        if ((party.Raw[0x3C] & 1) != 0 || (party.Raw[0x4B] & 0x40) == 0 ||
            party.GetInfo().Ivs["ability"] != 1)
            throw new InvalidOperationException("究极绿宝石 5.5 队伍普通特性槽读写回归失败。");

        var box = BoxPokemon.Create(pid, otId, adapter.LiveBoxLayout);
        box.SetCaughtBallCode(7);
        box.SetAbilitySlot(2);
        adapter.InitializeImportedBox(box, 5, 1);
        if (box.Raw[0x46] != 5 || box.Raw[0x47] != 0x9F ||
            (box.Raw[0x3C] & 1) == 0 || box.GetInfo().Ivs["ability"] != 2)
            throw new InvalidOperationException("究极绿宝石 5.5 箱子新建记录的来源字段或特性槽初始化回归失败。");
        box.SetEvs(new Dictionary<string, byte> { ["spa"] = 0 });
        if ((box.Raw[0x3C] & 1) == 0 || box.GetInfo().Ivs["ability"] != 2)
            throw new InvalidOperationException("究极绿宝石 5.5 箱子努力值写入破坏了隐藏特性标志。");
        box.SetAbilitySlot(1);
        if ((box.Raw[0x3C] & 1) != 0 || (box.Raw[0x4B] & 0x40) == 0 ||
            box.GetInfo().Ivs["ability"] != 1)
            throw new InvalidOperationException("究极绿宝石 5.5 箱子普通特性槽读写回归失败。");
    }
}

public static class ProfileRomIdentityValidator
{
    public static void Validate(
        GameProfile profile,
        string title,
        string gameCode,
        Func<GameProfileRomFingerprint, byte[]> readFingerprint)
    {
        var identity = profile.RomIdentity;
        if (!string.Equals(title, identity.HeaderTitle, StringComparison.Ordinal) ||
            !string.Equals(gameCode, identity.GameCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"当前 mGBA ROM 身份不匹配：读取到 {title}/{gameCode}，所选版本要求 {identity.HeaderTitle}/{identity.GameCode}。已停止访问。");
        }

        foreach (var fingerprint in identity.LiveFingerprints)
        {
            var expected = Convert.FromHexString(fingerprint.Hex);
            var actual = readFingerprint(fingerprint);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"当前 mGBA ROM 与所选版本 {profile.DisplayName} 的指纹不匹配（offset 0x{fingerprint.Offset:X}）。已停止访问，避免跨版本读写。");
            }
        }
    }
}
