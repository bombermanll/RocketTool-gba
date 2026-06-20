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
    ushort QuantityKey,
    bool QuantityXor,
    string Title,
    string Detail);
public sealed record BagCalibrationRequest(int Pocket, ushort ItemId, ushort Quantity);
public sealed record BoxSlotRow(int Slot, uint Address, BoxPokemon Mon, BoxMonInfo Info, string Title, string Detail, Bitmap? Sprite);
public sealed record BoxGridCell(int SlotInBox, BoxSlotRow? Row, Bitmap? Sprite, string Title, bool HasPokemon);
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
    public bool IsTextVisible => !HasTypeBadges;
}
public sealed record DexStatRow(string Name, string Base, string Level50, string Level100, string MaxLevel);
public sealed record DexLevelMoveRow(string Level, string Move, string Type, string Category, string Power, string Accuracy, string Pp);
public sealed record MapLocationChoice(string Name, int X, int Y)
{
    public override string ToString() => Name;
}
public sealed record ChoiceRow(int Id, string Name, string? Display = null)
{
    public override string ToString() => Display ?? Name;
}

public enum DataSourceMode
{
    Live,
    SaveReadOnly
}

public partial class MainWindow : Window
{
    private const string AppNameBase = "火箭队修改工具";
    private const string AppTitleBase = "火箭队修改工具 - mGBA 实时编辑";

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

    private static readonly int[] Gen3TmMoveIds =
    [
        264, 337, 352, 347, 46, 92, 258, 339, 331, 237,
        241, 269, 58, 59, 63, 113, 182, 240, 202, 219,
        218, 76, 231, 85, 87, 89, 216, 91, 94, 247,
        280, 104, 115, 351, 53, 188, 201, 126, 317, 332,
        259, 263, 290, 156, 213, 168, 211, 285, 289, 315
    ];
    private const int MachineTmStartItem = 592;
    private const int MachineTmCount = 246;
    private const int MachineHmStartItem = 838;
    private const int MachineHmCount = 8;
    private const int MaxPokemonLevel = 150;
    private const int DexImportLevel = 5;
    private const ushort MaxBagWriteQuantity = 255;

