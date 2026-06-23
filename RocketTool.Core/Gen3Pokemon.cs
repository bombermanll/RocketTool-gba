namespace RocketTool.Core;

public static class Gen3Constants
{
    public const int PartyMonSize = 100;
    public const int PartySlots = 6;
    public const uint DefaultPartyBase = 0x02025170;
    public const int PartyCountOffsetFromPartyBase = -3;
    public const uint DefaultPartyCountAddress = 0x0202516D;
    public static readonly string[] StatNames = ["hp", "atk", "def", "spe", "spa", "spd"];

    public static readonly int[][] SubstructureOrders =
    [
        [0, 1, 2, 3], [0, 1, 3, 2], [0, 2, 1, 3], [0, 2, 3, 1],
        [0, 3, 1, 2], [0, 3, 2, 1], [1, 0, 2, 3], [1, 0, 3, 2],
        [1, 2, 0, 3], [1, 2, 3, 0], [1, 3, 0, 2], [1, 3, 2, 0],
        [2, 0, 1, 3], [2, 0, 3, 1], [2, 1, 0, 3], [2, 1, 3, 0],
        [2, 3, 0, 1], [2, 3, 1, 0], [3, 0, 1, 2], [3, 0, 2, 1],
        [3, 1, 0, 2], [3, 1, 2, 0], [3, 2, 0, 1], [3, 2, 1, 0],
    ];
}

public sealed record PartyMonInfo(
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
    uint Status,
    byte Level,
    ushort Hp,
    ushort MaxHp,
    ushort Attack,
    ushort Defense,
    ushort Speed,
    ushort SpAttack,
    ushort SpDefense,
    ushort Checksum,
    ushort CalculatedChecksum);

public sealed class PartyPokemon
{
    public const int Size = Gen3Constants.PartyMonSize;
    public const int GenderMale = 0;
    public const int GenderFemale = 1;
    public const int Genderless = 2;
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
    private readonly byte[] _raw;

