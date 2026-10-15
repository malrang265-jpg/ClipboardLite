using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

using Forms = System.Windows.Forms;

namespace ClipboardLite;

public partial class MainWindow : Window
{
    // =========================================================
    // Windows 메시지
    // =========================================================

    private const int WM_HOTKEY = 0x0312;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    // 단축키 액션별 ID (RegisterHotKey/WM_HOTKEY 구분용)
    private const int HOTKEY_ID_SHOW = 1001;
    private const int HOTKEY_ID_HIDE = 1002;
    private const int HOTKEY_ID_TAB_PREV = 1003;
    private const int HOTKEY_ID_TAB_NEXT = 1004;
    private const int HOTKEY_ID_MULTISELECT = 1005;
    private const int HOTKEY_ID_ESCAPE = 1006; // 창이 보이는 동안에만 등록/해제되는 전역 Esc
    private const uint MOD_NOREPEAT = 0x4000;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    // 마우스를 단축키로 쓰기 위한 저수준 후크 상수
    private const int WH_MOUSE_LL = 14;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_MOUSEHWHEEL = 0x020E;

    // GetAncestor 플래그: 최상위(루트) 창
    private const uint GA_ROOT = 2;

    // 같은 틸트 한 번이 후크와 창 메시지로 둘 다 들어와도 탭이 두 칸 넘어가지
    // 않도록, 같은 방향 틸트를 이 시간(ms) 안에 또 받으면 한 번으로 본다.
    private const long TabTiltDedupeMs = 30;

    // 단축키로 쓸 수 있는 마우스 트리거 종류 (Key 필드에 이 값을 저장한다.
    // RegisterHotKey의 가상키 코드와 겹치지 않도록 큰 수를 쓴다)
    internal const uint MOUSE_TRIGGER_BASE = 0xF000;
    internal const uint MOUSE_MIDDLE = MOUSE_TRIGGER_BASE + 1;      // 휠 클릭(가운데 버튼)
    internal const uint MOUSE_XBUTTON1 = MOUSE_TRIGGER_BASE + 2;    // 뒤로가기 버튼
    internal const uint MOUSE_XBUTTON2 = MOUSE_TRIGGER_BASE + 3;    // 앞으로가기 버튼
    internal const uint MOUSE_WHEEL_TILT_LEFT = MOUSE_TRIGGER_BASE + 4;  // 휠 왼쪽 틸트
    internal const uint MOUSE_WHEEL_TILT_RIGHT = MOUSE_TRIGGER_BASE + 5; // 휠 오른쪽 틸트

    private const int MaxClipboardItems = 100;

    // =========================================================
    // 전체 클립보드
    // =========================================================

    public ObservableCollection<string> ClipboardItems { get; }
        = new();

    // =========================================================
    // 사용자 탭
    // =========================================================

    public ObservableCollection<ClipboardTab> Tabs { get; }
        = new();

    // =========================================================
    // 현재 선택된 탭
    // =========================================================

    private ClipboardTab? _currentTab;

    public ObservableCollection<string> CurrentItems
    {
        get
        {
            if (_currentTab == null)
            {
                return ClipboardItems;
            }

            return _currentTab.Items;
        }
    }

    // =========================================================
    // 기본 탭
    // =========================================================

    private string _defaultTab = "기록";

    private const string RecordTabId = "__RECORD__";
    public ClipboardTab RecordTab { get; } = new ClipboardTab { Id = RecordTabId, Name = "기록", IsRecord = true };
    public ObservableCollection<ClipboardTab> DisplayTabs { get; } = new();
    private int _tabPageStart;
    private System.Windows.Point _dragStartPoint;
    private ClipboardTab? _dragTab;
    private string? _dragText;
    private bool _isDraggingTab;
    private bool _isDraggingTile;
    public double TileWidth { get; private set; } = 220;
    public double TileHeight { get; private set; } = 110;
    public int TileColumns { get; private set; } = 3;
    public int TileRows { get; private set; } = 3;

    // 탭 폭: 타일 폭보다 살짝 좁게. 타일 폭 설정이 바뀌면 함께 따라간다.
    public double TabWidth => Math.Max(56, TileWidth - 24);
    public double TabHeight { get; private set; } = 22;

    public double TileFontSize { get; private set; } = 14;
    public string TileFontFamily { get; private set; } = "Segoe UI";
    public string WindowBackground { get; private set; } = "#F5F5F5";
    public string TileBackground { get; private set; } = "#FFFFFF";
    public string TileForeground { get; private set; } = "#222222";
    public string TabBackground { get; private set; } = "#E1E1E1";
    public string TabForeground { get; private set; } = "#333333";
    public string SelectedTabBackground { get; private set; } = "#FFFFFF";
    public string TabArrowColor { get; private set; } = "#C0392B";

    // =========================================================
    // 단축키 (Key가 0이면 미설정. IsMouse가 true면 Mod는 쓰지 않고
    // Key에는 MOUSE_* 상수가 들어간다)
    // =========================================================

    public uint HotkeyShowMod { get; private set; } = MOD_CONTROL | MOD_SHIFT;
    public uint HotkeyShowKey { get; private set; } = (uint)Forms.Keys.V;
    public bool HotkeyShowIsMouse { get; private set; }
    public uint HotkeyHideMod { get; private set; }
    public uint HotkeyHideKey { get; private set; }
    public bool HotkeyHideIsMouse { get; private set; }
    public uint HotkeyTabPrevMod { get; private set; }
    public uint HotkeyTabPrevKey { get; private set; }
    public bool HotkeyTabPrevIsMouse { get; private set; }
    public uint HotkeyTabNextMod { get; private set; }
    public uint HotkeyTabNextKey { get; private set; }
    public bool HotkeyTabNextIsMouse { get; private set; }
    public uint HotkeyMultiSelectMod { get; private set; }
    public uint HotkeyMultiSelectKey { get; private set; }
    public bool HotkeyMultiSelectIsMouse { get; private set; }

    // =========================================================
    // 여러 문구 선택 모드
    // =========================================================

    private bool _isMultiSelectMode;
    private readonly HashSet<string> _selectedTexts = new();
    internal bool IsMultiSelectMode => _isMultiSelectMode;
    internal bool IsTextSelected(string text) => _selectedTexts.Contains(text);

    // =========================================================
    // 일정 시간 후 자동 숨김
    // =========================================================

    public bool AutoHideEnabled { get; private set; }
    public bool KeepOpenOnClick { get; private set; }
    public double AutoHideSeconds { get; private set; } = 30;
    private DispatcherTimer? _autoHideTimer;

    // 트레이/단축키로 창을 여는 직후 자동 숨김 방지
    private DispatcherTimer? _openingGuardTimer;

    // 긴 문구 수정창의 마지막 크기
    private double _editWindowWidth = 1000;
    private double _editWindowHeight = 650;

    // 설정창의 마지막 크기
    private double _settingsWindowWidth = 760;
    private double _settingsWindowHeight = 680;

    // =========================================================
    // 트레이
    // =========================================================

    private Forms.NotifyIcon? _trayIcon;

    // =========================================================
    // Windows 메시지 처리
    // =========================================================

    private HwndSource? _hwndSource;

    private bool _startupInitialized;

    private DispatcherTimer? _hotkeyRetryTimer;

    // 탭을 드래그해서 순서를 바꾸는 중 이전/다음 화살표 위에 머무르면
    // 자동으로 탭 페이지를 넘기기 위한 타이머
    private DispatcherTimer? _tabDragPageTimer;

    // =========================================================
    // 포터블 저장 경로
    // =========================================================

    private readonly string _programFolder;

    private readonly string _dataFolder;

    private readonly string _imagesFolder;

    private readonly string _dataFile;

    // =========================================================
    // 문구별 대표 이미지
    // =========================================================

    private readonly Dictionary<string, string> _imagePaths =
        new(StringComparer.Ordinal);

    // =========================================================
    // 문구별 제목
    // =========================================================

    private readonly Dictionary<string, string> _tileTitles =
        new(StringComparer.Ordinal);

    // =========================================================
    // 자동 숨김 처리
    // =========================================================

    private bool _isClosing;

    private bool _isSelectingClipboard;

    // =========================================================
    // 생성자
    // =========================================================

    public MainWindow()
    {
        InitializeComponent();

        DataContext = this;

        // =====================================================
        // 포터블 경로
        //
        // ClipboardLite.exe가 있는 폴더를 기준으로 한다.
        // =====================================================

        _programFolder =
            AppContext.BaseDirectory;

        _dataFolder =
            Path.Combine(
                _programFolder,
                "Data");

        _imagesFolder =
            Path.Combine(
                _programFolder,
                "Images");

        Directory.CreateDirectory(
            _dataFolder);

        Directory.CreateDirectory(
            _imagesFolder);

        _dataFile =
            Path.Combine(
                _dataFolder,
                "clipboard.json");

        // =====================================================
        // 창 이벤트
        // =====================================================

        Loaded += MainWindow_Loaded;

        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        // 창이 보이는 동안에는(포커스가 다른 창에 가 있어도) Esc로 숨길 수
        // 있도록, 보일 때만 전역 Esc 단축키를 걸었다가 숨겨지면 바로 뗀다.
        IsVisibleChanged += MainWindow_IsVisibleChanged;

        // 예전에는 다른 창을 클릭해 포커스를 잃으면(Deactivated) 자동으로
        // 숨겼지만, 이러면 "1) 프로그램 호출 → 2) 붙여넣을 위치 클릭 →
        // 3) 저장된 문구 클릭"이라는 실제 사용 순서 자체가 불가능해진다
        // (2번 클릭 시점에 이미 숨겨져 버림). 그래서 자동 숨김은 더 이상
        // 하지 않는다. 창을 닫는 수단은 그대로 남아있다: Esc 키,
        // 단축키/트레이 아이콘 재클릭(토글), 문구 클릭 시 붙여넣기 후 숨김.
    }

    // =========================================================
    // 프로그램 시작
    // =========================================================

