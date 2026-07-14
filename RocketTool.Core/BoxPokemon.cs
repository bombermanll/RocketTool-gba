namespace RocketTool.Core;

public sealed record BoxMonInfo(
    uint Pid,
    uint OtId,
    int Nature,
    byte GameNatureCode,
    ushort Species,
    ushort Item,
    uint Exp,
    byte PpBonuses,
    byte Friendship,
    ushort[] Moves,
    byte[] Pp,
    byte[] Evs,
    Dictionary<string, int> Ivs,
    uint IvWord,
    ushort Checksum,
    ushort CalculatedChecksum);

public sealed class BoxPokemon
{
    public const int Size = 80;
    public const int UnboundCompressedSize = 58;
    private const int NicknameOffset = 0x08;
    private const int NicknameLength = 10;
    private const int OtNameOffset = 0x13;
    public const int OtNameLength = 7;
    private const int GrowthPpBonusesOffset = 7;
    private const int GrowthFriendshipOffset = 8;
    private const int GrowthNatureOverrideWordOffset = 8;
    private const int GrowthNatureOverrideShift = 13;
    private const uint GrowthNatureOverrideMask = 0x1Fu << GrowthNatureOverrideShift;
    private const byte NatureOverrideUsePid = 0x1A;
    private const uint GrowthExpMask = 0x007FFFFF;
    private const int MiscAbilitySlotOffset = 11;
    private const byte MiscAbilitySlotMask = 0x03;
    private const int DestinyGrowthOffset = 0x20;
    private const int DestinyAttacksOffset = 0x2C;
    private const int DestinyEvsOffset = 0x38;
    private const int DestinyMiscOffset = 0x44;
    private readonly byte[] _raw;
    private readonly PokemonDataLayout _layout;

    public BoxPokemon(ReadOnlySpan<byte> raw, PokemonDataLayout layout)
    {
        var expectedSize = layout == PokemonDataLayout.UnboundCfruPlainParty ? UnboundCompressedSize : Size;
        if (raw.Length != expectedSize) throw new ArgumentException($"Box Pokemon must be {expectedSize} bytes for {layout}");
        _raw = raw.ToArray();
        _layout = layout;
    }

    public ReadOnlySpan<byte> Raw => _raw;
    public PokemonDataLayout Layout => _layout;
    public uint Pid => ReadU32(_raw, 0);
    public uint OtId => ReadU32(_raw, 4);
    public uint Key => Pid ^ OtId;
    public ushort Checksum => _layout is PokemonDataLayout.UnboundCfruPlainParty or PokemonDataLayout.DestinyCfruPlainBox
        ? (ushort)0
        : ReadU16(_raw, 0x1C);
    public bool IsEmpty => _layout switch
    {
        PokemonDataLayout.UnboundCfruPlainParty => ReadU16(_raw, 28) == 0,
        PokemonDataLayout.DestinyCfruPlainBox => ReadU16(_raw, DestinyGrowthOffset) == 0,
        _ => Pid == 0 && OtId == 0
    };
    public bool IsShiny => PartyPokemon.IsShinyPid(Pid, OtId);

    public bool HasValidHeader(int maxSpecies)
    {
        if (_layout == PokemonDataLayout.DestinyCfruPlainBox)
        {
            var destinySpecies = ReadU16(_raw, DestinyGrowthOffset);
            return destinySpecies is >= 1 && destinySpecies <= maxSpecies;
        }
        if (_layout != PokemonDataLayout.UnboundCfruPlainParty)
            return !IsEmpty;
        var species = ReadU16(_raw, 28);
        var language = _raw[18];
        var sanity = _raw[19];
        return species is >= 1 && species <= maxSpecies &&
               language is >= 1 and <= 7 &&
               (sanity & 0x02) != 0 && (sanity & ~0x07) == 0;
    }

