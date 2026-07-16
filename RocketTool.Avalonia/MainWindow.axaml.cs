using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RocketTool.Core;

namespace RocketTool.Avalonia;

public sealed record PartySlotRow(int Slot, uint Address, PartyPokemon Mon, PartyMonInfo? Info, string Title, string Detail, Bitmap? Sprite);
public sealed record BagSlotRow(
    uint Address,
    ushort ItemId,
    ushort Quantity,
    int Pocket,
    int SlotInPocket,
    ushort QuantityKey,
    bool QuantityXor,
    string Title,
    string Detail);
public sealed record SaveBagEditableRow(
    BagSlotRow Row,
    IReadOnlyList<ChoiceRow> ItemChoices,
    ChoiceRow? SelectedItem,
    string QuantityText)
{
    public string SlotLabel => Row.SlotInPocket > 0 ? Row.SlotInPocket.ToString("00") : "--";
    public bool IsQuantityEditable => Row.Pocket != 8;
}
public sealed record BagCalibrationRequest(int Pocket, ushort ItemId, ushort Quantity);
public sealed record BoxSlotRow(int Slot, uint Address, BoxPokemon Mon, BoxMonInfo Info, string Title, string Detail, Bitmap? Sprite);
public sealed record BoxGridCell(int GlobalSlot, int SlotInBox, BoxSlotRow? Row, Bitmap? Sprite, string Title, bool HasPokemon, bool IsWritable)
{
    public bool CanInteract => HasPokemon || IsWritable;
}
public sealed record BoxGridGroup(int BoxNumber, string Title, IReadOnlyList<BoxGridCell> Cells);
public sealed record DexSpeciesRow(int Id, string Name, string DisplayName, string Title, Bitmap? Sprite)
{
    public override string ToString() => Title;
}
public sealed record DexTypeBadge(string Name, IBrush Background, IBrush Foreground);
public sealed record DexInfoRow(string Label, string Value, string? Tooltip = null, IReadOnlyList<DexTypeBadge>? TypeBadges = null)
{
    public bool HasTooltip => !string.IsNullOrWhiteSpace(Tooltip);
    public bool HasTypeBadges => TypeBadges is { Count: > 0 };
    public bool IsTextVisible => !HasTypeBadges && !HasTooltip;
    public bool IsTooltipTextVisible => !HasTypeBadges && HasTooltip;
}
public sealed record DexStatRow(
    string Name,
    string ModifiedBase,
    string OfficialBase,
    IBrush ModifiedBaseForeground,
    string Level50,
    string Level100,
    string MaxLevel);
public sealed record DexLevelMoveRow(
    string Level,
    string Move,
    string Type,
    string Category,
    string Power,
    string OfficialPower,
    IBrush PowerForeground,
    string Accuracy,
    string OfficialAccuracy,
    IBrush AccuracyForeground,
    string Pp);
public sealed record DexEncounterRow(string Map, string Method, string Level, string Rate, string Slot);
public sealed record OfficialSpeciesStats(int Hp, int Attack, int Defense, int Speed, int SpAttack, int SpDefense)
{
    public int Bst => Hp + Attack + Defense + Speed + SpAttack + SpDefense;
}
public sealed record OfficialMoveData(int Power, int Accuracy, int Pp);
public sealed record MoveDexRow(
    int Id,
    string Name,
    string Title,
    int TypeId,
    int CategoryId,
    IReadOnlyList<DexTypeBadge> TypeBadges,
    string Category,
    string Power,
    string OfficialPower,
    IBrush PowerForeground,
    string Accuracy,
    string OfficialAccuracy,
    IBrush AccuracyForeground,
    string Pp)
{
    public bool HasTypeBadges => TypeBadges.Count > 0;
}
public sealed record MapLocationChoice(string Name, int X, int Y)
{
    public override string ToString() => Name;
}
public sealed record ChoiceRow(int Id, string Name, string? Display = null)
{
    public override string ToString() => Display ?? Name;
}
public sealed record TrainerInfo(uint SaveBlock2Address, byte[] NameBytes, string Name, uint OtId, ushort TrainerId, ushort SecretId, string Source);

public enum DataSourceMode
{
    Live,
    SaveFile
}

public partial class MainWindow : Window
{
    public event EventHandler? VersionSwitchRequested;

    private const string AppNameBase = "火箭队修改工具";
    private const string AppTitleBase = "火箭队修改工具";
    private static readonly IBrush ComparisonNeutralBrush = new SolidColorBrush(Color.Parse("#102E4A"));
    private static readonly IBrush ComparisonHigherBrush = new SolidColorBrush(Color.Parse("#C33C36"));
    private static readonly IBrush ComparisonLowerBrush = new SolidColorBrush(Color.Parse("#26805B"));

    private static readonly string[] NatureNames =
    [
        "勤奋", "怕寂寞", "勇敢", "固执", "顽皮",
        "大胆", "坦率", "悠闲", "淘气", "乐天",
        "胆小", "急躁", "认真", "爽朗", "天真",
        "内敛", "慢吞吞", "冷静", "害羞", "马虎",
        "温和", "温顺", "自大", "慎重", "浮躁"
    ];

    private static readonly string[] NatureDisplays =
    [
        "勤奋(无修正)", "怕寂寞(+攻击，-防御)", "勇敢(+攻击，-速度)", "固执(+攻击，-特攻)", "顽皮(+攻击，-特防)",
        "大胆(+防御，-攻击)", "坦率(无修正)", "悠闲(+防御，-速度)", "淘气(+防御，-特攻)", "乐天(+防御，-特防)",
        "胆小(+速度，-攻击)", "急躁(+速度，-防御)", "认真(无修正)", "爽朗(+速度，-特攻)", "天真(+速度，-特防)",
        "内敛(+特攻，-攻击)", "慢吞吞(+特攻，-防御)", "冷静(+特攻，-速度)", "害羞(无修正)", "马虎(+特攻，-特防)",
        "温和(+特防，-攻击)", "温顺(+特防，-防御)", "自大(+特防，-速度)", "慎重(+特防，-特攻)", "浮躁(无修正)"
    ];
    private const int NatureCodeUsePid = 26;
    private const int SummaryAllStatsIncreaseNatureCode = 31;

    private const int MaxPokemonLevel = PokemonExperienceTable.MaxLevel;
    private const int DexImportLevel = 5;
    private sealed record PlayerTrainerIdentity(uint OtId, byte[] OtName);
    private sealed record DexEvolutionSegment(string Text, int? SpeciesId = null, bool IsCurrent = false);

    private readonly ObservableCollection<PartySlotRow> _partyRows = [];
    private readonly GameProfile _profile;
    private readonly IGameRuntimeAdapter _runtime;
    private readonly ObservableCollection<BagSlotRow> _bagRows = [];
    private readonly ObservableCollection<SaveBagEditableRow> _saveBagRows = [];
    private readonly ObservableCollection<BoxSlotRow> _boxRows = [];
    private readonly ObservableCollection<BoxGridGroup> _boxGridGroups = [];
    private readonly ObservableCollection<DexSpeciesRow> _dexRows = [];
    private readonly ObservableCollection<DexInfoRow> _dexInfoRows = [];
    private readonly ObservableCollection<DexStatRow> _dexStatRows = [];
    private readonly ObservableCollection<DexLevelMoveRow> _dexLevelMoveRows = [];
    private readonly ObservableCollection<DexLevelMoveRow> _dexOtherMoveRows = [];
    private readonly ObservableCollection<DexEncounterRow> _dexEncounterRows = [];
    private readonly ObservableCollection<MoveDexRow> _moveDexRows = [];
    private readonly ObservableCollection<DexTypeBadge> _partyHeaderTypeBadges = [];
    private readonly ObservableCollection<DexTypeBadge> _boxHeaderTypeBadges = [];
    private readonly ObservableCollection<DexTypeBadge> _dexHeaderTypeBadges = [];
    private DexSpeciesRow[] _allDexRows = [];
    private MoveDexRow[] _allMoveDexRows = [];
    private int _dexMaxLevel;
    private readonly List<BagSlotRow> _allBagRows = [];
    private readonly ChoiceRow[] _speciesChoices;
    private readonly ChoiceRow[] _itemChoices;
    private readonly ChoiceRow[] _moveChoices;
    private readonly ChoiceRow[] _natureChoices;
    private readonly ChoiceRow[] _genderChoices;
    private readonly ChoiceRow[] _statusChoices;
    private readonly ChoiceRow[] _ppBonusChoices;
    private ChoiceRow[] _bagPocketChoices;
    private readonly ChoiceRow[] _dexSortChoices;
    private readonly ChoiceRow[] _dexSortDirectionChoices;
    private ChoiceRow[] _dexImportBoxChoices = [];
    private readonly ChoiceRow[] _moveDexTypeFilterChoices;
    private readonly ChoiceRow[] _moveDexCategoryFilterChoices;
    private readonly MapDatabase _mapDatabase;
    private readonly ChoiceRow[] _mapGroupChoices;
    private IReadOnlyList<ChoiceRow> _bagItemChoices = [];
    private readonly ModifierDatabase _db;
    private readonly PokemonExperienceTable _experienceTable;
    private Dictionary<char, byte[]>? _gameTextEncodeMap;
    private Dictionary<ushort, string>? _gameTextDecodeMap;
    private uint? _partyBase;
    private uint? _boxBase;
    private uint? _bagBase;
    private DataSourceMode _dataSourceMode = DataSourceMode.Live;
    private string? _loadedSavePath;
    private Gen3SaveDocument? _loadedSave;
    private bool _boxGridShowAllBoxes;
    private int? _abilitySpecies;
    private int? _boxAbilitySpecies;
    private bool _suppressEditorEvents;
    private bool _suppressExperienceSync;
    private bool _suppressCheatEvents;
    private bool _suppressDexImportBoxSelection;
    private bool _dexImportBoxManuallySelected;
    private Point? _boxDragStartPoint;
    private int? _boxDragSourceSlot;
    private Button? _boxDragSourceButton;
    private Bitmap? _boxDragSourceSprite;
    private bool _boxDragInProgress;
    private bool _boxDropInProgress;
    private readonly Dictionary<SearchableChoiceBox, IReadOnlyList<ChoiceRow>> _searchableChoices = [];
    private readonly Dictionary<int, IReadOnlyList<SpeciesEvolution>> _evolutionCache = [];
    private readonly Dictionary<int, IReadOnlyList<SpeciesLevelMove>> _levelMoveCache = [];
    private readonly Dictionary<int, Bitmap?> _spriteCache = [];
    private Bitmap? _eggSpriteCache;
    private int ProfileMaxPokemonLevel => Math.Clamp(_profile.Limits.MaxLevel, 1, MaxPokemonLevel);
    private bool UsesVerifiedSpriteAssets => _profile.Graphics.SpritesVerified;
    private bool UsesFixedLiveBag => _runtime.UsesFixedLiveBag;
    private bool UsesScannedLiveBag => _runtime.UsesScannedLiveBag;
    private bool HasOfficialSpeciesComparison => _db.Table("official_species_stats").Count > 0;
    private bool HasOfficialMoveComparison => _db.Table("official_move_data").Count > 0;
    private int MachinePocket => _runtime.MachinePocket;
    private PokemonDataLayout ActivePokemonLayout => _runtime.PartyLayout;
    private PokemonDataLayout ActiveBoxLayout => _runtime.LiveBoxLayout;
    private int ActiveBoxRecordSize => _profile.Memory.PcBoxRecordSize;


    public MainWindow()
        : this(MissingSelectedProfile())
    {
    }

    public MainWindow(GameProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _runtime = GameRuntimeAdapterCatalog.ForProfile(profile);
        _dexMaxLevel = ProfileMaxPokemonLevel;
        InitializeComponent();
        ReorderMainTabs();
        ApplyWindowIcon();
        ApplyWindowTitle();
        SelectedVersionText.Text = $"当前版本：{profile.DisplayName}";
        _db = new ModifierDatabase(profile.DatabaseDirectory, typeof(MainWindow).Assembly, profile.DatabaseResourcePrefix);
        ValidateProfileDatabase();
        _experienceTable = new PokemonExperienceTable(_db);
        _mapDatabase = new MapDatabase(_db);
        _mapGroupChoices = _mapDatabase.Groups()
            .Select(g => new ChoiceRow(g.Key, MapGroupChoiceText(g.Key, g)))
            .ToArray();
        _speciesChoices = SpeciesChoiceRows();
        _itemChoices = [new ChoiceRow(0, "无"), .. ItemChoiceRows()];
        _bagItemChoices = _itemChoices;
        _moveChoices = [new ChoiceRow(0, "无"), .. ChoiceRows("moves")];
        _natureChoices = NatureDisplays.Select((name, id) => new ChoiceRow(id, name, name)).ToArray();
        _genderChoices =
        [
            new(PartyPokemon.GenderMale, GenderText(PartyPokemon.GenderMale)),
            new(PartyPokemon.GenderFemale, GenderText(PartyPokemon.GenderFemale)),
            new(PartyPokemon.Genderless, GenderText(PartyPokemon.Genderless))
        ];
        _statusChoices =
        [
            new(0x00, "无异常"),
            new(0x01, "睡眠"),
            new(0x08, "中毒"),
            new(0x10, "灼伤"),
            new(0x20, "冰冻"),
            new(0x40, "麻痹"),
            new(0x80, "剧毒")
        ];
        _ppBonusChoices =
        [
            new(0, "无"),
            new(1, "1次"),
            new(2, "2次"),
            new(3, "3次")
        ];
        _dexSortChoices =
        [
            new(0, "按编号"),
            new(1, "按总种族值"),
            new(2, "按HP"),
            new(3, "按攻击"),
            new(4, "按防御"),
            new(5, "按特攻"),
            new(6, "按特防"),
            new(7, "按速度")
        ];
        _dexSortDirectionChoices =
        [
            new(0, "升序"),
            new(1, "降序")
        ];
        _moveDexTypeFilterChoices =
        [
            new(-1, "全部属性"),
            .. _runtime.MoveTypeIds.Select(type => new ChoiceRow(type, MoveTypeNameZh(type)))
        ];
        _moveDexCategoryFilterChoices =
        [
            new(-1, "全部分类"),
            new(0, "物理"),
            new(1, "特殊"),
            new(2, "变化")
        ];
        _bagPocketChoices = BuildBagPocketChoices(profile.Features.LiveEditing);
        PartyList.ItemsSource = _partyRows;
        BagList.ItemsSource = _bagRows;
        SaveBagList.ItemsSource = _saveBagRows;
        BoxList.ItemsSource = _boxRows;
        BoxGridGroupsView.ItemsSource = _boxGridGroups;
        BoxGridGroupsView.AddHandler(
            InputElement.PointerPressedEvent,
            OnBoxGridSlotPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        BoxGridGroupsView.AddHandler(
            InputElement.PointerMovedEvent,
            OnBoxGridSlotPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        BoxGridGroupsView.AddHandler(
            InputElement.PointerReleasedEvent,
            OnBoxGridSlotPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DexSpeciesList.ItemsSource = _dexRows;
        MoveDexList.ItemsSource = _moveDexRows;
        DexInfoRowsView.ItemsSource = _dexInfoRows;
        DexStatRowsView.ItemsSource = _dexStatRows;
        DexLevelMoveRowsView.ItemsSource = _dexLevelMoveRows;
        DexOtherMoveRowsView.ItemsSource = _dexOtherMoveRows;
        DexEncounterRowsView.ItemsSource = _dexEncounterRows;
        PartyHeaderTypeBadgesView.ItemsSource = _partyHeaderTypeBadges;
        BoxHeaderTypeBadgesView.ItemsSource = _boxHeaderTypeBadges;
        DexHeaderTypeBadgesView.ItemsSource = _dexHeaderTypeBadges;
        DexSortBox.ItemsSource = _dexSortChoices;
        DexSortDirectionBox.ItemsSource = _dexSortDirectionChoices;
        MoveDexTypeFilterBox.ItemsSource = _moveDexTypeFilterChoices;
        MoveDexCategoryFilterBox.ItemsSource = _moveDexCategoryFilterChoices;
        SetChoice(DexSortBox, _dexSortChoices, 0);
        SetChoice(DexSortDirectionBox, _dexSortDirectionChoices, 0);
        SetChoice(MoveDexTypeFilterBox, _moveDexTypeFilterChoices, -1);
        SetChoice(MoveDexCategoryFilterBox, _moveDexCategoryFilterChoices, -1);
        ConfigureSearchableCombo(SpeciesBox, _speciesChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(ItemBox, _itemChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(Move1Box, _moveChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(Move2Box, _moveChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(Move3Box, _moveChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(Move4Box, _moveChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(NatureBox, _natureChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(BagItemBox, _bagItemChoices, UpdateBagNameText);
        ConfigureSearchableCombo(BoxSpeciesBox, _speciesChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxItemBox, _itemChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxMove1Box, _moveChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxMove2Box, _moveChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxMove3Box, _moveChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxMove4Box, _moveChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxNatureBox, _natureChoices, UpdateBoxNameText);
        GenderBox.ItemsSource = _genderChoices;
        BoxGenderBox.ItemsSource = _genderChoices;
        StatusBox.ItemsSource = _statusChoices;
        foreach (var box in PpBonusBoxes())
        {
            box.ItemsSource = _ppBonusChoices;
            box.SelectedIndex = 0;
        }
        FitComboToContent(StatusBox, _statusChoices);
        FitComboToContent(GenderBox, _genderChoices);
        FitComboToContent(BoxGenderBox, _genderChoices);
        BagPocketTabs.ItemsSource = _bagPocketChoices;
        BagPocketTabs.SelectedIndex = 0;
        UpdateBagBatchButtons();
        TeleportGroupBox.ItemsSource = _mapGroupChoices;
        TeleportGroupBox.SelectedIndex = _mapGroupChoices.Length > 0 ? 0 : -1;
        RefreshTeleportMaps();
        ConfigureNumericInputLimits();
        HookNameRefresh();
        InitializeDexRows();
        InitializeMoveDexRows();
        DexLevelMoveComparisonNote.IsVisible = HasOfficialMoveComparison;
        MoveDexComparisonNote.IsVisible = HasOfficialMoveComparison;
        SetDataSourceMode(profile.Features.LiveEditing ? DataSourceMode.Live : DataSourceMode.SaveFile);
        Log($"已选择游戏版本：{profile.DisplayName}（{profile.Id}）。");
        Log($"已载入列表：宝可梦 {_speciesChoices.Length} 项，道具 {_itemChoices.Length} 项，招式 {_moveChoices.Length} 项。");
        if (!UsesVerifiedSpriteAssets)
            Log("当前版本尚未验证宝可梦图片素材，已暂时隐藏旧版本图片，避免显示错误。");
        Log("界面已就绪。请先在 mGBA 加载 bridge 脚本，然后点击“连接 mGBA”。");
    }

    private static GameProfile MissingSelectedProfile()
        => throw new InvalidOperationException("必须先在启动窗口选择游戏版本。");

    private void ValidateProfileDatabase()
    {
        var requiredTables = new List<string> { "species", "moves", "items", "abilities", "species_stats", "move_data", "item_data", "experience" };
        if (_profile.RomTables.MachineCompatibility is not null)
            requiredTables.AddRange(["machine_moves", "species_machine_moves", "species_machine_compatibility"]);
        if (_profile.RomTables.TutorCompatibility is not null)
            requiredTables.AddRange(["tutor_moves", "species_tutor_moves", "species_tutor_compatibility"]);
        if (_profile.RomTables.EggMoves is not null)
            requiredTables.Add("species_egg_moves");
        if (_profile.RomTables.WildEncounters is not null)
            requiredTables.AddRange(["wild_encounters", "species_encounters"]);
        foreach (var table in requiredTables)
        {
            if (_db.Table(table).Count == 0)
                throw new InvalidOperationException($"版本 {_profile.DisplayName} 缺少数据表：db/{table}.tsv");
        }
    }

    private void ReorderMainTabs()
    {
        if (!MainTabs.Items.Contains(TrainerTab) || !MainTabs.Items.Contains(BoxTab)) return;
        MainTabs.Items.Remove(TrainerTab);
        var boxIndex = MainTabs.Items.IndexOf(BoxTab);
        MainTabs.Items.Insert(boxIndex + 1, TrainerTab);
    }

    private void SetDataSourceMode(DataSourceMode mode)
    {
        if (mode == DataSourceMode.Live && !_profile.Features.LiveEditing)
            throw new InvalidOperationException($"版本 {_profile.DisplayName} 暂不支持实时编辑。");
        if (mode == DataSourceMode.SaveFile && !_profile.Features.SaveEditing)
            throw new InvalidOperationException($"版本 {_profile.DisplayName} 暂不支持存档编辑。");

        _dataSourceMode = mode;
        var live = mode == DataSourceMode.Live;
        var partyRead = _runtime.CanRead(GameDataSurface.Party, live);
        var partyWrite = _runtime.CanWrite(GameDataSurface.Party, live);
        var boxRead = _runtime.CanRead(GameDataSurface.Boxes, live);
        var boxWrite = _runtime.CanWrite(GameDataSurface.Boxes, live);
        var bagRead = _runtime.CanRead(GameDataSurface.Bag, live);
        var bagWrite = _runtime.CanWrite(GameDataSurface.Bag, live);
        var trainerRead = _runtime.CanRead(GameDataSurface.Trainer, live);
        var trainerWrite = _runtime.CanWrite(GameDataSurface.Trainer, live);

        ModeSwitchButton.IsVisible = _profile.Features.LiveEditing && _profile.Features.SaveEditing;
        ModeSwitchButton.Content = live ? "切换到存档模式" : "切换到实时模式";
        ScanButton.IsVisible = live && _profile.Features.LiveEditing;
        LoadSaveButton.IsVisible = !live && _profile.Features.SaveEditing;
        ReadPartyButton.IsVisible = live && partyRead;
        PartyTab.IsVisible = partyRead;
        BagTab.IsVisible = bagRead;
        BoxTab.IsVisible = boxRead;
        BoxReadButton.IsVisible = live && boxRead;
        DexTab.IsVisible = _profile.Features.BuiltInDex;
        MoveDexTab.IsVisible = _profile.Features.MoveDex;
        var experimentsVisible = live && _profile.Features.Experiments;
        ExperimentTab.IsVisible = experimentsVisible;
        TeleportExperimentPanel.IsVisible = experimentsVisible && _profile.Runtime.ExperimentTeleport;
        BattleExperimentPanel.IsVisible = experimentsVisible &&
                                          (_profile.Runtime.ExperimentBattleAssist ||
                                           _profile.Runtime.ExperimentNoEncounter ||
                                           _profile.Runtime.ExperimentWalkThroughWalls);
        CheatInfinitePpBox.IsVisible = _profile.Runtime.ExperimentBattleAssist;
        CheatLockHpBox.IsVisible = _profile.Runtime.ExperimentBattleAssist;
        CheatClearStatusBox.IsVisible = _profile.Runtime.ExperimentBattleAssist;
        CheatAlwaysCritBox.IsVisible = _profile.Runtime.ExperimentBattleAssist;
        CheatNoEncounterBox.IsVisible = _profile.Runtime.ExperimentNoEncounter;
        CheatWalkThroughWallsBox.IsVisible = _profile.Runtime.ExperimentWalkThroughWalls;
        TrainerTab.IsVisible = trainerRead &&
                               (live || _loadedSave?.Snapshot.Trainer is not null);
        ExportSaveButton.IsVisible = !live && _profile.Features.SaveEditing;
        SaveAsButton.IsVisible = !live && _profile.Features.SaveEditing;
        UpdateSaveButtons();

        PartyDeleteButton.IsVisible = partyWrite && (live || _loadedSave?.CanWrite == true);
        PartyDeleteButton.Content = live ? "删除队伍精灵" : "从待保存队伍删除";
        PartyApplyButton.IsVisible = partyWrite && (live || _loadedSave?.CanWrite == true);
        PartyApplyButton.Content = live ? "写入当前精灵" : "应用到待保存存档";
        BoxDeleteButton.IsVisible = boxWrite && (live || _loadedSave?.CanWrite == true);
        BoxDeleteButton.Content = live ? "删除箱子精灵" : "从待保存箱子删除";
        BoxApplyButton.IsVisible = boxWrite && (live || _loadedSave?.CanWrite == true);
        BoxApplyButton.Content = live ? "写入当前箱子精灵" : "应用到待保存存档";
        PartyOtSyncButton.IsVisible = live && partyWrite;
        BoxOtSyncButton.IsVisible = live && boxWrite;
        TrainerReadButton.IsVisible = live && trainerRead;
        TrainerApplyNameButton.IsVisible = trainerWrite && (live || _loadedSave?.CanWrite == true);
        TrainerApplyNameButton.Content = live ? "写入名字" : "应用名字到待保存存档";
        TrainerMoneyReadButton.IsVisible = live && trainerRead;
        TrainerMoneyApplyButton.IsVisible = trainerWrite && (live || _loadedSave?.CanWrite == true);
        TrainerMoneyApplyButton.Content = live ? "写入金钱" : "应用金钱到待保存存档";
        BagReadButton.IsVisible = live && bagRead;
        BagSnapshotButton.IsVisible = live && bagRead && UsesScannedLiveBag;
        BagAddButton.IsVisible = bagWrite && (live || _loadedSave?.CanWrite == true);
        BagApplyButton.IsVisible = bagWrite && (live || _loadedSave?.CanWrite == true);
        BagApplyButton.Content = live ? "写入当前槽" : "应用到当前槽";
        BagList.IsVisible = live;
        SaveBagList.IsVisible = !live;
        BagEditorTitleText.Text = live ? "背包槽编辑" : "添加道具与详情";
        BagEditorDescriptionText.Text = live
            ? "选中左侧口袋槽位后编辑，再明确写入 mGBA 内存。"
            : "左侧可直接修改现有道具；选择完成或数量输入失焦/回车后自动更新待保存副本。右侧用于添加道具和查看详情。";
        BagAddButton.Content = live ? "添加到当前背包" : "添加到当前分类";
        DexImportPartyButton.IsVisible = live
            ? _profile.Features.ImportToParty && partyWrite
            : _profile.Features.ImportToSaveParty && partyWrite && _loadedSave?.CanWrite == true;
        var dexImportBoxVisible = live
            ? _profile.Features.ImportToBoxes && boxWrite
            : _profile.Features.ImportToSaveBoxes && boxWrite && _loadedSave?.CanWrite == true;
        DexImportBoxPanel.IsVisible = dexImportBoxVisible;
        DexImportBoxButton.IsVisible = dexImportBoxVisible;
        DexImportPartyButton.IsEnabled = DexImportPartyButton.IsVisible;
        DexImportBoxButton.IsEnabled = dexImportBoxVisible;
        DexImportBoxNumberBox.IsEnabled = dexImportBoxVisible;
        ConfigureDexImportBoxChoices(live);
        _bagPocketChoices = BuildBagPocketChoices(live);
        BagPocketTabs.ItemsSource = _bagPocketChoices;
        BagPocketTabs.SelectedIndex = 0;
        BagModeHintText.Text = live
            ? "请先对比下方道具列表是否和游戏中一致；如不一致，请先校准背包。"
            : _loadedSave is null
                ? "请先点击顶部“选择存档”读取 .sav 或 .srm 文件。"
                : _loadedSave.CanWrite
                ? "背包修改只保存在待保存副本中；重要物品可能影响流程，请谨慎修改。"
                : $"当前存档背包只读：{_loadedSave.WriteBlockReason}";
        var boxDragEnabled = boxWrite && (live || _loadedSave?.CanWrite == true);
        BoxModeHintText.Text = !live && _loadedSave is null
            ? "请先点击顶部“选择存档”读取 .sav 或 .srm 文件。"
            : !boxDragEnabled
                ? "当前 Profile 或当前存档只开放箱子读取，不能拖动或写入槽位。"
                : live
                ? "可拖动宝可梦调整槽位，也可右键删除或移动到指定箱子；拖到已有宝可梦会立即交换。实时写入前建议先保存 mGBA 即时存档。"
                : "可拖动宝可梦调整槽位，也可右键删除或移动到指定箱子；拖到已有宝可梦会立即交换。所有变化先进入待保存副本。";

        if (!live && MainTabs.SelectedItem == ExperimentTab)
            MainTabs.SelectedItem = PartyTab;
    }

    private ChoiceRow[] BuildBagPocketChoices(bool live)
        =>
        [
            .. _runtime.BagPockets(live).Select(pocket => new ChoiceRow(pocket.Id, pocket.Name)),
            new(-1, "未知/候选"),
            new(0, "全部口袋")
        ];

    private void UpdateSaveButtons()
    {
        var enabled = _dataSourceMode == DataSourceMode.SaveFile &&
                      _profile.Features.SaveEditing &&
                      _loadedSave is { CanWrite: true, HasChanges: true };
        ExportSaveButton.IsEnabled = enabled;
        SaveAsButton.IsEnabled = enabled;
    }

    private void ApplyWindowTitle()
    {
        var version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var suffix = string.IsNullOrWhiteSpace(version) ? string.Empty : $" v{version.Split('+')[0]}";
        Title = $"{AppTitleBase} - {_profile.DisplayName}{suffix}";
        AppTitleText.Text = AppNameBase + suffix;
    }

    private void ApplyWindowIcon()
    {
        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://RocketTool.Avalonia/Assets/AppIcon.ico")));
        }
        catch
        {
            // The executable icon is still set by the project file; ignore resource load failures.
        }
    }

    private void ConfigureNumericInputLimits()
    {
        foreach (var box in new[]
                 {
                     FriendshipBox, LevelBox,
                     Pp1Box, Pp2Box, Pp3Box, Pp4Box,
                     EvHpBox, EvAtkBox, EvDefBox, EvSpeBox, EvSpaBox, EvSpdBox,
                     EggHatchStepsBox,
                     BoxFriendshipBox,
                     BoxEggHatchStepsBox,
                     BoxPp1Box, BoxPp2Box, BoxPp3Box, BoxPp4Box,
                     BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpeBox, BoxEvSpaBox, BoxEvSpdBox,
                     BagQuantityBox,
                     TeleportXBox, TeleportYBox
                 })
            box.MaxLength = 3; // current supported bag quantity limits are all 3-digit values.

        foreach (var box in new[] { IvHpBox, IvAtkBox, IvDefBox, IvSpeBox, IvSpaBox, IvSpdBox, BoxIvHpBox, BoxIvAtkBox, BoxIvDefBox, BoxIvSpeBox, BoxIvSpaBox, BoxIvSpdBox })
            box.MaxLength = 2; // IVs are 0..31.

        MaxHpBox.MaxLength = 5; // u16.
        ExpBox.MaxLength = 10; // u32.
        BoxExpBox.MaxLength = 10; // u32.
    }

    private async void OnScanClicked(object? sender, RoutedEventArgs e)
    {
        SetDataSourceMode(DataSourceMode.Live);
        _loadedSavePath = null;
        _loadedSave = null;
        await RunUiTask("连接 mGBA", () =>
        {
            using var bridge = ConnectBridge();
            var gameCode = bridge.GameCode();
            ConnectionStatusText.Text = $"mGBA连接成功：{gameCode}";
            LastWriteText.Text = "当前来源：mGBA 实时内存；可以读取队伍、背包和箱子。";
            Log($"已连接 mGBA：游戏={gameCode}。队伍数据尚未读取。连接会在本次操作结束后自动关闭，这是正常现象。");
        });
    }

    private async void OnModeSwitchClicked(object? sender, RoutedEventArgs e)
    {
        if (_dataSourceMode == DataSourceMode.SaveFile &&
            _loadedSave is { HasChanges: true } &&
            !await ConfirmDiscardSaveChangesAsync())
            return;

        ClearDataSourceRows();
        _loadedSavePath = null;
        _loadedSave = null;
        _partyBase = null;
        _boxBase = null;
        _bagBase = null;
        BaseBox.Text = string.Empty;

        if (_dataSourceMode == DataSourceMode.Live)
        {
            SetDataSourceMode(DataSourceMode.SaveFile);
            ConnectionStatusText.Text = "当前模式：存档文件；尚未选择存档";
            LastWriteText.Text = "请选择 .sav 或 .srm 文件；修改会先进入待保存副本。";
        }
        else
        {
            SetDataSourceMode(DataSourceMode.Live);
            ConnectionStatusText.Text = "当前模式：实时 mGBA；尚未连接 bridge";
            LastWriteText.Text = "请先连接 mGBA，再读取需要编辑的数据。";
        }
    }

    private async void OnSwitchVersionClicked(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmSwitchVersionAsync()) return;

        ClearDataSourceRows();
        _loadedSavePath = null;
        _loadedSave = null;
        _partyBase = null;
        _boxBase = null;
        _bagBase = null;
        BaseBox.Text = string.Empty;
        ConnectionStatusText.Text = "正在返回版本选择…";
        LastWriteText.Text = "当前版本的数据已清空。";
        LogBox.Text = string.Empty;
        VersionSwitchRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> ConfirmSwitchVersionAsync()
    {
        var hasPendingSave = _loadedSave is { HasChanges: true };
        var dialog = new Window
        {
            Title = "切换游戏版本",
            Width = 570,
            Height = hasPendingSave ? 280 : 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = hasPendingSave
                ? "当前存档还有尚未保存的修改。切换版本后，这些修改将无法恢复。"
                : $"即将离开“{_profile.DisplayName}”并返回版本选择。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = hasPendingSave
                ? new SolidColorBrush(Color.FromRgb(150, 43, 43))
                : new SolidColorBrush(Color.FromRgb(16, 46, 74)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "程序会关闭当前编辑界面，清空已读取的队伍、背包、箱子、连接状态和日志，再按新版本重新加载配置与数据表。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "继续使用当前版本" };
        var confirm = new Button { Content = "清空并切换版本", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private async Task<bool> ConfirmDiscardSaveChangesAsync()
    {
        var dialog = new Window
        {
            Title = "尚未保存存档",
            Width = 520,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = "当前存档中还有尚未保存的修改。切换到实时模式会丢弃这些修改。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 43, 43)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "建议先取消并使用顶部“保存修改并备份”。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var discard = new Button { Content = "丢弃并切换" };
        buttons.Children.Add(cancel);
        buttons.Children.Add(discard);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        discard.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private void ClearDataSourceRows()
    {
        ResetBoxDragState();
        _partyRows.Clear();
        _bagRows.Clear();
        _allBagRows.Clear();
        _saveBagRows.Clear();
        _boxRows.Clear();
        _boxGridGroups.Clear();
        ClearEditor();
        BoxSelectedTitleText.Text = "箱子槽编辑";
        BoxNameText.Text = "选择箱子槽后显示。";
        BoxAbilityDescriptionText.Text = "选择特性后显示说明。";
        SetPokemonSprite(BoxDetailSpriteImage, 0);
        UpdateBoxHeaderInfo(0, null);
        UpdateBoxEggHatchEditor(null);
        ClearBoxStatTexts();
    }

    private async void OnLoadSaveClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "读取 GBA 存档",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("GBA 存档") { Patterns = ["*.sav", "*.srm"] },
                FilePickerFileTypes.All
            ]
        });
        var file = files.FirstOrDefault();
        if (file is null) return;
        var path = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowToast("无法读取该存档路径。", success: false);
            return;
        }

        await RunUiTask("读取存档", () => LoadSaveFile(path));
    }

    private async void OnSaveInPlaceClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPendingSaveDocument(out var document)) return;
        if (!await ConfirmSaveInPlaceAsync(document)) return;

        var sourcePath = document.SourcePath;
        var partyCount = document.ModifiedPartyCount;
        var bagCount = document.ModifiedBagCount;
        var boxCount = document.ModifiedBoxCount;
        await RunUiTask("保存存档", () =>
        {
            var result = document.SaveInPlaceWithBackup();
            LoadSaveFile(sourcePath);
            var backupText = result.BackupCreated
                ? $"已创建原始备份 {Path.GetFileName(result.BackupPath)}"
                : $"已有原始备份 {Path.GetFileName(result.BackupPath)}，已保持不变";
            ConnectionStatusText.Text = $"存档保存成功：{Path.GetFileName(sourcePath)}";
            SetWriteNotice(
                $"已安全保存并重新校验：{Path.GetFileName(sourcePath)}；{backupText}；队伍修改 {partyCount} 只，背包修改 {bagCount} 格，箱子修改 {boxCount} 只，section {result.Snapshot.ValidSectionCount}/14。",
                success: true);
        });
    }

    private async void OnExportSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryGetPendingSaveDocument(out var document)) return;

        var extension = Path.GetExtension(document.SourcePath);
        if (!string.Equals(extension, ".srm", StringComparison.OrdinalIgnoreCase))
            extension = ".sav";
        var sourceName = Path.GetFileNameWithoutExtension(document.SourcePath);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "将修改后的 GBA 存档另存为",
            SuggestedFileName = $"{sourceName}{extension}",
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType("GBA 存档") { Patterns = ["*.sav", "*.srm"] },
                FilePickerFileTypes.All
            ]
        });
        if (file is null) return;
        var outputPath = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            ShowToast("无法读取另存路径。", success: false);
            return;
        }

        var partyCount = document.ModifiedPartyCount;
        var bagCount = document.ModifiedBagCount;
        var boxCount = document.ModifiedBoxCount;
        await RunUiTask("另存存档", () =>
        {
            var verified = document.SaveAs(outputPath);
            LoadSaveFile(outputPath);
            ConnectionStatusText.Text = $"存档另存成功：{Path.GetFileName(outputPath)}";
            SetWriteNotice(
                $"已另存并重新校验：{Path.GetFileName(outputPath)}；队伍修改 {partyCount} 只，背包修改 {bagCount} 格，箱子修改 {boxCount} 只，section {verified.ValidSectionCount}/14。原存档未改动。",
                success: true);
        });
    }

    private bool TryGetPendingSaveDocument(out Gen3SaveDocument document)
    {
        if (_loadedSave is not { } loaded)
        {
            document = null!;
            ShowToast("请先读取一个存档。", success: false);
            return false;
        }
        if (!loaded.CanWrite)
        {
            document = null!;
            ShowToast(loaded.WriteBlockReason ?? "当前存档不能安全写回。", success: false);
            return false;
        }
        if (!loaded.HasChanges)
        {
            document = null!;
            ShowToast("当前存档没有待保存的修改。", success: false);
            return false;
        }

        document = loaded;
        return true;
    }

    private async Task<bool> ConfirmSaveInPlaceAsync(Gen3SaveDocument document)
    {
        var sourceName = Path.GetFileName(document.SourcePath);
        var backupPath = document.SourcePath + ".bak";
        var backupName = Path.GetFileName(backupPath);
        var backupExists = File.Exists(backupPath);
        var dialog = new Window
        {
            Title = "保存修改并保留原始备份",
            Width = 590,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = $"修改后的内容将安全保存回原文件：\n{sourceName}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(16, 46, 74)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = backupExists
                ? $"检测到原始备份 {backupName}，程序会保留它，不会覆盖。"
                : $"保存前会先创建原始备份 {backupName}；今后再次保存也不会覆盖它。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });
        root.Children.Add(new TextBlock
        {
            Text = "请确认 mGBA 当前没有正在写入这个存档文件。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 43, 43))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var save = new Button { Content = "确认保存并备份", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        return confirmed;
    }

    private async void OnReloadPartyClicked(object? sender, RoutedEventArgs e) => await ReloadPartyAsync();

    private async Task RefreshPartyAndBoxAfterWriteAsync(int selectSlot)
    {
        await ReloadPartyAsync(selectSlot);
        if (_boxBase is not null)
            await ReloadBoxIfLoadedAsync();
    }

    private async Task ReloadBoxIfLoadedAsync()
    {
        if (_boxBase is null) return;
        await RunUiTask("刷新箱子", () =>
        {
            using var bridge = ConnectBridge();
            LoadBoxRows(bridge);
        });
    }

    private void ConfigureSearchableCombo(SearchableChoiceBox box, IReadOnlyList<ChoiceRow> choices, Action changed)
    {
        _searchableChoices[box] = choices;
        box.ItemsSource = choices;
        FitComboToContent(box, choices);
        box.TextChanged += (_, _) =>
        {
            if (!_suppressEditorEvents) changed();
        };
        box.SelectionChanged += (_, _) =>
        {
            if (!_suppressEditorEvents) changed();
        };
    }

    private void ResetSearchableComboItems(SearchableChoiceBox box, IEnumerable<ChoiceRow> choices, bool preserveSelection = false, bool preserveText = false)
    {
        var selectedId = preserveSelection && box.SelectedItem is ChoiceRow selected ? selected.Id : (int?)null;
        var text = preserveText ? box.Text : null;
        var rows = choices as IReadOnlyList<ChoiceRow> ?? choices.ToArray();
        box.ItemsSource = rows;
        if (selectedId is not null)
            box.SelectedItem = rows.FirstOrDefault(c => c.Id == selectedId.Value);
        else if (!preserveSelection)
            box.SelectedItem = null;
        if (preserveText)
            box.Text = text;
    }

    private static void FitComboToContent(SearchableChoiceBox box, IEnumerable<ChoiceRow> choices)
    {
        // Search boxes should follow the grid column width; long names stay visible in the popup.
        box.MinWidth = 0;
        box.MaxDropDownHeight = 360;
    }

    private static void FitComboToContent(ComboBox box, IEnumerable<ChoiceRow> choices)
    {
        var maxLen = choices.Select(c => c.ToString().Length).DefaultIfEmpty(0).Max();
        if (maxLen <= 0) return;
        FitComboToText(box, new string('国', maxLen));
    }

    private static void FitComboToText(ComboBox box, string text)
    {
        box.MinWidth = Math.Clamp(text.Length * 18 + 80, 160, 520);
        box.MaxDropDownHeight = 360;
    }

    private static bool MatchesChoice(ChoiceRow choice, string term)
        => choice.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
           || (choice.Display?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
           || choice.Id.ToString().Contains(term, StringComparison.OrdinalIgnoreCase);

    private static ChoiceRow? FindExactChoice(IEnumerable<ChoiceRow> choices, string? text)
    {
        var term = text?.Trim();
        if (string.IsNullOrWhiteSpace(term)) return null;
        return choices.FirstOrDefault(c => string.Equals(c.Name, term, StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(c.Display, term, StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(c.ToString(), term, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ReloadPartyAsync(int selectSlot = 1)
    {
        await RunUiTask("刷新队伍", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            LoadPartyRows(bridge, baseAddr, selectSlot);
            ConnectionStatusText.Text = $"mGBA连接成功：{bridge.GameCode()}；队伍已刷新";
            Log($"已从 0x{baseAddr:X8} 刷新队伍，当前队伍数量={_partyRows.Count}。");
        });
    }

    private void LoadPartyRows(MgbaBridgeClient bridge, uint baseAddr, int selectSlot)
    {
        _runtime.EnsureCanRead(GameDataSurface.Party, live: true);
        var rows = new List<PartySlotRow>();
        var partyCount = ReadLivePartyCount(bridge, baseAddr) ?? Gen3Constants.PartySlots;
        for (var slot = 1; slot <= partyCount; slot++)
        {
            var addr = SlotAddress(baseAddr, slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize), ActivePokemonLayout);
            rows.Add(ToRow(slot, addr, mon));
        }
        _partyRows.Clear();
        foreach (var row in rows) _partyRows.Add(row);
        PartyList.SelectedIndex = _partyRows.Count == 0 ? -1 : Math.Clamp(selectSlot - 1, 0, _partyRows.Count - 1);
    }

    private void LoadSaveFile(string path)
    {
        _loadedSave = Gen3SaveReader.Open(path, _profile);
        var save = _loadedSave.Snapshot;
        _loadedSavePath = path;
        SetDataSourceMode(DataSourceMode.SaveFile);
        _partyBase = null;
        _boxBase = null;
        _bagBase = null;
        BaseBox.Text = string.Empty;

        _partyRows.Clear();
        for (var i = 0; i < save.Party.Count; i++)
            _partyRows.Add(ToRow(i + 1, (uint)_loadedSave.PartySaveOffset(i + 1), save.Party[i]));
        PartyList.SelectedIndex = _partyRows.Count > 0 ? 0 : -1;
        if (_partyRows.Count == 0)
        {
            SelectedTitleText.Text = "存档中没有识别到队伍";
            ClearEditor();
        }

        LoadSaveBagRows(save.Bag);
        if (save.Trainer is not null)
            FillSaveTrainerInfo();

        _boxRows.Clear();
        foreach (var entry in save.Boxes)
        {
            var info = entry.Mon.GetInfo();
            var title = BoxDisplayTitle(entry.BoxNumber, entry.SlotInBox, entry.Mon, info);
            if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
            _boxRows.Add(new BoxSlotRow(entry.GlobalSlot, (uint)entry.SaveOffset, entry.Mon, info, title, "存档待保存副本", BoxDisplaySprite(info)));
        }

        _boxGridShowAllBoxes = true;
        RefreshBoxGridGroups();
        BoxList.SelectedIndex = _boxRows.Count > 0 ? 0 : -1;
        if (_boxRows.Count == 0)
        {
            BoxNameText.Text = "存档中没有识别到箱子宝可梦。";
            SetPokemonSprite(BoxDetailSpriteImage, 0);
            UpdateBoxHeaderInfo(0, null);
            UpdateBoxEggHatchEditor(null);
        }

        ConnectionStatusText.Text = $"mGBA已断开；已读取存档：{save.FileName}";
        LastWriteText.Text = _loadedSave.CanWrite
            ? "当前来源：存档编辑副本；队伍、背包和箱子可修改，原文件不会被覆盖。"
            : $"当前来源：存档只读预览；{_loadedSave.WriteBlockReason}";
        Log($"已读取存档：{save.FileName}，大小 {save.FileSize} 字节，使用副本 {save.SaveSlot}，save index {save.SaveIndex}，有效 section {save.ValidSectionCount}/14。");
        Log($"存档内容：队伍 {save.Party.Count} 只，背包可显示 {save.Bag.Count} 格，箱子非空 {save.Boxes.Count} 格。支持 .sav/.srm 原始电池存档。");
        foreach (var warning in save.Warnings)
            Log("存档提示：" + warning);
        if (!_loadedSave.CanWrite)
            Log("存档写回已禁用：" + _loadedSave.WriteBlockReason);
    }

    private int? ReadLivePartyCount(MgbaBridgeClient bridge, uint baseAddr)
    {
        var countAddress = (long)baseAddr + _profile.Memory.PartyCountOffsetFromPartyBase;
        if (countAddress < _profile.Memory.EwramBase) return null;
        var count = bridge.Read((uint)countAddress, 1)[0];
        return count is >= 0 and <= Gen3Constants.PartySlots ? count : null;
    }

    private void WritePartyCount(MgbaBridgeClient bridge, uint baseAddr, int count)
    {
        _runtime.EnsureCanWrite(GameDataSurface.Party, live: true);
        if (count is < 0 or > Gen3Constants.PartySlots)
            throw new InvalidOperationException($"队伍数量必须在 0..{Gen3Constants.PartySlots}。");
        if (_partyBase is not uint scannedPartyBase || baseAddr != scannedPartyBase)
            throw new InvalidOperationException("队伍基址不是当前扫描确认的地址，已拒绝写入数量。");
        var countAddress = (long)baseAddr + _profile.Memory.PartyCountOffsetFromPartyBase;
        if (countAddress < _profile.Memory.EwramBase ||
            countAddress >= (long)_profile.Memory.EwramBase + _profile.Memory.EwramSize)
            throw new InvalidOperationException("队伍数量地址无效。");
        bridge.Command($"WRITE8 0x{countAddress:X} 0x{count:X}");
        var actual = bridge.Read((uint)countAddress, 1)[0];
        if (actual != count)
            throw new InvalidOperationException("队伍数量写入校验失败。");
    }

    private void OnPartySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PartyList.SelectedItem is not PartySlotRow row) return;
        FillEditor(row);
    }

    private async void OnTrainerReadClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("读取训练家", () =>
        {
            if (_dataSourceMode == DataSourceMode.SaveFile)
            {
                FillSaveTrainerInfo();
                return;
            }
            _runtime.EnsureCanRead(GameDataSurface.Trainer, live: true);
            using var bridge = ConnectBridge();
            var info = ReadTrainerInfo(bridge);
            FillTrainerInfo(info);
            try
            {
                FillTrainerMoney(bridge);
            }
            catch (Exception ex)
            {
                TrainerMoneyStatusText.Text = $"金钱读取失败：{ex.Message}";
                Log($"训练家金钱读取失败：{ex.Message}");
            }
            Log($"已读取训练家：{info.Name}，Trainer ID={info.TrainerId}，Secret ID={info.SecretId}。");
        });
    }

    private async void OnTrainerApplyNameClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("写入训练家名字", () =>
        {
            var newName = TrainerNameBox.Text?.Trim() ?? string.Empty;
            var encoded = EncodeGameTextBuffer(newName, _profile.Memory.PlayerNameLength, requireTerminator: true);
            if (_dataSourceMode == DataSourceMode.SaveFile)
            {
                var document = _loadedSave ?? throw new InvalidOperationException("请先读取一个存档。");
                var before = document.Snapshot.Trainer ?? throw new InvalidOperationException("当前存档没有已验证的训练家字段。");
                document.ReplaceTrainerName(encoded);
                var info = ToTrainerInfo(encoded, before.OtId, "存档待保存副本 / SaveBlock2");
                FillTrainerInfo(info);
                TrainerStatusText.Text = "名字已更新到待保存副本；点击顶部保存后才会写入文件。";
                UpdateSaveButtons();
                SetWriteNotice($"训练家名字已更新为 {info.Name}，等待保存。现有宝可梦的初训家名字不会自动批量改写。");
                return;
            }

            using var bridge = ConnectBridge();
            var beforeLive = ReadTrainerInfo(bridge);
            _runtime.EnsureCanWrite(GameDataSurface.Trainer, live: true);
            bridge.WriteRangeVerified(beforeLive.SaveBlock2Address, encoded);
            var after = ReadTrainerInfo(bridge);
            FillTrainerInfo(after);
            SetWriteNotice($"训练家名字已写入：{beforeLive.Name} -> {after.Name}。现有宝可梦的初训家名字不会自动批量改写。");
            Log($"训练家名字写入完成：{beforeLive.Name} -> {after.Name}。");
        });
    }

    private async void OnPartyOtNameSyncClicked(object? sender, RoutedEventArgs e)
        => await SyncCurrentTrainerNameToOtBox("同步队伍初训家", PartyOtNameBox);

    private async void OnBoxOtNameSyncClicked(object? sender, RoutedEventArgs e)
        => await SyncCurrentTrainerNameToOtBox("同步箱子初训家", BoxOtNameBox);

    private async Task SyncCurrentTrainerNameToOtBox(string label, TextBox target)
    {
        await RunUiTask(label, () =>
        {
            using var bridge = ConnectBridge();
            var trainer = ReadTrainerInfo(bridge);
            target.Text = trainer.Name;
            Log($"{label}：已从当前训练家信息同步名字 {trainer.Name}。");
        });
    }

    private async void OnTrainerMoneyReadClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("读取金钱", () =>
        {
            if (_dataSourceMode == DataSourceMode.SaveFile)
            {
                FillSaveTrainerInfo();
                return;
            }
            _runtime.EnsureCanRead(GameDataSurface.Trainer, live: true);
            using var bridge = ConnectBridge();
            FillTrainerMoney(bridge);
        });
    }

    private async void OnTrainerMoneyApplyClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("写入金钱", () =>
        {
            var money = ParseIntRequired(TrainerMoneyCurrentBox.Text, "当前金钱");
            if (money is < 0 || money > _profile.Runtime.MaxTrainerMoney)
                throw new InvalidOperationException($"当前金钱必须在 0..{_profile.Runtime.MaxTrainerMoney}。");

            if (_dataSourceMode == DataSourceMode.SaveFile)
            {
                var document = _loadedSave ?? throw new InvalidOperationException("请先读取一个存档。");
                document.ReplaceTrainerMoney((uint)money);
                TrainerMoneyCurrentBox.Text = money.ToString();
                TrainerMoneyStatusText.Text = $"金钱已改为 {money}，等待保存。";
                UpdateSaveButtons();
                SetWriteNotice($"金钱已更新为 {money}，等待保存。", success: true);
                return;
            }

            using var bridge = ConnectBridge();
            WriteTrainerMoney(bridge, money);
            var actual = ReadTrainerMoney(bridge);
            TrainerMoneyCurrentBox.Text = actual.ToString();
            TrainerMoneyStatusText.Text = $"已写入金钱：{actual}。";
            SetWriteNotice($"金钱已写入：{actual}。请回游戏确认后手动保存。");
            Log($"金钱写入完成：{actual}。");
        });
    }

    private static bool HasSummaryAllStatsIncreaseNatureCode(byte gameNatureCode)
        => gameNatureCode == SummaryAllStatsIncreaseNatureCode;

    private static string NatureText(int pidNature, byte gameNatureCode)
    {
        var pidText = NatureDisplays[pidNature];
        if (gameNatureCode == NatureCodeUsePid)
            return pidText;
        if (gameNatureCode <= 24)
            return gameNatureCode == pidNature
                ? NatureDisplays[gameNatureCode]
                : $"游戏性格：{NatureDisplays[gameNatureCode]}；PID性格：{NatureNames[pidNature]}";
        return HasSummaryAllStatsIncreaseNatureCode(gameNatureCode)
            ? $"性格代码异常；PID性格：{NatureNames[pidNature]}"
            : $"性格代码异常({gameNatureCode})；PID性格：{NatureNames[pidNature]}";
    }

    private async void OnHealClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        await RunUiTask("恢复选中宝可梦", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var (addr, mon) = ReadSelectedLiveMon(bridge, baseAddr, row);
            var info = mon.GetInfo();
            mon.SetUnencrypted(info.MaxHp, null, 0, null);
            WriteMon(bridge, addr, mon);
            SetWriteNotice($"队伍槽位 {row.Slot} 恢复成功：HP {info.Hp}->{info.MaxHp}，状态已清除。");
        });
        await RefreshPartyAndBoxAfterWriteAsync(row.Slot);
    }

    private async void OnApplyBasicClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        await RunUiTask("写入基础信息", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var (addr, mon) = ReadSelectedLiveMon(bridge, baseAddr, row);
            var before = mon.GetInfo();
            var originalOtName = PokemonOtName(mon.Raw);
            var species = ParseSpeciesOrNull(SpeciesBox);
            var experience = ResolvePartyExperience(before, species);
            SyncNicknameForSpeciesChange(mon, species);
            ApplyEditedOtName(mon, PartyOtNameBox.Text, originalOtName);
            var nature = SelectedChoiceId(NatureBox);
            if (nature is not null) mon.SetNature(nature.Value);
            mon.SetShiny(ShinyBox.IsChecked == true);
            ApplyGenderChoice(mon, species ?? before.Species);
            var abilitySlot = SelectedAbilitySlot();
            if (abilitySlot is not null)
                mon.SetAbilitySlot(abilitySlot.Value, ReadSpeciesStats(species ?? before.Species).GenderRatio);
            mon.SetGrowth(species, ParseItemOrNull(ItemBox), experience.Exp, ParsePartyFriendshipOrHatchCounter(before), null);
            var level = experience.Level;
            var maxHp = ParseUShortOrNull(MaxHpBox.Text);
            mon.SetUnencrypted(null, null, SelectedStatus(), experience.Level);
            var shouldRecalculateStats =
                species is not null && species.Value != before.Species ||
                nature is not null && (nature.Value != before.Nature || before.GameNatureCode != NatureCodeUsePid) ||
                level != before.Level;
            if (shouldRecalculateStats) RecalculateLiveStats(mon);
            if (!shouldRecalculateStats || maxHp is not null && maxHp.Value != before.MaxHp)
                mon.SetUnencrypted(maxHp: maxHp);
            WriteMon(bridge, addr, mon);
            SetWriteNotice($"队伍槽位 {row.Slot} 基础信息写入成功：种类/道具/经验/亲密度/等级/性格/特性/闪光/状态已更新。");
        });
        await RefreshPartyAndBoxAfterWriteAsync(row.Slot);
    }

    private async void OnApplyPokemonClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        if (!TryReadCurrentEvTotal("写入当前精灵", out var pendingEvTotal)) return;
        if (!await ConfirmHighEvTotalAsync(pendingEvTotal)) return;

        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            await RunUiTask("更新存档队伍", () =>
            {
                var document = _loadedSave ?? throw new InvalidOperationException("当前没有已读取的存档。");
                var mon = new PartyPokemon(row.Mon.Raw, ActivePokemonLayout);
                var evTotal = ApplyPartyEditor(mon);
                document.ReplacePartyPokemon(row.Slot, mon);
                var updated = ToRow(row.Slot, row.Address, mon);
                var index = _partyRows.IndexOf(row);
                if (index >= 0) _partyRows[index] = updated;
                PartyList.SelectedItem = updated;
                UpdateSaveButtons();
                SetWriteNotice(
                    evTotal > 510
                        ? $"存档队伍槽位 {row.Slot} 已更新，等待保存。警告：努力值总和已超过限制。"
                        : $"存档队伍槽位 {row.Slot} 已更新，等待保存。",
                    success: evTotal <= 510);
            });
            return;
        }

        await RunUiTask("写入当前精灵", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var (addr, mon) = ReadSelectedLiveMon(bridge, baseAddr, row);
            var evTotal = ApplyPartyEditor(mon);
            WriteMon(bridge, addr, mon);
            SetWriteNotice(
                evTotal > 510
                    ? $"队伍槽位 {row.Slot} 当前精灵写入成功：基础信息/招式/个体/努力已更新。警告：努力值总和已超过限制，可能发生未知错误或坏档。"
                    : $"队伍槽位 {row.Slot} 当前精灵写入成功：基础信息/招式/个体/努力已更新。",
                success: evTotal <= 510);
        });
        await RefreshPartyAndBoxAfterWriteAsync(row.Slot);
    }

    private int ApplyPartyEditor(PartyPokemon mon)
    {
        var ivs = BuildIntStats(IvHpBox, IvAtkBox, IvDefBox, IvSpeBox, IvSpaBox, IvSpdBox);
        foreach (var value in ivs.Values)
        {
            if (value is < 0 or > 31) throw new InvalidOperationException("个体值必须在 0..31 之间。");
        }

        var evs = BuildByteStats(EvHpBox, EvAtkBox, EvDefBox, EvSpeBox, EvSpaBox, EvSpdBox);
        var before = mon.GetInfo();
        var originalOtName = PokemonOtName(mon.Raw);
        var species = ParseSpeciesOrNull(SpeciesBox);
        var experience = ResolvePartyExperience(before, species);
        SyncNicknameForSpeciesChange(mon, species);
        ApplyEditedOtName(mon, PartyOtNameBox.Text, originalOtName);

        if (SelectedChoiceId(NatureBox) is { } nature) mon.SetNature(nature);
        mon.SetShiny(ShinyBox.IsChecked == true);
        ApplyGenderChoice(mon, species ?? before.Species);
        if (SelectedAbilitySlot() is { } abilitySlot)
            mon.SetAbilitySlot(abilitySlot, ReadSpeciesStats(species ?? before.Species).GenderRatio);
        mon.SetGrowth(
            species,
            ParseItemOrNull(ItemBox),
            experience.Exp,
            ParsePartyFriendshipOrHatchCounter(before),
            BuildPpBonuses(PpUp1Box, PpUp2Box, PpUp3Box, PpUp4Box));
        mon.SetUnencrypted(null, ParseUShortOrNull(MaxHpBox.Text), SelectedStatus(), experience.Level);
        mon.SetMoves(
            [ParseMoveOrNull(Move1Box), ParseMoveOrNull(Move2Box), ParseMoveOrNull(Move3Box), ParseMoveOrNull(Move4Box)],
            [ParseByteOrNull(Pp1Box.Text), ParseByteOrNull(Pp2Box.Text), ParseByteOrNull(Pp3Box.Text), ParseByteOrNull(Pp4Box.Text)]);
        mon.SetIvs(ivs);
        mon.SetEvs(evs);
        RecalculateLiveStats(mon);
        return evs.Values.Sum(x => x);
    }

    private async void OnPartyDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        if (!await ConfirmPartyDeleteAsync(row)) return;

        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            await RunUiTask("删除存档队伍精灵", () =>
            {
                var document = _loadedSave ?? throw new InvalidOperationException("当前没有已读取的存档。");
                document.RemovePartyPokemon(row.Slot);
                _partyRows.RemoveAt(row.Slot - 1);
                for (var index = row.Slot - 1; index < _partyRows.Count; index++)
                {
                    var current = _partyRows[index];
                    _partyRows[index] = ToRow(index + 1, (uint)document.PartySaveOffset(index + 1), current.Mon);
                }
                PartyList.SelectedIndex = _partyRows.Count == 0 ? -1 : Math.Clamp(row.Slot - 1, 0, _partyRows.Count - 1);
                UpdateSaveButtons();
                SetWriteNotice($"存档队伍槽位 {row.Slot} 已删除并压缩后续槽位，等待保存。");
                ShowToast("存档队伍精灵已删除，等待保存。", success: true);
            });
            return;
        }

        await RunUiTask("删除队伍精灵", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var count = ReadLivePartyCount(bridge, baseAddr)
                        ?? throw new InvalidOperationException("没有读到当前队伍数量，请重新读取队伍。");
            if (row.Slot > count)
                throw new InvalidOperationException($"当前队伍只有 {count} 只，槽位 {row.Slot} 已不在队伍中。请重新读取队伍。");

            var (addr, _) = ReadSelectedLiveMon(bridge, baseAddr, row);
            var blocksToWrite = count - row.Slot + 1;
            var compacted = new byte[blocksToWrite * Gen3Constants.PartyMonSize];
            for (var slot = row.Slot + 1; slot <= count; slot++)
            {
                var source = bridge.Read(SlotAddress(baseAddr, slot), Gen3Constants.PartyMonSize);
                source.CopyTo(compacted, (slot - row.Slot - 1) * Gen3Constants.PartyMonSize);
            }

            WriteLivePartyRange(bridge, addr, compacted);
            var newCount = count - 1;
            WritePartyCount(bridge, baseAddr, newCount);
            SetWriteNotice($"队伍槽位 {row.Slot} 已删除：{row.Title}。请回游戏确认后手动保存。");
            ShowToast("队伍精灵删除成功。", success: true);
            LoadPartyRows(bridge, baseAddr, Math.Clamp(row.Slot, 1, Math.Max(newCount, 1)));
        });
    }

    private async void OnApplyMovesClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        await RunUiTask("写入招式", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var (addr, mon) = ReadSelectedLiveMon(bridge, baseAddr, row);
            mon.SetMoves(
                [ParseMoveOrNull(Move1Box), ParseMoveOrNull(Move2Box), ParseMoveOrNull(Move3Box), ParseMoveOrNull(Move4Box)],
                [ParseByteOrNull(Pp1Box.Text), ParseByteOrNull(Pp2Box.Text), ParseByteOrNull(Pp3Box.Text), ParseByteOrNull(Pp4Box.Text)]);
            mon.SetGrowth(ppBonuses: BuildPpBonuses(PpUp1Box, PpUp2Box, PpUp3Box, PpUp4Box));
            WriteMon(bridge, addr, mon);
            SetWriteNotice($"队伍槽位 {row.Slot} 招式写入成功：4 个招式、PP 和 PP提升已更新。");
        });
        await RefreshPartyAndBoxAfterWriteAsync(row.Slot);
    }

    private async void OnApplyEvsClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        if (!TryReadCurrentEvTotal("写入努力值", out var pendingEvTotal)) return;
        if (!await ConfirmHighEvTotalAsync(pendingEvTotal)) return;
        await RunUiTask("写入努力值", () =>
        {
            var values = BuildByteStats(EvHpBox, EvAtkBox, EvDefBox, EvSpeBox, EvSpaBox, EvSpdBox);
            var evTotal = values.Values.Sum(x => x);
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var (addr, mon) = ReadSelectedLiveMon(bridge, baseAddr, row);
            mon.SetEvs(values);
            RecalculateLiveStats(mon);
            WriteMon(bridge, addr, mon);
            SetWriteNotice(
                evTotal > 510
                    ? $"队伍槽位 {row.Slot} 努力值写入成功：当前能力已重新计算。警告：努力值总和已超过限制，可能发生未知错误或坏档。"
                    : $"队伍槽位 {row.Slot} 努力值写入成功：当前能力已重新计算。",
                success: evTotal <= 510);
        });
        await RefreshPartyAndBoxAfterWriteAsync(row.Slot);
    }

    private async void OnApplyIvsClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        await RunUiTask("写入个体值", () =>
        {
            var values = BuildIntStats(IvHpBox, IvAtkBox, IvDefBox, IvSpeBox, IvSpaBox, IvSpdBox);
            foreach (var value in values.Values)
            {
                if (value is < 0 or > 31) throw new InvalidOperationException("个体值必须在 0..31 之间。");
            }
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var (addr, mon) = ReadSelectedLiveMon(bridge, baseAddr, row);
            mon.SetIvs(values);
            RecalculateLiveStats(mon);
            WriteMon(bridge, addr, mon);
            SetWriteNotice($"队伍槽位 {row.Slot} 个体值写入成功：当前能力已重新计算。");
        });
        await RefreshPartyAndBoxAfterWriteAsync(row.Slot);
    }

    private async void OnApplyIvsEvsClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        if (!TryReadCurrentEvTotal("写入个体/努力", out var pendingEvTotal)) return;
        if (!await ConfirmHighEvTotalAsync(pendingEvTotal)) return;
        await RunUiTask("写入个体/努力", () =>
        {
            var ivs = BuildIntStats(IvHpBox, IvAtkBox, IvDefBox, IvSpeBox, IvSpaBox, IvSpdBox);
            foreach (var value in ivs.Values)
            {
                if (value is < 0 or > 31) throw new InvalidOperationException("个体值必须在 0..31 之间。");
            }

            var evs = BuildByteStats(EvHpBox, EvAtkBox, EvDefBox, EvSpeBox, EvSpaBox, EvSpdBox);
            var evTotal = evs.Values.Sum(x => x);

            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var (addr, mon) = ReadSelectedLiveMon(bridge, baseAddr, row);
            mon.SetIvs(ivs);
            mon.SetEvs(evs);
            RecalculateLiveStats(mon);
            WriteMon(bridge, addr, mon);
            SetWriteNotice(
                evTotal > 510
                    ? $"队伍槽位 {row.Slot} 个体值/努力值写入成功：当前能力已重新计算。警告：努力值总和已超过限制，可能发生未知错误或坏档。"
                    : $"队伍槽位 {row.Slot} 个体值/努力值写入成功：当前能力已重新计算。",
                success: evTotal <= 510);
        });
        await RefreshPartyAndBoxAfterWriteAsync(row.Slot);
    }

    private void OnIvAll31Clicked(object? sender, RoutedEventArgs e) => SetAllIvs(31);

    private void OnIvAll30Clicked(object? sender, RoutedEventArgs e) => SetAllIvs(30);

    private void OnBoxIvAll31Clicked(object? sender, RoutedEventArgs e) => SetAllBoxIvs(31);

    private void OnBoxIvAll30Clicked(object? sender, RoutedEventArgs e) => SetAllBoxIvs(30);

    private async void OnBagScanClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("读取背包", () =>
        {
            using var bridge = ConnectBridge();
            LoadBagRows(bridge);
        });
    }

    private async void OnBagSnapshotClicked(object? sender, RoutedEventArgs e)
    {
        var requests = await ShowBagCalibrationDialogAsync();
        if (requests is null) return;

        await RunUiTask("校准背包", () =>
        {
            using var bridge = ConnectBridge();
            _bagBase = TryLocateBagBase(bridge);
            var ewram = PartyScanner.ReadEwram(bridge, _profile);
            var quantityKey = BagScanner.InferQuantityKey(ewram);
            var pockets = CalibrateBagPockets(ewram, _bagBase, requests, quantityKey);
            if (pockets.Count == 0)
                throw new InvalidOperationException("没有按填写内容定位到口袋。请确认第一个道具和数量与游戏内一致。");

            _allBagRows.Clear();
            foreach (var pocket in pockets)
            {
                var definition = new BagPocketDefinition(
                    pocket.Pocket,
                    PocketNameZh(pocket.Pocket),
                    0,
                    pocket.SlotCount,
                    true,
                    quantityKey);
                for (var i = 0; i < pocket.Slots.Count; i++)
                {
                    var slot = pocket.Slots[i];
                    _allBagRows.Add(ToBagRow(
                        slot.Address,
                        slot.ItemId,
                        slot.Quantity,
                        $"第 {i + 1} 格 / 手动校准 / 数量密钥 0x{quantityKey:X4}",
                        pocket.Pocket,
                        i + 1,
                        definition));
                }
            }

            ApplyBagPocketFilter();
            if (_bagRows.Count > 0) BagList.SelectedIndex = 0;
            ShowToast($"背包校准完成：定位到 {pockets.Count} 个口袋、{_allBagRows.Count} 个槽位。", success: true);
            Log($"背包校准完成：定位到 {pockets.Count} 个口袋、{_allBagRows.Count} 个槽位。");
        });
    }

    private async Task<IReadOnlyList<BagCalibrationRequest>?> ShowBagCalibrationDialogAsync()
    {
        var dialog = new Window
        {
            Title = "校准背包",
            Width = 780,
            Height = 620,
            MinWidth = 720,
            MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var rows = new List<(int Pocket, SearchableChoiceBox ItemBox, TextBox QuantityBox, ChoiceRow[] Choices)>();
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "请按游戏背包中每个分类的第一个道具填写。空口袋或不需要校准的分类可以留空。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });
        root.Children.Add(new TextBlock
        {
            Text = "注意：这里不会写入游戏，只用于重新定位各口袋的真实起始地址。",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(179, 38, 30))
        });

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120,*,100"),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", 8))),
            ColumnSpacing = 10,
            RowSpacing = 8
        };

        for (var i = 0; i < 8; i++)
        {
            var pocket = i + 1;
            var choices = BagItemChoicesForPocket(pocket).Where(c => c.Id != 0).ToArray();
            var itemBox = new SearchableChoiceBox
            {
                ItemsSource = choices,
                PlaceholderText = "选择第一个道具",
                MaxDropDownHeight = 360
            };
            FitComboToContent(itemBox, choices);
            var quantityBox = new TextBox { Watermark = "数量" };

            if (_allBagRows.FirstOrDefault(r => r.Pocket == pocket) is { } current)
            {
                SetChoice(itemBox, choices, current.ItemId);
                quantityBox.Text = current.Quantity.ToString();
            }

            var label = new TextBlock
            {
                Text = PocketNameZh(pocket),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.SemiBold
            };

            Grid.SetRow(label, i);
            Grid.SetRow(itemBox, i);
            Grid.SetColumn(itemBox, 1);
            Grid.SetRow(quantityBox, i);
            Grid.SetColumn(quantityBox, 2);
            grid.Children.Add(label);
            grid.Children.Add(itemBox);
            grid.Children.Add(quantityBox);
            rows.Add((pocket, itemBox, quantityBox, choices));
        }

        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(179, 38, 30)),
            TextWrapping = TextWrapping.Wrap
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = "开始校准", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        root.Children.Add(grid);
        root.Children.Add(errorText);
        root.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = root };

        IReadOnlyList<BagCalibrationRequest>? result = null;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            try
            {
                var requests = new List<BagCalibrationRequest>();
                foreach (var row in rows)
                {
                    var item = ParseChoiceUShortOrNull(row.ItemBox, row.Choices);
                    if (item is null or 0) continue;
                    var quantity = ParseUShortRequired(row.QuantityBox.Text, $"{PocketNameZh(row.Pocket)}数量");
                    var maxQuantity = MaxBagWriteQuantityForPocket(row.Pocket);
                    if (quantity is 0 || quantity > maxQuantity)
                        throw new InvalidOperationException($"{PocketNameZh(row.Pocket)}数量必须在 1..{maxQuantity}。");
                    requests.Add(new BagCalibrationRequest(row.Pocket, item.Value, quantity));
                }

                if (requests.Count == 0)
                    throw new InvalidOperationException("至少填写一个口袋的第一个道具和数量。");
                result = requests;
                dialog.Close();
            }
            catch (Exception ex)
            {
                errorText.Text = ex.Message;
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private IReadOnlyList<BagPocket> CalibrateBagPockets(
        ReadOnlySpan<byte> ewram,
        uint? saveBlockBase,
        IReadOnlyList<BagCalibrationRequest> requests,
        ushort quantityKey)
    {
        var startOffset = 0;
        var endOffset = ewram.Length;
        if (saveBlockBase.HasValue &&
            saveBlockBase.Value >= _profile.Memory.EwramBase &&
            saveBlockBase.Value < _profile.Memory.EwramBase + _profile.Memory.EwramSize)
        {
            startOffset = Math.Max(0, (int)(saveBlockBase.Value - _profile.Memory.EwramBase));
            endOffset = Math.Min(ewram.Length, startOffset + 0x4000);
        }

        var pockets = new List<BagPocket>();
        foreach (var request in requests)
        {
            var pocket = CalibrateBagPocket(ewram, startOffset, endOffset, request, quantityKey);
            if (pocket is not null)
                pockets.Add(pocket);
        }

        return pockets.OrderBy(pocket => pocket.Pocket).ToArray();
    }

    private BagPocket? CalibrateBagPocket(
        ReadOnlySpan<byte> ewram,
        int startOffset,
        int endOffset,
        BagCalibrationRequest request,
        ushort quantityKey)
    {
        BagPocket? best = null;
        for (var off = startOffset; off <= endOffset - 4; off += 4)
        {
            var item = ReadU16(ewram, off);
            if (item != request.ItemId) continue;
            var quantity = (ushort)(ReadU16(ewram, off + 2) ^ quantityKey);
            if (quantity != request.Quantity) continue;

            var slots = new List<BagSlot>();
            var score = 0;
            for (var i = 0; i < BagScanner.MaxRunSlots && off + i * 4 + 4 <= endOffset; i++)
            {
                var slotOff = off + i * 4;
                var slotItem = ReadU16(ewram, slotOff);
                var rawQuantity = ReadU16(ewram, slotOff + 2);
                if (slotItem == 0)
                {
                    if (rawQuantity == 0 || rawQuantity == quantityKey) break;
                    slots.Clear();
                    break;
                }

                if (PocketOfItem(slotItem) != request.Pocket) break;
                var slotQuantity = (ushort)(rawQuantity ^ quantityKey);
                if (slotQuantity is 0 || slotQuantity > MaxBagWriteQuantityForPocket(request.Pocket)) break;
                score += slotItem == request.ItemId && slotQuantity == request.Quantity ? 60 : 30;
                slots.Add(new BagSlot(
                    _profile.Memory.EwramBase + (uint)slotOff,
                    slotItem,
                    slotQuantity,
                    score,
                    $"手动校准；数量密钥 0x{quantityKey:X4}"));
            }

            if (slots.Count == 0) continue;
            var candidate = new BagPocket(request.Pocket, slots[0].Address, slots.Count, slots.Count, score, slots);
            if (best is null || candidate.Score > best.Score)
                best = candidate;
        }

        return best;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private void OnBagPocketChanged(object? sender, SelectionChangedEventArgs e) => ApplyBagPocketFilter();

    private void OnBagSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BagList.SelectedItem is not BagSlotRow row) return;
        ShowBagRowDetails(row);
    }

    private void OnSaveBagSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SaveBagList.SelectedItem is not SaveBagEditableRow editable) return;
        ShowBagRowDetails(editable.Row);
    }

    private void ShowBagRowDetails(BagSlotRow row)
    {
        BagAddressBox.Text = $"0x{row.Address:X8}";
        SetBagItemChoicesForPocket(row.Pocket, row.ItemId);
        BagQuantityBox.Text = row.Quantity.ToString();
        UpdateBagNameText();
    }

    private async void OnSaveBagInlineItemChanged(object? sender, EventArgs e)
    {
        if (_dataSourceMode != DataSourceMode.SaveFile ||
            sender is not SearchableChoiceBox box ||
            box.DataContext is not SaveBagEditableRow editable ||
            box.SelectedItem is not ChoiceRow choice ||
            choice.Id == editable.Row.ItemId)
            return;

        var loaded = _loadedSave;
        if (choice.Id != 0 && loaded?.CurrentBag.Any(entry =>
                entry.SaveOffset != editable.Row.Address &&
                entry.Pocket == editable.Row.Pocket &&
                entry.ItemId == choice.Id) == true)
        {
            ShowToast($"{ItemName(choice.Id)}已存在于当前分类，不能产生重复格。", success: false);
            RefreshSaveBagEditableRows(editable.Row.Address);
            return;
        }

        await RunUiTask("修改存档背包道具", () =>
        {
            var document = loaded ?? throw new InvalidOperationException("请先读取存档。");
            var item = checked((ushort)choice.Id);
            var quantity = item == 0 ? (ushort)0 : IsKeyItemPocket(editable.Row.Pocket) ? (ushort)1 : editable.Row.Quantity;
            var updated = document.ReplaceBagEntry(checked((int)editable.Row.Address), item, quantity);
            LoadSaveBagRows(document.CurrentBag, updated?.SaveOffset);
            UpdateSaveButtons();
            SetWriteNotice(item == 0
                ? $"{PocketNameZh(editable.Row.Pocket)}第 {editable.Row.SlotInPocket} 格已清空，等待保存。"
                : $"{PocketNameZh(editable.Row.Pocket)}第 {editable.Row.SlotInPocket} 格已改为 {ItemName(item)}，等待保存。");
        });
    }

    private async void OnSaveBagInlineQuantityLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            await CommitSaveBagInlineQuantityAsync(box);
    }

    private async void OnSaveBagInlineQuantityKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != global::Avalonia.Input.Key.Enter || sender is not TextBox box) return;
        e.Handled = true;
        await CommitSaveBagInlineQuantityAsync(box);
    }

    private async Task CommitSaveBagInlineQuantityAsync(TextBox box)
    {
        if (_dataSourceMode != DataSourceMode.SaveFile || box.DataContext is not SaveBagEditableRow editable)
            return;
        if (IsKeyItemPocket(editable.Row.Pocket))
        {
            box.Text = "1";
            return;
        }
        var maxQuantity = MaxBagWriteQuantityForPocket(editable.Row.Pocket);
        if (!ushort.TryParse(box.Text?.Trim(), out var quantity) || quantity < 1 || quantity > maxQuantity)
        {
            box.Text = editable.Row.Quantity.ToString();
            ShowToast($"数量必须在 1..{maxQuantity} 之间。", success: false);
            return;
        }

        var document = _loadedSave;
        var current = document?.CurrentBag.FirstOrDefault(entry => entry.SaveOffset == editable.Row.Address);
        if (document is null || current is null)
        {
            ShowToast("背包槽已经变化，请重新读取存档。", success: false);
            return;
        }
        if (current.Quantity == quantity) return;

        await RunUiTask("修改存档背包数量", () =>
        {
            document.ReplaceBagEntry(current.SaveOffset, current.ItemId, quantity);
            LoadSaveBagRows(document.CurrentBag, current.SaveOffset);
            UpdateSaveButtons();
            SetWriteNotice($"{PocketNameZh(current.Pocket)}第 {current.SlotInPocket} 格数量已改为 {quantity}，等待保存。");
        });
    }

    private async void OnBagReadClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("读取背包槽", () =>
        {
            using var bridge = ConnectBridge();
            var addr = ParseUIntRequired(BagAddressBox.Text, "背包槽地址");
            var definition = ResolveBagDefinition(bridge, addr);
            var slot = definition is null
                ? BagScanner.ReadSlot(bridge, addr, MaxItemId(), MaxBagScanQuantity())
                : BagScanner.ReadSlot(bridge, addr, definition, MaxBagWriteQuantityForPocket(definition.Pocket));
            SetBagItemChoicesForPocket(definition?.Pocket ?? PocketOfItem(slot.ItemId), slot.ItemId);
            BagQuantityBox.Text = slot.Quantity.ToString();
            UpdateBagNameText();
            Log($"已读取背包槽 0x{addr:X8}: {slot.ItemId}({ItemName(slot.ItemId)}) x{slot.Quantity}。");
        });
    }

    private async void OnBagApplyClicked(object? sender, RoutedEventArgs e)
    {
        var row = _dataSourceMode == DataSourceMode.SaveFile
            ? (SaveBagList.SelectedItem as SaveBagEditableRow)?.Row
            : BagList.SelectedItem as BagSlotRow;
        if (row is null)
        {
            ShowToast("请先选择一个背包槽。", success: false);
            return;
        }

        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            await RunUiTask("修改存档背包槽", () =>
            {
                var document = _loadedSave ?? throw new InvalidOperationException("请先读取存档。");
                var item = ParseBagItemRequired(row.Pocket, allowEmpty: true);
                var qty = ParseBagQuantityRequired(item, allowZero: item == 0, row.Pocket);
                var updated = document.ReplaceBagEntry(checked((int)row.Address), item, qty);
                LoadSaveBagRows(document.CurrentBag, updated?.SaveOffset);
                UpdateSaveButtons();
                SetWriteNotice(item == 0
                    ? $"存档背包槽 0x{row.Address:X} 已清空，等待保存。"
                    : $"存档背包槽已更新为 {ItemName(item)} x{(row.Pocket == 8 ? 1 : qty)}，等待保存。");
            });
            return;
        }

        await RunUiTask("写入背包槽", () =>
        {
            using var bridge = ConnectBridge();
            EnsureBagSelectionFresh(bridge);
            var addr = row.Address;
            var item = ParseBagItemRequired(row.Pocket, allowEmpty: true);
            var qty = ParseBagQuantityRequired(item, allowZero: item == 0, row.Pocket);
            var liveKey = UsesFixedLiveBag ? row.QuantityKey : BagScanner.InferQuantityKey(PartyScanner.ReadEwram(bridge, _profile));
            var quantityKey = liveKey != 0 ? liveKey : row.QuantityKey;
            var quantityXor = row.QuantityXor || quantityKey != 0;
            var definition = new BagPocketDefinition(row.Pocket, PocketNameZh(row.Pocket), 0, 0, quantityXor, quantityKey);
            var before = BagScanner.ReadSlot(bridge, addr, definition, MaxBagWriteQuantityForPocket(row.Pocket));

            WriteLiveBagRange(bridge, addr, EncodeBagOverwriteSlot(item, qty, quantityKey, quantityXor));

            var slot = BagScanner.ReadSlot(bridge, addr, definition, MaxBagWriteQuantityForPocket(row.Pocket));
            SetWriteNotice($"背包覆盖成功：{ItemName(before.ItemId)} x{before.Quantity} -> {ItemName(slot.ItemId)} x{slot.Quantity}。");
            LoadBagRows(bridge);
            BagList.SelectedItem = _bagRows.FirstOrDefault(r => r.Address == addr) ?? BagList.SelectedItem;
        });
    }

    private async void OnBagAddClicked(object? sender, RoutedEventArgs e)
    {
        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            await RunUiTask("添加存档背包道具", () =>
            {
                var document = _loadedSave ?? throw new InvalidOperationException("请先读取存档。");
                var selectedPocket = SelectedChoiceId(BagPocketTabs) ?? 0;
                if (selectedPocket <= 0)
                    selectedPocket = CurrentBagEditPocket();
                if (!IsConcreteBagPocket(selectedPocket))
                    throw new InvalidOperationException("请先切换到具体背包分类。");
                var item = ParseBagItemRequired(selectedPocket, allowEmpty: false);
                var qty = ParseBagQuantityRequired(item, allowZero: false, selectedPocket);
                var added = document.AddBagItem(selectedPocket, item, qty);
                LoadSaveBagRows(document.CurrentBag, added.SaveOffset);
                UpdateSaveButtons();
                SetWriteNotice($"存档背包已添加 {ItemName(item)} x{(IsKeyItemPocket(selectedPocket) ? 1 : qty)}，等待保存。");
            });
            return;
        }

        await RunUiTask("添加背包道具", () =>
        {
            using var bridge = ConnectBridge();
            if (UsesFixedLiveBag)
            {
                AddFixedLiveBagItem(bridge);
                return;
            }
            _bagBase = TryLocateBagBase(bridge);
            var ewram = PartyScanner.ReadEwram(bridge, _profile);
            var selectedPocket = SelectedChoiceId(BagPocketTabs) ?? 0;
            if (selectedPocket <= 0)
                selectedPocket = CurrentBagEditPocket();
            if (selectedPocket <= 0)
                throw new InvalidOperationException("请先切换到具体背包分类，或先选中一个背包槽。");
            var item = ParseBagItemRequired(selectedPocket, allowEmpty: false);
            var qty = ParseBagQuantityRequired(item, allowZero: false, selectedPocket);
            var target = BagScanner.FindAddTarget(
                ewram, _profile.Memory.EwramBase, _bagBase, _runtime.ScannedBagDefinitions,
                selectedPocket, item, qty, PocketOfItem,
                pocket => _runtime.IsKeyItemPocket(pocket, true),
                pocket => _runtime.BagBatchCapacity(pocket, true),
                MaxItemId(), MaxBagWriteQuantityForPocket(selectedPocket));
            WriteLiveBagRange(bridge, target.Address, BagScanner.EncodeSlot(target.ItemId, target.AfterQuantity, target.QuantityKey, quantityXor: true));
            SetWriteNotice($"背包添加成功：{ItemName(item)} {target.BeforeQuantity} -> {target.AfterQuantity}（{target.Note}）。");
            LoadBagRows(bridge);
            BagList.SelectedItem = _bagRows.FirstOrDefault(r => r.Address == target.Address) ?? BagList.SelectedItem;
        });
    }

    private void AddFixedLiveBagItem(MgbaBridgeClient bridge)
    {
        var selectedPocket = SelectedChoiceId(BagPocketTabs) ?? 0;
        if (!IsConcreteBagPocket(selectedPocket))
            selectedPocket = CurrentBagEditPocket();
        var area = ProfileLiveBagArea(selectedPocket)
                   ?? throw new InvalidOperationException("请先切换到具体背包分类。");
        var item = ParseBagItemRequired(selectedPocket, allowEmpty: false);
        var quantity = ParseBagQuantityRequired(item, allowZero: false, selectedPocket);
        var key = ReadProfileQuantityKey(bridge);
        var raw = bridge.Read(area.Address, area.Capacity * 4);
        var targetIndex = -1;
        var lastUsedIndex = -1;
        ushort beforeQuantity = 0;
        for (var i = 0; i < area.Capacity; i++)
        {
            var existing = ReadU16(raw, i * 4);
            if (existing == item)
            {
                targetIndex = i;
                beforeQuantity = (ushort)(ReadU16(raw, i * 4 + 2) ^ key);
                quantity = (ushort)Math.Min(MaxBagWriteQuantityForPocket(selectedPocket), beforeQuantity + quantity);
                break;
            }
            if (existing != 0 && existing <= MaxItemId())
                lastUsedIndex = i;
        }
        if (targetIndex < 0)
        {
            for (var i = lastUsedIndex + 1; i < area.Capacity; i++)
            {
                if (ReadU16(raw, i * 4) == 0)
                {
                    targetIndex = i;
                    break;
                }
            }
        }
        if (targetIndex < 0) throw new InvalidOperationException("当前口袋已满。");
        var address = area.Address + (uint)(targetIndex * 4);
        WriteLiveBagRange(bridge, address, EncodeBagOverwriteSlot(item, quantity, key, quantityXor: true));
        LoadFixedBagRows(bridge);
        BagList.SelectedItem = _bagRows.FirstOrDefault(row => row.Address == address) ?? BagList.SelectedItem;
        SetWriteNotice($"背包添加成功：{ItemName(item)} {beforeQuantity} -> {quantity}。");
    }

    private async void OnBagGiveAllCurrentPocketClicked(object? sender, RoutedEventArgs e)
    {
        var pocket = SelectedChoiceId(BagPocketTabs) ?? 0;
        if (pocket <= 0)
            pocket = CurrentBagEditPocket();
        if (!IsConcreteBagPocket(pocket))
        {
            ShowToast("请先选择一个具体背包分类。", success: false);
            return;
        }

        var items = BagItemChoicesForPocket(pocket)
            .Where(choice => choice.Id > 0 && PocketOfItem(choice.Id) == pocket && IsBulkImportableItem(choice.Id))
            .Select(choice => (ushort)choice.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (items.Length == 0)
        {
            ShowToast("当前分类没有可写入的道具。", success: false);
            return;
        }

        var label = $"获取全部{PocketNameZh(pocket)}";
        if (!await ConfirmBagPocketBatchAsync(label, pocket, items.Length)) return;
        if (pocket == MachinePocket)
            await GiveBagMachinesAsync(label, items);
        else
            await GiveBagPocketItemsAsync(label, pocket, items);
    }

    private async void OnBagGiveAllTmsClicked(object? sender, RoutedEventArgs e)
    {
        var items = MachineBagItemIds(includeHms: false);
        if (!await ConfirmBagMachineBatchAsync("获取全部技能机", items.Length)) return;
        await GiveBagMachinesAsync("获取全部技能机", items);
    }

    private async void OnBagGiveAllHmsClicked(object? sender, RoutedEventArgs e)
    {
        var items = MachineBagItemIds(includeHms: true);
        if (!await ConfirmBagMachineBatchAsync("获取全部秘传机", items.Length)) return;
        await GiveBagMachinesAsync("获取全部秘传机", items);
    }

    private ushort[] MachineBagItemIds(bool includeHms)
    {
        if (_profile.Runtime.Machines.Mode == "item-data")
        {
            return Enumerable.Range(1, MaxItemId())
                .Select(item => (Item: item, HasData: TryReadEmbeddedItemData(item, out var data), Data: data))
                .Where(row =>
                    row.HasData &&
                    row.Data.Pocket == MachinePocket &&
                    row.Data.ExitsBagOnUse is >= 1 and <= 128 &&
                    (includeHms ? row.Data.ExitsBagOnUse >= 121 : row.Data.ExitsBagOnUse <= 120))
                .OrderBy(row => row.Data.ExitsBagOnUse)
                .Select(row => (ushort)row.Item)
                .ToArray();
        }

        var rules = _profile.Runtime.Machines;
        return includeHms
            ? Enumerable.Range(rules.HmStartItem, rules.HmCount).Select(i => (ushort)i).ToArray()
            : Enumerable.Range(rules.TmStartItem, rules.TmCount).Select(i => (ushort)i).ToArray();
    }

    private async Task GiveBagMachinesAsync(string label, IReadOnlyList<ushort> items)
    {
        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            await RunUiTask(label, () =>
            {
                var document = _loadedSave ?? throw new InvalidOperationException("请先读取存档。");
                var writableItems = items.Where(item => MachineMoveName(item) is not null).ToArray();
                var skipped = items.Count - writableItems.Length;
                var result = document.SetBagItems(MachinePocket, writableItems, BatchQuantityForPocket(MachinePocket));
                LoadSaveBagRows(document.CurrentBag);
                BagPocketTabs.SelectedItem = _bagPocketChoices.FirstOrDefault(choice => choice.Id == MachinePocket) ?? BagPocketTabs.SelectedItem;
                SelectSaveBagRow(_bagRows.FirstOrDefault(row => writableItems.Contains(row.ItemId))?.Address);
                UpdateSaveButtons();
                SetWriteNotice($"{label}已写入存档副本：新增 {result.Added} 个，更新 {result.Updated} 个，跳过 {skipped} 个，等待保存。");
            });
            return;
        }

        await RunUiTask(label, () =>
        {
            using var bridge = ConnectBridge();
            _bagBase = UsesFixedLiveBag
                ? _profile.Runtime.LiveBag.BaseAddress
                : BagScanner.LocateSaveBlockBase(bridge, _profile, _runtime.ScannedBagDefinitions);
            var ewram = PartyScanner.ReadEwram(bridge, _profile);
            var fixedArea = ProfileLiveBagArea(MachinePocket);
            var definition = UsesFixedLiveBag
                ? new BagPocketDefinition(MachinePocket, fixedArea?.Name ?? "招式机器", 0, fixedArea?.Capacity ?? 0, true, 0)
                : BagScanner.DefinitionForPocket(_runtime.ScannedBagDefinitions, MachinePocket)
                  ?? throw new InvalidOperationException("缺少招式机器/秘传机器口袋定义。");
            var startAddress = UsesFixedLiveBag
                ? fixedArea?.Address ?? throw new InvalidOperationException("当前 Profile 缺少实时机器口袋地址。")
                : _bagBase.Value + definition.Offset;
            var startOffset = checked((int)(startAddress - _profile.Memory.EwramBase));
            var endOffset = startOffset + definition.SlotCount * 4;
            if (startOffset < 0 || endOffset > ewram.Length)
                throw new InvalidOperationException("招式机器/秘传机器口袋地址无效。请先读取或校准背包。");

            var quantityKey = UsesFixedLiveBag
                ? ReadProfileQuantityKey(bridge)
                : BagScanner.InferQuantityKey(ewram);
            var machines = ReadMachinePocketQuantities(ewram, startOffset, definition.SlotCount, quantityKey);
            var existingBefore = items.Count(machines.ContainsKey);
            var skipped = 0;
            foreach (var item in items)
            {
                if (MachineMoveName(item) is null)
                {
                    skipped++;
                    continue;
                }

                machines[item] = BatchQuantityForPocket(MachinePocket);
            }

            if (machines.Count > definition.SlotCount)
                throw new InvalidOperationException($"机器口袋容量不足：需要 {machines.Count} 格，容量 {definition.SlotCount} 格。");

            var rewritten = EncodeMachinePocket(machines, definition.SlotCount, quantityKey, MaxBagWriteQuantityForPocket(MachinePocket));
            WriteLiveBagRange(bridge, startAddress, rewritten);
            var added = Math.Max(0, items.Count - existingBefore - skipped);

            LoadBagRows(bridge);
            BagPocketTabs.SelectedItem = _bagPocketChoices.FirstOrDefault(choice => choice.Id == MachinePocket) ?? BagPocketTabs.SelectedItem;
            if (added > 0)
                BagList.SelectedItem = _bagRows.FirstOrDefault(row => items.Contains(row.ItemId)) ?? BagList.SelectedItem;
            var message = $"{label}完成：新增 {added} 个，已有 {existingBefore} 个，跳过 {skipped} 个。请回游戏确认后手动保存。";
            SetWriteNotice(message);
        });
    }

    private async Task GiveBagPocketItemsAsync(string label, int pocket, IReadOnlyList<ushort> items)
    {
        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            await RunUiTask(label, () =>
            {
                var document = _loadedSave ?? throw new InvalidOperationException("请先读取存档。");
                var quantity = BatchQuantityForPocket(pocket);
                var result = document.SetBagItems(pocket, items, quantity);
                LoadSaveBagRows(document.CurrentBag);
                BagPocketTabs.SelectedItem = _bagPocketChoices.FirstOrDefault(choice => choice.Id == pocket) ?? BagPocketTabs.SelectedItem;
                SelectSaveBagRow(_bagRows.FirstOrDefault(row => items.Contains(row.ItemId))?.Address);
                UpdateSaveButtons();
                SetWriteNotice($"{label}已写入存档副本：新增 {result.Added} 个，更新 {result.Updated} 个，数量设为 {quantity}，等待保存。");
            });
            return;
        }

        if (UsesFixedLiveBag)
        {
            await GiveFixedLiveBagPocketItemsAsync(label, pocket, items);
            return;
        }

        await RunUiTask(label, () =>
        {
            using var bridge = ConnectBridge();
            _bagBase = TryLocateBagBase(bridge);
            var ewram = PartyScanner.ReadEwram(bridge, _profile);
            var quantityKey = BagScanner.InferQuantityKey(ewram);
            var run = BagScanner.FindLivePockets(
                    ewram, _profile.Memory.EwramBase, _bagBase,
                    _runtime.BagPockets(true).Select(candidate => candidate.Id).ToArray(),
                    PocketOfItem, candidate => _runtime.IsKeyItemPocket(candidate, true),
                    candidate => _runtime.BagBatchCapacity(candidate, true), MaxItemId(), MaxBagScanQuantity())
                .FirstOrDefault(candidate => candidate.Pocket == pocket)
                ?? throw new InvalidOperationException($"当前{PocketNameZh(pocket)}尚未定位。请先读取背包，或在游戏内至少获得该分类一个道具后再试。");

            var startOffset = checked((int)(run.StartAddress - _profile.Memory.EwramBase));
            var endOffset = Math.Min(ewram.Length, startOffset + BagBatchCapacity(pocket) * 4);
            var existing = new Dictionary<ushort, int>();
            var emptyOffsets = new List<int>();
            for (var offset = startOffset; offset <= endOffset - 4; offset += 4)
            {
                var item = ReadU16(ewram, offset);
                var rawQuantity = ReadU16(ewram, offset + 2);
                if (item == 0 && (rawQuantity == 0 || rawQuantity == quantityKey))
                {
                    emptyOffsets.Add(offset);
                    continue;
                }

                if (items.Contains(item) && !existing.ContainsKey(item))
                    existing[item] = offset;
            }

            var missing = items.Where(item => !existing.ContainsKey(item)).ToArray();
            if (missing.Length > emptyOffsets.Count)
                throw new InvalidOperationException($"{PocketNameZh(pocket)}容量不足：缺少 {missing.Length} 个道具，但只找到 {emptyOffsets.Count} 个空槽。未写入。");

            var updated = 0;
            foreach (var (item, offset) in existing)
            {
                var bytes = EncodeBatchBagSlot(pocket, item, quantityKey);
                WriteLiveBagRange(bridge, _profile.Memory.EwramBase + (uint)offset, bytes);
                updated++;
            }

            var added = 0;
            for (var i = 0; i < missing.Length; i++)
            {
                var offset = emptyOffsets[i];
                var bytes = EncodeBatchBagSlot(pocket, missing[i], quantityKey);
                WriteLiveBagRange(bridge, _profile.Memory.EwramBase + (uint)offset, bytes);
                added++;
            }

            LoadBagRows(bridge);
            BagPocketTabs.SelectedItem = _bagPocketChoices.FirstOrDefault(choice => choice.Id == pocket) ?? BagPocketTabs.SelectedItem;
            BagList.SelectedItem = _bagRows.FirstOrDefault(row => items.Contains(row.ItemId)) ?? BagList.SelectedItem;
            SetWriteNotice($"{label}完成：新增 {added} 个，更新数量 {updated} 个，数量设置为 {BatchQuantityForPocket(pocket)}。请回游戏确认后手动保存。");
        });
    }

    private async Task GiveFixedLiveBagPocketItemsAsync(string label, int pocket, IReadOnlyList<ushort> items)
    {
        await RunUiTask(label, () =>
        {
            var area = ProfileLiveBagArea(pocket)
                       ?? throw new InvalidOperationException("当前 Profile 没有配置该实时背包口袋。");
            using var bridge = ConnectBridge();
            var quantityKey = ReadProfileQuantityKey(bridge);
            var raw = bridge.Read(area.Address, area.Capacity * 4);
            var wanted = items.ToHashSet();
            var existing = new Dictionary<ushort, int>();
            var lastUsedIndex = -1;

            for (var i = 0; i < area.Capacity; i++)
            {
                var offset = i * 4;
                var item = ReadU16(raw, offset);
                if (item != 0 && item <= MaxItemId())
                    lastUsedIndex = i;
                if (wanted.Contains(item) && !existing.ContainsKey(item))
                    existing[item] = i;
            }

            var missing = items.Where(item => !existing.ContainsKey(item)).ToArray();
            var emptyIndexes = new List<int>();
            for (var i = lastUsedIndex + 1; i < area.Capacity; i++)
            {
                if (ReadU16(raw, i * 4) == 0) emptyIndexes.Add(i);
                if (emptyIndexes.Count >= missing.Length) break;
            }
            if (missing.Length > emptyIndexes.Count)
                throw new InvalidOperationException($"{area.Name}容量不足：缺少 {missing.Length} 个道具，但只找到 {emptyIndexes.Count} 个空槽。未写入。");

            var quantity = BatchQuantityForPocket(pocket);
            var updated = 0;
            foreach (var (item, index) in existing)
            {
                var address = area.Address + (uint)(index * 4);
                WriteLiveBagRange(bridge, address, BagScanner.EncodeSlot(item, quantity, quantityKey, quantityXor: true));
                updated++;
            }

            var added = 0;
            for (var i = 0; i < missing.Length; i++)
            {
                var address = area.Address + (uint)(emptyIndexes[i] * 4);
                WriteLiveBagRange(bridge, address, BagScanner.EncodeSlot(missing[i], quantity, quantityKey, quantityXor: true));
                added++;
            }

            LoadFixedBagRows(bridge);
            BagPocketTabs.SelectedItem = _bagPocketChoices.FirstOrDefault(choice => choice.Id == pocket) ?? BagPocketTabs.SelectedItem;
            BagList.SelectedItem = _bagRows.FirstOrDefault(row => items.Contains(row.ItemId)) ?? BagList.SelectedItem;
            SetWriteNotice($"{label}完成：新增 {added} 个，更新数量 {updated} 个，数量设置为 {quantity}。请回游戏确认后手动保存。");
        });
    }

    private async void OnBoxScanClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("读取箱子", () =>
        {
            using var bridge = ConnectBridge();
            LoadBoxRows(bridge);
        });
    }

    private void OnBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (BoxList.SelectedItem is not BoxSlotRow row) return;
        var info = row.Info;
        SetPokemonSprite(BoxDetailSpriteImage, info.Species);
        UpdateBoxHeaderInfo(info.Species, info.Exp);
        SetOtEditor(BoxOtNameBox, BoxOtIdText, row.Mon.Raw, info.OtId);
        BoxAddressText.Text = $"0x{row.Address:X8}";
        SetChoice(BoxSpeciesBox, _speciesChoices, info.Species);
        SetChoice(BoxItemBox, _itemChoices, info.Item);
        SetChoice(BoxNatureBox, _natureChoices, info.Nature);
        SetChoice(BoxGenderBox, _genderChoices, GenderChoiceId(info.Species, info.Pid));
        BoxShinyBox.IsChecked = row.Mon.IsShiny;
        BoxExpBox.Text = info.Exp.ToString();
        BoxFriendshipBox.Text = info.Friendship.ToString();
        UpdateBoxEggHatchEditor(info);
        RefreshBoxAbilityChoices(info.Species, info.Ivs["ability"]);
        SetChoice(BoxMove1Box, _moveChoices, info.Moves[0]);
        SetChoice(BoxMove2Box, _moveChoices, info.Moves[1]);
        SetChoice(BoxMove3Box, _moveChoices, info.Moves[2]);
        SetChoice(BoxMove4Box, _moveChoices, info.Moves[3]);
        BoxPp1Box.Text = info.Pp[0].ToString();
        BoxPp2Box.Text = info.Pp[1].ToString();
        BoxPp3Box.Text = info.Pp[2].ToString();
        BoxPp4Box.Text = info.Pp[3].ToString();
        SetPpBonusChoices(info.PpBonuses, info.Moves, info.Pp, BoxPpUp1Box, BoxPpUp2Box, BoxPpUp3Box, BoxPpUp4Box);
        BoxIvHpBox.Text = info.Ivs["hp"].ToString();
        BoxIvAtkBox.Text = info.Ivs["atk"].ToString();
        BoxIvDefBox.Text = info.Ivs["def"].ToString();
        BoxIvSpeBox.Text = info.Ivs["spe"].ToString();
        BoxIvSpaBox.Text = info.Ivs["spa"].ToString();
        BoxIvSpdBox.Text = info.Ivs["spd"].ToString();
        BoxEvHpBox.Text = info.Evs[0].ToString();
        BoxEvAtkBox.Text = info.Evs[1].ToString();
        BoxEvDefBox.Text = info.Evs[2].ToString();
        BoxEvSpeBox.Text = info.Evs[3].ToString();
        BoxEvSpaBox.Text = info.Evs[4].ToString();
        BoxEvSpdBox.Text = info.Evs[5].ToString();
        UpdateBoxStatTexts(info);
        UpdateBoxNameText();
    }

    private void OnBoxGridSlotClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BoxGridCell { Row: { } row } })
            BoxList.SelectedItem = row;
    }

    private void OnBoxGridSlotContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { Tag: BoxGridCell { Row: { } row } } button) return;
        BoxList.SelectedItem = row;
        var live = _dataSourceMode == DataSourceMode.Live;
        var canWrite = CanWriteBoxesInCurrentMode() && row.Slot <= WritableBoxSlotCount(live);

        var deleteItem = new MenuItem
        {
            Header = "删除",
            IsEnabled = canWrite
        };
        deleteItem.Click += async (_, _) => await DeleteBoxRowAsync(row);

        var destinationItems = Enumerable.Range(1, WritableBoxCount(live))
            .Select(boxNumber =>
            {
                var item = new MenuItem
                {
                    Header = boxNumber == (row.Slot - 1) / _profile.Memory.PcBoxSlots + 1
                        ? $"箱{boxNumber:00}（当前）"
                        : $"箱{boxNumber:00}",
                    IsEnabled = canWrite
                };
                item.Click += async (_, _) => await MoveBoxSlotToBoxAsync(row.Slot, boxNumber);
                return item;
            })
            .ToArray();
        var moveItem = new MenuItem
        {
            Header = "移动到箱子",
            IsEnabled = canWrite,
            ItemsSource = destinationItems
        };
        var menu = new ContextMenu
        {
            ItemsSource = new object[] { deleteItem, moveItem }
        };
        button.ContextMenu = menu;
        e.Handled = true;
        menu.Open(button);
    }

    private async Task MoveBoxSlotToBoxAsync(int sourceSlot, int targetBox)
    {
        var live = _dataSourceMode == DataSourceMode.Live;
        if (!CanWriteBoxesInCurrentMode() || targetBox < 1 || targetBox > WritableBoxCount(live))
        {
            ShowToast("目标箱子不在当前 Profile 的安全写入范围。", success: false);
            return;
        }

        var targetSlot = 0;
        var checkedSuccessfully = false;
        await RunUiTask("检查目标箱子", () =>
        {
            IReadOnlyList<BoxImportSlot> slots;
            if (live)
            {
                using var bridge = ConnectBridge();
                slots = ReadLiveBoxImportSlots(bridge);
            }
            else
            {
                slots = BuildSaveBoxImportSlots();
            }
            targetSlot = slots
                .Where(slot => slot.IsEmpty && BoxNumberForSlot(slot) == targetBox)
                .Select(slot => slot.GlobalSlot)
                .FirstOrDefault();
            checkedSuccessfully = true;
        });
        if (!checkedSuccessfully) return;
        if (targetSlot == 0)
        {
            ShowToast($"箱{targetBox:00}已满，无法移动。", success: false);
            return;
        }
        await MoveOrSwapBoxSlotAsync(sourceSlot, targetSlot);
    }

    private void OnBoxGridSlotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_boxDragInProgress || _boxDropInProgress ||
            FindBoxGridButton(e.Source) is not { Tag: BoxGridCell { Row: not null, IsWritable: true } cell } button)
            return;
        var point = e.GetCurrentPoint(BoxGridGroupsView);
        if (!point.Properties.IsLeftButtonPressed) return;
        _boxDragStartPoint = point.Position;
        _boxDragSourceSlot = cell.GlobalSlot;
        _boxDragSourceButton = button;
        _boxDragSourceSprite = cell.Row?.Sprite;
    }

    private void OnBoxGridSlotPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_boxDropInProgress || _boxDragStartPoint is not { } start || _boxDragSourceSlot is null)
            return;

        var point = e.GetCurrentPoint(BoxGridGroupsView);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetBoxDragState();
            return;
        }
        if (_boxDragInProgress)
        {
            UpdateBoxDragPreview(e.GetCurrentPoint(BoxDragPreviewLayer).Position);
            e.Handled = true;
            return;
        }
        var delta = point.Position - start;
        if (Math.Abs(delta.X) < 5 && Math.Abs(delta.Y) < 5) return;

        _boxDragInProgress = true;
        if (_boxDragSourceButton is not null)
            _boxDragSourceButton.Opacity = 0.55;
        BoxDragPreviewImage.Source = _boxDragSourceSprite;
        BoxDragPreviewBorder.IsVisible = true;
        UpdateBoxDragPreview(e.GetCurrentPoint(BoxDragPreviewLayer).Position);
        e.Handled = true;
    }

    private async void OnBoxGridSlotPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_boxDragInProgress || _boxDragSourceSlot is not { } sourceSlot)
        {
            ResetBoxDragState();
            return;
        }

        var point = e.GetCurrentPoint(BoxGridGroupsView);
        var targetButton = FindBoxGridButton(BoxGridGroupsView.InputHitTest(point.Position));
        var targetSlot = targetButton?.Tag is BoxGridCell { IsWritable: true } target
            ? target.GlobalSlot
            : 0;
        ResetBoxDragState();
        e.Handled = true;
        if (targetSlot != 0 && targetSlot != sourceSlot)
            await MoveOrSwapBoxSlotAsync(sourceSlot, targetSlot);
    }

    private static Button? FindBoxGridButton(object? eventSource)
    {
        if (eventSource is Button button) return button;
        return eventSource is Visual visual
            ? visual.GetVisualAncestors().OfType<Button>().FirstOrDefault(candidate => candidate.Tag is BoxGridCell)
            : null;
    }

    private void UpdateBoxDragPreview(Point pointerPosition)
    {
        Canvas.SetLeft(BoxDragPreviewBorder, pointerPosition.X + 12);
        Canvas.SetTop(BoxDragPreviewBorder, pointerPosition.Y + 12);
    }

    private void ResetBoxDragState()
    {
        if (_boxDragSourceButton is not null)
            _boxDragSourceButton.Opacity = 1;
        _boxDragStartPoint = null;
        _boxDragSourceSlot = null;
        _boxDragSourceButton = null;
        _boxDragSourceSprite = null;
        BoxDragPreviewImage.Source = null;
        BoxDragPreviewBorder.IsVisible = false;
        _boxDragInProgress = false;
    }

    private async Task MoveOrSwapBoxSlotAsync(int sourceSlot, int targetSlot)
    {
        if (_boxDropInProgress) return;
        var live = _dataSourceMode == DataSourceMode.Live;
        if (!CanWriteBoxesInCurrentMode())
        {
            ShowToast("当前 Profile 或当前存档未开放箱子写入，不能拖动位置。", success: false);
            return;
        }
        var writableSlots = WritableBoxSlotCount(live);
        if (sourceSlot < 1 || sourceSlot > writableSlots || targetSlot < 1 || targetSlot > writableSlots)
        {
            ShowToast("源槽或目标槽不在当前 Profile 的安全写入范围。", success: false);
            return;
        }
        if (_boxRows.FirstOrDefault(row => row.Slot == sourceSlot) is not { } sourceRow)
        {
            ShowToast("拖动源槽已经变空，请重新读取箱子。", success: false);
            return;
        }

        _boxDropInProgress = true;
        try
        {
            await RunUiTask("调整箱子位置", () =>
            {
                var targetRow = _boxRows.FirstOrDefault(row => row.Slot == targetSlot);
                if (live)
                    MoveOrSwapLiveBoxSlot(sourceRow, targetRow, targetSlot);
                else
                    MoveOrSwapSaveBoxSlot(sourceRow, targetRow, targetSlot);
            });
        }
        finally
        {
            _boxDropInProgress = false;
        }
    }

    private void MoveOrSwapSaveBoxSlot(BoxSlotRow sourceRow, BoxSlotRow? targetRow, int targetSlot)
    {
        var document = _loadedSave ?? throw new InvalidOperationException("当前没有已读取的存档。");
        var sourceMon = CloneBoxPokemon(sourceRow.Mon);
        var targetMon = targetRow is null ? null : CloneBoxPokemon(targetRow.Mon);
        try
        {
            document.ReplaceBoxPokemon(targetSlot, sourceMon);
            if (targetMon is null)
                document.ClearBoxPokemon(sourceRow.Slot);
            else
                document.ReplaceBoxPokemon(sourceRow.Slot, targetMon);
        }
        catch (Exception writeError)
        {
            try
            {
                document.ReplaceBoxPokemon(sourceRow.Slot, sourceMon);
                if (targetMon is null)
                    document.ClearBoxPokemon(targetSlot);
                else
                    document.ReplaceBoxPokemon(targetSlot, targetMon);
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException(
                    "存档箱子位置调整失败，且待保存副本回滚失败。请放弃当前副本并重新读取原存档。",
                    new AggregateException(writeError, rollbackError));
            }
            throw new InvalidOperationException("存档箱子位置调整失败，已恢复原槽位。", writeError);
        }

        var targetAddress = targetRow?.Address ?? SaveBoxDisplayAddress(targetSlot);
        ReplaceBoxRowsAfterMove(sourceRow, targetRow, targetSlot, targetAddress, sourceMon, targetMon, "存档待保存副本");
        UpdateSaveButtons();
        var action = targetMon is null ? "移动" : "交换";
        SetWriteNotice($"已在待保存存档中{action}：{BoxSlotLabel(sourceRow.Slot)} → {BoxSlotLabel(targetSlot)}。完成后请使用“保存修改并备份”。");
    }

    private void MoveOrSwapLiveBoxSlot(BoxSlotRow sourceRow, BoxSlotRow? targetRow, int targetSlot)
    {
        using var bridge = ConnectBridge();
        EnsureBoxStorageBaseFresh(bridge);
        var sourceAddress = ResolveLiveBoxSlotAddress(bridge, sourceRow.Slot);
        var targetAddress = ResolveLiveBoxSlotAddress(bridge, targetSlot);
        if (sourceRow.Address != sourceAddress || targetRow is not null && targetRow.Address != targetAddress)
        {
            LoadBoxRows(bridge);
            throw new InvalidOperationException("箱子槽地址已经变化，已刷新箱子列表，请重新拖动。");
        }

        var sourceRaw = bridge.Read(sourceAddress, ActiveBoxRecordSize);
        var targetRaw = bridge.Read(targetAddress, ActiveBoxRecordSize);
        if (!sourceRaw.AsSpan().SequenceEqual(sourceRow.Mon.Raw))
        {
            LoadBoxRows(bridge);
            throw new InvalidOperationException("拖动源槽已经变化，已刷新箱子列表，请重新拖动。");
        }
        if (targetRow is null)
        {
            if (!IsAllZero(targetRaw))
            {
                LoadBoxRows(bridge);
                throw new InvalidOperationException("目标空槽已经出现宝可梦，已刷新箱子列表，请重新拖动。");
            }
        }
        else if (!targetRaw.AsSpan().SequenceEqual(targetRow.Mon.Raw))
        {
            LoadBoxRows(bridge);
            throw new InvalidOperationException("拖动目标槽已经变化，已刷新箱子列表，请重新拖动。");
        }

        try
        {
            WriteLiveBoxRange(bridge, targetAddress, sourceRaw);
            WriteLiveBoxRange(bridge, sourceAddress, targetRaw);
            if (!bridge.Read(targetAddress, ActiveBoxRecordSize).AsSpan().SequenceEqual(sourceRaw) ||
                !bridge.Read(sourceAddress, ActiveBoxRecordSize).AsSpan().SequenceEqual(targetRaw))
                throw new InvalidOperationException("两个箱子槽的最终读回结果不一致。");
        }
        catch (Exception writeError)
        {
            var rollbackErrors = new List<Exception>();
            try { WriteLiveBoxRange(bridge, sourceAddress, sourceRaw); }
            catch (Exception ex) { rollbackErrors.Add(ex); }
            try { WriteLiveBoxRange(bridge, targetAddress, targetRaw); }
            catch (Exception ex) { rollbackErrors.Add(ex); }
            if (rollbackErrors.Count > 0)
                throw new InvalidOperationException(
                    "实时箱子位置调整失败，且至少一个槽位回滚失败。请立即载入操作前的 mGBA 即时存档。",
                    new AggregateException([writeError, .. rollbackErrors]));
            throw new InvalidOperationException("实时箱子位置调整失败，两个槽位已恢复原数据。", writeError);
        }

        var action = targetRow is null ? "移动" : "交换";
        LoadBoxRows(bridge);
        BoxList.SelectedItem = _boxRows.FirstOrDefault(row => row.Slot == targetSlot) ?? BoxList.SelectedItem;
        SetWriteNotice($"箱子精灵{action}成功：{BoxSlotLabel(sourceRow.Slot)} → {BoxSlotLabel(targetSlot)}。请回游戏确认后手动保存。");
    }

    private void ReplaceBoxRowsAfterMove(
        BoxSlotRow sourceRow,
        BoxSlotRow? targetRow,
        int targetSlot,
        uint targetAddress,
        BoxPokemon sourceMon,
        BoxPokemon? targetMon,
        string detail)
    {
        foreach (var row in _boxRows.Where(row => row.Slot == sourceRow.Slot || row.Slot == targetSlot).ToArray())
            _boxRows.Remove(row);
        var movedSource = CreateBoxSlotRow(targetSlot, targetAddress, sourceMon, detail);
        _boxRows.Add(movedSource);
        if (targetMon is not null)
            _boxRows.Add(CreateBoxSlotRow(sourceRow.Slot, sourceRow.Address, targetMon, detail));
        RefreshBoxGridGroups();
        BoxList.SelectedItem = movedSource;
    }

    private BoxSlotRow CreateBoxSlotRow(int globalSlot, uint address, BoxPokemon mon, string detail)
    {
        var copy = CloneBoxPokemon(mon);
        var info = copy.GetInfo();
        var boxNumber = (globalSlot - 1) / _profile.Memory.PcBoxSlots + 1;
        var slotInBox = (globalSlot - 1) % _profile.Memory.PcBoxSlots + 1;
        var title = BoxDisplayTitle(boxNumber, slotInBox, copy, info);
        if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
        return new BoxSlotRow(globalSlot, address, copy, info, title, detail, BoxDisplaySprite(info));
    }

    private static BoxPokemon CloneBoxPokemon(BoxPokemon mon)
        => new(mon.Raw, mon.Layout);

    private uint SaveBoxDisplayAddress(int globalSlot)
        => (uint)(_profile.Memory.PcBoxDataOffset + (globalSlot - 1) * ActiveBoxRecordSize);

    private string BoxSlotLabel(int globalSlot)
    {
        var boxNumber = (globalSlot - 1) / _profile.Memory.PcBoxSlots + 1;
        var slotInBox = (globalSlot - 1) % _profile.Memory.PcBoxSlots + 1;
        return $"箱{boxNumber:00}-{slotInBox:00}";
    }

    private uint ResolveLiveBoxSlotAddress(MgbaBridgeClient bridge, int globalSlot)
    {
        if (globalSlot < 1 || globalSlot > WritableBoxSlotCount(live: true))
            throw new ArgumentOutOfRangeException(nameof(globalSlot), "箱子槽不在当前 Profile 的实时可写范围内。");
        if (_profile.Memory.PcBoxRegions.Count > 0)
            return ProfileBoxAddress(globalSlot);
        var recordsBase = _profile.Memory.PcBoxStoragePointerAddress != 0
            ? ResolvePointerBoxRecordsBase(bridge)
            : _boxBase ?? throw new InvalidOperationException("尚未定位当前箱子存储。");
        return recordsBase + checked((uint)((globalSlot - 1) * ActiveBoxRecordSize));
    }

    private async void OnBoxApplyClicked(object? sender, RoutedEventArgs e)
    {
        if (BoxList.SelectedItem is not BoxSlotRow row)
        {
            ShowToast("请先选择一个箱子槽。", success: false);
            return;
        }
        if (!TryReadBoxEvTotal("写入箱子", out var pendingEvTotal)) return;
        if (!await ConfirmHighEvTotalAsync(pendingEvTotal)) return;

        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            await RunUiTask("更新存档箱子", () =>
            {
                var document = _loadedSave ?? throw new InvalidOperationException("当前没有已读取的存档。");
                var mon = new BoxPokemon(row.Mon.Raw, row.Mon.Layout);
                var (species, evTotal) = ApplyBoxEditor(mon);
                document.ReplaceBoxPokemon(row.Slot, mon);
                var info = mon.GetInfo();
                var boxNumber = (row.Slot - 1) / _profile.Memory.PcBoxSlots + 1;
                var slotInBox = (row.Slot - 1) % _profile.Memory.PcBoxSlots + 1;
                var title = BoxDisplayTitle(boxNumber, slotInBox, mon, info);
                if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
                var updated = new BoxSlotRow(row.Slot, row.Address, mon, info, title, "存档待保存副本", BoxDisplaySprite(info));
                var index = _boxRows.IndexOf(row);
                if (index >= 0) _boxRows[index] = updated;
                RefreshBoxGridGroups();
                BoxList.SelectedItem = updated;
                UpdateSaveButtons();
                SetWriteNotice(
                    evTotal > 510
                        ? $"存档箱{boxNumber:00}-{slotInBox:00}已更新为 {SpeciesName(species)}，等待保存。警告：努力值总和已超过限制。"
                        : $"存档箱{boxNumber:00}-{slotInBox:00}已更新为 {SpeciesName(species)}，等待保存。",
                    success: evTotal <= 510);
            });
            return;
        }

        await RunUiTask("写入箱子", () =>
        {
            using var bridge = ConnectBridge();
            var mon = ReadSelectedBoxMon(bridge, row);
            var (species, evTotal) = ApplyBoxEditor(mon);
            WriteLiveBoxRange(bridge, row.Address, mon.Raw);
            SetWriteNotice(
                evTotal > 510
                    ? $"箱子槽写入成功：{SpeciesName(species)}。警告：努力值总和已超过限制，可能发生未知错误或坏档。"
                    : $"箱子槽写入成功：{SpeciesName(species)}。",
                success: evTotal <= 510);
            LoadBoxRows(bridge);
            RefreshPartyRowsIfPossible(bridge);
            BoxList.SelectedItem = _boxRows.FirstOrDefault(r => r.Address == row.Address) ?? BoxList.SelectedItem;
        });
    }

    private (ushort Species, int EvTotal) ApplyBoxEditor(BoxPokemon mon)
    {
        var before = mon.GetInfo();
        var originalOtName = PokemonOtName(mon.Raw);
        var species = ParseSpeciesRequired(BoxSpeciesBox, "箱子宝可梦");
        var experience = ResolveBoxExperience(before, species);
        SyncNicknameForSpeciesChange(mon, species);
        ApplyEditedOtName(mon, BoxOtNameBox.Text, originalOtName);
        var item = ParseItemRequired(BoxItemBox, "箱子携带道具");
        mon.SetGrowth(
            species: species,
            item: item,
            exp: experience,
            friendship: ParseBoxFriendshipOrHatchCounter(before),
            ppBonuses: BuildPpBonuses(BoxPpUp1Box, BoxPpUp2Box, BoxPpUp3Box, BoxPpUp4Box));
        if (SelectedChoiceId(BoxNatureBox) is { } nature)
            mon.SetNature(nature);
        mon.SetShiny(BoxShinyBox.IsChecked == true);
        ApplyGenderChoice(mon, species);
        if (SelectedChoiceId(BoxAbilityBox) is { } ability)
            mon.SetAbilitySlot(ability, ReadSpeciesStats(species).GenderRatio);
        var ivs = BuildIntStats(BoxIvHpBox, BoxIvAtkBox, BoxIvDefBox, BoxIvSpeBox, BoxIvSpaBox, BoxIvSpdBox);
        foreach (var value in ivs.Values)
        {
            if (value is < 0 or > 31) throw new InvalidOperationException("个体值必须在 0..31 之间。");
        }
        var evs = BuildByteStats(BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpeBox, BoxEvSpaBox, BoxEvSpdBox);
        mon.SetIvs(ivs);
        mon.SetEvs(evs);
        mon.SetMoves(
            [
                ParseMoveOrNull(BoxMove1Box),
                ParseMoveOrNull(BoxMove2Box),
                ParseMoveOrNull(BoxMove3Box),
                ParseMoveOrNull(BoxMove4Box)
            ],
            [
                ParseByteOrNull(BoxPp1Box.Text),
                ParseByteOrNull(BoxPp2Box.Text),
                ParseByteOrNull(BoxPp3Box.Text),
                ParseByteOrNull(BoxPp4Box.Text)
            ]);
        return (species, evs.Values.Sum(x => x));
    }

    private async void OnBoxDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (BoxList.SelectedItem is not BoxSlotRow row)
        {
            ShowToast("请先选择一个箱子槽。", success: false);
            return;
        }
        await DeleteBoxRowAsync(row);
    }

    private async Task DeleteBoxRowAsync(BoxSlotRow row)
    {
        var liveMode = _dataSourceMode == DataSourceMode.Live;
        if (!CanWriteBoxesInCurrentMode() || row.Slot > WritableBoxSlotCount(liveMode))
        {
            ShowToast("当前 Profile 或当前存档未开放这个箱子槽的删除权限。", success: false);
            return;
        }
        if (!await ConfirmBoxDeleteAsync(row)) return;

        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            await RunUiTask("删除存档箱子精灵", () =>
            {
                var document = _loadedSave ?? throw new InvalidOperationException("当前没有已读取的存档。");
                document.ClearBoxPokemon(row.Slot);
                var oldIndex = BoxList.SelectedIndex;
                _boxRows.Remove(row);
                RefreshBoxGridGroups();
                BoxList.SelectedIndex = _boxRows.Count == 0 ? -1 : Math.Clamp(oldIndex, 0, _boxRows.Count - 1);
                UpdateSaveButtons();
                SetWriteNotice($"存档箱子槽已清空：{row.Title}，等待保存。");
                ShowToast("存档箱子精灵已删除，等待保存。", success: true);
            });
            return;
        }

        await RunUiTask("删除箱子精灵", () =>
        {
            using var bridge = ConnectBridge();
            var live = new BoxPokemon(bridge.Read(row.Address, ActiveBoxRecordSize), ActiveBoxLayout);
            if (live.IsEmpty)
                throw new InvalidOperationException("该箱子槽已经是空的。");
            if (!SamePokemonIdentity(row.Mon, live))
            {
                RefreshBoxAndSelectMovedMon(bridge, row);
                throw new InvalidOperationException($"箱子槽里的宝可梦已经变化。{MovedBoxHint(row)}");
            }

            WriteLiveBoxRange(bridge, row.Address, new byte[ActiveBoxRecordSize]);
            SetWriteNotice($"箱子槽已删除：{row.Title}。请回游戏确认后手动保存。");
            ShowToast("箱子精灵删除成功。", success: true);

            var oldIndex = BoxList.SelectedIndex;
            _boxRows.Remove(row);
            BoxList.SelectedIndex = _boxRows.Count == 0 ? -1 : Math.Clamp(oldIndex, 0, _boxRows.Count - 1);
        });
    }

    private async void OnDexImportPartyClicked(object? sender, RoutedEventArgs e)
    {
        var saveMode = _dataSourceMode == DataSourceMode.SaveFile;
        if (saveMode ? !_profile.Features.ImportToSaveParty : !_profile.Features.ImportToParty)
        {
            ShowToast("当前 Profile 未启用导入队伍。", success: false);
            return;
        }
        if (DexSpeciesList.SelectedItem is not DexSpeciesRow row)
        {
            ShowToast("请先在图鉴中选择一个宝可梦。", success: false);
            return;
        }

        await RunUiTask("导入队伍", () =>
        {
            if (saveMode)
            {
                var document = _loadedSave ?? throw new InvalidOperationException("当前没有已读取的存档。");
                var saveTrainer = ResolveSaveImportTrainerIdentity(document);
                var saveMon = BuildDexPartyPokemon(row.Id, saveTrainer);
                var saveTargetSlot = document.AppendPartyPokemon(saveMon);
                var updated = ToRow(saveTargetSlot, (uint)document.PartySaveOffset(saveTargetSlot), saveMon);
                _partyRows.Add(updated);
                PartyList.SelectedItem = updated;
                UpdateSaveButtons();
                SetWriteNotice($"已从图鉴导入到存档队伍槽位 {saveTargetSlot}：{row.DisplayName}。完成后请使用“保存修改并备份”。");
                return;
            }

            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var count = ReadLivePartyCount(bridge, baseAddr)
                        ?? throw new InvalidOperationException("没有读到当前队伍数量，请先读取队伍。");
            if (count >= Gen3Constants.PartySlots)
                throw new InvalidOperationException("队伍已满，无法从图鉴导入。");

            var trainer = ResolveImportTrainerIdentity(bridge);
            var mon = BuildDexPartyPokemon(row.Id, trainer);
            var targetSlot = count + 1;
            var targetAddress = SlotAddress(baseAddr, targetSlot);
            WriteLivePartyRange(bridge, targetAddress, mon.Raw);
            WritePartyCount(bridge, baseAddr, targetSlot);
            LoadPartyRows(bridge, baseAddr, targetSlot);
            SetWriteNotice($"已从图鉴导入到队伍槽位 {targetSlot}：{row.DisplayName}。请回游戏确认后手动保存。");
        });
    }

    private async void OnDexImportBoxClicked(object? sender, RoutedEventArgs e)
    {
        var saveMode = _dataSourceMode == DataSourceMode.SaveFile;
        if (saveMode ? !_profile.Features.ImportToSaveBoxes : !_profile.Features.ImportToBoxes)
        {
            ShowToast("当前 Profile 未启用导入箱子。", success: false);
            return;
        }
        if (DexSpeciesList.SelectedItem is not DexSpeciesRow row)
        {
            ShowToast("请先在图鉴中选择一个宝可梦。", success: false);
            return;
        }

        await RunUiTask("导入箱子", async () =>
        {
            if (saveMode)
            {
                var document = _loadedSave ?? throw new InvalidOperationException("当前没有已读取的存档。");
                var saveTarget = ResolveDexImportBoxTarget(BuildSaveBoxImportSlots());
                if (saveTarget.RequiresFallback)
                {
                    if (!await ConfirmDexImportFallbackBoxAsync(saveTarget.RequestedBox, saveTarget.TargetBox)) return;
                    SetDexImportBoxNumber(saveTarget.TargetBox, manuallySelected: true);
                }

                var saveTrainer = ResolveSaveImportTrainerIdentity(document);
                var saveMon = BuildDexBoxPokemon(row.Id, saveTrainer);
                document.ReplaceBoxPokemon(saveTarget.Slot.GlobalSlot, saveMon);
                var saveInfo = saveMon.GetInfo();
                var saveBoxNumber = saveTarget.TargetBox;
                var saveSlotInBox = (saveTarget.Slot.GlobalSlot - 1) % _profile.Memory.PcBoxSlots + 1;
                var title = BoxDisplayTitle(saveBoxNumber, saveSlotInBox, saveMon, saveInfo);
                var updated = new BoxSlotRow(
                    saveTarget.Slot.GlobalSlot,
                    (uint)(_profile.Memory.PcBoxDataOffset + (saveTarget.Slot.GlobalSlot - 1) * ActiveBoxRecordSize),
                    saveMon,
                    saveInfo,
                    title,
                    "存档待保存副本",
                    BoxDisplaySprite(saveInfo));
                _boxRows.Add(updated);
                RefreshBoxGridGroups();
                BoxList.SelectedItem = updated;
                UpdateSaveButtons();
                SetWriteNotice($"已从图鉴导入到存档箱{saveBoxNumber:00}-{saveSlotInBox:00}：{row.DisplayName}。完成后请使用“保存修改并备份”。");
                return;
            }

            using var bridge = ConnectBridge();
            var liveTarget = ResolveDexImportBoxTarget(ReadLiveBoxImportSlots(bridge));
            if (liveTarget.RequiresFallback)
            {
                if (!await ConfirmDexImportFallbackBoxAsync(liveTarget.RequestedBox, liveTarget.TargetBox)) return;
                SetDexImportBoxNumber(liveTarget.TargetBox, manuallySelected: true);
            }

            var trainer = ResolveImportTrainerIdentity(bridge);
            var mon = BuildDexBoxPokemon(row.Id, trainer);
            if (!IsAllZero(bridge.Read(liveTarget.Slot.Address, ActiveBoxRecordSize)))
                throw new InvalidOperationException("目标箱子槽在导入前已经发生变化，已拒绝覆盖。请重新读取箱子后再试。");
            WriteLiveBoxRange(bridge, liveTarget.Slot.Address, mon.Raw);
            LoadBoxRows(bridge);
            BoxList.SelectedItem = _boxRows.FirstOrDefault(r => r.Address == liveTarget.Slot.Address) ?? BoxList.SelectedItem;
            var slotInBox = (liveTarget.Slot.GlobalSlot - 1) % _profile.Memory.PcBoxSlots + 1;
            SetWriteNotice($"已从图鉴导入到箱{liveTarget.TargetBox:00}-{slotInBox:00}：{row.DisplayName}。请回游戏确认后手动保存。");
        });
    }

    private sealed record BoxImportSlot(int GlobalSlot, uint Address, bool IsEmpty);
    private sealed record DexImportBoxTarget(BoxImportSlot Slot, int RequestedBox, int TargetBox)
    {
        public bool RequiresFallback => RequestedBox != TargetBox;
    }

    private IReadOnlyList<BoxImportSlot> BuildSaveBoxImportSlots()
    {
        var occupied = _boxRows.Select(candidate => candidate.Slot).ToHashSet();
        return Enumerable.Range(1, WritableBoxSlotCount(live: false))
            .Select(slot => new BoxImportSlot(
                slot,
                (uint)(_profile.Memory.PcBoxDataOffset + (slot - 1) * ActiveBoxRecordSize),
                !occupied.Contains(slot)))
            .ToArray();
    }

    private IReadOnlyList<BoxImportSlot> ReadLiveBoxImportSlots(MgbaBridgeClient bridge)
    {
        var ewram = PartyScanner.ReadEwram(bridge, _profile);
        var writableSlotCount = WritableBoxSlotCount(live: true);
        uint? contiguousRecordsBase = null;
        if (_profile.Memory.PcBoxStoragePointerAddress != 0)
        {
            contiguousRecordsBase = ResolvePointerBoxRecordsBase(bridge);
        }
        else if (_profile.Memory.PcBoxRegions.Count == 0)
        {
            var storage = BoxScanner.LocatePcStorage(
                              ewram,
                              _profile.Memory.EwramBase,
                              maxSpecies: _profile.Limits.MaxSpecies,
                              maxBoxes: _profile.Memory.PcBoxCount,
                              slotsPerBox: _profile.Memory.PcBoxSlots,
                              minScore: 12,
                              layout: ActiveBoxLayout)
                          ?? throw new InvalidOperationException("没有定位到完整箱子存储。请先读取箱子，确认箱子列表正常后再导入。");
            contiguousRecordsBase = storage.StartAddress;
        }

        var slots = new List<BoxImportSlot>(writableSlotCount);
        for (var globalSlot = 1; globalSlot <= writableSlotCount; globalSlot++)
        {
            var address = contiguousRecordsBase is { } recordsBase
                ? recordsBase + checked((uint)((globalSlot - 1) * ActiveBoxRecordSize))
                : ProfileBoxAddress(globalSlot);
            var offset = checked((int)(address - _profile.Memory.EwramBase));
            if (offset < 0 || offset + ActiveBoxRecordSize > ewram.Length)
                throw new InvalidOperationException($"箱子槽 {globalSlot} 的地址超出 EWRAM 范围，无法安全导入。");
            slots.Add(new BoxImportSlot(globalSlot, address, IsAllZero(ewram.AsSpan(offset, ActiveBoxRecordSize))));
        }
        return slots;
    }

    private DexImportBoxTarget ResolveDexImportBoxTarget(IReadOnlyList<BoxImportSlot> slots)
    {
        var availableBoxes = slots
            .Where(slot => slot.IsEmpty)
            .Select(BoxNumberForSlot)
            .Distinct()
            .OrderBy(box => box)
            .ToArray();
        if (availableBoxes.Length == 0)
            throw new InvalidOperationException("所有已验证的可写箱子都已满，无法从图鉴导入。");

        var selectedBox = SelectedChoiceId(DexImportBoxNumberBox) ?? availableBoxes[0];
        if (!_dexImportBoxManuallySelected)
        {
            selectedBox = availableBoxes[0];
            SetDexImportBoxNumber(selectedBox, manuallySelected: false);
        }

        var selectedTarget = slots.FirstOrDefault(slot => slot.IsEmpty && BoxNumberForSlot(slot) == selectedBox);
        if (selectedTarget is not null)
            return new DexImportBoxTarget(selectedTarget, selectedBox, selectedBox);

        var availableSet = availableBoxes.ToHashSet();
        var writableBoxCount = WritableBoxCount(_dataSourceMode == DataSourceMode.Live);
        var fallbackBox = Enumerable.Range(1, writableBoxCount)
            .Select(offset => (selectedBox - 1 + offset) % writableBoxCount + 1)
            .First(box => availableSet.Contains(box));
        var fallbackTarget = slots.First(slot => slot.IsEmpty && BoxNumberForSlot(slot) == fallbackBox);
        return new DexImportBoxTarget(fallbackTarget, selectedBox, fallbackBox);
    }

    private int BoxNumberForSlot(BoxImportSlot slot)
        => (slot.GlobalSlot - 1) / _profile.Memory.PcBoxSlots + 1;

    private PartyPokemon BuildDexPartyPokemon(int species, PlayerTrainerIdentity trainer)
    {
        var stats = ReadSpeciesStats(species);
        var mon = PartyPokemon.Create(NewNonShinyPid(trainer.OtId), trainer.OtId, ActivePokemonLayout);
        mon.SetOtName(trainer.OtName);
        ApplyDexImportDefaults(mon, species, stats);
        mon.SetUnencrypted(status: 0, level: DexImportLevel);
        mon.RecalculateStats(stats);
        return mon;
    }

    private BoxPokemon BuildDexBoxPokemon(int species, PlayerTrainerIdentity trainer)
    {
        var stats = ReadSpeciesStats(species);
        var mon = BoxPokemon.Create(NewNonShinyPid(trainer.OtId), trainer.OtId, ActiveBoxLayout);
        mon.SetOtName(trainer.OtName);
        ApplyDexImportDefaults(mon, species, stats);
        return mon;
    }

    private void ApplyDexImportDefaults(PartyPokemon mon, int species, SpeciesStats stats)
    {
        var (moves, pp) = DexImportMoves(species);
        mon.SetNicknameFromSpeciesNameEntry(SpeciesNameEntryBytes(species));
        mon.SetGrowth((ushort)species, item: 0, ExperienceForLevel(DexImportLevel, stats.GrowthRate), stats.Friendship, ppBonuses: 0);
        mon.SetGameNatureCode(NatureCodeUsePid);
        mon.SetMoves(moves.Select(m => (ushort?)m).ToArray(), pp.Select(x => (byte?)x).ToArray());
        mon.SetIvs(DefaultImportIvs());
        mon.SetEvs(DefaultImportEvs());
    }

    private void ApplyDexImportDefaults(BoxPokemon mon, int species, SpeciesStats stats)
    {
        var (moves, pp) = DexImportMoves(species);
        mon.SetNicknameFromSpeciesNameEntry(SpeciesNameEntryBytes(species));
        mon.SetGrowth((ushort)species, item: 0, ExperienceForLevel(DexImportLevel, stats.GrowthRate), stats.Friendship, ppBonuses: 0);
        mon.SetGameNatureCode(NatureCodeUsePid);
        mon.SetMoves(moves.Select(m => (ushort?)m).ToArray(), pp.Select(x => (byte?)x).ToArray());
        mon.SetIvs(DefaultImportIvs());
        mon.SetEvs(DefaultImportEvs());
    }

    private (ushort[] Moves, byte[] Pp) DexImportMoves(int species)
    {
        var selected = new List<ushort>();
        foreach (var entry in ReadSpeciesLevelMoves(species)
                     .Where(m => m.Level <= DexImportLevel && m.Move != 0)
                     .OrderBy(m => m.Level))
        {
            selected.Remove(entry.Move);
            selected.Add(entry.Move);
            if (selected.Count > 4) selected.RemoveAt(0);
        }

        var moves = new ushort[4];
        var pp = new byte[4];
        for (var i = 0; i < selected.Count; i++)
        {
            moves[i] = selected[i];
            pp[i] = (byte)Math.Clamp(CalculateMaxPp(ReadMoveData(selected[i]).Pp, 0), 0, byte.MaxValue);
        }
        return (moves, pp);
    }

    private static Dictionary<string, int> DefaultImportIvs() => new()
    {
        ["hp"] = 31,
        ["atk"] = 31,
        ["def"] = 31,
        ["spe"] = 31,
        ["spa"] = 31,
        ["spd"] = 31,
        ["ability"] = 0
    };

    private static Dictionary<string, byte> DefaultImportEvs() => new()
    {
        ["hp"] = 0,
        ["atk"] = 0,
        ["def"] = 0,
        ["spe"] = 0,
        ["spa"] = 0,
        ["spd"] = 0
    };

    private PlayerTrainerIdentity ResolveImportTrainerIdentity(MgbaBridgeClient bridge)
    {
        var trainer = ReadTrainerInfo(bridge);
        return new PlayerTrainerIdentity(trainer.OtId, trainer.NameBytes);
    }

    private static PlayerTrainerIdentity ResolveSaveImportTrainerIdentity(Gen3SaveDocument document)
    {
        var trainer = document.Snapshot.Trainer
                      ?? throw new InvalidOperationException("存档中没有已验证的玩家名字和 OT ID，不能新建宝可梦。");
        return new PlayerTrainerIdentity(trainer.OtId, trainer.NameBytes);
    }

    private PlayerTrainerIdentity? TryReadCurrentPlayerIdentity(MgbaBridgeClient bridge)
    {
        try
        {
            var pointerRaw = bridge.Read(_profile.Memory.SaveBlock2PointerAddress, 4);
            var saveBlock2 = ReadU32Le(pointerRaw, 0);
            if (!IsEwramRange(saveBlock2, _profile.Memory.SaveBlock2HeaderLength)) return null;

            var header = bridge.Read(saveBlock2, _profile.Memory.SaveBlock2HeaderLength);
            if (!LooksLikePlayerSaveBlock2Header(header)) return null;
            var otName = CopyGameTextBuffer(header, _profile.Memory.PlayerNameLength);
            return new PlayerTrainerIdentity(ReadU32Le(header, _profile.Memory.SaveBlock2PlayerOtIdOffset), otName);
        }
        catch
        {
            return null;
        }
    }

    private TrainerInfo ReadTrainerInfo(MgbaBridgeClient bridge)
    {
        var pointerRaw = bridge.Read(_profile.Memory.SaveBlock2PointerAddress, 4);
        var saveBlock2 = ReadU32Le(pointerRaw, 0);
        if (!IsEwramRange(saveBlock2, _profile.Memory.SaveBlock2HeaderLength))
            throw new InvalidOperationException("没有读到有效的 SaveBlock2 指针。请确认游戏已经载入存档。");

        var header = bridge.Read(saveBlock2, _profile.Memory.SaveBlock2HeaderLength);
        if (!LooksLikePlayerSaveBlock2Header(header))
            throw new InvalidOperationException("SaveBlock2 头部不像玩家数据，当前状态暂不安全读取训练家信息。");

        var nameBytes = CopyGameTextBuffer(header, _profile.Memory.PlayerNameLength);
        var otId = ReadU32Le(header, _profile.Memory.SaveBlock2PlayerOtIdOffset);
        return new TrainerInfo(
            saveBlock2,
            nameBytes,
            DecodeGameTextBuffer(nameBytes),
            otId,
            (ushort)(otId & 0xFFFF),
            (ushort)(otId >> 16),
            "实时 mGBA / SaveBlock2");
    }

    private void FillTrainerInfo(TrainerInfo info)
    {
        TrainerNameBox.Text = info.Name;
        TrainerSourceText.Text = info.Source;
        TrainerPublicIdText.Text = info.TrainerId.ToString();
        TrainerSecretIdText.Text = info.SecretId.ToString();
        TrainerOtIdText.Text = info.OtId.ToString();
        TrainerNameEncodingText.Text = NameEncodingStatus(info.NameBytes);
        TrainerStatusText.Text = "已读取训练家信息。修改名字前建议保存 mGBA 即时存档。";
    }

    private TrainerInfo ToTrainerInfo(byte[] nameBytes, uint otId, string source)
        => new(
            0,
            nameBytes.ToArray(),
            DecodeGameTextBuffer(nameBytes),
            otId,
            (ushort)(otId & 0xFFFF),
            (ushort)(otId >> 16),
            source);

    private void FillSaveTrainerInfo()
    {
        var trainer = _loadedSave?.Snapshot.Trainer
                      ?? throw new InvalidOperationException("请先读取一个包含已验证训练家字段的存档。");
        FillTrainerInfo(ToTrainerInfo(trainer.NameBytes, trainer.OtId, "存档待保存副本 / SaveBlock2"));
        TrainerStatusText.Text = "已读取存档训练家信息；修改会先进入待保存副本。";
        TrainerMoneyCurrentBox.Text = trainer.Money.ToString();
        TrainerMoneyStatusText.Text = $"已读取存档金钱：{trainer.Money}。";
    }

    private void FillTrainerMoney(MgbaBridgeClient bridge)
    {
        var money = ReadTrainerMoney(bridge);
        TrainerMoneyCurrentBox.Text = money.ToString();
        TrainerMoneyStatusText.Text = $"已读取当前金钱：{money}。";
    }

    private int ReadTrainerMoney(MgbaBridgeClient bridge)
    {
        var (address, key) = ResolveTrainerMoneyField(bridge);
        var encrypted = ReadU32Le(bridge.Read(address, 4), 0);
        var money = encrypted ^ key;
        if (money > _profile.Runtime.MaxTrainerMoney)
            throw new InvalidOperationException($"金钱字段解密后为 {money}，超出 0..{_profile.Runtime.MaxTrainerMoney}。");

        return (int)money;
    }

    private void WriteTrainerMoney(MgbaBridgeClient bridge, int money)
    {
        _runtime.EnsureCanWrite(GameDataSurface.Trainer, live: true);
        var (address, key) = ResolveTrainerMoneyField(bridge);
        var encrypted = (uint)money ^ key;
        bridge.WriteRangeVerified(address, U32Le(encrypted));
    }

    private (uint Address, uint Key) ResolveTrainerMoneyField(MgbaBridgeClient bridge)
    {
        var saveBlock1 = ReadU32Le(bridge.Read(_profile.Memory.SaveBlock1PointerAddress, 4), 0);
        var saveBlock2 = ReadU32Le(bridge.Read(_profile.Memory.SaveBlock2PointerAddress, 4), 0);

        if (!IsEwramRange(saveBlock1, (int)_profile.Memory.SaveBlock1MoneyOffset + 4))
            throw new InvalidOperationException("没有读到有效的 SaveBlock1 金钱字段。请确认游戏已经载入存档。");
        if (!IsEwramRange(saveBlock2, (int)_profile.Memory.SaveBlock2EncryptionKeyOffset + 4))
            throw new InvalidOperationException("没有读到有效的 SaveBlock2 加密密钥。请确认游戏已经载入存档。");

        var moneyAddress = saveBlock1 + _profile.Memory.SaveBlock1MoneyOffset;
        var keyAddress = saveBlock2 + _profile.Memory.SaveBlock2EncryptionKeyOffset;
        var key = ReadU32Le(bridge.Read(keyAddress, 4), 0);
        return (moneyAddress, key);
    }

    private static uint NewNonShinyPid(uint otId)
    {
        uint pid;
        do
        {
            pid = NewNonZeroRandomUInt();
        } while (PartyPokemon.IsShinyPid(pid, otId));
        return pid;
    }

    private static uint NewNonZeroRandomUInt()
    {
        uint value;
        do
        {
            value = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1);
        } while (value == 0);
        return value;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            if (b != 0) return false;
        return true;
    }

    private static byte[] EmptyGameText(int length)
    {
        var bytes = new byte[length];
        Array.Fill<byte>(bytes, 0xFF);
        return bytes;
    }

    private static byte[] CopyGameTextBuffer(ReadOnlySpan<byte> source, int length)
    {
        var bytes = EmptyGameText(length);
        source[..Math.Min(source.Length, length)].CopyTo(bytes);
        return bytes;
    }

    private string DecodeGameTextBuffer(ReadOnlySpan<byte> bytes)
    {
        var decodeMap = GameTextDecodeMap();
        var output = new List<string>();
        for (var i = 0; i < bytes.Length;)
        {
            var b = bytes[i];
            if (b is 0x00 or 0xFF) break;
            if (i + 1 < bytes.Length && bytes[i + 1] != 0xFF)
            {
                var code = (ushort)(b << 8 | bytes[i + 1]);
                if (decodeMap.TryGetValue(code, out var text))
                {
                    output.Add(text);
                    i += 2;
                    continue;
                }
            }

            if (TrySingleByteGameTextChar(b, out var ch))
            {
                output.Add(ch.ToString());
                i++;
                continue;
            }

            if (i + 1 < bytes.Length && bytes[i + 1] != 0xFF)
            {
                output.Add("□");
                i += 2;
                continue;
            }

            output.Add("□");
            i++;
        }

        return string.Concat(output);
    }

    private string PokemonOtName(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < _profile.Memory.PokemonOtNameOffset + _profile.Memory.PokemonOtNameLength) return "未设置";
        var nameBytes = CopyGameTextBuffer(
            raw.Slice(_profile.Memory.PokemonOtNameOffset, _profile.Memory.PokemonOtNameLength),
            _profile.Memory.PlayerNameLength);
        var decoded = DecodeGameTextBuffer(nameBytes);
        return string.IsNullOrWhiteSpace(decoded) ? "未设置" : decoded;
    }

    private void SetOtEditor(TextBox nameBox, TextBlock idText, ReadOnlySpan<byte> raw, uint otId)
    {
        nameBox.Text = PokemonOtName(raw);
        idText.Text = $"训练家ID {(ushort)(otId & 0xFFFF)}  秘密ID {(ushort)(otId >> 16)}";
    }

    private void ApplyEditedOtName(PartyPokemon mon, string? text, string originalName)
    {
        var name = (text ?? string.Empty).Trim();
        if (name == originalName) return;
        mon.SetOtName(EncodeGameTextBuffer(name, _profile.Memory.PokemonOtNameLength));
    }

    private void ApplyEditedOtName(BoxPokemon mon, string? text, string originalName)
    {
        var name = (text ?? string.Empty).Trim();
        if (name == originalName) return;
        mon.SetOtName(EncodeGameTextBuffer(name, _profile.Memory.PokemonOtNameLength));
    }

    private byte[] EncodeGameTextBuffer(string text, int length, bool requireTerminator = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("名字不能为空。");

        var encodeMap = GameTextEncodeMap();
        var output = new List<byte>(length);
        foreach (var rawCh in text.Trim().ToUpperInvariant())
        {
            if (TrySingleByteGameTextCode(rawCh, out var code))
            {
                output.Add(code);
            }
            else if (encodeMap.TryGetValue(rawCh, out var bytes))
            {
                output.AddRange(bytes);
            }
            else
            {
                throw new InvalidOperationException($"当前字库还不能安全编码“{rawCh}”。请先用英文大写/数字，或等后续补完整中文编码表。");
            }

            var maxEncodedLength = requireTerminator ? length - 1 : length;
            if (output.Count > maxEncodedLength)
                throw new InvalidOperationException(requireTerminator
                    ? $"名字内部编码超过 {maxEncodedLength} 字节，必须为结束符保留 1 字节。"
                    : $"名字内部编码超过 {length} 字节，无法写入。");
        }

        var result = EmptyGameText(length);
        output.CopyTo(result);
        return result;
    }

    private string NameEncodingStatus(ReadOnlySpan<byte> bytes)
    {
        var decoded = DecodeGameTextBuffer(bytes);
        return decoded.Contains('□')
            ? "含未识别字码"
            : "已识别";
    }

    private Dictionary<char, byte[]> GameTextEncodeMap()
    {
        EnsureGameTextMaps();
        return _gameTextEncodeMap!;
    }

    private Dictionary<ushort, string> GameTextDecodeMap()
    {
        EnsureGameTextMaps();
        return _gameTextDecodeMap!;
    }

    private void EnsureGameTextMaps()
    {
        if (_gameTextEncodeMap is not null && _gameTextDecodeMap is not null) return;
        var encode = new Dictionary<char, byte[]>();
        var decode = new Dictionary<ushort, string>();
        var names = _db.Table("species");
        var nameByteTable = _db.Table("species_nickname_bytes");
        if (nameByteTable.Count == 0)
            nameByteTable = _db.Table("species_name_bytes");
        foreach (var (species, hex) in nameByteTable)
        {
            if (!names.TryGetValue(species, out var name) || string.IsNullOrWhiteSpace(name))
                continue;
            byte[] raw;
            try
            {
                raw = Convert.FromHexString(hex.Trim());
            }
            catch (FormatException)
            {
                continue;
            }

            LearnGameTextName(name, raw, encode, decode);
        }

        foreach (var (code, text) in _db.Table("game_text_chars"))
        {
            if (string.IsNullOrEmpty(text) || code is < 0 or > 0xFFFF) continue;
            var value = (ushort)code;
            decode[value] = text;
            if (text.Length == 1)
                encode[text[0]] = [(byte)(value >> 8), (byte)value];
        }

        _gameTextEncodeMap = encode;
        _gameTextDecodeMap = decode;
    }

    private static void LearnGameTextName(
        string name,
        ReadOnlySpan<byte> raw,
        Dictionary<char, byte[]> encode,
        Dictionary<ushort, string> decode)
    {
        var pos = 0;
        foreach (var ch in name)
        {
            if (pos >= raw.Length || raw[pos] == 0xFF) return;
            if (TrySingleByteGameTextCode(ch, out var single) && raw[pos] == single)
            {
                pos++;
                continue;
            }

            if (pos + 1 >= raw.Length || raw[pos + 1] == 0xFF) return;
            var code = (ushort)(raw[pos] << 8 | raw[pos + 1]);
            encode.TryAdd(ch, [raw[pos], raw[pos + 1]]);
            decode.TryAdd(code, ch.ToString());
            pos += 2;
        }
    }

    private static bool TrySingleByteGameTextCode(char ch, out byte code)
    {
        if (ch is >= '0' and <= '9')
        {
            code = (byte)(0xA1 + ch - '0');
            return true;
        }
        if (ch is >= 'A' and <= 'Z')
        {
            code = (byte)(0xBB + ch - 'A');
            return true;
        }
        if (ch == '-')
        {
            code = 0xBA;
            return true;
        }

        code = 0;
        return false;
    }

    private static bool TrySingleByteGameTextChar(byte code, out char ch)
    {
        if (code is >= 0xA1 and <= 0xAA)
        {
            ch = (char)('0' + code - 0xA1);
            return true;
        }
        if (code is >= 0xBB and <= 0xD4)
        {
            ch = (char)('A' + code - 0xBB);
            return true;
        }
        if (code == 0xBA)
        {
            ch = '-';
            return true;
        }

        ch = '\0';
        return false;
    }

    private static IReadOnlyList<(string Name, int Count)> ScanMoneyCandidates(ReadOnlySpan<byte> ewram, int money)
    {
        var bcd3 = EncodeMoneyBcd(money, 3);
        var bcd4 = EncodeMoneyBcd(money, 4);
        var u24 = new byte[] { (byte)money, (byte)(money >> 8), (byte)(money >> 16) };
        var u24Be = u24.Reverse().ToArray();
        var u32 = BitConverter.GetBytes(money);
        var patterns = new (string Name, byte[] Bytes)[]
        {
            ("3字节BCD", bcd3),
            ("3字节BCD反序", bcd3.Reverse().ToArray()),
            ("4字节BCD", bcd4),
            ("4字节BCD反序", bcd4.Reverse().ToArray()),
            ("24位整数", u24),
            ("24位整数反序", u24Be),
            ("32位整数", u32)
        };

        var results = new List<(string Name, int Count)>();
        foreach (var pattern in patterns)
        {
            var count = CountPattern(ewram, pattern.Bytes);
            if (count > 0)
                results.Add((pattern.Name, count));
        }

        return results;
    }

    private static byte[] EncodeMoneyBcd(int money, int byteCount)
    {
        var text = money.ToString().PadLeft(byteCount * 2, '0');
        if (text.Length > byteCount * 2)
            text = text.Substring(text.Length - byteCount * 2);
        var bytes = new byte[byteCount];
        for (var i = 0; i < byteCount; i++)
            bytes[i] = (byte)(((text[i * 2] - '0') << 4) | (text[i * 2 + 1] - '0'));
        return bytes;
    }

    private static int CountPattern(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length) return 0;
        var count = 0;
        for (var i = 0; i <= data.Length - pattern.Length; i++)
            if (data.Slice(i, pattern.Length).SequenceEqual(pattern))
                count++;
        return count;
    }

    private void OnDexSearchChanged(object? sender, TextChangedEventArgs e) => ApplyDexFilter();

    private void OnDexSortChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyDexFilter(selectFirst: true, preserveSelection: false, scrollToTop: true);

    private void OnMoveDexFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyMoveDexFilter();

    private void OnMoveDexSearchChanged(object? sender, TextChangedEventArgs e) => ApplyMoveDexFilter();

    private void OnDexSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DexSpeciesList.SelectedItem is DexSpeciesRow row)
            RefreshDexDetails(row);
    }

    private void InitializeDexRows()
    {
        _allDexRows = _speciesChoices
            .Where(c => c.Id > 0)
            .Select(c =>
            {
                var display = c.ToString();
                return new DexSpeciesRow(c.Id, c.Name, display, $"No.{c.Id:0000} {display}", LoadSpriteBitmap(c.Id));
            })
            .ToArray();
        ApplyDexFilter(selectFirst: true);
    }

    private void InitializeMoveDexRows()
    {
        _allMoveDexRows = _moveChoices
            .Where(c => c.Id > 0)
            .Select(c =>
            {
                var name = c.ToString();
                if (!TryReadEmbeddedMoveData(c.Id, out var data))
                {
                    return new MoveDexRow(
                        c.Id,
                        c.Name,
                        $"No.{c.Id:000} {name}",
                        -1,
                        -1,
                        [],
                        "",
                        "",
                        "",
                        ComparisonNeutralBrush,
                        "",
                        "",
                        ComparisonNeutralBrush,
                        "");
                }

                var power = MovePowerText(data.Power);
                var accuracy = MoveAccuracyText(data.Accuracy);
                var officialPower = string.Empty;
                var officialAccuracy = string.Empty;
                var powerForeground = ComparisonNeutralBrush;
                var accuracyForeground = ComparisonNeutralBrush;
                if (TryReadOfficialMoveData(c.Id, out var official))
                {
                    officialPower = MovePowerText(official.Power);
                    officialAccuracy = MoveAccuracyText(official.Accuracy);
                    powerForeground = ModifiedValueForeground(data.Power, official.Power);
                    accuracyForeground = ModifiedValueForeground(data.Accuracy, official.Accuracy);
                }

                return new MoveDexRow(
                    c.Id,
                    c.Name,
                    $"No.{c.Id:000} {name}",
                    data.Type,
                    data.Category,
                    TypeBadges(data.Type, data.Type),
                    MoveCategoryNameZh(data.Category),
                    power,
                    officialPower,
                    powerForeground,
                    accuracy,
                    officialAccuracy,
                    accuracyForeground,
                    data.Pp.ToString());
            })
            .ToArray();
        ApplyMoveDexFilter();
    }

    private void ApplyDexFilter(
        bool selectFirst = false,
        bool preserveSelection = true,
        bool scrollToTop = false,
        int? selectSpeciesId = null)
    {
        var previousId = DexSpeciesList.SelectedItem is DexSpeciesRow selected ? selected.Id : (int?)null;
        var filter = DexSearchBox.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _allDexRows
            : _allDexRows
                .Where(r => r.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            r.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        var rows = SortDexRows(filtered);

        _dexRows.Clear();
        foreach (var row in rows)
            _dexRows.Add(row);

        if (_dexRows.Count == 0)
        {
            DexSpeciesList.SelectedIndex = -1;
            DexTitleText.Text = "没有匹配的宝可梦";
            DexSubtitleText.Text = "换一个中文名或编号继续搜索。";
            ClearDexDetails();
            return;
        }

        var next = selectSpeciesId is null ? null : _dexRows.FirstOrDefault(r => r.Id == selectSpeciesId.Value);
        if (next is null && preserveSelection && previousId is not null)
            next = _dexRows.FirstOrDefault(r => r.Id == previousId.Value);
        DexSpeciesList.SelectedItem = next ?? (selectFirst ? _dexRows[0] : _dexRows[0]);
        if (scrollToTop)
            ScrollDexListToTop();
    }

    private void ScrollDexListToTop()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = DexSpeciesList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scrollViewer is not null)
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, 0);
            if (_dexRows.Count > 0)
                DexSpeciesList.ScrollIntoView(_dexRows[0]);
        }, DispatcherPriority.Background);
    }

    private void ScrollDexSelectedItemToTop()
    {
        if (DexSpeciesList.SelectedItem is not DexSpeciesRow row) return;
        DexSpeciesList.ScrollIntoView(row);
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = DexSpeciesList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var item = DexSpeciesList.GetVisualDescendants()
                .OfType<ListBoxItem>()
                .FirstOrDefault(x => Equals(x.DataContext, row));
            if (scrollViewer is null || item is null) return;
            var point = item.TranslatePoint(new Point(0, 0), scrollViewer);
            if (point is null) return;
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Max(0, scrollViewer.Offset.Y + point.Value.Y));
        }, DispatcherPriority.Background);
    }

    private DexSpeciesRow[] SortDexRows(IEnumerable<DexSpeciesRow> rows)
    {
        var sortId = DexSortBox.SelectedItem is ChoiceRow sort ? sort.Id : 0;
        var descending = DexSortDirectionBox.SelectedItem is ChoiceRow direction && direction.Id == 1;
        if (sortId == 0)
            return (descending ? rows.OrderByDescending(r => r.Id) : rows.OrderBy(r => r.Id)).ToArray();

        var keyedRows = rows
            .Select(row => (Row: row, SortValue: DexSortValue(row.Id, sortId)))
            .ToArray();
        var validRows = keyedRows.Where(x => x.SortValue is not null);
        var invalidRows = keyedRows.Where(x => x.SortValue is null).OrderBy(x => x.Row.Id);
        var orderedValid = descending
            ? validRows.OrderByDescending(x => x.SortValue!.Value).ThenBy(x => x.Row.Id)
            : validRows.OrderBy(x => x.SortValue!.Value).ThenBy(x => x.Row.Id);
        return orderedValid.Concat(invalidRows).Select(x => x.Row).ToArray();
    }

    private int? DexSortValue(int species, int sortId)
    {
        try
        {
            var stats = ReadSpeciesStats(species);
            return sortId switch
            {
                1 => stats.Bst,
                2 => stats.Hp,
                3 => stats.Attack,
                4 => stats.Defense,
                5 => stats.SpAttack,
                6 => stats.SpDefense,
                7 => stats.Speed,
                _ => species
            };
        }
        catch
        {
            return null;
        }
    }

    private void ApplyMoveDexFilter()
    {
        var previousId = MoveDexList.SelectedItem is MoveDexRow selected ? selected.Id : (int?)null;
        var filter = MoveDexSearchBox.Text?.Trim() ?? string.Empty;
        var typeFilter = SelectedChoiceId(MoveDexTypeFilterBox) ?? -1;
        var categoryFilter = SelectedChoiceId(MoveDexCategoryFilterBox) ?? -1;

        var rows = _allMoveDexRows.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = rows.Where(r => r.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                   r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                   r.Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        if (typeFilter >= 0)
            rows = rows.Where(r => r.TypeId == typeFilter);
        if (categoryFilter >= 0)
            rows = rows.Where(r => r.CategoryId == categoryFilter);

        _moveDexRows.Clear();
        foreach (var row in rows.OrderBy(r => r.Id))
            _moveDexRows.Add(row);

        MoveDexEmptyText.IsVisible = _moveDexRows.Count == 0;
        MoveDexEmptyText.Text = _moveDexRows.Count == 0
            ? "没有匹配的招式。"
            : "";
        if (_moveDexRows.Count == 0)
        {
            MoveDexList.SelectedIndex = -1;
            return;
        }

        MoveDexList.SelectedItem = previousId is null
            ? null
            : _moveDexRows.FirstOrDefault(r => r.Id == previousId.Value);
    }

    private void RefreshDexDetails(DexSpeciesRow row)
    {
        DexTitleText.Text = row.Title;
        SetDexSprite(row.Sprite);
        try
        {
            var stats = ReadSpeciesStats(row.Id);
            SetHeaderSpeciesInfo(stats, _dexHeaderTypeBadges, DexHeaderBstText);
            DexSubtitleText.Text = "";
            DexSubtitleText.IsVisible = false;
            FillDexBaseInfo(row, stats);
            FillDexEvolutionInfo(row);
            FillDexLevelMoves(row.Id);
            FillDexOtherMoves(row.Id);
            FillDexEncounters(row.Id);
        }
        catch (Exception ex)
        {
            ClearHeaderSpeciesInfo(_dexHeaderTypeBadges, DexHeaderBstText);
            DexSubtitleText.Text = "图鉴数据读取失败。";
            DexSubtitleText.IsVisible = true;
            ClearDexDetails();
            _dexInfoRows.Add(new DexInfoRow("错误", ex.Message));
            SetDexEvolutionPlainText("错误：" + ex.Message);
            DexLevelMovesEmptyText.Text = "错误：" + ex.Message;
            DexLevelMovesEmptyText.IsVisible = true;
            DexOtherMovesEmptyText.Text = "错误：" + ex.Message;
            DexOtherMovesEmptyText.IsVisible = true;
            DexEncountersEmptyText.Text = "错误：" + ex.Message;
            DexEncountersEmptyText.IsVisible = true;
            Log("图鉴读取错误：" + ex.Message);
        }
    }

    private void ClearDexDetails()
    {
        _dexInfoRows.Clear();
        _dexStatRows.Clear();
        _dexLevelMoveRows.Clear();
        _dexOtherMoveRows.Clear();
        _dexEncounterRows.Clear();
        ClearHeaderSpeciesInfo(_dexHeaderTypeBadges, DexHeaderBstText);
        DexSpriteImage.Source = null;
        DexSpriteImage.IsVisible = false;
        SetDexEvolutionPlainText("");
        DexLevelMovesEmptyText.Text = "";
        DexLevelMovesEmptyText.IsVisible = false;
        DexOtherMovesEmptyText.Text = "";
        DexOtherMovesEmptyText.IsVisible = false;
        DexEncountersEmptyText.Text = "";
        DexEncountersEmptyText.IsVisible = false;
    }

    private void SetDexSprite(Bitmap? sprite)
    {
        DexSpriteImage.Source = sprite;
        DexSpriteImage.IsVisible = sprite is not null;
    }

    private void SetPokemonSprite(Image image, int speciesId)
    {
        var sprite = speciesId > 0 ? LoadSpriteBitmap(speciesId) : null;
        image.Source = sprite;
        image.IsVisible = sprite is not null;
    }

    private void UpdatePartyHeaderInfo(int species, int? level)
    {
        if (species <= 0)
        {
            SelectedTitleText.Text = "请选择或刷新槽位";
            ClearHeaderSpeciesInfo(_partyHeaderTypeBadges, PartyHeaderBstText);
            return;
        }

        SelectedTitleText.Text = level is > 0
            ? $"{SpeciesName(species)} Lv{level.Value}"
            : SpeciesName(species);
        UpdateHeaderSpeciesInfo(species, _partyHeaderTypeBadges, PartyHeaderBstText);
    }

    private void UpdateBoxHeaderInfo(int species, uint? exp)
    {
        if (species <= 0)
        {
            BoxSelectedTitleText.Text = "箱子槽编辑";
            ClearHeaderSpeciesInfo(_boxHeaderTypeBadges, BoxHeaderBstText);
            BoxOtNameBox.Text = string.Empty;
            BoxOtIdText.Text = "未选择";
            return;
        }

        try
        {
            var stats = ReadSpeciesStats(species);
            int? level = exp is null ? null : LevelFromExp(exp.Value, stats.GrowthRate);
            BoxSelectedTitleText.Text = level is > 0
                ? $"{SpeciesName(species)} Lv{level.Value}"
                : SpeciesName(species);
            SetHeaderSpeciesInfo(stats, _boxHeaderTypeBadges, BoxHeaderBstText);
        }
        catch
        {
            BoxSelectedTitleText.Text = SpeciesName(species);
            ClearHeaderSpeciesInfo(_boxHeaderTypeBadges, BoxHeaderBstText);
        }
    }

    private void UpdateHeaderSpeciesInfo(int species, ObservableCollection<DexTypeBadge> target, TextBlock bstText)
    {
        try
        {
            SetHeaderSpeciesInfo(ReadSpeciesStats(species), target, bstText);
        }
        catch
        {
            ClearHeaderSpeciesInfo(target, bstText);
        }
    }

    private static void SetHeaderSpeciesInfo(SpeciesStats stats, ObservableCollection<DexTypeBadge> target, TextBlock bstText)
    {
        target.Clear();
        foreach (var badge in TypeBadges(stats.Type1, stats.Type2))
            target.Add(badge);
        bstText.Text = $"总种族值 {stats.Bst}";
    }

    private static void ClearHeaderSpeciesInfo(ObservableCollection<DexTypeBadge> target, TextBlock bstText)
    {
        target.Clear();
        bstText.Text = string.Empty;
    }

    private Bitmap? LoadSpriteBitmap(int speciesId)
    {
        if (!UsesVerifiedSpriteAssets) return null;
        if (_spriteCache.TryGetValue(speciesId, out var cached)) return cached;
        var sprite = LoadSpriteBitmapCore(speciesId) ?? LoadIconBitmapCore(speciesId);
        _spriteCache[speciesId] = sprite;
        return sprite;
    }

    private Bitmap? LoadEggSpriteBitmap()
    {
        if (!UsesVerifiedSpriteAssets || _profile.Graphics.EggSpeciesId <= 0) return null;
        _eggSpriteCache ??= LoadSpriteBitmapCore(_profile.Graphics.EggSpeciesId);
        return _eggSpriteCache;
    }

    private Bitmap? LoadSpriteBitmapCore(int speciesId)
        => LoadSpriteBitmapCore($"{speciesId:0000}");

    private Bitmap? LoadIconBitmapCore(int speciesId)
    {
        if (string.IsNullOrWhiteSpace(_profile.Graphics.IconAssetRoot)) return null;
        try
        {
            var root = _profile.Graphics.IconAssetRoot.Trim().Trim('/').Replace('\\', '/');
            var uri = new Uri($"avares://RocketTool.Avalonia/Assets/{root}/{speciesId:0000}.png");
            return new Bitmap(AssetLoader.Open(uri));
        }
        catch
        {
            return null;
        }
    }

    private Bitmap? LoadSpriteBitmapCore(string assetName)
    {
        try
        {
            var root = _profile.Graphics.SpriteAssetRoot.Trim().Trim('/').Replace('\\', '/');
            var uri = new Uri($"avares://RocketTool.Avalonia/Assets/{root}/{assetName}.png");
            return new Bitmap(AssetLoader.Open(uri));
        }
        catch
        {
            return null;
        }
    }

    private void FillDexBaseInfo(DexSpeciesRow row, SpeciesStats s)
    {
        _dexInfoRows.Clear();
        _dexInfoRows.Add(new DexInfoRow("宝可梦", row.DisplayName));
        _dexInfoRows.Add(new DexInfoRow("属性", TypePairText(s.Type1, s.Type2), TypeBadges: TypeBadges(s.Type1, s.Type2)));
        _dexInfoRows.Add(new DexInfoRow("特性1", AbilityName(s.Ability1), AbilityTooltip(s.Ability1)));
        if (s.Ability2 != 0)
            _dexInfoRows.Add(new DexInfoRow("特性2", AbilityName(s.Ability2), AbilityTooltip(s.Ability2)));
        if (s.Ability3 is not null)
            _dexInfoRows.Add(new DexInfoRow("隐藏特性", AbilityName(s.Ability3.Value), AbilityTooltip(s.Ability3.Value)));
        _dexInfoRows.Add(new DexInfoRow("携带道具", $"{HeldItemText(s.Item1)} / {HeldItemText(s.Item2)}"));
        _dexInfoRows.Add(new DexInfoRow("捕获率", s.CatchRate.ToString()));
        _dexInfoRows.Add(new DexInfoRow("经验收益", s.ExpYield.ToString()));
        _dexInfoRows.Add(new DexInfoRow("成长率", GrowthRateName(s.GrowthRate)));
        _dexInfoRows.Add(new DexInfoRow("努力值收益", EvYieldText(s.EvYield)));
        _dexInfoRows.Add(new DexInfoRow("性别", GenderRatioText(s.GenderRatio)));
        _dexInfoRows.Add(new DexInfoRow("孵化周期", s.EggCycles.ToString()));
        _dexInfoRows.Add(new DexInfoRow("初始亲密度", s.Friendship.ToString()));
        _dexInfoRows.Add(new DexInfoRow("蛋组", $"{EggGroupName(s.EggGroup1)} / {EggGroupName(s.EggGroup2)}"));

        FillDexStatRows(s);
    }

    private void FillDexStatRows(SpeciesStats s)
    {
        _dexMaxLevel = ProfileMaxPokemonLevel;
        DexMaxLevelHeaderText.Text = $"{_dexMaxLevel}级时";
        var hasOfficial = TryReadOfficialSpeciesStats(s.Species, out var official);
        DexStatsNoteText.Text = hasOfficial
            ? $"当前种族值与原版种族值分列显示；当前值更高为红色、更低为绿色、相同不染色。能力按当前种族值、31 个体、0 努力、无性格修正计算，最高级为 {_dexMaxLevel} 级。"
            : HasOfficialSpeciesComparison
                ? $"未匹配到可靠的官方原版形态；能力按当前种族值、31 个体、0 努力、无性格修正计算，最高级为 {_dexMaxLevel} 级。"
                : $"按 31 个体、0 努力、无性格修正计算；最高级按 ROM 等级上限 {_dexMaxLevel} 级显示。";

        var stats = new[]
        {
            ("HP", (int)s.Hp, hasOfficial ? official.Hp : (int?)null, true),
            ("攻击", (int)s.Attack, hasOfficial ? official.Attack : (int?)null, false),
            ("防御", (int)s.Defense, hasOfficial ? official.Defense : (int?)null, false),
            ("特攻", (int)s.SpAttack, hasOfficial ? official.SpAttack : (int?)null, false),
            ("特防", (int)s.SpDefense, hasOfficial ? official.SpDefense : (int?)null, false),
            ("速度", (int)s.Speed, hasOfficial ? official.Speed : (int?)null, false)
        };

        _dexStatRows.Clear();
        var sum50 = 0;
        var sum100 = 0;
        var sumMax = 0;
        foreach (var (name, baseStat, officialStat, isHp) in stats)
        {
            var level50 = CalculateDexStat(baseStat, 50, isHp);
            var level100 = CalculateDexStat(baseStat, 100, isHp);
            var maxLevel = CalculateDexStat(baseStat, _dexMaxLevel, isHp);
            sum50 += level50;
            sum100 += level100;
            sumMax += maxLevel;
            _dexStatRows.Add(new DexStatRow(
                name,
                baseStat.ToString(),
                officialStat?.ToString() ?? string.Empty,
                ModifiedValueForeground(baseStat, officialStat),
                level50.ToString(),
                level100.ToString(),
                maxLevel.ToString()));
        }
        _dexStatRows.Add(new DexStatRow(
            "合计",
            s.Bst.ToString(),
            hasOfficial ? official.Bst.ToString() : string.Empty,
            ModifiedValueForeground(s.Bst, hasOfficial ? official.Bst : null),
            sum50.ToString(),
            sum100.ToString(),
            sumMax.ToString()));
    }

    private void FillDexEvolutionInfo(DexSpeciesRow row)
    {
        var family = EvolutionFamilySpecies(row.Id);
        var outgoingBySpecies = family
            .OrderBy(id => id)
            .ToDictionary(
                id => id,
                id => ReadSpeciesEvolutions(id)
                    .Where(e => e.TargetSpecies > 0 && family.Contains(e.TargetSpecies))
                    .OrderBy(e => e.Slot)
                    .ThenBy(e => e.TargetSpecies)
                    .ToArray());
        var hasIncoming = outgoingBySpecies.Values
            .SelectMany(evolutions => evolutions)
            .Select(e => (int)e.TargetSpecies)
            .ToHashSet();
        var roots = family
            .Where(id => !hasIncoming.Contains(id))
            .OrderBy(id => id)
            .ToArray();
        if (roots.Length == 0)
            roots = [row.Id];

        var branchCount = outgoingBySpecies.Count(kv => kv.Value.Length > 1);
        var lines = new List<IReadOnlyList<DexEvolutionSegment>>
        {
            new[] { new DexEvolutionSegment("完整进化链") }
        };
        if (branchCount > 0)
            lines.Add(new[] { new DexEvolutionSegment($"存在 {branchCount} 个分支节点，已按分支缩进显示。") });
        lines.Add(new[] { new DexEvolutionSegment("") });

        if (outgoingBySpecies.Values.All(evolutions => evolutions.Length == 0))
        {
            lines.Add(EvolutionSpeciesSegments(row.Id, row.Id, ""));
            lines.Add(new[] { new DexEvolutionSegment("无进化链。") });
            SetDexEvolutionLines(lines);
            return;
        }

        foreach (var root in roots)
            AppendEvolutionTree(lines, root, row.Id, outgoingBySpecies, [], 0);

        SetDexEvolutionLines(lines);
    }

    private HashSet<int> EvolutionFamilySpecies(int species)
    {
        var family = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(species);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!family.Add(current)) continue;

            foreach (var evolution in ReadSpeciesEvolutions(current))
            {
                if (evolution.TargetSpecies > 0 && !family.Contains(evolution.TargetSpecies))
                    pending.Enqueue(evolution.TargetSpecies);
            }

            foreach (var source in _allDexRows)
            {
                if (family.Contains(source.Id)) continue;
                if (ReadSpeciesEvolutions(source.Id).Any(e => e.TargetSpecies == current))
                    pending.Enqueue(source.Id);
            }
        }

        return family;
    }

    private void AppendEvolutionTree(
        List<IReadOnlyList<DexEvolutionSegment>> lines,
        int species,
        int selectedSpecies,
        IReadOnlyDictionary<int, SpeciesEvolution[]> outgoingBySpecies,
        HashSet<int> path,
        int depth)
    {
        var indent = new string(' ', depth * 2);
        lines.Add(EvolutionSpeciesSegments(species, selectedSpecies, indent));

        if (!path.Add(species))
        {
            lines.Add(new[] { new DexEvolutionSegment($"{indent}  已检测到循环进化引用。") });
            return;
        }

        AppendEvolutionChildren(lines, species, selectedSpecies, outgoingBySpecies, path, depth + 1);
    }

    private void AppendEvolutionChildren(
        List<IReadOnlyList<DexEvolutionSegment>> lines,
        int species,
        int selectedSpecies,
        IReadOnlyDictionary<int, SpeciesEvolution[]> outgoingBySpecies,
        HashSet<int> path,
        int depth)
    {
        if (outgoingBySpecies.TryGetValue(species, out var evolutions))
        {
            foreach (var evolution in evolutions)
            {
                var target = evolution.TargetSpecies;
                var edgeIndent = new string(' ', depth * 2);
                var line = new List<DexEvolutionSegment>
                {
                    new($"{edgeIndent}{EvolutionEdgeLabel(evolution)} -> ")
                };
                line.AddRange(EvolutionSpeciesSegments(target, selectedSpecies, ""));
                lines.Add(line);
                if (path.Contains(target))
                {
                    lines.Add(new[] { new DexEvolutionSegment($"{edgeIndent}  已检测到循环进化引用。") });
                    continue;
                }
                var childPath = new HashSet<int>(path) { target };
                AppendEvolutionChildren(lines, target, selectedSpecies, outgoingBySpecies, childPath, depth + 1);
            }
        }
    }

    private string EvolutionSpeciesText(int species, int selectedSpecies)
    {
        var name = SpeciesName(species);
        return species == selectedSpecies ? $"{name}（当前）" : name;
    }

    private IReadOnlyList<DexEvolutionSegment> EvolutionSpeciesSegments(int species, int selectedSpecies, string prefix)
        => new[]
        {
            new DexEvolutionSegment(prefix),
            new DexEvolutionSegment(
                EvolutionSpeciesText(species, selectedSpecies),
                _db.Table("species").ContainsKey(species) ? species : null,
                species == selectedSpecies)
        };

    private void SetDexEvolutionPlainText(string text)
    {
        DexEvolutionPanel.Children.Clear();
        DexEvolutionPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.DarkSlateBlue,
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void SetDexEvolutionLines(IEnumerable<IReadOnlyList<DexEvolutionSegment>> lines)
    {
        DexEvolutionPanel.Children.Clear();
        foreach (var line in lines)
        {
            var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var segment in line)
            {
                if (segment.SpeciesId is { } speciesId)
                {
                    var button = new Button
                    {
                        Content = segment.Text,
                        Padding = new Thickness(4, 0),
                        Margin = new Thickness(0, 0, 2, 0),
                        Foreground = segment.IsCurrent ? Brushes.Firebrick : Brushes.DarkSlateBlue,
                        FontWeight = segment.IsCurrent ? FontWeight.Bold : FontWeight.SemiBold
                    };
                    button.Click += (_, _) => SelectDexSpeciesFromEvolution(speciesId);
                    row.Children.Add(button);
                }
                else
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = segment.Text,
                        Foreground = Brushes.DarkSlateBlue,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
            }

            DexEvolutionPanel.Children.Add(row);
        }
    }

    private void SelectDexSpeciesFromEvolution(int speciesId)
    {
        var name = SpeciesName(speciesId);
        DexSearchBox.Text = name;
        ApplyDexFilter(selectFirst: true, preserveSelection: false, scrollToTop: true, selectSpeciesId: speciesId);
    }

    private void FillDexLevelMoves(int species)
    {
        _dexLevelMoveRows.Clear();
        var moves = ReadSpeciesLevelMoves(species);
        if (moves.Count == 0)
        {
            DexLevelMovesEmptyText.Text = "无升级招式数据。";
            DexLevelMovesEmptyText.IsVisible = true;
            return;
        }

        DexLevelMovesEmptyText.Text = "";
        DexLevelMovesEmptyText.IsVisible = false;
        foreach (var entry in moves)
            AddDexMoveRow(_dexLevelMoveRows, $"Lv.{entry.Level:000}", entry.Move);
    }

    private void FillDexOtherMoves(int species)
    {
        _dexOtherMoveRows.Clear();
        foreach (var (index, move) in ReadIndexedSpeciesMoves("species_machine_moves", species))
            AddDexMoveRow(_dexOtherMoveRows, $"机器槽 {index:000}", move);
        foreach (var (index, move) in ReadIndexedSpeciesMoves("species_tutor_moves", species))
            AddDexMoveRow(_dexOtherMoveRows, $"教学 {index + 1:000}", move);
        if (_db.Table("species_egg_moves").TryGetValue(species, out var rawEggMoves))
        {
            foreach (var text in rawEggMoves.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(text, out var move))
                    AddDexMoveRow(_dexOtherMoveRows, "蛋招式", move);
            }
        }
        DexOtherMovesEmptyText.Text = _dexOtherMoveRows.Count == 0 ? "无机器、教学或蛋招式数据。" : "";
        DexOtherMovesEmptyText.IsVisible = _dexOtherMoveRows.Count == 0;
    }

    private void FillDexEncounters(int species)
    {
        _dexEncounterRows.Clear();
        if (_db.Table("species_encounters").TryGetValue(species, out var raw))
        {
            foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(',');
                if (parts.Length != 7 || !int.TryParse(parts[0], out var group) || !int.TryParse(parts[1], out var map))
                    continue;
                var mapInfo = _mapDatabase.Maps.FirstOrDefault(candidate => candidate.Group == group && candidate.Map == map);
                var mapName = MapStatusName(group, map, mapInfo);
                var level = parts[5] == parts[6] ? $"Lv.{parts[5]}" : $"Lv.{parts[5]}-{parts[6]}";
                _dexEncounterRows.Add(new DexEncounterRow(mapName, parts[2], level, parts[3], (int.Parse(parts[4]) + 1).ToString()));
            }
        }
        DexEncountersEmptyText.Text = _dexEncounterRows.Count == 0 ? "当前 ROM 的野外遭遇表中没有记录。" : "";
        DexEncountersEmptyText.IsVisible = _dexEncounterRows.Count == 0;
    }

    private IEnumerable<(int Index, int Move)> ReadIndexedSpeciesMoves(string table, int species)
    {
        if (!_db.Table(table).TryGetValue(species, out var raw)) yield break;
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var index) && int.TryParse(parts[1], out var move))
                yield return (index, move);
        }
    }

    private void AddDexMoveRow(ObservableCollection<DexLevelMoveRow> rows, string source, int move)
    {
        try
        {
            var data = ReadMoveData(move);
            var officialPower = string.Empty;
            var officialAccuracy = string.Empty;
            var powerForeground = ComparisonNeutralBrush;
            var accuracyForeground = ComparisonNeutralBrush;
            if (TryReadOfficialMoveData(move, out var official))
            {
                officialPower = MovePowerText(official.Power);
                officialAccuracy = MoveAccuracyText(official.Accuracy);
                powerForeground = ModifiedValueForeground(data.Power, official.Power);
                accuracyForeground = ModifiedValueForeground(data.Accuracy, official.Accuracy);
            }
            rows.Add(new DexLevelMoveRow(
                source, MoveName(move), MoveTypeNameZh(data.Type), MoveCategoryNameZh(data.Category),
                MovePowerText(data.Power), officialPower, powerForeground,
                MoveAccuracyText(data.Accuracy), officialAccuracy, accuracyForeground, data.Pp.ToString()));
        }
        catch
        {
            rows.Add(new DexLevelMoveRow(source, MoveName(move), "", "", "", "",
                ComparisonNeutralBrush, "", "", ComparisonNeutralBrush, ""));
        }
    }

    private void OnLookupSpeciesClicked(object? sender, RoutedEventArgs e)
    {
        LookupTo(SpeciesResultBox, () =>
        {
            var id = ParseIntRequired(SpeciesLookupBox.Text, "宝可梦ID");
            var s = ReadSpeciesStats(id);
            var hasOfficial = TryReadOfficialSpeciesStats(id, out var official);
            string Stat(int current, Func<OfficialSpeciesStats, int> value) => hasOfficial
                ? ComparisonText(current, value(official), static number => number.ToString())
                : current.ToString();
            return $"宝可梦 {id}（{_db.NameOf("species", id)}）\n" +
                   $"种族值：HP {Stat(s.Hp, x => x.Hp)} / 攻击 {Stat(s.Attack, x => x.Attack)} / 防御 {Stat(s.Defense, x => x.Defense)} / 速度 {Stat(s.Speed, x => x.Speed)} / 特攻 {Stat(s.SpAttack, x => x.SpAttack)} / 特防 {Stat(s.SpDefense, x => x.SpDefense)} / 总和 {Stat(s.Bst, x => x.Bst)}\n" +
                   $"特性：{s.Ability1}（{_db.NameOf("abilities", s.Ability1)}） / {s.Ability2}（{_db.NameOf("abilities", s.Ability2)}）" +
                   (s.Ability3 is null ? "" : $" / {s.Ability3}({_db.NameOf("abilities", s.Ability3.Value)})") + "\n" +
                   $"携带道具：{s.Item1}（{ItemName(s.Item1)}） / {s.Item2}（{ItemName(s.Item2)}）\n" +
                   $"属性原始值：{s.Type1} / {s.Type2}  成长率={s.GrowthRate}  EV收益=0x{s.EvYield:X4}";
        });
    }

    private void OnLookupMoveClicked(object? sender, RoutedEventArgs e)
    {
        LookupTo(MoveResultBox, () =>
        {
            var id = ParseIntRequired(MoveLookupBox.Text, "招式ID");
            var m = ReadMoveData(id);
            var hasOfficial = TryReadOfficialMoveData(id, out var official);
            var power = hasOfficial
                ? ComparisonText(m.Power, official.Power, MovePowerText)
                : HasOfficialMoveComparison ? MovePowerText(m.Power) : m.Power.ToString();
            var accuracy = hasOfficial
                ? ComparisonText(m.Accuracy, official.Accuracy, MoveAccuracyText)
                : HasOfficialMoveComparison ? MoveAccuracyText(m.Accuracy) : m.Accuracy.ToString();
            var description = _db.Table("move_descriptions").TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : "暂无完整说明";
            return $"招式 {id}（{_db.NameOf("moves", id)}）\n" +
                   $"说明：{description}\n" +
                   $"威力 {power}  属性 {m.Type}（{MoveTypeNameZh(m.Type)}）  命中 {accuracy}  PP {m.Pp}\n" +
                   $"分类 {m.Category}（{MoveCategoryNameZh(m.Category)}）  优先度 {m.Priority}  附加几率 {m.SecondaryEffectChance}\n" +
                   $"效果 {m.Effect}（{_db.NameOf("move_effects", m.Effect)}）\n" +
                   $"目标 0x{m.Target:X4}  标志 0x{m.Flags:X4}  Z/基础威力 {m.ZMovePower}\n" +
                   $"原始数据：{Convert.ToHexString(m.Raw)}";
        });
    }

    private void OnLookupItemClicked(object? sender, RoutedEventArgs e)
    {
        LookupTo(ItemResultBox, () =>
        {
            var id = ParseIntRequired(ItemLookupBox.Text, "道具ID");
            var item = ReadItemData(id);
            var effect = item.HoldEffect == 0 ? "无" : _db.NameOf("item_effects", item.HoldEffect);
            var description = _db.Table("item_descriptions").TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : "暂无完整说明";
            return $"道具 {id}（{_db.NameOf("items", id)}）\n" +
                   $"说明：{description}\n" +
                   $"内部ID {item.ItemId}  价格 {item.Price}\n" +
                   $"携带效果 {item.HoldEffect}（{effect}）  参数 {item.HoldEffectParam}\n" +
                   $"口袋 {item.Pocket}（{PocketNameZh(item.Pocket)}）  类型 {item.Type}  重要度 {item.Importance}\n" +
                   $"说明指针 0x{item.DescriptionPointer:X8}\n场景使用函数 0x{item.FieldUseFunction:X8}\n战斗使用函数 0x{item.BattleUseFunction:X8}\nSecondary 0x{item.SecondaryId:X8}";
        });
    }

    private void OnClearLogClicked(object? sender, RoutedEventArgs e) => LogBox.Text = string.Empty;

    private async void OnCheatToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressCheatEvents || sender is not CheckBox box) return;
        if (CheatNameForBox(box) is not { } cheatName) return;
        var enabled = box.IsChecked == true;

        await RunUiTask(enabled ? "开启实验功能" : "关闭实验功能", () =>
        {
            using var bridge = ConnectBridge();
            ConfigureBridgeCheatProfile(bridge);
            if (cheatName is "INFINITE_PP" or "LOCK_HP" or "CLEAR_STATUS")
            {
                var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
                bridge.CheatCommand($"PARTY_BASE 0x{baseAddr:X}");
            }
            else if (_profile.Runtime.ExperimentNeedsSaveBaseForNoEncounter && cheatName is "NO_ENCOUNTER" && TryLocateBagBase(bridge) is { } saveBase)
            {
                bridge.CheatCommand($"SAVE_BASE 0x{saveBase:X}");
            }

            var status = bridge.Cheat(cheatName, enabled);
            CheatStatusText.Text = "实验功能状态：" + status;
            Log($"实验功能 {cheatName} -> {(enabled ? "开启" : "关闭")}：{status}");
        });
    }

    private async void OnCheatRefreshClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("刷新实验功能状态", () =>
        {
            using var bridge = ConnectBridge();
            ConfigureBridgeCheatProfile(bridge);
            var status = bridge.CheatCommand("STATUS");
            CheatStatusText.Text = "实验功能状态：" + status;
            SyncCheatBoxesFromStatus(status);
        });
    }

    private async void OnCheatClearClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("关闭实验功能", () =>
        {
            using var bridge = ConnectBridge();
            ConfigureBridgeCheatProfile(bridge);
            var status = bridge.CheatCommand("CLEAR");
            CheatStatusText.Text = "实验功能状态：" + status;
            _suppressCheatEvents = true;
            try
            {
                CheatInfinitePpBox.IsChecked = false;
                CheatLockHpBox.IsChecked = false;
                CheatClearStatusBox.IsChecked = false;
                CheatAlwaysCritBox.IsChecked = false;
                CheatNoEncounterBox.IsChecked = false;
                CheatWalkThroughWallsBox.IsChecked = false;
            }
            finally
            {
                _suppressCheatEvents = false;
            }
        });
    }

    private string? CheatNameForBox(CheckBox box)
    {
        if (ReferenceEquals(box, CheatInfinitePpBox)) return "INFINITE_PP";
        if (ReferenceEquals(box, CheatLockHpBox)) return "LOCK_HP";
        if (ReferenceEquals(box, CheatClearStatusBox)) return "CLEAR_STATUS";
        if (ReferenceEquals(box, CheatAlwaysCritBox)) return "ALWAYS_CRIT";
        if (ReferenceEquals(box, CheatNoEncounterBox)) return "NO_ENCOUNTER";
        if (ReferenceEquals(box, CheatWalkThroughWallsBox)) return "WALK_THROUGH_WALLS";
        return null;
    }

    private void ConfigureBridgeCheatProfile(MgbaBridgeClient bridge)
    {
        if (!_profile.Features.Experiments)
            throw new InvalidOperationException($"版本 {_profile.DisplayName} 未启用实验功能。");
        bridge.CheatCommand($"PROFILE {_runtime.BridgeExperimentProfile}");
    }

    private void SyncCheatBoxesFromStatus(string status)
    {
        _suppressCheatEvents = true;
        try
        {
            CheatInfinitePpBox.IsChecked = status.Contains("INFINITE_PP=1", StringComparison.OrdinalIgnoreCase);
            CheatLockHpBox.IsChecked = status.Contains("LOCK_HP=1", StringComparison.OrdinalIgnoreCase);
            CheatClearStatusBox.IsChecked = status.Contains("CLEAR_STATUS=1", StringComparison.OrdinalIgnoreCase);
            CheatAlwaysCritBox.IsChecked = status.Contains("ALWAYS_CRIT=1", StringComparison.OrdinalIgnoreCase);
            CheatNoEncounterBox.IsChecked = status.Contains("NO_ENCOUNTER=1", StringComparison.OrdinalIgnoreCase);
            CheatWalkThroughWallsBox.IsChecked = status.Contains("WALK_THROUGH_WALLS=1", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _suppressCheatEvents = false;
        }
    }

    private void OnTeleportGroupChanged(object? sender, SelectionChangedEventArgs e)
        => RefreshTeleportMaps();

    private void OnTeleportMapChanged(object? sender, SelectionChangedEventArgs e)
        => RefreshTeleportLocations();

    private void OnTeleportLocationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TeleportLocationBox.SelectedItem is not MapLocationChoice location) return;
        TeleportXBox.Text = location.X.ToString();
        TeleportYBox.Text = location.Y.ToString();
    }

    private async void OnTeleportReadLocationClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("读取当前位置", () =>
        {
            using var bridge = ConnectBridge();
            ConfigureBridgeCheatProfile(bridge);
            var status = bridge.CheatCommand("LOCATION");
            var values = ParseKeyValues(status);
            var group = ParseStatusInt(values, "GROUP");
            var map = ParseStatusInt(values, "MAP");
            var x = ParseStatusInt(values, "X");
            var y = ParseStatusInt(values, "Y");
            SelectTeleportMap(group, map);
            TeleportXBox.Text = x.ToString();
            TeleportYBox.Text = y.ToString();
            var info = _mapDatabase.Maps.FirstOrDefault(m => m.Group == group && m.Map == map);
            TeleportStatusText.Text = $"当前位置：{MapStatusName(group, map, info)}  X={x} Y={y}";
        });
    }

    private async void OnTeleportClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("传送", () =>
        {
            if (SelectedChoiceId(TeleportGroupBox) is not { } group)
                throw new InvalidOperationException("请选择地图组。");
            if (SelectedChoiceId(TeleportMapBox) is not { } map)
                throw new InvalidOperationException("请选择地图。");
            var x = ParseIntRequired(TeleportXBox.Text, "X坐标");
            var y = ParseIntRequired(TeleportYBox.Text, "Y坐标");
            var info = _mapDatabase.Maps.FirstOrDefault(m => m.Group == group && m.Map == map);
            if (info is null)
                throw new InvalidOperationException("地图数据不存在。");
            if (x < 0 || y < 0 || x >= info.Width || y >= info.Height)
                throw new InvalidOperationException($"坐标超出地图范围：该地图为 {info.Width}x{info.Height}。");

            using var bridge = ConnectBridge();
            ConfigureBridgeCheatProfile(bridge);
            var result = bridge.CheatCommand($"TELEPORT {group} {map} {x} {y}");
            TeleportStatusText.Text = $"已请求传送：{MapStatusName(group, map, info)}  X={x} Y={y}";
            SetWriteNotice($"传送请求已发送：{info.Name} X={x} Y={y}", success: true);
            ShowToast("传送请求已发送。", success: true);
            Log("传送：" + result);
        });
    }

    private void RefreshTeleportMaps()
    {
        if (TeleportGroupBox is null || TeleportMapBox is null) return;
        var selectedGroup = SelectedChoiceId(TeleportGroupBox);
        if (selectedGroup is null && _mapGroupChoices.Length == 0) return;
        var group = selectedGroup ?? _mapGroupChoices[0].Id;
        var maps = _mapDatabase.MapsInGroup(group)
            .Select(m => new ChoiceRow(m.Map, MapChoiceText(m)))
            .ToArray();
        TeleportMapBox.ItemsSource = maps;
        TeleportMapBox.SelectedIndex = maps.Length > 0 ? 0 : -1;
        RefreshTeleportLocations();
    }

    private static string MapGroupChoiceText(int group, IEnumerable<MapInfo> maps)
    {
        var mapList = maps.ToArray();
        var sampleName = mapList
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .FirstOrDefault();
        var sample = string.IsNullOrWhiteSpace(sampleName) ? string.Empty : $"：{sampleName}等";
        return $"组{group:00}{sample}（{mapList.Length}张）";
    }

    private static string MapChoiceText(MapInfo map)
        => $"{map.Name}（地图{map.Map:00}, {map.Width}x{map.Height}, 出入口{map.WarpCount}）";

    private static string MapStatusName(int group, int map, MapInfo? info)
        => info is null ? $"组{group:00} 地图{map:00}" : $"{info.Name}（组{group:00} 地图{map:00}）";

    private void RefreshTeleportLocations()
    {
        if (TeleportLocationBox is null) return;
        if (SelectedChoiceId(TeleportGroupBox) is not { } group ||
            SelectedChoiceId(TeleportMapBox) is not { } map)
        {
            TeleportLocationBox.ItemsSource = Array.Empty<MapLocationChoice>();
            return;
        }

        var info = _mapDatabase.Maps.FirstOrDefault(m => m.Group == group && m.Map == map);
        var choices = new List<MapLocationChoice>();
        if (info is not null)
            choices.Add(new MapLocationChoice($"地图中心 ({info.Width / 2},{info.Height / 2})", info.Width / 2, info.Height / 2));
        choices.AddRange(_mapDatabase.WarpsFor(group, map)
            .Select(w => new MapLocationChoice(w.Label, w.X, w.Y)));
        TeleportLocationBox.ItemsSource = choices;
        TeleportLocationBox.SelectedIndex = choices.Count > 0 ? 0 : -1;
    }

    private void SelectTeleportMap(int group, int map)
    {
        var groupIndex = _mapGroupChoices.ToList().FindIndex(g => g.Id == group);
        if (groupIndex >= 0)
        {
            TeleportGroupBox.SelectedIndex = groupIndex;
            RefreshTeleportMaps();
        }

        if (TeleportMapBox.ItemsSource is IEnumerable<ChoiceRow> maps)
        {
            var mapRows = maps.ToArray();
            var mapIndex = Array.FindIndex(mapRows, m => m.Id == map);
            if (mapIndex >= 0)
            {
                TeleportMapBox.SelectedIndex = mapIndex;
                RefreshTeleportLocations();
            }
        }
    }

    private static Dictionary<string, string> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2) values[pieces[0]] = pieces[1];
        }
        return values;
    }

    private static int ParseStatusInt(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var text))
            throw new InvalidOperationException($"状态中缺少 {key}。");
        return ParseInt(text);
    }

    private bool TryReadCurrentEvTotal(string label, out int evTotal)
    {
        try
        {
            evTotal = BuildByteStats(EvHpBox, EvAtkBox, EvDefBox, EvSpeBox, EvSpaBox, EvSpdBox).Values.Sum(x => x);
            return true;
        }
        catch (Exception ex)
        {
            evTotal = 0;
            SetReady(label + "失败。");
            ShowToast($"{label}失败：{ex.Message}", success: false);
            Log("错误：" + ex.Message);
            return false;
        }
    }

    private bool TryReadBoxEvTotal(string label, out int evTotal)
    {
        try
        {
            evTotal = BuildByteStats(BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpeBox, BoxEvSpaBox, BoxEvSpdBox).Values.Sum(x => x);
            return true;
        }
        catch (Exception ex)
        {
            evTotal = 0;
            SetReady(label + "失败。");
            ShowToast($"{label}失败：{ex.Message}", success: false);
            Log("错误：" + ex.Message);
            return false;
        }
    }

    private async Task<bool> ConfirmHighEvTotalAsync(int evTotal)
    {
        if (evTotal <= 510) return true;

        var dialog = new Window
        {
            Title = "努力值风险提示",
            Width = 520,
            Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = $"努力值总和为 {evTotal}，已超过通常限制 510。\n继续写入可能发生未知错误或坏档。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 43, 43)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = _dataSourceMode == DataSourceMode.SaveFile
                ? "确定继续应用到待保存副本吗？保存前请确认已有原始备份。"
                : "确定继续写入吗？建议先保存 mGBA 即时存档。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = _dataSourceMode == DataSourceMode.SaveFile ? "继续应用" : "确定写入", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        if (!confirmed)
            ShowToast("已取消写入。", success: false);
        return confirmed;
    }

    private async Task<bool> ConfirmBagMachineBatchAsync(string title, int count)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = $"即将向招式机器/秘传机器口袋补齐 {count} 个候选道具。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 43, 43)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = _dataSourceMode == DataSourceMode.SaveFile
                ? $"已有机器和新增机器的数量都会设为 {BatchQuantityForPocket(MachinePocket)}。此步骤只更新待保存副本，尚未写回原存档。"
                : "已有机器会跳过，不会叠加数量。写入前建议先保存 mGBA 即时存档；写入后需要在游戏中手动存档才会保留。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = "确认写入", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        if (!confirmed)
            ShowToast("已取消写入。", success: false);
        return confirmed;
    }

    private async Task<bool> ConfirmBagPocketBatchAsync(string title, int pocket, int count)
    {
        var isKeyItems = IsKeyItemPocket(pocket);
        var dialog = new Window
        {
            Title = title,
            Width = 600,
            Height = isKeyItems ? 310 : 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = $"即将向{PocketNameZh(pocket)}补齐 {count} 个候选道具。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 43, 43)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = isKeyItems
                ? _dataSourceMode == DataSourceMode.SaveFile
                    ? "重要物品会影响剧情标记和流程状态，可能导致剧情卡住、NPC 不触发或进程无法继续，不建议使用。此步骤只更新待保存副本。"
                    : "重要物品会直接影响剧情标记和流程状态，可能导致剧情卡住、NPC 不触发或进程无法继续，不建议使用。继续前务必保存 mGBA 即时存档。"
                : _dataSourceMode == DataSourceMode.SaveFile
                    ? $"已有道具会把数量改为 {BatchQuantityForPocket(pocket)}，缺失道具会写入空槽。此步骤只更新待保存副本。"
                    : $"已有道具会把数量改为 {BatchQuantityForPocket(pocket)}，缺失道具会写入空槽。写入后需要在游戏中手动存档才会保留。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(isKeyItems ? Color.FromRgb(150, 43, 43) : Color.FromRgb(108, 98, 85)),
            FontWeight = isKeyItems ? FontWeight.SemiBold : FontWeight.Normal
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = isKeyItems ? "确认风险并写入" : "确认写入", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        if (!confirmed)
            ShowToast("已取消写入。", success: false);
        return confirmed;
    }

    private async Task<bool> ConfirmDexImportFallbackBoxAsync(int selectedBox, int fallbackBox)
    {
        var dialog = new Window
        {
            Title = "当前箱子已满",
            Width = 520,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = $"箱{selectedBox:00}已满，是否导入到箱{fallbackBox:00}？",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 43, 43)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "程序会使用建议箱子中的第一个空槽，不会覆盖已有宝可梦。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = $"导入到箱{fallbackBox:00}", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        if (!confirmed)
            ShowToast("已取消导入箱子。", success: false);
        return confirmed;
    }

    private async Task<bool> ConfirmBoxDeleteAsync(BoxSlotRow row)
    {
        var dialog = new Window
        {
            Title = "删除箱子精灵",
            Width = 560,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = $"即将清空选中的箱子槽：\n{row.Title}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 43, 43)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "删除前强烈建议先保存 mGBA 即时存档；删除后需要在游戏中手动存档才会保留。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = "确认删除", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        if (!confirmed)
            ShowToast("已取消删除。", success: false);
        return confirmed;
    }

    private async Task<bool> ConfirmPartyDeleteAsync(PartySlotRow row)
    {
        var dialog = new Window
        {
            Title = "删除队伍精灵",
            Width = 560,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        root.Children.Add(new TextBlock
        {
            Text = $"即将删除选中的队伍槽位：\n{row.Title}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 43, 43)),
            FontWeight = FontWeight.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "删除前强烈建议先保存 mGBA 即时存档；删除后需要在游戏中手动存档才会保留。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 98, 85))
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button { Content = "取消" };
        var ok = new Button { Content = "确认删除", Classes = { "primary" } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        dialog.Content = root;

        var confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            confirmed = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        if (!confirmed)
            ShowToast("已取消删除。", success: false);
        return confirmed;
    }

    private async Task RunUiTask(string label, Action action)
    {
        try
        {
            SetBusy(label + "...");
            await Dispatcher.UIThread.InvokeAsync(() => BusyBorder.UpdateLayout(), DispatcherPriority.Render);
            await Task.Delay(80);
            action();
            SetReady(label + "完成。");
        }
        catch (Exception ex)
        {
            SetReady(label + "失败。");
            ShowToast($"{label}失败：{ex.Message}", success: false);
            if (label.Contains("写入", StringComparison.Ordinal) ||
                label.Contains("保存", StringComparison.Ordinal) ||
                label.Contains("另存", StringComparison.Ordinal) ||
                label.Contains("添加", StringComparison.Ordinal) ||
                label.Contains("获取", StringComparison.Ordinal) ||
                label.Contains("恢复", StringComparison.Ordinal) ||
                label.Contains("调整", StringComparison.Ordinal) ||
                label.Contains("传送", StringComparison.Ordinal))
            {
                SetWriteNotice($"{label}失败：{ex.Message}", success: false);
            }
            Log("错误：" + ex.Message);
        }
    }

    private async Task RunUiTask(string label, Func<Task> action)
    {
        try
        {
            SetBusy(label + "...");
            await Dispatcher.UIThread.InvokeAsync(() => BusyBorder.UpdateLayout(), DispatcherPriority.Render);
            await Task.Delay(80);
            await action();
            SetReady(label + "完成。");
        }
        catch (Exception ex)
        {
            SetReady(label + "失败。");
            ShowToast($"{label}失败：{ex.Message}", success: false);
            if (label.Contains("写入", StringComparison.Ordinal) ||
                label.Contains("保存", StringComparison.Ordinal) ||
                label.Contains("另存", StringComparison.Ordinal) ||
                label.Contains("添加", StringComparison.Ordinal) ||
                label.Contains("获取", StringComparison.Ordinal) ||
                label.Contains("恢复", StringComparison.Ordinal) ||
                label.Contains("调整", StringComparison.Ordinal) ||
                label.Contains("传送", StringComparison.Ordinal))
            {
                SetWriteNotice($"{label}失败：{ex.Message}", success: false);
            }
            Log("错误：" + ex.Message);
        }
    }

    private MgbaBridgeClient ConnectBridge()
    {
        if (_dataSourceMode == DataSourceMode.SaveFile)
            throw new InvalidOperationException("当前是存档模式，该操作只支持 mGBA 实时内存。");
        var host = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();
        var port = int.TryParse(PortBox.Text, out var parsed) ? parsed : 8765;
        var bridge = MgbaBridgeClient.Connect(host, port);
        _runtime.ValidateLiveRom(bridge);
        Log(bridge.Welcome);
        return bridge;
    }

    private uint ResolvePartyBase(MgbaBridgeClient bridge, bool forceRefresh = false)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(BaseBox.Text))
        {
            _partyBase = ParseUInt(BaseBox.Text.Trim());
            return _partyBase.Value;
        }
        if (!forceRefresh && _partyBase is not null) return _partyBase.Value;
        var ewram = PartyScanner.ReadEwram(bridge, _profile);
        var run = PartyScanner.LocateParty(
                      ewram,
                      _profile.Memory.EwramBase,
                      ActivePokemonLayout,
                      _profile.Memory.DefaultPartyBase,
                      _profile.Memory.PartyCountOffsetFromPartyBase,
                      _profile.Limits.MaxSpecies)
                  ?? throw new InvalidOperationException("没有定位到队伍。请先在游戏中载入存档，再点击“读取队伍”。");
        _partyBase = run.StartAddress;
        BaseBox.Text = $"0x{run.StartAddress:X8}";
        return run.StartAddress;
    }

    private PartySlotRow ToRow(int slot, uint addr, PartyPokemon mon)
    {
        if (mon.IsEmpty) return new PartySlotRow(slot, addr, mon, null, $"{slot} 空槽", "", null);
        var info = mon.GetInfo();
        var ok = info.Checksum == info.CalculatedChecksum ? "正常" : "异常";
        var title = $"{slot} {(mon.IsShiny ? "★" : "")}{SpeciesName(info.Species)} Lv{info.Level}";
        if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
        var item = info.Item == 0 ? "无" : ItemName(info.Item);
        var shiny = mon.IsShiny ? "闪光" : "非闪";
        var gender = GenderText(GenderChoiceId(info.Species, info.Pid));
        var detail = $"HP {info.Hp}/{info.MaxHp}  {shiny}  性别 {gender}  性格 {NatureText(info.Nature, info.GameNatureCode)}  特性 {AbilityText(info.Species, info.Ivs["ability"])}  携带 {item}  校验 {ok}";
        return new PartySlotRow(slot, addr, mon, info, title, detail, LoadSpriteBitmap(info.Species));
    }

    private void FillEditor(PartySlotRow row)
    {
        var headerInfo = row.Info;
        if (headerInfo is not null)
            UpdatePartyHeaderInfo(headerInfo.Species, headerInfo.Level);
        else
            UpdatePartyHeaderInfo(0, null);
        SetPokemonSprite(PartyDetailSpriteImage, headerInfo?.Species ?? 0);
        SelectedDetailText.Text = string.Empty;
        SelectedDetailText.IsVisible = false;
        if (row.Info is not { } info)
        {
            ClearEditor();
            return;
        }
        SetOtEditor(PartyOtNameBox, PartyOtIdText, row.Mon.Raw, info.OtId);
        _suppressEditorEvents = true;
        try
        {
            SetChoice(SpeciesBox, _speciesChoices, info.Species);
            SetChoice(ItemBox, _itemChoices, info.Item);
            SetChoice(NatureBox, _natureChoices, info.Nature);
            SetChoice(GenderBox, _genderChoices, GenderChoiceId(info.Species, info.Pid));
            RefreshAbilityChoices(info.Species, info.Ivs["ability"]);
            ExpBox.Text = info.Exp.ToString();
            FriendshipBox.Text = info.Friendship.ToString();
            UpdateEggHatchEditor(info);
            LevelBox.Text = info.Level.ToString();
            MaxHpBox.Text = info.MaxHp.ToString();
            SetChoice(StatusBox, _statusChoices, (int)NormalizeStatus(info.Status));
            ShinyBox.IsChecked = row.Mon.IsShiny;

            SetChoice(Move1Box, _moveChoices, info.Moves[0]);
            SetChoice(Move2Box, _moveChoices, info.Moves[1]);
            SetChoice(Move3Box, _moveChoices, info.Moves[2]);
            SetChoice(Move4Box, _moveChoices, info.Moves[3]);
            Pp1Box.Text = info.Pp[0].ToString();
            Pp2Box.Text = info.Pp[1].ToString();
            Pp3Box.Text = info.Pp[2].ToString();
            Pp4Box.Text = info.Pp[3].ToString();
            SetPpBonusChoices(info.PpBonuses, info.Moves, info.Pp, PpUp1Box, PpUp2Box, PpUp3Box, PpUp4Box);

            EvHpBox.Text = info.Evs[0].ToString();
            EvAtkBox.Text = info.Evs[1].ToString();
            EvDefBox.Text = info.Evs[2].ToString();
            EvSpeBox.Text = info.Evs[3].ToString();
            EvSpaBox.Text = info.Evs[4].ToString();
            EvSpdBox.Text = info.Evs[5].ToString();

            IvHpBox.Text = info.Ivs["hp"].ToString();
            IvAtkBox.Text = info.Ivs["atk"].ToString();
            IvDefBox.Text = info.Ivs["def"].ToString();
            IvSpeBox.Text = info.Ivs["spe"].ToString();
            IvSpaBox.Text = info.Ivs["spa"].ToString();
            IvSpdBox.Text = info.Ivs["spd"].ToString();
            UpdateStatTexts(info);
        }
        finally
        {
            _suppressEditorEvents = false;
        }
        UpdateNameHints(info);
    }

    private void ClearEditor()
    {
        _suppressEditorEvents = true;
        try
        {
            SpeciesBox.Text = string.Empty;
            SpeciesBox.SelectedItem = null;
            ItemBox.Text = string.Empty;
            ItemBox.SelectedItem = null;
            Move1Box.Text = string.Empty;
            Move1Box.SelectedItem = null;
            Move2Box.Text = string.Empty;
            Move2Box.SelectedItem = null;
            Move3Box.Text = string.Empty;
            Move3Box.SelectedItem = null;
            Move4Box.Text = string.Empty;
            Move4Box.SelectedItem = null;
            foreach (var box in new[] { PpUp1Box, PpUp2Box, PpUp3Box, PpUp4Box })
                SetChoice(box, _ppBonusChoices, 0);
            ClearMaxPpTexts(Move1MaxPpText, Move2MaxPpText, Move3MaxPpText, Move4MaxPpText);
            NatureBox.Text = string.Empty;
            NatureBox.SelectedItem = null;
            GenderBox.SelectedItem = null;
            StatusBox.SelectedItem = null;
            ShinyBox.IsChecked = false;
            AbilityBox.ItemsSource = null;
            AbilityDescriptionText.Text = "选择特性后显示说明。";
            foreach (var box in EditorBoxes()) box.Text = string.Empty;
            ClearStatTexts();
            SetPokemonSprite(PartyDetailSpriteImage, 0);
            UpdatePartyHeaderInfo(0, null);
            PartyOtNameBox.Text = string.Empty;
            PartyOtIdText.Text = "未选择";
            UpdateEggHatchEditor(null);
        }
        finally
        {
            _suppressEditorEvents = false;
        }
        BasicNameText.Text = "中文名、性格、特性会显示在这里。";
        MoveNameText.Text = "招式中文名会显示在这里。";
        SelectedDetailText.Text = string.Empty;
        SelectedDetailText.IsVisible = false;
    }

    private IEnumerable<TextBox> EditorBoxes()
    {
        yield return ExpBox; yield return FriendshipBox;
        yield return EggHatchStepsBox;
        yield return LevelBox; yield return MaxHpBox;
        yield return Pp1Box; yield return Pp2Box; yield return Pp3Box; yield return Pp4Box;
        yield return EvHpBox; yield return EvAtkBox; yield return EvDefBox; yield return EvSpeBox; yield return EvSpaBox; yield return EvSpdBox;
        yield return IvHpBox; yield return IvAtkBox; yield return IvDefBox; yield return IvSpeBox; yield return IvSpaBox; yield return IvSpdBox;
    }

    private void UpdateEggHatchEditor(PartyMonInfo? info)
    {
        var isEgg = info is not null && IsEgg(info);
        EggHatchPanel.IsVisible = isEgg;
        EggHatchStepsBox.Text = isEgg ? info!.Friendship.ToString() : string.Empty;
    }

    private void UpdateBoxEggHatchEditor(BoxMonInfo? info)
    {
        var isEgg = info is not null && IsEgg(info);
        BoxEggHatchPanel.IsVisible = isEgg;
        BoxEggHatchStepsBox.Text = isEgg ? info!.Friendship.ToString() : string.Empty;
    }

    private byte? ParsePartyFriendshipOrHatchCounter(PartyMonInfo info)
        => IsEgg(info) ? ParseHatchCounter(EggHatchStepsBox.Text) : ParseByteOrNull(FriendshipBox.Text);

    private byte? ParseBoxFriendshipOrHatchCounter(BoxMonInfo info)
        => IsEgg(info) ? ParseHatchCounter(BoxEggHatchStepsBox.Text) : ParseByteOrNull(BoxFriendshipBox.Text);

    private static byte ParseHatchCounter(string? text)
    {
        return ParseByteOrNull(text) ?? throw new InvalidOperationException("剩余孵化周期必须在 0..255 之间。");
    }

    private static bool IsEgg(PartyMonInfo info)
        => info.Ivs.TryGetValue("egg", out var egg) && egg == 1;

    private static bool IsEgg(BoxMonInfo info)
        => info.Ivs.TryGetValue("egg", out var egg) && egg == 1;

    private PartySlotRow? SelectedRow() => PartyList.SelectedItem as PartySlotRow;

    private BagSlotRow ToBagRow(
        uint address,
        ushort itemId,
        ushort quantity,
        string note,
        int? pocketOverride = null,
        int? slotNumber = null,
        BagPocketDefinition? definition = null)
    {
        var name = itemId == 0 ? "无" : ItemName(itemId);
        var pocket = pocketOverride ?? PocketOfItem(itemId);
        definition ??= BagScanner.DefinitionForPocket(_runtime.ScannedBagDefinitions, pocket);
        var pocketName = PocketNameZh(pocket);
        var prefix = slotNumber is null ? "" : $"{slotNumber.Value:00}. ";
        var title = itemId == 0
            ? $"{prefix}空槽"
            : IsKeyItemPocket(pocket)
                ? $"{prefix}{name}"
                : $"{prefix}{name} x{quantity}";
        var detail = $"{pocketName}  地址 0x{address:X8}  {note}";
        return new BagSlotRow(address, itemId, quantity, pocket, slotNumber ?? 0, definition?.QuantityKey ?? 0, definition?.QuantityXor == true, title, detail);
    }

    private void LoadBagRows(MgbaBridgeClient bridge)
    {
        _runtime.EnsureCanRead(GameDataSurface.Bag, live: true);
        if (UsesFixedLiveBag)
        {
            LoadFixedBagRows(bridge);
            return;
        }

        _bagBase = TryLocateBagBase(bridge);
        var ewram = PartyScanner.ReadEwram(bridge, _profile);
        var pockets = BagScanner.FindLivePockets(
                ewram, _profile.Memory.EwramBase, _bagBase,
                _runtime.BagPockets(true).Select(candidate => candidate.Id).ToArray(),
                PocketOfItem, candidate => _runtime.IsKeyItemPocket(candidate, true),
                candidate => _runtime.BagBatchCapacity(candidate, true), MaxItemId(), MaxBagScanQuantity())
            .ToArray();
        var quantityKey = BagScanner.InferQuantityKey(ewram);
        _allBagRows.Clear();
        foreach (var pocket in pockets)
        {
            var definition = new BagPocketDefinition(
                pocket.Pocket,
                PocketNameZh(pocket.Pocket),
                0,
                pocket.SlotCount,
                true,
                quantityKey);
            for (var i = 0; i < pocket.Slots.Count; i++)
            {
                var slot = pocket.Slots[i];
                _allBagRows.Add(ToBagRow(
                    slot.Address,
                    slot.ItemId,
                    slot.Quantity,
                    $"第 {i + 1} 格 / 口袋起始 0x{pocket.StartAddress:X8} / 数量密钥 0x{definition.QuantityKey:X4}",
                    pocket.Pocket,
                    i + 1,
                    definition));
            }
        }

        ApplyBagPocketFilter();
        if (_bagRows.Count > 0 && BagList.SelectedItem is null) BagList.SelectedIndex = 0;
        var nonEmpty = pockets.Sum(p => p.NonEmptyCount);
        Log($"已自动定位背包口袋：基址 {(_bagBase is null ? "未知" : $"0x{_bagBase:X8}")}，数量密钥 0x{quantityKey:X4}，非空 {nonEmpty} 格，当前筛选显示 {_bagRows.Count} 格。");
        if (_bagRows.Count == 0 && SelectedChoiceId(BagPocketTabs) is { } selected && selected > 0)
            Log($"{PocketNameZh(selected)} 当前未定位到道具，按空口袋处理。");
        if (nonEmpty == 0)
            Log("没有读到可信背包槽：当前画面可能不是可读状态，或该存档需要用“校准背包”重新定位。");
    }

    private void LoadFixedBagRows(MgbaBridgeClient bridge)
    {
        _runtime.EnsureCanRead(GameDataSurface.Bag, live: true);
        var areas = _profile.Runtime.LiveBag.Areas;
        var quantityKey = ReadProfileQuantityKey(bridge);
        _bagBase = _profile.Runtime.LiveBag.BaseAddress;
        _allBagRows.Clear();
        foreach (var area in areas)
        {
            var raw = bridge.Read(area.Address, area.Capacity * 4);
            var displaySlot = 0;
            for (var i = 0; i < area.Capacity; i++)
            {
                var item = ReadU16(raw, i * 4);
                if (item == 0) continue;
                if (item > MaxItemId()) continue;
                var quantity = (ushort)(ReadU16(raw, i * 4 + 2) ^ quantityKey);
                if (quantity == 0) quantity = 1;
                var definition = new BagPocketDefinition(area.Pocket, area.Name, 0, area.Capacity, true, quantityKey);
                _allBagRows.Add(ToBagRow(
                    area.Address + (uint)(i * 4), item, quantity,
                    $"第 {i + 1} 格 / 当前版本固定口袋 / 数量密钥 0x{quantityKey:X4}",
                    area.Pocket, ++displaySlot, definition));
            }
        }
        ApplyBagPocketFilter();
        if (_bagRows.Count > 0 && BagList.SelectedItem is null) BagList.SelectedIndex = 0;
        Log($"已按 {_profile.DisplayName} 独立 Profile 的固定地址读取背包：非空 {_allBagRows.Count} 格，数量密钥 0x{quantityKey:X4}。");
    }

    private ushort ReadProfileQuantityKey(MgbaBridgeClient bridge)
    {
        if (_profile.Runtime.LiveBag.QuantityKeyMode != "save-block2-key-low16")
            throw new InvalidOperationException($"当前 Profile 不支持固定背包数量密钥模式：{_profile.Runtime.LiveBag.QuantityKeyMode}");
        var saveBlock2 = ReadU32Le(bridge.Read(_profile.Memory.SaveBlock2PointerAddress, 4), 0);
        return (ushort)(ReadU32Le(bridge.Read(saveBlock2 + _profile.Memory.SaveBlock2EncryptionKeyOffset, 4), 0) & 0xFFFF);
    }

    private GameProfileLiveBagArea? ProfileLiveBagArea(int pocket)
        => _runtime.LiveBagArea(pocket);

    private void LoadSaveBagRows(IEnumerable<Gen3SaveBagEntry> entries, int? selectedOffset = null)
    {
        _bagRows.Clear();
        _allBagRows.Clear();
        foreach (var entry in entries.OrderBy(entry => entry.SaveOffset))
        {
            var pocket = IsConcreteBagPocket(entry.Pocket) ? entry.Pocket : PocketOfItem(entry.ItemId);
            var definition = new BagPocketDefinition(
                pocket,
                PocketNameZh(pocket),
                0,
                0,
                entry.QuantityXor,
                entry.QuantityKey);
            _allBagRows.Add(ToBagRow(
                (uint)entry.SaveOffset,
                entry.ItemId,
                entry.Quantity,
                entry.Note,
                pocket,
                entry.SlotInPocket,
                definition));
        }

        ApplyBagPocketFilter();
        SelectSaveBagRow(selectedOffset is null ? null : (uint)selectedOffset.Value);
    }

    private void RefreshSaveBagEditableRows(uint? selectedAddress = null)
    {
        if (_dataSourceMode != DataSourceMode.SaveFile) return;
        selectedAddress ??= (SaveBagList.SelectedItem as SaveBagEditableRow)?.Row.Address;
        _saveBagRows.Clear();
        foreach (var row in _bagRows)
        {
            var choices = BagItemChoicesForPocket(row.Pocket);
            _saveBagRows.Add(new SaveBagEditableRow(
                row,
                choices,
                choices.FirstOrDefault(choice => choice.Id == row.ItemId),
                row.Pocket == 8 ? "1" : row.Quantity.ToString()));
        }
        SelectSaveBagRow(selectedAddress);
    }

    private void SelectSaveBagRow(uint? address)
    {
        if (_dataSourceMode != DataSourceMode.SaveFile) return;
        SaveBagList.SelectedItem = address is null
            ? null
            : _saveBagRows.FirstOrDefault(editable => editable.Row.Address == address.Value);
        if (SaveBagList.SelectedItem is null && _saveBagRows.Count > 0)
            SaveBagList.SelectedIndex = 0;
    }

    private void LoadBoxRows(MgbaBridgeClient bridge)
    {
        _runtime.EnsureCanRead(GameDataSurface.Boxes, live: true);
        var ewram = PartyScanner.ReadEwram(bridge, _profile);
        if (_profile.Memory.PcBoxStoragePointerAddress != 0)
        {
            LoadPointerBoxRows(bridge, ewram);
            return;
        }
        if (_profile.Memory.PcBoxRegions.Count > 0)
        {
            LoadProfileBoxRows(ewram);
            return;
        }

        var storage = BoxScanner.LocatePcStorage(
            ewram,
            _profile.Memory.EwramBase,
            maxSpecies: _profile.Limits.MaxSpecies,
            maxBoxes: _profile.Memory.PcBoxCount,
            slotsPerBox: _profile.Memory.PcBoxSlots,
            minScore: 12,
            layout: ActiveBoxLayout);
        if (storage is null)
        {
            LoadBoxRowsFromBestRun(ewram);
            return;
        }

        _boxBase = storage.StartAddress;
        _boxGridShowAllBoxes = true;
        _boxRows.Clear();
        foreach (var (globalSlot, candidate) in storage.CandidatesBySlot.OrderBy(kv => kv.Key))
        {
            var address = candidate.Address;
            var offset = checked((int)(address - _profile.Memory.EwramBase));
            var mon = new BoxPokemon(ewram.AsSpan(offset, ActiveBoxRecordSize), ActiveBoxLayout);
            var info = mon.GetInfo();
            var slotInBox = ((globalSlot - 1) % _profile.Memory.PcBoxSlots) + 1;
            var boxNo = ((globalSlot - 1) / _profile.Memory.PcBoxSlots) + 1;
            var title = BoxDisplayTitle(boxNo, slotInBox, mon, info);
            if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
            _boxRows.Add(new BoxSlotRow(globalSlot, address, mon, info, title, $"地址 0x{address:X8}", BoxDisplaySprite(info)));
        }

        RefreshBoxGridGroups();
        if (_boxRows.Count > 0) BoxList.SelectedIndex = 0;
        else
        {
            UpdateBoxHeaderInfo(0, null);
            UpdateBoxEggHatchEditor(null);
        }
        Log($"已定位箱子存储：起始 0x{storage.StartAddress:X8}，非空 {storage.NonEmptyCount}/{_profile.Memory.PcBoxCount * _profile.Memory.PcBoxSlots} 槽。");
    }

    private void LoadPointerBoxRows(MgbaBridgeClient bridge, ReadOnlySpan<byte> ewram)
    {
        var recordsBase = ResolvePointerBoxRecordsBase(bridge);
        _boxBase = recordsBase;
        _boxGridShowAllBoxes = true;
        _boxRows.Clear();
        var totalSlots = _profile.Memory.PcBoxCount * _profile.Memory.PcBoxSlots;
        for (var globalSlot = 1; globalSlot <= totalSlots; globalSlot++)
        {
            var address = recordsBase + checked((uint)((globalSlot - 1) * ActiveBoxRecordSize));
            var offset = checked((int)(address - _profile.Memory.EwramBase));
            var raw = ewram.Slice(offset, ActiveBoxRecordSize);
            if (IsAllZero(raw)) continue;
            var mon = new BoxPokemon(raw, ActiveBoxLayout);
            if (mon.IsEmpty || !mon.HasValidHeader(_profile.Limits.MaxSpecies)) continue;
            var info = mon.GetInfo();
            var boxNumber = (globalSlot - 1) / _profile.Memory.PcBoxSlots + 1;
            var slotInBox = (globalSlot - 1) % _profile.Memory.PcBoxSlots + 1;
            var title = BoxDisplayTitle(boxNumber, slotInBox, mon, info);
            _boxRows.Add(new BoxSlotRow(globalSlot, address, mon, info, title, $"地址 0x{address:X8}", BoxDisplaySprite(info)));
        }

        RefreshBoxGridGroups();
        BoxList.SelectedIndex = _boxRows.Count > 0 ? 0 : -1;
        if (_boxRows.Count == 0)
        {
            UpdateBoxHeaderInfo(0, null);
            UpdateBoxEggHatchEditor(null);
        }
        Log($"已按 Profile 指针 0x{_profile.Memory.PcBoxStoragePointerAddress:X8} 读取 {_profile.Memory.PcBoxCount} 个实时箱子，非空 {_boxRows.Count}/{totalSlots} 槽。");
    }

    private uint ResolvePointerBoxRecordsBase(MgbaBridgeClient bridge)
    {
        var pointerAddress = _profile.Memory.PcBoxStoragePointerAddress;
        if (pointerAddress == 0)
            throw new InvalidOperationException("当前 Profile 没有配置 PC 箱子存储指针。");
        var storageBase = ReadU32Le(bridge.Read(pointerAddress, 4), 0);
        var recordsBase = storageBase + checked((uint)_profile.Memory.PcBoxDataOffset);
        var byteLength = checked((uint)(_profile.Memory.PcBoxCount * _profile.Memory.PcBoxSlots * ActiveBoxRecordSize));
        var ewramEnd = _profile.Memory.EwramBase + checked((uint)_profile.Memory.EwramSize);
        if (storageBase < _profile.Memory.EwramBase || recordsBase < storageBase || recordsBase + byteLength > ewramEnd)
            throw new InvalidOperationException($"PC 箱子指针 0x{pointerAddress:X8} 未指向完整 EWRAM 箱子区域，请确认游戏已经载入存档。");
        return recordsBase;
    }

    private int WritableBoxSlotCount(bool live)
    {
        var total = _profile.Memory.PcBoxCount * _profile.Memory.PcBoxSlots;
        var configured = live
            ? _profile.Memory.LivePcBoxWritableSlotCount
            : _profile.Memory.SavePcBoxWritableSlotCount;
        return configured > 0 ? Math.Min(configured, total) : total;
    }

    private int WritableBoxCount(bool live)
        => (WritableBoxSlotCount(live) + _profile.Memory.PcBoxSlots - 1) / _profile.Memory.PcBoxSlots;

    private void ConfigureDexImportBoxChoices(bool live)
    {
        _dexImportBoxManuallySelected = false;
        _dexImportBoxChoices = Enumerable.Range(1, WritableBoxCount(live))
            .Select(box => new ChoiceRow(box, box.ToString("00")))
            .ToArray();
        _suppressDexImportBoxSelection = true;
        try
        {
            DexImportBoxNumberBox.ItemsSource = _dexImportBoxChoices;
            DexImportBoxNumberBox.SelectedIndex = _dexImportBoxChoices.Length > 0 ? 0 : -1;
        }
        finally
        {
            _suppressDexImportBoxSelection = false;
        }
    }

    private void SetDexImportBoxNumber(int boxNumber, bool manuallySelected)
    {
        var choice = _dexImportBoxChoices.FirstOrDefault(choice => choice.Id == boxNumber);
        if (choice is null) return;
        _suppressDexImportBoxSelection = true;
        try
        {
            DexImportBoxNumberBox.SelectedItem = choice;
        }
        finally
        {
            _suppressDexImportBoxSelection = false;
        }
        _dexImportBoxManuallySelected = manuallySelected;
    }

    private void OnDexImportBoxNumberChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_suppressDexImportBoxSelection && DexImportBoxNumberBox.SelectedItem is ChoiceRow)
            _dexImportBoxManuallySelected = true;
    }

    private void UpdateDexImportBoxDefaultFromKnownRows()
    {
        if (_dexImportBoxManuallySelected || _dexImportBoxChoices.Length == 0) return;
        var occupied = _boxRows.Select(row => row.Slot).ToHashSet();
        var firstEmptySlot = Enumerable.Range(1, WritableBoxSlotCount(_dataSourceMode == DataSourceMode.Live))
            .FirstOrDefault(slot => !occupied.Contains(slot));
        if (firstEmptySlot == 0) return;
        var boxNumber = (firstEmptySlot - 1) / _profile.Memory.PcBoxSlots + 1;
        SetDexImportBoxNumber(boxNumber, manuallySelected: false);
    }

    private void LoadProfileBoxRows(ReadOnlySpan<byte> ewram)
    {
        _boxBase = _profile.Memory.PcBoxRegions.OrderBy(region => region.FirstBox).First().Address;
        _boxGridShowAllBoxes = true;
        _boxRows.Clear();
        var totalSlots = _profile.Memory.PcBoxCount * _profile.Memory.PcBoxSlots;
        for (var globalSlot = 1; globalSlot <= totalSlots; globalSlot++)
        {
            var address = ProfileBoxAddress(globalSlot);
            var offset = checked((int)(address - _profile.Memory.EwramBase));
            if (offset < 0 || offset + ActiveBoxRecordSize > ewram.Length)
                throw new InvalidOperationException($"箱子槽 {globalSlot} 的配置地址超出 EWRAM 范围。");
            var raw = ewram.Slice(offset, ActiveBoxRecordSize);
            if (IsAllZero(raw)) continue;
            var mon = new BoxPokemon(raw, ActiveBoxLayout);
            if (mon.IsEmpty) continue;
            var info = mon.GetInfo();
            if (!mon.HasValidHeader(_profile.Limits.MaxSpecies)) continue;
            var slotInBox = (globalSlot - 1) % _profile.Memory.PcBoxSlots + 1;
            var boxNo = (globalSlot - 1) / _profile.Memory.PcBoxSlots + 1;
            var title = BoxDisplayTitle(boxNo, slotInBox, mon, info);
            _boxRows.Add(new BoxSlotRow(globalSlot, address, mon, info, title, $"地址 0x{address:X8}", BoxDisplaySprite(info)));
        }

        RefreshBoxGridGroups();
        BoxList.SelectedIndex = _boxRows.Count > 0 ? 0 : -1;
        if (_boxRows.Count == 0)
        {
            UpdateBoxHeaderInfo(0, null);
            UpdateBoxEggHatchEditor(null);
        }
        Log($"已按版本配置读取 {_profile.Memory.PcBoxCount} 个箱子，非空 {_boxRows.Count}/{totalSlots} 槽。");
    }

    private uint ProfileBoxAddress(int globalSlot)
    {
        if (globalSlot < 1 || globalSlot > _profile.Memory.PcBoxCount * _profile.Memory.PcBoxSlots)
            throw new ArgumentOutOfRangeException(nameof(globalSlot), "箱子槽位超出版本配置范围。");
        var boxNumber = (globalSlot - 1) / _profile.Memory.PcBoxSlots + 1;
        var slotInBox = (globalSlot - 1) % _profile.Memory.PcBoxSlots;
        var region = _profile.Memory.PcBoxRegions.FirstOrDefault(candidate =>
            boxNumber >= candidate.FirstBox && boxNumber < candidate.FirstBox + candidate.BoxCount)
            ?? throw new InvalidOperationException($"版本配置缺少箱子 {boxNumber} 的内存区域。");
        var boxWithinRegion = boxNumber - region.FirstBox;
        var recordIndex = boxWithinRegion * _profile.Memory.PcBoxSlots + slotInBox;
        return region.Address + checked((uint)(recordIndex * ActiveBoxRecordSize));
    }

    private void LoadBoxRowsFromBestRun(ReadOnlySpan<byte> ewram)
    {
        var run = BoxScanner.LocateBestRun(ewram, _profile.Memory.EwramBase, _profile.Limits.MaxSpecies, ActiveBoxLayout) ?? throw new InvalidOperationException("没有定位到箱子宝可梦。请先确保箱子里有非空槽位。");
        _boxBase = run.StartAddress;
        _boxGridShowAllBoxes = false;
        _boxRows.Clear();
        for (var i = 0; i < run.Candidates.Count; i++)
        {
            var address = run.Candidates[i].Address;
            var offset = checked((int)(address - _profile.Memory.EwramBase));
            var mon = new BoxPokemon(ewram.Slice(offset, ActiveBoxRecordSize), ActiveBoxLayout);
            var info = mon.GetInfo();
            var slotInBox = (i % _profile.Memory.PcBoxSlots) + 1;
            var boxNo = (i / _profile.Memory.PcBoxSlots) + 1;
            var title = BoxDisplayTitle(boxNo, slotInBox, mon, info);
            if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
            _boxRows.Add(new BoxSlotRow(i + 1, address, mon, info, title, $"地址 0x{address:X8}", BoxDisplaySprite(info)));
        }

        RefreshBoxGridGroups();
        if (_boxRows.Count > 0) BoxList.SelectedIndex = 0;
        else
        {
            UpdateBoxHeaderInfo(0, null);
            UpdateBoxEggHatchEditor(null);
        }
        Log($"只定位到连续箱子候选：起始 0x{run.StartAddress:X8}，连续非空 {_boxRows.Count} 槽；箱号可能不完整。");
    }

    private void RefreshBoxGridGroups()
    {
        _boxGridGroups.Clear();
        var canWriteBoxes = CanWriteBoxesInCurrentMode();
        var rowsByBox = _boxRows.GroupBy(row => (row.Slot - 1) / _profile.Memory.PcBoxSlots + 1)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var boxNumbers = _boxGridShowAllBoxes
            ? Enumerable.Range(1, _profile.Memory.PcBoxCount)
            : rowsByBox.Keys.OrderBy(x => x);
        foreach (var boxNumber in boxNumbers)
        {
            var bySlot = rowsByBox.TryGetValue(boxNumber, out var groupRows)
                ? groupRows.ToDictionary(row => (row.Slot - 1) % _profile.Memory.PcBoxSlots + 1)
                : new Dictionary<int, BoxSlotRow>();
            var cells = Enumerable.Range(1, _profile.Memory.PcBoxSlots)
                .Select(slotInBox =>
                {
                    var globalSlot = (boxNumber - 1) * _profile.Memory.PcBoxSlots + slotInBox;
                    var writable = canWriteBoxes &&
                                   globalSlot <= WritableBoxSlotCount(_dataSourceMode == DataSourceMode.Live);
                    if (bySlot.TryGetValue(slotInBox, out var row))
                        return new BoxGridCell(globalSlot, slotInBox, row, row.Sprite, row.Title, true, writable);
                    return new BoxGridCell(
                        globalSlot,
                        slotInBox,
                        null,
                        null,
                        writable ? "空槽，可拖入宝可梦" : "该槽不在当前 Profile 的安全写入范围",
                        false,
                        writable);
                })
                .ToArray();
            _boxGridGroups.Add(new BoxGridGroup(boxNumber, $"箱{boxNumber:00}", cells));
        }
        UpdateDexImportBoxDefaultFromKnownRows();
    }

    private bool CanWriteBoxesInCurrentMode()
    {
        var live = _dataSourceMode == DataSourceMode.Live;
        return _runtime.CanWrite(GameDataSurface.Boxes, live) &&
               (live || _loadedSave?.CanWrite == true);
    }

    private string BoxDisplayTitle(int boxNumber, int slotInBox, BoxPokemon mon, BoxMonInfo info)
    {
        if (IsEgg(info))
            return $"箱{boxNumber:00}-{slotInBox:00} 蛋";

        var title = $"箱{boxNumber:00}-{slotInBox:00} {(mon.IsShiny ? "★" : "")}{SpeciesName(info.Species)}";
        if (info.Item != 0) title += $" / {ItemName(info.Item)}";
        return title;
    }

    private Bitmap? BoxDisplaySprite(BoxMonInfo info)
        => IsEgg(info) ? LoadEggSpriteBitmap() : LoadSpriteBitmap(info.Species);

    private void ApplyBagPocketFilter()
    {
        if (_bagRows is null || BagPocketTabs is null || BagItemBox is null || _itemChoices is null) return;
        var selectedPocket = SelectedChoiceId(BagPocketTabs) ?? 0;
        UpdateBagBatchButtons(selectedPocket);
        var rows = selectedPocket == 0
            ? _allBagRows
            : _allBagRows.Where(row => row.Pocket == selectedPocket);
        _bagRows.Clear();
        foreach (var row in rows)
            _bagRows.Add(row);
        if (_dataSourceMode == DataSourceMode.SaveFile)
        {
            RefreshSaveBagEditableRows();
            UpdateBagNameText();
            return;
        }
        if (BagList.SelectedItem is BagSlotRow selectedRow && _bagRows.Contains(selectedRow))
            SetBagItemChoicesForPocket(selectedRow.Pocket);
        else
            SetBagItemChoicesForPocket(selectedPocket);
        UpdateBagNameText();
    }

    private void UpdateBagBatchButtons(int? selectedPocket = null)
    {
        if (BagGiveAllCurrentPocketButton is null || BagGiveAllTmsButton is null || BagGiveAllHmsButton is null) return;
        if (_dataSourceMode == DataSourceMode.SaveFile && _loadedSave?.CanWrite != true)
        {
            BagGiveAllCurrentPocketButton.IsVisible = false;
            BagGiveAllTmsButton.IsVisible = false;
            BagGiveAllHmsButton.IsVisible = false;
            return;
        }

        var pocket = selectedPocket ?? SelectedChoiceId(BagPocketTabs) ?? 0;
        var isConcretePocket = IsConcreteBagPocket(pocket);
        var isMachinePocket = pocket == MachinePocket;
        if (UsesFixedLiveBag && _dataSourceMode == DataSourceMode.Live)
        {
            BagGiveAllCurrentPocketButton.IsVisible = isConcretePocket;
            BagGiveAllTmsButton.IsVisible = isMachinePocket;
            BagGiveAllHmsButton.IsVisible = isMachinePocket;
            return;
        }

        BagGiveAllCurrentPocketButton.IsVisible = isConcretePocket && (_dataSourceMode == DataSourceMode.SaveFile || !isMachinePocket);
        BagGiveAllTmsButton.IsVisible = isMachinePocket;
        BagGiveAllHmsButton.IsVisible = isMachinePocket;
    }

    private void HookNameRefresh()
    {
        SpeciesBox.SelectionChanged += (_, _) =>
        {
            SyncPartyExperienceFromLevel();
            UpdateNameHintsFromBoxes();
        };
        ItemBox.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        NatureBox.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        GenderBox.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        AbilityBox.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        ShinyBox.IsCheckedChanged += (_, _) => UpdateNameHintsFromBoxes();
        Move1Box.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        Move2Box.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        Move3Box.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        Move4Box.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        PpUp1Box.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        PpUp2Box.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        PpUp3Box.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        PpUp4Box.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        LevelBox.TextChanged += (_, _) =>
        {
            SyncPartyExperienceFromLevel();
            UpdateNameHintsFromBoxes();
        };
        ExpBox.TextChanged += (_, _) =>
        {
            SyncPartyLevelFromExperience();
            UpdateNameHintsFromBoxes();
        };
        BagItemBox.SelectionChanged += (_, _) => UpdateBagNameText();
        BoxSpeciesBox.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxItemBox.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxNatureBox.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxGenderBox.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxAbilityBox.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxShinyBox.IsCheckedChanged += (_, _) => UpdateBoxNameText();
        BoxMove1Box.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxMove2Box.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxMove3Box.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxMove4Box.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxPpUp1Box.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxPpUp2Box.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxPpUp3Box.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxPpUp4Box.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxExpBox.TextChanged += (_, _) => UpdateBoxNameText();
        BoxFriendshipBox.TextChanged += (_, _) => UpdateBoxNameText();
        foreach (var box in new[] { BoxIvHpBox, BoxIvAtkBox, BoxIvDefBox, BoxIvSpaBox, BoxIvSpdBox, BoxIvSpeBox, BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpaBox, BoxEvSpdBox, BoxEvSpeBox })
            box.TextChanged += (_, _) => UpdateBoxStatsFromBoxes();
        foreach (var box in new[] { IvHpBox, IvAtkBox, IvDefBox, IvSpaBox, IvSpdBox, IvSpeBox, EvHpBox, EvAtkBox, EvDefBox, EvSpaBox, EvSpdBox, EvSpeBox })
            box.TextChanged += (_, _) => UpdateStatTotalsFromBoxes();
    }

    private void UpdateNameHints(PartyMonInfo info)
    {
        UpdatePartyHeaderInfo(info.Species, info.Level);
        var itemName = info.Item == 0 ? "无" : ItemName(info.Item);
        var nature = NatureText(info.Nature, info.GameNatureCode);
        var ability = AbilityText(info.Species, info.Ivs["ability"]);
        BasicNameText.Text = $"种类：{SpeciesName(info.Species)}  携带道具：{itemName}  性格：{nature}  性别：{GenderText(GenderChoiceId(info.Species, info.Pid))}  特性：{ability}  闪光：{(PartyPokemon.IsShinyPid(info.Pid, info.OtId) ? "是" : "否")}";
        MoveNameText.Text = string.Join("  /  ", info.Moves.Select((m, i) => $"招式{i + 1}: {(m == 0 ? "无" : MoveName(m))}"));
        UpdateMaxPpTexts(
            [Move1Box, Move2Box, Move3Box, Move4Box],
            [PpUp1Box, PpUp2Box, PpUp3Box, PpUp4Box],
            [Move1MaxPpText, Move2MaxPpText, Move3MaxPpText, Move4MaxPpText]);
    }

    private void UpdateNameHintsFromBoxes()
    {
        if (_suppressEditorEvents) return;
        try
        {
            var species = ParseChoiceUShortOrNull(SpeciesBox, _speciesChoices) ?? 0;
            var item = ParseChoiceUShortOrNull(ItemBox, _itemChoices) ?? 0;
            var level = ParseByteOrNull(LevelBox.Text);
            SetPokemonSprite(PartyDetailSpriteImage, species);
            UpdatePartyHeaderInfo(species, level);
            RefreshAbilityChoices(species, SelectedAbilitySlot());
            AbilityDescriptionText.Text = AbilityDescriptionFor(species, SelectedAbilitySlot());
            EnsureValidGenderChoice(GenderBox, species);
            UpdateBaseStatTexts(species);
            var nature = SelectedChoiceId(NatureBox) is { } n ? $"PID性格：{NatureDisplays[n]}" : "PID性格：未选择";
            var gender = SelectedChoiceId(GenderBox) is { } g ? GenderText(g) : "未选择";
            var ability = AbilityBox.SelectedItem?.ToString() ?? "未选择";
            BasicNameText.Text = $"种类：{(species == 0 ? "无" : SpeciesName(species))}  携带道具：{ItemName(item)}  {nature}  性别：{gender}  特性：{ability}  闪光：{(ShinyBox.IsChecked == true ? "是" : "否")}";

            var ppUps = new[] { PpUp1Box, PpUp2Box, PpUp3Box, PpUp4Box };
            UpdateMaxPpTexts(
                [Move1Box, Move2Box, Move3Box, Move4Box],
                ppUps,
                [Move1MaxPpText, Move2MaxPpText, Move3MaxPpText, Move4MaxPpText]);
            var moves = new[] { Move1Box, Move2Box, Move3Box, Move4Box }
                .Select((box, i) =>
                {
                    var move = ParseChoiceUShortOrNull(box, _moveChoices) ?? 0;
                    return $"招式{i + 1}: {(move == 0 ? "无" : MoveName(move))} / PP提升{SelectedPpBonus(ppUps[i])}次";
                });
            MoveNameText.Text = string.Join("  /  ", moves);
        }
        catch
        {
            // 输入过程中可能出现半个十六进制等临时状态，不打断编辑。
        }
    }

    private void UpdateBagNameText()
    {
        try
        {
            var item = ParseChoiceUShortOrNull(BagItemBox, _itemChoices) ?? 0;
            var editPocket = CurrentBagEditPocket();
            var itemPocket = PocketOfItem(item);
            var warning = item != 0 && IsConcreteBagPocket(editPocket) && itemPocket != editPocket
                ? $"  注意：不属于当前{PocketNameZh(editPocket)}，不能写入。"
                : "";
            BagItemNameText.Text = item == 0
                ? "道具：无"
                : $"道具：{ItemName(item)}  口袋：{PocketNameZh(itemPocket)}{warning}";
            BagQuantityLimitText.Text = BagQuantityLimitTextFor(item, editPocket);
        }
        catch
        {
            BagItemNameText.Text = "道具ID无法解析。";
            BagQuantityLimitText.Text = "无法解析当前道具。";
        }
    }

    private string BagQuantityLimitTextFor(int item, int editPocket)
    {
        if (item == 0)
            return "清空槽位时数量必须为 0。";

        var pocket = IsConcreteBagPocket(editPocket) ? editPocket : PocketOfItem(item);
        if (IsKeyItemPocket(pocket))
            return "1（重要物品固定数量）";

        var maxQuantity = MaxBagWriteQuantityForPocket(pocket);
        return $"1..{maxQuantity}（已按当前版本 ROM 逆向确认）";
    }

    private void UpdateBoxNameText()
    {
        try
        {
            var species = ParseChoiceUShortOrNull(BoxSpeciesBox, _speciesChoices) ?? 0;
            var item = ParseChoiceUShortOrNull(BoxItemBox, _itemChoices) ?? 0;
            var exp = ParseUIntOrNull(BoxExpBox.Text);
            var friendship = ParseByteOrNull(BoxFriendshipBox.Text);
            SetPokemonSprite(BoxDetailSpriteImage, species);
            UpdateBoxHeaderInfo(species, exp);
            RefreshBoxAbilityChoices(species, SelectedChoiceId(BoxAbilityBox));
            BoxAbilityDescriptionText.Text = AbilityDescriptionFor(species, SelectedChoiceId(BoxAbilityBox));
            EnsureValidGenderChoice(BoxGenderBox, species);
            UpdateBoxStatsFromBoxes();
            var ppUps = new[] { BoxPpUp1Box, BoxPpUp2Box, BoxPpUp3Box, BoxPpUp4Box };
            UpdateMaxPpTexts(
                [BoxMove1Box, BoxMove2Box, BoxMove3Box, BoxMove4Box],
                ppUps,
                [BoxMove1MaxPpText, BoxMove2MaxPpText, BoxMove3MaxPpText, BoxMove4MaxPpText]);
            var moves = new[] { BoxMove1Box, BoxMove2Box, BoxMove3Box, BoxMove4Box }
                .Select((box, i) =>
                {
                    var move = ParseChoiceUShortOrNull(box, _moveChoices) ?? 0;
                    return $"招式{i + 1}: {(move == 0 ? "无" : MoveName(move))} / PP提升{SelectedPpBonus(ppUps[i])}次";
                });
            BoxNameText.Text = $"宝可梦：{(species == 0 ? "无" : SpeciesName(species))}  携带：{ItemName(item)}  " +
                               $"PID性格：{(SelectedChoiceId(BoxNatureBox) is { } n ? NatureDisplays[n] : "未选择")}  " +
                               $"性别：{(SelectedChoiceId(BoxGenderBox) is { } g ? GenderText(g) : "未选择")}  " +
                               $"特性：{BoxAbilityBox.SelectedItem?.ToString() ?? "未选择"}  " +
                               $"闪光：{(BoxShinyBox.IsChecked == true ? "是" : "否")}  " +
                               $"经验：{(exp is null ? "未填" : exp.Value)}  亲密度：{(friendship is null ? "未填" : friendship.Value)}\n" +
                               string.Join("  /  ", moves);
        }
        catch
        {
            BoxNameText.Text = "箱子输入内容暂时无法解析。";
        }
    }

    private void SetBagItemChoicesForPocket(int pocket, int? selectedItem = null)
    {
        _bagItemChoices = BagItemChoicesForPocket(pocket, selectedItem);
        _searchableChoices[BagItemBox] = _bagItemChoices;
        ResetSearchableComboItems(BagItemBox, _bagItemChoices, preserveSelection: selectedItem is null);
        if (selectedItem is not null)
            SetChoice(BagItemBox, _bagItemChoices, selectedItem.Value);
    }

    private ChoiceRow[] BagItemChoicesForPocket(int pocket, int? selectedItem = null)
    {
        if (!IsConcreteBagPocket(pocket))
            return _itemChoices;
        var choices = _itemChoices
            .Where(choice => choice.Id == 0 || PocketOfItem(choice.Id) == pocket)
            .ToArray();
        if (selectedItem is > 0 &&
            choices.All(choice => choice.Id != selectedItem.Value) &&
            _itemChoices.FirstOrDefault(choice => choice.Id == selectedItem.Value) is { } current)
        {
            return choices.Concat([current]).OrderBy(choice => choice.Id).ToArray();
        }
        return choices;
    }

    private int CurrentBagEditPocket()
    {
        if (_dataSourceMode == DataSourceMode.SaveFile &&
            SaveBagList.SelectedItem is SaveBagEditableRow editable &&
            string.Equals(BagAddressBox.Text, $"0x{editable.Row.Address:X8}", StringComparison.OrdinalIgnoreCase))
            return editable.Row.Pocket;
        if (BagList.SelectedItem is BagSlotRow row &&
            string.Equals(BagAddressBox.Text, $"0x{row.Address:X8}", StringComparison.OrdinalIgnoreCase))
            return row.Pocket;
        var selectedPocket = SelectedChoiceId(BagPocketTabs) ?? 0;
        return IsConcreteBagPocket(selectedPocket) ? selectedPocket : 0;
    }

    private bool IsConcreteBagPocket(int pocket)
        => _runtime.IsConcreteBagPocket(pocket, _dataSourceMode == DataSourceMode.Live);

    private bool IsKeyItemPocket(int pocket)
        => _runtime.IsKeyItemPocket(pocket, _dataSourceMode == DataSourceMode.Live);

    private int MaxBagScanQuantity()
        => _profile.Limits.MaxBagQuantity;

    private ushort MaxBagWriteQuantityForPocket(int pocket)
        => (ushort)_profile.Limits.MaxBagQuantityForPocket(pocket);

    private ushort MaxBagWriteQuantityForItem(ushort item)
    {
        var pocket = CurrentBagEditPocket();
        if (!IsConcreteBagPocket(pocket))
            pocket = PocketOfItem(item);
        return MaxBagWriteQuantityForPocket(pocket);
    }

    private ushort BatchQuantityForPocket(int pocket)
        => IsKeyItemPocket(pocket)
            ? (ushort)1
            : (ushort)Math.Min(_profile.Runtime.BagBatchQuantity, MaxBagWriteQuantityForPocket(pocket));

    private bool IsBulkImportableItem(int item)
    {
        var name = ItemName(item);
        return !name.StartsWith("道具", StringComparison.Ordinal) &&
               !name.StartsWith("未知道具", StringComparison.Ordinal);
    }

    private ushort ParseBagItemRequired(int requiredPocket, bool allowEmpty)
    {
        var item = ParseChoiceUShortRequired(BagItemBox, _itemChoices, "道具");
        if (item == 0)
        {
            if (allowEmpty) return item;
            throw new InvalidOperationException("添加道具不能选择“无”。");
        }
        if (item > MaxItemId())
            throw new InvalidOperationException($"道具 ID 必须在 0..{MaxItemId()} 范围内。");

        var actualPocket = PocketOfItem(item);
        if (IsConcreteBagPocket(requiredPocket) && actualPocket != requiredPocket)
            throw new InvalidOperationException($"“{ItemName(item)}”属于{PocketNameZh(actualPocket)}，不能写入当前{PocketNameZh(requiredPocket)}。");
        return item;
    }

    private ushort ParseBagQuantityRequired(ushort item, bool allowZero, int? requiredPocket = null)
    {
        var quantity = ParseUShortRequired(BagQuantityBox.Text, "数量");
        if (item == 0)
        {
            if (quantity == 0) return 0;
            throw new InvalidOperationException("清空槽位时数量必须为 0。");
        }

        if (!allowZero && quantity == 0)
            throw new InvalidOperationException("背包道具数量必须大于 0。");
        var maxQuantity = requiredPocket is { } pocket && IsConcreteBagPocket(pocket)
            ? MaxBagWriteQuantityForPocket(pocket)
            : MaxBagWriteQuantityForItem(item);
        if (quantity > maxQuantity)
            throw new InvalidOperationException($"背包道具数量最多只能写入 {maxQuantity}。");
        return quantity;
    }

    private static byte[] EncodeBagOverwriteSlot(ushort item, ushort quantity, ushort quantityKey, bool quantityXor)
    {
        var bytes = new byte[4];
        PutU16(bytes, 0, item);
        // Live empty slots in this ROM often store the quantity key rather than 0.
        var rawQuantity = quantityXor ? (ushort)(quantity ^ quantityKey) : quantity;
        PutU16(bytes, 2, rawQuantity);
        return bytes;
    }

    private static void PutU16(Span<byte> bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private int MaxItemId()
        => _profile.Limits.MaxItem > 0
            ? _profile.Limits.MaxItem
            : _db.Table("items").Keys.DefaultIfEmpty(0).Max();

    private int MaxSpeciesId()
        => Math.Min(ushort.MaxValue, _db.Table("species").Keys.DefaultIfEmpty(0).Max());

    private int MaxMoveId()
        => Math.Min(ushort.MaxValue, _db.Table("moves").Keys.DefaultIfEmpty(0).Max());

    private ChoiceRow[] ChoiceRows(string table)
        => _db.Table(table).OrderBy(kv => kv.Key).Select(kv => new ChoiceRow(kv.Key, kv.Value)).ToArray();

    private ChoiceRow[] ItemChoiceRows()
        => _db.Table("items")
            .Where(kv => kv.Key != 0 && !string.Equals(kv.Value, "未使用", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var machineName = MachineMoveName(kv.Key);
                var display = machineName ?? kv.Value;
                return new ChoiceRow(kv.Key, kv.Value, display);
            })
            .ToArray();

    private ChoiceRow[] SpeciesChoiceRows()
    {
        var table = _db.Table("species");
        var duplicateIdsByName = table
            .GroupBy(kv => kv.Value)
            .Where(group => group.Count() > 1)
            .ToDictionary(
                group => group.Key,
                group => group.Select(kv => kv.Key).OrderBy(id => id).ToArray());

        return table
            .Where(kv => !string.Equals(kv.Value, "未使用", StringComparison.Ordinal))
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var display = duplicateIdsByName.TryGetValue(kv.Value, out var duplicateIds)
                    ? DuplicateSpeciesDisplay(kv.Key, kv.Value, duplicateIds)
                    : kv.Value;
                return new ChoiceRow(kv.Key, kv.Value, display);
            })
            .ToArray();
    }

    private string SpeciesDisplayName(int species, string fallback)
    {
        var table = _db.Table("species");
        if (!table.TryGetValue(species, out var name))
        {
            if (!_db.Table("species_evolution_names").TryGetValue(species, out name)) return fallback;
            var extendedForm = KnownSpeciesFormLabel(species);
            return string.IsNullOrWhiteSpace(extendedForm) ? name : $"{name}（{extendedForm}）";
        }
        var duplicateIds = table
            .Where(kv => kv.Value == name)
            .Select(kv => kv.Key)
            .OrderBy(id => id)
            .ToArray();
        return duplicateIds.Length > 1 ? DuplicateSpeciesDisplay(species, name, duplicateIds) : name;
    }

    private string DuplicateSpeciesDisplay(int species, string name, IReadOnlyList<int> duplicateIds)
    {
        var form = KnownSpeciesFormLabel(species);
        if (string.IsNullOrWhiteSpace(form))
        {
            var index = -1;
            for (var i = 0; i < duplicateIds.Count; i++)
            {
                if (duplicateIds[i] == species)
                {
                    index = i;
                    break;
                }
            }
            form = index <= 0 ? "普通" : $"形态{index + 1}";
        }

        return $"{name}（{form}）";
    }

    private string? KnownSpeciesFormLabel(int species)
    {
        if (_db.Table("species_forms").TryGetValue(species, out var configured) && !string.IsNullOrWhiteSpace(configured))
            return configured;
        return _runtime.SpeciesFormLabel(species);
    }

    private string LongestKnownName(string table)
        => _db.Table(table).Values.OrderByDescending(name => name.Length).FirstOrDefault() ?? "";

    private static string ChoiceText(IReadOnlyList<ChoiceRow> choices, int id)
        => choices.FirstOrDefault(c => c.Id == id)?.ToString() ?? string.Empty;

    private void SetChoice(SearchableChoiceBox box, IReadOnlyList<ChoiceRow> choices, int id)
    {
        ResetSearchableComboItems(box, choices);
        var choice = choices.FirstOrDefault(c => c.Id == id);
        box.SelectedItem = choice;
        box.Text = choice?.ToString() ?? string.Empty;
    }

    private static void SetChoice(ComboBox box, IReadOnlyList<ChoiceRow> choices, int id)
    {
        var choice = choices.FirstOrDefault(c => c.Id == id);
        box.SelectedItem = choice;
        if (choice is not null)
            box.Text = choice.ToString();
    }

    private static ushort? ParseChoiceUShortOrNull(SearchableChoiceBox box, IReadOnlyList<ChoiceRow> choices)
    {
        var text = box.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var exact = choices.FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase) ||
                                                    string.Equals(c.Display, text, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return checked((ushort)exact.Id);
            var contains = choices.FirstOrDefault(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                                       (c.Display?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
            if (contains is not null) return checked((ushort)contains.Id);
            return ParseUShortOrNull(text);
        }
        if (box.SelectedItem is ChoiceRow selected) return checked((ushort)selected.Id);
        return null;
    }

    private static ushort ParseChoiceUShortRequired(SearchableChoiceBox box, IReadOnlyList<ChoiceRow> choices, string label)
        => ParseChoiceUShortOrNull(box, choices) ?? throw new InvalidOperationException($"缺少 {label}。");

    private ushort? ParseBoundedChoiceOrNull(SearchableChoiceBox box, IReadOnlyList<ChoiceRow> choices, int maxValue, string label)
    {
        var value = ParseChoiceUShortOrNull(box, choices);
        if (value is not null && value.Value > maxValue)
            throw new InvalidOperationException($"{label} ID 必须在 0..{maxValue} 范围内。");
        return value;
    }

    private ushort ParseBoundedChoiceRequired(SearchableChoiceBox box, IReadOnlyList<ChoiceRow> choices, int maxValue, string label)
        => ParseBoundedChoiceOrNull(box, choices, maxValue, label) ?? throw new InvalidOperationException($"缺少 {label}。");

    private ushort? ParseSpeciesOrNull(SearchableChoiceBox box)
        => ParseBoundedChoiceOrNull(box, _speciesChoices, MaxSpeciesId(), "宝可梦");

    private ushort ParseSpeciesRequired(SearchableChoiceBox box, string label)
        => ParseBoundedChoiceRequired(box, _speciesChoices, MaxSpeciesId(), label);

    private ushort? ParseItemOrNull(SearchableChoiceBox box)
        => ParseBoundedChoiceOrNull(box, _itemChoices, MaxItemId(), "道具");

    private ushort ParseItemRequired(SearchableChoiceBox box, string label)
        => ParseBoundedChoiceRequired(box, _itemChoices, MaxItemId(), label);

    private ushort? ParseMoveOrNull(SearchableChoiceBox box)
        => ParseBoundedChoiceOrNull(box, _moveChoices, MaxMoveId(), "招式");

    private uint? SelectedStatus()
        => StatusBox.SelectedItem is ChoiceRow row ? (uint)row.Id : null;

    private static uint NormalizeStatus(uint status)
    {
        if (status == 0) return 0;
        if ((status & 0x07) != 0) return 0x01;
        if ((status & 0x80) != 0) return 0x80;
        if ((status & 0x40) != 0) return 0x40;
        if ((status & 0x20) != 0) return 0x20;
        if ((status & 0x10) != 0) return 0x10;
        if ((status & 0x08) != 0) return 0x08;
        return 0;
    }

    private void SetAllIvs(int value)
    {
        IvHpBox.Text = value.ToString();
        IvAtkBox.Text = value.ToString();
        IvDefBox.Text = value.ToString();
        IvSpeBox.Text = value.ToString();
        IvSpaBox.Text = value.ToString();
        IvSpdBox.Text = value.ToString();
        UpdateStatTotalsFromBoxes();
    }

    private void SetAllBoxIvs(int value)
    {
        BoxIvHpBox.Text = value.ToString();
        BoxIvAtkBox.Text = value.ToString();
        BoxIvDefBox.Text = value.ToString();
        BoxIvSpeBox.Text = value.ToString();
        BoxIvSpaBox.Text = value.ToString();
        BoxIvSpdBox.Text = value.ToString();
        UpdateBoxStatsFromBoxes();
    }

    private void UpdateStatTexts(PartyMonInfo info)
    {
        CurrentHpStatText.Text = info.MaxHp.ToString();
        CurrentAtkStatText.Text = info.Attack.ToString();
        CurrentDefStatText.Text = info.Defense.ToString();
        CurrentSpaStatText.Text = info.SpAttack.ToString();
        CurrentSpdStatText.Text = info.SpDefense.ToString();
        CurrentSpeStatText.Text = info.Speed.ToString();
        IvTotalText.Text = Gen3Constants.StatNames.Sum(name => info.Ivs[name]).ToString();
        EvTotalText.Text = info.Evs.Sum(x => x).ToString();

        try
        {
            UpdateBaseStatTexts(info.Species);
        }
        catch
        {
            ClearBaseStatTexts();
        }
    }

    private void UpdateBaseStatTexts(int species)
    {
        if (species <= 0)
        {
            ClearBaseStatTexts();
            return;
        }

        try
        {
            var stats = ReadSpeciesStats(species);
            BaseHpStatText.Text = stats.Hp.ToString();
            BaseAtkStatText.Text = stats.Attack.ToString();
            BaseDefStatText.Text = stats.Defense.ToString();
            BaseSpaStatText.Text = stats.SpAttack.ToString();
            BaseSpdStatText.Text = stats.SpDefense.ToString();
            BaseSpeStatText.Text = stats.Speed.ToString();
            BaseTotalText.Text = (stats.Hp + stats.Attack + stats.Defense + stats.SpAttack + stats.SpDefense + stats.Speed).ToString();
        }
        catch
        {
            ClearBaseStatTexts();
        }
    }

    private void ClearBaseStatTexts()
    {
        BaseHpStatText.Text = "";
        BaseAtkStatText.Text = "";
        BaseDefStatText.Text = "";
        BaseSpaStatText.Text = "";
        BaseSpdStatText.Text = "";
        BaseSpeStatText.Text = "";
        BaseTotalText.Text = "";
    }

    private void UpdateStatTotalsFromBoxes()
    {
        if (_suppressEditorEvents) return;
        IvTotalText.Text = SumBoxes(IvHpBox, IvAtkBox, IvDefBox, IvSpaBox, IvSpdBox, IvSpeBox).ToString();
        EvTotalText.Text = SumBoxes(EvHpBox, EvAtkBox, EvDefBox, EvSpaBox, EvSpdBox, EvSpeBox).ToString();
    }

    private void ClearStatTexts()
    {
        CurrentHpStatText.Text = "";
        CurrentAtkStatText.Text = "";
        CurrentDefStatText.Text = "";
        CurrentSpaStatText.Text = "";
        CurrentSpdStatText.Text = "";
        CurrentSpeStatText.Text = "";
        ClearBaseStatTexts();
        IvTotalText.Text = "";
        EvTotalText.Text = "";
    }

    private void UpdateBoxStatTexts(BoxMonInfo info)
    {
        BoxIvTotalText.Text = Gen3Constants.StatNames.Sum(name => info.Ivs[name]).ToString();
        BoxEvTotalText.Text = info.Evs.Sum(x => x).ToString();
        try
        {
            UpdateBoxBaseStatTexts(info.Species);
            UpdateBoxCurrentStats(info.Species, info.Exp, PartyPokemon.EffectiveStatNature(info.Nature, info.GameNatureCode));
        }
        catch
        {
            ClearBoxStatTexts();
        }
    }

    private void UpdateBoxStatsFromBoxes()
    {
        if (_suppressEditorEvents) return;
        BoxIvTotalText.Text = SumBoxes(BoxIvHpBox, BoxIvAtkBox, BoxIvDefBox, BoxIvSpaBox, BoxIvSpdBox, BoxIvSpeBox).ToString();
        BoxEvTotalText.Text = SumBoxes(BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpaBox, BoxEvSpdBox, BoxEvSpeBox).ToString();
        try
        {
            var species = ParseSpeciesOrNull(BoxSpeciesBox) ?? 0;
            var exp = ParseUIntOrNull(BoxExpBox.Text) ?? 0;
            var nature = SelectedChoiceId(BoxNatureBox) ?? 0;
            UpdateBoxBaseStatTexts(species);
            UpdateBoxCurrentStats(species, exp, nature);
        }
        catch
        {
            ClearBoxCurrentStatTexts();
        }
    }

    private void UpdateBoxBaseStatTexts(int species)
    {
        if (species <= 0)
        {
            ClearBoxBaseStatTexts();
            return;
        }

        var stats = ReadSpeciesStats(species);
        BoxBaseHpStatText.Text = stats.Hp.ToString();
        BoxBaseAtkStatText.Text = stats.Attack.ToString();
        BoxBaseDefStatText.Text = stats.Defense.ToString();
        BoxBaseSpaStatText.Text = stats.SpAttack.ToString();
        BoxBaseSpdStatText.Text = stats.SpDefense.ToString();
        BoxBaseSpeStatText.Text = stats.Speed.ToString();
        BoxBaseTotalText.Text = stats.Bst.ToString();
    }

    private void UpdateBoxCurrentStats(int species, uint exp, int nature)
    {
        if (species <= 0)
        {
            ClearBoxCurrentStatTexts();
            return;
        }

        var stats = ReadSpeciesStats(species);
        var level = LevelFromExp(exp, stats.GrowthRate);
        var ivs = BuildIntStats(BoxIvHpBox, BoxIvAtkBox, BoxIvDefBox, BoxIvSpeBox, BoxIvSpaBox, BoxIvSpdBox);
        var evs = BuildByteStats(BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpeBox, BoxEvSpaBox, BoxEvSpdBox);
        BoxCurrentHpStatText.Text = CalculateHpDisplay(stats.Hp, ivs.GetValueOrDefault("hp"), evs.GetValueOrDefault("hp"), level).ToString();
        BoxCurrentAtkStatText.Text = CalculateOtherDisplay(stats.Attack, ivs.GetValueOrDefault("atk"), evs.GetValueOrDefault("atk"), level, nature, 0).ToString();
        BoxCurrentDefStatText.Text = CalculateOtherDisplay(stats.Defense, ivs.GetValueOrDefault("def"), evs.GetValueOrDefault("def"), level, nature, 1).ToString();
        BoxCurrentSpeStatText.Text = CalculateOtherDisplay(stats.Speed, ivs.GetValueOrDefault("spe"), evs.GetValueOrDefault("spe"), level, nature, 2).ToString();
        BoxCurrentSpaStatText.Text = CalculateOtherDisplay(stats.SpAttack, ivs.GetValueOrDefault("spa"), evs.GetValueOrDefault("spa"), level, nature, 3).ToString();
        BoxCurrentSpdStatText.Text = CalculateOtherDisplay(stats.SpDefense, ivs.GetValueOrDefault("spd"), evs.GetValueOrDefault("spd"), level, nature, 4).ToString();
    }

    private void SyncPartyExperienceFromLevel()
    {
        if (_suppressEditorEvents || _suppressExperienceSync) return;
        try
        {
            var species = ParseChoiceUShortOrNull(SpeciesBox, _speciesChoices);
            var level = ParseByteOrNull(LevelBox.Text);
            if (species is null or 0 || level is null || level.Value < 1 || level.Value > ProfileMaxPokemonLevel) return;
            var growthRate = ReadSpeciesStats(species.Value).GrowthRate;
            SetPartyExperienceText(ExperienceForLevel(level.Value, growthRate), level.Value);
        }
        catch
        {
            // 输入过程中允许临时非法值。
        }
    }

    private void SyncPartyLevelFromExperience()
    {
        if (_suppressEditorEvents || _suppressExperienceSync) return;
        try
        {
            var species = ParseChoiceUShortOrNull(SpeciesBox, _speciesChoices);
            var exp = ParseUIntOrNull(ExpBox.Text);
            if (species is null or 0 || exp is null || exp.Value > PokemonExperienceTable.MaxStoredExperience) return;
            var growthRate = ReadSpeciesStats(species.Value).GrowthRate;
            SetPartyExperienceText(exp.Value, LevelFromExp(exp.Value, growthRate));
        }
        catch
        {
            // 输入过程中允许临时非法值。
        }
    }

    private void SetPartyExperienceText(uint exp, int level)
    {
        _suppressExperienceSync = true;
        try
        {
            ExpBox.Text = exp.ToString();
            LevelBox.Text = Math.Clamp(level, 1, ProfileMaxPokemonLevel).ToString();
        }
        finally
        {
            _suppressExperienceSync = false;
        }
    }

    private (uint Exp, byte Level) ResolvePartyExperience(PartyMonInfo before, ushort? editedSpecies)
    {
        var targetSpecies = editedSpecies ?? before.Species;
        var parsedLevel = ParseByteOrNull(LevelBox.Text);
        var targetLevel = (int)(parsedLevel ?? before.Level);
        if (targetLevel < 1 || targetLevel > ProfileMaxPokemonLevel)
            throw new InvalidOperationException($"等级必须在 1..{ProfileMaxPokemonLevel} 之间。");

        var parsedExp = ParseUIntOrNull(ExpBox.Text);
        var enteredExp = parsedExp ?? before.Exp;
        if (enteredExp > PokemonExperienceTable.MaxStoredExperience)
            throw new InvalidOperationException($"经验最多为 {PokemonExperienceTable.MaxStoredExperience}；当前版本只保存经验字段的低 23 位。");

        var sourceGrowthRate = ReadSpeciesStats(before.Species).GrowthRate;
        var targetGrowthRate = ReadSpeciesStats(targetSpecies).GrowthRate;
        var speciesChanged = targetSpecies != before.Species;
        var levelEdited = parsedLevel is not null && parsedLevel.Value != before.Level;
        var expEdited = parsedExp is not null && parsedExp.Value != before.Exp;
        var sourceWasConsistent = _experienceTable.IsConsistent(before.Exp, before.Level, sourceGrowthRate);
        uint targetExp;

        if (levelEdited)
        {
            targetExp = ExperienceForLevel(targetLevel, targetGrowthRate);
        }
        else if (expEdited)
        {
            targetExp = enteredExp;
            targetLevel = LevelFromExp(targetExp, targetGrowthRate);
        }
        else if (speciesChanged || !sourceWasConsistent)
        {
            targetExp = _experienceTable.RemapPreservingLevelProgress(
                before.Exp,
                before.Level,
                sourceGrowthRate,
                targetLevel,
                targetGrowthRate);
        }
        else
        {
            targetExp = enteredExp;
        }

        if (!_experienceTable.IsConsistent(targetExp, targetLevel, targetGrowthRate))
        {
            if (expEdited && !levelEdited)
            {
                targetLevel = LevelFromExp(targetExp, targetGrowthRate);
            }
            else
            {
                targetExp = ExperienceForLevel(targetLevel, targetGrowthRate);
            }
        }

        SetPartyExperienceText(targetExp, targetLevel);
        return (targetExp, checked((byte)targetLevel));
    }

    private uint ResolveBoxExperience(BoxMonInfo before, ushort targetSpecies)
    {
        var enteredExp = ParseUIntOrNull(BoxExpBox.Text) ?? before.Exp;
        if (enteredExp > PokemonExperienceTable.MaxStoredExperience)
            throw new InvalidOperationException($"经验最多为 {PokemonExperienceTable.MaxStoredExperience}；当前版本只保存经验字段的低 23 位。");

        if (targetSpecies == before.Species || enteredExp != before.Exp)
            return enteredExp;

        var sourceGrowthRate = ReadSpeciesStats(before.Species).GrowthRate;
        var targetGrowthRate = ReadSpeciesStats(targetSpecies).GrowthRate;
        var level = _experienceTable.LevelFromExperience(before.Exp, sourceGrowthRate);
        return _experienceTable.RemapPreservingLevelProgress(
            before.Exp,
            level,
            sourceGrowthRate,
            level,
            targetGrowthRate);
    }

    private int LevelFromExp(uint exp, byte growthRate)
        => _experienceTable.LevelFromExperience(exp, growthRate);

    private uint ExperienceForLevel(int level, byte growthRate)
        => _experienceTable.ExperienceForLevel(Math.Clamp(level, 1, ProfileMaxPokemonLevel), growthRate);

    private static int CalculateHpDisplay(int baseStat, int iv, int ev, int level)
        => (((2 * baseStat + iv + ev / 4) * level) / 100) + level + 10;

    private static int CalculateOtherDisplay(int baseStat, int iv, int ev, int level, int nature, int statIndex)
    {
        var value = ((((2 * baseStat + iv + ev / 4) * level) / 100) + 5);
        if (nature is < 0 or > 24) return value;
        var increased = nature / 5;
        var decreased = nature % 5;
        if (increased == statIndex && decreased != statIndex) value = value * 110 / 100;
        else if (decreased == statIndex && increased != statIndex) value = value * 90 / 100;
        return value;
    }

    private void ClearBoxStatTexts()
    {
        ClearBoxCurrentStatTexts();
        ClearBoxBaseStatTexts();
        BoxIvTotalText.Text = "";
        BoxEvTotalText.Text = "";
    }

    private void ClearBoxCurrentStatTexts()
    {
        BoxCurrentHpStatText.Text = "";
        BoxCurrentAtkStatText.Text = "";
        BoxCurrentDefStatText.Text = "";
        BoxCurrentSpaStatText.Text = "";
        BoxCurrentSpdStatText.Text = "";
        BoxCurrentSpeStatText.Text = "";
    }

    private void ClearBoxBaseStatTexts()
    {
        BoxBaseHpStatText.Text = "";
        BoxBaseAtkStatText.Text = "";
        BoxBaseDefStatText.Text = "";
        BoxBaseSpaStatText.Text = "";
        BoxBaseSpdStatText.Text = "";
        BoxBaseSpeStatText.Text = "";
        BoxBaseTotalText.Text = "";
    }

    private static int SumBoxes(params TextBox[] boxes)
        => boxes.Sum(box => int.TryParse(box.Text, out var value) ? value : 0);

    private SpeciesStats ReadSpeciesStats(int species)
    {
        if (TryReadEmbeddedSpeciesStats(species, out var stats))
            return stats;

        throw MissingEmbeddedDataException("宝可梦种族值", species);
    }

    private bool TryReadEmbeddedSpeciesStats(int species, out SpeciesStats stats)
    {
        stats = default!;
        if (!_db.Table("species_stats").TryGetValue(species, out var raw)) return false;
        var parts = raw.Split('\t');
        if (parts.Length < 22) return false;
        try
        {
            byte B(int index) => byte.Parse(parts[index]);
            ushort U(int index) => ushort.Parse(parts[index]);
            ushort? OptionalAbility(int index)
            {
                var ability = U(index);
                return ability == 0 ? null : ability;
            }
            stats = new SpeciesStats(
                species, B(0), B(1), B(2), B(3), B(4), B(5), B(6), B(7),
                U(8), U(9), U(10), U(11), U(12), B(13), B(14), B(15), B(16),
                B(17), B(18), U(19), U(20), OptionalAbility(21));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadOfficialSpeciesStats(int species, out OfficialSpeciesStats stats)
    {
        stats = default!;
        if (!_db.Table("official_species_stats").TryGetValue(species, out var raw)) return false;
        var parts = raw.Split('\t');
        if (parts.Length < 6) return false;
        try
        {
            stats = new OfficialSpeciesStats(
                int.Parse(parts[0]),
                int.Parse(parts[1]),
                int.Parse(parts[2]),
                int.Parse(parts[3]),
                int.Parse(parts[4]),
                int.Parse(parts[5]));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private int GenderChoiceId(ushort species, uint pid)
        => GenderChoiceId((int)species, pid);

    private int GenderChoiceId(int species, uint pid)
    {
        if (species <= 0) return PartyPokemon.Genderless;
        return PartyPokemon.GenderFromPid(pid, ReadSpeciesStats(species).GenderRatio);
    }

    private static string GenderText(int gender) => gender switch
    {
        PartyPokemon.GenderMale => "公 ♂",
        PartyPokemon.GenderFemale => "母 ♀",
        PartyPokemon.Genderless => "无性别",
        _ => "未知"
    };

    private void EnsureValidGenderChoice(ComboBox box, int species)
    {
        if (species <= 0) return;
        var ratio = ReadSpeciesStats(species).GenderRatio;
        var selected = SelectedChoiceId(box);
        var valid = ratio switch
        {
            255 => selected == PartyPokemon.Genderless,
            254 => selected == PartyPokemon.GenderFemale,
            0 => selected == PartyPokemon.GenderMale,
            _ => selected is PartyPokemon.GenderMale or PartyPokemon.GenderFemale
        };
        if (valid) return;

        var replacement = ratio switch
        {
            255 => PartyPokemon.Genderless,
            254 => PartyPokemon.GenderFemale,
            0 => PartyPokemon.GenderMale,
            _ => PartyPokemon.GenderMale
        };
        SetChoice(box, _genderChoices, replacement);
    }

    private void ApplyGenderChoice(PartyPokemon mon, int species)
    {
        if (species <= 0) return;
        if (SelectedChoiceId(GenderBox) is not { } gender) return;
        mon.SetGender(ReadSpeciesStats(species).GenderRatio, gender);
    }

    private void ApplyGenderChoice(BoxPokemon mon, int species)
    {
        if (species <= 0) return;
        if (SelectedChoiceId(BoxGenderBox) is not { } gender) return;
        mon.SetGender(ReadSpeciesStats(species).GenderRatio, gender);
    }

    private IReadOnlyList<SpeciesEvolution> ReadSpeciesEvolutions(int species)
    {
        if (_evolutionCache.TryGetValue(species, out var cached)) return cached;

        var result = TryReadEmbeddedSpeciesEvolutions(species, out var embedded) ? embedded : [];
        _evolutionCache[species] = result;
        return result;
    }

    private bool TryReadEmbeddedSpeciesEvolutions(int species, out IReadOnlyList<SpeciesEvolution> evolutions)
    {
        evolutions = [];
        if (!_db.Table("species_evolutions").TryGetValue(species, out var raw)) return false;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var result = new List<SpeciesEvolution>();
        var slot = 0;
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(',');
            if (parts.Length < 4) return false;
            result.Add(new SpeciesEvolution(
                species,
                slot++,
                ushort.Parse(parts[0]),
                ushort.Parse(parts[1]),
                ushort.Parse(parts[2]),
                ushort.Parse(parts[3])));
        }
        evolutions = result;
        return true;
    }

    private IReadOnlyList<SpeciesLevelMove> ReadSpeciesLevelMoves(int species)
    {
        if (_levelMoveCache.TryGetValue(species, out var cached)) return cached;

        var result = TryReadEmbeddedSpeciesLevelMoves(species, out var embedded) ? embedded : [];
        _levelMoveCache[species] = result;
        return result;
    }

    private bool TryReadEmbeddedSpeciesLevelMoves(int species, out IReadOnlyList<SpeciesLevelMove> moves)
    {
        moves = [];
        if (!_db.Table("species_level_moves").TryGetValue(species, out var raw)) return false;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var result = new List<SpeciesLevelMove>();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length < 2) return false;
            result.Add(new SpeciesLevelMove(species, ushort.Parse(parts[0]), ushort.Parse(parts[1])));
        }
        moves = result;
        return true;
    }

    private ItemData ReadItemData(int item)
    {
        if (TryReadEmbeddedItemData(item, out var data))
            return data;

        throw MissingEmbeddedDataException("道具数据", item);
    }

    private bool TryReadEmbeddedItemData(int item, out ItemData data)
    {
        data = default!;
        if (!_db.Table("item_data").TryGetValue(item, out var raw)) return false;
        var parts = raw.Split('\t');
        if (parts.Length < 13) return false;
        try
        {
            ushort U(int index) => ushort.Parse(parts[index]);
            byte B(int index) => byte.Parse(parts[index]);
            uint UI(int index) => uint.Parse(parts[index]);
            data = new ItemData(item, U(0), U(1), B(2), B(3), UI(4), B(5), B(6), B(7), B(8), UI(9), B(10), UI(11), UI(12), [], []);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private MoveData ReadMoveData(int move)
    {
        if (TryReadEmbeddedMoveData(move, out var data))
            return data;

        throw MissingEmbeddedDataException("招式数据", move);
    }

    private bool TryReadEmbeddedMoveData(int move, out MoveData data)
    {
        data = default!;
        if (!_db.Table("move_data").TryGetValue(move, out var raw)) return false;
        var parts = raw.Split('\t');
        if (parts.Length < 10) return false;
        try
        {
            ushort U(int index) => ushort.Parse(parts[index]);
            byte B(int index) => byte.Parse(parts[index]);
            sbyte S(int index) => unchecked((sbyte)byte.Parse(parts[index]));
            var effect = parts.Length >= 11 ? B(10) : (byte)0;
            data = new MoveData(move, U(0), B(1), B(2), B(3), B(4), U(5), S(6), U(7), B(8), U(9), effect, []);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryReadOfficialMoveData(int move, out OfficialMoveData data)
    {
        data = default!;
        if (!_db.Table("official_move_data").TryGetValue(move, out var raw)) return false;
        var parts = raw.Split('\t');
        if (parts.Length < 3) return false;
        try
        {
            data = new OfficialMoveData(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateMaxPpTexts(IReadOnlyList<SearchableChoiceBox> moveBoxes, IReadOnlyList<ComboBox> ppBonusBoxes, IReadOnlyList<TextBlock> targets)
    {
        for (var i = 0; i < Math.Min(Math.Min(moveBoxes.Count, ppBonusBoxes.Count), targets.Count); i++)
        {
            try
            {
                var move = ParseMoveOrNull(moveBoxes[i]) ?? 0;
                targets[i].Text = move == 0 ? "" : CalculateMaxPp(ReadMoveData(move).Pp, SelectedPpBonus(ppBonusBoxes[i])).ToString();
            }
            catch
            {
                targets[i].Text = "";
            }
        }
    }

    private static void ClearMaxPpTexts(params TextBlock[] targets)
    {
        foreach (var target in targets)
            target.Text = "";
    }

    private static int CalculateMaxPp(int basePp, int ppBonus)
        => basePp <= 0 ? 0 : basePp + (basePp / 5) * Math.Clamp(ppBonus, 0, 3);

    private void RecalculateLiveStats(PartyPokemon mon)
    {
        var info = mon.GetInfo();
        var stats = ReadSpeciesStats(info.Species);
        mon.RecalculateStats(stats);
    }

    private void SyncNicknameForSpeciesChange(PartyPokemon mon, ushort? newSpecies)
    {
        if (newSpecies is null or 0) return;
        if (mon.GetInfo().Species == newSpecies.Value) return;
        mon.SetNicknameFromSpeciesNameEntry(SpeciesNameEntryBytes(newSpecies.Value));
    }

    private void SyncNicknameForSpeciesChange(BoxPokemon mon, ushort newSpecies)
    {
        if (newSpecies == 0) return;
        if (mon.GetInfo().Species == newSpecies) return;
        mon.SetNicknameFromSpeciesNameEntry(SpeciesNameEntryBytes(newSpecies));
    }

    private byte[] SpeciesNameEntryBytes(int species)
    {
        if (!_db.Table("species_nickname_bytes").TryGetValue(species, out var hex) &&
            !_db.Table("species_name_bytes").TryGetValue(species, out hex))
            throw new InvalidOperationException($"缺少宝可梦 {SpeciesName(species)} 的昵称名字字节，无法自动同步昵称。");

        try
        {
            var bytes = Convert.FromHexString(hex.Trim());
            if (bytes.Length < 10)
                throw new FormatException("entry too short");
            return bytes;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"宝可梦 {SpeciesName(species)} 的昵称名字字节格式无效，无法自动同步昵称。", ex);
        }
    }

    private void RefreshAbilityChoices(int species, int? selectedBit)
    {
        if (species <= 0)
        {
            _abilitySpecies = null;
            AbilityBox.ItemsSource = null;
            AbilityDescriptionText.Text = "选择宝可梦和特性后显示说明。";
            return;
        }
        if (_abilitySpecies == species && AbilityBox.ItemsSource is IEnumerable<object> existing)
        {
            if (selectedBit is not null)
                AbilityBox.SelectedItem = existing.OfType<ChoiceRow>().FirstOrDefault(c => c.Id == selectedBit.Value) ?? AbilityBox.SelectedItem;
            AbilityDescriptionText.Text = AbilityDescriptionFor(species, SelectedAbilitySlot());
            return;
        }

        try
        {
            var stats = ReadSpeciesStats(species);
            var choices = AbilityChoices(stats.Ability1, stats.Ability2, stats.Ability3);
            _abilitySpecies = species;
            AbilityBox.ItemsSource = choices;
            AbilityBox.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedBit) ?? choices.First();
            AbilityDescriptionText.Text = AbilityDescriptionFor(species, SelectedAbilitySlot());
        }
        catch
        {
            SetFallbackAbilityChoices(AbilityBox, species, selectedBit, ref _abilitySpecies);
            AbilityDescriptionText.Text = AbilityDescriptionFor(species, SelectedAbilitySlot());
        }
    }

    private void RefreshBoxAbilityChoices(int species, int? selectedBit)
    {
        if (species <= 0)
        {
            _boxAbilitySpecies = null;
            BoxAbilityBox.ItemsSource = null;
            BoxAbilityDescriptionText.Text = "选择宝可梦和特性后显示说明。";
            return;
        }
        if (_boxAbilitySpecies == species && BoxAbilityBox.ItemsSource is IEnumerable<object> existing)
        {
            if (selectedBit is not null)
                BoxAbilityBox.SelectedItem = existing.OfType<ChoiceRow>().FirstOrDefault(c => c.Id == selectedBit.Value) ?? BoxAbilityBox.SelectedItem;
            BoxAbilityDescriptionText.Text = AbilityDescriptionFor(species, SelectedChoiceId(BoxAbilityBox));
            return;
        }

        try
        {
            var stats = ReadSpeciesStats(species);
            var choices = AbilityChoices(stats.Ability1, stats.Ability2, stats.Ability3);
            _boxAbilitySpecies = species;
            BoxAbilityBox.ItemsSource = choices;
            BoxAbilityBox.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedBit) ?? choices.First();
            BoxAbilityDescriptionText.Text = AbilityDescriptionFor(species, SelectedChoiceId(BoxAbilityBox));
        }
        catch
        {
            SetFallbackAbilityChoices(BoxAbilityBox, species, selectedBit, ref _boxAbilitySpecies);
            BoxAbilityDescriptionText.Text = AbilityDescriptionFor(species, SelectedChoiceId(BoxAbilityBox));
        }
    }

    private void SetFallbackAbilityChoices(ComboBox target, int species, int? selectedBit, ref int? cacheSpecies)
    {
        var choices = FallbackAbilityChoices(species);
        cacheSpecies = species;
        target.ItemsSource = choices;
        target.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedBit) ?? choices.First();
    }

    private List<ChoiceRow> FallbackAbilityChoices(int species)
    {
        if (TryReadSpeciesAbilities(species, out var ability1, out var ability2, out var ability3))
            return AbilityChoices(ability1, ability2, ability3);

        return
        [
            new(0, "特性1", "特性1"),
            new(1, "特性2", "特性2"),
            new(2, "特性3（隐藏）", "特性3（隐藏）")
        ];
    }

    private List<ChoiceRow> AbilityChoices(ushort ability1, ushort ability2, ushort? ability3)
    {
        var choices = new List<ChoiceRow>
        {
            new(0, $"特性1：{AbilityName(ability1)}", $"特性1：{AbilityName(ability1)}")
        };
        if (ability2 != 0)
            choices.Add(new(1, $"特性2：{AbilityName(ability2)}", $"特性2：{AbilityName(ability2)}"));
        if (ability3 is not null and not 0)
            choices.Add(new(2, $"特性3（隐藏）：{AbilityName(ability3.Value)}", $"特性3（隐藏）：{AbilityName(ability3.Value)}"));
        return choices;
    }

    private string AbilityDescriptionFor(int species, int? abilitySlot)
    {
        if (species <= 0 || abilitySlot is null)
            return "选择宝可梦和特性后显示说明。";
        return TrySpeciesAbilityId(species, abilitySlot.Value, out var ability)
            ? AbilityTooltip(ability)
            : "当前槽位没有可用特性。";
    }

    private bool TrySpeciesAbilityId(int species, int abilitySlot, out ushort ability)
    {
        ability = 0;
        try
        {
            var stats = ReadSpeciesStats(species);
            return TryAbilityIdFromSlots(stats.Ability1, stats.Ability2, stats.Ability3, abilitySlot, out ability);
        }
        catch
        {
            if (TryReadSpeciesAbilities(species, out var ability1, out var ability2, out var ability3))
                return TryAbilityIdFromSlots(ability1, ability2, ability3, abilitySlot, out ability);
            return false;
        }
    }

    private static bool TryAbilityIdFromSlots(ushort ability1, ushort ability2, ushort? ability3, int abilitySlot, out ushort ability)
    {
        ability = abilitySlot switch
        {
            0 => ability1,
            1 when ability2 != 0 => ability2,
            2 when ability3 is not null and not 0 => ability3.Value,
            _ => (ushort)0
        };
        return ability != 0;
    }

    private bool TryReadSpeciesAbilities(int species, out ushort ability1, out ushort ability2, out ushort? ability3)
    {
        ability1 = 0;
        ability2 = 0;
        ability3 = null;
        if (!_db.Table("species_abilities").TryGetValue(species, out var raw)) return false;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            !ushort.TryParse(parts[0], out ability1) ||
            !ushort.TryParse(parts[1], out ability2))
            return false;
        if (parts.Length >= 3 && ushort.TryParse(parts[2], out var third) && third != 0)
            ability3 = third;
        return true;
    }

    private string AbilityText(int species, int abilitySlot)
    {
        try
        {
            var stats = ReadSpeciesStats(species);
            return abilitySlot switch
            {
                0 => $"{AbilityName(stats.Ability1)}（特性1）",
                1 when stats.Ability2 != 0 => $"{AbilityName(stats.Ability2)}（特性2）",
                1 => $"{AbilityName(stats.Ability1)}（特性1；第二槽为空，游戏回退）",
                2 when stats.Ability3 is not null => $"{AbilityName(stats.Ability3.Value)}（特性3·隐藏）",
                _ => $"特性{abilitySlot + 1}"
            };
        }
        catch
        {
            if (TryReadSpeciesAbilities(species, out var ability1, out var ability2, out var ability3))
            {
                return abilitySlot switch
                {
                    0 => $"{AbilityName(ability1)}（特性1）",
                    1 when ability2 != 0 => $"{AbilityName(ability2)}（特性2）",
                    1 => $"{AbilityName(ability1)}（特性1；第二槽为空，游戏回退）",
                    2 when ability3 is not null => $"{AbilityName(ability3.Value)}（特性3·隐藏）",
                    _ => $"特性{abilitySlot + 1}"
                };
            }

            return $"特性{abilitySlot + 1}";
        }
    }

    private int? SelectedAbilitySlot()
    {
        var id = SelectedChoiceId(AbilityBox);
        return id is 0 or 1 or 2 ? id : null;
    }

    private static int? SelectedChoiceId(ComboBox control)
    {
        if (control.SelectedItem is ChoiceRow row) return row.Id;
        return null;
    }

    private static int? SelectedChoiceId(SearchableChoiceBox control)
    {
        if (control.SelectedItem is ChoiceRow row) return row.Id;
        return null;
    }

    private static int? SelectedChoiceId(SelectingItemsControl? control)
    {
        if (control?.SelectedItem is ChoiceRow row) return row.Id;
        return null;
    }

    private IEnumerable<ComboBox> PpBonusBoxes()
    {
        yield return PpUp1Box;
        yield return PpUp2Box;
        yield return PpUp3Box;
        yield return PpUp4Box;
        yield return BoxPpUp1Box;
        yield return BoxPpUp2Box;
        yield return BoxPpUp3Box;
        yield return BoxPpUp4Box;
    }

    private static int SelectedPpBonus(ComboBox box)
        => SelectedChoiceId(box) is { } value ? Math.Clamp(value, 0, 3) : 0;

    private static byte BuildPpBonuses(params ComboBox[] boxes)
    {
        var packed = 0;
        for (var i = 0; i < Math.Min(4, boxes.Length); i++)
            packed |= SelectedPpBonus(boxes[i]) << (i * 2);
        return (byte)packed;
    }

    private void SetPpBonusChoices(byte packed, params ComboBox[] boxes)
    {
        for (var i = 0; i < Math.Min(4, boxes.Length); i++)
            SetChoice(boxes[i], _ppBonusChoices, (packed >> (i * 2)) & 0x03);
    }

    private void SetPpBonusChoices(byte packed, IReadOnlyList<ushort> moves, IReadOnlyList<byte> currentPp, params ComboBox[] boxes)
        => SetPpBonusChoices(packed, boxes);

    private Dictionary<string, byte> BuildByteStats(params TextBox[] boxes)
    {
        var values = new Dictionary<string, byte>();
        for (var i = 0; i < Gen3Constants.StatNames.Length; i++)
        {
            var value = ParseByteOrNull(boxes[i].Text);
            if (value is not null) values[Gen3Constants.StatNames[i]] = value.Value;
        }
        return values;
    }

    private Dictionary<string, int> BuildIntStats(params TextBox[] boxes)
    {
        var values = new Dictionary<string, int>();
        for (var i = 0; i < Gen3Constants.StatNames.Length; i++)
        {
            var text = boxes[i].Text;
            if (!string.IsNullOrWhiteSpace(text)) values[Gen3Constants.StatNames[i]] = ParseInt(text.Trim());
        }
        return values;
    }

    private void LookupTo(TextBox target, Func<string> lookup)
    {
        try
        {
            target.Text = lookup();
        }
        catch (Exception ex)
        {
            target.Text = "错误：" + ex.Message;
            Log("查询错误：" + ex.Message);
        }
    }

    private static InvalidOperationException MissingEmbeddedDataException(string label, int id)
        => new($"内置{label}缺少 ID {id}。程序使用内置派生数据，运行时不会读取 ROM。");

    private string ItemName(int item)
    {
        if (item == 0) return "无";
        if (MachineMoveName(item) is { } machineName) return machineName;
        return KnownName("items", item, "未知道具");
    }

    private string HeldItemText(ushort item)
        => item == 0 ? "无" : ItemName(item);

    private string AbilityTooltip(ushort ability)
    {
        var name = AbilityName(ability);
        if (ability == 0) return "无特性。";
        return _db.Table("ability_descriptions").TryGetValue(ability, out var description) && !string.IsNullOrWhiteSpace(description)
            ? $"{name}：{description}"
            : $"{name}：暂无特性说明。";
    }

    private static int CalculateDexStat(int baseStat, int level, bool isHp)
    {
        const int iv = 31;
        const int ev = 0;
        if (isHp && baseStat == 1) return 1;
        return isHp
            ? (((2 * baseStat + iv + ev / 4) * level) / 100) + level + 10
            : ((((2 * baseStat + iv + ev / 4) * level) / 100) + 5);
    }

    private string TypePairText(int type1, int type2)
    {
        var first = MoveTypeNameZh(type1);
        var second = MoveTypeNameZh(type2);
        return type1 == type2 ? first : $"{first} / {second}";
    }

    private static IReadOnlyList<DexTypeBadge> TypeBadges(int type1, int type2)
    {
        var badges = new List<DexTypeBadge> { TypeBadge(type1) };
        if (type2 != type1) badges.Add(TypeBadge(type2));
        return badges;
    }

    private static DexTypeBadge TypeBadge(int type)
    {
        var (background, foreground) = TypeBadgeColors(type);
        return new DexTypeBadge(MoveTypeNameZh(type), Brush.Parse(background), Brush.Parse(foreground));
    }

    private static (string Background, string Foreground) TypeBadgeColors(int type) => type switch
    {
        0 => ("#A8A77A", "#1B1A14"), // 一般
        1 => ("#C22E28", "#FFF7EF"), // 格斗
        2 => ("#A98FF3", "#191233"), // 飞行
        3 => ("#A33EA1", "#FFF3FF"), // 毒
        4 => ("#E2BF65", "#2E230D"), // 地面
        5 => ("#B6A136", "#241F08"), // 岩石
        6 => ("#A6B91A", "#1F2502"), // 虫
        7 => ("#735797", "#F7F0FF"), // 幽灵
        8 => ("#B7B7CE", "#1B2330"), // 钢
        9 => ("#6C6255", "#FFF9EE"), // ？？？
        10 => ("#EE8130", "#2E1200"), // 火
        11 => ("#6390F0", "#F2F7FF"), // 水
        12 => ("#4DBB55", "#06240B"), // 草
        13 => ("#F7D02C", "#302400"), // 电
        14 => ("#F95587", "#FFF3F7"), // 超能力
        15 => ("#96D9D6", "#073331"), // 冰
        16 => ("#6F35FC", "#F5F0FF"), // 龙
        17 => ("#705746", "#FFF7EF"), // 恶
        18 or 23 => ("#D685AD", "#33111F"),
        _ => ("#6C6255", "#FFF9EE")
    };

    private string EvolutionEdgeLabel(SpeciesEvolution evolution)
    {
        var paramItem = EvolutionParameterItemText(evolution.Parameter);
        return evolution.Method switch
        {
            1 => "亲密度",
            2 => "亲密度（白天）",
            3 => "亲密度（夜晚）",
            4 => $"{evolution.Parameter}级",
            5 => "通信交换",
            6 => $"携带{paramItem}通信交换",
            7 => paramItem,
            8 => $"{evolution.Parameter}级，攻击>防御",
            9 => $"{evolution.Parameter}级，攻击=防御",
            10 => $"{evolution.Parameter}级，攻击<防御",
            11 => $"{evolution.Parameter}级，特殊分支1",
            12 => $"{evolution.Parameter}级，特殊分支2",
            13 => $"{evolution.Parameter}级，分裂进化1",
            14 => $"{evolution.Parameter}级，分裂进化2",
            15 => $"美丽度{evolution.Parameter}",
            16 => $"使用{paramItem}",
            17 => $"白天携带{paramItem}升级",
            18 => $"夜晚携带{paramItem}升级",
            19 => $"{evolution.Parameter}级（白天）",
            20 => $"{evolution.Parameter}级（夜晚）",
            21 => $"{evolution.Parameter}级（公）",
            22 => $"{evolution.Parameter}级（母）",
            25 => $"学会{MoveName(evolution.Parameter)}后升级",
            26 => $"队伍中有{SpeciesName(evolution.Parameter)}时升级",
            254 => $"使用{paramItem}（特殊形态）",
            0 when evolution.Parameter != 0 => $"使用{paramItem}（特殊形态）",
            0xFFFF => paramItem,
            0xFFFE => $"特殊形态，使用{EvolutionParameterItemText(evolution.Parameter)}",
            0xFFFD => $"特殊形态，使用{EvolutionParameterItemText(evolution.Parameter)}",
            _ => $"特殊条件 {EvolutionParameterText(evolution.Parameter)}"
        };
    }

    private string EvolutionConditionText(SpeciesEvolution evolution)
    {
        var paramItem = EvolutionParameterItemText(evolution.Parameter);
        return evolution.Method switch
        {
            1 => "亲密度",
            2 => "亲密度（白天）",
            3 => "亲密度（夜晚）",
            4 => $"{evolution.Parameter}级",
            5 => "通信交换",
            6 => $"携带{paramItem}通信交换",
            7 => $"使用{paramItem}",
            8 => $"{evolution.Parameter}级，攻击大于防御",
            9 => $"{evolution.Parameter}级，攻击等于防御",
            10 => $"{evolution.Parameter}级，攻击小于防御",
            11 => $"{evolution.Parameter}级，特殊分支1",
            12 => $"{evolution.Parameter}级，特殊分支2",
            13 => $"{evolution.Parameter}级，分裂进化1",
            14 => $"{evolution.Parameter}级，分裂进化2",
            15 => $"美丽度达到{evolution.Parameter}",
            16 => $"使用{paramItem}",
            17 => $"白天携带{paramItem}升级",
            18 => $"夜晚携带{paramItem}升级",
            19 => $"达到{evolution.Parameter}级（白天）",
            20 => $"达到{evolution.Parameter}级（夜晚）",
            21 => $"达到{evolution.Parameter}级且为公",
            22 => $"达到{evolution.Parameter}级且为母",
            25 => $"学会{MoveName(evolution.Parameter)}后升级",
            26 => $"队伍中有{SpeciesName(evolution.Parameter)}时升级",
            254 => $"使用{paramItem}进入特殊形态",
            0 when evolution.Parameter != 0 => $"使用{paramItem}进入特殊形态",
            0xFFFF => $"特殊形态，使用{paramItem}",
            0xFFFE => $"特殊形态，使用{EvolutionParameterItemText(evolution.Parameter)}",
            0xFFFD => $"特殊形态，使用{EvolutionParameterItemText(evolution.Parameter)}",
            _ => $"特殊条件，参数{EvolutionParameterText(evolution.Parameter)}"
        };
    }

    private string EvolutionParameterItemText(ushort parameter)
    {
        if (parameter == 0) return "指定道具";
        var table = _db.Table("items");
        return table.ContainsKey(parameter) ? ItemName(parameter) : $"参数{parameter}";
    }

    private string EvolutionParameterText(ushort parameter)
    {
        if (parameter == 0) return "0";
        var table = _db.Table("items");
        return table.ContainsKey(parameter) ? ItemName(parameter) : parameter.ToString();
    }

    private static string GenderRatioText(byte genderRatio) => genderRatio switch
    {
        255 => "无性别",
        254 => "只有母 ♀",
        0 => "只有公 ♂",
        _ => $"母 ♀约 {genderRatio * 100.0 / 254.0:0.#}%"
    };

    private static string GrowthRateName(byte growthRate) => growthRate switch
    {
        0 => "较快",
        1 => "中等",
        2 => "较慢",
        3 => "较慢",
        4 => "较快",
        5 => "波动",
        _ => $"类型{growthRate}"
    };

    private static string EggGroupName(byte eggGroup) => eggGroup switch
    {
        0 => "无",
        1 => "怪兽",
        2 => "水中1",
        3 => "虫",
        4 => "飞行",
        5 => "陆上",
        6 => "妖精",
        7 => "植物",
        8 => "人形",
        9 => "水中3",
        10 => "矿物",
        11 => "不定形",
        12 => "水中2",
        13 => "百变怪",
        14 => "龙",
        15 => "未发现",
        _ => $"蛋组{eggGroup}"
    };

    private static string EvYieldText(ushort evYield)
    {
        string[] labels = ["HP", "攻击", "防御", "速度", "特攻", "特防"];
        var parts = new List<string>();
        for (var i = 0; i < labels.Length; i++)
        {
            var value = (evYield >> (i * 2)) & 0x3;
            if (value > 0) parts.Add($"{labels[i]}+{value}");
        }
        return parts.Count == 0 ? "无" : string.Join("，", parts);
    }

    private static string ComparisonText(int current, int official, Func<int, string> format)
        => $"{format(current)}（{format(official)}）";

    private static IBrush ModifiedValueForeground(int current, int? official)
        => official is null || current == official.Value
            ? ComparisonNeutralBrush
            : current > official.Value ? ComparisonHigherBrush : ComparisonLowerBrush;

    private static string MovePowerText(int power)
        => power == 0 ? "-" : power.ToString();

    private static string MoveAccuracyText(int accuracy)
        => accuracy == 0 ? "-" : accuracy.ToString();

    private string? MachineMoveName(int item)
    {
        var rules = _profile.Runtime.Machines;
        if (rules.Mode == "item-data")
        {
            if (TryReadEmbeddedItemData(item, out var data) &&
                data.Pocket == rules.Pocket &&
                data.ExitsBagOnUse is >= 1 and <= 128)
            {
                var machineNumber = data.ExitsBagOnUse;
                var moveId = MachineMoveId(machineNumber - 1);
                if (moveId <= 0) return null;
                return machineNumber >= 121
                    ? $"HM{machineNumber - 120:00} {MoveName(moveId)}"
                    : $"TM{machineNumber:000} {MoveName(moveId)}";
            }

            return null;
        }

        var tmIndex = item - rules.TmStartItem;
        if (tmIndex >= 0 && tmIndex < rules.TmCount)
        {
            var moveId = MachineMoveId(tmIndex);
            if (moveId > 0) return $"NO.{tmIndex + 1:000} {MoveName(moveId)}";
        }

        var hmIndex = item - rules.HmStartItem;
        if (hmIndex >= 0 && hmIndex < rules.HmCount)
        {
            var moveId = MachineMoveId(rules.HmMoveStartIndex + hmIndex);
            if (moveId > 0) return $"MO{hmIndex + 1:00} {MoveName(moveId)}";
        }

        return null;
    }

    private int MachineMoveId(int machineIndex)
    {
        if (_db.Table("machine_moves").TryGetValue(machineIndex, out var embedded) && int.TryParse(embedded, out var moveId))
            return moveId;

        return machineIndex >= 0 && machineIndex < _profile.Runtime.Machines.FallbackMoveIds.Count
            ? _profile.Runtime.Machines.FallbackMoveIds[machineIndex]
            : 0;
    }

    private int PocketOfItem(int item)
    {
        if (item == 0) return -1;
        if (item < 0 || item > MaxItemId()) return -1;
        if (_profile.Runtime.Machines.Mode == "fixed-ranges" && IsBagMachineItem(item))
            return MachinePocket;

        if (_db.Table("item_pockets").TryGetValue(item, out var pocketText) &&
            int.TryParse(pocketText, out var embeddedPocket) &&
            embeddedPocket is >= 1 and <= 8)
            return _runtime.RemapItemPocket(embeddedPocket);

        try
        {
            var dataPocket = ReadItemData(item).Pocket;
            return dataPocket is >= 1 and <= 8 ? _runtime.RemapItemPocket(dataPocket) : -1;
        }
        catch
        {
            return _runtime.FallbackPocketOfItem(item);
        }
    }

    private bool IsBagMachineItem(int item)
    {
        var rules = _profile.Runtime.Machines;
        return item >= rules.TmStartItem && item < rules.TmStartItem + rules.TmCount ||
               item >= rules.HmStartItem && item < rules.HmStartItem + rules.HmCount;
    }

    private void EnsureBagSelectionFresh(MgbaBridgeClient bridge)
    {
        if (UsesFixedLiveBag) return;
        var liveBase = TryLocateBagBase(bridge);
        if (_bagBase is not null && liveBase is not null && liveBase.Value != _bagBase.Value)
        {
            LoadBagRows(bridge);
            throw new InvalidOperationException("背包位置已变化，已刷新背包列表。请重新选择槽位后再写入。");
        }
        _bagBase = liveBase;
    }

    private bool BagMachineExists(ReadOnlySpan<byte> ewram, uint saveBlockBase, ushort item)
    {
        var definition = BagScanner.DefinitionForPocket(_runtime.ScannedBagDefinitions, MachinePocket)
                         ?? throw new InvalidOperationException("缺少招式机器/秘传机器口袋定义。");
        var start = checked((int)(saveBlockBase + definition.Offset - _profile.Memory.EwramBase));
        var end = start + definition.SlotCount * 4;
        if (start < 0 || end > ewram.Length)
            throw new InvalidOperationException("招式机器/秘传机器口袋地址无效。请先读取或校准背包。");

        for (var offset = start; offset <= end - 4; offset += 4)
        {
            if (ReadU16(ewram, offset) == item)
                return true;
        }

        return false;
    }

    private Dictionary<ushort, ushort> ReadMachinePocketQuantities(
        ReadOnlySpan<byte> ewram,
        int startOffset,
        int slotCount,
        ushort quantityKey)
    {
        var machines = new Dictionary<ushort, ushort>();
        for (var i = 0; i < slotCount; i++)
        {
            var offset = startOffset + i * 4;
            var item = ReadU16(ewram, offset);
            if (!IsBagMachineItem(item)) continue;
            var rawQuantity = ReadU16(ewram, offset + 2);
            machines[item] = DecodeMachineQuantity(rawQuantity, quantityKey, MaxBagWriteQuantityForPocket(MachinePocket));
        }

        return machines;
    }

    private static byte[] EncodeMachinePocket(IReadOnlyDictionary<ushort, ushort> machines, int slotCount, ushort quantityKey, ushort maxQuantity)
    {
        var bytes = new byte[slotCount * 4];
        var index = 0;
        foreach (var (item, quantity) in machines.OrderBy(kv => kv.Key))
        {
            var safeQuantity = quantity;
            if (safeQuantity == 0) safeQuantity = 1;
            if (safeQuantity > maxQuantity) safeQuantity = maxQuantity;
            var slot = BagScanner.EncodeSlot(item, safeQuantity, quantityKey, quantityXor: true);
            slot.CopyTo(bytes.AsSpan(index * 4, 4));
            index++;
        }

        return bytes;
    }

    private static ushort DecodeMachineQuantity(ushort rawQuantity, ushort quantityKey, ushort maxQuantity)
    {
        if (quantityKey != 0)
        {
            var decoded = (ushort)(rawQuantity ^ quantityKey);
            if (decoded > 0 && decoded <= maxQuantity)
                return decoded;
        }

        return rawQuantity > 0 && rawQuantity <= maxQuantity ? rawQuantity : (ushort)1;
    }

    private int BagBatchCapacity(int pocket)
        => _runtime.BagBatchCapacity(pocket, _dataSourceMode == DataSourceMode.Live);

    private byte[] EncodeBatchBagSlot(int pocket, ushort item, ushort quantityKey)
        => UsesFixedLiveBag
            ? BagScanner.EncodeSlot(item, BatchQuantityForPocket(pocket), quantityKey, quantityXor: true)
            : IsKeyItemPocket(pocket)
            ? BagScanner.EncodeSlot(item, 0, quantityKey, quantityXor: false)
            : BagScanner.EncodeSlot(item, BatchQuantityForPocket(pocket), quantityKey, quantityXor: true);

    private BoxPokemon ReadSelectedBoxMon(MgbaBridgeClient bridge, BoxSlotRow row)
    {
        if (_boxBase is not null)
            EnsureBoxStorageBaseFresh(bridge);

        var mon = new BoxPokemon(bridge.Read(row.Address, ActiveBoxRecordSize), ActiveBoxLayout);
        if (mon.IsEmpty)
        {
            RefreshBoxAndSelectMovedMon(bridge, row);
            throw new InvalidOperationException("该箱子槽已经变空，宝可梦可能已被取出或移动。已刷新箱子列表，请重新选择后再写入。");
        }

        if (SamePokemonIdentity(row.Mon, mon))
            return mon;

        RefreshBoxAndSelectMovedMon(bridge, row);
        throw new InvalidOperationException($"箱子槽里的宝可梦已经变化。{MovedBoxHint(row)}");
    }

    private void EnsureBoxStorageBaseFresh(MgbaBridgeClient bridge)
    {
        if (_profile.Memory.PcBoxStoragePointerAddress != 0)
        {
            var recordsBase = ResolvePointerBoxRecordsBase(bridge);
            if (_boxBase == recordsBase) return;
            LoadBoxRows(bridge);
            throw new InvalidOperationException("箱子位置已变化，已刷新箱子列表。请重新选择槽位后再写入。");
        }
        if (_profile.Memory.PcBoxRegions.Count > 0)
            return;

        var ewram = PartyScanner.ReadEwram(bridge, _profile);
        var storage = BoxScanner.LocatePcStorage(
            ewram,
            _profile.Memory.EwramBase,
            maxSpecies: _profile.Limits.MaxSpecies,
            maxBoxes: _profile.Memory.PcBoxCount,
            slotsPerBox: _profile.Memory.PcBoxSlots,
            minScore: 12,
            layout: ActiveBoxLayout);
        if (storage is not null)
        {
            if (_boxBase is { } boxBase && storage.StartAddress == boxBase) return;
            LoadBoxRows(bridge);
            throw new InvalidOperationException("箱子位置已变化，已刷新箱子列表。请重新选择槽位后再写入。");
        }

        var run = BoxScanner.LocateBestRun(ewram, _profile.Memory.EwramBase, _profile.Limits.MaxSpecies, ActiveBoxLayout) ?? throw new InvalidOperationException("没有定位到箱子宝可梦。请重新读取箱子后再写入。");
        if (_boxBase is { } fallbackBase && run.StartAddress == fallbackBase) return;
        LoadBoxRows(bridge);
        throw new InvalidOperationException("箱子位置已变化，已刷新箱子列表。请重新选择槽位后再写入。");
    }

    private void RefreshBoxAndSelectMovedMon(MgbaBridgeClient bridge, BoxSlotRow oldRow)
    {
        try
        {
            LoadBoxRows(bridge);
            if (FindBoxRowByIdentity(oldRow) is { } moved)
                BoxList.SelectedItem = moved;
        }
        catch
        {
            // Keep the original write failure visible; refresh errors are secondary.
        }
    }

    private BoxSlotRow? FindBoxRowByIdentity(BoxSlotRow oldRow)
        => _boxRows.FirstOrDefault(row => SamePokemonIdentity(oldRow.Mon, row.Mon));

    private string MovedBoxHint(BoxSlotRow oldRow)
    {
        if (FindBoxRowByIdentity(oldRow) is { } moved)
            return $"已在箱子列表中定位到新位置：{moved.Title}。请确认后重新写入。";
        return "未在当前箱子候选中找到同一只宝可梦，它可能已经进入队伍、被移动到未读取箱子，或当前箱子定位已变化。请重新读取队伍/箱子。";
    }

    private static bool SamePokemonIdentity(BoxPokemon left, BoxPokemon right)
        => !left.IsEmpty && !right.IsEmpty && left.Pid == right.Pid && left.OtId == right.OtId;

    private BagPocketDefinition? ResolveBagDefinition(MgbaBridgeClient bridge, uint address)
    {
        if (BagList.SelectedItem is BagSlotRow row && row.Address == address)
            return new BagPocketDefinition(row.Pocket, PocketNameZh(row.Pocket), 0, 0, row.QuantityXor, row.QuantityKey);

        try
        {
            _bagBase ??= BagScanner.LocateSaveBlockBase(bridge, _profile, _runtime.ScannedBagDefinitions);
            return BagScanner.DefinitionForAddress(_runtime.ScannedBagDefinitions, _bagBase.Value, address);
        }
        catch
        {
            return null;
        }
    }

    private uint? TryLocateBagBase(MgbaBridgeClient bridge)
    {
        try
        {
            return BagScanner.LocateSaveBlockBase(bridge, _profile, _runtime.ScannedBagDefinitions);
        }
        catch
        {
            return null;
        }
    }

    private string SpeciesName(int species) => SpeciesDisplayName(species, "未知宝可梦");

    private string MoveName(int move) => move == 0 ? "无" : KnownName("moves", move, "未知招式");

    private string AbilityName(int ability) => KnownName("abilities", ability, "未知特性");

    private string KnownName(string table, int id, string fallback)
    {
        var name = _db.NameOf(table, id);
        return name.StartsWith('#') ? fallback : name;
    }

    private void WriteMon(MgbaBridgeClient bridge, uint addr, PartyPokemon mon)
        => WriteLivePartyRange(bridge, addr, mon.Raw);

    private void WriteLivePartyRange(MgbaBridgeClient bridge, uint address, ReadOnlySpan<byte> data)
    {
        _runtime.EnsureCanWrite(GameDataSurface.Party, live: true);
        var partyBase = _partyBase ?? throw new InvalidOperationException("尚未定位当前队伍，已拒绝写入。");
        var byteLength = data.Length;
        var partyByteLength = Gen3Constants.PartySlots * Gen3Constants.PartyMonSize;
        if (byteLength <= 0 || byteLength % Gen3Constants.PartyMonSize != 0 ||
            address < partyBase || (address - partyBase) % Gen3Constants.PartyMonSize != 0 ||
            (ulong)address + (uint)byteLength > (ulong)partyBase + (uint)partyByteLength ||
            !IsEwramRange(address, byteLength))
            throw new InvalidOperationException("队伍写入范围不属于当前扫描确认的 6 个队伍槽，已拒绝写入。");
        bridge.WriteRangeVerified(address, data);
    }

    private void WriteLiveBoxRange(MgbaBridgeClient bridge, uint address, ReadOnlySpan<byte> data)
    {
        _runtime.EnsureCanWrite(GameDataSurface.Boxes, live: true);
        if (data.Length != ActiveBoxRecordSize || !IsEwramRange(address, data.Length))
            throw new InvalidOperationException("箱子写入必须是一个位于 EWRAM 的完整箱子记录，已拒绝写入。");

        var writableSlots = WritableBoxSlotCount(live: true);
        if (_profile.Memory.PcBoxRegions.Count > 0)
        {
            var globalSlot = Enumerable.Range(1, writableSlots)
                .FirstOrDefault(slot => ProfileBoxAddress(slot) == address);
            if (globalSlot == 0)
                throw new InvalidOperationException("箱子写入地址不属于当前 Profile 已验证的可写槽位，已拒绝写入。");
        }
        else if (_profile.Memory.PcBoxStoragePointerAddress != 0)
        {
            var recordsBase = ResolvePointerBoxRecordsBase(bridge);
            var offset = address >= recordsBase ? address - recordsBase : uint.MaxValue;
            if (offset == uint.MaxValue || offset % (uint)ActiveBoxRecordSize != 0 ||
                offset / (uint)ActiveBoxRecordSize >= writableSlots)
                throw new InvalidOperationException("箱子写入地址不属于当前 Profile 指针指向的可写槽位，已拒绝写入。");
        }
        else
        {
            var recordsBase = _boxBase ?? throw new InvalidOperationException("尚未定位当前箱子，已拒绝写入。");
            var offset = address >= recordsBase ? address - recordsBase : uint.MaxValue;
            if (offset == uint.MaxValue || offset % (uint)ActiveBoxRecordSize != 0 ||
                offset / (uint)ActiveBoxRecordSize >= writableSlots)
                throw new InvalidOperationException("箱子写入地址不属于当前扫描确认的可写槽位，已拒绝写入。");
        }
        bridge.WriteRangeVerified(address, data);
    }

    private void WriteLiveBagRange(MgbaBridgeClient bridge, uint address, ReadOnlySpan<byte> data)
    {
        _runtime.EnsureCanWrite(GameDataSurface.Bag, live: true);
        bridge.WriteRangeVerified(address, data);
    }

    private void RefreshPartyRowsIfPossible(MgbaBridgeClient bridge)
    {
        try
        {
            var selectedSlot = SelectedRow()?.Slot ?? 1;
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            LoadPartyRows(bridge, baseAddr, selectedSlot);
        }
        catch (Exception ex)
        {
            Log("刷新队伍失败：" + ex.Message);
        }
    }



    private (uint Address, PartyPokemon Mon) ReadSelectedLiveMon(MgbaBridgeClient bridge, uint baseAddr, PartySlotRow row)
    {
        var addr = LiveSlotAddress(bridge, baseAddr, row.Slot);
        var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize), ActivePokemonLayout);
        if (row.Mon.IsEmpty || row.Mon.Pid == mon.Pid && row.Mon.OtId == mon.OtId)
            return (addr, mon);

        throw new InvalidOperationException($"队伍槽位 {row.Slot} 的宝可梦已经变化。请先重新读取队伍，再写入。");
    }

    private uint LiveSlotAddress(MgbaBridgeClient bridge, uint baseAddr, int slot)
    {
        var count = ReadLivePartyCount(bridge, baseAddr);
        if (count is not null && slot > count.Value)
            throw new InvalidOperationException($"当前队伍只有 {count.Value} 只，槽位 {slot} 已不在队伍中。请重新读取队伍。");
        return SlotAddress(baseAddr, slot);
    }

    private static uint SlotAddress(uint baseAddr, int slot) => baseAddr + (uint)((slot - 1) * Gen3Constants.PartyMonSize);

    private static ushort? ParseUShortOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = ParseInt(text.Trim());
        if (value is < 0 or > ushort.MaxValue) throw new InvalidOperationException($"数值 {text} 不在 0..65535 范围内。");
        return (ushort)value;
    }

    private static ushort ParseUShortRequired(string? text, string label)
        => ParseUShortOrNull(text) ?? throw new InvalidOperationException($"缺少 {label}。");

    private static byte? ParseByteOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = ParseInt(text.Trim());
        if (value is < 0 or > byte.MaxValue) throw new InvalidOperationException($"数值 {text} 不在 0..255 范围内。");
        return (byte)value;
    }

    private static uint? ParseUIntOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return ParseUInt(text.Trim());
    }

    private static int ParseIntRequired(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"缺少 {label}。");
        return ParseInt(text.Trim());
    }

    private static uint ParseUIntRequired(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException($"缺少 {label}。");
        return ParseUInt(text.Trim());
    }

    private static int ParseInt(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = new string(text[2..].TakeWhile(Uri.IsHexDigit).ToArray());
            return Convert.ToInt32(hex, 16);
        }

        var sign = text.StartsWith('-') ? 1 : 0;
        var digits = new string(text.Skip(sign).TakeWhile(char.IsDigit).ToArray());
        if (digits.Length > 0) return int.Parse(text[..sign] + digits);
        throw new FormatException($"无法解析数值：{text}");
    }

    private static uint ParseUInt(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = new string(text[2..].TakeWhile(Uri.IsHexDigit).ToArray());
            return Convert.ToUInt32(hex, 16);
        }

        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length > 0) return Convert.ToUInt32(digits);
        throw new FormatException($"无法解析数值：{text}");
    }

    private string PocketNameZh(int pocket)
        => _runtime.PocketName(pocket, _dataSourceMode == DataSourceMode.Live);

    private bool IsEwramRange(uint address, int length)
    {
        if (length <= 0) return false;
        var ewramEnd = _profile.Memory.EwramBase + (uint)_profile.Memory.EwramSize;
        return address >= _profile.Memory.EwramBase && address <= ewramEnd - (uint)length;
    }

    private bool LooksLikePlayerSaveBlock2Header(ReadOnlySpan<byte> header)
    {
        if (header.Length < _profile.Memory.SaveBlock2HeaderLength) return false;
        for (var i = 0; i < 8; i++)
        {
            if (header[i] is not 0x00 and not 0xFF)
                return true;
        }

        return false;
    }

    private static uint ReadU32Le(ReadOnlySpan<byte> data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static byte[] U32Le(uint value) =>
    [
        (byte)value,
        (byte)(value >> 8),
        (byte)(value >> 16),
        (byte)(value >> 24)
    ];

    private static string MoveCategoryNameZh(int category) => category switch
    {
        0 => "物理",
        1 => "特殊",
        2 => "变化",
        _ => MoveDataReader.CategoryName(category)
    };

    private static string MoveTypeNameZh(int type) => type switch
    {
        0 => "一般",
        1 => "格斗",
        2 => "飞行",
        3 => "毒",
        4 => "地面",
        5 => "岩石",
        6 => "虫",
        7 => "幽灵",
        8 => "钢",
        9 => "？？？",
        10 => "火",
        11 => "水",
        12 => "草",
        13 => "电",
        14 => "超能力",
        15 => "冰",
        16 => "龙",
        17 => "恶",
        18 or 23 => "妖精",
        _ => MoveDataReader.TypeName(type)
    };

    private void SetBusy(string message)
    {
        ConnectionStatusText.Text = message;
        BusyText.Text = message;
        BusyBorder.IsVisible = true;
        Log(message);
    }

    private void SetReady(string message)
    {
        ConnectionStatusText.Text = message;
        BusyBorder.IsVisible = false;
        Log(message);
    }

    private void SetWriteNotice(string message, bool success = true)
    {
        ShowToast(message, success);
        Log(message);
    }

    private void ShowToast(string message, bool success = true)
    {
        ToastText.Text = message;
        ToastBorder.Background = new SolidColorBrush(success ? Color.FromRgb(16, 46, 74) : Color.FromRgb(150, 43, 43));
        ToastBorder.IsVisible = true;
    }

    private void OnToastCloseClicked(object? sender, RoutedEventArgs e)
    {
        ToastBorder.IsVisible = false;
    }

    private void Log(string message)
    {
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogBox.CaretIndex = LogBox.Text.Length;
    }
}