    private void MainWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        InitializeStartup();
    }

    // 창을 표시하지 않고도 프로그램 초기화를 완료한다.
    internal void InitializeStartup()
    {
        if (_startupInitialized)
        {
            return;
        }

        _startupInitialized = true;

        // 메인 창의 HWND를 표시 전에 먼저 확보하고 메시지 훅을 연결한다.
        // 별도의 HwndSource가 아니라 실제 MainWindow HWND를 사용하므로
        // 첫 번째 Ctrl+Shift+V도 즉시 받을 수 있다.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(hwnd);
        if (source != null)
        {
            _hwndSource = source;
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.AddHook(WndProc);
        }

        // 메시지 루프가 시작되기 전에 등록을 확정해 첫 입력이 누락되지 않게 한다.
        // 단, 사용자가 지정한 단축키 값은 LoadData() 이후에야 알 수 있으므로,
        // 먼저 데이터를 불러온 뒤에 최종 등록을 한다
        // (WarmUpWindowHandle에서 기본값으로 이미 한 번 예비 등록을 해 둔 상태다).
        RegisterClipboardListener();
        CreateTrayIcon();
        InstallMouseHook();

        LoadData();
        InitializeTabs();
        RefreshItems();
        ApplyMainWindowSizeFromTiles();

        // WarmUpWindowHandle의 예열은 문구가 하나도 없는 빈 상태에서
        // 이루어져서, 실제 타일(이미지/텍스트 바인딩 등)을 그리는 코드
        // 경로는 예열되지 않았다. 데이터를 불러온 지금 한 번 더 화면 밖에서
        // 조용히 그려 보면, 사용자가 처음 누르는 순간 실제 문구가 채워진
        // 타일들도 이미 한 차례 준비된 상태라 더 빨리 나타난다.
        try
        {
            var prevLeft = Left; var prevTop = Top;
            Left = -32000; Top = -32000;
            Show();
            UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Hide();
            Left = prevLeft; Top = prevTop;
        }
        catch
        {
            // 예열에 실패해도 실제 동작에는 지장이 없으므로 무시한다.
        }

        for (int i = 0; i < 8 && !TryRegisterGlobalHotkey(); i++)
            Thread.Sleep(25);

        // 위의 초기화 동안 메시지 루프가 멈춰 있었으므로, 그사이 마우스 후크가
        // 시간 초과로 떨어져 나갔을 수 있다. 시작이 끝난 시점에 한 번 새로 건다.
        RefreshMouseHook();
    }

    // =========================================================
    // 기본 사용자 탭 생성
    // =========================================================

    private void InitializeTabs()
    {
        RecordTab.Items = ClipboardItems;
        if (string.IsNullOrWhiteSpace(RecordTab.Name)) RecordTab.Name = "기록";
        foreach (var tab in Tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.Id)) tab.Id = Guid.NewGuid().ToString("N");
            tab.IsRecord = false;
        }
        RebuildDisplayTabs();
    }

    private void RebuildDisplayTabs()
    {
        var existing = DisplayTabs.ToList();
        if (existing.Count == 0)
        {
            DisplayTabs.Add(RecordTab);
            foreach (var tab in Tabs) DisplayTabs.Add(tab);
        }
        else
        {
            foreach (var tab in Tabs)
                if (!existing.Contains(tab))
                    existing.Add(tab);
            if (!existing.Contains(RecordTab))
                existing.Insert(0, RecordTab);
            existing.RemoveAll(t => t != RecordTab && !Tabs.Contains(t));
            DisplayTabs.Clear();
            foreach (var tab in existing) DisplayTabs.Add(tab);
        }
        ClampTabPage();
        UpdateTabSelectionVisuals();
    }

    private void ClampTabPage()
    {
        int page = Math.Max(1, TileColumns);
        int max = Math.Max(0, ((DisplayTabs.Count - 1) / page) * page);
        _tabPageStart = Math.Clamp(_tabPageStart, 0, max);
    }

    public IEnumerable<ClipboardTab> VisibleTabs => DisplayTabs.Skip(_tabPageStart).Take(Math.Max(1, TileColumns));

    // =========================================================
    // 기본 탭 선택
    // =========================================================

    private void SelectDefaultTab()
    {
        if (string.IsNullOrWhiteSpace(_defaultTab)) _defaultTab = RecordTab.Name;
        var target = string.Equals(_defaultTab, RecordTab.Name, StringComparison.OrdinalIgnoreCase)
            ? null
            : FindTab(_defaultTab);
        if (target == null && !string.Equals(_defaultTab, RecordTab.Name, StringComparison.OrdinalIgnoreCase))
            _defaultTab = RecordTab.Name;

        // RefreshItems()는 창 전체의 바인딩을 통째로 다시 계산하는 무거운
        // 작업이다. 창을 열 때마다 (탭이 그대로인 경우가 대부분인데도)
        // 매번 이걸 호출하는 게 타일이 늦게 나타나는 주된 원인이었다.
        // 이전과 같은 탭이면 굳이 다시 계산할 필요가 없으므로 건너뛴다.
        bool changed = !ReferenceEquals(_currentTab, target);
        _currentTab = target;

        // 창을 열 때는 선택된 탭이 들어 있는 탭 페이지가 보이도록 맞춘다.
        // 맞추지 않으면 위쪽 탭 줄은 이전에 넘겨 둔 페이지(처음엔 첫 페이지)
        // 그대로인데 아래 내용만 선택된 탭의 것으로 바뀌어서, 어느 탭이
        // 선택된 건지 알 수 없는 상태로 열리는 경우가 생긴다.
        int selectedIndex = DisplayTabs.IndexOf(target ?? RecordTab);
        if (selectedIndex >= 0)
        {
            int page = Math.Max(1, TileColumns);
            _tabPageStart = (selectedIndex / page) * page;
        }

        UpdateTabSelectionVisuals();
        if (changed)
            RefreshItems();
    }

    // =========================================================
    // 탭 선택 상태 시각 갱신
    // =========================================================

    private void UpdateTabSelectionVisuals()
    {
        foreach (var tab in DisplayTabs)
            tab.IsSelected = (_currentTab == null && tab.IsRecord) || _currentTab == tab;
        if (TabsControl != null)
            TabsControl.ItemsSource = VisibleTabs.ToList();
    }

    // =========================================================
    // 탭 찾기
    // =========================================================

    private ClipboardTab? FindTab(string name)
    {
        if (string.Equals(name, RecordTab.Name, StringComparison.OrdinalIgnoreCase)) return RecordTab;
        return Tabs.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================
    // 화면 갱신
    // =========================================================

    private void RefreshItems()
    {
        DataContext = null;

        DataContext = this;
    }

    // =========================================================
    // 탭 클릭
    // =========================================================

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.Tag is not ClipboardTab tab) return;
        _currentTab = tab.IsRecord ? null : tab;
        _defaultTab = tab.Name;
        int page = Math.Max(1, TileColumns);
        _tabPageStart = (DisplayTabs.IndexOf(tab) / page) * page;
        SaveData();
        UpdateTabSelectionVisuals();
        RefreshItems();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
        e.Handled = true;
    }

    // =========================================================
    // 탭 우클릭
    // =========================================================

    private void TabButton_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.Tag is not ClipboardTab tab) return;
        var menu = new System.Windows.Controls.ContextMenu();

        var add = new System.Windows.Controls.MenuItem { Header = "탭 추가" };
        add.Click += (_, _) => AddTab_Click(sender, e);
        menu.Items.Add(add);

        var rename = new System.Windows.Controls.MenuItem { Header = "탭 이름 변경" };
        rename.Click += (_, _) => RenameTab(tab);
        menu.Items.Add(rename);
        if (!tab.IsRecord)
        {
            var del = new System.Windows.Controls.MenuItem { Header = "탭 삭제" };
            del.Click += (_, _) => DeleteTab(tab);
            menu.Items.Add(del);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    // =========================================================
    // 탭 추가
    // =========================================================

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        var d = new TabNameWindow { Owner = this, Title = "탭 이름", TabName = "" };
        if (d.ShowDialog() != true) return;
        string name = d.TabName.Trim();
        if (!ValidateTabName(name, null)) return;
        var tab = new ClipboardTab { Id = Guid.NewGuid().ToString("N"), Name = name };
        Tabs.Add(tab);
        _currentTab = tab;
        _defaultTab = name;
        RebuildDisplayTabs();
        SaveData();
        RefreshItems();
    }

    private bool ValidateTabName(string name, ClipboardTab? self)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (DisplayTabs.Any(t => t != self && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show("이미 존재하는 탭 이름입니다.", "ClipboardLite", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    // =========================================================
    // 설정 가져오기: 탭 + 문구 복원
    // 이름이 같은 탭이 있으면 문구를 이어붙이고(중복 제외),
    // 없으면 새 탭을 만든다. 반환값은 실제로 추가된 문구 수.
    // =========================================================

    internal int ImportTabItems(List<ExportedTabData> tabs)
    {
        int imported = 0;
        foreach (var t in tabs)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            var existing = Tabs.FirstOrDefault(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new ClipboardTab { Id = Guid.NewGuid().ToString("N"), Name = t.Name };
                Tabs.Add(existing);
            }
            foreach (var item in t.Items)
            {
                if (!string.IsNullOrWhiteSpace(item) && !existing.Items.Contains(item))
                {
                    existing.Items.Add(item);
                    imported++;
                }
            }
        }
        if (imported > 0)
        {
            RebuildDisplayTabs();
            SaveData();
            RefreshItems();
        }
        return imported;
    }

    // 설정 내보내기에서 읽기 전용으로 참조한다.
    internal IReadOnlyDictionary<string, string> ImagePathsSnapshot => _imagePaths;
    internal IReadOnlyDictionary<string, string> TileTitlesSnapshot => _tileTitles;

    // 설정 가져오기: 문구별 대표 이미지 경로/제목을 병합한다.
    // 가져온 값이 있으면 기존 값을 덮어쓴다(같은 문구에 대해 다른
    // 컴퓨터에서 다시 지정한 값이 최신이라고 보는 게 자연스럽기 때문).
    // 이미지는 경로 문자열만 옮겨지고 실제 파일은 같이 옮겨지지 않으므로,
    // 가져온 컴퓨터에 그 경로의 파일이 없으면 자동으로 원문(또는 제목)이
    // 대신 보인다.
    internal void ImportImagePathsAndTitles(Dictionary<string, string>? images, Dictionary<string, string>? titles)
    {
        bool changed = false;
        if (images != null)
            foreach (var kv in images)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    _imagePaths[kv.Key] = kv.Value;
                    changed = true;
                }
        if (titles != null)
            foreach (var kv in titles)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    _tileTitles[kv.Key] = kv.Value;
                    changed = true;
                }
        if (changed)
        {
            SaveData();
            RefreshItems();
        }
    }

    // =========================================================
    // 탭 이름 변경
    // =========================================================

    private void RenameTab(ClipboardTab tab)
    {
        var d = new TabNameWindow { Owner = this, Title = "탭 이름", TabName = tab.Name };
        if (d.ShowDialog() != true) return;
        string name = d.TabName.Trim();
        if (!ValidateTabName(name, tab)) return;
        string old = tab.Name;
        tab.Name = name;
        if (string.Equals(_defaultTab, old, StringComparison.OrdinalIgnoreCase)) _defaultTab = name;
        SaveData();
        RebuildDisplayTabs();
        RefreshItems();
    }

    // =========================================================
    // 탭 삭제
    // =========================================================

    private void DeleteTab(ClipboardTab tab)
    {
        if (tab.IsRecord) return;
        if (System.Windows.MessageBox.Show($"'{tab.Name}' 탭을 삭제할까요?\n\n탭 안의 문구도 함께 삭제됩니다.",
                "ClipboardLite", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        bool current = _currentTab == tab;
        Tabs.Remove(tab);
        if (current) _currentTab = null;
        if (string.Equals(_defaultTab, tab.Name, StringComparison.OrdinalIgnoreCase)) _defaultTab = RecordTab.Name;
        RebuildDisplayTabs();
        SaveData();
        RefreshItems();
    }

    // App.xaml.cs에서 시작 시 호출할 수 있는 호환용 초기화 진입점입니다.
    internal void InitializeHotkeyImmediately()
    {
        InitializeStartup();
    }

    // 창을 실제로 표시하지 않고 HWND만 미리 생성합니다.
    // Show/Hide를 사용하지 않아 시작 시 깜빡임을 만들지 않습니다.
    internal void WarmUpWindowHandle()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(hwnd);
        if (source != null)
        {
            _hwndSource = source;
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.AddHook(WndProc);
        }
        RegisterGlobalHotkey();

        // WPF는 창을 "처음" Show()할 때 렌더링 파이프라인(레이아웃, 첫
        // 프레임 합성 등)을 준비하는 비용이 별도로 든다. 이 예열을 미리
        // 해 두지 않으면 프로그램을 켠 뒤 첫 번째 단축키/트레이 클릭이
        // 눈에 띄게 느리거나 반응이 없어 보여서, 사용자가 여러 번 눌러야
        // 겨우 창이 뜨는 것처럼 느껴진다.
        // 화면 밖 아주 먼 좌표에서 한 번 Show() → 레이아웃 강제 계산 →
        // Hide() 를 해서, 사용자 눈에 보이지 않게 이 비용을 미리 치른다.
        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            Show();
            UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Hide();
        }
        catch
        {
            // 예열에 실패해도 실제 동작에는 지장이 없으므로 무시한다.
        }
    }

    // =========================================================
    // 트레이 아이콘
    // =========================================================

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = GetAppIcon(),
            Text = "ClipboardLite",
            Visible = true
        };
        _trayIcon.MouseClick += TrayIcon_MouseClick;
        _trayIcon.MouseDoubleClick += TrayIcon_MouseDoubleClick;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("설정", null, (_, _) => OpenSettings());
        menu.Items.Add("종료", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
    }

    // csproj의 ApplicationIcon으로 exe에 이미 박혀 있는 아이콘을 그대로
    // 꺼내와 트레이 아이콘으로도 재사용한다. 실행 파일 경로를 못 찾거나
    // 아이콘 추출에 실패하면 시스템 기본 아이콘으로 대신한다.
    private static System.Drawing.Icon GetAppIcon()
    {
        try
        {
            string? exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) return icon;
            }
        }
        catch
        {
            // 무시하고 기본 아이콘으로 대신한다.
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void TrayIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left)
            return;

        ToggleWindow();
    }

    private void TrayIcon_MouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        // Windows 트레이의 더블클릭은 MouseClick도 먼저 발생하므로
        // 여기서는 추가 동작을 하지 않는다.
    }

    // =========================================================
    // 전역 단축키
    // =========================================================

    // 각 단축키 액션의 (ID, 수정자, 키, 실행할 동작)을 한 곳에서 관리한다.
    // 수정자/키 중 하나라도 0이면 "미설정"으로 보고 등록하지 않는다.
    private IEnumerable<(int Id, bool IsMouse, uint Mod, uint Key, Action Run)> GetHotkeyBindings()
    {
        yield return (HOTKEY_ID_SHOW, HotkeyShowIsMouse, HotkeyShowMod, HotkeyShowKey,
            ToggleWindow); // 기존 Ctrl+Shift+V와 동일하게 토글로 동작 (하위 호환)
        yield return (HOTKEY_ID_HIDE, HotkeyHideIsMouse, HotkeyHideMod, HotkeyHideKey,
            () => Dispatcher.Invoke(Hide));
        yield return (HOTKEY_ID_TAB_PREV, HotkeyTabPrevIsMouse, HotkeyTabPrevMod, HotkeyTabPrevKey,
            () => Dispatcher.Invoke(() => SelectAdjacentTab(false)));
        yield return (HOTKEY_ID_TAB_NEXT, HotkeyTabNextIsMouse, HotkeyTabNextMod, HotkeyTabNextKey,
            () => Dispatcher.Invoke(() => SelectAdjacentTab(true)));
        yield return (HOTKEY_ID_MULTISELECT, HotkeyMultiSelectIsMouse, HotkeyMultiSelectMod, HotkeyMultiSelectKey,
            () => Dispatcher.Invoke(() => SetMultiSelectMode(!_isMultiSelectMode)));
    }

    private bool TryRegisterGlobalHotkey()
    {
        if (_hwndSource == null)
            return false;

        bool allOk = true;
        foreach (var (id, isMouse, mod, key, _) in GetHotkeyBindings())
        {
            UnregisterHotKey(_hwndSource.Handle, id);
            if (isMouse) continue; // 마우스 트리거는 RegisterHotKey 대상이 아니다 (저수준 후크가 처리)
            if (key == 0)
                continue; // 미설정 단축키는 등록하지 않는다 (실패로 취급하지 않음).
                          // mod는 0이어도 된다 (수정자 없이 단일 키만 쓰는 것도 허용).
            if (!RegisterHotKey(_hwndSource.Handle, id, mod, key))
                allOk = false;
        }
        return allOk;
    }

    private void RegisterGlobalHotkey()
    {
        if (_hwndSource == null)
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(hwnd);
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource.AddHook(WndProc);
            }
        }

        if (_hwndSource == null)
            return;

        if (TryRegisterGlobalHotkey())
        {
            _hotkeyRetryTimer?.Stop();
            _hotkeyRetryTimer = null;
            return;
        }

        if (_hotkeyRetryTimer == null)
        {
            _hotkeyRetryTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                DispatcherPriority.ApplicationIdle,
                (_, _) => RegisterGlobalHotkey(),
                Dispatcher);
        }
        if (!_hotkeyRetryTimer.IsEnabled)
            _hotkeyRetryTimer.Start();
    }

    // =========================================================
    // 마우스 단축키(휠클릭 / 뒤로·앞으로가기 버튼 / 휠 좌우 틸트)를
    // 감지하기 위한 저수준 마우스 후크.
    // RegisterHotKey는 키보드 전용이라 마우스 버튼은 등록할 수 없어서,
    // 시스템 전체 마우스 입력을 직접 들여다보는 이 방식을 쓴다.
    // 후크 콜백은 아주 가볍게(비교 몇 번) 유지해서 다른 프로그램의
    // 마우스 반응성에 지장이 없도록 한다.
    // =========================================================

    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private LowLevelMouseProc? _mouseHookProc;

    // 설정창에서 마우스 트리거를 캡처하는 동안만 값이 들어간다.
    // 값이 있으면 후크는 정상 실행 대신 이 콜백에만 결과를 전달한다.
    internal Action<uint>? MouseCaptureCallback { get; set; }

    private void InstallMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero) return;
        _mouseHookProc = MouseHookCallback;
        _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        _mouseHookProc = null;
    }

    // 저수준 후크는 UI 스레드가 오래 막혀(시작 직후, 큰 화면 갱신, 붙여넣기 대기 등)
    // 시간 제한을 넘기면 Windows가 아무 알림 없이 후크를 떼어 버린다. 그러면
    // 휠틸트 같은 마우스 단축키가 그 뒤로 조용히 먹통이 된다. 후크가 아직
    // 살아 있는지 물어볼 방법이 없으므로, 창을 보여줄 때마다 새로 걸어 둔다.
    private void RefreshMouseHook()
    {
        if (!_startupInitialized || _isClosing) return;
        UninstallMouseHook();
        InstallMouseHook();
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            uint? trigger = null;

            if (msg == WM_MBUTTONDOWN)
            {
                trigger = MOUSE_MIDDLE;
            }
            else if (msg == WM_XBUTTONDOWN)
            {
                var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT))!;
                int xButton = (int)(data.mouseData >> 16);
                trigger = xButton == 1 ? MOUSE_XBUTTON1 : MOUSE_XBUTTON2;
            }
            else if (msg == WM_MOUSEHWHEEL)
            {
                var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT))!;
                short delta = (short)(data.mouseData >> 16);
                trigger = delta < 0 ? MOUSE_WHEEL_TILT_LEFT : MOUSE_WHEEL_TILT_RIGHT;
            }

            if (trigger.HasValue)
            {
                if (MouseCaptureCallback != null)
                {
                    var cb = MouseCaptureCallback;
                    MouseCaptureCallback = null;
                    uint t = trigger.Value;
                    Dispatcher.BeginInvoke(new Action(() => cb(t)));
                }
                else
                {
                    uint t = trigger.Value;
                    bool isTilt = t == MOUSE_WHEEL_TILT_LEFT || t == MOUSE_WHEEL_TILT_RIGHT;

                    // 휠 틸트는 다른 프로그램에서도 가로 스크롤에 흔히 쓰이므로,
                    // 탭 이동에 연결된 틸트는 "창이 떠 있고 커서가 그 창 위에
                    // 있을 때"만 쓴다. 쓴 틸트는 아래쪽 프로그램으로 새어 나가지
                    // 않게 여기서 삼킨다. (조건이 안 맞으면 건드리지 않고 그대로 넘긴다)
                    if (isTilt && TryHandleTabTilt(t == MOUSE_WHEEL_TILT_RIGHT))
                        return (IntPtr)1;

                    foreach (var (id, isMouse, _, key, run) in GetHotkeyBindings())
                    {
                        if (!isMouse || key != t) continue;

                        // 틸트에 연결된 탭 이동은 위에서 조건을 따져 이미 처리했다.
                        if (isTilt && (id == HOTKEY_ID_TAB_PREV || id == HOTKEY_ID_TAB_NEXT)) continue;

                        Dispatcher.BeginInvoke(run);
                        break;
                    }
                }
            }
        }
        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    // =========================================================
    // 휠 틸트로 탭 이전/다음 이동.
    //
    // 예전에는 틸트가 시스템 어디서 일어나든(창이 숨겨져 있어도, 다른
    // 프로그램의 가로 스크롤에 쓴 틸트까지) 전부 탭 이동으로 처리하고 그때마다
    // "마지막으로 선택한 탭"을 저장했다. 그래서 쓴 적도 없는데 탭이 오른쪽
    // 끝으로 밀려 있다가 그 상태로 저장되어, 다음 실행 때 가장 오른쪽 탭이
    // 보이곤 했다.
    //
    // 이제는 창이 보이고 커서가 창 위에 있을 때만 처리한다. 반대로 창이
    // 활성 상태가 아니어도(다른 프로그램이 앞에 있어도) 커서만 창 위에 있으면 된다.
    //
    // 틸트는 두 갈래로 들어올 수 있다. (1) 저수준 후크 - 보통은 이쪽.
    // (2) 창 메시지 WM_MOUSEHWHEEL - 후크가 입력을 못 받는 상황(예: 앞에 있는
    // 프로그램이 관리자 권한이라 Windows 보안(UIPI)이 일반 권한 프로그램의
    // 후크를 막는 경우)에서도, 세로 휠이 커서 아래 창에 창 메시지로 전달되는
    // 것과 같은 경로로 틸트가 이 창에 도착할 수 있다. 그래서 WndProc에서도 받는다.
    // 어느 쪽으로 들어와도 아래 한 곳에서 처리한다.
    //
    // 처리했으면 true(= 이 틸트는 우리가 썼으니 더 전달하지 않아도 됨),
    // 조건이 안 맞아 건드리지 않았으면 false.
    // =========================================================

    private readonly object _tabTiltLock = new();
    private long _lastTabTiltTick;
    private bool _lastTabTiltRight;

    private bool TryHandleTabTilt(bool tiltRight)
    {
        uint trigger = tiltRight ? MOUSE_WHEEL_TILT_RIGHT : MOUSE_WHEEL_TILT_LEFT;
        bool prevBound = HotkeyTabPrevIsMouse && HotkeyTabPrevKey == trigger;
        bool nextBound = HotkeyTabNextIsMouse && HotkeyTabNextKey == trigger;
        if (!prevBound && !nextBound)
            return false; // 이 방향 틸트에 탭 이동을 연결해 두지 않았다

        if (!IsCursorOverOwnVisibleWindow())
            return false;

        lock (_tabTiltLock)
        {
            long now = Environment.TickCount64;
            if (_lastTabTiltRight == tiltRight && now - _lastTabTiltTick < TabTiltDedupeMs)
                return true; // 같은 입력이 두 경로로 들어온 것: 이미 처리했다

            _lastTabTiltTick = now;
            _lastTabTiltRight = tiltRight;
        }

        // 같은 방향에 이전/다음이 모두 연결돼 있으면 예전과 같이 이전 쪽이 우선한다.
        bool forward = !prevBound;
        Dispatcher.BeginInvoke(new Action(() => SelectAdjacentTab(forward)));
        return true;
    }

    // 이 프로그램의 창이 보이는 상태이고, 마우스 커서가 (다른 창에 가려지지 않은)
    // 그 창 위에 있는지 확인한다. 활성 창인지는 따지지 않는다.
    private bool IsCursorOverOwnVisibleWindow()
    {
        try
        {
            if (!IsVisible || _hwndSource == null)
                return false;

            IntPtr hwnd = _hwndSource.Handle;
            if (hwnd == IntPtr.Zero || !IsWindowEnabled(hwnd))
                return false; // 설정창 같은 모달 창이 떠 있으면 사용 불가 상태다

            if (!GetCursorPos(out POINT cursor))
                return false;

            IntPtr hit = WindowFromPoint(cursor);
            return hit != IntPtr.Zero && GetAncestor(hit, GA_ROOT) == hwnd;
        }
        catch
        {
            return false;
        }
    }

    // =========================================================
    // 클립보드 감시 등록
    // =========================================================

    private void RegisterClipboardListener()
    {
        if (_hwndSource == null)
        {
            RegisterGlobalHotkey();
        }

        if (_hwndSource != null)
        {
            AddClipboardFormatListener(_hwndSource.Handle);
        }
    }

    // =========================================================
    // 창 열기 / 숨기기
    // =========================================================

    private void ToggleWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (IsVisible)
            {
                Hide();
                return;
            }

            // 메인 창 크기는 저장된 창 크기가 아니라
            // 설정한 타일 크기와 가로/세로 타일 수로 결정한다.
            ApplyMainWindowSizeFromTiles();

            // 트레이/단축키로 창을 여는 순간 다른 프로그램이
            // 포그라운드로 잠깐 잡히는 경우를 방지한다.
            _isSelectingClipboard = true;
            _openingGuardTimer?.Stop();
            _openingGuardTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _openingGuardTimer.Tick += OpeningGuardTimer_Tick;
            _openingGuardTimer.Start();

            SelectDefaultTab();
            PositionWindowAtMouse();

            Opacity = 1.0;
            if (KeepOpenOnClick)
            {
                // 기존에 포커스를 가진 프로그램이 그대로 포커스를 유지하게 한다.
                ShowWithoutActivation();
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ApplyMainWindowSizeFromTiles()));
            }
            else
            {
                Show();
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ApplyMainWindowSizeFromTiles()));
                Activate();
                Focus();
            }
        });
    }

    private void ShowWithoutActivation()
    {
        bool prev = ShowActivated;
        ShowActivated = false;
        try { Show(); }
        finally { ShowActivated = prev; }
    }

    // 현재 포그라운드 창이 이 프로그램의 창인지 확인한다.
    private static bool IsOwnWindowForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void OpeningGuardTimer_Tick(object? sender, EventArgs e)
    {
        _openingGuardTimer?.Stop();
        _openingGuardTimer = null;
        _isSelectingClipboard = false;
    }

    // =========================================================
    // 마우스 위치에서 창 열기
    // =========================================================

    private void PositionWindowAtMouse()
    {
        if (!GetCursorPos(
                out POINT cursor))
        {
            WindowStartupLocation =
                WindowStartupLocation.CenterScreen;

            return;
        }

        double windowWidth =
            ActualWidth > 0
                ? ActualWidth
                : Width;

        double windowHeight =
            ActualHeight > 0
                ? ActualHeight
                : Height;

        var source =
            PresentationSource.FromVisual(
                this);

        double dpiX = 1.0;

        double dpiY = 1.0;

        if (source?.CompositionTarget != null)
        {
            dpiX =
                source.CompositionTarget
                    .TransformFromDevice.M11;

            dpiY =
                source.CompositionTarget
                    .TransformFromDevice.M22;
        }

        double mouseX =
            cursor.X * dpiX;

        double mouseY =
            cursor.Y * dpiY;

        double left =
            mouseX + 12;

        double top =
            mouseY + 12;

        var screen =
            Forms.Screen.FromPoint(
                new System.Drawing.Point(
                    cursor.X,
                    cursor.Y));

        var area =
            screen.WorkingArea;

        double screenLeft =
            area.Left * dpiX;

        double screenTop =
            area.Top * dpiY;

        double screenRight =
            area.Right * dpiX;

        double screenBottom =
            area.Bottom * dpiY;

        if (left + windowWidth >
            screenRight)
        {
            left =
                mouseX -
                windowWidth -
                12;
        }

        if (left < screenLeft)
        {
            left =
                screenLeft + 12;
        }

        if (top + windowHeight >
            screenBottom)
        {
            top =
                mouseY -
                windowHeight -
                12;
        }

        if (top < screenTop)
        {
            top =
                screenTop + 12;
        }

        Left = left;

        Top = top;
    }

    // =========================================================
    // 클립보드 변경
    // =========================================================

    private void ClipboardChanged()
    {
        try
        {
            if (!Forms.Clipboard.ContainsText())
            {
                return;
            }

            string text =
                Forms.Clipboard.GetText();

            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            AddClipboardItem(text);
        }
        catch
        {
            // 클립보드가 잠겨 있으면 무시
        }
    }

    // =========================================================
    // 전체 클립보드에 추가
    // =========================================================

    private void AddClipboardItem(
        string text)
    {
        Dispatcher.Invoke(() =>
        {
            ClipboardItems.Remove(text);

            ClipboardItems.Insert(
                0,
                text);

            while (
                ClipboardItems.Count >
                MaxClipboardItems)
            {
                ClipboardItems.RemoveAt(
                    ClipboardItems.Count - 1);
            }

            SaveData();

            if (_currentTab == null)
            {
                RefreshItems();
            }
        });
    }

    // =========================================================
    // 대표 이미지 경로
    // =========================================================

    public string? GetImagePath(string text)
    {
        if (!_imagePaths.TryGetValue(text, out var path))
        {
            return null;
        }

        string fullPath = path;

        if (!Path.IsPathRooted(fullPath))
        {
            fullPath = Path.Combine(_programFolder, fullPath);
        }

        return File.Exists(fullPath)
            ? fullPath
            : null;
    }

    // =========================================================
    // 대표 이미지 설정
    // =========================================================

    private void SetTileImage(string text)
    {
        try
        {
            using var dialog = new Forms.OpenFileDialog
            {
                Title = "대표 이미지 선택",
                InitialDirectory = _imagesFolder,
                Filter =
                    "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" +
                    "모든 파일|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != Forms.DialogResult.OK)
            {
                return;
            }

            string selectedPath = Path.GetFullPath(dialog.FileName);
            string relativePath;

            try
            {
                relativePath =
                    Path.GetRelativePath(
                        _programFolder,
                        selectedPath);
            }
            catch
            {
                relativePath = selectedPath;
            }

            _imagePaths[text] = relativePath;

            SaveData();
            RefreshItems();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"대표 이미지 설정에 실패했습니다.\n\n{ex.Message}",
                "ClipboardLite",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // =========================================================
    // 대표 이미지 제거
    // =========================================================

    private void RemoveTileImage(string text)
    {
        if (_imagePaths.Remove(text))
        {
            SaveData();
            RefreshItems();
        }
    }

    // =========================================================
    // 제목 조회
    // =========================================================

    public string? GetTitle(string text)
    {
        if (_tileTitles.TryGetValue(text, out var title) && !string.IsNullOrWhiteSpace(title))
            return title;
        return null;
    }

    // =========================================================
    // 제목 설정
    // =========================================================

    private void SetTileTitle(string text)
    {
        var d = new TabNameWindow(multiline: false)
        {
            Owner = this,
            Title = "타일 제목",
            TabName = GetTitle(text) ?? ""
        };
        if (d.ShowDialog() != true) return;
        string title = d.TabName.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            _tileTitles.Remove(text);
        }
        else
        {
            _tileTitles[text] = title;
        }
        SaveData();
        RefreshItems();
    }

    // =========================================================
    // 제목 제거
    // =========================================================

    private void RemoveTileTitle(string text)
    {
        if (_tileTitles.Remove(text))
        {
            SaveData();
            RefreshItems();
        }
    }

    // =========================================================
    // 사용자 탭에 문구 추가
    // =========================================================

    private void AddTextToTab(
        string text,
        ClipboardTab tab)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 이미 있으면 앞으로 이동
        tab.Items.Remove(text);

        tab.Items.Insert(
            0,
            text);

        SaveData();

        // 현재 보고 있는 탭이면 갱신
        if (_currentTab == tab)
        {
            RefreshItems();
        }
    }

    // =========================================================
    // 현재 목록에서 문구 삭제
    // =========================================================

    private void DeleteClipboardTile(string text)
    {
        var list = _currentTab?.Items ?? ClipboardItems;
        list.Remove(text);
        _imagePaths.Remove(text);
        _tileTitles.Remove(text);
        SaveData();
        RefreshItems();
    }

    // =========================================================
    // 타일 우클릭
    // =========================================================

    private void ClipboardTile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border b || b.DataContext is not string text) return;
        var menu = new System.Windows.Controls.ContextMenu();
        var edit = new System.Windows.Controls.MenuItem { Header = "문구 수정" };
        edit.Click += (_, _) => EditClipboardTile(text);
        menu.Items.Add(edit);
        var del = new System.Windows.Controls.MenuItem { Header = "문구 삭제" };
        del.Click += (_, _) => DeleteClipboardTile(text);
        menu.Items.Add(del);
        var move = new System.Windows.Controls.MenuItem { Header = "지정한 탭으로 이동" };
        var source = _currentTab ?? RecordTab;
        foreach (var tab in DisplayTabs)
        {
            if (tab == source) continue;
            var mi = new System.Windows.Controls.MenuItem { Header = tab.Name };
            mi.Click += (_, _) => MoveTextToTarget(text, tab);
            move.Items.Add(mi);
        }
        if (move.Items.Count > 0)
            menu.Items.Add(move);
        var img = new System.Windows.Controls.MenuItem { Header = "대표 이미지 설정" };
        img.Click += (_, _) => SetTileImage(text);
        menu.Items.Add(img);
        if (GetImagePath(text) != null)
        {
            var ri = new System.Windows.Controls.MenuItem { Header = "대표 이미지 삭제" };
            ri.Click += (_, _) => RemoveTileImage(text);
            menu.Items.Add(ri);
        }
        var title = new System.Windows.Controls.MenuItem { Header = "제목 설정" };
        title.Click += (_, _) => SetTileTitle(text);
        menu.Items.Add(title);
        if (GetTitle(text) != null)
        {
            var rt = new System.Windows.Controls.MenuItem { Header = "제목 삭제" };
            rt.Click += (_, _) => RemoveTileTitle(text);
            menu.Items.Add(rt);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void MoveTextToTarget(string text, ClipboardTab target)
    {
        var source = _currentTab ?? RecordTab;
        if (source == target) return;
        source.Items.Remove(text);
        target.Items.Remove(text);
        target.Items.Insert(0, text);
        SaveData();
        RefreshItems();
    }

    // =========================================================
    // 여러 문구 선택 모드
    // =========================================================

    private void ToggleTileSelection(string text)
    {
        if (!_selectedTexts.Add(text))
            _selectedTexts.Remove(text);
        RefreshItems();
        UpdateMultiSelectUi();
    }

    private void SetMultiSelectMode(bool on)
    {
        _isMultiSelectMode = on;
        _selectedTexts.Clear();
        UpdateMultiSelectUi();
        RefreshItems();
    }

    private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
    {
        SetMultiSelectMode(!_isMultiSelectMode);
    }

    private void MultiSelectExit_Click(object sender, RoutedEventArgs e)
    {
        SetMultiSelectMode(false);
    }

    private void UpdateMultiSelectUi()
    {
        if (MultiSelectToggleButton != null)
            MultiSelectToggleButton.Foreground = _isMultiSelectMode
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x90, 0xD9));
        if (MultiSelectToggleButton != null)
            MultiSelectToggleButton.Background = _isMultiSelectMode
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x90, 0xD9))
                : System.Windows.Media.Brushes.Transparent;
        if (MultiSelectBar != null)
            MultiSelectBar.Visibility = _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        if (MultiSelectCountText != null)
            MultiSelectCountText.Text = $"{_selectedTexts.Count}개 선택됨";
        bool hasSelection = _selectedTexts.Count > 0;
        if (MultiSelectMoveButton != null) MultiSelectMoveButton.IsEnabled = hasSelection;
        if (MultiSelectDeleteButton != null) MultiSelectDeleteButton.IsEnabled = hasSelection;

        if (MultiSelectAllButton != null)
        {
            var current = CurrentItems;
            bool allSelected = current.Count > 0 && current.All(_selectedTexts.Contains);
            MultiSelectAllButton.Content = allSelected ? "전체 해제" : "전체 선택";
            MultiSelectAllButton.IsEnabled = current.Count > 0;
        }
    }

    private void MultiSelectAll_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentItems;
        bool allSelected = current.Count > 0 && current.All(_selectedTexts.Contains);
        if (allSelected)
        {
            // 이미 전체 선택 상태였다면, 다시 누르면 전체 해제한다.
            _selectedTexts.Clear();
        }
        else
        {
            foreach (var text in current)
                _selectedTexts.Add(text);
        }
        RefreshItems();
        UpdateMultiSelectUi();
    }

    private void MultiSelectDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTexts.Count == 0) return;
        var result = System.Windows.MessageBox.Show(
            $"선택한 {_selectedTexts.Count}개 문구를 삭제할까요?",
            "ClipboardLite", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var list = _currentTab?.Items ?? ClipboardItems;
        foreach (var text in _selectedTexts.ToList())
        {
            list.Remove(text);
            _imagePaths.Remove(text);
            _tileTitles.Remove(text);
        }
        SaveData();
        SetMultiSelectMode(false);
    }

    private void MultiSelectMove_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTexts.Count == 0 || sender is not System.Windows.Controls.Button btn) return;
        var menu = new System.Windows.Controls.ContextMenu();
        var source = _currentTab ?? RecordTab;
        foreach (var tab in DisplayTabs)
        {
            if (tab == source) continue;
            var mi = new System.Windows.Controls.MenuItem { Header = tab.Name };
            mi.Click += (_, _) => MoveSelectedTo(tab);
            menu.Items.Add(mi);
        }
        if (menu.Items.Count == 0) return;
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    private void MoveSelectedTo(ClipboardTab target)
    {
        var source = _currentTab ?? RecordTab;
        if (source == target) return;
        foreach (var text in _selectedTexts.ToList())
        {
            source.Items.Remove(text);
            target.Items.Remove(text);
            target.Items.Insert(0, text);
        }
        SaveData();
        SetMultiSelectMode(false);
    }

    private void EditClipboardTile(string oldText)
    {
        var d = new TabNameWindow(true)
        {
            Owner = this,
            Title = "문구 수정",
            TabName = oldText,
            Width = _editWindowWidth,
            Height = _editWindowHeight
        };

        if (d.ShowDialog() != true)
        {
            _editWindowWidth = d.Width;
            _editWindowHeight = d.Height;
            SaveData();
            return;
        }

        _editWindowWidth = d.Width;
        _editWindowHeight = d.Height;
        SaveData();
        string text = d.TabName;
        if (string.IsNullOrWhiteSpace(text) || text == oldText) return;
        var list = _currentTab?.Items ?? ClipboardItems;
        int i = list.IndexOf(oldText);
        if (i < 0) return;
        list[i] = text;
        if (_imagePaths.TryGetValue(oldText, out var path))
        {
            _imagePaths.Remove(oldText);
            _imagePaths[text] = path;
        }
        if (_tileTitles.TryGetValue(oldText, out var title))
        {
            _tileTitles.Remove(oldText);
            _tileTitles[text] = title;
        }
        SaveData();
        RefreshItems();
    }

    // =========================================================
    // 사용자 탭 → 다른 사용자 탭으로 이동
    // =========================================================

    private void MoveTextToTab(
        string text,
        ClipboardTab sourceTab,
        ClipboardTab targetTab)
    {
        if (sourceTab == targetTab)
        {
            return;
        }

        sourceTab.Items.Remove(text);

        targetTab.Items.Remove(text);
        targetTab.Items.Insert(0, text);

        SaveData();

        if (_currentTab == sourceTab ||
            _currentTab == targetTab)
        {
            RefreshItems();
        }
    }

    private void ClipboardTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b && b.DataContext is string text)
        {
            if (_isMultiSelectMode)
            {
                ToggleTileSelection(text);
                e.Handled = true;
                return;
            }
            _dragStartPoint = e.GetPosition(this);
            _dragText = text;
            _isDraggingTile = false;
            b.CaptureMouse();
        }
    }

    // 드래그 중 커서 아래에서 하이라이트되고 있는 대상 타일
    private System.Windows.Controls.Border? _dragOverTarget;

    // 탭/타일을 드래그로 옮길 때 대상 위에 표시하는 선의 색.
    // "선택하지 않은 탭"의 배경색과 같은 색을 쓴다.
    private System.Windows.Media.Brush DragOverBrush()
    {
        try { return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(TabBackground)!; }
        catch { return System.Windows.Media.Brushes.Gray; }
    }

    private void ClipboardTile_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragText == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_isDraggingTile && (Math.Abs(p.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                                 Math.Abs(p.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            _isDraggingTile = true;
            if (sender is System.Windows.Controls.Border dragBorder)
            {
                // 잡고 있는 타일 자체: 살짝 커지고 그림자가 생겨 "들어올린" 느낌을 준다.
                dragBorder.Opacity = 0.55;
                dragBorder.SetValue(System.Windows.Controls.Panel.ZIndexProperty, 100);
                dragBorder.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                dragBorder.RenderTransform = new System.Windows.Media.ScaleTransform(1.06, 1.06);
                dragBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    Opacity = 0.45,
                    BlurRadius = 14,
                    ShadowDepth = 4
                };
            }
        }

        if (_isDraggingTile)
        {
            var hover = FindTileBorderUnderPoint(e.GetPosition(TileScrollViewer));
            if (!ReferenceEquals(hover, _dragOverTarget))
            {
                ClearDragOverHighlight();
                if (hover != null && hover.DataContext as string != _dragText)
                {
                    hover.BorderBrush = DragOverBrush();
                    hover.BorderThickness = new Thickness(4);
                    _dragOverTarget = hover;
                }
            }
        }
    }

    private void ClearDragOverHighlight()
    {
        if (_dragOverTarget == null) return;
        _dragOverTarget.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);
        _dragOverTarget.ClearValue(System.Windows.Controls.Border.BorderThicknessProperty);
        _dragOverTarget = null;
    }

    private void ClipboardTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
        {
            b.ReleaseMouseCapture();
            b.Opacity = 1.0;
            b.ClearValue(UIElement.RenderTransformProperty);
            b.ClearValue(UIElement.EffectProperty);
            b.ClearValue(System.Windows.Controls.Panel.ZIndexProperty);
        }

        ClearDragOverHighlight();

        if (_dragText != null)
        {
            if (_isDraggingTile)
            {
                // 마우스 캡처 중에는 e.OriginalSource가 계속 잡고 있던 타일
                // 자신을 가리키므로, 실제 커서 좌표로 다시 히트테스트해서
                // 그 아래에 있는 타일을 찾는다.
                string? target = FindTileUnderMouse(e);
                if (target != null && target != _dragText)
                    ReorderTile(_dragText, target);
            }
            else
            {
                // 이동 없이 눌렀다 뗐다면 일반 클릭 → 붙여넣기
                PasteText(_dragText);
            }
            e.Handled = true;
        }

        _dragText = null;
        _isDraggingTile = false;
    }

    // 마우스가 캡처된 상태에서도 실제 커서 아래에 있는 타일을 정확히 찾는다.
    private string? FindTileUnderMouse(MouseButtonEventArgs e)
    {
        if (TileScrollViewer == null) return null;
        return FindTileBorderUnderPoint(e.GetPosition(TileScrollViewer))?.DataContext as string;
    }

    private System.Windows.Controls.Border? FindTileBorderUnderPoint(System.Windows.Point pos)
    {
        if (TileScrollViewer == null) return null;
        var result = System.Windows.Media.VisualTreeHelper.HitTest(TileScrollViewer, pos);
        return FindVisualParent<System.Windows.Controls.Border>(result?.VisualHit);
    }

    private void ReorderTile(string text, string targetText)
    {
        var list = _currentTab?.Items ?? ClipboardItems;
        int a = list.IndexOf(text), b = list.IndexOf(targetText);
        if (a < 0 || b < 0 || a == b) return;
        list.Move(a, b);
        SaveData();
        RefreshItems();
    }

    private void TileArea_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TileScrollViewer == null) return;
        // 타일 Border의 Margin은 "0,6,6,0" (좌,상,우,하) 이라서
        // 세로 방향 간격에는 위쪽 여백 6만 반영된다 (오른쪽 6은 가로 간격용).
        // 기존엔 12로 계산해서 스크롤할 때마다 한 줄 높이만큼씩 어긋났었다.
        double row = TileHeight + 6;
        double page = TileRows * row;
        double target = TileScrollViewer.VerticalOffset + page * (e.Delta < 0 ? 1 : -1);
        // 혹시 모를 미세한 오차가 스크롤을 거듭하며 누적되지 않도록,
        // 매번 행 높이의 정확한 배수로 스냅한다.
        target = Math.Round(target / row) * row;
        TileScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T t) return t;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    // =========================================================
    // 문구 붙여넣기
    // =========================================================

    // =========================================================
    // 명령어 인식 (!명령어! 형식)
    // =========================================================
    //
    // 문구가 !로 시작하고 그 뒤에 닫는 !가 있으면, 그 사이의 내용을
    // "붙여넣을 문구"가 아니라 "실행할 키 조합"으로 본다.
    // 예) "ctrl+a"           → 그냥 원문 그대로 붙여넣기
    //     "!ctrl+a!"         → Ctrl+A 를 실행 (전체 선택)
    //     "!ctrl+a!아무거나"  → 뒤쪽("아무거나")은 무시하고 Ctrl+A만 실행
    // 문구가 !로 시작하지 않으면 뒤에 !가 있어도 전부 무시하고
    // 원문 그대로 붙여넣는다.

    // isCommandFormat: !로 시작하고 닫는 !도 있어서 "명령어로 보이는
    // 형태"인지. (내용을 인식했는지와는 별개 — 형식은 맞는데 모르는
    // 명령어일 수도 있다. 그 경우 반환값은 null이지만 isCommandFormat은 true.)
    private static string? TryParseCommand(string text, out bool isCommandFormat)
    {
        isCommandFormat = false;
        if (string.IsNullOrEmpty(text) || text[0] != '!') return null;

        int close = text.IndexOf('!', 1);
        if (close < 0) return null; // 닫는 !가 없으면 명령어로 보지 않고 그냥 붙여넣는다

        isCommandFormat = true;
        string command = text.Substring(1, close - 1).Trim();
        if (command.Length == 0) return null;

        return ParseCommandToSendKeys(command);
    }

    // "ctrl+a" 같은 표기를 SendKeys 문법으로 바꾼다.
    // 지원: ctrl/control, shift, alt (수정자) + 영숫자 한 글자 또는 이름 있는 특수키.
    // SendKeys 자체가 Windows 키를 보낼 방법을 제공하지 않아서 win/windows는
    // 수정자로 인식하지 않는다(모르는 키로 취급해 명령어 실행이 실패한다).
    private static string? ParseCommandToSendKeys(string command)
    {
        var parts = command.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        string modifiers = "";
        string? mainKey = null;

        foreach (var raw in parts)
        {
            string p = raw.Trim().ToLowerInvariant();
            if (p.Length == 0) continue;
            switch (p)
            {
                case "ctrl":
                case "control":
                    modifiers += "^";
                    break;
                case "shift":
                    modifiers += "+";
                    break;
                case "alt":
                    modifiers += "%";
                    break;
                default:
                    if (mainKey != null) return null; // 실제 키가 두 개 이상이면 인식하지 않는다
                    mainKey = p;
                    break;
            }
        }

        if (mainKey == null) return null;

        string? keyPart = MapKeyName(mainKey);
        if (keyPart == null) return null;

        return modifiers + keyPart;
    }

    private static string? MapKeyName(string key) => key switch
    {
        "enter" or "return" => "{ENTER}",
        "esc" or "escape" => "{ESC}",
        "tab" => "{TAB}",
        "space" or "spacebar" => " ",
        "delete" or "del" => "{DELETE}",
        "backspace" or "bs" => "{BACKSPACE}",
        "up" => "{UP}",
        "down" => "{DOWN}",
        "left" => "{LEFT}",
        "right" => "{RIGHT}",
        "home" => "{HOME}",
        "end" => "{END}",
        "pageup" or "pgup" => "{PGUP}",
        "pagedown" or "pgdn" => "{PGDN}",
        "insert" or "ins" => "{INSERT}",
        "f1" => "{F1}",
        "f2" => "{F2}",
        "f3" => "{F3}",
        "f4" => "{F4}",
        "f5" => "{F5}",
        "f6" => "{F6}",
        "f7" => "{F7}",
        "f8" => "{F8}",
        "f9" => "{F9}",
        "f10" => "{F10}",
        "f11" => "{F11}",
        "f12" => "{F12}",
        _ when key.Length == 1 => EscapeSendKeysChar(key),
        _ => null // 알 수 없는 키 이름
    };

    private static string EscapeSendKeysChar(string key)
    {
        // SendKeys에서 +^%~(){}[] 는 특별한 의미가 있어서, 문자 그대로
        // 보내려면 중괄호로 감싸야 한다.
        if ("+^%~(){}[]".Contains(key))
            return "{" + key + "}";
        return key;
    }

    private void PasteText(string text)
    {
        string? sendKeysCommand = TryParseCommand(text, out bool isCommandFormat);

        if (isCommandFormat && sendKeysCommand == null)
        {
            System.Windows.MessageBox.Show(
                "알 수 없는 명령어입니다.",
                "ClipboardLite",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // 옵션이 켜져 있으면 창을 숨기지 않는다. 단, 포커스가 이 프로그램에
        // 있는 상태(예: 창을 연 직후 바로 클릭, 대화상자를 닫은 직후)라면
        // 붙여넣을 곳이 없으므로 잠깐 숨겨 포커스를 돌려준 뒤 다시 표시한다.
        bool keepOpen = KeepOpenOnClick;
        bool needTempHide = !keepOpen || IsOwnWindowForeground();

        try
        {
            _isSelectingClipboard = true;

            if (needTempHide)
            {
                Hide();
                Thread.Sleep(50);
            }

            if (sendKeysCommand != null)
            {
                Forms.SendKeys.SendWait(sendKeysCommand);
            }
            else
            {
                Forms.Clipboard.Clear();
                Forms.Clipboard.SetText(text);
                Forms.SendKeys.SendWait("^v");
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"{(sendKeysCommand != null ? "명령어 실행" : "붙여넣기")}에 실패했습니다.\n\n{ex.Message}",
                "ClipboardLite",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _isSelectingClipboard = false;

            if (keepOpen && needTempHide && !IsVisible && !_isClosing)
            {
                ShowWithoutActivation();
            }
        }
    }

    private void OpenSettings()
    {
        var w = new SettingsWindow(this) { Owner = this };
        if (w.ShowDialog() == true)
        {
            ApplySettingsToView();
            SaveData();
        }
    }

    internal void ApplySettings(double width, double height, int columns, int rows, double fontSize, string fontFamily,
        string bg, string tileBg, string tileFg, string tabBg, string selectedBg, string tabArrowColor,
        double tabHeight, string tabFg)
    {
        TileWidth = Math.Clamp(width, 120, 500);
        TileHeight = Math.Clamp(height, 60, 300);
        TileColumns = Math.Clamp(columns, 1, 12);
        TileRows = Math.Clamp(rows, 1, 12);
        TileFontSize = Math.Clamp(fontSize, 8, 32);
        TileFontFamily = string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily;
        WindowBackground = bg;
        TileBackground = tileBg;
        TileForeground = tileFg;
        TabBackground = tabBg;
        SelectedTabBackground = selectedBg;
        TabArrowColor = tabArrowColor;
        TabHeight = Math.Clamp(tabHeight, 16, 60);
        TabForeground = tabFg;
        _tabPageStart = 0;
        ApplyMainWindowSizeFromTiles();
        ApplySettingsToView();
    }

    // =========================================================
    // 단축키 설정 적용 (설정창에서 호출)
    // =========================================================

    internal void ApplyHotkeys(
        (uint Mod, uint Key, bool IsMouse) show,
        (uint Mod, uint Key, bool IsMouse) hide,
        (uint Mod, uint Key, bool IsMouse) prev,
        (uint Mod, uint Key, bool IsMouse) next,
        (uint Mod, uint Key, bool IsMouse) multi)
    {
        HotkeyShowMod = show.Mod; HotkeyShowKey = show.Key; HotkeyShowIsMouse = show.IsMouse;
        HotkeyHideMod = hide.Mod; HotkeyHideKey = hide.Key; HotkeyHideIsMouse = hide.IsMouse;
        HotkeyTabPrevMod = prev.Mod; HotkeyTabPrevKey = prev.Key; HotkeyTabPrevIsMouse = prev.IsMouse;
        HotkeyTabNextMod = next.Mod; HotkeyTabNextKey = next.Key; HotkeyTabNextIsMouse = next.IsMouse;
        HotkeyMultiSelectMod = multi.Mod; HotkeyMultiSelectKey = multi.Key; HotkeyMultiSelectIsMouse = multi.IsMouse;
        RegisterGlobalHotkey();
        SaveData();
    }

    // =========================================================
    // 설정창 크기 기억
    // =========================================================

    internal double SettingsWindowWidth => _settingsWindowWidth;
    internal double SettingsWindowHeight => _settingsWindowHeight;

    internal void SetSettingsWindowSize(double width, double height)
    {
        if (width < 400 || height < 300) return;
        _settingsWindowWidth = width;
        _settingsWindowHeight = height;
        SaveData();
    }

    // 색상 대화상자의 "사용자 지정 색" 팔레트. 프로그램을 종료해도
    // 기억되도록 다른 설정들과 함께 저장/불러오기 대상에 포함한다.
    internal int[] CustomColors { get; set; } = new int[16];

    // 사용자 지정 색을 고른 즉시(설정창의 적용/취소와 무관하게) 저장해서
    // 잃어버리지 않게 한다.
    internal void SaveCustomColors() => SaveData();

    internal void ResetAllSettings()
    {
        Tabs.Clear();
        ClipboardItems.Clear();
        _imagePaths.Clear();
        _tileTitles.Clear();
        RecordTab.Name = "기록";
        _defaultTab = RecordTab.Name;
        _currentTab = null;
        TileWidth = 220;
        TileHeight = 110;
        TileColumns = 3;
        TileRows = 3;
        TileFontSize = 14;
        TileFontFamily = "Segoe UI";
        WindowBackground = "#F5F5F5";
        TileBackground = "#FFFFFF";
        TileForeground = "#222222";
        TabBackground = "#E1E1E1";
        SelectedTabBackground = "#FFFFFF";
        TabArrowColor = "#C0392B";
        TabHeight = 22;
        TabForeground = "#333333";
        HotkeyShowMod = MOD_CONTROL | MOD_SHIFT;
        HotkeyShowKey = (uint)Forms.Keys.V;
        HotkeyShowIsMouse = false;
        HotkeyHideMod = 0; HotkeyHideKey = 0; HotkeyHideIsMouse = false;
        HotkeyTabPrevMod = 0; HotkeyTabPrevKey = 0; HotkeyTabPrevIsMouse = false;
        HotkeyTabNextMod = 0; HotkeyTabNextKey = 0; HotkeyTabNextIsMouse = false;
        HotkeyMultiSelectMod = 0; HotkeyMultiSelectKey = 0; HotkeyMultiSelectIsMouse = false;
        KeepOpenOnClick = false;
        RegisterGlobalHotkey();
        ApplyMainWindowSizeFromTiles();
        RebuildDisplayTabs();
        SaveData();
        ApplySettingsToView();
    }

    private void ApplyMainWindowSizeFromTiles()
    {
        // 메인 창은 사용자가 직접 크기를 조절하지 않는다.
        // 실제 표시 영역은 설정한 타일 열/행 수에 정확히 맞춘다.
        const double rootMargin = 12;       // MainRoot Margin=6
        double tabHeight = TabHeight;
        const double tileTopGap = 6;        // Border Margin top
        const double tileRightGap = 6;
        const double horizontalPadding = 12; // 좌우 MainRoot margin
        const double nonClientWidthFallback = 16;
        const double nonClientHeightFallback = 39;

        int columns = Math.Max(1, TileColumns);
        int rows = Math.Max(1, TileRows);

        // 타일의 실제 배치 크기: Margin이 "0,6,6,0"이라 각 행이 자기 위쪽에,
        // 각 열이 자기 오른쪽에 여백을 가진다(마지막 행/열도 마찬가지).
        // 그래서 "칸 사이 간격"이 아니라 "칸 수만큼" 여백을 더해야 한다 -
        // 이전엔 (칸 수 - 1)로 계산해서 정확히 여백 한 칸(6px)만큼 모자라
        // 맨 아래 행이 살짝 가려지고 있었다.
        double desiredTileWidth =
            columns * TileWidth + columns * tileRightGap;
        double desiredTileHeight =
            rows * TileHeight + rows * tileTopGap;

        double headerMinWidth = 26 + 26 + 32; // 이전 / 다음 / 설정 (탭 열은 * 이므로 제외)
        double clientWidth = horizontalPadding + Math.Max(desiredTileWidth, headerMinWidth);
        double clientHeight = rootMargin + tabHeight + desiredTileHeight;

        // 제목 표시줄/테두리의 실제 크기를 우선 사용한다.
        double nonClientWidth = (ActualWidth > 0 && Width > ActualWidth)
            ? Width - ActualWidth
            : nonClientWidthFallback;
        double nonClientHeight = (ActualHeight > 0 && Height > ActualHeight)
            ? Height - ActualHeight
            : nonClientHeightFallback;

        Width = Math.Ceiling(clientWidth + nonClientWidth);
        Height = Math.Ceiling(clientHeight + nonClientHeight);
        ResizeMode = ResizeMode.NoResize;
        MinWidth = MaxWidth = Width;
        MinHeight = MaxHeight = Height;

        if (TileScrollViewer != null)
        {
            TileScrollViewer.Height = desiredTileHeight;
            TileScrollViewer.Width = desiredTileWidth;
        }
    }

    private void ApplySettingsToView()
    {
        if (MainRoot != null)
            MainRoot.Background = BrushFromHex(WindowBackground);
        UpdateTabSelectionVisuals();
        RefreshItems();
    }

    private static System.Windows.Media.Brush BrushFromHex(string value)
    {
        try
        {
            return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(value)!;
        }
        catch
        {
            return System.Windows.Media.Brushes.Transparent;
        }
    }

    // =========================================================
    // 전체 클립보드 기록 삭제
    // =========================================================

    private void ClearClipboardHistory()
    {
        Dispatcher.Invoke(() =>
        {
            ClipboardItems.Clear();

            SaveData();

            RefreshItems();
        });
    }

    // =========================================================
    // 저장
    // =========================================================

    private void SaveData()
    {
        try
        {
            Directory.CreateDirectory(_dataFolder);
            Directory.CreateDirectory(_imagesFolder);
            var data = new ClipboardData
            {
                DefaultTab = _defaultTab,
                ClipboardItems = new ObservableCollection<string>(ClipboardItems),
                Tabs = Tabs,
                ImagePaths = new Dictionary<string, string>(_imagePaths),
                TileTitles = new Dictionary<string, string>(_tileTitles),
                RecordTabName = RecordTab.Name,
                TabOrder = DisplayTabs.Select(t => t.Id).ToList(),
                TileWidth = TileWidth,
                TileHeight = TileHeight,
                TileColumns = TileColumns,
                TileRows = TileRows,
                TileFontSize = TileFontSize,
                TileFontFamily = TileFontFamily,
                WindowBackground = WindowBackground,
                TileBackground = TileBackground,
                TileForeground = TileForeground,
                TabBackground = TabBackground,
                SelectedTabBackground = SelectedTabBackground,
                TabArrowColor = TabArrowColor,
                TabHeight = TabHeight,
                TabForeground = TabForeground,
                EditWindowWidth = _editWindowWidth,
                EditWindowHeight = _editWindowHeight,
                HotkeyShowMod = HotkeyShowMod, HotkeyShowKey = HotkeyShowKey, HotkeyShowIsMouse = HotkeyShowIsMouse,
                HotkeyHideMod = HotkeyHideMod, HotkeyHideKey = HotkeyHideKey, HotkeyHideIsMouse = HotkeyHideIsMouse,
                HotkeyTabPrevMod = HotkeyTabPrevMod, HotkeyTabPrevKey = HotkeyTabPrevKey, HotkeyTabPrevIsMouse = HotkeyTabPrevIsMouse,
                HotkeyTabNextMod = HotkeyTabNextMod, HotkeyTabNextKey = HotkeyTabNextKey, HotkeyTabNextIsMouse = HotkeyTabNextIsMouse,
                HotkeyMultiSelectMod = HotkeyMultiSelectMod, HotkeyMultiSelectKey = HotkeyMultiSelectKey, HotkeyMultiSelectIsMouse = HotkeyMultiSelectIsMouse,
                SettingsWindowWidth = _settingsWindowWidth,
                SettingsWindowHeight = _settingsWindowHeight,
                AutoHideEnabled = AutoHideEnabled,
                AutoHideSeconds = AutoHideSeconds,
                KeepOpenOnClick = KeepOpenOnClick,
                CustomColors = CustomColors
            };
            File.WriteAllText(_dataFile,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    // =========================================================
    // 불러오기
    // =========================================================

    private void LoadData()
    {
        try
        {
            Directory.CreateDirectory(_dataFolder);
            Directory.CreateDirectory(_imagesFolder);
            MigrateOldDataIfNecessary();
            if (!File.Exists(_dataFile))
            {
                InitializeTabs();
                return;
            }
            var data = JsonSerializer.Deserialize<ClipboardData>(File.ReadAllText(_dataFile));
            if (data == null)
            {
                InitializeTabs();
                return;
            }
            ClipboardItems.Clear();
            foreach (var item in data.ClipboardItems ?? new ObservableCollection<string>())
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    ClipboardItems.Add(item);
                    if (ClipboardItems.Count >= MaxClipboardItems)
                        break;
                }
            }
            Tabs.Clear();
            foreach (var tab in data.Tabs ?? new ObservableCollection<ClipboardTab>())
            {
                if (tab == null || string.IsNullOrWhiteSpace(tab.Name)) continue;
                if (string.IsNullOrWhiteSpace(tab.Id)) tab.Id = Guid.NewGuid().ToString("N");
                tab.IsRecord = false;
                Tabs.Add(tab);
            }
            _imagePaths.Clear();
            if (data.ImagePaths != null)
                foreach (var p in data.ImagePaths)
                    if (!string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                        _imagePaths[p.Key] = p.Value;
            _tileTitles.Clear();
            if (data.TileTitles != null)
                foreach (var p in data.TileTitles)
                    if (!string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                        _tileTitles[p.Key] = p.Value;
            RecordTab.Name = string.IsNullOrWhiteSpace(data.RecordTabName) ? "기록" : data.RecordTabName;
            TileWidth = data.TileWidth > 0 ? data.TileWidth : 220;
            TileHeight = data.TileHeight > 0 ? data.TileHeight : 110;
            TileColumns = Math.Clamp(data.TileColumns <= 0 ? 3 : data.TileColumns, 1, 12);
            TileRows = Math.Clamp(data.TileRows <= 0 ? 3 : data.TileRows, 1, 12);
            TileFontSize = data.TileFontSize > 0 ? data.TileFontSize : 14;
            TileFontFamily = string.IsNullOrWhiteSpace(data.TileFontFamily) ? "Segoe UI" : data.TileFontFamily;
            WindowBackground = string.IsNullOrWhiteSpace(data.WindowBackground) ? "#F5F5F5" : data.WindowBackground;
            TileBackground = string.IsNullOrWhiteSpace(data.TileBackground) ? "#FFFFFF" : data.TileBackground;
            TileForeground = string.IsNullOrWhiteSpace(data.TileForeground) ? "#222222" : data.TileForeground;
            TabBackground = string.IsNullOrWhiteSpace(data.TabBackground) ? "#E1E1E1" : data.TabBackground;
            SelectedTabBackground = string.IsNullOrWhiteSpace(data.SelectedTabBackground) ? "#FFFFFF" : data.SelectedTabBackground;
            TabArrowColor = string.IsNullOrWhiteSpace(data.TabArrowColor) ? "#C0392B" : data.TabArrowColor;
            TabHeight = data.TabHeight > 0 ? data.TabHeight : 22;
            TabForeground = string.IsNullOrWhiteSpace(data.TabForeground) ? "#333333" : data.TabForeground;
            _editWindowWidth = data.EditWindowWidth >= 650 ? data.EditWindowWidth : 1000;
            _editWindowHeight = data.EditWindowHeight >= 400 ? data.EditWindowHeight : 650;

            // 단축키: 저장된 값이 있으면 그대로 쓰고, "프로그램 호출"만
            // 저장된 값이 전혀 없을 때(첫 실행 등) 기존 기본값(Ctrl+Shift+V)을 유지한다.
            // (Mod는 0이어도 유효한 설정일 수 있으므로 Key만으로 판단한다)
            if (data.HotkeyShowKey != 0)
            {
                HotkeyShowMod = data.HotkeyShowMod;
                HotkeyShowKey = data.HotkeyShowKey;
                HotkeyShowIsMouse = data.HotkeyShowIsMouse;
            }
            HotkeyHideMod = data.HotkeyHideMod;
            HotkeyHideKey = data.HotkeyHideKey;
            HotkeyHideIsMouse = data.HotkeyHideIsMouse;
            HotkeyTabPrevMod = data.HotkeyTabPrevMod;
            HotkeyTabPrevKey = data.HotkeyTabPrevKey;
            HotkeyTabPrevIsMouse = data.HotkeyTabPrevIsMouse;
            HotkeyTabNextMod = data.HotkeyTabNextMod;
            HotkeyTabNextKey = data.HotkeyTabNextKey;
            HotkeyTabNextIsMouse = data.HotkeyTabNextIsMouse;
            HotkeyMultiSelectMod = data.HotkeyMultiSelectMod;
            HotkeyMultiSelectKey = data.HotkeyMultiSelectKey;
            HotkeyMultiSelectIsMouse = data.HotkeyMultiSelectIsMouse;

            _settingsWindowWidth = data.SettingsWindowWidth >= 400 ? data.SettingsWindowWidth : 760;
            _settingsWindowHeight = data.SettingsWindowHeight >= 300 ? data.SettingsWindowHeight : 680;
            AutoHideEnabled = data.AutoHideEnabled;
            AutoHideSeconds = data.AutoHideSeconds > 0 ? data.AutoHideSeconds : 30;
            KeepOpenOnClick = data.KeepOpenOnClick;
            if (data.CustomColors is { Length: 16 })
                CustomColors = data.CustomColors;

            _defaultTab = string.IsNullOrWhiteSpace(data.DefaultTab) ? RecordTab.Name : data.DefaultTab;
            InitializeTabs();
            if (data.TabOrder != null && data.TabOrder.Count > 0)
            {
                var map = DisplayTabs.ToDictionary(t => t.Id);
                var ordered = new List<ClipboardTab>();
                foreach (var id in data.TabOrder)
                    if (map.TryGetValue(id, out var t) && !ordered.Contains(t))
                        ordered.Add(t);
                foreach (var t in DisplayTabs)
                    if (!ordered.Contains(t))
                        ordered.Add(t);
                DisplayTabs.Clear();
                foreach (var t in ordered)
                    DisplayTabs.Add(t);
                Tabs.Clear();
                foreach (var t in DisplayTabs.Where(t => !t.IsRecord))
                    Tabs.Add(t);
            }
            ApplySettingsToView();
        }
        catch
        {
            ClipboardItems.Clear();
            Tabs.Clear();
            RecordTab.Name = "기록";
            _defaultTab = RecordTab.Name;
            InitializeTabs();
            ApplySettingsToView();
        }
    }

    // =========================================================
    // 기존 AppData → 포터블 폴더 이전
    // =========================================================

    private void MigrateOldDataIfNecessary()
    {
        try
        {
            // 이미 포터블 데이터가 있으면
            // 절대 덮어쓰지 않는다.
            if (File.Exists(_dataFile))
            {
                return;
            }

            string oldFolder =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "ClipboardLite");

            string oldFile =
                Path.Combine(
                    oldFolder,
                    "clipboard.json");

            if (!File.Exists(oldFile))
            {
                return;
            }

            File.Copy(
                oldFile,
                _dataFile,
                false);
        }
        catch
        {
            // 이전 실패 시 새 데이터로 시작
        }
    }

    // =========================================================
    // 종료
    // =========================================================

    private void ExitApplication()
    {
        _isClosing = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    // =========================================================
    // 종료 처리
    // =========================================================

    private void MainWindow_PreviewKeyDown(object? sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    // =========================================================
    // 창이 보이는 동안에만 Esc를 전역 단축키로 등록한다.
    // (포커스가 다른 프로그램 창에 가 있어도 Esc로 숨길 수 있게 하기 위함.
    //  숨겨지면 곧바로 해제하므로, 다른 프로그램에서 쓰는 Esc에는
    //  ClipboardLite가 떠 있을 때만 영향을 준다.)
    // =========================================================

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RegisterEscapeGlobalHotkey();
            StartAutoHideTimer();
            RefreshMouseHook();
        }
        else
        {
            UnregisterEscapeGlobalHotkey();
            StopAutoHideTimer();
        }
    }

    private void RegisterEscapeGlobalHotkey()
    {
        if (_hwndSource == null) return;
        UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_ESCAPE);
        RegisterHotKey(_hwndSource.Handle, HOTKEY_ID_ESCAPE, MOD_NOREPEAT, (uint)Forms.Keys.Escape);
    }

    private void UnregisterEscapeGlobalHotkey()
    {
        if (_hwndSource == null) return;
        UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_ESCAPE);
    }

    // 창이 보이는 동안 설정한 시간이 지나면 자동으로 숨긴다.
    // (다른 창 클릭해도 안 숨는 것과는 별개로, 순수하게 시간 기준으로만 동작한다.)
    private void StartAutoHideTimer()
    {
        StopAutoHideTimer();
        if (!AutoHideEnabled || AutoHideSeconds <= 0) return;
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoHideSeconds) };
        _autoHideTimer.Tick += AutoHideTimer_Tick;
        _autoHideTimer.Start();
    }

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        StopAutoHideTimer();
        Hide();
    }

    private void StopAutoHideTimer()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer = null;
    }

    // 설정창에서 "클릭해도 창 유지" 옵션을 바꿨을 때 호출한다.
    internal void ApplyKeepOpenOnClick(bool enabled)
    {
        KeepOpenOnClick = enabled;
        SaveData();
    }

    // 설정창에서 자동 숨김 값을 바꿨을 때 호출한다.
    internal void ApplyAutoHide(bool enabled, double seconds)
    {
        AutoHideEnabled = enabled;
        AutoHideSeconds = seconds;
        SaveData();
        if (IsVisible) StartAutoHideTimer();
    }

    private void MainWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        // 사용자가 X를 누르면 종료하지 않고 숨긴다.
        if (!_isClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _isClosing = true;

        SaveData();

        _openingGuardTimer?.Stop();
        _openingGuardTimer = null;

        _hotkeyRetryTimer?.Stop();
        _hotkeyRetryTimer = null;

        _autoHideTimer?.Stop();
        _autoHideTimer = null;

        UninstallMouseHook();

        if (_hwndSource != null)
        {
            foreach (var (id, _, _, _, _) in GetHotkeyBindings())
                UnregisterHotKey(_hwndSource.Handle, id);
            UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID_ESCAPE);

            RemoveClipboardFormatListener(
                _hwndSource.Handle);

            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    // =========================================================
    // Windows 메시지 처리
    // =========================================================

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();

            if (id == HOTKEY_ID_ESCAPE)
            {
                Hide();
                handled = true;
                return IntPtr.Zero;
            }

            foreach (var (bindId, isMouse, mod, key, run) in GetHotkeyBindings())
            {
                if (bindId == id && !isMouse && key != 0)
                {
                    run();
                    handled = true;
                    break;
                }
            }
        }

        // 휠 좌우 틸트가 창 메시지로 직접 도착한 경우(저수준 후크가 못 받는 상황,
        // 또는 Windows가 커서 아래 창으로 라우팅한 경우)에도 탭을 넘긴다.
        // wParam의 상위 16비트가 부호 있는 틸트 양(양수=오른쪽, 음수=왼쪽)이다.
        // 창이 보이고 커서가 창 위에 있을 때만 처리하며, 아니면 건드리지 않는다.
        if (msg == WM_MOUSEHWHEEL)
        {
            int tiltDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (tiltDelta != 0 && TryHandleTabTilt(tiltDelta > 0))
            {
                handled = true;
                return IntPtr.Zero;
            }
        }

        // 옵션이 켜져 있으면 창을 클릭해도 활성화하지 않는다.
        // (붙여넣을 프로그램이 계속 포커스를 유지하므로 창을 숨길 필요가 없다.)
        if (msg == WM_MOUSEACTIVATE && KeepOpenOnClick)
        {
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }

        if (msg ==
            WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged();

            handled = true;
        }

        return IntPtr.Zero;
    }

    // =========================================================
    // Windows API
    // =========================================================

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(
        out POINT lpPoint);

    // 커서 아래에 있는 창이 이 프로그램의 창인지 확인하는 용도 (휠 틸트 처리)
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(
        IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(
        IntPtr hwnd);

    // 마우스 버튼/휠틸트를 단축키로 쓰기 위한 저수준 마우스 후크
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // =========================================================
    // POINT
    // =========================================================

    [StructLayout(
        LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;

        public int Y;
    }

    // =========================================================
    // 탭 이전 / 다음
    // =========================================================

    private void TabPrev_Click(object sender, RoutedEventArgs e)
    {
        PageTabs(forward: false);
    }

    private void TabNext_Click(object sender, RoutedEventArgs e)
    {
        PageTabs(forward: true);
    }

    private void PageTabs(bool forward)
    {
        if (DisplayTabs.Count == 0)
            return;

        int pageSize = Math.Max(1, TileColumns);

        if (forward)
        {
            int maxStart = Math.Max(0, ((DisplayTabs.Count - 1) / pageSize) * pageSize);
            _tabPageStart = Math.Min(maxStart, _tabPageStart + pageSize);
        }
        else
        {
            _tabPageStart = Math.Max(0, _tabPageStart - pageSize);
        }

        UpdateTabSelectionVisuals();
    }

    // =========================================================
    // 단축키로 탭 전환: 화면(페이지) 단위가 아니라 한 칸씩 "선택"을
    // 옮긴다. 선택된 탭이 지금 보이는 화면 밖으로 나가면, 그 탭이
    // 보이는 페이지로 화면이 따라간다. 클릭/드래그 쪽 PageTabs는
    // 건드리지 않고 그대로 둔다 - 이 메서드는 단축키 전용이다.
    // =========================================================

    private void SelectAdjacentTab(bool forward)
    {
        if (DisplayTabs.Count == 0) return;

        var current = _currentTab ?? RecordTab;
        int currentIndex = DisplayTabs.IndexOf(current);
        if (currentIndex < 0) currentIndex = 0;

        int newIndex = Math.Clamp(currentIndex + (forward ? 1 : -1), 0, DisplayTabs.Count - 1);
        if (newIndex == currentIndex) return; // 이미 맨 끝 탭이면 아무것도 안 한다

        var target = DisplayTabs[newIndex];
        _currentTab = target.IsRecord ? null : target;
        _defaultTab = target.Name;

        int page = Math.Max(1, TileColumns);
        _tabPageStart = (newIndex / page) * page;

        SaveData();
        UpdateTabSelectionVisuals();
        RefreshItems();
    }

    // =========================================================
    // 탭 드래그 중 이전/다음 화살표 위로 올라가면
    // 자동으로 탭 페이지를 앞뒤로 넘긴다.
    // =========================================================

    private void TabPrevArrow_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ClipboardTab))) return;
        if (sender is System.Windows.Controls.Button btn) btn.Background = System.Windows.Media.Brushes.Gainsboro;
        StartTabDragPaging(forward: false);
    }

    private void TabNextArrow_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ClipboardTab))) return;
        if (sender is System.Windows.Controls.Button btn) btn.Background = System.Windows.Media.Brushes.Gainsboro;
        StartTabDragPaging(forward: true);
    }

    private void TabArrow_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ClipboardTab)))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void TabArrow_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn) btn.Background = System.Windows.Media.Brushes.Transparent;
        StopTabDragPaging();
    }

    private void StartTabDragPaging(bool forward)
    {
        StopTabDragPaging();
        // 삼각형 위에 2초 이상 머물러야 넘어간다 (스치기만 해도 바로
        // 넘어가던 문제 수정 - 예전엔 진입 즉시 한 번 넘겼었다).
        _tabDragPageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _tabDragPageTimer.Tick += (_, _) => PageTabs(forward);
        _tabDragPageTimer.Start();
    }

    private void StopTabDragPaging()
    {
        _tabDragPageTimer?.Stop();
        _tabDragPageTimer = null;
    }

    // =========================================================
    // 탭 드래그
    // =========================================================

    private void TabButton_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        if (element.DataContext is not ClipboardTab tab)
            return;

        // 클릭 자체가 드래그 감지에 의해 가로채어져도 탭 선택은 즉시 반영한다.
        _currentTab = tab.IsRecord ? null : tab;
        _defaultTab = tab.Name;
        int page = Math.Max(1, TileColumns);
        _tabPageStart = (DisplayTabs.IndexOf(tab) / page) * page;
        SaveData();
        UpdateTabSelectionVisuals();
        RefreshItems();

        _dragStartPoint = e.GetPosition(this);
        _dragTab = tab;
        _isDraggingTab = false;

        element.CaptureMouse();
    }

    private void TabButton_MouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTab == null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        System.Windows.Point currentPoint = e.GetPosition(this);

        Vector delta =
            currentPoint - _dragStartPoint;

        if (!_isDraggingTab &&
            (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance))
        {
            _isDraggingTab = true;
        }
        if (!_isDraggingTab)
            return;

        if (sender is not FrameworkElement element)
            return;

        // 잡고 있는 탭 자체: 타일 드래그와 같은 방식으로 살짝 커지고
        // 그림자가 생겨 "들어올린" 느낌을 준다. DoDragDrop은 드래그가
        // 끝날 때까지 아래 호출을 블로킹하므로, 호출 전에 켜고 호출이
        // 끝난 뒤 곧바로 원복한다.
        element.Opacity = 0.55;
        element.SetValue(System.Windows.Controls.Panel.ZIndexProperty, 100);
        element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        element.RenderTransform = new System.Windows.Media.ScaleTransform(1.08, 1.08);
        element.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = System.Windows.Media.Colors.Black,
            Opacity = 0.45,
            BlurRadius = 12,
            ShadowDepth = 3
        };

        // 실제 이동은 대상 탭의 Drop 이벤트에서 처리한다.
        // DoDragDrop은 드래그가 끝날 때까지 현재 호출을 유지한다.
        System.Windows.DragDrop.DoDragDrop(
            element,
            _dragTab,
            System.Windows.DragDropEffects.Move);

        element.Opacity = 1.0;
        element.ClearValue(UIElement.RenderTransformProperty);
        element.ClearValue(UIElement.EffectProperty);
        element.ClearValue(System.Windows.Controls.Panel.ZIndexProperty);
        ClearTabDragOverHighlight();

        // 드롭이 성공했든 취소됐든, 화살표 위에서 돌던 자동 페이지
        // 타이머와 드래그 상태를 여기서 확실히 정리한다.
        StopTabDragPaging();
        if (element.IsMouseCaptured)
            element.ReleaseMouseCapture();
        _dragTab = null;
        _isDraggingTab = false;
    }

    // 탭 드래그 중 커서 아래에서 하이라이트되고 있는 대상 탭
    private System.Windows.Controls.Button? _tabDragOverTarget;

    private void ClearTabDragOverHighlight()
    {
        if (_tabDragOverTarget == null) return;
        _tabDragOverTarget.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        _tabDragOverTarget.ClearValue(System.Windows.Controls.Control.BorderThicknessProperty);
        _tabDragOverTarget = null;
    }

    private void TabButton_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ClipboardTab)))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;

            if (sender is System.Windows.Controls.Button btn &&
                !ReferenceEquals(btn, _tabDragOverTarget) &&
                btn.DataContext != _dragTab)
            {
                ClearTabDragOverHighlight();
                btn.BorderBrush = DragOverBrush();
                btn.BorderThickness = new Thickness(4);
                _tabDragOverTarget = btn;
            }
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
    }

    private void TabButton_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && ReferenceEquals(btn, _tabDragOverTarget))
            ClearTabDragOverHighlight();
    }

    private void TabButton_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ClearTabDragOverHighlight();

        if (sender is not FrameworkElement targetElement ||
            targetElement.DataContext is not ClipboardTab targetTab ||
            !e.Data.GetDataPresent(typeof(ClipboardTab)))
            return;

        if (e.Data.GetData(typeof(ClipboardTab)) is not ClipboardTab sourceTab ||
            ReferenceEquals(sourceTab, targetTab))
            return;

        int sourceIndex = DisplayTabs.IndexOf(sourceTab);
        int targetIndex = DisplayTabs.IndexOf(targetTab);

        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            return;

        DisplayTabs.Move(sourceIndex, targetIndex);

        // DisplayTabs의 순서를 사용자 탭 컬렉션에도 반영한다.
        var orderedUserTabs = DisplayTabs.Where(t => !t.IsRecord).ToList();
        Tabs.Clear();
        foreach (var tab in orderedUserTabs)
            Tabs.Add(tab);

        _tabPageStart = (DisplayTabs.IndexOf(sourceTab) / Math.Max(1, TileColumns)) * Math.Max(1, TileColumns);

        SaveData();
        UpdateTabSelectionVisuals();
        e.Handled = true;
    }
    
    private void TabButton_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
            element.ReleaseMouseCapture();

        _dragTab = null;
        _isDraggingTab = false;
    }
}

