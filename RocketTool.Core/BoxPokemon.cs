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
    private const int NicknameOffset = 0x08;
    private const int NicknameLength = 10;
    private const int OtNameOffset = 0x14;
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
    private readonly byte[] _raw;

    public BoxPokemon(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != Size) throw new ArgumentException($"Box Pokemon must be {Size} bytes");
        _raw = raw.ToArray();
    }

    public ReadOnlySpan<byte> Raw => _raw;
    public uint Pid => ReadU32(_raw, 0);
    public uint OtId => ReadU32(_raw, 4);
    public uint Key => Pid ^ OtId;
    public ushort Checksum => ReadU16(_raw, 0x1C);
    public bool IsEmpty => Pid == 0 && OtId == 0;
    public bool IsShiny => PartyPokemon.IsShinyPid(Pid, OtId);

    public static BoxPokemon Create(uint pid, uint otId)
    {
        if (pid == 0) throw new ArgumentOutOfRangeException(nameof(pid), "PID must be non-zero.");
        var raw = new byte[Size];
        WriteU32(raw, 0x00, pid);
        WriteU32(raw, 0x04, otId);
        raw[0x12] = 0x12;
        raw[0x13] = 0x0B;
        var key = pid ^ otId;
        for (var i = 0x20; i < 0x50; i += 4)
            WriteU32(raw, i, key);
        return new BoxPokemon(raw);
    }

    public BoxMonInfo GetInfo()
    {
        var dec = Decrypted();
        var growth = Subblock(dec, 0);
        var attacks = Subblock(dec, 1);
        var evs = Subblock(dec, 2);
        var misc = Subblock(dec, 3);
        var ivWord = ReadU32(misc, 4);
        var ivs = PartyPokemon.IvWordToDictionary(ivWord);
        ivs["ability"] = misc[MiscAbilitySlotOffset] & MiscAbilitySlotMask;
        return new BoxMonInfo(
            Pid,
            OtId,
            NatureFromPid(Pid),
            ReadGameNatureCode(growth),
            ReadU16(growth, 0),
            ReadU16(growth, 2),
            ReadExp(growth),
            growth[GrowthPpBonusesOffset],
            growth[GrowthFriendshipOffset],
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
        var dec = Decrypted();
        var block = Subblock(dec, 0);
        if (species is not null) WriteU16(block, 0, species.Value);
        if (item is not null) WriteU16(block, 2, item.Value);
        if (exp is not null) WriteExp(block, exp.Value);
        if (ppBonuses is not null) block[GrowthPpBonusesOffset] = ppBonuses.Value;
        if (friendship is not null) block[GrowthFriendshipOffset] = friendship.Value;
        ReplaceSubblock(dec, 0, block);
        SetDecrypted(dec);
    }

    public void SetGameNatureCode(int code)
    {
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
        _raw[0x12] = 0x12;
        _raw[0x13] = 0x0B;
    }

    public void SetMoves(IReadOnlyList<ushort?> moves, IReadOnlyList<byte?> pp)
    {
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
        var dec = Decrypted();
        var block = Subblock(dec, 2);
        for (var i = 0; i < Gen3Constants.StatNames.Length; i++)
            if (values.TryGetValue(Gen3Constants.StatNames[i], out var value)) block[i] = value;
        ReplaceSubblock(dec, 2, block);
        SetDecrypted(dec);
    }

    public void SetIvs(Dictionary<string, int> values)
    {
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
        var dec = Decrypted();
        var growth = Subblock(dec, 0);
        SetGameNatureCode(growth, NatureOverrideUsePid);
        ReplaceSubblock(dec, 0, growth);
        WriteU32(_raw, 0, FindPidWithNatureAndShinyState(Pid, OtId, nature, IsShiny));
        SetDecrypted(dec);
    }

    public void SetShiny(bool shiny)
    {
        if (IsShiny == shiny) return;
        var decrypted = Decrypted();
        WriteU32(_raw, 0, FindPidWithShinyState(Pid, OtId, shiny));
        SetDecrypted(decrypted);
    }

    public void SetGender(byte genderRatio, int gender)
    {
        if (PartyPokemon.GenderFromPid(Pid, genderRatio) == gender) return;
        ValidateGenderTarget(genderRatio, gender);
        var decrypted = Decrypted();
        WriteU32(_raw, 0, FindPidWithNatureShinyGenderState(Pid, OtId, NatureFromPid(Pid), IsShiny, genderRatio, gender));
        SetDecrypted(decrypted);
    }

    public void SetOtId(uint otId)
    {
        if (OtId == otId) return;
        var decrypted = Decrypted();
        WriteU32(_raw, 4, otId);
        SetDecrypted(decrypted);
    }

    public void SetOtName(ReadOnlySpan<byte> otName)
    {
        var target = _raw.AsSpan(OtNameOffset, OtNameLength);
        target.Clear();
        otName[..Math.Min(otName.Length, OtNameLength)].CopyTo(target);
    }

    private static uint ReadExp(ReadOnlySpan<byte> growth)
        => ReadU32(growth, 4) & GrowthExpMask;

    private static void WriteExp(Span<byte> growth, uint exp)
    {
        var preserved = ReadU32(growth, 4) & ~GrowthExpMask;
        WriteU32(growth, 4, preserved | (exp & GrowthExpMask));
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
            if (gender != PartyPokemon.Genderless) throw new InvalidOperationException("该宝可梦固定为无性别，不能改为雄性或雌性。");
            return;
        }
        if (gender == PartyPokemon.Genderless) throw new InvalidOperationException("该宝可梦不是无性别种族，不能改为无性别。");
        if (genderRatio == 0 && gender != PartyPokemon.GenderMale) throw new InvalidOperationException("该宝可梦固定为雄性。");
        if (genderRatio == 254 && gender != PartyPokemon.GenderFemale) throw new InvalidOperationException("该宝可梦固定为雌性。");
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