    public PartyPokemon(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != Size) throw new ArgumentException($"Party Pokemon must be {Size} bytes");
        _raw = raw.ToArray();
    }

    public ReadOnlySpan<byte> Raw => _raw;
    public uint Pid => U32(0x00);
    public uint OtId => U32(0x04);
    public uint Key => Pid ^ OtId;
    public ushort Checksum => U16(0x1C);
    public bool IsEmpty => Pid == 0 && OtId == 0;
    public bool IsShiny => IsShinyPid(Pid, OtId);

    public static PartyPokemon Create(uint pid, uint otId)
    {
        if (pid == 0) throw new ArgumentOutOfRangeException(nameof(pid), "PID must be non-zero.");
        var raw = new byte[Size];
        WriteU32(raw, 0x00, pid);
        WriteU32(raw, 0x04, otId);
        raw[0x12] = 0x12;
        raw.AsSpan(OtNameOffset, OtNameLength).Fill(0xFF);
        var key = pid ^ otId;
        for (var i = 0x20; i < 0x50; i += 4)
            WriteU32(raw, i, key);
        return new PartyPokemon(raw);
    }

    public PartyMonInfo GetInfo()
    {
        var dec = Decrypted();
        var growth = Subblock(dec, 0);
        var attacks = Subblock(dec, 1);
        var evs = Subblock(dec, 2);
        var misc = Subblock(dec, 3);
        var ivWord = ReadU32(misc, 4);
        var ivs = IvWordToDictionary(ivWord);
        ivs["ability"] = AbilitySlotFromMisc(misc);
        return new PartyMonInfo(
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
            U32(0x50),
            _raw[0x54],
            U16(0x56),
            U16(0x58),
            U16(0x5A),
            U16(0x5C),
            U16(0x5E),
            U16(0x60),
            U16(0x62),
            Checksum,
            ChecksumDecrypted(dec));
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
    }

    public void SetMoves(IReadOnlyList<ushort?>? moves, IReadOnlyList<byte?>? pp)
    {
        var dec = Decrypted();
        var block = Subblock(dec, 1);
        if (moves is not null)
        {
            for (var i = 0; i < Math.Min(4, moves.Count); i++)
                if (moves[i] is ushort move) WriteU16(block, i * 2, move);
        }
        if (pp is not null)
        {
            for (var i = 0; i < Math.Min(4, pp.Count); i++)
                if (pp[i] is byte value) block[8 + i] = value;
        }
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
            SetAbilitySlotInMisc(block, abilitySlot);
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
        Put32(0x00, FindPidWithNatureAndShinyState(Pid, OtId, nature, IsShiny));
        SetDecrypted(dec);
    }

    public void SetShiny(bool shiny)
    {
        if (IsShiny == shiny) return;
        var decrypted = Decrypted();
        Put32(0x00, FindPidWithShinyState(Pid, OtId, shiny));
        SetDecrypted(decrypted);
    }

    public void SetGender(byte genderRatio, int gender)
    {
        if (GenderFromPid(Pid, genderRatio) == gender) return;
        var decrypted = Decrypted();
        Put32(0x00, FindPidWithNatureShinyGenderState(Pid, OtId, NatureFromPid(Pid), IsShiny, genderRatio, gender));
        SetDecrypted(decrypted);
    }

    public void SetOtId(uint otId)
    {
        if (OtId == otId) return;
        var decrypted = Decrypted();
        Put32(0x04, otId);
        SetDecrypted(decrypted);
    }

    public void SetOtName(ReadOnlySpan<byte> otName)
    {
        var target = _raw.AsSpan(OtNameOffset, OtNameLength);
        target.Fill(0xFF);
        otName[..Math.Min(otName.Length, OtNameLength)].CopyTo(target);
    }

    public void SetAbilitySlot(int abilitySlot)
    {
        if (abilitySlot is not (0 or 1 or 2)) throw new ArgumentOutOfRangeException(nameof(abilitySlot), "ability slot must be 0..2");
        SetIvs(new Dictionary<string, int> { ["ability"] = abilitySlot });
    }

    public void SetAbilityBit(int abilityBit) => SetAbilitySlot(abilityBit);

    public void SetUnencrypted(ushort? hp = null, ushort? maxHp = null, uint? status = null, byte? level = null)
    {
        if (status is not null) Put32(0x50, status.Value);
        if (level is not null) _raw[0x54] = level.Value;
        if (hp is not null) Put16(0x56, hp.Value);
        if (maxHp is not null) Put16(0x58, maxHp.Value);
    }

    public void RecalculateStats(SpeciesStats stats)
    {
        var info = GetInfo();
        if (info.Level == 0 || info.Species == 0) return;

        var oldMaxHp = info.MaxHp;
        var newMaxHp = info.Species == 292 ? (ushort)1 : CalculateHp(stats.Hp, info.Ivs["hp"], info.Evs[0], info.Level);
        var newHp = info.Hp;
        if (oldMaxHp == 0 || info.Hp >= oldMaxHp)
        {
            newHp = newMaxHp;
        }
        else
        {
            newHp = (ushort)Math.Clamp((info.Hp * newMaxHp + oldMaxHp / 2) / oldMaxHp, 1, newMaxHp);
        }

        SetUnencrypted(
            hp: newHp,
            maxHp: newMaxHp,
            level: null,
            status: null);
        var statNature = EffectiveStatNature(info.Nature, info.GameNatureCode);
        Put16(0x5A, CalculateOtherStat(stats.Attack, info.Ivs["atk"], info.Evs[1], info.Level, statNature, 0));
        Put16(0x5C, CalculateOtherStat(stats.Defense, info.Ivs["def"], info.Evs[2], info.Level, statNature, 1));
        Put16(0x5E, CalculateOtherStat(stats.Speed, info.Ivs["spe"], info.Evs[3], info.Level, statNature, 2));
        Put16(0x60, CalculateOtherStat(stats.SpAttack, info.Ivs["spa"], info.Evs[4], info.Level, statNature, 3));
        Put16(0x62, CalculateOtherStat(stats.SpDefense, info.Ivs["spd"], info.Evs[5], info.Level, statNature, 4));
    }


    private static uint ReadExp(ReadOnlySpan<byte> growth)
        => ReadU32(growth, 4) & GrowthExpMask;

    private static void WriteExp(Span<byte> growth, uint exp)
    {
        var preserved = ReadU32(growth, 4) & ~GrowthExpMask;
        WriteU32(growth, 4, preserved | (exp & GrowthExpMask));
    }

    private static int NatureFromPid(uint pid) => (int)(pid % 25);

    public static int EffectiveStatNature(int pidNature, byte gameNatureCode)
        => gameNatureCode == NatureOverrideUsePid ? pidNature : gameNatureCode;

    private static byte ReadGameNatureCode(ReadOnlySpan<byte> growth)
        => (byte)((ReadU32(growth, GrowthNatureOverrideWordOffset) & GrowthNatureOverrideMask) >> GrowthNatureOverrideShift);

    private static void SetGameNatureCode(Span<byte> growth, int code)
    {
        var word = ReadU32(growth, GrowthNatureOverrideWordOffset);
        word = (word & ~GrowthNatureOverrideMask) | (((uint)code & 0x1F) << GrowthNatureOverrideShift);
        WriteU32(growth, GrowthNatureOverrideWordOffset, word);
    }

    private byte[] Decrypted()
    {
        var outBytes = new byte[48];
        for (var i = 0; i < 48; i += 4)
            WriteU32(outBytes, i, U32(0x20 + i) ^ Key);
        return outBytes;
    }

    private void SetDecrypted(byte[] decrypted)
    {
        Put16(0x1C, ChecksumDecrypted(decrypted));
        for (var i = 0; i < 48; i += 4)
            Put32(0x20 + i, ReadU32(decrypted, i) ^ Key);
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

    private ushort U16(int offset) => ReadU16(_raw, offset);
    private uint U32(int offset) => ReadU32(_raw, offset);
    private void Put16(int offset, ushort value) => WriteU16(_raw, offset, value);
    private void Put32(int offset, uint value) => WriteU32(_raw, offset, value);

    public static ushort ChecksumDecrypted(ReadOnlySpan<byte> decrypted)
    {
        uint total = 0;
        for (var i = 0; i < 48; i += 2) total = (total + ReadU16(decrypted, i)) & 0xFFFF;
        return (ushort)total;
    }

    public static Dictionary<string, int> IvWordToDictionary(uint word) => new()
    {
        ["hp"] = (int)(word & 0x1F),
        ["atk"] = (int)((word >> 5) & 0x1F),
        ["def"] = (int)((word >> 10) & 0x1F),
        ["spe"] = (int)((word >> 15) & 0x1F),
        ["spa"] = (int)((word >> 20) & 0x1F),
        ["spd"] = (int)((word >> 25) & 0x1F),
        ["egg"] = (int)((word >> 30) & 1),
    };

    private static uint SetIvWord(uint word, Dictionary<string, int> values)
    {
        var ivs = IvWordToDictionary(word);
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

    private static int AbilitySlotFromMisc(ReadOnlySpan<byte> misc)
        => misc[MiscAbilitySlotOffset] & MiscAbilitySlotMask;

    private static void SetAbilitySlotInMisc(Span<byte> misc, int abilitySlot)
    {
        if (abilitySlot is not (0 or 1 or 2)) throw new ArgumentOutOfRangeException(nameof(abilitySlot), "ability slot must be 0..2");
        misc[MiscAbilitySlotOffset] = (byte)((misc[MiscAbilitySlotOffset] & ~MiscAbilitySlotMask) | (abilitySlot & MiscAbilitySlotMask));
    }

    private static ushort CalculateHp(int baseStat, int iv, int ev, int level)
        => (ushort)((((2 * baseStat + iv + ev / 4) * level) / 100) + level + 10);

    public static bool IsShinyPid(uint pid, uint otId)
        => (((pid & 0xFFFF) ^ (pid >> 16) ^ (otId & 0xFFFF) ^ (otId >> 16)) < 8);

    public static int GenderFromPid(uint pid, byte genderRatio) => genderRatio switch
    {
        255 => Genderless,
        254 => GenderFemale,
        0 => GenderMale,
        _ => (pid & 0xFF) < genderRatio ? GenderFemale : GenderMale
    };

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
                if (IsShinyPid(candidate, otId) == shiny) return candidate;
            }
            if (delta != 0 && oldK >= delta)
            {
                var candidate = residue + (oldK - delta) * 600;
                if (IsShinyPid(candidate, otId) == shiny) return candidate;
            }
        }

        throw new InvalidOperationException("没有找到可用的闪光 PID。");
    }

    private static uint FindPidWithNatureShinyGenderState(uint oldPid, uint otId, int nature, bool shiny, byte genderRatio, int gender)
    {
        ValidateGenderTarget(genderRatio, gender);
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
                if (IsShinyPid(candidate, otId) == shiny && GenderFromPid(candidate, genderRatio) == gender) return candidate;
            }
            if (delta != 0 && oldK >= delta)
            {
                var candidate = residue + (oldK - delta) * 600;
                if (IsShinyPid(candidate, otId) == shiny && GenderFromPid(candidate, genderRatio) == gender) return candidate;
            }
        }

        throw new InvalidOperationException("没有找到可用的性别 PID。");
    }

    private static void ValidateGenderTarget(byte genderRatio, int gender)
    {
        if (gender is not (GenderMale or GenderFemale or Genderless))
            throw new ArgumentOutOfRangeException(nameof(gender), "gender must be male, female, or genderless.");
        if (genderRatio == 255)
        {
            if (gender != Genderless) throw new InvalidOperationException("该宝可梦固定为无性别，不能改为雄性或雌性。");
            return;
        }
        if (gender == Genderless) throw new InvalidOperationException("该宝可梦不是无性别种族，不能改为无性别。");
        if (genderRatio == 0 && gender != GenderMale) throw new InvalidOperationException("该宝可梦固定为雄性。");
        if (genderRatio == 254 && gender != GenderFemale) throw new InvalidOperationException("该宝可梦固定为雌性。");
    }

    private static ushort CalculateOtherStat(int baseStat, int iv, int ev, int level, int nature, int statIndex)
    {
        var value = ((((2 * baseStat + iv + ev / 4) * level) / 100) + 5);
        if (nature is < 0 or > 24) return (ushort)value;
        var increased = nature / 5;
        var decreased = nature % 5;
        if (increased == statIndex && decreased != statIndex) value = value * 110 / 100;
        else if (decreased == statIndex && increased != statIndex) value = value * 90 / 100;
        return (ushort)value;
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