// =============================================================
// 사용자 탭
// =============================================================

public class ClipboardTab : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool IsRecord { get; set; }
    public string Name { get; set; } = "";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public ObservableCollection<string> Items { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
}

// =============================================================
// 저장 데이터
// =============================================================

public class ClipboardData
{
    public string DefaultTab { get; set; } = "기록";
    public string RecordTabName { get; set; } = "기록";
    public List<string> TabOrder { get; set; } = new();
    public double TileWidth { get; set; } = 220;
    public double TileHeight { get; set; } = 110;
    public int TileColumns { get; set; } = 3;
    public int TileRows { get; set; } = 3;
    public double TileFontSize { get; set; } = 14;
    public string TileFontFamily { get; set; } = "Segoe UI";
    public string WindowBackground { get; set; } = "#F5F5F5";
    public string TileBackground { get; set; } = "#FFFFFF";
    public string TileForeground { get; set; } = "#222222";
    public string TabBackground { get; set; } = "#E1E1E1";
    public string SelectedTabBackground { get; set; } = "#FFFFFF";
    public string TabArrowColor { get; set; } = "#C0392B";
    public double TabHeight { get; set; } = 22;
    public string TabForeground { get; set; } = "#333333";
    // 긴 문구 수정창의 마지막 크기
    public double EditWindowWidth { get; set; } = 1000;
    public double EditWindowHeight { get; set; } = 650;