    public static BoxPokemon Create(uint pid, uint otId, PokemonDataLayout layout)
    {
        if (pid == 0) throw new ArgumentOutOfRangeException(nameof(pid), "PID must be non-zero.");
        var raw = new byte[layout == PokemonDataLayout.UnboundCfruPlainParty ? UnboundCompressedSize : Size];
        WriteU32(raw, 0x00, pid);
        WriteU32(raw, 0x04, otId);
        if (layout is PokemonDataLayout.UnboundCfruPlainParty or PokemonDataLayout.DestinyCfruPlainBox)
        {
            raw[0x12] = 2;
            raw[0x13] = 2;
            raw.AsSpan(0x14, OtNameLength).Fill(0xFF);
            return new BoxPokemon(raw, layout);
        }

        raw[0x12] = 0x12;
        raw.AsSpan(OtNameOffset, OtNameLength).Fill(0xFF);
        var key = pid ^ otId;
        for (var i = 0x20; i < 0x50; i += 4)
            WriteU32(raw, i, key);
        return new BoxPokemon(raw, layout);
    }

    public BoxMonInfo GetInfo()
    {
        if (_layout == PokemonDataLayout.DestinyCfruPlainBox)
        {
            var destinyIvWord = ReadU32(_raw, DestinyMiscOffset + 4);
            var destinyIvs = PartyPokemon.IvWordToDictionary(destinyIvWord);
            destinyIvs["ability"] = _raw[DestinyMiscOffset + MiscAbilitySlotOffset] & MiscAbilitySlotMask;
            return new BoxMonInfo(
                Pid,
                OtId,
                NatureFromPid(Pid),
                NatureOverrideUsePid,
                ReadU16(_raw, DestinyGrowthOffset),
                ReadU16(_raw, DestinyGrowthOffset + 2),
                ReadU32(_raw, DestinyGrowthOffset + 4),
                _raw[DestinyGrowthOffset + 8],
                _raw[DestinyGrowthOffset + 9],
                [ReadU16(_raw, DestinyAttacksOffset), ReadU16(_raw, DestinyAttacksOffset + 2), ReadU16(_raw, DestinyAttacksOffset + 4), ReadU16(_raw, DestinyAttacksOffset + 6)],
                _raw.AsSpan(DestinyAttacksOffset + 8, 4).ToArray(),
                _raw.AsSpan(DestinyEvsOffset, 6).ToArray(),
                destinyIvs,
                destinyIvWord,
                0,
                0);
        }
        if (_layout == PokemonDataLayout.UnboundCfruPlainParty)
        {
            var unboundIvWord = ReadU32(_raw, 54);
            var unboundIvs = PartyPokemon.IvWordToDictionary(unboundIvWord);
            unboundIvs["ability"] = (unboundIvWord & 0x80000000u) != 0 ? 2 : (int)(Pid & 1);
            return new BoxMonInfo(
                Pid,
                OtId,
                NatureFromPid(Pid),
                0x1A,
                ReadU16(_raw, 28),
                ReadU16(_raw, 30),
                ReadU32(_raw, 32),
                _raw[36],
                _raw[37],
                ReadCompressedMoves(),
                [0, 0, 0, 0],
                _raw.AsSpan(44, 6).ToArray(),
                unboundIvs,
                unboundIvWord,
                0,
                0);
        }

        var dec = Decrypted();
        var growth = Subblock(dec, 0);
        var attacks = Subblock(dec, 1);
        var evs = Subblock(dec, 2);
        var misc = Subblock(dec, 3);
        var ivWord = ReadU32(misc, 4);
        var ivs = PartyPokemon.IvWordToDictionary(ivWord);
        ivs["ability"] = _layout == PokemonDataLayout.UnboundCfruPlainParty
            ? ((ivWord & 0x80000000u) != 0 ? 2 : (int)(Pid & 1))
            : misc[MiscAbilitySlotOffset] & MiscAbilitySlotMask;
        return new BoxMonInfo(
            Pid,
            OtId,
            NatureFromPid(Pid),
            _layout == PokemonDataLayout.UnboundCfruPlainParty ? (byte)0x1A : ReadGameNatureCode(growth),
            ReadU16(growth, 0),
            ReadU16(growth, 2),
            _layout == PokemonDataLayout.UnboundCfruPlainParty ? ReadU32(growth, 4) : ReadExp(growth),
            growth[_layout == PokemonDataLayout.UnboundCfruPlainParty ? 8 : GrowthPpBonusesOffset],
            growth[_layout == PokemonDataLayout.UnboundCfruPlainParty ? 9 : GrowthFriendshipOffset],
            [ReadU16(attacks, 0), ReadU16(attacks, 2), ReadU16(attacks, 4), ReadU16(attacks, 6)],
            [attacks[8], attacks[9], attacks[10], attacks[11]],
            evs[..6].ToArray(),
            ivs,
            ivWord,
            Checksum,
            PartyPokemon.ChecksumDecrypted(dec));
    }