    private readonly ObservableCollection<PartySlotRow> _partyRows = [];
    private readonly ObservableCollection<BagSlotRow> _bagRows = [];
    private readonly ObservableCollection<BoxSlotRow> _boxRows = [];
    private readonly ObservableCollection<BoxGridGroup> _boxGridGroups = [];
    private readonly ObservableCollection<DexSpeciesRow> _dexRows = [];
    private readonly ObservableCollection<DexInfoRow> _dexInfoRows = [];
    private readonly ObservableCollection<DexStatRow> _dexStatRows = [];
    private readonly ObservableCollection<DexLevelMoveRow> _dexLevelMoveRows = [];
    private DexSpeciesRow[] _allDexRows = [];
    private int _dexMaxLevel = MaxPokemonLevel;
    private readonly List<BagSlotRow> _allBagRows = [];
    private readonly ChoiceRow[] _speciesChoices;
    private readonly ChoiceRow[] _itemChoices;
    private readonly ChoiceRow[] _moveChoices;
    private readonly ChoiceRow[] _natureChoices;
    private readonly ChoiceRow[] _statusChoices;
    private readonly ChoiceRow[] _ppBonusChoices;
    private readonly ChoiceRow[] _bagPocketChoices;
    private readonly MapDatabase _mapDatabase;
    private readonly ChoiceRow[] _mapGroupChoices;
    private IReadOnlyList<ChoiceRow> _bagItemChoices = [];
    private readonly ModifierDatabase _db;
    private uint? _partyBase;
    private uint? _boxBase;
    private uint? _bagBase;
    private DataSourceMode _dataSourceMode = DataSourceMode.Live;
    private string? _loadedSavePath;
    private bool _boxGridShowAllBoxes;
    private int? _abilitySpecies;
    private int? _boxAbilitySpecies;
    private bool _suppressEditorEvents;
    private bool _suppressCheatEvents;
    private DispatcherTimer? _toastTimer;
    private readonly Dictionary<SearchableChoiceBox, IReadOnlyList<ChoiceRow>> _searchableChoices = [];
    private readonly Dictionary<int, IReadOnlyList<SpeciesEvolution>> _evolutionCache = [];
    private readonly Dictionary<int, IReadOnlyList<SpeciesLevelMove>> _levelMoveCache = [];
    private readonly Dictionary<int, Bitmap?> _spriteCache = [];


    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        ApplyWindowTitle();
        _db = new ModifierDatabase(Path.Combine(RootDir(), "modifier_db"), typeof(MainWindow).Assembly);
        _mapDatabase = new MapDatabase(_db);
        _mapGroupChoices = _mapDatabase.Groups()
            .Select(g => new ChoiceRow(g.Key, MapGroupChoiceText(g.Key, g)))
            .ToArray();
        _speciesChoices = SpeciesChoiceRows();
        _itemChoices = [new ChoiceRow(0, "无"), .. ChoiceRows("items")];
        _bagItemChoices = _itemChoices;
        _moveChoices = [new ChoiceRow(0, "无"), .. ChoiceRows("moves")];
        _natureChoices = NatureDisplays.Select((name, id) => new ChoiceRow(id, name, name)).ToArray();
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
        _bagPocketChoices =
        [
            new(1, "普通道具"),
            new(2, "回复药品"),
            new(3, "精灵球"),
            new(4, "战斗道具"),
            new(5, "树果"),
            new(6, "宝物"),
            new(7, "招式机器/秘传机器"),
            new(8, "重要物品"),
            new(-1, "未知/候选"),
            new(0, "全部口袋")
        ];
        PartyList.ItemsSource = _partyRows;
        BagList.ItemsSource = _bagRows;
        BoxList.ItemsSource = _boxRows;
        BoxGridGroupsView.ItemsSource = _boxGridGroups;
        DexSpeciesList.ItemsSource = _dexRows;
        DexInfoRowsView.ItemsSource = _dexInfoRows;
        DexStatRowsView.ItemsSource = _dexStatRows;
        DexLevelMoveRowsView.ItemsSource = _dexLevelMoveRows;
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
        StatusBox.ItemsSource = _statusChoices;
        foreach (var box in PpBonusBoxes())
        {
            box.ItemsSource = _ppBonusChoices;
            box.SelectedIndex = 0;
        }
        FitComboToContent(StatusBox, _statusChoices);
        BagPocketTabs.ItemsSource = _bagPocketChoices;
        BagPocketTabs.SelectedIndex = 0;
        TeleportGroupBox.ItemsSource = _mapGroupChoices;
        TeleportGroupBox.SelectedIndex = _mapGroupChoices.Length > 0 ? 0 : -1;
        RefreshTeleportMaps();
        ConfigureNumericInputLimits();
        HookNameRefresh();
        InitializeDexRows();
        SetDataSourceMode(DataSourceMode.Live);
        Log("界面已就绪。请先在 mGBA 加载 bridge 脚本，然后点击“连接 mGBA”。");
    }

    private void SetDataSourceMode(DataSourceMode mode)
    {
        _dataSourceMode = mode;
        var live = mode == DataSourceMode.Live;

        ReadPartyButton.IsVisible = live;
        BagTab.IsVisible = true;
        BoxReadButton.IsVisible = live;
        ExperimentTab.IsVisible = live;

        PartyDeleteButton.IsVisible = live;
        PartyApplyButton.IsVisible = live;
        BoxDeleteButton.IsVisible = live;
        BoxApplyButton.IsVisible = live;
        BagReadButton.IsVisible = live;
        BagSnapshotButton.IsVisible = live;
        BagAddButton.IsVisible = live;
        BagApplyButton.IsVisible = live;
        DexImportPartyButton.IsVisible = live;
        DexImportBoxButton.IsVisible = live;
        DexImportPartyButton.IsEnabled = live;
        DexImportBoxButton.IsEnabled = live;
        BagModeHintText.Text = live
            ? "请先对比下方道具列表是否和游戏中一致；如不一致，请先校准背包。"
            : "存档模式保留背包编辑页；当前只做只读结构探测，写回存档前需要先确认本改版背包结构。";

        if (!live && MainTabs.SelectedItem == ExperimentTab)
            MainTabs.SelectedItem = PartyTab;
    }

    private void ApplyWindowTitle()
    {
        var version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var suffix = string.IsNullOrWhiteSpace(version) ? string.Empty : $" v{version.Split('+')[0]}";
        Title = AppTitleBase + suffix;
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
                     BoxFriendshipBox,
                     BoxPp1Box, BoxPp2Box, BoxPp3Box, BoxPp4Box,
                     BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpeBox, BoxEvSpaBox, BoxEvSpdBox,
                     BagQuantityBox,
                     TeleportXBox, TeleportYBox
                 })
            box.MaxLength = 3; // byte-sized fields, and bag quantity is intentionally capped at 255.

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
        await RunUiTask("连接 mGBA", () =>
        {
            using var bridge = ConnectBridge();
            var gameCode = bridge.GameCode();
            ConnectionStatusText.Text = $"mGBA连接成功：{gameCode}";
            LastWriteText.Text = "当前来源：mGBA 实时内存；可以读取队伍、背包和箱子。";
            Log($"已连接 mGBA：游戏={gameCode}。队伍数据尚未读取。连接会在本次操作结束后自动关闭，这是正常现象。");
        });
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
        var rows = new List<PartySlotRow>();
        var partyCount = ReadLivePartyCount(bridge, baseAddr) ?? Gen3Constants.PartySlots;
        for (var slot = 1; slot <= partyCount; slot++)
        {
            var addr = SlotAddress(baseAddr, slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
            rows.Add(ToRow(slot, addr, mon));
        }
        _partyRows.Clear();
        foreach (var row in rows) _partyRows.Add(row);
        PartyList.SelectedIndex = _partyRows.Count == 0 ? -1 : Math.Clamp(selectSlot - 1, 0, _partyRows.Count - 1);
    }

    private void LoadSaveFile(string path)
    {
        var save = Gen3SaveReader.Read(path);
        SetDataSourceMode(DataSourceMode.SaveReadOnly);
        _loadedSavePath = path;
        _partyBase = null;
        _boxBase = null;
        _bagBase = null;
        BaseBox.Text = string.Empty;

        _partyRows.Clear();
        for (var i = 0; i < save.Party.Count; i++)
            _partyRows.Add(ToRow(i + 1, (uint)i, save.Party[i]));
        PartyList.SelectedIndex = _partyRows.Count > 0 ? 0 : -1;
        if (_partyRows.Count == 0)
        {
            SelectedTitleText.Text = "存档中没有识别到队伍";
            ClearEditor();
        }

        _bagRows.Clear();
        _allBagRows.Clear();
        foreach (var entry in save.Bag)
        {
            var definition = new BagPocketDefinition(
                entry.Pocket,
                PocketNameZh(entry.Pocket is >= 1 and <= 8 ? entry.Pocket : PocketOfItem(entry.ItemId)),
                0,
                0,
                entry.QuantityXor,
                entry.QuantityKey);
            _allBagRows.Add(ToBagRow(
                (uint)entry.SaveOffset,
                entry.ItemId,
                entry.Quantity,
                entry.Note,
                entry.Pocket is >= 1 and <= 8 ? entry.Pocket : null,
                entry.SlotInPocket,
                definition));
        }
        ApplyBagPocketFilter();
        BagList.SelectedIndex = -1;
        if (_bagRows.Count > 0) BagList.SelectedIndex = 0;

        _boxRows.Clear();
        foreach (var entry in save.Boxes)
        {
            var info = entry.Mon.GetInfo();
            var title = $"箱{entry.BoxNumber:00}-{entry.SlotInBox:00} {(entry.Mon.IsShiny ? "★" : "")}{SpeciesName(info.Species)}";
            if (info.Item != 0) title += $" / {ItemName(info.Item)}";
            if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
            _boxRows.Add(new BoxSlotRow(entry.GlobalSlot, (uint)entry.SaveOffset, entry.Mon, info, title, "存档只读", LoadSpriteBitmap(info.Species)));
        }

        _boxGridShowAllBoxes = true;
        RefreshBoxGridGroups();
        BoxList.SelectedIndex = _boxRows.Count > 0 ? 0 : -1;
        if (_boxRows.Count == 0)
        {
            BoxNameText.Text = "存档中没有识别到箱子宝可梦。";
            SetPokemonSprite(BoxDetailSpriteImage, 0);
        }

        ConnectionStatusText.Text = $"mGBA已断开；已读取存档：{save.FileName}";
        LastWriteText.Text = "当前来源：存档只读预览；实时读取、实验功能和写回文件暂不开放。";
        Log($"已读取存档：{save.FileName}，大小 {save.FileSize} 字节，使用副本 {save.SaveSlot}，save index {save.SaveIndex}，有效 section {save.ValidSectionCount}/14。");
        Log($"存档内容：队伍 {save.Party.Count} 只，背包可显示 {save.Bag.Count} 格，箱子非空 {save.Boxes.Count} 格。支持 .sav/.srm 原始电池存档。");
        foreach (var warning in save.Warnings)
            Log("存档提示：" + warning);
    }

    private static int? ReadLivePartyCount(MgbaBridgeClient bridge, uint baseAddr)
    {
        var countAddress = (long)baseAddr + Gen3Constants.PartyCountOffsetFromPartyBase;
        if (countAddress < PartyScanner.EwramBase) return null;
        var count = bridge.Read((uint)countAddress, 1)[0];
        return count is >= 0 and <= Gen3Constants.PartySlots ? count : null;
    }

    private static void WritePartyCount(MgbaBridgeClient bridge, uint baseAddr, int count)
    {
        if (count is < 0 or > Gen3Constants.PartySlots)
            throw new InvalidOperationException($"队伍数量必须在 0..{Gen3Constants.PartySlots}。");
        var countAddress = (long)baseAddr + Gen3Constants.PartyCountOffsetFromPartyBase;
        if (countAddress < PartyScanner.EwramBase)
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
            var species = ParseSpeciesOrNull(SpeciesBox);
            SyncNicknameForSpeciesChange(mon, species);
            var nature = SelectedChoiceId(NatureBox);
            if (nature is not null) mon.SetNature(nature.Value);
            mon.SetShiny(ShinyBox.IsChecked == true);
            var abilitySlot = SelectedAbilitySlot();
            if (abilitySlot is not null) mon.SetAbilitySlot(abilitySlot.Value);
            mon.SetGrowth(species, ParseItemOrNull(ItemBox), ParseUIntOrNull(ExpBox.Text), ParseByteOrNull(FriendshipBox.Text), null);
            var level = ParseByteOrNull(LevelBox.Text);
            var maxHp = ParseUShortOrNull(MaxHpBox.Text);
            mon.SetUnencrypted(null, null, SelectedStatus(), level);
            var shouldRecalculateStats =
                species is not null && species.Value != before.Species ||
                nature is not null && (nature.Value != before.Nature || before.GameNatureCode != NatureCodeUsePid) ||
                level is not null && level.Value != before.Level;
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
        await RunUiTask("写入当前精灵", () =>
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
            var species = ParseSpeciesOrNull(SpeciesBox);
            SyncNicknameForSpeciesChange(mon, species);

            if (SelectedChoiceId(NatureBox) is { } nature) mon.SetNature(nature);
            mon.SetShiny(ShinyBox.IsChecked == true);
            if (SelectedAbilitySlot() is { } abilitySlot) mon.SetAbilitySlot(abilitySlot);
            mon.SetGrowth(
                species,
                ParseItemOrNull(ItemBox),
                ParseUIntOrNull(ExpBox.Text),
                ParseByteOrNull(FriendshipBox.Text),
                BuildPpBonuses(PpUp1Box, PpUp2Box, PpUp3Box, PpUp4Box));
            mon.SetUnencrypted(null, ParseUShortOrNull(MaxHpBox.Text), SelectedStatus(), ParseByteOrNull(LevelBox.Text));
            mon.SetMoves(
                [ParseMoveOrNull(Move1Box), ParseMoveOrNull(Move2Box), ParseMoveOrNull(Move3Box), ParseMoveOrNull(Move4Box)],
                [ParseByteOrNull(Pp1Box.Text), ParseByteOrNull(Pp2Box.Text), ParseByteOrNull(Pp3Box.Text), ParseByteOrNull(Pp4Box.Text)]);
            mon.SetIvs(ivs);
            mon.SetEvs(evs);
            RecalculateLiveStats(mon);
            WriteMon(bridge, addr, mon);
            SetWriteNotice(
                evTotal > 510
                    ? $"队伍槽位 {row.Slot} 当前精灵写入成功：基础信息/招式/个体/努力已更新。警告：努力值总和已超过限制，可能发生未知错误或坏档。"
                    : $"队伍槽位 {row.Slot} 当前精灵写入成功：基础信息/招式/个体/努力已更新。",
                success: evTotal <= 510);
        });
        await RefreshPartyAndBoxAfterWriteAsync(row.Slot);
    }

    private async void OnPartyDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        if (!await ConfirmPartyDeleteAsync(row)) return;

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

            bridge.WriteRangeVerified(addr, compacted);
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
            var ewram = PartyScanner.ReadEwram(bridge);
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
                    if (quantity is 0 or > MaxBagWriteQuantity)
                        throw new InvalidOperationException($"{PocketNameZh(row.Pocket)}数量必须在 1..{MaxBagWriteQuantity}。");
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
        if (saveBlockBase is >= PartyScanner.EwramBase and < PartyScanner.EwramBase + PartyScanner.EwramSize)
        {
            startOffset = Math.Max(0, (int)(saveBlockBase.Value - PartyScanner.EwramBase));
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
                if (slotQuantity is 0 or > BagScanner.DefaultMaxQuantity) break;
                score += slotItem == request.ItemId && slotQuantity == request.Quantity ? 60 : 30;
                slots.Add(new BagSlot(
                    PartyScanner.EwramBase + (uint)slotOff,
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
        BagAddressBox.Text = $"0x{row.Address:X8}";
        SetBagItemChoicesForPocket(row.Pocket, row.ItemId);
        BagQuantityBox.Text = row.Quantity.ToString();
        UpdateBagNameText();
    }

    private async void OnBagReadClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("读取背包槽", () =>
        {
            using var bridge = ConnectBridge();
            var addr = ParseUIntRequired(BagAddressBox.Text, "背包槽地址");
            var definition = ResolveBagDefinition(bridge, addr);
            var slot = definition is null
                ? BagScanner.ReadSlot(bridge, addr, MaxItemId(), BagScanner.DefaultMaxQuantity)
                : BagScanner.ReadSlot(bridge, addr, definition);
            SetBagItemChoicesForPocket(definition?.Pocket ?? PocketOfItem(slot.ItemId), slot.ItemId);
            BagQuantityBox.Text = slot.Quantity.ToString();
            UpdateBagNameText();
            Log($"已读取背包槽 0x{addr:X8}: {slot.ItemId}({ItemName(slot.ItemId)}) x{slot.Quantity}。");
        });
    }

    private async void OnBagApplyClicked(object? sender, RoutedEventArgs e)
    {
        if (BagList.SelectedItem is not BagSlotRow row)
        {
            ShowToast("请先选择一个背包槽。", success: false);
            return;
        }

        await RunUiTask("写入背包槽", () =>
        {
            using var bridge = ConnectBridge();
            EnsureBagSelectionFresh(bridge);
            var addr = row.Address;
            var item = ParseBagItemRequired(row.Pocket, allowEmpty: true);
            var qty = ParseBagQuantityRequired(item, allowZero: item == 0);
            var liveKey = BagScanner.InferQuantityKey(PartyScanner.ReadEwram(bridge));
            var quantityKey = liveKey != 0 ? liveKey : row.QuantityKey;
            var quantityXor = row.QuantityXor || quantityKey != 0;
            var definition = new BagPocketDefinition(row.Pocket, PocketNameZh(row.Pocket), 0, 0, quantityXor, quantityKey);
            var before = BagScanner.ReadSlot(bridge, addr, definition);

            bridge.WriteRangeVerified(addr, EncodeBagOverwriteSlot(item, qty, quantityKey, quantityXor));

            var slot = BagScanner.ReadSlot(bridge, addr, definition);
            SetWriteNotice($"背包覆盖成功：{ItemName(before.ItemId)} x{before.Quantity} -> {ItemName(slot.ItemId)} x{slot.Quantity}。");
            LoadBagRows(bridge);
            BagList.SelectedItem = _bagRows.FirstOrDefault(r => r.Address == addr) ?? BagList.SelectedItem;
        });
    }

    private async void OnBagAddClicked(object? sender, RoutedEventArgs e)
    {
        await RunUiTask("添加背包道具", () =>
        {
            using var bridge = ConnectBridge();
            _bagBase = TryLocateBagBase(bridge);
            var ewram = PartyScanner.ReadEwram(bridge);
            var selectedPocket = SelectedChoiceId(BagPocketTabs) ?? 0;
            if (selectedPocket <= 0)
                selectedPocket = CurrentBagEditPocket();
            if (selectedPocket <= 0)
                throw new InvalidOperationException("请先切换到具体背包分类，或先选中一个背包槽。");
            var item = ParseBagItemRequired(selectedPocket, allowEmpty: false);
            var qty = ParseBagQuantityRequired(item, allowZero: false);
            var target = BagScanner.FindAddTarget(ewram, _bagBase, selectedPocket, item, qty, PocketOfItem, MaxBagWriteQuantity);
            bridge.WriteRangeVerified(target.Address, BagScanner.EncodeSlot(target.ItemId, target.AfterQuantity, target.QuantityKey, quantityXor: true));
            SetWriteNotice($"背包添加成功：{ItemName(item)} {target.BeforeQuantity} -> {target.AfterQuantity}（{target.Note}）。");
            LoadBagRows(bridge);
            BagList.SelectedItem = _bagRows.FirstOrDefault(r => r.Address == target.Address) ?? BagList.SelectedItem;
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
        BoxAddressText.Text = $"0x{row.Address:X8}";
        SetChoice(BoxSpeciesBox, _speciesChoices, info.Species);
        SetChoice(BoxItemBox, _itemChoices, info.Item);
        SetChoice(BoxNatureBox, _natureChoices, info.Nature);
        BoxShinyBox.IsChecked = row.Mon.IsShiny;
        BoxExpBox.Text = info.Exp.ToString();
        BoxFriendshipBox.Text = info.Friendship.ToString();
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
        if (sender is Button { Tag: BoxSlotRow row })
            BoxList.SelectedItem = row;
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

        await RunUiTask("写入箱子", () =>
        {
            using var bridge = ConnectBridge();
            var mon = ReadSelectedBoxMon(bridge, row);
            var species = ParseSpeciesRequired(BoxSpeciesBox, "箱子宝可梦");
            SyncNicknameForSpeciesChange(mon, species);
            var item = ParseItemRequired(BoxItemBox, "箱子携带道具");
            mon.SetGrowth(
                species: species,
                item: item,
                exp: ParseUIntOrNull(BoxExpBox.Text),
                friendship: ParseByteOrNull(BoxFriendshipBox.Text),
                ppBonuses: BuildPpBonuses(BoxPpUp1Box, BoxPpUp2Box, BoxPpUp3Box, BoxPpUp4Box));
            if (SelectedChoiceId(BoxNatureBox) is { } nature)
                mon.SetNature(nature);
            mon.SetShiny(BoxShinyBox.IsChecked == true);
            if (SelectedChoiceId(BoxAbilityBox) is { } ability)
                mon.SetIvs(new Dictionary<string, int> { ["ability"] = ability });
            var ivs = BuildIntStats(BoxIvHpBox, BoxIvAtkBox, BoxIvDefBox, BoxIvSpeBox, BoxIvSpaBox, BoxIvSpdBox);
            foreach (var value in ivs.Values)
            {
                if (value is < 0 or > 31) throw new InvalidOperationException("个体值必须在 0..31 之间。");
            }
            var evs = BuildByteStats(BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpeBox, BoxEvSpaBox, BoxEvSpdBox);
            var evTotal = evs.Values.Sum(x => x);
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
            bridge.WriteRangeVerified(row.Address, mon.Raw.ToArray());
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

    private async void OnBoxDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (BoxList.SelectedItem is not BoxSlotRow row)
        {
            ShowToast("请先选择一个箱子槽。", success: false);
            return;
        }

        if (!await ConfirmBoxDeleteAsync(row)) return;

        await RunUiTask("删除箱子精灵", () =>
        {
            using var bridge = ConnectBridge();
            var live = new BoxPokemon(bridge.Read(row.Address, BoxPokemon.Size));
            if (live.IsEmpty)
                throw new InvalidOperationException("该箱子槽已经是空的。");
            if (!SamePokemonIdentity(row.Mon, live))
            {
                RefreshBoxAndSelectMovedMon(bridge, row);
                throw new InvalidOperationException($"箱子槽里的宝可梦已经变化。{MovedBoxHint(row)}");
            }

            bridge.WriteRangeVerified(row.Address, new byte[BoxPokemon.Size]);
            SetWriteNotice($"箱子槽已删除：{row.Title}。请回游戏确认后手动保存。");
            ShowToast("箱子精灵删除成功。", success: true);

            var oldIndex = BoxList.SelectedIndex;
            _boxRows.Remove(row);
            BoxList.SelectedIndex = _boxRows.Count == 0 ? -1 : Math.Clamp(oldIndex, 0, _boxRows.Count - 1);
        });
    }

    private async void OnDexImportPartyClicked(object? sender, RoutedEventArgs e)
    {
        if (DexSpeciesList.SelectedItem is not DexSpeciesRow row)
        {
            ShowToast("请先在图鉴中选择一个宝可梦。", success: false);
            return;
        }

        await RunUiTask("导入队伍", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var count = ReadLivePartyCount(bridge, baseAddr)
                        ?? throw new InvalidOperationException("没有读到当前队伍数量，请先读取队伍。");
            if (count >= Gen3Constants.PartySlots)
                throw new InvalidOperationException("队伍已满，无法从图鉴导入。");

            var otId = ResolveImportOtId(bridge, baseAddr, count);
            var mon = BuildDexPartyPokemon(row.Id, otId);
            var targetSlot = count + 1;
            var targetAddress = SlotAddress(baseAddr, targetSlot);
            bridge.WriteRangeVerified(targetAddress, mon.Raw);
            WritePartyCount(bridge, baseAddr, targetSlot);
            LoadPartyRows(bridge, baseAddr, targetSlot);
            SetWriteNotice($"已从图鉴导入到队伍槽位 {targetSlot}：{row.DisplayName}。请回游戏确认后手动保存。");
        });
    }

    private async void OnDexImportBoxClicked(object? sender, RoutedEventArgs e)
    {
        if (DexSpeciesList.SelectedItem is not DexSpeciesRow row)
        {
            ShowToast("请先在图鉴中选择一个宝可梦。", success: false);
            return;
        }

        await RunUiTask("导入箱子", () =>
        {
            using var bridge = ConnectBridge();
            var ewram = PartyScanner.ReadEwram(bridge);
            var run = BoxScanner.LocateBestRun(ewram)
                      ?? throw new InvalidOperationException("没有定位到箱子。请先确保箱子里有连续的非空槽位，再点“读取箱子”。");
            if (run.Length >= BoxScanner.BoxSlots * BoxScanner.MaxBoxes)
                throw new InvalidOperationException("箱子已满，无法从图鉴导入。");

            var targetAddress = run.StartAddress + (uint)(run.Length * BoxPokemon.Size);
            var targetOffset = checked((int)(targetAddress - PartyScanner.EwramBase));
            if (targetOffset < 0 || targetOffset + BoxPokemon.Size > ewram.Length)
                throw new InvalidOperationException("箱子追加地址超出 EWRAM 范围，无法安全写入。");
            if (!IsAllZero(ewram.AsSpan(targetOffset, BoxPokemon.Size)))
                throw new InvalidOperationException("箱子后方目标槽不是空槽，箱子可能已满或定位不可靠。请重新读取箱子后再试。");

            var otId = ResolveImportOtId(bridge, null, null);
            var mon = BuildDexBoxPokemon(row.Id, otId);
            bridge.WriteRangeVerified(targetAddress, mon.Raw);
            LoadBoxRows(bridge);
            BoxList.SelectedItem = _boxRows.FirstOrDefault(r => r.Address == targetAddress) ?? BoxList.SelectedItem;
            SetWriteNotice($"已从图鉴导入到箱子后方空槽：{row.DisplayName}。请回游戏确认后手动保存。");
        });
    }

    private PartyPokemon BuildDexPartyPokemon(int species, uint otId)
    {
        var stats = ReadSpeciesStats(species);
        var mon = PartyPokemon.Create(NewNonShinyPid(otId), otId);
        ApplyDexImportDefaults(mon, species, stats);
        mon.SetUnencrypted(status: 0, level: DexImportLevel);
        mon.RecalculateStats(stats);
        return mon;
    }

    private BoxPokemon BuildDexBoxPokemon(int species, uint otId)
    {
        var stats = ReadSpeciesStats(species);
        var mon = BoxPokemon.Create(NewNonShinyPid(otId), otId);
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

    private uint ResolveImportOtId(MgbaBridgeClient bridge, uint? partyBase, int? partyCount)
    {
        try
        {
            var baseAddr = partyBase ?? ResolvePartyBase(bridge, forceRefresh: true);
            var count = partyCount ?? ReadLivePartyCount(bridge, baseAddr) ?? 0;
            if (count > 0)
            {
                var first = new PartyPokemon(bridge.Read(SlotAddress(baseAddr, 1), Gen3Constants.PartyMonSize));
                if (first.OtId != 0) return first.OtId;
            }
        }
        catch
        {
            // Fall through to cached rows or a generated non-zero OT ID.
        }

        if (_partyRows.FirstOrDefault(r => r.Mon.OtId != 0) is { } partyRow)
            return partyRow.Mon.OtId;
        if (_boxRows.FirstOrDefault(r => r.Mon.OtId != 0) is { } boxRow)
            return boxRow.Mon.OtId;
        return NewNonZeroRandomUInt();
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

    private void OnDexSearchChanged(object? sender, TextChangedEventArgs e) => ApplyDexFilter();

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

    private void ApplyDexFilter(bool selectFirst = false)
    {
        var previousId = DexSpeciesList.SelectedItem is DexSpeciesRow selected ? selected.Id : (int?)null;
        var filter = DexSearchBox.Text?.Trim() ?? string.Empty;
        var rows = string.IsNullOrWhiteSpace(filter)
            ? _allDexRows
            : _allDexRows
                .Where(r => r.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            r.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();

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

        var next = previousId is null ? null : _dexRows.FirstOrDefault(r => r.Id == previousId.Value);
        DexSpeciesList.SelectedItem = next ?? (selectFirst ? _dexRows[0] : _dexRows[0]);
    }

    private void RefreshDexDetails(DexSpeciesRow row)
    {
        DexTitleText.Text = row.Title;
        SetDexSprite(row.Sprite);
        try
        {
            var stats = ReadSpeciesStats(row.Id);
            DexSubtitleText.Text = $"{TypePairText(stats.Type1, stats.Type2)}  |  总种族值 {stats.Bst}";
            FillDexBaseInfo(row, stats);
            FillDexEvolutionInfo(row);
            FillDexLevelMoves(row.Id);
        }
        catch (Exception ex)
        {
            DexSubtitleText.Text = "图鉴数据读取失败。";
            ClearDexDetails();
            _dexInfoRows.Add(new DexInfoRow("错误", ex.Message));
            SetDexEvolutionPlainText("错误：" + ex.Message);
            DexLevelMovesEmptyText.Text = "错误：" + ex.Message;
            DexLevelMovesEmptyText.IsVisible = true;
            Log("图鉴读取错误：" + ex.Message);
        }
    }

    private void ClearDexDetails()
    {
        _dexInfoRows.Clear();
        _dexStatRows.Clear();
        _dexLevelMoveRows.Clear();
        DexSpriteImage.Source = null;
        DexSpriteImage.IsVisible = false;
        SetDexEvolutionPlainText("");
        DexLevelMovesEmptyText.Text = "";
        DexLevelMovesEmptyText.IsVisible = false;
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

    private Bitmap? LoadSpriteBitmap(int speciesId)
    {
        if (_spriteCache.TryGetValue(speciesId, out var cached)) return cached;
        var sprite = LoadSpriteBitmapCore(speciesId);
        _spriteCache[speciesId] = sprite;
        return sprite;
    }

    private static Bitmap? LoadSpriteBitmapCore(int speciesId)
    {
        try
        {
            var uri = new Uri($"avares://RocketTool.Avalonia/Assets/Pokemon/front/{speciesId:0000}.png");
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
        _dexMaxLevel = MaxPokemonLevel;
        DexMaxLevelHeaderText.Text = $"{_dexMaxLevel}级时";
        DexStatsNoteText.Text = $"按 31 个体、0 努力、无性格修正计算；最高级按 ROM 等级上限 {_dexMaxLevel} 级显示。";

        var stats = new[]
        {
            ("HP", (int)s.Hp, true),
            ("攻击", (int)s.Attack, false),
            ("防御", (int)s.Defense, false),
            ("特攻", (int)s.SpAttack, false),
            ("特防", (int)s.SpDefense, false),
            ("速度", (int)s.Speed, false)
        };

        _dexStatRows.Clear();
        var sum50 = 0;
        var sum100 = 0;
        var sumMax = 0;
        foreach (var (name, baseStat, isHp) in stats)
        {
            var level50 = CalculateDexStat(baseStat, 50, isHp);
            var level100 = CalculateDexStat(baseStat, 100, isHp);
            var maxLevel = CalculateDexStat(baseStat, _dexMaxLevel, isHp);
            sum50 += level50;
            sum100 += level100;
            sumMax += maxLevel;
            _dexStatRows.Add(new DexStatRow(name, baseStat.ToString(), level50.ToString(), level100.ToString(), maxLevel.ToString()));
        }
        _dexStatRows.Add(new DexStatRow("合计", s.Bst.ToString(), sum50.ToString(), sum100.ToString(), sumMax.ToString()));
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
        var lines = new List<string> { "完整进化链" };
        if (branchCount > 0)
            lines.Add($"存在 {branchCount} 个分支节点，已按分支缩进显示。");
        lines.Add("");

        if (outgoingBySpecies.Values.All(evolutions => evolutions.Length == 0))
        {
            lines.Add(EvolutionSpeciesText(row.Id, row.Id));
            lines.Add("无进化链。");
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
        List<string> lines,
        int species,
        int selectedSpecies,
        IReadOnlyDictionary<int, SpeciesEvolution[]> outgoingBySpecies,
        HashSet<int> path,
        int depth)
    {
        var indent = new string(' ', depth * 2);
        lines.Add($"{indent}{EvolutionSpeciesText(species, selectedSpecies)}");

        if (!path.Add(species))
        {
            lines.Add($"{indent}  已检测到循环进化引用。");
            return;
        }

        AppendEvolutionChildren(lines, species, selectedSpecies, outgoingBySpecies, path, depth + 1);
    }

    private void AppendEvolutionChildren(
        List<string> lines,
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
                lines.Add($"{edgeIndent}{EvolutionEdgeLabel(evolution)} -> {EvolutionSpeciesText(target, selectedSpecies)}");
                if (path.Contains(target))
                {
                    lines.Add($"{edgeIndent}  已检测到循环进化引用。");
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

    private void SetDexEvolutionPlainText(string text)
    {
        DexEvolutionText.Inlines?.Clear();
        DexEvolutionText.Text = text;
    }

    private void SetDexEvolutionLines(IEnumerable<string> lines)
    {
        DexEvolutionText.Text = "";
        DexEvolutionText.Inlines?.Clear();
        var first = true;
        foreach (var line in lines)
        {
            if (!first)
                DexEvolutionText.Inlines?.Add(new LineBreak());
            first = false;

            var isCurrent = line.Contains("（当前）", StringComparison.Ordinal);
            DexEvolutionText.Inlines?.Add(new Run(line)
            {
                Foreground = isCurrent ? Brushes.Firebrick : Brushes.DarkSlateBlue,
                FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal
            });
        }
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
        {
            try
            {
                var data = ReadMoveData(entry.Move);
                _dexLevelMoveRows.Add(new DexLevelMoveRow(
                    $"Lv.{entry.Level:000}",
                    MoveName(entry.Move),
                    MoveTypeNameZh(data.Type),
                    MoveCategoryNameZh(data.Category),
                    MovePowerText(data.Power),
                    MoveAccuracyText(data.Accuracy),
                    data.Pp.ToString()));
            }
            catch
            {
                _dexLevelMoveRows.Add(new DexLevelMoveRow($"Lv.{entry.Level:000}", MoveName(entry.Move), "", "", "", "", ""));
            }
        }
    }

    private void OnLookupSpeciesClicked(object? sender, RoutedEventArgs e)
    {
        LookupTo(SpeciesResultBox, () =>
        {
            var id = ParseIntRequired(SpeciesLookupBox.Text, "宝可梦ID");
            var s = ReadSpeciesStats(id);
            return $"宝可梦 {id}（{_db.NameOf("species", id)}）\n" +
                   $"种族值：HP {s.Hp} / 攻击 {s.Attack} / 防御 {s.Defense} / 速度 {s.Speed} / 特攻 {s.SpAttack} / 特防 {s.SpDefense} / 总和 {s.Bst}\n" +
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
            return $"招式 {id}（{_db.NameOf("moves", id)}）\n" +
                   $"威力 {m.Power}  属性 {m.Type}（{MoveTypeNameZh(m.Type)}）  命中 {m.Accuracy}  PP {m.Pp}\n" +
                   $"分类 {m.Category}（{MoveCategoryNameZh(m.Category)}）  优先度 {m.Priority}  附加几率 {m.SecondaryEffectChance}\n" +
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
            return $"道具 {id}（{_db.NameOf("items", id)}）\n" +
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
            if (cheatName is "INFINITE_PP" or "LOCK_HP")
            {
                var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
                bridge.CheatCommand($"PARTY_BASE 0x{baseAddr:X}");
            }
            else if (cheatName is "NO_ENCOUNTER" && TryLocateBagBase(bridge) is { } saveBase)
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
            var status = bridge.CheatCommand("CLEAR");
            CheatStatusText.Text = "实验功能状态：" + status;
            _suppressCheatEvents = true;
            try
            {
                CheatInfinitePpBox.IsChecked = false;
                CheatLockHpBox.IsChecked = false;
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
        if (ReferenceEquals(box, CheatNoEncounterBox)) return "NO_ENCOUNTER";
        if (ReferenceEquals(box, CheatWalkThroughWallsBox)) return "WALK_THROUGH_WALLS";
        return null;
    }

    private void SyncCheatBoxesFromStatus(string status)
    {
        _suppressCheatEvents = true;
        try
        {
            CheatInfinitePpBox.IsChecked = status.Contains("INFINITE_PP=1", StringComparison.OrdinalIgnoreCase);
            CheatLockHpBox.IsChecked = status.Contains("LOCK_HP=1", StringComparison.OrdinalIgnoreCase);
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
            Text = "确定继续写入吗？建议先保存 mGBA 即时存档。",
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
        var ok = new Button { Content = "确定写入", Classes = { "primary" } };
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
                label.Contains("添加", StringComparison.Ordinal) ||
                label.Contains("恢复", StringComparison.Ordinal) ||
                label.Contains("传送", StringComparison.Ordinal))
            {
                SetWriteNotice($"{label}失败：{ex.Message}", success: false);
            }
            Log("错误：" + ex.Message);
        }
    }

    private MgbaBridgeClient ConnectBridge()
    {
        if (_dataSourceMode == DataSourceMode.SaveReadOnly)
            throw new InvalidOperationException("当前是存档只读模式。请先点击“连接 mGBA”切换到实时模式；存档写回功能尚未开放。");
        var host = string.IsNullOrWhiteSpace(HostBox.Text) ? "127.0.0.1" : HostBox.Text.Trim();
        var port = int.TryParse(PortBox.Text, out var parsed) ? parsed : 8765;
        var bridge = MgbaBridgeClient.Connect(host, port);
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
        var ewram = PartyScanner.ReadEwram(bridge);
        var run = PartyScanner.LocateParty(ewram) ?? throw new InvalidOperationException("没有定位到队伍。请先在游戏中载入存档，再点击“读取队伍”。");
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
        var detail = $"HP {info.Hp}/{info.MaxHp}  {shiny}  性格 {NatureText(info.Nature, info.GameNatureCode)}  特性 {AbilityText(info.Species, info.Ivs["ability"])}  携带 {item}  校验 {ok}";
        return new PartySlotRow(slot, addr, mon, info, title, detail, LoadSpriteBitmap(info.Species));
    }

    private void FillEditor(PartySlotRow row)
    {
        var headerInfo = row.Info;
        SelectedTitleText.Text = headerInfo is not null
            ? $"{SpeciesName(headerInfo.Species)} Lv{headerInfo.Level}"
            : row.Title;
        SetPokemonSprite(PartyDetailSpriteImage, headerInfo?.Species ?? 0);
        SelectedDetailText.Text = string.Empty;
        SelectedDetailText.IsVisible = false;
        if (row.Info is not { } info)
        {
            ClearEditor();
            return;
        }
        _suppressEditorEvents = true;
        try
        {
            SetChoice(SpeciesBox, _speciesChoices, info.Species);
            SetChoice(ItemBox, _itemChoices, info.Item);
            SetChoice(NatureBox, _natureChoices, info.Nature);
            RefreshAbilityChoices(info.Species, info.Ivs["ability"]);
            ExpBox.Text = info.Exp.ToString();
            FriendshipBox.Text = info.Friendship.ToString();
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
            StatusBox.SelectedItem = null;
            ShinyBox.IsChecked = false;
            AbilityBox.ItemsSource = null;
            foreach (var box in EditorBoxes()) box.Text = string.Empty;
            ClearStatTexts();
            SetPokemonSprite(PartyDetailSpriteImage, 0);
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
        yield return LevelBox; yield return MaxHpBox;
        yield return Pp1Box; yield return Pp2Box; yield return Pp3Box; yield return Pp4Box;
        yield return EvHpBox; yield return EvAtkBox; yield return EvDefBox; yield return EvSpeBox; yield return EvSpaBox; yield return EvSpdBox;
        yield return IvHpBox; yield return IvAtkBox; yield return IvDefBox; yield return IvSpeBox; yield return IvSpaBox; yield return IvSpdBox;
    }

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
        definition ??= BagScanner.DefinitionForPocket(pocket);
        var pocketName = PocketNameZh(pocket);
        var prefix = slotNumber is null ? "" : $"{slotNumber.Value:00}. ";
        var title = itemId == 0
            ? $"{prefix}空槽"
            : pocket == 8
                ? $"{prefix}{name}"
                : $"{prefix}{name} x{quantity}";
        var detail = $"{pocketName}  地址 0x{address:X8}  {note}";
        return new BagSlotRow(address, itemId, quantity, pocket, definition?.QuantityKey ?? 0, definition?.QuantityXor == true, title, detail);
    }

    private void LoadBagRows(MgbaBridgeClient bridge)
    {
        _bagBase = TryLocateBagBase(bridge);
        var ewram = PartyScanner.ReadEwram(bridge);
        var pockets = BagScanner.FindLivePockets(ewram, _bagBase, PocketOfItem)
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

    private void LoadBoxRows(MgbaBridgeClient bridge)
    {
        var ewram = PartyScanner.ReadEwram(bridge);
        var storage = BoxScanner.LocatePcStorage(ewram);
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
            var offset = checked((int)(address - PartyScanner.EwramBase));
            var mon = new BoxPokemon(ewram.AsSpan(offset, BoxPokemon.Size));
            var info = mon.GetInfo();
            var slotInBox = ((globalSlot - 1) % BoxScanner.BoxSlots) + 1;
            var boxNo = ((globalSlot - 1) / BoxScanner.BoxSlots) + 1;
            var title = $"箱{boxNo:00}-{slotInBox:00} {(mon.IsShiny ? "★" : "")}{SpeciesName(info.Species)}";
            if (info.Item != 0) title += $" / {ItemName(info.Item)}";
            if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
            _boxRows.Add(new BoxSlotRow(globalSlot, address, mon, info, title, $"地址 0x{address:X8}", LoadSpriteBitmap(info.Species)));
        }

        RefreshBoxGridGroups();
        if (_boxRows.Count > 0) BoxList.SelectedIndex = 0;
        Log($"已定位箱子存储：起始 0x{storage.StartAddress:X8}，非空 {storage.NonEmptyCount}/{BoxScanner.TotalSlots} 槽。");
    }

    private void LoadBoxRowsFromBestRun(ReadOnlySpan<byte> ewram)
    {
        var run = BoxScanner.LocateBestRun(ewram) ?? throw new InvalidOperationException("没有定位到箱子宝可梦。请先确保箱子里有非空槽位。");
        _boxBase = run.StartAddress;
        _boxGridShowAllBoxes = false;
        _boxRows.Clear();
        for (var i = 0; i < run.Candidates.Count; i++)
        {
            var address = run.Candidates[i].Address;
            var offset = checked((int)(address - PartyScanner.EwramBase));
            var mon = new BoxPokemon(ewram.Slice(offset, BoxPokemon.Size));
            var info = mon.GetInfo();
            var slotInBox = (i % BoxScanner.BoxSlots) + 1;
            var boxNo = (i / BoxScanner.BoxSlots) + 1;
            var title = $"箱{boxNo:00}-{slotInBox:00} {(mon.IsShiny ? "★" : "")}{SpeciesName(info.Species)}";
            if (info.Item != 0) title += $" / {ItemName(info.Item)}";
            if (HasSummaryAllStatsIncreaseNatureCode(info.GameNatureCode)) title += " / 性格代码异常";
            _boxRows.Add(new BoxSlotRow(i + 1, address, mon, info, title, $"地址 0x{address:X8}", LoadSpriteBitmap(info.Species)));
        }

        RefreshBoxGridGroups();
        if (_boxRows.Count > 0) BoxList.SelectedIndex = 0;
        Log($"只定位到连续箱子候选：起始 0x{run.StartAddress:X8}，连续非空 {_boxRows.Count} 槽；箱号可能不完整。");
    }

    private void RefreshBoxGridGroups()
    {
        _boxGridGroups.Clear();
        var rowsByBox = _boxRows.GroupBy(row => (row.Slot - 1) / BoxScanner.BoxSlots + 1)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var boxNumbers = _boxGridShowAllBoxes
            ? Enumerable.Range(1, BoxScanner.MaxBoxes)
            : rowsByBox.Keys.OrderBy(x => x);
        foreach (var boxNumber in boxNumbers)
        {
            var bySlot = rowsByBox.TryGetValue(boxNumber, out var groupRows)
                ? groupRows.ToDictionary(row => (row.Slot - 1) % BoxScanner.BoxSlots + 1)
                : new Dictionary<int, BoxSlotRow>();
            var cells = Enumerable.Range(1, BoxScanner.BoxSlots)
                .Select(slotInBox =>
                {
                    if (bySlot.TryGetValue(slotInBox, out var row))
                        return new BoxGridCell(slotInBox, row, row.Sprite, row.Title, true);
                    return new BoxGridCell(slotInBox, null, null, "空槽", false);
                })
                .ToArray();
            _boxGridGroups.Add(new BoxGridGroup(boxNumber, $"箱{boxNumber:00}", cells));
        }
    }

    private void ApplyBagPocketFilter()
    {
        if (_bagRows is null || BagPocketTabs is null || BagItemBox is null || _itemChoices is null) return;
        var selectedPocket = SelectedChoiceId(BagPocketTabs) ?? 0;
        var rows = selectedPocket == 0
            ? _allBagRows
            : _allBagRows.Where(row => row.Pocket == selectedPocket);
        _bagRows.Clear();
        foreach (var row in rows)
            _bagRows.Add(row);
        if (BagList.SelectedItem is BagSlotRow selectedRow && _bagRows.Contains(selectedRow))
            SetBagItemChoicesForPocket(selectedRow.Pocket);
        else
            SetBagItemChoicesForPocket(selectedPocket);
        UpdateBagNameText();
    }

    private void HookNameRefresh()
    {
        SpeciesBox.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        ItemBox.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
        NatureBox.SelectionChanged += (_, _) => UpdateNameHintsFromBoxes();
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
        BagItemBox.SelectionChanged += (_, _) => UpdateBagNameText();
        BoxSpeciesBox.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxItemBox.SelectionChanged += (_, _) => UpdateBoxNameText();
        BoxNatureBox.SelectionChanged += (_, _) => UpdateBoxNameText();
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
        var itemName = info.Item == 0 ? "无" : ItemName(info.Item);
        var nature = NatureText(info.Nature, info.GameNatureCode);
        var ability = AbilityText(info.Species, info.Ivs["ability"]);
        BasicNameText.Text = $"种类：{SpeciesName(info.Species)}  携带道具：{itemName}  性格：{nature}  特性：{ability}  闪光：{(PartyPokemon.IsShinyPid(info.Pid, info.OtId) ? "是" : "否")}";
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
            SetPokemonSprite(PartyDetailSpriteImage, species);
            RefreshAbilityChoices(species, SelectedAbilitySlot());
            UpdateBaseStatTexts(species);
            var nature = SelectedChoiceId(NatureBox) is { } n ? $"PID性格：{NatureDisplays[n]}" : "PID性格：未选择";
            var ability = AbilityBox.SelectedItem?.ToString() ?? "未选择";
            BasicNameText.Text = $"种类：{(species == 0 ? "无" : SpeciesName(species))}  携带道具：{ItemName(item)}  {nature}  特性：{ability}  闪光：{(ShinyBox.IsChecked == true ? "是" : "否")}";

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
            var warning = item != 0 && editPocket is >= 1 and <= 8 && itemPocket != editPocket
                ? $"  注意：不属于当前{PocketNameZh(editPocket)}，不能写入。"
                : "";
            BagItemNameText.Text = item == 0
                ? "道具：无"
                : $"道具：{ItemName(item)}  口袋：{PocketNameZh(itemPocket)}{warning}";
        }
        catch
        {
            BagItemNameText.Text = "道具ID无法解析。";
        }
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
            RefreshBoxAbilityChoices(species, SelectedChoiceId(BoxAbilityBox));
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
        _bagItemChoices = BagItemChoicesForPocket(pocket);
        _searchableChoices[BagItemBox] = _bagItemChoices;
        ResetSearchableComboItems(BagItemBox, _bagItemChoices, preserveSelection: selectedItem is null);
        if (selectedItem is not null)
            SetChoice(BagItemBox, _bagItemChoices, selectedItem.Value);
    }

    private ChoiceRow[] BagItemChoicesForPocket(int pocket)
    {
        if (pocket is < 1 or > 8)
            return _itemChoices;
        return _itemChoices
            .Where(choice => choice.Id == 0 || PocketOfItem(choice.Id) == pocket)
            .ToArray();
    }

    private int CurrentBagEditPocket()
    {
        if (BagList.SelectedItem is BagSlotRow row &&
            string.Equals(BagAddressBox.Text, $"0x{row.Address:X8}", StringComparison.OrdinalIgnoreCase))
            return row.Pocket;
        var selectedPocket = SelectedChoiceId(BagPocketTabs) ?? 0;
        return selectedPocket is >= 1 and <= 8 ? selectedPocket : 0;
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
        if (requiredPocket is >= 1 and <= 8 && actualPocket != requiredPocket)
            throw new InvalidOperationException($"“{ItemName(item)}”属于{PocketNameZh(actualPocket)}，不能写入当前{PocketNameZh(requiredPocket)}。");
        return item;
    }

    private ushort ParseBagQuantityRequired(ushort item, bool allowZero)
    {
        var quantity = ParseUShortRequired(BagQuantityBox.Text, "数量");
        if (item == 0)
        {
            if (quantity == 0) return 0;
            throw new InvalidOperationException("清空槽位时数量必须为 0。");
        }

        if (!allowZero && quantity == 0)
            throw new InvalidOperationException("背包道具数量必须大于 0。");
        if (quantity > MaxBagWriteQuantity)
            throw new InvalidOperationException($"背包道具数量最多只能写入 {MaxBagWriteQuantity}。");
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
        => Math.Max(BagScanner.DefaultMaxItemId, _db.Table("items").Keys.DefaultIfEmpty(0).Max());

    private int MaxSpeciesId()
        => Math.Min(ushort.MaxValue, _db.Table("species").Keys.DefaultIfEmpty(0).Max());

    private int MaxMoveId()
        => Math.Min(ushort.MaxValue, _db.Table("moves").Keys.DefaultIfEmpty(0).Max());

    private ChoiceRow[] ChoiceRows(string table)
        => _db.Table(table).OrderBy(kv => kv.Key).Select(kv => new ChoiceRow(kv.Key, kv.Value)).ToArray();

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
        if (!table.TryGetValue(species, out var name)) return fallback;
        var duplicateIds = table
            .Where(kv => kv.Value == name)
            .Select(kv => kv.Key)
            .OrderBy(id => id)
            .ToArray();
        return duplicateIds.Length > 1 ? DuplicateSpeciesDisplay(species, name, duplicateIds) : name;
    }

    private static string DuplicateSpeciesDisplay(int species, string name, IReadOnlyList<int> duplicateIds)
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

    private static string? KnownSpeciesFormLabel(int species)
    {
        if (species is >= 977 and <= 1032) return species switch
        {
            978 => "Mega X",
            979 => "Mega Y",
            990 => "Mega X",
            991 => "Mega Y",
            _ => "Mega"
        };
        if (species == 1033) return "Mega";
        if (species is 1034 or 1035) return "原始回归";
        if (species is >= 1046 and <= 1063) return "阿罗拉";
        if (species is 1064 or >= 1065 and <= 1082) return "伽勒尔";
        if (species is >= 1083 and <= 1096) return $"换装{species - 1082}";
        if (species is >= 1098 and <= 1124) return $"字母形态{species - 1097}";
        if (species is >= 1145 and <= 1161)
        {
            string[] types =
            [
                "格斗", "飞行", "毒", "地面", "岩石", "虫", "幽灵", "钢",
                "火", "水", "草", "电", "超能力", "冰", "龙", "恶", "妖精"
            ];
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
            913 or 914 or 915 => "P",
            919 => "特殊形态",
            920 => "攻击形态",
            921 => "防御形态",
            923 => "闪电卡带",
            924 => "火焰卡带",
            944 => "盾牌形态",
            947 => "特殊形态",
            953 => "标点形态",
            1072 or 1073 or 1074 => "伽勒尔",
            1097 => "刺刺耳",
            1125 => "太阳",
            1126 => "雨水",
            1127 => "雪云",
            1128 => "攻击形态",
            1129 => "防御形态",
            1130 => "速度形态",
            1131 or 1133 => "沙土蓑衣",
            1132 or 1134 => "垃圾蓑衣",
            1135 => "晴天",
            1136 or 1137 => "东海",
            1138 => "加热",
            1139 => "清洗",
            1140 => "结冰",
            1141 => "旋转",
            1142 => "切割",
            1143 => "起源形态",
            1144 => "天空形态",
            1162 => "蓝条纹",
            1163 => "达摩模式",
            1164 => "伽勒尔达摩模式",
            1165 or 1168 => "夏天",
            1166 or 1169 => "秋天",
            1167 or 1170 => "冬天",
            1171 or 1172 or 1173 => "灵兽形态",
            1174 => "焰白",
            1175 => "暗黑",
            1176 => "觉悟形态",
            1177 => "舞步形态",
            1178 => "水流卡带",
            1179 => "冰冻卡带",
            1180 => "闪电卡带",
            1181 => "火焰卡带",
            1182 => "小智版",
            1183 => "羁绊变身",
            1225 => "雌性",
            1226 => "刀剑形态",
            1227 or 1230 => "小尺寸",
            1228 or 1231 => "大尺寸",
            1229 or 1232 => "特大尺寸",
            1233 => "活跃模式",
            1234 => "10%形态",
            1235 => "50%形态",
            1236 => "完全体",
            1237 => "核心",
            1238 => "解放形态",
            1239 => "啪滋啪滋",
            1240 => "呼拉呼拉",
            1241 => "轻盈轻盈",
            1242 => "我行我素",
            1243 => "黑夜",
            1244 => "黄昏",
            1245 => "鱼群形态",
            1276 => "现形",
            1277 => "黄昏之鬃",
            1278 => "拂晓之翼",
            1279 => "究极",
            1280 => "500年前",
            1281 => "一口吞",
            1282 => "大口吞",
            1283 => "低调",
            1284 or 1285 => "真品",
            1294 => "解冻头",
            1295 => "雌性",
            1296 => "空腹花纹",
            1297 => "剑之王",
            1298 => "盾之王",
            1299 => "无极巨化",
            1300 => "连击流",
            1301 => "披披形态",
            1302 => "白马",
            1303 => "黑马",
            1304 => "P形态2",
            1307 => "P形态2",
            1310 or 1311 or 1312 or 1315 or 1317 or 1319 or 1320 or 1321 or 1322 or 1326 or 1327 or 1328 or 1330 or 1331 or 1332 or 1333 => "洗翠",
            1323 => "白条纹",
            1324 => "雄性",
            1325 => "雌性",
            1334 => "化身形态",
            1335 => "灵兽形态",
            1336 or 1337 => "起源形态",
            1338 => "帕底亚",
            1342 => "帕底亚斗战种",
            1343 => "帕底亚火炽种",
            1344 => "帕底亚水澜种",
            1383 => "超极巨",
            _ => null
        };
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

    private static int LevelFromExp(uint exp, byte growthRate)
    {
        var level = 1;
        while (level < MaxPokemonLevel && ExperienceForLevel(level + 1, growthRate) <= exp)
            level++;
        return level;
    }

    private static uint ExperienceForLevel(int level, byte growthRate)
    {
        var n = Math.Clamp(level, 1, MaxPokemonLevel);
        var n2 = n * n;
        var n3 = n2 * n;
        var exp = growthRate switch
        {
            1 when n <= 50 => n3 * (100 - n) / 50,
            1 when n <= 68 => n3 * (150 - n) / 100,
            1 when n <= 98 => n3 * ((1911 - 10 * n) / 3) / 500,
            1 => n3 * (160 - n) / 100,
            2 when n <= 15 => n3 * (((n + 1) / 3) + 24) / 50,
            2 when n <= 36 => n3 * (n + 14) / 50,
            2 => n3 * ((n / 2) + 32) / 50,
            3 => (6 * n3 / 5) - (15 * n2) + (100 * n) - 140,
            4 => 4 * n3 / 5,
            5 => 5 * n3 / 4,
            _ => n3
        };
        return (uint)Math.Max(0, exp);
    }

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
            stats = new SpeciesStats(
                species, B(0), B(1), B(2), B(3), B(4), B(5), B(6), B(7),
                U(8), U(9), U(10), U(11), U(12), B(13), B(14), B(15), B(16),
                B(17), B(18), U(19), U(20), U(21));
            return true;
        }
        catch
        {
            return false;
        }
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
            sbyte S(int index) => sbyte.Parse(parts[index]);
            data = new MoveData(move, U(0), B(1), B(2), B(3), B(4), U(5), S(6), U(7), B(8), U(9), []);
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
        if (!_db.Table("species_name_bytes").TryGetValue(species, out var hex))
            throw new InvalidOperationException($"缺少宝可梦 {SpeciesName(species)} 的内部名字字节，无法自动同步昵称。");

        try
        {
            var bytes = Convert.FromHexString(hex.Trim());
            if (bytes.Length < 10)
                throw new FormatException("entry too short");
            return bytes;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"宝可梦 {SpeciesName(species)} 的内部名字字节格式无效，无法自动同步昵称。", ex);
        }
    }

    private void RefreshAbilityChoices(int species, int? selectedBit)
    {
        if (species <= 0) return;
        if (_abilitySpecies == species && AbilityBox.ItemsSource is IEnumerable<object> existing)
        {
            if (selectedBit is not null)
                AbilityBox.SelectedItem = existing.OfType<ChoiceRow>().FirstOrDefault(c => c.Id == selectedBit.Value) ?? AbilityBox.SelectedItem;
            return;
        }

        try
        {
            var stats = ReadSpeciesStats(species);
            var choices = new List<ChoiceRow>
            {
                new(0, $"特性1：{AbilityName(stats.Ability1)}", $"特性1：{AbilityName(stats.Ability1)}"),
                new(1, $"特性2：{AbilityName(stats.Ability2)}", $"特性2：{AbilityName(stats.Ability2)}")
            };
            if (stats.Ability3 is not null)
                choices.Add(new(2, $"特性3（隐藏）：{AbilityName(stats.Ability3.Value)}", $"特性3（隐藏）：{AbilityName(stats.Ability3.Value)}"));
            _abilitySpecies = species;
            AbilityBox.ItemsSource = choices;
            AbilityBox.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedBit) ?? choices.First();
        }
        catch
        {
            SetFallbackAbilityChoices(AbilityBox, species, selectedBit, ref _abilitySpecies);
        }
    }

    private void RefreshBoxAbilityChoices(int species, int? selectedBit)
    {
        if (species <= 0) return;
        if (_boxAbilitySpecies == species && BoxAbilityBox.ItemsSource is IEnumerable<object> existing)
        {
            if (selectedBit is not null)
                BoxAbilityBox.SelectedItem = existing.OfType<ChoiceRow>().FirstOrDefault(c => c.Id == selectedBit.Value) ?? BoxAbilityBox.SelectedItem;
            return;
        }

        try
        {
            var stats = ReadSpeciesStats(species);
            var choices = new List<ChoiceRow>
            {
                new(0, $"特性1：{AbilityName(stats.Ability1)}", $"特性1：{AbilityName(stats.Ability1)}"),
                new(1, $"特性2：{AbilityName(stats.Ability2)}", $"特性2：{AbilityName(stats.Ability2)}")
            };
            if (stats.Ability3 is not null)
                choices.Add(new(2, $"特性3（隐藏）：{AbilityName(stats.Ability3.Value)}", $"特性3（隐藏）：{AbilityName(stats.Ability3.Value)}"));
            _boxAbilitySpecies = species;
            BoxAbilityBox.ItemsSource = choices;
            BoxAbilityBox.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedBit) ?? choices.First();
        }
        catch
        {
            SetFallbackAbilityChoices(BoxAbilityBox, species, selectedBit, ref _boxAbilitySpecies);
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
        {
            var choices = new List<ChoiceRow>
            {
                new(0, $"特性1：{AbilityName(ability1)}", $"特性1：{AbilityName(ability1)}"),
                new(1, $"特性2：{AbilityName(ability2)}", $"特性2：{AbilityName(ability2)}")
            };
            if (ability3 is not null)
                choices.Add(new(2, $"特性3（隐藏）：{AbilityName(ability3.Value)}", $"特性3（隐藏）：{AbilityName(ability3.Value)}"));
            return choices;
        }

        return
        [
            new(0, "特性1", "特性1"),
            new(1, "特性2", "特性2"),
            new(2, "特性3（隐藏）", "特性3（隐藏）")
        ];
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
                1 => $"{AbilityName(stats.Ability2)}（特性2）",
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
                    1 => $"{AbilityName(ability2)}（特性2）",
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
        return _db.Table("ability_descriptions").TryGetValue(ability, out var description)
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
        18 => ("#D685AD", "#33111F"), // 妖精
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
            0xFFFF => paramItem,
            0xFFFE => $"特殊形态 {EvolutionParameterText(evolution.Parameter)}",
            0xFFFD => $"特殊形态 {EvolutionParameterText(evolution.Parameter)}",
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
            0xFFFF => $"特殊形态，使用{paramItem}",
            0xFFFE => $"特殊形态，参数{EvolutionParameterText(evolution.Parameter)}",
            0xFFFD => $"特殊形态，参数{EvolutionParameterText(evolution.Parameter)}",
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
        254 => "只有雌性",
        0 => "只有雄性",
        _ => $"雌性约 {genderRatio * 100.0 / 254.0:0.#}%"
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

    private static string MovePowerText(ushort power)
        => power == 0 ? "-" : power.ToString();

    private static string MoveAccuracyText(ushort accuracy)
        => accuracy == 0 ? "-" : accuracy.ToString();

    private string? MachineMoveName(int item)
    {
        var tmIndex = item - MachineTmStartItem;
        if (tmIndex >= 0 && tmIndex < MachineTmCount)
        {
            var moveId = MachineMoveId(tmIndex);
            if (moveId > 0) return $"NO.{tmIndex + 1:000} {MoveName(moveId)}";
        }

        var hmIndex = item - MachineHmStartItem;
        if (hmIndex >= 0 && hmIndex < MachineHmCount)
        {
            var moveId = MachineMoveId(MachineTmCount + hmIndex);
            if (moveId > 0) return $"MO{hmIndex + 1} {MoveName(moveId)}";
        }

        return null;
    }

    private int MachineMoveId(int machineIndex)
    {
        if (_db.Table("machine_moves").TryGetValue(machineIndex, out var embedded) && int.TryParse(embedded, out var moveId))
            return moveId;

        return machineIndex >= 0 && machineIndex < Gen3TmMoveIds.Length
            ? Gen3TmMoveIds[machineIndex]
            : 0;
    }

    private int PocketOfItem(int item)
    {
        if (item == 0) return -1;
        if (item < 0 || item > MaxItemId()) return -1;

        if (_db.Table("item_pockets").TryGetValue(item, out var pocketText) &&
            int.TryParse(pocketText, out var embeddedPocket) &&
            embeddedPocket is >= 1 and <= 8)
            return embeddedPocket;

        try
        {
            return ReadItemData(item).Pocket;
        }
        catch
        {
            var fromOriginalRanges = PocketOfItemByOriginalRanges(item);
            return fromOriginalRanges ?? -1;
        }
    }

    private static int? PocketOfItemByOriginalRanges(int item)
    {
        if (item <= 0) return null;
        if (item is >= 1 and <= 27) return 3;
        if (item is >= 28 and <= 48 or 921) return 2;
        if (item is >= 512 and <= 591) return 5;
        if (item is >= MachineTmStartItem and < MachineTmStartItem + MachineTmCount) return 7;
        if (item is >= MachineHmStartItem and < MachineHmStartItem + MachineHmCount) return 7;
        if (item >= 0x300) return 8;
        return null;
    }

    private void EnsureBagSelectionFresh(MgbaBridgeClient bridge)
    {
        var liveBase = TryLocateBagBase(bridge);
        if (_bagBase is not null && liveBase is not null && liveBase.Value != _bagBase.Value)
        {
            LoadBagRows(bridge);
            throw new InvalidOperationException("背包位置已变化，已刷新背包列表。请重新选择槽位后再写入。");
        }
        _bagBase = liveBase;
    }

    private BoxPokemon ReadSelectedBoxMon(MgbaBridgeClient bridge, BoxSlotRow row)
    {
        if (_boxBase is not null)
            EnsureBoxStorageBaseFresh(bridge);

        var mon = new BoxPokemon(bridge.Read(row.Address, BoxPokemon.Size));
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
        var ewram = PartyScanner.ReadEwram(bridge);
        var storage = BoxScanner.LocatePcStorage(ewram);
        if (storage is not null)
        {
            if (_boxBase is { } boxBase && storage.StartAddress == boxBase) return;
            LoadBoxRows(bridge);
            throw new InvalidOperationException("箱子位置已变化，已刷新箱子列表。请重新选择槽位后再写入。");
        }

        var run = BoxScanner.LocateBestRun(ewram) ?? throw new InvalidOperationException("没有定位到箱子宝可梦。请重新读取箱子后再写入。");
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
            _bagBase ??= BagScanner.LocateSaveBlockBase(bridge);
            return BagScanner.DefinitionForAddress(_bagBase.Value, address);
        }
        catch
        {
            return null;
        }
    }

    private static uint? TryLocateBagBase(MgbaBridgeClient bridge)
    {
        try
        {
            return BagScanner.LocateSaveBlockBase(bridge);
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

    private static void WriteMon(MgbaBridgeClient bridge, uint addr, PartyPokemon mon) => bridge.WriteRangeVerified(addr, mon.Raw);

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



    private static (uint Address, PartyPokemon Mon) ReadSelectedLiveMon(MgbaBridgeClient bridge, uint baseAddr, PartySlotRow row)
    {
        var addr = LiveSlotAddress(bridge, baseAddr, row.Slot);
        var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
        if (row.Mon.IsEmpty || row.Mon.Pid == mon.Pid && row.Mon.OtId == mon.OtId)
            return (addr, mon);

        throw new InvalidOperationException($"队伍槽位 {row.Slot} 的宝可梦已经变化。请先重新读取队伍，再写入。");
    }

    private static uint LiveSlotAddress(MgbaBridgeClient bridge, uint baseAddr, int slot)
    {
        var count = ReadLivePartyCount(bridge, baseAddr);
        if (count is not null && slot > count.Value)
            throw new InvalidOperationException($"当前队伍只有 {count.Value} 只，槽位 {slot} 已不在队伍中。请重新读取队伍。");
        return SlotAddress(baseAddr, slot);
    }

    private static uint SlotAddress(uint baseAddr, int slot) => baseAddr + (uint)((slot - 1) * Gen3Constants.PartyMonSize);

    private static string RootDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, string.Concat(Enumerable.Repeat("../", i))));
            if (Directory.Exists(Path.Combine(candidate, "modifier_db"))) return candidate;
        }
        return AppContext.BaseDirectory;
    }

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

    private static string PocketNameZh(int pocket) => pocket switch
    {
        1 => "普通道具",
        2 => "回复药品",
        3 => "精灵球",
        4 => "战斗道具",
        5 => "树果",
        6 => "宝物",
        7 => "招式机器/秘传机器",
        8 => "重要物品",
        -1 => "未知/候选",
        _ => $"#{pocket}"
    };

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
        18 => "妖精",
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
        LastWriteText.Text = $"最近写入：{message}";
        LastWriteText.Foreground = new SolidColorBrush(success ? Color.FromRgb(16, 82, 58) : Color.FromRgb(150, 43, 43));
        ShowToast(message, success);
        Log(message);
    }

    private void ShowToast(string message, bool success = true)
    {
        ToastText.Text = message;
        ToastBorder.Background = new SolidColorBrush(success ? Color.FromRgb(16, 46, 74) : Color.FromRgb(150, 43, 43));
        ToastBorder.IsVisible = true;
        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _toastTimer.Stop();
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    private void HideToast(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        ToastBorder.IsVisible = false;
    }

    private void Log(string message)
    {
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogBox.CaretIndex = LogBox.Text.Length;
    }
}
