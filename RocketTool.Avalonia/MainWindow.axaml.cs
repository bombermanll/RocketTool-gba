using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RocketTool.Core;

namespace RocketTool.Avalonia;

public sealed record PartySlotRow(int Slot, uint Address, PartyPokemon Mon, PartyMonInfo? Info, string Title, string Detail);
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
public sealed record BoxSlotRow(int Slot, uint Address, BoxPokemon Mon, BoxMonInfo Info, string Title, string Detail);
public sealed record ChoiceRow(int Id, string Name, string? Display = null)
{
    public override string ToString() => Display ?? Name;
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

    private static readonly int[] Gen3TmMoveIds =
    [
        264, 337, 352, 347, 46, 92, 258, 339, 331, 237,
        241, 269, 58, 59, 63, 113, 182, 240, 202, 219,
        218, 76, 231, 85, 87, 89, 216, 91, 94, 247,
        280, 104, 115, 351, 53, 188, 201, 126, 317, 332,
        259, 263, 290, 156, 213, 168, 211, 285, 289, 315
    ];
    private const int MachineMoveTableOffset = 0xCF8C54;
    private const int MachineTmStartItem = 592;
    private const int MachineTmCount = 246;
    private const int MachineHmStartItem = 838;
    private const int MachineHmCount = 8;
    private const ushort MaxBagWriteQuantity = 255;

