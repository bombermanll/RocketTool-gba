namespace RocketTool.Core;

public static class ProfileIsolationSelfTest
{
    public static void Verify(GameProfile profile)
    {
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
        party.SetCaughtBallCode(partyCode);
        var partyInfo = party.GetInfo();
        if (partyInfo.CaughtBallCode != partyCode ||
            adapter.PartyLayout == PokemonDataLayout.SpanishRocketEncrypted &&
            (partyInfo.GameNatureCode != 31 || partyInfo.Checksum != partyInfo.CalculatedChecksum))
            throw new InvalidOperationException("队伍球种位域没有保留相邻性格位或没有刷新校验。");

        var box = BoxPokemon.Create(pid, otId, adapter.LiveBoxLayout);
        if (adapter.LiveBoxLayout == PokemonDataLayout.SpanishRocketEncrypted)
            box.SetGameNatureCode(31);
        box.SetCaughtBallCode(boxCode);
        var boxInfo = box.GetInfo();
        if (boxInfo.CaughtBallCode != boxCode ||
            adapter.LiveBoxLayout == PokemonDataLayout.SpanishRocketEncrypted &&
            (boxInfo.GameNatureCode != 31 || boxInfo.Checksum != boxInfo.CalculatedChecksum))
            throw new InvalidOperationException("箱子球种位域没有保留相邻性格位或没有刷新校验。");
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