    public void SetGrowth(ushort? species = null, ushort? item = null, uint? exp = null, byte? friendship = null, byte? ppBonuses = null)
    {
        if (_layout == PokemonDataLayout.DestinyCfruPlainBox)
        {
            if (species is not null) WriteU16(_raw, DestinyGrowthOffset, species.Value);
            if (item is not null) WriteU16(_raw, DestinyGrowthOffset + 2, item.Value);
            if (exp is not null) WriteU32(_raw, DestinyGrowthOffset + 4, exp.Value);
            if (ppBonuses is not null) _raw[DestinyGrowthOffset + 8] = ppBonuses.Value;
            if (friendship is not null) _raw[DestinyGrowthOffset + 9] = friendship.Value;
            return;
        }
        if (_layout == PokemonDataLayout.UnboundCfruPlainParty)
        {
            if (species is not null)
            {
                WriteU16(_raw, 28, species.Value);
                _raw[0x13] |= 2;
            }
            if (item is not null) WriteU16(_raw, 30, item.Value);
            if (exp is not null) WriteU32(_raw, 32, exp.Value);
            if (ppBonuses is not null) _raw[36] = ppBonuses.Value;
            if (friendship is not null) _raw[37] = friendship.Value;
            return;
        }

        var dec = Decrypted();
        var block = Subblock(dec, 0);
        if (species is not null) WriteU16(block, 0, species.Value);
        if (item is not null) WriteU16(block, 2, item.Value);
        if (exp is not null)
        {
            if (_layout == PokemonDataLayout.UnboundCfruPlainParty) WriteU32(block, 4, exp.Value);
            else WriteExp(block, exp.Value);
        }
        if (ppBonuses is not null) block[_layout == PokemonDataLayout.UnboundCfruPlainParty ? 8 : GrowthPpBonusesOffset] = ppBonuses.Value;
        if (friendship is not null) block[_layout == PokemonDataLayout.UnboundCfruPlainParty ? 9 : GrowthFriendshipOffset] = friendship.Value;
        ReplaceSubblock(dec, 0, block);
        SetDecrypted(dec);
    }

    public void SetGameNatureCode(int code)
    {
        if (_layout is PokemonDataLayout.UnboundCfruPlainParty or PokemonDataLayout.DestinyCfruPlainBox)
        {
            if (code != NatureOverrideUsePid)
                throw new InvalidOperationException("宝可梦解放的性格由 PID 决定，不支持旧版性格覆盖字段。");
            return;
        }
        if (code is < 0 or > 0x1F) throw new ArgumentOutOfRangeException(nameof(code), "game nature code must be 0..31");
        var dec = Decrypted();
        var block = Subblock(dec, 0);
        SetGameNatureCode(block, code);
        ReplaceSubblock(dec, 0, block);
        SetDecrypted(dec);
    }

    public void SetNicknameFromSpeciesNameEntry(ReadOnlySpan<byte> speciesNameEntry)
    {
        if (speciesNameEntry.Length < NicknameLength)
            throw new ArgumentException($"Species name entry must contain at least {NicknameLength} bytes.", nameof(speciesNameEntry));
        speciesNameEntry[..NicknameLength].CopyTo(_raw.AsSpan(NicknameOffset, NicknameLength));
        if (_layout == PokemonDataLayout.UnboundCfruPlainParty) _raw[0x12] = 2;
        else if (_layout != PokemonDataLayout.DestinyCfruPlainBox) _raw[0x12] = 0x12;
    }