    private readonly ObservableCollection<PartySlotRow> _partyRows = [];
    private readonly ObservableCollection<BagSlotRow> _bagRows = [];
    private readonly ObservableCollection<BoxSlotRow> _boxRows = [];
    private readonly List<BagSlotRow> _allBagRows = [];
    private readonly ChoiceRow[] _speciesChoices;
    private readonly ChoiceRow[] _itemChoices;
    private readonly ChoiceRow[] _moveChoices;
    private readonly ChoiceRow[] _natureChoices;
    private readonly ChoiceRow[] _statusChoices;
    private readonly ChoiceRow[] _ppBonusChoices;
    private readonly ChoiceRow[] _bagPocketChoices;
    private IReadOnlyList<ChoiceRow> _bagItemChoices = [];
    private readonly ModifierDatabase _db;
    private uint? _partyBase;
    private uint? _boxBase;
    private uint? _bagBase;
    private SpeciesStatsReader? _speciesReader;
    private string? _speciesReaderPath;
    private ItemDataReader? _itemReader;
    private string? _itemReaderPath;
    private MoveDataReader? _moveReader;
    private string? _moveReaderPath;
    private ushort[]? _machineMoveIds;
    private string? _machineMoveReaderPath;
    private int? _abilitySpecies;
    private int? _boxAbilitySpecies;
    private bool _suppressEditorEvents;
    private bool _updatingSearchableCombo;
    private DispatcherTimer? _toastTimer;
    private readonly Dictionary<ComboBox, IReadOnlyList<ChoiceRow>> _searchableChoices = [];

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowTitle();
        _db = new ModifierDatabase(Path.Combine(RootDir(), "modifier_db"), typeof(MainWindow).Assembly);
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
        ConfigureSearchableCombo(SpeciesBox, _speciesChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(ItemBox, _itemChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(Move1Box, _moveChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(Move2Box, _moveChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(Move3Box, _moveChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(Move4Box, _moveChoices, UpdateNameHintsFromBoxes);
        ConfigureSearchableCombo(BagItemBox, _bagItemChoices, UpdateBagNameText);
        ConfigureSearchableCombo(BoxSpeciesBox, _speciesChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxItemBox, _itemChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxMove1Box, _moveChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxMove2Box, _moveChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxMove3Box, _moveChoices, UpdateBoxNameText);
        ConfigureSearchableCombo(BoxMove4Box, _moveChoices, UpdateBoxNameText);
        NatureBox.ItemsSource = _natureChoices;
        StatusBox.ItemsSource = _statusChoices;
        BoxNatureBox.ItemsSource = _natureChoices;
        foreach (var box in PpBonusBoxes())
        {
            box.ItemsSource = _ppBonusChoices;
            box.SelectedIndex = 0;
        }
        FitComboToContent(StatusBox, _statusChoices);
        BagPocketTabs.ItemsSource = _bagPocketChoices;
        BagPocketTabs.SelectedIndex = 0;
        ConfigureNumericInputLimits();
        HookNameRefresh();
        RomPathBox.Text = DefaultRom();
        Log("界面已就绪。请先在 mGBA 加载 bridge 脚本，然后点击“连接 mGBA”。");
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
                     BagQuantityBox
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
        await RunUiTask("连接 mGBA", () =>
        {
            using var bridge = ConnectBridge();
            var gameCode = bridge.GameCode();
            ConnectionStatusText.Text = $"上次连接成功 {gameCode}；载入存档后点击“读取队伍”";
            Log($"已连接 mGBA：游戏={gameCode}。队伍数据尚未读取。连接会在本次操作结束后自动关闭，这是正常现象。");
        });
    }

    private async void OnReloadPartyClicked(object? sender, RoutedEventArgs e) => await ReloadPartyAsync();

    private void ConfigureSearchableCombo(ComboBox box, IReadOnlyList<ChoiceRow> choices, Action changed)
    {
        _searchableChoices[box] = choices;
        box.ItemsSource = choices;
        FitComboToContent(box, choices);
        box.PropertyChanged += (_, args) =>
        {
            if (args.Property != ComboBox.TextProperty || _suppressEditorEvents || _updatingSearchableCombo) return;
            if (box.SelectedItem is ChoiceRow selected && string.Equals(box.Text, selected.ToString(), StringComparison.Ordinal))
            {
                changed();
                return;
            }
            if (_searchableChoices.TryGetValue(box, out var choices) && FindExactChoice(choices, box.Text) is not null)
            {
                changed();
                return;
            }
            FilterSearchableCombo(box, box.Text);
            changed();
        };
    }

    private void OnSearchableComboDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox box && _searchableChoices.TryGetValue(box, out var choices))
            ResetSearchableComboItems(box, choices, preserveSelection: true);
    }

    private void FilterSearchableCombo(ComboBox box, string? text)
    {
        if (!_searchableChoices.TryGetValue(box, out var choices)) return;
        var term = text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrWhiteSpace(term)
            ? choices
            : choices.Where(choice => MatchesChoice(choice, term)).ToArray();
        ResetSearchableComboItems(box, filtered, preserveSelection: true);
        if (!box.IsDropDownOpen) box.IsDropDownOpen = true;
    }

    private void ResetSearchableComboItems(ComboBox box, IEnumerable<ChoiceRow> choices, bool preserveSelection = false)
    {
        _updatingSearchableCombo = true;
        try
        {
            var selectedId = preserveSelection && box.SelectedItem is ChoiceRow selected ? selected.Id : (int?)null;
            var rows = choices as IReadOnlyList<ChoiceRow> ?? choices.ToArray();
            box.ItemsSource = rows;
            if (selectedId is not null)
                box.SelectedItem = rows.FirstOrDefault(c => c.Id == selectedId.Value);
        }
        finally
        {
            _updatingSearchableCombo = false;
        }
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
            ConnectionStatusText.Text = $"上次连接成功。游戏={bridge.GameCode()} 队伍=0x{baseAddr:X8}";
            Log($"已从 0x{baseAddr:X8} 刷新队伍。");
        });
    }