    // 단축키 (Key가 0이면 미설정)
    public uint HotkeyShowMod { get; set; }
    public uint HotkeyShowKey { get; set; }
    public bool HotkeyShowIsMouse { get; set; }
    public uint HotkeyHideMod { get; set; }
    public uint HotkeyHideKey { get; set; }
    public bool HotkeyHideIsMouse { get; set; }
    public uint HotkeyTabPrevMod { get; set; }
    public uint HotkeyTabPrevKey { get; set; }
    public bool HotkeyTabPrevIsMouse { get; set; }
    public uint HotkeyTabNextMod { get; set; }
    public uint HotkeyTabNextKey { get; set; }
    public bool HotkeyTabNextIsMouse { get; set; }
    public uint HotkeyMultiSelectMod { get; set; }
    public uint HotkeyMultiSelectKey { get; set; }
    public bool HotkeyMultiSelectIsMouse { get; set; }

    // 설정창의 마지막 크기
    public double SettingsWindowWidth { get; set; } = 760;
    public double SettingsWindowHeight { get; set; } = 680;

    // 일정 시간 후 자동 숨김
    public bool AutoHideEnabled { get; set; }
    public double AutoHideSeconds { get; set; } = 30;

    // 문구 클릭 후에도 창을 숨기지 않기
    public bool KeepOpenOnClick { get; set; }