    public void SetMoves(IReadOnlyList<ushort?> moves, IReadOnlyList<byte?> pp)
    {
        if (_layout == PokemonDataLayout.DestinyCfruPlainBox)
        {
            for (var i = 0; i < Math.Min(4, moves.Count); i++)
                if (moves[i] is ushort move) WriteU16(_raw, DestinyAttacksOffset + i * 2, move);
            for (var i = 0; i < Math.Min(4, pp.Count); i++)
                if (pp[i] is byte value) _raw[DestinyAttacksOffset + 8 + i] = value;
            return;
        }
        if (_layout == PokemonDataLayout.UnboundCfruPlainParty)
        {
            var current = ReadCompressedMoves();
            for (var i = 0; i < Math.Min(4, moves.Count); i++)
                if (moves[i] is ushort move)
                {
                    if (move > 0x3FF) throw new ArgumentOutOfRangeException(nameof(moves), "解放版压缩箱子招式 ID 必须小于 1024。");
                    current[i] = move;
                }
            WriteCompressedMoves(current);
            return; // CFRU 压缩箱子不保存当前 PP，取出时按招式和 PP 提升重算。
        }

        var dec = Decrypted();
        var block = Subblock(dec, 1);
        for (var i = 0; i < Math.Min(4, moves.Count); i++)
            if (moves[i] is ushort move) WriteU16(block, i * 2, move);
        for (var i = 0; i < Math.Min(4, pp.Count); i++)
            if (pp[i] is byte value) block[8 + i] = value;
        ReplaceSubblock(dec, 1, block);
        SetDecrypted(dec);
    }

    public void SetEvs(Dictionary<string, byte> values)
    {
        if (_layout == PokemonDataLayout.DestinyCfruPlainBox)
        {
            for (var i = 0; i < Gen3Constants.StatNames.Length; i++)
                if (values.TryGetValue(Gen3Constants.StatNames[i], out var value)) _raw[DestinyEvsOffset + i] = value;
            return;
        }
        if (_layout == PokemonDataLayout.UnboundCfruPlainParty)
        {
            for (var i = 0; i < Gen3Constants.StatNames.Length; i++)
                if (values.TryGetValue(Gen3Constants.StatNames[i], out var value)) _raw[44 + i] = value;
            return;
        }

        var dec = Decrypted();
        var block = Subblock(dec, 2);
        for (var i = 0; i < Gen3Constants.StatNames.Length; i++)
            if (values.TryGetValue(Gen3Constants.StatNames[i], out var value)) block[i] = value;
        ReplaceSubblock(dec, 2, block);
        SetDecrypted(dec);
    }

    public void SetIvs(Dictionary<string, int> values)
    {
        if (_layout == PokemonDataLayout.DestinyCfruPlainBox)
        {
            WriteU32(_raw, DestinyMiscOffset + 4, SetIvWord(ReadU32(_raw, DestinyMiscOffset + 4), values));
            if (values.TryGetValue("ability", out var destinyAbilitySlot)) SetAbilitySlot(destinyAbilitySlot);
            return;
        }
        if (_layout == PokemonDataLayout.UnboundCfruPlainParty)
        {
            var unboundWord = SetIvWord(ReadU32(_raw, 54), values);
            WriteU32(_raw, 54, unboundWord);
            if (values.TryGetValue("ability", out var unboundAbilitySlot)) SetAbilitySlot(unboundAbilitySlot);
            return;
        }

        var dec = Decrypted();
        var block = Subblock(dec, 3);
        var word = SetIvWord(ReadU32(block, 4), values);
        WriteU32(block, 4, word);
        if (values.TryGetValue("ability", out var abilitySlot))
            block[MiscAbilitySlotOffset] = (byte)((block[MiscAbilitySlotOffset] & ~MiscAbilitySlotMask) | (abilitySlot & MiscAbilitySlotMask));
        ReplaceSubblock(dec, 3, block);
        SetDecrypted(dec);
    }

    public void SetNature(int nature)
    {
        if (nature is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(nature), "nature must be 0..24");
        if (_layout is PokemonDataLayout.UnboundCfruPlainParty or PokemonDataLayout.DestinyCfruPlainBox)
        {
            WriteU32(_raw, 0, FindPidWithNatureAndShinyState(Pid, OtId, nature, IsShiny));
            return;
        }
        var dec = Decrypted();
        if (_layout != PokemonDataLayout.UnboundCfruPlainParty)
        {
            var growth = Subblock(dec, 0);
            SetGameNatureCode(growth, NatureOverrideUsePid);
            ReplaceSubblock(dec, 0, growth);
        }
        WriteU32(_raw, 0, FindPidWithNatureAndShinyState(Pid, OtId, nature, IsShiny));
        SetDecrypted(dec);
    }

