namespace RocketTool.Core;

public static class ProfileIsolationSelfTest
{
    public static void Verify(GameProfile profile)
    {
        VerifyMovePriorityParsing();
        VerifySpanishRocketSparseBagQuantityKey();
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
             adapter.CanRead(GameDataSurface.Party, false) ||
             adapter.CanRead(GameDataSurface.Boxes, false) ||
             adapter.CanRead(GameDataSurface.Bag, false) ||
             adapter.CanRead(GameDataSurface.Trainer, false)))
            throw new InvalidOperationException("disabled 存档策略仍暴露了存档能力。");

        VerifyCaughtBallIsolation(profile, adapter);
        VerifyImportInitialization(adapter);
    }

    private static void VerifyMovePriorityParsing()
    {
        if (MoveDataReader.ParsePriority("-6") != -6 ||
            MoveDataReader.ParsePriority("250") != -6 ||
            MoveDataReader.ParsePriority("0") != 0 ||
            MoveDataReader.ParsePriority("7") != 7)
            throw new InvalidOperationException("招式优先度有符号/原始字节解析回归失败。");
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