    // 색상 대화상자의 "사용자 지정 색" 팔레트 (16칸)
    public int[] CustomColors { get; set; } = new int[16];

    public ObservableCollection<string> ClipboardItems { get; set; }
        = new();

    public ObservableCollection<ClipboardTab> Tabs { get; set; }
        = new();

    public Dictionary<string, string> ImagePaths { get; set; }
        = new();

    public Dictionary<string, string> TileTitles { get; set; }
        = new();
}

public sealed class ShortTextConverter : System.Windows.Data.IValueConverter
{
    private const int PreviewLength = 220;

    public object Convert(object value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is not string text)
            return string.Empty;
        if (text.Length <= PreviewLength)
            return text;
        return text.Substring(0, PreviewLength).TrimEnd() + "…";
    }

    public object ConvertBack(object value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class TileTextMaxHeightConverter : System.Windows.Data.IValueConverter
{
    // 타일의 Padding(위/아래 10px)과 Border를 제외한 실제 문구 영역 높이
    public object Convert(object value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is double height)
            return Math.Max(20d, height - 22d);

        if (value is int intHeight)
            return Math.Max(20d, intHeight - 22d);

        return 20d;
    }

    public object ConvertBack(object value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

// =============================================================
// 설정 창
// =============================================================
public class SettingsWindow : Window
{
    // MainWindow의 같은 이름 상수를 참조하는 대신, 이 클래스 안에 값을
    // 그대로 복사해서 둔다 (MOD_CONTROL 등도 이미 이 방식으로 처리돼
    // 있었다). 값은 MainWindow의 MOUSE_TRIGGER_BASE=0xF000 기준과
    // 반드시 똑같이 맞춰야 한다.
    private const uint MOUSE_MIDDLE = 0xF001;
    private const uint MOUSE_XBUTTON1 = 0xF002;
    private const uint MOUSE_XBUTTON2 = 0xF003;
    private const uint MOUSE_WHEEL_TILT_LEFT = 0xF004;
    private const uint MOUSE_WHEEL_TILT_RIGHT = 0xF005;

    private readonly MainWindow _owner;
    private readonly System.Windows.Controls.TextBox _w, _h, _cols, _rows, _font, _tabHeight;
    private readonly System.Windows.Controls.ComboBox _fontFamily;
    private readonly System.Windows.Controls.Border _bg, _tileBg, _tileFg, _tabBg, _selBg, _arrowColor, _tabFg;
    private readonly System.Windows.Controls.CheckBox _keepOpenCheck;
    private readonly System.Windows.Controls.CheckBox _autoHideCheck;
    private readonly System.Windows.Controls.TextBox _autoHideSeconds;

    // 단축키 5종의 현재 값 (수정자, 키, 마우스여부). 키가 0이면 미설정.
    private uint _hkShowMod, _hkShowKey; private bool _hkShowIsMouse;
    private uint _hkHideMod, _hkHideKey; private bool _hkHideIsMouse;
    private uint _hkPrevMod, _hkPrevKey; private bool _hkPrevIsMouse;
    private uint _hkNextMod, _hkNextKey; private bool _hkNextIsMouse;
    private uint _hkMultiMod, _hkMultiKey; private bool _hkMultiIsMouse;

    public SettingsWindow(MainWindow owner)
    {
        _owner = owner;
        Title = "ClipboardLite 설정";
        Width = owner.SettingsWindowWidth;
        Height = owner.SettingsWindowHeight;
        MinWidth = 620;
        MinHeight = 480;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // 사용자가 마우스로 조절한 창 크기를 다음에 열 때도 그대로 쓴다.
        Closing += (_, _) =>
        {
            owner.SetSettingsWindowSize(ActualWidth, ActualHeight);
            owner.MouseCaptureCallback = null; // 캡처 중 창을 닫아도 후크에 콜백이 남지 않도록
        };

        _hkShowMod = owner.HotkeyShowMod; _hkShowKey = owner.HotkeyShowKey; _hkShowIsMouse = owner.HotkeyShowIsMouse;
        _hkHideMod = owner.HotkeyHideMod; _hkHideKey = owner.HotkeyHideKey; _hkHideIsMouse = owner.HotkeyHideIsMouse;
        _hkPrevMod = owner.HotkeyTabPrevMod; _hkPrevKey = owner.HotkeyTabPrevKey; _hkPrevIsMouse = owner.HotkeyTabPrevIsMouse;
        _hkNextMod = owner.HotkeyTabNextMod; _hkNextKey = owner.HotkeyTabNextKey; _hkNextIsMouse = owner.HotkeyTabNextIsMouse;
        _hkMultiMod = owner.HotkeyMultiSelectMod; _hkMultiKey = owner.HotkeyMultiSelectKey; _hkMultiIsMouse = owner.HotkeyMultiSelectIsMouse;

        var root = new System.Windows.Controls.DockPanel { Margin = new Thickness(18) };

        var buttons = new System.Windows.Controls.Grid { Margin = new Thickness(0, 14, 0, 0) };
        buttons.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);

        var reset = Button("초기화", 82);
        reset.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        reset.Click += (_, _) =>
        {
            _owner.ResetAllSettings();
            DialogResult = true;
        };
        buttons.Children.Add(reset);

        var right = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var export = Button("내보내기", 82); export.Click += (_, _) => ExportSettings();
        var import = Button("가져오기", 82); import.Click += (_, _) => ImportSettings();
        var save = Button("저장", 82); save.Click += (_, _) => { if (TryApply()) DialogResult = true; };
        var cancel = Button("취소", 82); cancel.Click += (_, _) => DialogResult = false;
        right.Children.Add(export); right.Children.Add(import); right.Children.Add(save); right.Children.Add(cancel);
        System.Windows.Controls.Grid.SetColumn(right, 1); buttons.Children.Add(right);
        root.Children.Add(buttons);

        var scroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
        };
        var panel = new System.Windows.Controls.StackPanel();
        scroll.Content = panel;
        root.Children.Add(scroll);

        AddTitle(panel, "모양");
        var shape = Grid3();
        AddField(shape, 0, "타일 너비", _w = Box(owner.TileWidth.ToString("0")));
        AddField(shape, 1, "타일 높이", _h = Box(owner.TileHeight.ToString("0")));
        AddField(shape, 2, "줄 당 타일 수", _cols = Box(owner.TileColumns.ToString()));
        AddField(shape, 0, "표시 줄 수", _rows = Box(owner.TileRows.ToString()), 1);
        AddField(shape, 1, "탭 높이", _tabHeight = Box(owner.TabHeight.ToString("0")), 1);
        panel.Children.Add(shape);

        AddTitle(panel, "폰트");
        var font = Grid3();
        AddField(font, 0, "폰트 크기", _font = Box(owner.TileFontSize.ToString("0")));
        _fontFamily = new System.Windows.Controls.ComboBox { Height = 30, IsEditable = false };
        foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(x => x.Source)) _fontFamily.Items.Add(f.Source);
        _fontFamily.SelectedItem = _fontFamily.Items.Cast<object>().FirstOrDefault(x => string.Equals(x.ToString(), owner.TileFontFamily, StringComparison.OrdinalIgnoreCase)) ?? "Segoe UI";
        AddField(font, 1, "폰트 종류", _fontFamily);
        panel.Children.Add(font);

        // 색상 항목만 한 줄에 4개씩 배치한다.
        AddTitle(panel, "색상");
        var colors = Grid4();
        AddColorField(colors, 0, "창 배경색", owner.WindowBackground, out _bg);
        AddColorField(colors, 1, "타일 배경색", owner.TileBackground, out _tileBg);
        AddColorField(colors, 2, "타일 글자색", owner.TileForeground, out _tileFg);
        AddColorField(colors, 3, "탭 배경색", owner.TabBackground, out _tabBg);
        AddColorField(colors, 0, "탭 글자색", owner.TabForeground, out _tabFg, 1);
        AddColorField(colors, 1, "선택 탭 색", owner.SelectedTabBackground, out _selBg, 1);
        AddColorField(colors, 2, "탭 화살표 색", owner.TabArrowColor, out _arrowColor, 1);
        panel.Children.Add(colors);

        AddTitle(panel, "단축키");
        var hotkeys = Grid3();
        AddHotkeyField(owner, hotkeys, 0, "프로그램 호출", _hkShowMod, _hkShowKey, _hkShowIsMouse, (m, k, mouse) => { _hkShowMod = m; _hkShowKey = k; _hkShowIsMouse = mouse; });
        AddHotkeyField(owner, hotkeys, 1, "프로그램 숨기기", _hkHideMod, _hkHideKey, _hkHideIsMouse, (m, k, mouse) => { _hkHideMod = m; _hkHideKey = k; _hkHideIsMouse = mouse; });
        AddHotkeyField(owner, hotkeys, 2, "여러 문구 선택", _hkMultiMod, _hkMultiKey, _hkMultiIsMouse, (m, k, mouse) => { _hkMultiMod = m; _hkMultiKey = k; _hkMultiIsMouse = mouse; });
        AddHotkeyField(owner, hotkeys, 0, "이전 탭 페이지(◀)", _hkPrevMod, _hkPrevKey, _hkPrevIsMouse, (m, k, mouse) => { _hkPrevMod = m; _hkPrevKey = k; _hkPrevIsMouse = mouse; }, 1);
        AddHotkeyField(owner, hotkeys, 1, "다음 탭 페이지(▶)", _hkNextMod, _hkNextKey, _hkNextIsMouse, (m, k, mouse) => { _hkNextMod = m; _hkNextKey = k; _hkNextIsMouse = mouse; }, 1);
        panel.Children.Add(hotkeys);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "칸을 클릭한 뒤 원하는 키나 마우스 버튼(휠클릭/뒤로/앞으로/휠 좌우 틸트)을 누르세요. 수정자(Ctrl/Shift/Alt) 없이 키 하나만 지정할 수도 있습니다. Backspace로 지웁니다.",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4)
        });

        AddTitle(panel, "붙여넣기");
        _keepOpenCheck = new System.Windows.Controls.CheckBox
        {
            Content = "문구를 클릭해도 프로그램 창을 숨기지 않기",
            IsChecked = owner.KeepOpenOnClick,
            Margin = new Thickness(0, 2, 0, 4)
        };
        panel.Children.Add(_keepOpenCheck);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "켜면 문구를 클릭해 붙여넣은 뒤에도 창이 계속 떠 있고, 창을 클릭해도 붙여넣을 프로그램의 포커스가 유지됩니다. 창은 Esc, 단축키, 트레이 아이콘 또는 자동 숨김으로 닫을 수 있습니다.",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });

        AddTitle(panel, "자동 숨김");
        var autoHideRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 10)
        };
        _autoHideCheck = new System.Windows.Controls.CheckBox
        {
            Content = "일정 시간 후 자동 숨김, 초:",
            IsChecked = owner.AutoHideEnabled,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        _autoHideSeconds = new System.Windows.Controls.TextBox
        {
            Text = owner.AutoHideSeconds.ToString("0"),
            Width = 60,
            Height = 26,
            Margin = new Thickness(8, 0, 0, 0)
        };
        autoHideRow.Children.Add(_autoHideCheck);
        autoHideRow.Children.Add(_autoHideSeconds);
        panel.Children.Add(autoHideRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "창이 열려 있는 상태로 이 시간(초)이 지나면 자동으로 숨겨집니다. 다른 곳을 클릭해도 안 숨는 것과는 별개로, 순수하게 시간만 기준으로 동작합니다.",
            FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4)
        });

        Content = root;
    }

    private static System.Windows.Controls.Button Button(string text, double width) => new() { Content = text, Width = width, Height = 30, Margin = new Thickness(4, 0, 0, 0) };
    private static System.Windows.Controls.TextBox Box(string text) => new() { Text = text, Height = 30 };

    private static System.Windows.Controls.Grid Grid3()
    {
        var g = new System.Windows.Controls.Grid();
        for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    private static System.Windows.Controls.Grid Grid4()
    {
        var g = new System.Windows.Controls.Grid();
        for (int i = 0; i < 4; i++) g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    private static void AddTitle(System.Windows.Controls.Panel p, string text)
    {
        p.Children.Add(new System.Windows.Controls.TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 7) });
    }

    private static void AddField(System.Windows.Controls.Grid g, int col, string label, System.Windows.UIElement control, int row = 0)
    {
        if (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        var box = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 10, 8) };
        box.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
        box.Children.Add(control);
        System.Windows.Controls.Grid.SetColumn(box, col); System.Windows.Controls.Grid.SetRow(box, row); g.Children.Add(box);
    }

    private void AddColorField(System.Windows.Controls.Grid g, int col, string label, string hex, out System.Windows.Controls.Border swatch, int row = 0)
    {
        var colorSwatch = new System.Windows.Controls.Border { Height = 30, Background = BrushFromHex(hex), BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Cursor = System.Windows.Input.Cursors.Hand, Tag = hex };
        swatch = colorSwatch;
        colorSwatch.MouseLeftButtonDown += (_, e) =>
        {
            // _owner.CustomColors는 설정 파일에 저장되므로, 여기서 고른
            // "사용자 지정 색"은 다른 색상 필드에서도 바로 보이고
            // 프로그램을 껐다 켜도, 내보내기에도 그대로 남는다.
            using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AllowFullOpen = true, AnyColor = true, CustomColors = _owner.CustomColors };
            try { dialog.Color = System.Drawing.ColorTranslator.FromHtml((string)colorSwatch.Tag); } catch { dialog.Color = System.Drawing.Color.White; }
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _owner.CustomColors = dialog.CustomColors;
                _owner.SaveCustomColors();
                var c = dialog.Color;
                var value = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                colorSwatch.Tag = value;
                colorSwatch.Background = BrushFromHex(value);
            }
            e.Handled = true;
        };
        AddField(g, col, label, colorSwatch, row);
    }

    // =========================================================
    // 단축키 입력칸: 클릭 후 키 조합을 누르면 그대로 저장한다.
    // =========================================================

    private static void AddHotkeyField(MainWindow owner, System.Windows.Controls.Grid g, int col, string label,
        uint mod, uint key, bool isMouse, Action<uint, uint, bool> onChange, int row = 0)
    {
        var normalBorder = System.Windows.Media.Brushes.Gray;
        var normalBg = System.Windows.Media.Brushes.White;
        var listeningBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x90, 0xD9));
        var listeningBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0xF3, 0xFC));

        var box = new System.Windows.Controls.TextBox
        {
            Height = 30,
            IsReadOnly = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Text = HotkeyToDisplay(mod, key, isMouse),
            Background = normalBg,
            BorderBrush = normalBorder,
            BorderThickness = new Thickness(1)
        };

        // 지금 이 칸이 입력을 "듣고 있다"는 걸 눈에 띄게 보여준다
        // (예전엔 클릭해도 아무 반응이 없어서 알아보기 어려웠다).
        box.GotFocus += (_, _) =>
        {
            box.Background = listeningBg;
            box.BorderBrush = listeningBorder;
            box.BorderThickness = new Thickness(2);
            // 휠 좌우 틸트는 WPF 표준 이벤트로 못 잡아서, 저수준 후크의
            // 캡처 콜백으로 받는다. 다른 키/마우스 입력이 먼저 들어오면
            // 아래 핸들러들이 콜백을 지워서 후크가 조용히 무시하게 한다.
            owner.MouseCaptureCallback = trigger =>
            {
                box.Text = HotkeyToDisplay(0, trigger, true);
                onChange(0, trigger, true);
            };
        };
        box.LostFocus += (_, _) =>
        {
            box.Background = normalBg;
            box.BorderBrush = normalBorder;
            box.BorderThickness = new Thickness(1);
            owner.MouseCaptureCallback = null;
        };

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;

            if (e.Key == Key.Back || e.Key == Key.Delete)
            {
                owner.MouseCaptureCallback = null;
                box.Text = HotkeyToDisplay(0, 0, false);
                onChange(0, 0, false);
                return;
            }

            // 수정자 키 자체만 눌렀을 때는 무시하고, 실제 키가 눌릴 때까지 기다린다.
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                    or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
                return;

            // 수정자(Ctrl/Shift/Alt) 없이 키 하나만으로도 단축키를 만들 수 있다.
            uint modifiers = (uint)Keyboard.Modifiers;
            var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(actualKey);

            owner.MouseCaptureCallback = null;
            box.Text = HotkeyToDisplay(modifiers, vk, false);
            onChange(modifiers, vk, false);
        };

        // 휠클릭(가운데 버튼) / 뒤로가기 / 앞으로가기 버튼은 WPF 표준
        // 마우스 이벤트로 바로 잡을 수 있다.
        box.PreviewMouseDown += (_, e) =>
        {
            uint? mouseCode = e.ChangedButton switch
            {
                System.Windows.Input.MouseButton.Middle => MOUSE_MIDDLE,
                System.Windows.Input.MouseButton.XButton1 => MOUSE_XBUTTON1,
                System.Windows.Input.MouseButton.XButton2 => MOUSE_XBUTTON2,
                _ => null
            };
            if (mouseCode == null) return; // 왼쪽/오른쪽 클릭은 칸에 포커스를 주는 보통 클릭으로 둔다
            e.Handled = true;
            owner.MouseCaptureCallback = null;
            box.Text = HotkeyToDisplay(0, mouseCode.Value, true);
            onChange(0, mouseCode.Value, true);
        };

        AddField(g, col, label, box, row);
    }

    private static string HotkeyToDisplay(uint mod, uint key, bool isMouse)
    {
        if (key == 0) return "(없음)";

        if (isMouse)
        {
            return key switch
            {
                MOUSE_MIDDLE => "휠 클릭",
                MOUSE_XBUTTON1 => "뒤로가기 버튼",
                MOUSE_XBUTTON2 => "앞으로가기 버튼",
                MOUSE_WHEEL_TILT_LEFT => "휠 왼쪽 틸트",
                MOUSE_WHEEL_TILT_RIGHT => "휠 오른쪽 틸트",
                _ => "(알 수 없는 마우스 입력)"
            };
        }

        var parts = new List<string>();
        if ((mod & 0x0002) != 0) parts.Add("Ctrl");   // MOD_CONTROL
        if ((mod & 0x0004) != 0) parts.Add("Shift");  // MOD_SHIFT
        if ((mod & 0x0001) != 0) parts.Add("Alt");    // MOD_ALT
        if ((mod & 0x0008) != 0) parts.Add("Win");    // MOD_WIN
        try
        {
            var wpfKey = KeyInterop.KeyFromVirtualKey((int)key);
            parts.Add(new System.Windows.Input.KeyConverter().ConvertToString(wpfKey) ?? "?");
        }
        catch { parts.Add("?"); }
        return string.Join("+", parts);
    }

    private static System.Windows.Media.Brush BrushFromHex(string value)
    {
        try { return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(value)!; }
        catch { return System.Windows.Media.Brushes.Transparent; }
    }

    private bool TryApply()
    {
        if (!double.TryParse(_w.Text, out var w) || !double.TryParse(_h.Text, out var h) ||
            !int.TryParse(_cols.Text, out var c) || !int.TryParse(_rows.Text, out var r) ||
            !double.TryParse(_font.Text, out var f) || !double.TryParse(_tabHeight.Text, out var tabH))
        {
            System.Windows.MessageBox.Show("숫자 설정값을 확인하세요.", "ClipboardLite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (!double.TryParse(_autoHideSeconds.Text, out var autoHideSec) || autoHideSec <= 0)
        {
            System.Windows.MessageBox.Show("자동 숨김 시간(초)을 0보다 큰 숫자로 입력하세요.", "ClipboardLite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _owner.ApplySettings(w, h, c, r, f, _fontFamily.SelectedItem?.ToString() ?? "Segoe UI",
            (string)_bg.Tag, (string)_tileBg.Tag, (string)_tileFg.Tag, (string)_tabBg.Tag, (string)_selBg.Tag, (string)_arrowColor.Tag,
            tabH, (string)_tabFg.Tag);
        _owner.ApplyHotkeys(
            (_hkShowMod, _hkShowKey, _hkShowIsMouse),
            (_hkHideMod, _hkHideKey, _hkHideIsMouse),
            (_hkPrevMod, _hkPrevKey, _hkPrevIsMouse),
            (_hkNextMod, _hkNextKey, _hkNextIsMouse),
            (_hkMultiMod, _hkMultiKey, _hkMultiIsMouse));
        _owner.ApplyKeepOpenOnClick(_keepOpenCheck.IsChecked == true);
        _owner.ApplyAutoHide(_autoHideCheck.IsChecked == true, autoHideSec);
        return true;
    }

    private void ExportSettings()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "ClipboardLite 설정 (*.json)|*.json", FileName = "ClipboardLite-설정.json" };
        if (dlg.ShowDialog(this) != true) return;
        var data = new ClipboardSettingsData
        {
            TileWidth = _owner.TileWidth, TileHeight = _owner.TileHeight, TileColumns = _owner.TileColumns,
            TileRows = _owner.TileRows, TileFontSize = _owner.TileFontSize, TileFontFamily = _owner.TileFontFamily,
            WindowBackground = (string)_bg.Tag, TileBackground = (string)_tileBg.Tag, TileForeground = (string)_tileFg.Tag,
            TabBackground = (string)_tabBg.Tag, SelectedTabBackground = (string)_selBg.Tag,
            TabArrowColor = (string)_arrowColor.Tag,
            TabHeight = double.TryParse(_tabHeight.Text, out var expTabH) ? expTabH : 22,
            TabForeground = (string)_tabFg.Tag,
            CustomColors = _owner.CustomColors,
            // 기본(기록) 탭은 제외하고, 사용자가 추가한 탭들의 문구를 함께 내보낸다.
            Tabs = _owner.Tabs.Select(t => new ExportedTabData { Name = t.Name, Items = t.Items.ToList() }).ToList(),
            // 문구별 대표 이미지 경로와 제목도 함께 내보낸다.
            ImagePaths = new Dictionary<string, string>(_owner.ImagePathsSnapshot),
            TileTitles = new Dictionary<string, string>(_owner.TileTitlesSnapshot)
        };
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ImportSettings()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ClipboardLite 설정 (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var d = JsonSerializer.Deserialize<ClipboardSettingsData>(File.ReadAllText(dlg.FileName));
            if (d == null) return;
            _w.Text = d.TileWidth.ToString("0"); _h.Text = d.TileHeight.ToString("0"); _cols.Text = d.TileColumns.ToString(); _rows.Text = d.TileRows.ToString(); _font.Text = d.TileFontSize.ToString("0");
            _tabHeight.Text = (d.TabHeight > 0 ? d.TabHeight : 22).ToString("0");
            _fontFamily.SelectedItem = _fontFamily.Items.Cast<object>().FirstOrDefault(x => string.Equals(x.ToString(), d.TileFontFamily, StringComparison.OrdinalIgnoreCase));
            SetColor(_bg, d.WindowBackground); SetColor(_tileBg, d.TileBackground); SetColor(_tileFg, d.TileForeground); SetColor(_tabBg, d.TabBackground); SetColor(_selBg, d.SelectedTabBackground);
            SetColor(_arrowColor, string.IsNullOrWhiteSpace(d.TabArrowColor) ? "#C0392B" : d.TabArrowColor);
            SetColor(_tabFg, string.IsNullOrWhiteSpace(d.TabForeground) ? "#333333" : d.TabForeground);
            if (d.CustomColors is { Length: 16 })
            {
                _owner.CustomColors = d.CustomColors;
                _owner.SaveCustomColors();
            }

            // 탭+문구 가져오기: 이름이 같은 탭이 있으면 문구를 이어붙이고
            // (중복 문구는 건너뜀), 없으면 새 탭을 만든다.
            if (d.Tabs is { Count: > 0 })
            {
                int imported = _owner.ImportTabItems(d.Tabs);
                if (imported > 0)
                    System.Windows.MessageBox.Show($"탭 문구 {imported}개를 가져왔습니다.", "ClipboardLite", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            // 문구별 대표 이미지 경로/제목도 함께 복원한다. 이미지 파일
            // 자체는 옮겨지지 않으므로, 가져온 컴퓨터의 같은 상대경로에
            // 실제 파일이 있을 때만 이미지가 보이고, 없으면 자동으로
            // 원문(또는 제목)이 대신 보인다.
            _owner.ImportImagePathsAndTitles(d.ImagePaths, d.TileTitles);
        }
        catch { System.Windows.MessageBox.Show("설정 파일을 읽을 수 없습니다.", "ClipboardLite", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static void SetColor(System.Windows.Controls.Border b, string value) { b.Tag = value; b.Background = BrushFromHex(value); }
}

public class ClipboardSettingsData
{
    public double TileWidth { get; set; } = 220;
    public double TileHeight { get; set; } = 110;
    public int TileColumns { get; set; } = 3;
    public int TileRows { get; set; } = 3;
    public double TileFontSize { get; set; } = 14;
    public string TileFontFamily { get; set; } = "Segoe UI";
    public string WindowBackground { get; set; } = "#F5F5F5";
    public string TileBackground { get; set; } = "#FFFFFF";
    public string TileForeground { get; set; } = "#222222";
    public string TabBackground { get; set; } = "#E1E1E1";
    public string SelectedTabBackground { get; set; } = "#FFFFFF";
    public string TabArrowColor { get; set; } = "#C0392B";
    public double TabHeight { get; set; } = 22;
    public string TabForeground { get; set; } = "#333333";

    // 색상 대화상자의 "사용자 지정 색" 팔레트
    public int[] CustomColors { get; set; } = new int[16];

    // 기본(기록) 탭을 제외한, 사용자가 추가한 탭들의 이름+문구
    public List<ExportedTabData> Tabs { get; set; } = new();

    // 문구별 대표 이미지 경로(프로그램 폴더 기준 상대경로). 경로만
    // 옮겨지고 실제 이미지 파일은 같이 옮겨지지 않으므로, 가져온 컴퓨터의
    // 같은 상대경로에 파일이 없으면 자동으로 원문(또는 제목)이 보인다.
    public Dictionary<string, string> ImagePaths { get; set; } = new();

    // 문구별 제목
    public Dictionary<string, string> TileTitles { get; set; } = new();
}

public class ExportedTabData
{
    public string Name { get; set; } = "";
    public List<string> Items { get; set; } = new();
}

// =============================================================
// 탭 이름 입력창
// =============================================================

public class TabNameWindow : Window
{
    private readonly System.Windows.Controls.TextBox _textBox;

    public string TabName
    {
        get => _textBox.Text;
        set => _textBox.Text = value;
    }

    public TabNameWindow(bool multiline = false)
    {
        Title = multiline ? "문구 수정" : "탭 이름";
        Width = multiline ? 1000 : 320;
        Height = multiline ? 650 : 150;
        MinWidth = multiline ? 650 : 0;
        MinHeight = multiline ? 400 : 0;
        ResizeMode = multiline ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new System.Windows.Controls.Grid
        {
            Margin = new Thickness(16)
        };

        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        _textBox = new System.Windows.Controls.TextBox
        {
            FontSize = multiline ? 15 : 15,
            Height = multiline ? double.NaN : 30,
            MinHeight = multiline ? 300 : 30,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };

        // WPF 기본 TextBox의 IME(한자 변환) 우클릭 메뉴 대신
        // 일반적인 편집 메뉴만 사용합니다.
        _textBox.ContextMenu = new System.Windows.Controls.ContextMenu();

        var cutItem = new System.Windows.Controls.MenuItem { Header = "잘라내기" };
        cutItem.Click += (_, _) => _textBox.Cut();

        var copyItem = new System.Windows.Controls.MenuItem { Header = "복사" };
        copyItem.Click += (_, _) => _textBox.Copy();

        var pasteItem = new System.Windows.Controls.MenuItem { Header = "붙여넣기" };
        pasteItem.Click += (_, _) => _textBox.Paste();

        var selectAllItem = new System.Windows.Controls.MenuItem { Header = "전체 선택" };
        selectAllItem.Click += (_, _) => _textBox.SelectAll();

        _textBox.ContextMenu.Items.Add(cutItem);
        _textBox.ContextMenu.Items.Add(copyItem);
        _textBox.ContextMenu.Items.Add(pasteItem);
        _textBox.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
        _textBox.ContextMenu.Items.Add(selectAllItem);

        _textBox.ContextMenuOpening += (_, _) =>
        {
            bool hasSelection = _textBox.SelectionLength > 0;
            cutItem.IsEnabled = hasSelection;
            copyItem.IsEnabled = hasSelection;
            pasteItem.IsEnabled = Forms.Clipboard.ContainsText();
        };

        System.Windows.Controls.Grid.SetRow(_textBox, 0);
        grid.Children.Add(_textBox);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var cancel = new System.Windows.Controls.Button
        {
            Content = "취소",
            Width = 70,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;

        var ok = new System.Windows.Controls.Button
        {
            Content = "확인",
            Width = 70
        };
        ok.Click += (_, _) => DialogResult = true;

        buttonPanel.Children.Add(cancel);
        buttonPanel.Children.Add(ok);

        System.Windows.Controls.Grid.SetRow(buttonPanel, 2);
        grid.Children.Add(buttonPanel);

        Content = grid;

        Loaded += (_, _) =>
        {
            _textBox.Focus();
            _textBox.SelectAll();
        };
    }
}

// =============================================================
// 대표 이미지가 없을 때만 문구 표시
// =============================================================

public class ClipboardTextVisibilityConverter :
    System.Windows.Data.IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is not string text)
        {
            return Visibility.Visible;
        }

        if (System.Windows.Application.Current?.MainWindow
            is MainWindow window &&
            (window.GetImagePath(text) != null || window.GetTitle(text) != null))
        {
            // 대표 이미지나 제목 중 하나라도 있으면, 복사된 원문 대신 그걸 보여준다.
            return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// =============================================================
// 제목 텍스트 자체 (제목이 없으면 빈 문자열)
// =============================================================

public class ClipboardTitleConverter :
    System.Windows.Data.IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is string text &&
            System.Windows.Application.Current?.MainWindow is MainWindow window)
        {
            return window.GetTitle(text) ?? "";
        }
        return "";
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// =============================================================
// 제목은 있고 대표 이미지는 없을 때만: 원문 자리에 제목을 대신 보여준다.
// =============================================================

public class ClipboardTitleOnlyVisibilityConverter :
    System.Windows.Data.IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is string text &&
            System.Windows.Application.Current?.MainWindow is MainWindow window &&
            window.GetTitle(text) != null &&
            window.GetImagePath(text) == null)
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// =============================================================
// 제목과 대표 이미지가 둘 다 있을 때만: 이미지 위에 제목을 캡션으로 보여준다.
// =============================================================

public class ClipboardTitleCaptionVisibilityConverter :
    System.Windows.Data.IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is string text &&
            System.Windows.Application.Current?.MainWindow is MainWindow window &&
            window.GetTitle(text) != null &&
            window.GetImagePath(text) != null)
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// =============================================================
// 여러 문구 선택 모드에서, 선택된 타일 위에만 표시(체크 오버레이)
// =============================================================

public class TileSelectedConverter :
    System.Windows.Data.IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is string text &&
            System.Windows.Application.Current?.MainWindow
                is MainWindow window &&
            window.IsMultiSelectMode &&
            window.IsTextSelected(text))
        {
            return Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

// =============================================================
// 타일 대표 이미지 변환기
// =============================================================

public class ClipboardImageConverter :
    System.Windows.Data.IValueConverter
{
    public object? Convert(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is not string text)
        {
            return null;
        }

        if (System.Windows.Application.Current?.MainWindow
            is not MainWindow window)
        {
            return null;
        }

        string? path =
            window.GetImagePath(text);

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var image =
                new System.Windows.Media.Imaging.BitmapImage();

            image.BeginInit();
            image.UriSource =
                new Uri(path, UriKind.Absolute);
            image.CacheOption =
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object? parameter,
        System.Globalization.CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}