    public void SetShiny(bool shiny)
    {
        if (IsShiny == shiny) return;
        if (_layout is PokemonDataLayout.UnboundCfruPlainParty or PokemonDataLayout.DestinyCfruPlainBox)
        {
            WriteU32(_raw, 0, FindPidWithShinyState(Pid, OtId, shiny));
            return;
        }
        var decrypted = Decrypted();
        WriteU32(_raw, 0, FindPidWithShinyState(Pid, OtId, shiny));
        SetDecrypted(decrypted);
    }

    public void SetGender(byte genderRatio, int gender)
    {
        if (PartyPokemon.GenderFromPid(Pid, genderRatio) == gender) return;
        ValidateGenderTarget(genderRatio, gender);
        if (_layout is PokemonDataLayout.UnboundCfruPlainParty or PokemonDataLayout.DestinyCfruPlainBox)
        {
            WriteU32(_raw, 0, FindPidWithNatureShinyGenderState(Pid, OtId, NatureFromPid(Pid), IsShiny, genderRatio, gender));
            return;
        }
        var decrypted = Decrypted();
        WriteU32(_raw, 0, FindPidWithNatureShinyGenderState(Pid, OtId, NatureFromPid(Pid), IsShiny, genderRatio, gender));
        SetDecrypted(decrypted);
    }

    public void SetOtId(uint otId)
    {
        if (OtId == otId) return;
        if (_layout is PokemonDataLayout.UnboundCfruPlainParty or PokemonDataLayout.DestinyCfruPlainBox)
        {
            WriteU32(_raw, 4, otId);
            return;
        }
        var decrypted = Decrypted();
        WriteU32(_raw, 4, otId);
        SetDecrypted(decrypted);
    }

    public void SetOtName(ReadOnlySpan<byte> otName)
    {
        var target = _raw.AsSpan(_layout is PokemonDataLayout.UnboundCfruPlainParty or PokemonDataLayout.DestinyCfruPlainBox ? 0x14 : OtNameOffset, OtNameLength);
        target.Fill(0xFF);
        otName[..Math.Min(otName.Length, OtNameLength)].CopyTo(target);
    }

    private static uint ReadExp(ReadOnlySpan<byte> growth)
        => ReadU32(growth, 4) & GrowthExpMask;

    private static void WriteExp(Span<byte> growth, uint exp)
    {
        if (exp > GrowthExpMask)
            throw new ArgumentOutOfRangeException(nameof(exp), $"experience must be 0..{GrowthExpMask}");
        var preserved = ReadU32(growth, 4) & ~GrowthExpMask;
        WriteU32(growth, 4, preserved | exp);
    }

    private static int NatureFromPid(uint pid) => (int)(pid % 25);

    private static byte ReadGameNatureCode(ReadOnlySpan<byte> growth)
        => (byte)((ReadU32(growth, GrowthNatureOverrideWordOffset) & GrowthNatureOverrideMask) >> GrowthNatureOverrideShift);

    private static void SetGameNatureCode(Span<byte> growth, int code)
    {
        var word = ReadU32(growth, GrowthNatureOverrideWordOffset);
        word = (word & ~GrowthNatureOverrideMask) | (((uint)code & 0x1F) << GrowthNatureOverrideShift);
        WriteU32(growth, GrowthNatureOverrideWordOffset, word);
    }

    private static uint SetIvWord(uint word, Dictionary<string, int> values)
    {
        var ivs = PartyPokemon.IvWordToDictionary(word);
        foreach (var (key, value) in values)
        {
            if (ivs.ContainsKey(key)) ivs[key] = value;
        }
        var result = word & 0x80000000u;
        result |= (uint)(ivs["hp"] & 0x1F);
        result |= (uint)(ivs["atk"] & 0x1F) << 5;
        result |= (uint)(ivs["def"] & 0x1F) << 10;
        result |= (uint)(ivs["spe"] & 0x1F) << 15;
        result |= (uint)(ivs["spa"] & 0x1F) << 20;
        result |= (uint)(ivs["spd"] & 0x1F) << 25;
        result |= (uint)(ivs["egg"] & 1) << 30;
        return result;
    }