    private void LoadPartyRows(MgbaBridgeClient bridge, uint baseAddr, int selectSlot)
    {
        var rows = new List<PartySlotRow>();
        for (var slot = 1; slot <= Gen3Constants.PartySlots; slot++)
        {
            var addr = SlotAddress(baseAddr, slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
            rows.Add(ToRow(slot, addr, mon));
        }
        _partyRows.Clear();
        foreach (var row in rows) _partyRows.Add(row);
        PartyList.SelectedIndex = Math.Clamp(selectSlot - 1, 0, _partyRows.Count - 1);
    }

    private void OnPartySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PartyList.SelectedItem is not PartySlotRow row) return;
        FillEditor(row);
    }

    private async void OnHealClicked(object? sender, RoutedEventArgs e)
    {
        if (!WritesEnabled() || SelectedRow() is not { } row) return;
        await RunUiTask("恢复选中宝可梦", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var addr = SlotAddress(baseAddr, row.Slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
            var info = mon.GetInfo();
            mon.SetUnencrypted(info.MaxHp, null, 0, null);
            WriteMon(bridge, addr, mon);
            SetWriteNotice($"队伍槽位 {row.Slot} 恢复成功：HP {info.Hp}->{info.MaxHp}，状态已清除。");
        });
        await ReloadPartyAsync(row.Slot);
    }

    private async void OnApplyBasicClicked(object? sender, RoutedEventArgs e)
    {
        if (!WritesEnabled() || SelectedRow() is not { } row) return;
        await RunUiTask("写入基础信息", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var addr = SlotAddress(baseAddr, row.Slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
            var nature = SelectedChoiceId(NatureBox);
            if (nature is not null) mon.SetNature(nature.Value);
            mon.SetShiny(ShinyBox.IsChecked == true);
            var abilitySlot = SelectedAbilitySlot();
            if (abilitySlot is not null) mon.SetAbilitySlot(abilitySlot.Value);
            mon.SetGrowth(ParseSpeciesOrNull(SpeciesBox), ParseItemOrNull(ItemBox), ParseUIntOrNull(ExpBox.Text), ParseByteOrNull(FriendshipBox.Text), null);
            mon.SetUnencrypted(null, ParseUShortOrNull(MaxHpBox.Text), SelectedStatus(), ParseByteOrNull(LevelBox.Text));
            WriteMon(bridge, addr, mon);
            SetWriteNotice($"队伍槽位 {row.Slot} 基础信息写入成功：种类/道具/经验/亲密度/等级/性格/特性/闪光/状态已更新。");
        });
        await ReloadPartyAsync(row.Slot);
    }

    private async void OnApplyPokemonClicked(object? sender, RoutedEventArgs e)
    {
        if (!WritesEnabled() || SelectedRow() is not { } row) return;
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
            var addr = SlotAddress(baseAddr, row.Slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));

            if (SelectedChoiceId(NatureBox) is { } nature) mon.SetNature(nature);
            mon.SetShiny(ShinyBox.IsChecked == true);
            if (SelectedAbilitySlot() is { } abilitySlot) mon.SetAbilitySlot(abilitySlot);
            mon.SetGrowth(
                ParseSpeciesOrNull(SpeciesBox),
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
        await ReloadPartyAsync(row.Slot);
    }

    private async void OnApplyMovesClicked(object? sender, RoutedEventArgs e)
    {
        if (!WritesEnabled() || SelectedRow() is not { } row) return;
        await RunUiTask("写入招式", () =>
        {
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var addr = SlotAddress(baseAddr, row.Slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
            mon.SetMoves(
                [ParseMoveOrNull(Move1Box), ParseMoveOrNull(Move2Box), ParseMoveOrNull(Move3Box), ParseMoveOrNull(Move4Box)],
                [ParseByteOrNull(Pp1Box.Text), ParseByteOrNull(Pp2Box.Text), ParseByteOrNull(Pp3Box.Text), ParseByteOrNull(Pp4Box.Text)]);
            mon.SetGrowth(ppBonuses: BuildPpBonuses(PpUp1Box, PpUp2Box, PpUp3Box, PpUp4Box));
            WriteMon(bridge, addr, mon);
            SetWriteNotice($"队伍槽位 {row.Slot} 招式写入成功：4 个招式、PP 和 PP提升已更新。");
        });
        await ReloadPartyAsync(row.Slot);
    }

    private async void OnApplyEvsClicked(object? sender, RoutedEventArgs e)
    {
        if (!WritesEnabled() || SelectedRow() is not { } row) return;
        if (!TryReadCurrentEvTotal("写入努力值", out var pendingEvTotal)) return;
        if (!await ConfirmHighEvTotalAsync(pendingEvTotal)) return;
        await RunUiTask("写入努力值", () =>
        {
            var values = BuildByteStats(EvHpBox, EvAtkBox, EvDefBox, EvSpeBox, EvSpaBox, EvSpdBox);
            var evTotal = values.Values.Sum(x => x);
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var addr = SlotAddress(baseAddr, row.Slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
            mon.SetEvs(values);
            RecalculateLiveStats(mon);
            WriteMon(bridge, addr, mon);
            SetWriteNotice(
                evTotal > 510
                    ? $"队伍槽位 {row.Slot} 努力值写入成功：当前能力已重新计算。警告：努力值总和已超过限制，可能发生未知错误或坏档。"
                    : $"队伍槽位 {row.Slot} 努力值写入成功：当前能力已重新计算。",
                success: evTotal <= 510);
        });
        await ReloadPartyAsync(row.Slot);
    }

    private async void OnApplyIvsClicked(object? sender, RoutedEventArgs e)
    {
        if (!WritesEnabled() || SelectedRow() is not { } row) return;
        await RunUiTask("写入个体值", () =>
        {
            var values = BuildIntStats(IvHpBox, IvAtkBox, IvDefBox, IvSpeBox, IvSpaBox, IvSpdBox);
            foreach (var value in values.Values)
            {
                if (value is < 0 or > 31) throw new InvalidOperationException("个体值必须在 0..31 之间。");
            }
            using var bridge = ConnectBridge();
            var baseAddr = ResolvePartyBase(bridge, forceRefresh: true);
            var addr = SlotAddress(baseAddr, row.Slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
            mon.SetIvs(values);
            RecalculateLiveStats(mon);
            WriteMon(bridge, addr, mon);
            SetWriteNotice($"队伍槽位 {row.Slot} 个体值写入成功：当前能力已重新计算。");
        });
        await ReloadPartyAsync(row.Slot);
    }

    private async void OnApplyIvsEvsClicked(object? sender, RoutedEventArgs e)
    {
        if (!WritesEnabled() || SelectedRow() is not { } row) return;
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
            var addr = SlotAddress(baseAddr, row.Slot);
            var mon = new PartyPokemon(bridge.Read(addr, Gen3Constants.PartyMonSize));
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
        await ReloadPartyAsync(row.Slot);
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

        var rows = new List<(int Pocket, ComboBox ItemBox, TextBox QuantityBox, ChoiceRow[] Choices)>();
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
            var itemBox = new ComboBox
            {
                IsEditable = true,
                IsTextSearchEnabled = false,
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
        if (!WritesEnabled()) return;
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
        if (!WritesEnabled()) return;
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
        BoxAddressText.Text = $"0x{row.Address:X8}";
        SetChoice(BoxSpeciesBox, _speciesChoices, info.Species);
        SetChoice(BoxItemBox, _itemChoices, info.Item);
        SetChoice(BoxNatureBox, _natureChoices, (int)(info.Pid % 25));
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

    private async void OnBoxApplyClicked(object? sender, RoutedEventArgs e)
    {
        if (!WritesEnabled()) return;
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
            EnsureBoxSelectionFresh(bridge);
            var mon = new BoxPokemon(bridge.Read(row.Address, BoxPokemon.Size));
            var species = ParseSpeciesRequired(BoxSpeciesBox, "箱子宝可梦");
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
            BoxList.SelectedItem = _boxRows.FirstOrDefault(r => r.Address == row.Address) ?? BoxList.SelectedItem;
        });
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

    private async Task RunUiTask(string label, Action action)
    {
        try
        {
            SetBusy(label + "...");
            await Task.Yield();
            action();
            SetReady(label + "完成。");
        }
        catch (Exception ex)
        {
            SetReady(label + "失败。");
            ShowToast($"{label}失败：{ex.Message}", success: false);
            if (label.Contains("写入", StringComparison.Ordinal) ||
                label.Contains("添加", StringComparison.Ordinal) ||
                label.Contains("恢复", StringComparison.Ordinal))
            {
                SetWriteNotice($"{label}失败：{ex.Message}", success: false);
            }
            Log("错误：" + ex.Message);
        }
    }

    private MgbaBridgeClient ConnectBridge()
    {
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
        if (mon.IsEmpty) return new PartySlotRow(slot, addr, mon, null, $"{slot} 空槽", "");
        var info = mon.GetInfo();
        var ok = info.Checksum == info.CalculatedChecksum ? "正常" : "异常";
        var title = $"{slot} {(mon.IsShiny ? "★" : "")}{SpeciesName(info.Species)} Lv{info.Level}";
        var item = info.Item == 0 ? "无" : ItemName(info.Item);
        var shiny = mon.IsShiny ? "闪光" : "非闪";
        var detail = $"HP {info.Hp}/{info.MaxHp}  {shiny}  性格 {NatureDisplays[info.Pid % 25]}  特性 {AbilityText(info.Species, info.Ivs["ability"])}  携带 {item}  校验 {ok}";
        return new PartySlotRow(slot, addr, mon, info, title, detail);
    }

    private void FillEditor(PartySlotRow row)
    {
        SelectedTitleText.Text = row.Info is { } headerInfo
            ? $"{SpeciesName(headerInfo.Species)} Lv{headerInfo.Level}"
            : row.Title;
        SelectedDetailText.Text = string.Empty;
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
            NatureBox.SelectedItem = _natureChoices[(int)(info.Pid % 25)];
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
            NatureBox.SelectedItem = null;
            StatusBox.SelectedItem = null;
            ShinyBox.IsChecked = false;
            AbilityBox.ItemsSource = null;
            foreach (var box in EditorBoxes()) box.Text = string.Empty;
            ClearStatTexts();
        }
        finally
        {
            _suppressEditorEvents = false;
        }
        BasicNameText.Text = "中文名、性格、特性会显示在这里。";
        MoveNameText.Text = "招式中文名会显示在这里。";
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
        var run = BoxScanner.LocateBestRun(ewram) ?? throw new InvalidOperationException("没有定位到箱子宝可梦。请先确保箱子里有连续的非空槽位。");
        _boxBase = run.StartAddress;
        _boxRows.Clear();
        for (var i = 0; i < run.Candidates.Count; i++)
        {
            var address = run.Candidates[i].Address;
            var offset = checked((int)(address - PartyScanner.EwramBase));
            var mon = new BoxPokemon(ewram.AsSpan(offset, BoxPokemon.Size));
            var info = mon.GetInfo();
            var slotInBox = (i % BoxScanner.BoxSlots) + 1;
            var boxNo = (i / BoxScanner.BoxSlots) + 1;
            var title = $"箱{boxNo:00}-{slotInBox:00} {(mon.IsShiny ? "★" : "")}{SpeciesName(info.Species)}";
            if (info.Item != 0) title += $" / {ItemName(info.Item)}";
            _boxRows.Add(new BoxSlotRow(i + 1, address, mon, info, title, $"地址 0x{address:X8}"));
        }

        if (_boxRows.Count > 0) BoxList.SelectedIndex = 0;
        Log($"已定位箱子候选：起始 0x{run.StartAddress:X8}，连续非空 {_boxRows.Count} 槽。");
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
        var nature = NatureDisplays[info.Pid % 25];
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
            RefreshAbilityChoices(species, SelectedAbilitySlot());
            UpdateBaseStatTexts(species);
            var nature = SelectedChoiceId(NatureBox) is { } n ? NatureDisplays[n] : "未选择";
            var ability = AbilityBox.SelectedItem?.ToString() ?? "未选择";
            BasicNameText.Text = $"种类：{(species == 0 ? "无" : SpeciesName(species))}  携带道具：{ItemName(item)}  性格：{nature}  特性：{ability}  闪光：{(ShinyBox.IsChecked == true ? "是" : "否")}";

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
                               $"性格：{(SelectedChoiceId(BoxNatureBox) is { } n ? NatureDisplays[n] : "未选择")}  " +
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

    private void SetChoice(ComboBox box, IReadOnlyList<ChoiceRow> choices, int id)
    {
        if (_searchableChoices.ContainsKey(box))
            ResetSearchableComboItems(box, choices);
        var choice = choices.FirstOrDefault(c => c.Id == id);
        box.SelectedItem = choice;
        if (box.IsEditable)
            box.Text = choice?.ToString() ?? string.Empty;
    }

    private static ushort? ParseChoiceUShortOrNull(ComboBox box, IReadOnlyList<ChoiceRow> choices)
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

    private static ushort ParseChoiceUShortRequired(ComboBox box, IReadOnlyList<ChoiceRow> choices, string label)
        => ParseChoiceUShortOrNull(box, choices) ?? throw new InvalidOperationException($"缺少 {label}。");

    private ushort? ParseBoundedChoiceOrNull(ComboBox box, IReadOnlyList<ChoiceRow> choices, int maxValue, string label)
    {
        var value = ParseChoiceUShortOrNull(box, choices);
        if (value is not null && value.Value > maxValue)
            throw new InvalidOperationException($"{label} ID 必须在 0..{maxValue} 范围内。");
        return value;
    }

    private ushort ParseBoundedChoiceRequired(ComboBox box, IReadOnlyList<ChoiceRow> choices, int maxValue, string label)
        => ParseBoundedChoiceOrNull(box, choices, maxValue, label) ?? throw new InvalidOperationException($"缺少 {label}。");

    private ushort? ParseSpeciesOrNull(ComboBox box)
        => ParseBoundedChoiceOrNull(box, _speciesChoices, MaxSpeciesId(), "宝可梦");

    private ushort ParseSpeciesRequired(ComboBox box, string label)
        => ParseBoundedChoiceRequired(box, _speciesChoices, MaxSpeciesId(), label);

    private ushort? ParseItemOrNull(ComboBox box)
        => ParseBoundedChoiceOrNull(box, _itemChoices, MaxItemId(), "道具");

    private ushort ParseItemRequired(ComboBox box, string label)
        => ParseBoundedChoiceRequired(box, _itemChoices, MaxItemId(), label);

    private ushort? ParseMoveOrNull(ComboBox box)
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
            UpdateBoxCurrentStats(info.Species, info.Exp, info.Pid);
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
            UpdateBoxCurrentStats(species, exp, (uint)nature);
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

    private void UpdateBoxCurrentStats(int species, uint exp, uint pidOrNature)
    {
        if (species <= 0)
        {
            ClearBoxCurrentStatTexts();
            return;
        }

        var stats = ReadSpeciesStats(species);
        var level = LevelFromExp(exp, stats.GrowthRate);
        var naturePid = pidOrNature;
        var ivs = BuildIntStats(BoxIvHpBox, BoxIvAtkBox, BoxIvDefBox, BoxIvSpeBox, BoxIvSpaBox, BoxIvSpdBox);
        var evs = BuildByteStats(BoxEvHpBox, BoxEvAtkBox, BoxEvDefBox, BoxEvSpeBox, BoxEvSpaBox, BoxEvSpdBox);
        BoxCurrentHpStatText.Text = CalculateHpDisplay(stats.Hp, ivs.GetValueOrDefault("hp"), evs.GetValueOrDefault("hp"), level).ToString();
        BoxCurrentAtkStatText.Text = CalculateOtherDisplay(stats.Attack, ivs.GetValueOrDefault("atk"), evs.GetValueOrDefault("atk"), level, naturePid, 0).ToString();
        BoxCurrentDefStatText.Text = CalculateOtherDisplay(stats.Defense, ivs.GetValueOrDefault("def"), evs.GetValueOrDefault("def"), level, naturePid, 1).ToString();
        BoxCurrentSpeStatText.Text = CalculateOtherDisplay(stats.Speed, ivs.GetValueOrDefault("spe"), evs.GetValueOrDefault("spe"), level, naturePid, 2).ToString();
        BoxCurrentSpaStatText.Text = CalculateOtherDisplay(stats.SpAttack, ivs.GetValueOrDefault("spa"), evs.GetValueOrDefault("spa"), level, naturePid, 3).ToString();
        BoxCurrentSpdStatText.Text = CalculateOtherDisplay(stats.SpDefense, ivs.GetValueOrDefault("spd"), evs.GetValueOrDefault("spd"), level, naturePid, 4).ToString();
    }

    private static int LevelFromExp(uint exp, byte growthRate)
    {
        var level = 1;
        while (level < 100 && ExperienceForLevel(level + 1, growthRate) <= exp)
            level++;
        return level;
    }

    private static uint ExperienceForLevel(int level, byte growthRate)
    {
        var n = Math.Clamp(level, 1, 100);
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

    private static int CalculateOtherDisplay(int baseStat, int iv, int ev, int level, uint pid, int statIndex)
    {
        var value = ((((2 * baseStat + iv + ev / 4) * level) / 100) + 5);
        var nature = (int)(pid % 25);
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

    private SpeciesStatsReader SpeciesReader(string path)
    {
        if (_speciesReader is null || _speciesReaderPath != path)
        {
            _speciesReader = new SpeciesStatsReader(path);
            _speciesReaderPath = path;
        }
        return _speciesReader;
    }

    private ItemDataReader ItemReader(string path)
    {
        if (_itemReader is null || _itemReaderPath != path)
        {
            _itemReader = new ItemDataReader(path);
            _itemReaderPath = path;
        }
        return _itemReader;
    }

    private MoveDataReader MoveReader(string path)
    {
        if (_moveReader is null || _moveReaderPath != path)
        {
            _moveReader = new MoveDataReader(path);
            _moveReaderPath = path;
        }
        return _moveReader;
    }

    private SpeciesStats ReadSpeciesStats(int species)
    {
        Exception? romError = null;
        if (TryExistingRomPath(out var path))
        {
            try
            {
                return SpeciesReader(path).Read(species);
            }
            catch (Exception ex)
            {
                romError = ex;
            }
        }

        if (TryReadEmbeddedSpeciesStats(species, out var stats))
            return stats;

        throw MissingEmbeddedDataException("宝可梦种族值", species, romError);
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

    private ItemData ReadItemData(int item)
    {
        Exception? romError = null;
        if (TryExistingRomPath(out var path))
        {
            try
            {
                return ItemReader(path).Read(item);
            }
            catch (Exception ex)
            {
                romError = ex;
            }
        }

        if (TryReadEmbeddedItemData(item, out var data))
            return data;

        throw MissingEmbeddedDataException("道具数据", item, romError);
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
        Exception? romError = null;
        if (TryExistingRomPath(out var path))
        {
            try
            {
                return MoveReader(path).Read(move);
            }
            catch (Exception ex)
            {
                romError = ex;
            }
        }

        if (TryReadEmbeddedMoveData(move, out var data))
            return data;

        throw MissingEmbeddedDataException("招式数据", move, romError);
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

    private void UpdateMaxPpTexts(IReadOnlyList<ComboBox> moveBoxes, IReadOnlyList<ComboBox> ppBonusBoxes, IReadOnlyList<TextBlock> targets)
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
        => SetPpBonusChoices(NormalizePpBonusesForDisplay(packed, moves, currentPp), boxes);

    private byte NormalizePpBonusesForDisplay(byte packed, IReadOnlyList<ushort> moves, IReadOnlyList<byte> currentPp)
    {
        if (packed != 0xFF) return packed;

        // This ROM often stores 0xFF in the PP bonus byte for untouched mons.
        // Treat it as "no PP Up" when current PP never exceeds the move's base PP.
        for (var i = 0; i < Math.Min(4, Math.Min(moves.Count, currentPp.Count)); i++)
        {
            var move = moves[i];
            if (move == 0) continue;
            try
            {
                if (currentPp[i] > ReadMoveData(move).Pp)
                    return packed;
            }
            catch
            {
                return packed;
            }
        }

        return 0;
    }

    private bool WritesEnabled() => true;

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

    private bool TryExistingRomPath(out string path)
    {
        path = RomPathBox.Text?.Trim() ?? string.Empty;
        return path.Length > 0 && File.Exists(path);
    }

    private static InvalidOperationException MissingEmbeddedDataException(string label, int id, Exception? romError)
    {
        var message = $"内置{label}缺少 ID {id}";
        if (romError is not null)
            message += $"，ROM 读取也失败：{romError.Message}";
        else
            message += "，且当前未设置可用 ROM 路径。";
        return new InvalidOperationException(message, romError);
    }

    private string ItemName(int item)
    {
        if (item == 0) return "无";
        if (MachineMoveName(item) is { } machineName) return machineName;
        return KnownName("items", item, "未知道具");
    }

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
        var romPath = RomPathBox.Text?.Trim();
        if (!string.IsNullOrEmpty(romPath) && File.Exists(romPath))
        {
            if (_machineMoveIds is null || !string.Equals(_machineMoveReaderPath, romPath, StringComparison.Ordinal))
            {
                _machineMoveIds = ReadMachineMoveTable(romPath);
                _machineMoveReaderPath = romPath;
            }

            if (machineIndex >= 0 && machineIndex < _machineMoveIds.Length)
                return _machineMoveIds[machineIndex];
        }

        if (_db.Table("machine_moves").TryGetValue(machineIndex, out var embedded) && int.TryParse(embedded, out var moveId))
            return moveId;

        return machineIndex >= 0 && machineIndex < Gen3TmMoveIds.Length
            ? Gen3TmMoveIds[machineIndex]
            : 0;
    }

    private static ushort[] ReadMachineMoveTable(string romPath)
    {
        var count = MachineTmCount + MachineHmCount;
        var result = new ushort[count];
        using var stream = File.OpenRead(romPath);
        if (stream.Length < MachineMoveTableOffset + count * 2) return result;
        stream.Position = MachineMoveTableOffset;
        Span<byte> bytes = stackalloc byte[count * 2];
        if (stream.Read(bytes) != bytes.Length) return result;
        for (var i = 0; i < result.Length; i++)
            result[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return result;
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

    private void EnsureBoxSelectionFresh(MgbaBridgeClient bridge)
    {
        if (_boxBase is null) return;
        var ewram = PartyScanner.ReadEwram(bridge);
        var run = BoxScanner.LocateBestRun(ewram) ?? throw new InvalidOperationException("没有定位到箱子宝可梦。请重新读取箱子后再写入。");
        if (run.StartAddress == _boxBase.Value) return;
        LoadBoxRows(bridge);
        throw new InvalidOperationException("箱子位置已变化，已刷新箱子列表。请重新选择槽位后再写入。");
    }

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

    private static uint SlotAddress(uint baseAddr, int slot) => baseAddr + (uint)((slot - 1) * Gen3Constants.PartyMonSize);

    private static string RootDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, string.Concat(Enumerable.Repeat("../", i))));
            if (Directory.Exists(Path.Combine(candidate, "modifier_db"))) return candidate;
        }
        return "/Users/bombermanll/Downloads/mgba_bridge_prototype_2026-06-06";
    }

    private static string DefaultRom()
        => string.Empty;


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
        Log(message);
    }

    private void SetReady(string message)
    {
        ConnectionStatusText.Text = message;
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
