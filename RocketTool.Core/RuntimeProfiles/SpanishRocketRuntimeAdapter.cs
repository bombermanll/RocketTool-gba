namespace RocketTool.Core;

public sealed class SpanishRocketRuntimeAdapter : GameRuntimeAdapterBase
{
    public const string StrategyId = "spanish-rocket-runtime-v1";

    public SpanishRocketRuntimeAdapter(GameProfile profile) : base(profile) { }

    public override PokemonDataLayout PartyLayout => PokemonDataLayout.SpanishRocketEncrypted;
    public override PokemonDataLayout LiveBoxLayout => PokemonDataLayout.SpanishRocketEncrypted;
    public override IReadOnlyList<int> MoveTypeIds { get; } = Enumerable.Range(0, 19).ToArray();
    public override string? SpeciesFormLabel(int species)
    {
        if (species is >= 977 and <= 1033) return species switch { 978 => "Mega X", 979 => "Mega Y", 990 => "Mega X", 991 => "Mega Y", _ => "Mega" };
        if (species is 1034 or 1035) return "原始回归";
        if (species is >= 1046 and <= 1063) return "阿罗拉";
        if (species is 1064 or >= 1065 and <= 1082) return "伽勒尔";
        if (species is >= 1083 and <= 1096) return $"换装{species - 1082}";
        if (species is >= 1098 and <= 1124) return $"字母形态{species - 1097}";
        if (species is >= 1145 and <= 1161)
        {
            string[] types = ["格斗", "飞行", "毒", "地面", "岩石", "虫", "幽灵", "钢", "火", "水", "草", "电", "超能力", "冰", "龙", "恶", "妖精"];
            return types[species - 1145];
        }
        if (species is >= 1184 and <= 1202) return $"花纹{species - 1183}";
        if (species is >= 1203 and <= 1215) return $"颜色{species - 1202}";
        if (species is >= 1216 and <= 1224) return $"造型{species - 1215}";
        if (species is >= 1246 and <= 1262) return $"属性{species - 1245}";
        if (species is >= 1263 and <= 1275) return $"核心色{species - 1262}";
        if (species is >= 1286 and <= 1293) return $"奶油形态{species - 1285}";

        return species switch
        {
            913 or 914 or 915 => "P", 919 => "特殊形态", 920 => "攻击形态", 921 => "防御形态",
            923 => "闪电卡带", 924 => "火焰卡带", 944 => "盾牌形态", 947 => "特殊形态", 953 => "标点形态",
            1072 or 1073 or 1074 => "伽勒尔", 1097 => "刺刺耳", 1125 => "太阳", 1126 => "雨水", 1127 => "雪云",
            1128 => "攻击形态", 1129 => "防御形态", 1130 => "速度形态", 1131 or 1133 => "沙土蓑衣",
            1132 or 1134 => "垃圾蓑衣", 1135 => "晴天", 1136 or 1137 => "东海", 1138 => "加热", 1139 => "清洗",
            1140 => "结冰", 1141 => "旋转", 1142 => "切割", 1143 => "起源形态", 1144 => "天空形态",
            1162 => "蓝条纹", 1163 => "达摩模式", 1164 => "伽勒尔达摩模式", 1165 or 1168 => "夏天",
            1166 or 1169 => "秋天", 1167 or 1170 => "冬天", 1171 or 1172 or 1173 => "灵兽形态",
            1174 => "焰白", 1175 => "暗黑", 1176 => "觉悟形态", 1177 => "舞步形态", 1178 => "水流卡带",
            1179 => "冰冻卡带", 1180 => "闪电卡带", 1181 => "火焰卡带", 1182 => "小智版", 1183 => "羁绊变身",
            1225 => "雌性", 1226 => "刀剑形态", 1227 or 1230 => "小尺寸", 1228 or 1231 => "大尺寸",
            1229 or 1232 => "特大尺寸", 1233 => "活跃模式", 1234 => "10%形态", 1235 => "50%形态",
            1236 => "完全体", 1237 => "核心", 1238 => "解放形态", 1239 => "啪滋啪滋", 1240 => "呼拉呼拉",
            1241 => "轻盈轻盈", 1242 => "我行我素", 1243 => "黑夜", 1244 => "黄昏", 1245 => "鱼群形态",
            1276 => "现形", 1277 => "黄昏之鬃", 1278 => "拂晓之翼", 1279 => "究极", 1280 => "500年前",
            1281 => "一口吞", 1282 => "大口吞", 1283 => "低调", 1284 or 1285 => "真品", 1294 => "解冻头",
            1295 => "雌性", 1296 => "空腹花纹", 1297 => "剑之王", 1298 => "盾之王", 1299 => "无极巨化",
            1300 => "连击流", 1301 => "披披形态", 1302 => "白马", 1303 => "黑马", 1304 => "P形态2", 1307 => "P形态2",
            1310 or 1311 or 1312 or 1315 or 1317 or 1319 or 1320 or 1321 or 1322 or 1326 or 1327 or 1328 or 1330 or 1331 or 1332 or 1333 => "洗翠",
            1323 => "白条纹", 1324 => "雄性", 1325 => "雌性", 1334 => "化身形态", 1335 => "灵兽形态",
            1336 or 1337 => "起源形态", 1338 => "帕底亚", 1342 => "帕底亚斗战种", 1343 => "帕底亚火炽种",
            1344 => "帕底亚水澜种", 1383 => "超极巨", _ => null
        };
    }
    protected override string ExpectedProfileId => "spanish-rocket-current";
    protected override string ExpectedRuntimeStrategy => StrategyId;
    protected override string ExpectedPokemonStrategy => "spanish-rocket-pokemon-v1";
    protected override string ExpectedPartyStrategy => "spanish-rocket-party-v1";
    protected override string ExpectedBoxStrategy => "spanish-rocket-box-v1";
    protected override string ExpectedBagStrategy => "spanish-rocket-bag-v1";
    protected override string ExpectedSaveStrategy => SpanishRocketSaveStrategy.StrategyId;
}