    public void SetAbilitySlot(int abilitySlot, byte? genderRatio = null)
    {
        if (abilitySlot is not (0 or 1 or 2)) throw new ArgumentOutOfRangeException(nameof(abilitySlot), "ability slot must be 0..2");
        if (_layout == PokemonDataLayout.DestinyCfruPlainBox)
        {
            var offset = DestinyMiscOffset + MiscAbilitySlotOffset;
            _raw[offset] = (byte)((_raw[offset] & ~MiscAbilitySlotMask) | (abilitySlot & MiscAbilitySlotMask));
            return;
        }
        if (_layout != PokemonDataLayout.UnboundCfruPlainParty)
        {
            SetIvs(new Dictionary<string, int> { ["ability"] = abilitySlot });
            return;
        }

        var word = ReadU32(_raw, 54);
        if (abilitySlot == 2)
        {
            WriteU32(_raw, 54, word | 0x80000000u);
            return;
        }

        WriteU32(_raw, 54, word & 0x7FFFFFFFu);
        var newPid = Pid;
        if ((Pid & 1) != (uint)abilitySlot)
        {
            var oldGender = genderRatio is byte ratio ? PartyPokemon.GenderFromPid(Pid, ratio) : (int?)null;
            newPid = FindPidForAbility(Pid, OtId, abilitySlot, genderRatio, oldGender);
        }
        WriteU32(_raw, 0, newPid);
    }

    private static uint FindPidForAbility(uint oldPid, uint otId, int abilitySlot, byte? genderRatio, int? gender)
    {
        var nature = (int)(oldPid % 25);
        var shiny = PartyPokemon.IsShinyPid(oldPid, otId);
        for (ulong delta = 1; delta <= uint.MaxValue; delta++)
        {
            if ((ulong)oldPid + delta <= uint.MaxValue)
            {
                var candidate = oldPid + (uint)delta;
                if ((candidate & 1) == (uint)abilitySlot && candidate % 25 == nature && PartyPokemon.IsShinyPid(candidate, otId) == shiny &&
                    (genderRatio is not byte ratio || gender is null || PartyPokemon.GenderFromPid(candidate, ratio) == gender)) return candidate;
            }
            if (oldPid >= delta)
            {
                var candidate = oldPid - (uint)delta;
                if ((candidate & 1) == (uint)abilitySlot && candidate % 25 == nature && PartyPokemon.IsShinyPid(candidate, otId) == shiny &&
                    (genderRatio is not byte ratio || gender is null || PartyPokemon.GenderFromPid(candidate, ratio) == gender)) return candidate;
            }
        }
        throw new InvalidOperationException("无法生成保持性格、闪光和性别的特性 PID。");
    }

    private ushort[] ReadCompressedMoves()
    {
        ulong packed = 0;
        for (var i = 0; i < 5; i++) packed |= (ulong)_raw[39 + i] << (i * 8);
        return
        [
            (ushort)(packed & 0x3FF),
            (ushort)((packed >> 10) & 0x3FF),
            (ushort)((packed >> 20) & 0x3FF),
            (ushort)((packed >> 30) & 0x3FF)
        ];
    }

    private void WriteCompressedMoves(IReadOnlyList<ushort> moves)
    {
        ulong packed = 0;
        for (var i = 0; i < 4; i++) packed |= ((ulong)moves[i] & 0x3FF) << (i * 10);
        for (var i = 0; i < 5; i++) _raw[39 + i] = (byte)(packed >> (i * 8));
    }

    private byte[] Decrypted()
    {
        var output = new byte[48];
        for (var i = 0; i < 48; i += 4)
            WriteU32(output, i, ReadU32(_raw, 0x20 + i) ^ Key);
        return output;
    }

    private void SetDecrypted(byte[] decrypted)
    {
        WriteU16(_raw, 0x1C, PartyPokemon.ChecksumDecrypted(decrypted));
        for (var i = 0; i < 48; i += 4)
            WriteU32(_raw, 0x20 + i, ReadU32(decrypted, i) ^ Key);
    }

