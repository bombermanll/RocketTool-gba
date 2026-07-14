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
