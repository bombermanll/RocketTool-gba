namespace RocketTool.Core;

public sealed class PokemonExperienceTable
{
    public const int MaxLevel = 150;
    public const uint MaxStoredExperience = 0x007FFFFF;
    private const int GrowthRateCount = 6;
    private readonly uint[][] _thresholds;

    public PokemonExperienceTable(ModifierDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _thresholds = new uint[GrowthRateCount][];

        var rows = database.Table("experience");
        for (var growthRate = 0; growthRate < GrowthRateCount; growthRate++)
        {
            if (!rows.TryGetValue(growthRate, out var raw))
                throw new InvalidDataException($"经验表缺少成长率 {growthRate}。");

            var parts = raw.Split('\t');
            if (parts.Length != MaxLevel + 1)
                throw new InvalidDataException($"成长率 {growthRate} 的经验表应有 {MaxLevel + 1} 项，实际为 {parts.Length} 项。");

            var values = new uint[MaxLevel + 1];
            for (var level = 0; level <= MaxLevel; level++)
            {
                if (!uint.TryParse(parts[level], out values[level]) || values[level] > MaxStoredExperience)
                    throw new InvalidDataException($"成长率 {growthRate} 的 {level} 级经验值无效：{parts[level]}。");
            }
            _thresholds[growthRate] = values;
        }
    }

    public uint ExperienceForLevel(int level, byte growthRate)
    {
        ValidateGrowthRate(growthRate);
        if (level is < 0 or > MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(level), $"level must be 0..{MaxLevel}");
        return _thresholds[growthRate][level];
    }

    public int LevelFromExperience(uint experience, byte growthRate)
    {
        ValidateGrowthRate(growthRate);
        var level = 1;
        while (level < MaxLevel && _thresholds[growthRate][level + 1] <= experience)
            level++;
        return level;
    }

    public bool IsConsistent(uint experience, int level, byte growthRate)
        => experience <= MaxStoredExperience
           && level is >= 1 and <= MaxLevel
           && experience >= ExperienceForLevel(level, growthRate)
           && LevelFromExperience(experience, growthRate) == level;

    public uint RemapPreservingLevelProgress(
        uint experience,
        int sourceLevel,
        byte sourceGrowthRate,
        int targetLevel,
        byte targetGrowthRate)
    {
        ValidateGrowthRate(sourceGrowthRate);
        ValidateGrowthRate(targetGrowthRate);
        if (sourceLevel is < 1 or > MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(sourceLevel));
        if (targetLevel is < 1 or > MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(targetLevel));

        var sourceMin = ExperienceForLevel(sourceLevel, sourceGrowthRate);
        var targetMin = ExperienceForLevel(targetLevel, targetGrowthRate);
        if (targetLevel == MaxLevel) return targetMin;

        var sourceNext = ExperienceForLevel(Math.Min(sourceLevel + 1, MaxLevel), sourceGrowthRate);
        var targetNext = ExperienceForLevel(targetLevel + 1, targetGrowthRate);
        if (sourceNext <= sourceMin || targetNext <= targetMin) return targetMin;

        var sourceSpan = sourceNext - sourceMin;
        var targetSpan = targetNext - targetMin;
        var sourceProgress = Math.Clamp((long)experience - sourceMin, 0, sourceSpan - 1);
        var targetProgress = (ulong)sourceProgress * targetSpan / sourceSpan;
        return targetMin + (uint)Math.Min(targetProgress, targetSpan - 1);
    }

    private static void ValidateGrowthRate(byte growthRate)
    {
        if (growthRate >= GrowthRateCount)
            throw new ArgumentOutOfRangeException(nameof(growthRate), $"growth rate must be 0..{GrowthRateCount - 1}");
    }
}