    private static uint FindPidWithShinyState(uint oldPid, uint otId, bool shiny)
        => FindPidWithNatureAndShinyState(oldPid, otId, (int)(oldPid % 25), shiny);

    private static uint FindPidWithNatureAndShinyState(uint oldPid, uint otId, int nature, bool shiny)
    {
        var targetNature = (uint)nature;
        var targetOrder = oldPid % 24;
        uint residue = 0;
        for (; residue < 600; residue++)
            if (residue % 25 == targetNature && residue % 24 == targetOrder) break;
        if (residue >= 600) throw new InvalidOperationException("无法生成保持性格和加密顺序的 PID。");

        var oldK = oldPid >= residue ? (oldPid - residue) / 600 : 0;
        var maxK = (uint.MaxValue - residue) / 600;
        for (uint delta = 0; delta <= maxK; delta++)
        {
            if (oldK + delta <= maxK)
            {
                var candidate = residue + (oldK + delta) * 600;
                if (PartyPokemon.IsShinyPid(candidate, otId) == shiny) return candidate;
            }
            if (delta != 0 && oldK >= delta)
            {
                var candidate = residue + (oldK - delta) * 600;
                if (PartyPokemon.IsShinyPid(candidate, otId) == shiny) return candidate;
            }
        }

        throw new InvalidOperationException("没有找到可用的闪光 PID。");
    }

    private static uint FindPidWithNatureShinyGenderState(uint oldPid, uint otId, int nature, bool shiny, byte genderRatio, int gender)
    {
        var targetNature = (uint)nature;
        var targetOrder = oldPid % 24;
        uint residue = 0;
        for (; residue < 600; residue++)
            if (residue % 25 == targetNature && residue % 24 == targetOrder) break;
        if (residue >= 600) throw new InvalidOperationException("无法生成保持性格和加密顺序的 PID。");

        var oldK = oldPid >= residue ? (oldPid - residue) / 600 : 0;
        var maxK = (uint.MaxValue - residue) / 600;
        for (uint delta = 0; delta <= maxK; delta++)
        {
            if (oldK + delta <= maxK)
            {
                var candidate = residue + (oldK + delta) * 600;
                if (PartyPokemon.IsShinyPid(candidate, otId) == shiny && PartyPokemon.GenderFromPid(candidate, genderRatio) == gender) return candidate;
            }
            if (delta != 0 && oldK >= delta)
            {
                var candidate = residue + (oldK - delta) * 600;
                if (PartyPokemon.IsShinyPid(candidate, otId) == shiny && PartyPokemon.GenderFromPid(candidate, genderRatio) == gender) return candidate;
            }
        }

        throw new InvalidOperationException("没有找到可用的性别 PID。");
    }

    private static void ValidateGenderTarget(byte genderRatio, int gender)
    {
        if (gender is not (PartyPokemon.GenderMale or PartyPokemon.GenderFemale or PartyPokemon.Genderless))
            throw new ArgumentOutOfRangeException(nameof(gender), "gender must be male, female, or genderless.");
        if (genderRatio == 255)
        {
            if (gender != PartyPokemon.Genderless) throw new InvalidOperationException("该宝可梦固定为无性别，不能改为公 ♂ 或母 ♀。");
            return;
        }
        if (gender == PartyPokemon.Genderless) throw new InvalidOperationException("该宝可梦不是无性别种族，不能改为无性别。");
        if (genderRatio == 0 && gender != PartyPokemon.GenderMale) throw new InvalidOperationException("该宝可梦固定为公 ♂。");
        if (genderRatio == 254 && gender != PartyPokemon.GenderFemale) throw new InvalidOperationException("该宝可梦固定为母 ♀。");
    }

    private byte[] Subblock(byte[] decrypted, int substructure)
    {
        var offset = Array.IndexOf(Gen3Constants.SubstructureOrders[Pid % 24], substructure) * 12;
        return decrypted[offset..(offset + 12)];
    }

    private void ReplaceSubblock(byte[] decrypted, int substructure, byte[] block)
    {
        var offset = Array.IndexOf(Gen3Constants.SubstructureOrders[Pid % 24], substructure) * 12;
        block.CopyTo(decrypted.AsSpan(offset, 12));
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | data[offset + 1] << 8);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static void WriteU16(Span<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
