// ---------------------------------------------------------------
//  Views.cs  �  MainWindow � PcBrowserPanel � QuickSearchOverlay
//               HotkeyDialog � OnlineSearchDialog � SettingsDialog
// ---------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Threading;
using IO = System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Soundeck.Models;
using Soundeck.Services;
using Soundeck.ViewModels;
using Soundeck.Views.Dialogs;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;


// -- MainWindow.cs -----------------------------------------------

namespace Soundeck.Views
{
public static class DialogHelper
{
    public static async Task<bool> ShowCustomDialog(XamlRoot root, string title, UIElement content, string okText = "OK", string? cancelText = null)
    {
        var d = new ContentDialog { 
            Background = MainWindow.BR_CONTROL, 
            BorderBrush = MainWindow.BR_ACCENT_DIM, 
            BorderThickness = new Thickness(1), 
            Title = new TextBlock { Text = title, Foreground = MainWindow.BR_TXT, FontWeight = Microsoft.UI.Text.FontWeights.Bold }, 
            XamlRoot = root 
        };
        var stack = new StackPanel { Spacing = 24 };
        stack.Children.Add(content);
        
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        Button btnOk = new Button { Content = okText, Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1), Foreground = MainWindow.BR_TXT, Width = 120 };
        btnOk.Resources["ButtonBackgroundPointerOver"] = MainWindow.BR_ACCENT_DIM;
        btnOk.Resources["ButtonBackgroundPressed"] = MainWindow.BR_ACCENT;
        btnOk.Resources["ButtonForegroundPointerOver"] = MainWindow.BR_TXT;
        btnOk.Resources["ButtonForegroundPressed"] = MainWindow.BR_TXT;
        btnOk.Resources["ButtonBorderBrushPointerOver"] = MainWindow.BR_ACCENT_DIM;
        btnOk.Resources["ButtonBorderBrushPressed"] = MainWindow.BR_ACCENT_DIM;
        btnPanel.Children.Add(btnOk);
        
        Button? btnCancel = null;
        if (cancelText != null) {
            btnCancel = new Button { Content = cancelText, Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1), Foreground = MainWindow.BR_TXT, Width = 120 };
            btnCancel.Resources["ButtonBackgroundPointerOver"] = MainWindow.BR_ACCENT_DIM;
            btnCancel.Resources["ButtonBackgroundPressed"] = MainWindow.BR_ACCENT;
            btnCancel.Resources["ButtonForegroundPointerOver"] = MainWindow.BR_TXT;
            btnCancel.Resources["ButtonForegroundPressed"] = MainWindow.BR_TXT;
            btnCancel.Resources["ButtonBorderBrushPointerOver"] = MainWindow.BR_ACCENT_DIM;
            btnCancel.Resources["ButtonBorderBrushPressed"] = MainWindow.BR_ACCENT_DIM;
            btnPanel.Children.Add(btnCancel);
        }
        
        stack.Children.Add(btnPanel);
        d.Content = stack;
        
        bool res = false;
        btnOk.Click += (_, _) => { res = true; d.Hide(); };
        if (btnCancel != null) btnCancel.Click += (_, _) => { res = false; d.Hide(); };
        
        try { await d.ShowAsync(); } catch { }
        return res;
    }
}

public sealed class MainWindow : Window
{
    private readonly MainViewModel _vm = App.ViewModel;
    private Sound? _contextSound;
    private nint _hwnd;

    // P/Invoke
    [DllImport("user32.dll")] static extern nint SetWindowLongPtr(nint h, int i, nint v);
    [DllImport("user32.dll")] static extern nint CallWindowProc(nint p, nint h, uint m, nuint w, nint l);
    [DllImport("shell32.dll")] static extern void DragAcceptFiles(nint h, bool f);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(nuint d, uint i, char[]? b, uint c);
    [DllImport("shell32.dll")] static extern void DragFinish(nuint d);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    // Hook message pour intercepter WM_SYSCHAR avant WinUI
    [DllImport("user32.dll")] static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hhk, int nCode, nuint w, nint l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    // Mode sombre natif DWM � évite le cadre/bordure blanc visible (notamment en perte de focus)
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref uint value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;

    private delegate nint HookProc(int nCode, nuint wParam, nint lParam);
    private HookProc? _msgHook;
    private nint _msgHookHandle;

    [StructLayout(LayoutKind.Sequential)]
    private struct CWPSTRUCT { public nint lParam; public nuint wParam; public uint message; public nint hwnd; }

    // MSG structure for WH_GETMESSAGE hook
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int ptX, ptY; }

    private nint MsgHookProc(int nCode, nuint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var msg = Marshal.PtrToStructure<MSG>(lParam);
            // N'intercepter QUE WM_SYSCHAR et WM_SYSDEADCHAR � ce sont eux qui
            // produisent le "ding" Windows. WM_SYSKEYDOWN/UP ne doivent PAS être
            // filtrés ici : WinUI en a besoin pour générer ses propres KeyDown events
            // (notamment dans HotkeyDialog). Sans eux, les combinaisons Alt+touche
            // ne remontent plus jamais jusqu'au handler OnKeyDown.
            if (msg.message == WM_SYSCHAR || msg.message == WM_SYSDEADCHAR)
                Marshal.WriteInt32(lParam, IntPtr.Size, 0 /*WM_NULL*/);
        }
        return CallNextHookEx(_msgHookHandle, nCode, wParam, lParam);
    }
    private delegate nint WndProcDelegate(nint h, uint m, nuint w, nint l);
    private WndProcDelegate? _wndProc;
    private nint _oldWndProc;
    private const uint WM_HOTKEY = 0x0312, WM_DROPFILES = 0x0233,
                       WM_SYSCHAR = 0x0106, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105,
                       WM_SYSDEADCHAR = 0x0107;
    private const int GWLP_WNDPROC = -4;

    // Palette
    // -- Palette ---------------------------------------------------------------
    static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
    static SolidColorBrush  B(Windows.UI.Color c)     => new(c);

    // BG / SIDEBAR_BG / CARD / CARD_HV / NP_BG sont des SolidColorBrush MUTABLES
    // (comme BR_ACCENT), pas des Windows.UI.Color figées : ApplyTheme() modifie
    // directement leur .Color, ce qui propage le changement partout o� le m�me
    // objet brush est utilisé (root grid, sidebar, cartes, barre now-playing�)
    // sans reconstruire l'arbre visuel. C'est ce qui manquait pour que I18n.T("Solide")
    // et les modes Mica/Acrylic aient un effet visible.
    internal static readonly SolidColorBrush BR_BG          = new(C(15, 15, 20));
    internal static readonly SolidColorBrush BR_SIDEBAR_BG  = new(C(20, 20, 28));
    internal static readonly SolidColorBrush BR_CARD        = new(C(30, 30, 45));
    internal static readonly SolidColorBrush BR_CARD_HV     = new(C(40, 40, 60));
    internal static readonly SolidColorBrush BR_NP_BG       = new(C(18, 18, 28));
    // Couleurs de base opaques (mode Solide) mémorisées pour pouvoir y revenir
    // proprement en repassant de Mica/Acrylic � Solide.
    internal static Windows.UI.Color BG_BASE         = C(15, 15, 20);
    static Windows.UI.Color SIDEBAR_BG_BASE = C(20, 20, 28);
    static Windows.UI.Color CARD_BASE       = C(30, 30, 45);
    static Windows.UI.Color CARD_HV_BASE    = C(40, 40, 60);
    static Windows.UI.Color NP_BG_BASE      = C(18, 18, 28);
    static Windows.UI.Color CONTROL_BASE    = C(25, 25, 30);
    static Windows.UI.Color BORDER      = C(45, 45, 65);
    internal static readonly SolidColorBrush BR_ACCENT     = new(C(108, 114, 245));
    internal static readonly SolidColorBrush BR_ACCENT2    = new(C(150, 155, 255));
    internal static readonly SolidColorBrush BR_ACCENT_DIM = new(C(30, 32, 80));
    internal static readonly SolidColorBrush BR_ACCENT_TRANSLUCENT = new(C(108, 114, 245));
    internal static readonly SolidColorBrush BR_ACCENT_TXT = new(C(255, 255, 255));
    internal static readonly SolidColorBrush BR_CONTROL = new(C(25, 25, 30));
    internal static readonly SolidColorBrush BR_GLASSY = new(Windows.UI.Color.FromArgb(15, 255, 255, 255));
    static Windows.UI.Color GREEN       = C(34, 197, 94);
    static Windows.UI.Color GREEN_DIM   = C(13, 50, 22);
    static Windows.UI.Color YELLOW      = C(234, 179, 8);
    static Windows.UI.Color RED         = C(239, 68, 68);
    static Windows.UI.Color RED_DIM     = C(60, 18, 18);
    static Windows.UI.Color TXT         = C(232, 232, 248);
    static Windows.UI.Color TXT2        = C(144, 144, 176);
    static Windows.UI.Color TXT3        = C(90, 90, 120);
    internal static readonly SolidColorBrush BR_TXT  = new(TXT);
    internal static readonly SolidColorBrush BR_TXT2 = new(TXT2);
    internal static readonly SolidColorBrush BR_TXT3 = new(TXT3);
    static Windows.UI.Color TRANSP      = Windows.UI.Color.FromArgb(0, 0, 0, 0);
    static Windows.UI.Color WHITE       = Windows.UI.Color.FromArgb(255, 255, 255, 255);
    // legacy aliases
    static Windows.UI.Color SURFACE     = C(20, 20, 34);
    static Windows.UI.Color SIDEBAR     = C(20, 20, 28);
    static Windows.UI.Color BTN_BG      = C(40, 40, 60);
    static Windows.UI.Color ADIM        = C(30, 32, 80);

    // -- Accent color presets --------------------------------------------------
    internal static readonly Dictionary<string, (Windows.UI.Color Accent, Windows.UI.Color Accent2, Windows.UI.Color Dim)> AccentPresets = new()
    {
        ["purple"]   = (C(90, 30, 160),   C(110, 50, 180),  C(25, 10, 40)),
        ["pink"]     = (C(160, 20, 90),   C(180, 40, 110),  C(40, 8, 20)),
        ["blue"]     = (C(20, 60, 160),   C(40, 80, 180),   C(8, 20, 40)),
        ["green"]    = (C(15, 110, 50),   C(25, 130, 65),   C(8, 25, 12)),
        ["orange"]   = (C(160, 65, 10),   C(180, 85, 20),   C(35, 15, 4)),
        ["sombre"]   = (C(80, 80, 95),    C(100, 100, 115), C(18, 18, 25)),
        ["blanc"]    = (C(255, 255, 255), C(235, 235, 240), C(200, 200, 205)),
    };

    // -- Extra UI refs ---------------------------------------------------------
        
    // MULTI-SELECT FIELDS
    private System.Collections.Generic.HashSet<Sound> _selectedSounds = new();
    private System.Collections.Generic.HashSet<Sound> _preLassoSelection = new();
    private Microsoft.UI.Xaml.Controls.Canvas _lassoCanvas = null!;
    private Microsoft.UI.Xaml.Shapes.Rectangle _lassoRect = null!;
    private Windows.Foundation.Point _lassoStart;
    private bool _isLassoing;
    private Windows.Foundation.Point _lastLassoPointer;
    private Microsoft.UI.Xaml.DispatcherTimer _lassoScrollTimer = null!;
    private Microsoft.UI.Xaml.Controls.Border _multiSelectBar = null!;
    private Microsoft.UI.Xaml.Controls.TextBlock _multiSelectText = null!;
    private Microsoft.UI.Xaml.Controls.Grid _gridContainer = null!;
    private StackPanel   _sidebarNav       = null!;
    private StackPanel   _bottomNav        = null!;
    private TextBlock    _mainTitle        = null!;
    private Microsoft.UI.Xaml.FrameworkElement _titleRow = null!;
    private TextBlock    _mainCount        = null!;
    private StackPanel   _actionRow        = null!;
    private Border       _vbBanner         = null!;
    private TextBox      _searchBox        = null!;
    private Grid         _soundGrid        = null!;
    private Microsoft.UI.Xaml.Controls.VariableSizedWrapGrid _vwg = null;
    private Microsoft.UI.Xaml.Controls.ContentControl _pageContainer = null!;
    private ScrollViewer _gridScroll       = null!;
    private StackPanel   _emptyState       = null!;
    private TextBlock    _statusBar        = null!;
    private TextBlock    _soundCount       = null!;
    private Ellipse      _engineDot        = null!;
    private TextBlock    _engineStatusText = null!;
    private MenuFlyout   _contextMenu      = null!;
    private StackPanel   _collectionTabs   = null!;
    private TextBlock    _npName           = null!;
    private StackPanel   _npSub            = null!;
    private TextBlock    _npTime           = null!;
    private Border       _npBar            = null!;
    private Slider       _npVolSlider      = null!;
    // Seek bar custom (ProgressBar + overlay transparent) � remplace le Slider
    // dont PointerReleased est avalé par le template interne WinUI 3.
    private Grid         _npSeekTrack      = null!;
    private Rectangle    _npSeekFill       = null!;
    private TextBlock    _npTimeLeft       = null!;
    private TextBlock    _npTimeRight      = null!;
    private Button       _npPlayPauseBtn   = null!;
    private Microsoft.UI.Xaml.Controls.FontIcon     _npPlayPauseIcon  = null!;
    private bool         _userDraggingSeek = false; // true pendant que l'user drag la seek bar
    private string?      _currentPlayingEid = null;  // engine id du son en cours, pour seek fiable
    private DispatcherTimer? _npTimer;
    private PcBrowserPanel? _pcBrowserPanel;
    private QuickSearchOverlay? _overlay;
    private TrayIconService?    _tray;
    private bool _forceClose;
    private bool _onPcTab;
    private const string PcTabKey = "💻 PC";

    // Page navigation
    private enum Page { Sounds, PcBrowser, OnlineSearch, Settings }
    private Page _currentPage = Page.Sounds;
    private OnlineSearchPanel? _onlineSearchPanel;
    private SettingsPanel? _settingsPanel;

    // Sidebar collapse
    private bool _sidebarCollapsed = false;
    private ColumnDefinition? _sidebarColumn;
    private FrameworkElement? _sidebarElement;
    private FrameworkElement? _mainContent;

    // Minimum window size
    private int _minWidth  = 700;
    private int _minHeight = 500;

    // Local mirror of Sound.Id ? engine active id, for IsPlaying sync
    private readonly Dictionary<string, string> _activeSoundEngineIds = new();
    private readonly Dictionary<string, Action<float>> _soundVolCallbacks = new();
    // Track last column count so RefreshGrid only rebuilds cards when layout actually changes
    private int _lastCols = -1;

    // -- Build UI --------------------------------------------------------------
    private UIElement BuildUI()
    {
        _contextMenu    = BuildContextMenu();
        _collectionTabs = new StackPanel(); // compat — sidebar remplace les tabs

        var root = new Grid { Background = BR_BG };
        
        // Espace dédié pour la barre de titre (évite la superposition avec les boutons système)
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _sidebarColumn = new ColumnDefinition { Width = new GridLength(240) };
        root.ColumnDefinitions.Add(_sidebarColumn);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSidebar();
        _sidebarElement = sidebar as FrameworkElement;
        var main    = BuildMain();
        _mainContent = main;
        
        Grid.SetRow(sidebar, 1); Grid.SetColumn(sidebar, 0); Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(sidebar, 2);
        Grid.SetRow(main, 1);    Grid.SetColumn(main, 1);
        
        var appTitle = new TextBlock
        {
            Text = "Soundeck",
            FontSize = 12,
            Foreground = MainWindow.BR_TXT3,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        Grid.SetRow(appTitle, 0);
        Grid.SetColumnSpan(appTitle, 2);
        
        // Ligne séparatrice sous la barre de titre
        var titleBarLine = new Border
        {
            Height = 1,
            Background = MainWindow.BR_ACCENT_DIM,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(titleBarLine, 0);
        Grid.SetColumnSpan(titleBarLine, 2);
        
        root.Children.Add(sidebar); root.Children.Add(main); root.Children.Add(appTitle); root.Children.Add(titleBarLine);
        return root;
    }

    // --------------------------- SIDEBAR --------------------------------------
    private FrameworkElement BuildSidebar()
    {
        var root = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        // Bordure droite continue sur toute la hauteur (delimiteur sidebar / contenu)
        var sb = new Grid { Width = 240, Background = BR_SIDEBAR_BG, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(0, 0, 1, 0), RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform() };
        root.Children.Add(sb);
        sb.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // logo
        sb.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // nav
        sb.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // volumes
        sb.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // bottom

        // Indicateur engine moderne (sans logo ni titre "Soundeck Pro")
        _engineDot        = new Ellipse { Width = 7, Height = 7, Fill = MainWindow.BR_TXT3, VerticalAlignment = VerticalAlignment.Center };
        _engineStatusText = new TextBlock { FontSize = 10, Foreground = MainWindow.BR_TXT3, VerticalAlignment = VerticalAlignment.Center };
        var enginePill = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        enginePill.Children.Add(_engineDot); enginePill.Children.Add(_engineStatusText);

        var logoRow = new ContentControl
        {
            Height = 44,
            Padding = new Thickness(14, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = enginePill
        };
        Grid.SetRow(logoRow, 0); sb.Children.Add(logoRow);

        // Nav
        var navScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 2, 0, 8)
        };
        _sidebarNav = new StackPanel { Spacing = 2 };
        var navGrid = new Grid();

        navGrid.Children.Add(_sidebarNav);
        navScroll.Content = navGrid;
        Grid.SetRow(navScroll, 1); sb.Children.Add(navScroll);

        // Volumes block (master + mic + sons)
        var volBlock = new StackPanel
        {
            Padding = new Thickness(16, 12, 16, 8), Spacing = 8,
            BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(0, 1, 0, 0)
        };

        // Master volume
        volBlock.Children.Add(new TextBlock { Text = "VOLUME MASTER", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = MainWindow.BR_TXT3 });
        var volRow = new Grid();
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        var masterSlider = new Slider { Minimum = 0, Maximum = 150, Value = Math.Min(_vm.Config.MasterVolume * 100, 150), VerticalAlignment = VerticalAlignment.Center, Foreground = MainWindow.BR_ACCENT };
        var masterLbl    = new TextBlock { Text = $"{(int)(_vm.Config.MasterVolume * 100)}%", FontSize = 11, Foreground = MainWindow.BR_TXT2, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        masterSlider.ValueChanged += (_, e) =>
        {
            float v = (float)(e.NewValue / 100.0);
            _vm.Engine.UpdateMasterVolume(v);
            _vm.Config.MasterVolume = v;
            masterLbl.Text = $"{(int)e.NewValue}%";
            _vm.Save();
        };
        Grid.SetColumn(masterSlider, 0); Grid.SetColumn(masterLbl, 1);
        volRow.Children.Add(masterSlider); volRow.Children.Add(masterLbl);
        volBlock.Children.Add(volRow);

        // Mic volume
        volBlock.Children.Add(new TextBlock { Text = I18n.T("VOLUME MICRO"), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = MainWindow.BR_TXT3 });
        var micRow = new Grid();
        micRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        micRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        var micSlider = new Slider { Minimum = 0, Maximum = 150, Value = Math.Min(_vm.Config.MicVolume * 100, 150), VerticalAlignment = VerticalAlignment.Center, Foreground = B(GREEN) };
        var micLbl    = new TextBlock { Text = $"{(int)(_vm.Config.MicVolume * 100)}%", FontSize = 11, Foreground = MainWindow.BR_TXT2, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        micSlider.ValueChanged += (_, e) =>
        {
            float v = (float)(e.NewValue / 100.0);
            _vm.Engine.UpdateMicVolume(v);
            _vm.Config.MicVolume = v;
            micLbl.Text = $"{(int)e.NewValue}%";
        };
        Grid.SetColumn(micSlider, 0); Grid.SetColumn(micLbl, 1);
        micRow.Children.Add(micSlider); micRow.Children.Add(micLbl);
        volBlock.Children.Add(micRow);

        // Sons volume (default volume for new sounds)
        volBlock.Children.Add(new TextBlock { Text = I18n.T("VOLUME DÉFAUT SONS"), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = MainWindow.BR_TXT3 });
        var sndRow = new Grid();
        sndRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sndRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        var sndSlider = new Slider { Minimum = 0, Maximum = 150, Value = Math.Min(_vm.Config.DefaultVolume * 100, 150), VerticalAlignment = VerticalAlignment.Center, Foreground = B(YELLOW) };
        var sndLbl    = new TextBlock { Text = $"{(int)(_vm.Config.DefaultVolume * 100)}%", FontSize = 11, Foreground = MainWindow.BR_TXT2, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        sndSlider.ValueChanged += (_, e) =>
        {
            float v = (float)(e.NewValue / 100.0);
            _vm.Config.DefaultVolume = v;
            sndLbl.Text = $"{(int)e.NewValue}%";
        };
        Grid.SetColumn(sndSlider, 0); Grid.SetColumn(sndLbl, 1);
        sndRow.Children.Add(sndSlider); sndRow.Children.Add(sndLbl);
        volBlock.Children.Add(sndRow);

        Grid.SetRow(volBlock, 2); sb.Children.Add(volBlock);

        // Bottom
        _bottomNav = new StackPanel
        {
            Spacing = 2, Padding = new Thickness(0, 6, 0, 10)
        };
        _bottomNav.Children.Add(SbDivider());
        _bottomNav.Children.Add(SbBtn("+", I18n.T("Nouvelle collection"), false, asyncClick: async () => await AddCollection()));
        _bottomNav.Children.Add(SbDivider());
        _bottomNav.Children.Add(SbBtn("⚙️", I18n.T("Paramètres"), _currentPage == Page.Settings, click: OpenSettings));
        Grid.SetRow(_bottomNav, 3); sb.Children.Add(_bottomNav);

        return root;
    }

    private Border SbDivider() => new() { Height = 1, Background = MainWindow.BR_ACCENT_DIM, Margin = new Thickness(12, 4, 12, 4), BorderThickness = new Thickness(0) };

        private void AttachButtonAnimations(FrameworkElement btn)
    {
        btn.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        btn.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = 1.0, ScaleY = 1.0 };
        btn.PointerPressed += (s, e) => {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var animX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 0.95, Duration = new Duration(TimeSpan.FromMilliseconds(50)) };
            var animY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 0.95, Duration = new Duration(TimeSpan.FromMilliseconds(50)) };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animX, btn.RenderTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animX, "ScaleX");
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animY, btn.RenderTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animY, "ScaleY");
            sb.Children.Add(animX); sb.Children.Add(animY); sb.Begin();
        };
        btn.PointerReleased += (s, e) => {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var animX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
            var animY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animX, btn.RenderTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animX, "ScaleX");
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animY, btn.RenderTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animY, "ScaleY");
            sb.Children.Add(animX); sb.Children.Add(animY); sb.Begin();
        };
        btn.PointerExited += (s, e) => {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var animX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
            var animY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animX, btn.RenderTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animX, "ScaleX");
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animY, btn.RenderTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animY, "ScaleY");
            sb.Children.Add(animX); sb.Children.Add(animY); sb.Begin();
        };
    }

    private Border SbBtn(string icon, string label, bool active, bool showActiveBg = false, Func<Task>? asyncClick = null, Action? click = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
        var iconBox = new Grid { Width = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        iconBox.Children.Add(new TextBlock { Text = icon, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = active ? 1.0 : 0.7 });
        row.Children.Add(iconBox);
        row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, Foreground = active ? MainWindow.BR_TXT : MainWindow.BR_TXT2, VerticalAlignment = VerticalAlignment.Center, FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal });
        var btn = new Border
        {
            Child = row, Background = (active && showActiveBg) ? MainWindow.BR_ACCENT_TRANSLUCENT : new SolidColorBrush(TRANSP),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 6, 12, 8), CornerRadius = new CornerRadius(6)
        };
        btn.PointerEntered += (_, _) => { if (!active) btn.Background = MainWindow.BR_GLASSY; };
        btn.PointerExited += (_, _) => { if (!active) btn.Background = new SolidColorBrush(TRANSP); };
        if (asyncClick != null) btn.Tapped += async (_, _) => await asyncClick();
        if (click      != null) btn.Tapped += (_, _) => click();
        AttachButtonAnimations(btn);
        return btn;
    }

    private void RebuildSidebar()
    {
        _sidebarNav.Children.Clear();
        _sidebarNav.Children.Add(SbLabel(I18n.T("Collections")));

        bool favActive = _currentPage == Page.Sounds && _vm.ActiveCollection == "__FAVORITES__";
        var favInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
        var favIconBox = new Grid { Width = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        favIconBox.Children.Add(new TextBlock { Text = "⭐", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        favInner.Children.Add(favIconBox);
        favInner.Children.Add(new TextBlock { Text = I18n.T("Favoris"), FontSize = 13.5, FontWeight = favActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Foreground = favActive ? B(TXT) : B(TXT2) });
        
        var favCountPanel = new Border { Background = favActive ? MainWindow.BR_ACCENT_DIM : MainWindow.BR_CONTROL, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, -2, 0, 0) };
        favCountPanel.HorizontalAlignment = HorizontalAlignment.Right;
        var favCount = _vm.AllSounds.Count(s => s.Favorite);
        favCountPanel.Child = new TextBlock { Text = favCount.ToString(), FontSize = 10, Foreground = favActive ? MainWindow.BR_TXT : MainWindow.BR_TXT2, FontWeight = Microsoft.UI.Text.FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        
        var favGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        favGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        favGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(favInner, 0); Grid.SetColumn(favCountPanel, 1);
        favGrid.Children.Add(favInner); favGrid.Children.Add(favCountPanel);

        var favBtn = new Border
        {
            Child = favGrid, Background = new SolidColorBrush(TRANSP),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 6, 12, 8), CornerRadius = new CornerRadius(6)
        };
        favBtn.PointerEntered += (_, _) => { if (!favActive) favBtn.Background = MainWindow.BR_GLASSY; };
        favBtn.PointerExited += (_, _) => { if (!favActive) favBtn.Background = new SolidColorBrush(TRANSP); };
        favBtn.Tapped      += (_, _) => SwitchToCollection("__FAVORITES__");
        AttachButtonAnimations(favBtn);
        _sidebarNav.Children.Add(favBtn);

        foreach (var cap in _vm.Collections)
        {
            bool active = _currentPage == Page.Sounds && cap == _vm.ActiveCollection;
            var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
            var colIconBox = new Grid { Width = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            colIconBox.Children.Add(new TextBlock { Text = "\U0001F4C2", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            inner.Children.Add(colIconBox);
            inner.Children.Add(new TextBlock { Text = cap, FontSize = 13.5, FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Foreground = active ? B(TXT) : B(TXT2) });
            
            var countPanel = new Border { Background = active ? MainWindow.BR_ACCENT_DIM : MainWindow.BR_CONTROL, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, -2, 0, 0) };
            countPanel.HorizontalAlignment = HorizontalAlignment.Right;
            var c = _vm.AllSounds.Count(s => s.Collection == cap);
            countPanel.Child = new TextBlock { Text = c.ToString(), FontSize = 10, Foreground = active ? MainWindow.BR_TXT : MainWindow.BR_TXT2, FontWeight = Microsoft.UI.Text.FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            
            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(inner, 0); Grid.SetColumn(countPanel, 1);
            grid.Children.Add(inner); grid.Children.Add(countPanel);

            var btn = new Border
            {
                Child = grid, Background = new SolidColorBrush(TRANSP),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 6, 12, 8), CornerRadius = new CornerRadius(6)
            };
            btn.PointerEntered += (_, _) => { if (!active) btn.Background = MainWindow.BR_GLASSY; };
            btn.PointerExited += (_, _) => { if (!active) btn.Background = new SolidColorBrush(TRANSP); };
            btn.Tapped      += (_, _) => SwitchToCollection(cap);
            btn.RightTapped += (_, _) => ShowTabMenu(cap, btn);
            AttachButtonAnimations(btn);
            _sidebarNav.Children.Add(btn);

            }

        _sidebarNav.Children.Add(SbLabel(I18n.T("Outils")));
        
        bool pcActive = _currentPage == Page.PcBrowser;
        var pcBtn = SbBtn("\U0001F50D", I18n.T("Parcourir le PC"), pcActive, click: SwitchToPcTab);
        _sidebarNav.Children.Add(pcBtn);
        bool dlActive = _currentPage == Page.OnlineSearch;
        var dlBtn = SbBtn("\U0001F310", I18n.T("Recherche en ligne"), dlActive, click: OpenOnlineSearch);
        _sidebarNav.Children.Add(dlBtn);
        bool setActive = _currentPage == Page.Settings;
        // Rebuild bottom nav so Settings button updates
        if (_bottomNav != null)
        {
            _bottomNav.Children.Clear();
            _bottomNav.Children.Add(SbDivider());
            _bottomNav.Children.Add(SbBtn("+", I18n.T("Nouvelle collection"), false, asyncClick: async () => await AddCollection()));
            _bottomNav.Children.Add(SbDivider());
            _bottomNav.Children.Add(SbBtn("⚙️", I18n.T("Paramètres"), setActive, showActiveBg: true, click: OpenSettings));
        }

        
    }

    private TextBlock SbLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 10,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = MainWindow.BR_TXT3, Margin = new Thickness(14, 4, 14, 4)
    };

    // --------------------------- MAIN -----------------------------------------
    
    private void GridContainer_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var parent = e.OriginalSource as Microsoft.UI.Xaml.DependencyObject;
        while (parent != null)
        {
            if (parent is Microsoft.UI.Xaml.FrameworkElement fe && fe.DataContext is Sound) return;
            if (parent == _vwg || parent == _gridScroll || parent == _gridContainer) break;
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
        }
        
        bool isCtrl = (GetAsyncKeyState(0x11) & unchecked((short)0x8000)) != 0;
        bool isShift = (GetAsyncKeyState(0x10) & unchecked((short)0x8000)) != 0;
        if (!isCtrl && !isShift) { ClearSelection(); }
        
        _lassoStart = e.GetCurrentPoint(_gridContainer).Position;
        _isLassoing = true;
        _lastLassoPointer = e.GetCurrentPoint(_gridContainer).Position;
        _lassoStart = _gridContainer.TransformToVisual(_lassoCanvas).TransformPoint(_lastLassoPointer);
        _lassoScrollTimer.Start();
        
        _lassoRect.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_lassoRect, _lassoStart.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_lassoRect, _lassoStart.Y);
        _lassoRect.Width = 0;
        _lassoRect.Height = 0;
        _gridContainer.CapturePointer(e.Pointer);
        
        _preLassoSelection = new System.Collections.Generic.HashSet<Sound>(_selectedSounds);
    }

    private void GridContainer_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isLassoing) return;
        _lastLassoPointer = e.GetCurrentPoint(_gridContainer).Position;
        UpdateLassoRect();
    }

    private void UpdateLassoRect()
    {
        if (!_isLassoing) return;
        var currentInGrid = _gridContainer.TransformToVisual(_lassoCanvas).TransformPoint(_lastLassoPointer);
        
        double x = System.Math.Min(_lassoStart.X, currentInGrid.X);
        double y = System.Math.Min(_lassoStart.Y, currentInGrid.Y);
        double w = System.Math.Abs(_lassoStart.X - currentInGrid.X);
        double h = System.Math.Abs(_lassoStart.Y - currentInGrid.Y);
        
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_lassoRect, x);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_lassoRect, y);
        _lassoRect.Width = w;
        _lassoRect.Height = h;
        
        var lassoRect = new Windows.Foundation.Rect(x, y, w, h);
        
        if (_vwg == null) return;
        
        foreach (var child in _vwg.Children)
        {
            if (child is Microsoft.UI.Xaml.Controls.ContentPresenter cp && cp.Tag is string sid && !sid.StartsWith("DEL_"))
            {
                var sound = _vm.AllSounds.FirstOrDefault(s => s.Id == sid);
                if (sound == null) continue;
                
                try {
                    var ttv = cp.TransformToVisual(_lassoCanvas);
                    var bounds = ttv.TransformBounds(new Windows.Foundation.Rect(0, 0, cp.ActualWidth, cp.ActualHeight));
                    
                    if (lassoRect.Left < bounds.Right && lassoRect.Right > bounds.Left &&
                        lassoRect.Top < bounds.Bottom && lassoRect.Bottom > bounds.Top)
                    {
                        if (!_selectedSounds.Contains(sound))
                        {
                            _selectedSounds.Add(sound);
                            if (cp.Content is Border bg && bg.Child is Grid g)
                            {
                                var overlay = g.Children.OfType<Microsoft.UI.Xaml.FrameworkElement>().FirstOrDefault(x => x.Name == "selectionOverlay") as Border;
                                if (overlay != null) overlay.Visibility = Visibility.Visible;
                            }
                        }
                    }
                    else if (!_preLassoSelection.Contains(sound))
                    {
                        if (_selectedSounds.Contains(sound))
                        {
                            _selectedSounds.Remove(sound);
                            if (cp.Content is Border bg && bg.Child is Grid g)
                            {
                                var overlay = g.Children.OfType<Microsoft.UI.Xaml.FrameworkElement>().FirstOrDefault(x => x.Name == "selectionOverlay") as Border;
                                if (overlay != null) overlay.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                } catch { }
            }
        }
        UpdateMultiSelectBar();
    }

    private void GridContainer_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_isLassoing)
        {
            _isLassoing = false;
            _lassoScrollTimer.Stop();
            _lassoRect.Visibility = Visibility.Collapsed;
            ((Grid)sender).ReleasePointerCapture(e.Pointer);
        }
    }

    private void UpdateMultiSelectBar()
    {
        bool show = _selectedSounds.Count > 0;
        if (show) _multiSelectText.Text = $"{_selectedSounds.Count} {I18n.T("sons sélectionnés")}";
        
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = show ? 1 : 0, Duration = new Microsoft.UI.Xaml.Duration(System.TimeSpan.FromMilliseconds(250)) };
        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = show ? 0 : 50, Duration = new Microsoft.UI.Xaml.Duration(System.TimeSpan.FromMilliseconds(250)), EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, _multiSelectBar);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, _multiSelectBar);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        sb.Children.Add(fade); sb.Children.Add(slide);
        
        if (show) _multiSelectBar.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        else sb.Completed += (_, _) => { if (_selectedSounds.Count == 0) _multiSelectBar.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; };
        sb.Begin();
    }
    
    private void ClearSelection()
    {
        _selectedSounds.Clear();
        UpdateMultiSelectBar();
        if (_vwg == null) return;
        foreach (var child in _vwg.Children)
        {
            if (child is ContentPresenter cp && cp.Content is Border bg && bg.Child is Grid cardGrid)
            {
                var overlay = cardGrid.Children.OfType<Border>().FirstOrDefault(b => b.Name == "selectionOverlay");
                if (overlay != null) overlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async System.Threading.Tasks.Task DeleteMultiSelected()
    {
        if (_selectedSounds.Count == 0) return;
        if (await Confirm($"{I18n.T("Supprimer")} {_selectedSounds.Count} {I18n.T("sons ?")}\n({I18n.T("Les fichiers audio ne sont PAS supprimés.")})"))
        {
            foreach(var s in _selectedSounds) _vm.RemoveSound(s);
            ClearSelection();
            RefreshGrid();
        }
    }

    private async System.Threading.Tasks.Task MoveMultiSelected()
    {
        if (_selectedSounds.Count == 0) return;
        var combo = new ComboBox { ItemsSource = _vm.Collections.ToList(), SelectedItem = _vm.ActiveCollection };
        if (await Dlg(I18n.T("Déplacer vers"), combo) && combo.SelectedItem is string col)
        {
            foreach(var s in _selectedSounds) s.Collection = col;
            _vm.Save();
            ClearSelection();
            RefreshGrid();
        }
    }

    private FrameworkElement BuildMain()
    {
        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // vb banner
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // header
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // search
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });               // now playing

        // VB banner
        _vbBanner = new Border { Background = B(RED_DIM), BorderBrush = B(RED), BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(20,8,20,8), Visibility = Visibility.Collapsed };
        var vbRow = new Grid();
        vbRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        vbRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var vbMsg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        vbMsg.Children.Add(new TextBlock { Text = "⚠️", FontSize = 13, Foreground = B(RED), VerticalAlignment = VerticalAlignment.Center });
        vbMsg.Children.Add(new TextBlock { Text = I18n.T("VB-Cable non installé — routage micro indisponible."), Foreground = MainWindow.BR_TXT, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var vbBtn = SmallBtn(I18n.T("Installer"), RED, true);
        vbBtn.Click += async (_, _) => await InstallVbCable();
        Grid.SetColumn(vbMsg, 0); Grid.SetColumn(vbBtn, 1);
        vbRow.Children.Add(vbMsg); vbRow.Children.Add(vbBtn);
        _vbBanner.Child = vbRow;
        Grid.SetRow(_vbBanner, 0); main.Children.Add(_vbBanner);

        // Header � m�me hauteur que logoRow sidebar (44px), pas de margin
        var header = new Grid
        {
            Height = 44, Padding = new Thickness(12, 0, 20, 0),
            BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(0, 0, 0, 0)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // hamburger
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // title
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });      // actions

        // Hamburger button
        var hamburger = new Button
        {
            Content = new TextBlock { Text = "☰", FontSize = 16, Foreground = MainWindow.BR_TXT2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
            Background = B(TRANSP), BorderThickness = new Thickness(0),
            Width = 36, Height = 36, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        hamburger.Click += (_, _) => ToggleSidebar();
        Grid.SetColumn(hamburger, 0);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _titleRow = titleRow;
        _mainTitle = new TextBlock { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = MainWindow.BR_TXT, VerticalAlignment = VerticalAlignment.Center };
        _mainCount = new TextBlock { FontSize = 13, Foreground = MainWindow.BR_TXT3, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(_mainTitle); titleRow.Children.Add(_mainCount);
        Grid.SetColumn(titleRow, 1);

        _actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var stopBtn = new Border
        {
            Child = new StackPanel { 
                Orientation = Orientation.Horizontal, 
                Spacing = 6, 
                VerticalAlignment = VerticalAlignment.Center, 
                Children = {
                    new FontIcon { Glyph = "\uE73B", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 10, Foreground = B(RED), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = I18n.T("Stop tout"), FontSize = 12, Foreground = MainWindow.BR_TXT2, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
                }
            },
            Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        stopBtn.PointerEntered += (_, _) => stopBtn.Background = new SolidColorBrush(_vm.Config.AccentColor == "blanc" && (_vm.Config.BackdropMode == "solid" || string.IsNullOrEmpty(_vm.Config.BackdropMode)) ? Windows.UI.Color.FromArgb(20, 0, 0, 0) : Windows.UI.Color.FromArgb(25, 255, 255, 255));
        stopBtn.PointerExited += (_, _) => stopBtn.Background = MainWindow.BR_GLASSY;
        stopBtn.Tapped += (_, _) => { _vm.StopAllSounds(); _activeSoundEngineIds.Clear(); RefreshGrid(); UpdateNowPlaying(); };
        AttachButtonAnimations(stopBtn);

        var addBtn = new Border
        {
            Child = new StackPanel { 
                Orientation = Orientation.Horizontal, 
                Spacing = 6, 
                VerticalAlignment = VerticalAlignment.Center, 
                Children = {
                    new FontIcon { Glyph = "\uE710", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 11, Foreground = MainWindow.BR_TXT, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = I18n.T("Ajouter"), FontSize = 12, Foreground = MainWindow.BR_TXT, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
                }
            },
            Background = MainWindow.BR_ACCENT_TRANSLUCENT, BorderBrush = MainWindow.BR_ACCENT, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        addBtn.PointerEntered += (_, _) => addBtn.Background = new SolidColorBrush(_vm.Config.AccentColor == "blanc" && (_vm.Config.BackdropMode == "solid" || string.IsNullOrEmpty(_vm.Config.BackdropMode)) ? Windows.UI.Color.FromArgb(30, 0, 0, 0) : Windows.UI.Color.FromArgb(40, 255, 255, 255));
        addBtn.PointerExited += (_, _) => addBtn.Background = MainWindow.BR_ACCENT_TRANSLUCENT;
        addBtn.Tapped += async (_, _) => await AddSounds();
        AttachButtonAnimations(addBtn);

        _actionRow.Children.Add(stopBtn); _actionRow.Children.Add(addBtn);
        Grid.SetColumn(_actionRow, 2);
        header.Children.Add(hamburger); header.Children.Add(titleRow); header.Children.Add(_actionRow);
        Grid.SetRow(header, 1); main.Children.Add(header);

        // Search
        _searchBox = new TextBox
        {
            PlaceholderText = "\U0001F50D   " + I18n.T("Rechercher un son..."),
            Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Foreground = MainWindow.BR_TXT,
            Padding = new Thickness(14,10,14,10), FontSize = 13, Margin = new Thickness(16,2,20,4)
        };
        _searchBox.TextChanged += (_, _) => { _vm.SearchQuery = _searchBox.Text; RefreshGrid(); };
        Grid.SetRow(_searchBox, 2); main.Children.Add(_searchBox);

        // Grid area
        _pageContainer = new Microsoft.UI.Xaml.Controls.ContentControl { HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch, VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch, ContentTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection() };
        
        _gridContainer = new Microsoft.UI.Xaml.Controls.Grid();
        _gridContainer.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0));
        _gridContainer.PointerPressed += GridContainer_PointerPressed;
        _gridContainer.PointerMoved += GridContainer_PointerMoved;
        _gridContainer.PointerReleased += GridContainer_PointerReleased;
        _gridContainer.Children.Add(_pageContainer);
        
        _lassoScrollTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(16) };
        _lassoScrollTimer.Tick += (_, _) =>
        {
            if (!_isLassoing) { _lassoScrollTimer.Stop(); return; }
            double edge = 50;
            double step = 20;
            if (_lastLassoPointer.Y < edge) _gridScroll.ChangeView(null, _gridScroll.VerticalOffset - step, null, true);
            else if (_lastLassoPointer.Y > _gridContainer.ActualHeight - edge) _gridScroll.ChangeView(null, _gridScroll.VerticalOffset + step, null, true);
            UpdateLassoRect();
        };
        _lassoCanvas = new Microsoft.UI.Xaml.Controls.Canvas { IsHitTestVisible = false };
        _lassoRect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(50, 0, 120, 215)),
            Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215)),
            StrokeThickness = 1,
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed
        };
        _lassoCanvas.Children.Add(_lassoRect);
        
        _multiSelectBar = new Microsoft.UI.Xaml.Controls.Border
        {
            Background = MainWindow.BR_CARD,
            BorderBrush = MainWindow.BR_ACCENT,
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Bottom,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 20),
            Padding = new Microsoft.UI.Xaml.Thickness(16, 8, 16, 8),
            Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
            RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform { Y = 50 },
            Opacity = 0
        };
        
        var msStack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 16 };
        _multiSelectText = new Microsoft.UI.Xaml.Controls.TextBlock { VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        msStack.Children.Add(_multiSelectText);
        
        var delBtn = new Microsoft.UI.Xaml.Controls.Button { Content = I18n.T("Supprimer"), Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red) };
        delBtn.Click += async (_, _) => await DeleteMultiSelected();
        msStack.Children.Add(delBtn);
        
        var moveBtn = new Microsoft.UI.Xaml.Controls.Button { Content = I18n.T("Déplacer") };
        moveBtn.Click += async (_, _) => await MoveMultiSelected();
        msStack.Children.Add(moveBtn);
        
        var closeBtn = new Microsoft.UI.Xaml.Controls.Button { Content = "❌", Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Microsoft.UI.Xaml.Thickness(0) };
        closeBtn.Click += (_, _) => ClearSelection();
        msStack.Children.Add(closeBtn);
        
        _multiSelectBar.Child = msStack;
        Grid.SetRow(_multiSelectBar, 3);
        main.Children.Add(_multiSelectBar);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_multiSelectBar, 99);
        
        var gridArea = _gridContainer;
        _emptyState = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 12 };
        _emptyState.Children.Add(new TextBlock { Text = "📭", FontSize = 52, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.2 });
        _emptyState.Children.Add(new TextBlock { Text = I18n.T("Aucun son dans cette collection"), Foreground = MainWindow.BR_TXT2, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center });
        _emptyState.Children.Add(new TextBlock { Text = I18n.T("Cliquez sur « + Ajouter » ou glissez des fichiers audio ici"), Foreground = MainWindow.BR_TXT3, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });

        _soundGrid = new Grid();
        
        main.AllowDrop = true;
        main.DragOver += (_, e) => 
        {
            if (e.DataView.Properties.ContainsKey("soundId"))
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                e.DragUIOverride.IsCaptionVisible = false;
                e.DragUIOverride.IsGlyphVisible = false;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = I18n.T("Ajouter le son");
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        };
        main.Drop += async (_, e) =>
        {
            if (e.DataView.Properties.ContainsKey("soundId")) return;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
                if (AudioExts().Contains(IO.Path.GetExtension(item.Path)))
                    _vm.AddSound(item.Path);
            RefreshGrid();
        };
        _gridScroll    = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _soundGrid };
        _soundCount    = new TextBlock(); // compat
        _statusBar     = new TextBlock(); // compat
        _pcBrowserPanel = new PcBrowserPanel(_vm) { Visibility = Visibility.Collapsed };
        _onlineSearchPanel = new OnlineSearchPanel(_vm, () => RefreshGrid()) { Visibility = Visibility.Collapsed };
        _settingsPanel = new SettingsPanel(_vm,
            () => { _vm.RegisterAllHotkeys(_hwnd); UpdateEngineStatus(); },
            () => { ApplyTheme(); },
            () => { RebuildTabs(); RefreshGrid(); }) { Visibility = Visibility.Collapsed };
        _pageContainer.Content = _gridScroll;
        Grid.SetRow(gridArea, 3); main.Children.Add(gridArea);

        // Now playing
        var np = BuildNowPlayingBar();
        Grid.SetRow(np, 4); main.Children.Add(np);
        return main;
    }

    private Border BuildNowPlayingBar()
    {
        _npBar = new Border
        {
            Background = BR_NP_BG, BorderBrush = MainWindow.BR_ACCENT_DIM,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Height = 64,
            Visibility = Visibility.Collapsed,
            RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform()
        };

        // Layout: [name+sub 200] [play/pause Auto] [seekGroup *] [?? Auto] [vol 110]
        // Le play/pause est � GAUCHE de la barre timecode, entre le nom et le seek.
        var row = new Grid { Margin = new Thickness(6, 0, 14, 0), ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 300 }); // name+sub
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // play/pause
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // seek group
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // ??
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });                    // vol slider
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });                    // close btn

        // Name + subtitle
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        _npName = new TextBlock
        {
            Text = I18n.T("Aucun son en lecture"), FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = MainWindow.BR_TXT3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220
        };
        _npSub = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
            MaxWidth = 220
        };
        _npTime = new TextBlock { Visibility = Visibility.Collapsed }; // compat
        info.Children.Add(_npName);
        info.Children.Add(_npSub);
        Grid.SetColumn(info, 0);

        // Play/Pause button
        _npPlayPauseIcon = new Microsoft.UI.Xaml.Controls.FontIcon
        {
            Glyph = "\uF5B0", FontSize = 14, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = B(WHITE),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin = new Thickness(1.5, 0, 0, 0),
        };
        // Wrap l'icône dans une Grid pour un centrage parfait du glyphe (le ? a un bearing � gauche)
        var ppHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin = new Thickness(0), // removed margin
        };
        ppHost.Children.Add(_npPlayPauseIcon);
        _npPlayPauseBtn = new Button
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18),
            Background = MainWindow.BR_ACCENT, BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Content = ppHost,
            IsEnabled = false
        };
        _npPlayPauseBtn.Click += async (_, _) =>
        {
            var playing = _vm.AllSounds.FirstOrDefault(s => s.IsPlaying || s.IsPaused);
            if (playing != null)
            {
                _vm.TogglePause(playing);
            }
            else
            {
                // Relancer le dernier son joué (qui a encore une Tag callback)
                var last = _vm.AllSounds.FirstOrDefault(s => s.Tag != null);
                if (last != null)
                {
                    await _vm.PlaySoundAsync(last);
                    var eid = _vm.Engine.GetActiveEngineId(last.Filepath);
                    if (eid != null) _activeSoundEngineIds[last.Id] = eid;
                    (last.Tag as Action<bool>)?.Invoke(last.IsPlaying);
                }
            }
            RefreshGrid(); UpdateNowPlaying();
        };
        Grid.SetColumn(_npPlayPauseBtn, 1);

        // -- Seek group : [timeLeft Auto] [barre *] [timeRight Auto] ----------
        // Légère marge droite pour laisser respirer le volume � droite,
        // et marge gauche légèrement réduite pour coller un peu vers le centre.
        var seekGroup = new Grid { ColumnSpacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        seekGroup.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // time left
        seekGroup.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // seek bar
        seekGroup.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // time right

        // Time left label � col 0 du seekGroup
        _npTimeLeft = new TextBlock
        {
            Text = "0:00", FontSize = 10, Foreground = MainWindow.BR_TXT3,
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6,
            MinWidth = 32, TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(_npTimeLeft, 0);
        seekGroup.Children.Add(_npTimeLeft);

        // -- Seek bar custom ----------------------------------------------------
        // On n'utilise PAS Slider : son PointerReleased est avalé par son propre
        // template et la valeur lue dans ValueChanged est incorrecte au premier clic.
        // � la place : un Grid avec une piste (Rectangle gris), un fill (Rectangle
        // accent) et un Rectangle transparent par-dessus qui capte PointerPressed +
        // PointerMoved + PointerReleased et calcule le ratio x/width directement.
        _npSeekTrack = new Grid
        {
            Height = 4,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = true,
            Margin = new Thickness(0)
        };
        // Piste de fond
        var seekBg = new Rectangle
        {
            Fill = B(C(50, 50, 75)), RadiusX = 2, RadiusY = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        // Fill progress
        _npSeekFill = new Rectangle
        {
            Fill = MainWindow.BR_ACCENT, RadiusX = 2, RadiusY = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        // Overlay transparent pour capturer les événements pointer
        var seekOverlay = new Rectangle
        {
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, -10, 0, -10) // zone de clic élargie verticalement
        };

        _npSeekTrack.Children.Add(seekBg);
        _npSeekTrack.Children.Add(_npSeekFill);
        _npSeekTrack.Children.Add(seekOverlay);

        // Calcule le ratio 0-1 depuis la position pointer X
        double SeekRatio(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(_npSeekTrack).Position;
            return Math.Clamp(pt.X / Math.Max(_npSeekTrack.ActualWidth, 1), 0, 1);
        }

        void ApplySeek(double ratio)
        {
            if (_currentPlayingEid == null) return;
            var total = _vm.Engine.GetTotalTime(_currentPlayingEid);
            if (total <= 0) return;
            double pos = ratio * total;
            _vm.Engine.SeekSound(_currentPlayingEid, pos);
            _npTimeLeft.Text = FmtTime(pos);
            // Mettre � jour le fill visuellement tout de suite
            _npSeekFill.Width = ratio * _npSeekTrack.ActualWidth;
        }

        seekOverlay.PointerPressed += (_, e) =>
        {
            _userDraggingSeek = true;
            seekOverlay.CapturePointer(e.Pointer);
            // Mettre � jour visuellement le fill imm�diatement, mais NE PAS seeker
            var ratio = SeekRatio(e);
            _npSeekFill.Width = ratio * _npSeekTrack.ActualWidth;
            if (_currentPlayingEid != null)
            {
                var total = _vm.Engine.GetTotalTime(_currentPlayingEid);
                if (total > 0) _npTimeLeft.Text = FmtTime(ratio * total);
            }
        };
        seekOverlay.PointerMoved += (_, e) =>
        {
            if (!_userDraggingSeek) return;
            // Mise � jour visuelle uniquement � le seek réel est dans PointerReleased
            var ratio = SeekRatio(e);
            _npSeekFill.Width = ratio * _npSeekTrack.ActualWidth;
            if (_currentPlayingEid != null)
            {
                var total = _vm.Engine.GetTotalTime(_currentPlayingEid);
                if (total > 0) _npTimeLeft.Text = FmtTime(ratio * total);
            }
        };
        seekOverlay.PointerReleased += (_, e) =>
        {
            if (!_userDraggingSeek) return;
            _userDraggingSeek = false;
            seekOverlay.ReleasePointerCapture(e.Pointer);
            // Seek uniquement ici, après que l'user a rel�ch�
            ApplySeek(SeekRatio(e));
        };
        seekOverlay.PointerCaptureLost += (_, _) => { _userDraggingSeek = false; };

        Grid.SetColumn(_npSeekTrack, 1);
        seekGroup.Children.Add(_npSeekTrack);

        // Time right label
        _npTimeRight = new TextBlock
        {
            Text = "0:00", FontSize = 10, Foreground = MainWindow.BR_TXT3,
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6
        };
        Grid.SetColumn(_npTimeRight, 2);
        seekGroup.Children.Add(_npTimeRight);
        Grid.SetColumn(seekGroup, 2);

        // Volume icon
        var volIcon = new FontIcon
        {
            Glyph = "\uE767", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Foreground = MainWindow.BR_TXT,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        volIcon.PointerEntered += (_, _) => volIcon.Foreground = MainWindow.BR_ACCENT;
        volIcon.PointerExited += (_, _) => volIcon.Foreground = _npVolSlider != null && _npVolSlider.Value <= 0 ? B(RED) : MainWindow.BR_TXT;
        Grid.SetColumn(volIcon, 3);

        // Per-sound volume slider (contrôle le volume du son actuellement joué)
        var closeBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10, Foreground = MainWindow.BR_TXT3 },
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0)
        };
        closeBtn.Click += (_, _) => {
            var active = _vm.AllSounds.FirstOrDefault(s => s.IsPlaying || s.IsPaused);
            if (active != null) _vm.StopSound(active);
        };
        
        var volContainer = new Grid();
        
        _npVolSlider = new Slider
        {
            Minimum = 0, Maximum = 150, StepFrequency = 1,
            Value = 100,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MainWindow.BR_ACCENT,
            Margin = new Thickness(0, 0, 0, 0)
        };
        _npVolSlider.ValueChanged += (_, e) =>
        {
            volIcon.Glyph = e.NewValue <= 0 ? "\uE74F" : "\uE767";
            volIcon.Foreground = e.NewValue <= 0 ? B(RED) : MainWindow.BR_TXT;
            float v = (float)(e.NewValue / 100.0);
            var playing = _vm.AllSounds.FirstOrDefault(s => s.IsPlaying || s.IsPaused);
            if (playing == null) return;
            playing.Volume = v;
            _vm.Save();
            if (_activeSoundEngineIds.TryGetValue(playing.Id, out var eid))
                _vm.Engine.SetSoundVolume(eid, v);
            if (_soundVolCallbacks.TryGetValue(playing.Id, out var volCb))
                volCb(v);
        };
        double prevVol = 100;
        volIcon.Tapped += (_, _) =>
        {
            if (_npVolSlider.Value > 0)
            {
                prevVol = _npVolSlider.Value;
                _npVolSlider.Value = 0;
            }
            else
            {
                _npVolSlider.Value = prevVol > 0 ? prevVol : 100;
            }
        };
        volContainer.Children.Add(_npVolSlider);
        Grid.SetColumn(volContainer, 4);

        Grid.SetColumn(closeBtn, 5);

        row.Children.Add(info);
        row.Children.Add(seekGroup);
        row.Children.Add(_npPlayPauseBtn);
        row.Children.Add(volIcon);
        row.Children.Add(volContainer);
        row.Children.Add(closeBtn);
        _npBar.Child = row;

        // Timer pour mise � jour de la seek bar
        _npTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _npTimer.Tick += (_, _) => TickNowPlaying();
        _npTimer.Start();

        return _npBar;
    }

    private static string FmtTime(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.Hours > 0 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void TickNowPlaying()
    {
        // D�l�gue la d�tection de fin naturelle au ViewModel
        // (CleanupFinishedSounds dans l'engine + PlaybackChanged ? RefreshGrid)
        _vm.PollPlayback();

        // Synchroniser le cache view _activeSoundEngineIds avec l'�tat du ViewModel
        var playing = _vm.AllSounds.FirstOrDefault(x => x.IsPlaying);
        if (playing == null)
        {
            _activeSoundEngineIds.Clear();
            _currentPlayingEid = null;
            return;
        }

        // S'assurer qu'on a l'engineId dans notre cache (utile pour sons lanc�s par raccourci)
        if (!_activeSoundEngineIds.TryGetValue(playing.Id, out var peid))
        {
            peid = _vm.Engine.GetActiveEngineId(playing.Filepath);
            if (peid != null)
                _activeSoundEngineIds[playing.Id] = peid;
        }
        if (peid == null)
        {
            _currentPlayingEid = null;
            return;
        }
        _currentPlayingEid = peid;
        var cur   = _vm.Engine.GetCurrentTime(peid);
        var total = _vm.Engine.GetTotalTime(peid);
        if (total <= 0) return;
        // Ne pas �craser pendant que l'user drag
        if (!_userDraggingSeek)
        {
            double ratio = cur / total;
            _npSeekFill.Width = ratio * _npSeekTrack.ActualWidth;
            _npTimeLeft.Text  = FmtTime(cur);
        }
        _npTimeRight.Text = FmtTime(total);
    }

    private void UpdateNowPlaying()
    {
        var playing = _vm.AllSounds.FirstOrDefault(s => s.IsPlaying || s.IsPaused);
        if (playing == null)
        {
            if (_npBar.Visibility != Visibility.Collapsed)
            {
                var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var trans = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation 
                { 
                    Duration = new Microsoft.UI.Xaml.Duration(System.TimeSpan.FromMilliseconds(200)),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn },
                    To = 64
                };
                var opac = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { Duration = new Microsoft.UI.Xaml.Duration(System.TimeSpan.FromMilliseconds(200)), To = 0 };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(trans, _npBar);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(trans, "(UIElement.RenderTransform).(TranslateTransform.Y)");
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opac, _npBar);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opac, "Opacity");
                sb.Children.Add(trans); sb.Children.Add(opac);
                sb.Completed += (_, _) => { if (_vm.AllSounds.All(s => !s.IsPlaying && !s.IsPaused)) _npBar.Visibility = Visibility.Collapsed; };
                sb.Begin();
            }
            
            _currentPlayingEid = null;
            _npName.Text = I18n.T("Aucun son en lecture");
            _npName.Foreground = MainWindow.BR_TXT3;
            _npSub.Visibility = Visibility.Collapsed;
            _npPlayPauseIcon.Glyph = "\uF5B0";
            _npPlayPauseIcon.Margin = new Thickness(1.5, 0, 0, 0);
            _npPlayPauseBtn.IsEnabled = false;
            _npSeekFill.Width = 0;
            _npTimeLeft.Text  = "0:00";
            _npTimeRight.Text = "0:00";
        }
        else
        {
            if (_npBar.Visibility == Visibility.Collapsed)
            {
                if (_npBar.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform t) t.Y = 64;
                _npBar.Opacity = 0;
                _npBar.Visibility = Visibility.Visible;
                var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var trans = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation 
                { 
                    Duration = new Microsoft.UI.Xaml.Duration(System.TimeSpan.FromMilliseconds(250)),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    To = 0
                };
                var opac = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { Duration = new Microsoft.UI.Xaml.Duration(System.TimeSpan.FromMilliseconds(250)), To = 1 };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(trans, _npBar);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(trans, "(UIElement.RenderTransform).(TranslateTransform.Y)");
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opac, _npBar);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opac, "Opacity");
                sb.Children.Add(trans); sb.Children.Add(opac);
                sb.Begin();
            }
            
            _npName.Text = playing.Name;
            _npName.Foreground = MainWindow.BR_TXT;
            _npSub.Children.Clear();
            _npSub.Children.Add(new TextBlock { Text = playing.Collection, FontSize = 11, Foreground = MainWindow.BR_TXT3, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 });
            if (playing.Loop || playing.Favorite) {
                _npSub.Children.Add(new TextBlock { Text = " · ", FontSize = 11, Foreground = MainWindow.BR_TXT3, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                if (playing.Loop) _npSub.Children.Add(new FontIcon { Glyph = "\uE8EE", FontSize = 13, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Margin = new Thickness(0, 1, playing.Favorite ? 4 : 0, 0) });
                if (playing.Favorite) _npSub.Children.Add(new FontIcon { Glyph = "\uE735", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 11, Foreground = B(YELLOW), VerticalAlignment = VerticalAlignment.Center });
            }
            _npSub.Visibility = Visibility.Visible;
            _npPlayPauseIcon.Glyph = playing.IsPaused ? "\uF5B0" : "\uF8AE";
            _npPlayPauseIcon.Margin = playing.IsPaused ? new Thickness(1.5, 0, 0, 0) : new Thickness(1, 0, 0, 0);
            _npPlayPauseBtn.IsEnabled = true;
            // Essayer le cache view d'abord, puis fallback sur l'engine
            // (utile quand le son est lanc� par raccourci, sans passer par le clic UI)
            var eid = _activeSoundEngineIds.TryGetValue(playing.Id, out var cached)
                ? cached
                : _vm.Engine.GetActiveEngineId(playing.Filepath);
            if (eid != null)
            {
                // Synchroniser le cache pour les prochains appels
                _activeSoundEngineIds[playing.Id] = eid;
                _currentPlayingEid = eid;
                _npTimeRight.Text = FmtTime(_vm.Engine.GetTotalTime(eid));
            }
            // Synchroniser le slider volume du player avec le volume du son joué
            _npVolSlider.Value = Math.Min(playing.Volume * 100, 150);
        }
    }

    private Button SmallBtn(string text, Windows.UI.Color accent, bool filled) => new()
    {
        Content = text,
        Background  = filled ? B(accent) : B(TRANSP),
        Foreground  = filled ? B(WHITE)  : B(accent),
        BorderBrush = filled ? B(TRANSP) : B(accent),
        BorderThickness = new Thickness(filled ? 0 : 1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 7, 14, 7), FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
    };

    private MenuFlyout BuildContextMenu()
    {
        var m = new MenuFlyout();
        // Style the flyout presenter (background/border) to match theme
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, MainWindow.BR_CARD));
        presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, MainWindow.BR_ACCENT_DIM));
        presenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        m.MenuFlyoutPresenterStyle = presenterStyle;

        MIFluent(m, "\uF5B0", I18n.T("Lire"), async (_, _) =>
        {
            if (_contextSound == null) return;
            await _vm.PlaySoundAsync(_contextSound);
            var eid = _vm.Engine.GetActiveEngineId(_contextSound.Filepath);
            if (eid != null) _activeSoundEngineIds[_contextSound.Id] = eid;
            (_contextSound.Tag as Action<bool>)?.Invoke(_contextSound.IsPlaying);
            UpdateNowPlaying();
        }, new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 204, 113)), 12);

        MIFluent(m, "\uF8AE", I18n.T("Arrêter"), (_, _) =>
        {
            if (_contextSound == null) return;
            _vm.StopSound(_contextSound);
            _activeSoundEngineIds.Remove(_contextSound.Id);
            (_contextSound.Tag as Action<bool>)?.Invoke(false);
            UpdateNowPlaying();
        }, new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 75, 75)), 12);

        m.Items.Add(new MenuFlyoutSeparator());
        MI(m, "✏️", I18n.T("Renommer"),            async (_, _) => await RenameSound(), 13.5);
        MI(m, "⌨️", I18n.T("Définir raccourci"),  async (_, _) => await SetHotkey(), 13.5);
        MI(m, "📁", I18n.T("Changer le fichier"), async (_, _) => await ChangeFile(), 13.5);
        MI(m, "📂", I18n.T("Déplacer vers..."),     async (_, _) => await MoveSound(), 13.5);
        m.Items.Add(new MenuFlyoutSeparator());
        MI(m, "⭐", I18n.T("Favori"),              (_, _) => { if (_contextSound != null) { _contextSound.Favorite = !_contextSound.Favorite; _vm.Save(); RefreshGrid(true); UpdateNowPlaying(); } }, 13.5);
        MIFluent(m, "\uE8EE", I18n.T("Boucle on/off"),      (_, _) => { if (_contextSound != null) { _contextSound.Loop = !_contextSound.Loop; _vm.Save(); RefreshGrid(true); UpdateNowPlaying(); } }, null, 12);
        m.Items.Add(new MenuFlyoutSeparator());
        
        var del = new MenuFlyoutItem { 
            Text = I18n.T("Supprimer"), 
            Foreground = B(RED),
            Icon = new FontIcon { Glyph = "🗑️", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1.5, 0, 0) }
        };
        if (App.Current.Resources.TryGetValue("SoundDeckMenuItemStyle", out var sDel) && sDel is Style stDel) del.Style = stDel;
        del.Click += async (_, _) => await DeleteSound();
        m.Items.Add(del);
        return m;
    }

    // --------------------------- GRID -----------------------------------------
    private async void RefreshGrid(bool fullRebuild = false)
    {
        _soundVolCallbacks.Clear();
        foreach (var s in _vm.AllSounds)
        {
            if (_activeSoundEngineIds.TryGetValue(s.Id, out var eid))
                s.IsPlaying = _vm.Engine.IsSoundPlaying(eid);
            else
                s.IsPlaying = false;
        }

        _vm.RefreshFilteredSounds();

        bool onSounds = _currentPage == Page.Sounds;
        if (_mainTitle != null && onSounds)
            _mainTitle.Text = _vm.ActiveCollection == "__FAVORITES__" ? I18n.T("Favoris") : _vm.ActiveCollection;

        var sounds = _vm.FilteredSounds.ToList();
        int count  = sounds.Count;
        
        if (onSounds)
        {
            if (count == 0 && _pageContainer.Content != _emptyState) { _pageContainer.Content = _emptyState; _emptyState.Visibility = Visibility.Visible; }
            else if (count > 0 && _pageContainer.Content != _gridScroll) { _pageContainer.Content = _gridScroll; _gridScroll.Visibility = Visibility.Visible; }
        }
        
        if (_mainCount != null && onSounds)
            _mainCount.Text = $"{count} {(count != 1 ? I18n.T("sons") : I18n.T("son"))}";

        if (count == 0) { UpdateNowPlaying(); return; }
        if (!onSounds) { UpdateNowPlaying(); return; }

        if (fullRebuild && _vwg != null)
        {
            _soundGrid.Children.Remove(_vwg);
            _vwg = null;
        }

        bool isNewGrid = _vwg == null;
        if (_vwg == null)
        {
            _vwg = new Microsoft.UI.Xaml.Controls.VariableSizedWrapGrid
            {
                ItemWidth  = 183 + gap,
                ItemHeight = 221 + gap,
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                ChildrenTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
                {
                    new Microsoft.UI.Xaml.Media.Animation.RepositionThemeTransition()
                }
            };
            _soundGrid.ColumnDefinitions.Clear();
            _soundGrid.RowDefinitions.Clear();
            _soundGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _soundGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _soundGrid.Children.Clear();
            _soundGrid.Padding = new Thickness(18, 8, 20, 20);
            _soundGrid.Children.Add(_vwg);
            if (_lassoCanvas != null) _soundGrid.Children.Add(_lassoCanvas);
        }

        var newIds = sounds.Select(x => x.Id).ToList();

        for (int i = _vwg.Children.Count - 1; i >= 0; i--)
        {
            var cp = (ContentPresenter)_vwg.Children[i];
            if (cp.Tag is string tg && tg.StartsWith("DEL_")) continue;
            
            if (cp.Tag is string tg2 && !newIds.Contains(tg2))
            {
                cp.Tag = "DEL_" + tg2;
                var fadeOut = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 0, Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(200)) };
                var scaleX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 0.8, Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(200)) };
                var scaleY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 0.8, Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(200)) };
                
                if (!(cp.RenderTransform is Microsoft.UI.Xaml.Media.CompositeTransform))
                    cp.RenderTransform = new Microsoft.UI.Xaml.Media.CompositeTransform { CenterX = 50, CenterY = 50 };
                
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeOut, cp);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeOut, "Opacity");
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleX, cp);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleY, cp);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
                
                var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                sb.Children.Add(fadeOut); sb.Children.Add(scaleX); sb.Children.Add(scaleY);
                sb.Completed += (s, e) => { _vwg.Children.Remove(cp); };
                sb.Begin();
            }
        }

        for (int i = 0; i < sounds.Count; i++)
        {
            var s = sounds[i];
            if (_vwg.Children.Count <= i || (string)((ContentPresenter)_vwg.Children[i]).Tag != s.Id)
            {
                int existingIdx = -1;
                for (int j = i + 1; j < _vwg.Children.Count; j++)
                {
                    if ((string)((ContentPresenter)_vwg.Children[j]).Tag == s.Id)
                    {
                        existingIdx = j;
                        break;
                    }
                }

                if (existingIdx != -1)
                {
                    var cp = _vwg.Children[existingIdx];
                    _vwg.Children.RemoveAt(existingIdx);
                    _vwg.Children.Insert(i, cp);
                }
                else
                {
                    var card = BuildSoundCard(s);
                    var cp = new ContentPresenter
                    {
                        Content = card,
                        Margin = new Thickness(0, 0, gap, gap),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Tag = s.Id,
                        CanDrag = true,
                        AllowDrop = true
                    };
                    cp.DragStarting += (sender, e) => e.Data.Properties["soundId"] = s.Id;
                    cp.DragOver += (sender, e) =>
                    {
                        if (e.DataView.Properties.ContainsKey("soundId"))
                        {
                            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                            e.DragUIOverride.IsCaptionVisible = false;
                            e.DragUIOverride.IsGlyphVisible = false;
                        }
                    };
                    cp.DragEnter += (sender, e) =>
                    {
                        if (e.DataView.Properties.TryGetValue("soundId", out var idObj) && idObj is string draggedId && draggedId != s.Id)
                        {
                            var draggedCp = _vwg.Children.OfType<ContentPresenter>().FirstOrDefault(x => (string)x.Tag == draggedId);
                            if (draggedCp != null)
                            {
                                int targetIndex = _vwg.Children.IndexOf(cp);
                                int draggedIndex = _vwg.Children.IndexOf(draggedCp);
                                
                                if (targetIndex >= 0 && draggedIndex >= 0 && targetIndex != draggedIndex)
                                {
                                    _vwg.Children.RemoveAt(draggedIndex);
                                    _vwg.Children.Insert(targetIndex, draggedCp);
                                    _vm.ReorderSoundSilent(draggedId, s.Id);
                                }
                            }
                        }
                    };
                    cp.Drop += (sender, e) =>
                    {
                        _vm.Save();
                    };
                    
                    {
                        cp.Opacity = 0;
                        if (!(cp.RenderTransform is Microsoft.UI.Xaml.Media.CompositeTransform))
                            cp.RenderTransform = new Microsoft.UI.Xaml.Media.CompositeTransform { CenterX = 90, CenterY = 100, ScaleX = 0.8, ScaleY = 0.8 };
                        
                        var fadeIn = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(200)) };
                        var scaleX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0.8, To = 1, Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(200)) };
                        var scaleY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = 0.8, To = 1, Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(200)) };
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeIn, cp);
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeIn, "Opacity");
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleX, cp);
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleY, cp);
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
                        var sbIn = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                        sbIn.Children.Add(fadeIn); sbIn.Children.Add(scaleX); sbIn.Children.Add(scaleY);
                        sbIn.BeginTime = System.TimeSpan.FromMilliseconds(i * 30);
                        sbIn.Begin();
                    }

                    _vwg.Children.Insert(i, cp);
                }
            }
            
            (s.Tag as Action<bool>)?.Invoke(s.IsPlaying);
        }

        foreach (var s in sounds)
            (s.Tag as Action<bool>)?.Invoke(s.IsPlaying);

        UpdateNowPlaying();
    }

    private const int gap = 10;

    // --------------------------- SOUND CARD -----------------------------------
    private static readonly Windows.UI.Color[] CardGrads =
    {
        Windows.UI.Color.FromArgb(255,30,28,80),   // deep purple
        Windows.UI.Color.FromArgb(255,60,35,10),   // dark amber
        Windows.UI.Color.FromArgb(255,10,45,40),   // dark teal
        Windows.UI.Color.FromArgb(255,60,18,18),   // dark red
        Windows.UI.Color.FromArgb(255,40,15,60),   // deep violet
        Windows.UI.Color.FromArgb(255,10,35,60),   // navy
        Windows.UI.Color.FromArgb(255,55,35,8),    // dark orange
        Windows.UI.Color.FromArgb(255,12,50,30),   // forest green
    };
    private static readonly string[] CardEmojis = { "\U0001F3B5","🎶","🎧","🎼","🎸","🎙️","🎹","🔊" };

    private FrameworkElement BuildSoundCard(Sound sound)
    {
        bool   playing = sound.IsPlaying;
        bool   hasHk   = sound.Hotkey?.Count > 0;
        string hkText  = hasHk ? HotkeyService.ComboToDisplayString(sound.Hotkey) : "+";
        int seed;
        if (sound.CardColorIndex.HasValue && !string.IsNullOrEmpty(sound.CardEmoji))
        {
            seed = Math.Clamp(sound.CardColorIndex.Value, 0, CardGrads.Length - 1);
        }
        else
        {
            int stableHash = 0;
            foreach (char ch in (sound.Id ?? sound.Name ?? "sound")) stableHash = (stableHash * 31 + ch) & 0x7FFFFFFF;
            seed = stableHash % CardGrads.Length;
            sound.CardColorIndex = seed;
            sound.CardEmoji = CardEmojis[seed];
            _vm.Save();
        }
        var gradCol = CardGrads[seed];
        string emoji = sound.CardEmoji ?? CardEmojis[seed];

        var cardBg = new Border
        {
            CornerRadius    = new CornerRadius(12),
            Background      = playing ? MainWindow.BR_ACCENT_TRANSLUCENT : (Brush)BR_CARD,
            BorderBrush     = playing ? MainWindow.BR_ACCENT : new SolidColorBrush(TRANSP),
            BorderThickness = new Thickness(1.5),
            Width = 180,
            DataContext = sound
        };

        var card = new Grid() { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(116) });
        
        cardBg.Child = card;
        var artGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        artGrad.GradientStops.Add(new GradientStop { Color = gradCol, Offset = 0 });
        artGrad.GradientStops.Add(new GradientStop { Color = C(
            (byte)Math.Min(gradCol.R + 30, 255),
            (byte)Math.Min(gradCol.G + 30, 255),
            (byte)Math.Min(gradCol.B + 30, 255)), Offset = 1 });

        var art = new Border { CornerRadius = new CornerRadius(11, 11, 0, 0), Background = artGrad };
        var artGrid = new Grid();

        var emojiTb = new TextBlock
        {
            Text = emoji, FontSize = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Opacity = playing ? 0.5 : 0.80,
            IsHitTestVisible = false
        };
        artGrid.Children.Add(emojiTb);

        // Cover art: show cached cover or fetch in background
        if (!string.IsNullOrEmpty(sound.CoverUrl) && System.IO.File.Exists(sound.CoverUrl))
        {
            try
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(sound.CoverUrl));
                art.Background = new Microsoft.UI.Xaml.Media.ImageBrush
                {
                    ImageSource = bmp,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
                };
                emojiTb.Visibility = Visibility.Collapsed;
            }
            catch { }
        }
        else if (_vm.Config.AutoFetchCovers && !string.IsNullOrWhiteSpace(sound.Name))
        {
            var capturedArt = art;
            var capturedEmoji = emojiTb;
            var capturedSound = sound;
            _ = Task.Run(async () =>
            {
                var coverPath = await CoverArtService.GetCoverAsync(capturedSound.Name);
                if (coverPath != null)
                {
                    capturedSound.CoverUrl = coverPath;
                    _vm.Save();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(coverPath));
                            capturedArt.Background = new Microsoft.UI.Xaml.Media.ImageBrush
                            {
                                ImageSource = bmp,
                                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
                            };
                            capturedEmoji.Visibility = Visibility.Collapsed;
                        }
                        catch { }
                    });
                }
            });
        }


        var topBadges = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            IsHitTestVisible = false
        };
        if (sound.Favorite)    topBadges.Children.Add(new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE735", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 13, Foreground = B(YELLOW) });
        if (sound.Loop)        topBadges.Children.Add(new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE8EE", FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) });
        if (!sound.FileExists) topBadges.Children.Add(new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "\uE711", FontSize = 10, Foreground = B(RED) });
        artGrid.Children.Add(topBadges);

        var overlayDark = new Border
        {
            Background   = new SolidColorBrush(Windows.UI.Color.FromArgb(playing ? (byte)70 : (byte)0, 0, 0, 0)),
            CornerRadius = new CornerRadius(11, 11, 0, 0),
            IsHitTestVisible = false
        };
        artGrid.Children.Add(overlayDark);

        var pillIconTb = new Microsoft.UI.Xaml.Controls.FontIcon
        {
            Glyph = playing ? "\uE71A" : "\uE768",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = B(C(15, 15, 35)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Margin = new Thickness(playing ? 0 : 2.5, 0, 0, 0)
        };
        var playPill = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Opacity = 0.0,
            IsHitTestVisible = false,
            Child = pillIconTb
        };
        artGrid.Children.Add(playPill);

        var hitLayer = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1, 0, 0, 0)) };
        artGrid.Children.Add(hitLayer);
        art.Child = artGrid;
        Grid.SetRow(art, 0);
        card.Children.Add(art);

        var accentLine = new Rectangle { Fill = MainWindow.BR_ACCENT, Opacity = playing ? 1.0 : 0.15 };
        Grid.SetRow(accentLine, 1);
        card.Children.Add(accentLine);

        var body = new Grid { Padding = new Thickness(10, 9, 10, 9) };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });

        var nameTb = new TextBlock
        {
            Text = sound.Name, FontSize = 12,
            FontWeight   = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground   = playing ? MainWindow.BR_ACCENT2 : MainWindow.BR_TXT,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        Grid.SetRow(nameTb, 0);
        body.Children.Add(nameTb);

        var volRow = new Grid();
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        var slider = new Slider
        {
            Minimum = 0, Maximum = 150, Value = Math.Min(sound.Volume * 100, 150), StepFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = MainWindow.BR_ACCENT,
            Margin = new Thickness(0, -6, 4, -6)
        };
        var volLbl = new TextBlock
        {
            Text = $"{(int)(sound.Volume * 100)}%", FontSize = 9,
            Foreground = MainWindow.BR_TXT3,
            VerticalAlignment   = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        slider.ValueChanged += (_, e) =>
        {
            sound.Volume = (float)(e.NewValue / 100.0);
            volLbl.Text = $"{(int)e.NewValue}%";
            if (_activeSoundEngineIds.TryGetValue(sound.Id, out var eid))
            {
                _vm.Engine.SetSoundVolume(eid, sound.Volume);
                if (_npBar.Visibility == Visibility.Visible && _currentPlayingEid == eid)
                    _npVolSlider.Value = e.NewValue;
            }
        };
        Grid.SetColumn(slider, 0); Grid.SetColumn(volLbl, 1);
        volRow.Children.Add(slider); volRow.Children.Add(volLbl);
        Grid.SetRow(volRow, 2);
        body.Children.Add(volRow);

        var hkBadge = new Border
        {
            Background  = hasHk ? MainWindow.BR_ACCENT_DIM : MainWindow.BR_GLASSY,
            BorderBrush = hasHk ? MainWindow.BR_ACCENT : MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 2, 4, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Center,
            Child = hasHk
                ? (UIElement)new TextBlock
                {
                    Text = hkText, FontSize = 9,
                    Foreground  = MainWindow.BR_ACCENT2,
                    FontFamily  = new FontFamily("Cascadia Code, Consolas, monospace"),
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming  = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0)
                }
                : new Microsoft.UI.Xaml.Controls.FontIcon
                {
                    Glyph = "\uE710", FontSize = 11,
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    Foreground = MainWindow.BR_TXT3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
        };
        Grid.SetRow(hkBadge, 4);
        body.Children.Add(hkBadge);

        Grid.SetRow(body, 2);
        card.Children.Add(body);

        var bodyDarkOverlay = new Border
        {
            Background   = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            CornerRadius = new CornerRadius(0, 0, 11, 11),
            IsHitTestVisible = false
        };
        Grid.SetRow(bodyDarkOverlay, 2);
        card.Children.Add(bodyDarkOverlay);

        var glowBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Opacity = 0,
            Margin = new Thickness(0),
            BorderBrush = MainWindow.BR_ACCENT
        };
        Grid.SetRowSpan(glowBorder, 3);
        card.Children.Add(glowBorder);

        var pulseSb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var pulseAnim = new DoubleAnimation
        {
            From = 0.35, To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulseAnim, glowBorder);
        Storyboard.SetTargetProperty(pulseAnim, "Opacity");
        pulseSb.Children.Add(pulseAnim);

        var shimmer = new Rectangle
        {
            Width = 60, Height = 100,
            RadiusX = 8, RadiusY = 8,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0,255,255,255), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(30,255,255,255), Offset = 0.5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0,255,255,255), Offset = 1 }
                }
            },
            RenderTransform = new TranslateTransform { X = -60 }
        };
        artGrid.Children.Add(shimmer);

        var shimmerSb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var shimmerAnim = new DoubleAnimation
        {
            From = -60, To = 200,
            Duration = new Duration(TimeSpan.FromMilliseconds(1800)),
            BeginTime = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(shimmerAnim, shimmer);
        Storyboard.SetTargetProperty(shimmerAnim, "(UIElement.RenderTransform).(TranslateTransform.X)");
        shimmerSb.Children.Add(shimmerAnim);

        var selectionOverlay = new Border
        {
            Name = "selectionOverlay",
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
            BorderBrush = B(WHITE),
            BorderThickness = new Thickness(2),
            Visibility = _selectedSounds.Contains(sound) ? Visibility.Visible : Visibility.Collapsed,
            IsHitTestVisible = false
        };
        Grid.SetRowSpan(selectionOverlay, 3);
        card.Children.Add(selectionOverlay);

        void ApplyPlayState(bool p)
        {
            cardBg.Background      = p ? MainWindow.BR_ACCENT_TRANSLUCENT : (Brush)BR_CARD;
            cardBg.BorderBrush     = p ? MainWindow.BR_ACCENT : new SolidColorBrush(TRANSP);
            cardBg.BorderThickness = new Thickness(1.5);
            accentLine.Opacity     = p ? 1.0 : 0.15;
            nameTb.Foreground      = p ? MainWindow.BR_ACCENT2 : MainWindow.BR_TXT;
            emojiTb.Opacity        = p ? 0.45 : 0.80;
            overlayDark.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(p ? (byte)80 : (byte)0, 0, 0, 0));
            // pill icon updates but opacity stays 0 (only hover shows it)
            pillIconTb.Glyph  = p ? "\uE71A" : "\uE768";
            pillIconTb.Margin = p ? new Thickness(0) : new Thickness(2, 0, 0, 0);
            playPill.Opacity  = 0.0; // always hidden when not hovering
            

            shimmer.Opacity   = p ? 1.0 : 0.0;
            if (p) { pulseSb.Begin(); shimmerSb.Begin(); }
            else   { pulseSb.Stop(); shimmerSb.Stop(); glowBorder.Opacity = 0; }
        }

        // -- Hover -------------------------------------------------------------
        card.PointerEntered += (_, _) =>
        {
            overlayDark.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(sound.IsPlaying ? (byte)90 : (byte)100, 0, 0, 0));
            playPill.Opacity = 1.0; // show pill only on hover
            bodyDarkOverlay.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 0, 0, 0));
        };
        card.PointerExited += (_, _) =>
        {
            bool p = sound.IsPlaying;
            overlayDark.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(p ? (byte)80 : (byte)0, 0, 0, 0));
            playPill.Opacity = 0.0; // always hide when mouse leaves
            bodyDarkOverlay.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        };

        // -- Play/stop (async so .opus conversion never blocks the UI) ---------
        card.Tapped += async (_, _) =>
        {
            
            bool isCtrl = (GetAsyncKeyState(0x11) & unchecked((short)0x8000)) != 0;
              bool isShift = (GetAsyncKeyState(0x10) & unchecked((short)0x8000)) != 0;
            bool isMulti = isCtrl || isShift;
            
            if (isMulti)
            {
                if (_selectedSounds.Contains(sound))
                {
                    _selectedSounds.Remove(sound);
                    selectionOverlay.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _selectedSounds.Add(sound);
                    selectionOverlay.Visibility = Visibility.Visible;
                }
                UpdateMultiSelectBar();
                return;
            }
            
            if (_selectedSounds.Count > 0) ClearSelection();

            // For .opus files, run the ffmpeg conversion on a background thread
            // before handing off to the engine � avoids UI freeze/crash.
            var ext = IO.Path.GetExtension(sound.Filepath).ToLowerInvariant();
            if (ext is ".opus")
            {
                // Temporarily disable the hit layer to prevent double-tap during conversion
                hitLayer.IsHitTestVisible = false;
                try
                {
                    // Kick off async conversion; PlaySound will use cached WAV on next call
                    await Task.Run(() => _vm.Engine.EnsureOpusConverted(sound.Filepath))
                              .ConfigureAwait(true);
                }
                catch { /* non-fatal — engine will try anyway */ }
                finally { hitLayer.IsHitTestVisible = true; }
            }
            // Toggle play/stop
            bool wasPlaying = _activeSoundEngineIds.ContainsKey(sound.Id) &&
                              _vm.Engine.IsSoundPlaying(_activeSoundEngineIds[sound.Id]);
            if (wasPlaying)
            {
                _vm.StopSound(sound);
                _activeSoundEngineIds.Remove(sound.Id);
            }
            else
            {
                // Polyphony OFF: notify all previously playing cards that they stopped
                if (!_vm.Engine.Polyphony)
                {
                    foreach (var kvp in _activeSoundEngineIds.ToList())
                    {
                        var stoppedSound = _vm.AllSounds.FirstOrDefault(s => s.Id == kvp.Key);
                        if (stoppedSound != null && stoppedSound.Id != sound.Id)
                            (stoppedSound.Tag as Action<bool>)?.Invoke(false);
                    }
                    _activeSoundEngineIds.Clear();
                }
                await _vm.PlaySoundAsync(sound);
                var eid = _vm.Engine.GetActiveEngineId(sound.Filepath);
                if (eid != null) _activeSoundEngineIds[sound.Id] = eid;
            }
            ApplyPlayState(sound.IsPlaying);
            UpdateNowPlaying();
        };

        sound.Tag = (Action<bool>)ApplyPlayState;
        _soundVolCallbacks[sound.Id] = v => { slider.Value = Math.Min(v * 100, 150); volLbl.Text = $"{(int)(v * 100)}%"; };

        // -- Right-click -------------------------------------------------------
        void ShowCtxMenu(object? sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            _contextSound = sound;
            _contextMenu.ShowAt(card, new FlyoutShowOptions { Position = e.GetPosition(card) });
        }
        card.RightTapped += ShowCtxMenu;
        art.RightTapped  += ShowCtxMenu;

        return cardBg;
    }

    // compat
    private void RebuildTabs() => RebuildSidebar();

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        if (_sidebarElement != null && _mainContent != null)
        {
            _sidebarElement.Width = 240;
            _sidebarElement.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            if (!(_sidebarElement.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform))
                _sidebarElement.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform();
            if (!(_mainContent.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform))
                _mainContent.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform();

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var transSidebar = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation 
            { 
                Duration = new Microsoft.UI.Xaml.Duration(System.TimeSpan.FromMilliseconds(250)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } 
            };
            var transMain = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation 
            { 
                Duration = new Microsoft.UI.Xaml.Duration(System.TimeSpan.FromMilliseconds(250)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } 
            };
            
            if (_sidebarCollapsed)
            {
                transSidebar.From = 0;
                transSidebar.To = -240;
                transMain.From = 240;
                transMain.To = 0;
                sb.Completed += (_, _) => {
                    _sidebarElement.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                };
                if (_sidebarColumn != null) 
                    _sidebarColumn.Width = new Microsoft.UI.Xaml.GridLength(0);
            }
            else
            {
                _sidebarElement.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                if (_sidebarColumn != null) 
                    _sidebarColumn.Width = new Microsoft.UI.Xaml.GridLength(240);
                transSidebar.From = -240;
                transSidebar.To = 0;
                transMain.From = -240;
                transMain.To = 0;
            }
            
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(transSidebar, _sidebarElement);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(transSidebar, "(UIElement.RenderTransform).(TranslateTransform.X)");
            sb.Children.Add(transSidebar);
            
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(transMain, _mainContent);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(transMain, "(UIElement.RenderTransform).(TranslateTransform.X)");
            sb.Children.Add(transMain);

            sb.Begin();
        }
    }

    // -- Constructeur ----------------------------------------------------------
    internal Microsoft.UI.Xaml.ElementTheme GetCurrentTheme() =>
        _vm.Config.AccentColor == "blanc" && (_vm.Config.BackdropMode == "solid" || string.IsNullOrEmpty(_vm.Config.BackdropMode))
        ? Microsoft.UI.Xaml.ElementTheme.Light
        : Microsoft.UI.Xaml.ElementTheme.Dark;

    public MainWindow()
    {
        Microsoft.UI.Xaml.Application.Current.Resources["ButtonBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
        Microsoft.UI.Xaml.Application.Current.Resources["ButtonBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
        Microsoft.UI.Xaml.Application.Current.Resources["ButtonForegroundPointerOver"] = MainWindow.BR_TXT;
        Microsoft.UI.Xaml.Application.Current.Resources["ButtonForegroundPressed"] = MainWindow.BR_TXT;
        Microsoft.UI.Xaml.Application.Current.Resources["ComboBoxBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
        Microsoft.UI.Xaml.Application.Current.Resources["ComboBoxBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
        Microsoft.UI.Xaml.Application.Current.Resources["TextControlBorderBrushFocused"] = MainWindow.BR_ACCENT;
        Microsoft.UI.Xaml.Application.Current.Resources["TextControlBorderBrushPointerOver"] = MainWindow.BR_ACCENT_DIM;
        Microsoft.UI.Xaml.Application.Current.Resources["TextControlBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(20, 255, 255, 255));
        Microsoft.UI.Xaml.Application.Current.Resources["TextControlBackgroundFocused"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(15, 255, 255, 255));
        Title = "Soundeck";
        _hwnd = WindowNative.GetWindowHandle(this);

        var wid = Win32Interop.GetWindowIdFromWindow(_hwnd);
        var aw  = AppWindow.GetFromWindowId(wid);
        try {
            string ip = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(ip)) aw.SetIcon(ip);
        } catch { }
        aw.Resize(new SizeInt32(1200, 750));
        // Minimum window size � prevents element crush
        var presenter = aw.Presenter as OverlappedPresenter;
        if (presenter != null) { /* min size enforced via WndProc WM_GETMINMAXINFO */ }
        _minWidth  = 700;
        _minHeight = 500;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = aw.TitleBar;
            tb.ExtendsContentIntoTitleBar = true;
            tb.ButtonBackgroundColor      = Windows.UI.Color.FromArgb(0,0,0,0);
            tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0,0,0,0);
            tb.ButtonHoverBackgroundColor = C(40, 40, 60);
            tb.ButtonHoverForegroundColor = C(232, 232, 248);
            tb.ButtonPressedBackgroundColor = C(50, 50, 75);
            tb.ButtonPressedForegroundColor = C(232, 232, 248);
            tb.ButtonForegroundColor      = C(232, 232, 248);
            tb.ButtonInactiveForegroundColor = C(90, 90, 120);
        }

        // Active le mode sombre natif DWM pour la fenêtre � sans �a, le cadre/bordure
        // ext�rieur g�r� par Windows (hors zone client XAML) reste clair et un flash
        // blanc appara�t sur les bords, notamment quand la fenêtre perd le focus.
        int darkMode = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        uint borderColor = 0x001C1414u; // BGR: #14141C
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));
        uint captionColor = 0x001C1414u; // BGR: #14141C
        DwmSetWindowAttribute(_hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(uint));

        _wndProc    = WndProc;
        _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
        DragAcceptFiles(_hwnd, true);

        // Hook pour intercepter WM_SYSCHAR/SYSKEYDOWN avant WinUI (supprime le ding Alt)
        const int WH_GETMESSAGE = 3;
        _msgHook = MsgHookProc;
        _msgHookHandle = SetWindowsHookEx(WH_GETMESSAGE, _msgHook, 0, GetCurrentThreadId());

        Content = BuildUI();

        if (Content is FrameworkElement fe)
        {
            // Supprimer le "ding" Windows quand Alt est tenu :
            // On marque e.Handled=true sur PreviewKeyDown (descend avant les enfants)
            // ET sur KeyDown (remonte), avec handledEventsToo=true pour les deux.
            // �a emp�che WinUI de poster WM_SYSCHAR / WM_SYSDEADCHAR en r�ponse.
            var altHandler = new KeyEventHandler((_, e) =>
            {
                short s = GetAsyncKeyState(0x12); // VK_MENU
                if ((s & unchecked((short)0x8000)) != 0) e.Handled = true;
            });
            fe.AddHandler(UIElement.KeyDownEvent,    altHandler, handledEventsToo: true);
            fe.AddHandler(UIElement.PreviewKeyDownEvent, altHandler, handledEventsToo: true);
        }

        _vm.PropertyChanged += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsEngineRunning) or nameof(MainViewModel.StatusText))
                UpdateEngineStatus();
        });

        // PlaybackChanged : d�clench� aussi par les raccourcis clavier (via HotkeyService),
        // qui n'appellent que PlaySound/StopSound sans toucher l'UI. On rafra�chit ici.
        _vm.PlaybackChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            UpdateNowPlaying();
        });

        InitializeOnce();
        
        bool _ftuxShown = false;
        async void CheckAndShowFtux()
        {
            if (_ftuxShown || _vm.Config.HasSeenFtuxPopup) return;
            _ftuxShown = true;
            
            // Wait up to 3 seconds for XamlRoot to be ready
            for (int i = 0; i < 30; i++)
            {
                if (this.Content?.XamlRoot != null) break;
                await Task.Delay(100);
            }
            
            if (this.Content?.XamlRoot == null)
            {
                _ftuxShown = false;
                return;
            }

            bool isDriverInstalled = Soundeck.Services.VbCableService.IsInstalled();
            
            var titleText = new TextBlock 
            { 
                Text = I18n.T("Bienvenue sur Soundeck !"), 
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                FontSize = 20,
                Margin = new Thickness(0, 0, 0, 16)
            };
            
            var contentText = new TextBlock 
            { 
                Text = I18n.T("Pour que le volume de vos sons soit parfaitement équilibré et n'écrase jamais votre voix, nous vous conseillons d'aller dans les Paramètres pour effectuer un Calibrage de votre micro."),
                TextAlignment = TextAlignment.Justify,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 24)
            };

            var closeBtn = new Button 
            { 
                Content = isDriverInstalled ? I18n.T("Compris, aller aux paramètres") : I18n.T("Ok"), 
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = MainWindow.BR_ACCENT_TRANSLUCENT, Foreground = MainWindow.BR_ACCENT_TXT,
                BorderBrush = MainWindow.BR_ACCENT, BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 8, 18, 8),
                CornerRadius = new CornerRadius(6)
            };
            
            var panel = new StackPanel();
            panel.Children.Add(titleText);
            panel.Children.Add(contentText);
            panel.Children.Add(closeBtn);

            var dialog = new ContentDialog
            {
                Content = panel,
                XamlRoot = this.Content.XamlRoot
            };
            
            closeBtn.Click += (_, _) => dialog.Hide();
            
            try
            {
                await dialog.ShowAsync();
                _vm.Config.HasSeenFtuxPopup = true;
                _vm.Save();
                if (isDriverInstalled)
                {
                    SwitchToPage(Page.Settings);
                }
            }
            catch 
            {
                _ftuxShown = false;
            }
        }

        this.Activated += (_, _) => CheckAndShowFtux();
        if (this.Content is FrameworkElement rootFe) rootFe.Loaded += (_, _) => CheckAndShowFtux();

        aw.Closing += (_, e) =>
        {
            if (_vm.Config.MinimizeToTray && !_forceClose) { e.Cancel = true; aw.Hide(); }
        };
        Closed += (_, _) =>
        {
            if (_msgHookHandle != 0) { UnhookWindowsHookEx(_msgHookHandle); _msgHookHandle = 0; }
            _vm.Save(); _vm.Dispose(); _tray?.Dispose();
            Microsoft.UI.Xaml.Application.Current.Exit();
            Environment.Exit(0);
        };
        ApplyTheme();
    }

    // -- Theme --------------------------------------------------------------------
    private void ApplyTheme()
    {
        var cfg = _vm.Config;
        
        bool isLight = cfg.AccentColor == "blanc" && (cfg.BackdropMode == "solid" || string.IsNullOrEmpty(cfg.BackdropMode));
        
        var preset = cfg.AccentColor != null && AccentPresets.ContainsKey(cfg.AccentColor) ? AccentPresets[cfg.AccentColor] : AccentPresets["blanc"];
        
        if (isLight)
        {
            if (Content is Microsoft.UI.Xaml.FrameworkElement r) r.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Light;
            BG_BASE = C(228, 228, 233);
            SIDEBAR_BG_BASE = C(238, 238, 243);
            CARD_BASE = C(248, 248, 253);
            CARD_HV_BASE = C(240, 240, 245);
            NP_BG_BASE = C(248, 248, 253);
            CONTROL_BASE = C(245, 245, 250);
            TXT = C(0, 0, 0);
            TXT2 = C(25, 25, 30);
            TXT3 = C(60, 60, 65);
        }
        else
        {
            if (Content is Microsoft.UI.Xaml.FrameworkElement r) r.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark;
            BG_BASE = C(15, 15, 20);
            SIDEBAR_BG_BASE = C(20, 20, 28);
            CARD_BASE = C(30, 30, 45);
            CARD_HV_BASE = C(40, 40, 60);
            NP_BG_BASE = C(18, 18, 28);
            CONTROL_BASE = C(25, 25, 30);
            TXT = C(235, 235, 240);
            TXT2 = C(160, 160, 170);
            TXT3 = C(100, 100, 110);
        }
        BR_TXT.Color = TXT;
        BR_TXT2.Color = TXT2;
        BR_TXT3.Color = TXT3;
        byte rCtl = (byte)((preset.Accent.R * 0.25) + (20 * 0.75));
        byte gCtl = (byte)((preset.Accent.G * 0.25) + (20 * 0.75));
        byte bCtl = (byte)((preset.Accent.B * 0.25) + (25 * 0.75));
        BR_GLASSY.Color = isLight ? Windows.UI.Color.FromArgb(15, 0, 0, 0) : Windows.UI.Color.FromArgb(15, 255, 255, 255);

        // Backdrop : pilote � la fois le SystemBackdrop natif WinUI ET la
        // transparence de nos propres surfaces (root, sidebar, cartes, now-playing).
        // Sans rendre ces surfaces transparentes, Mica/Acrylic ne changent rien
        // visuellement puisqu'elles sont enti�rement recouvertes par des fonds
        // opaques peints par-dessus le backdrop.
        bool isMicaOrAcrylic;
        switch (cfg.BackdropMode)
        {
            case "mica":
                SystemBackdrop = new MicaBackdrop();
                isMicaOrAcrylic = true;
                break;
            case "mica_alt":
                SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
                isMicaOrAcrylic = true;
                break;
            case "acrylic":
                SystemBackdrop = new DesktopAcrylicBackdrop();
                isMicaOrAcrylic = true;
                break;
            default:
                SystemBackdrop = null;
                isMicaOrAcrylic = false;
                break;
        }

        if (isMicaOrAcrylic)
        {
            // Surfaces translucides pour laisser transparaêtre le backdrop.
            // Le fond racine est presque enti�rement transparent (le backdrop
            // fait le travail), les cartes/sidebar/now-playing gardent une
            // l�g�re teinte pour rester lisibles et d�limit�es.
            BR_BG.Color         = Windows.UI.Color.FromArgb(0,   BG_BASE.R,         BG_BASE.G,         BG_BASE.B);
            BR_SIDEBAR_BG.Color = Windows.UI.Color.FromArgb(120, SIDEBAR_BG_BASE.R, SIDEBAR_BG_BASE.G, SIDEBAR_BG_BASE.B);
            BR_CARD.Color       = Windows.UI.Color.FromArgb(140, CARD_BASE.R,       CARD_BASE.G,       CARD_BASE.B);
            BR_CARD_HV.Color    = Windows.UI.Color.FromArgb(170, CARD_HV_BASE.R,    CARD_HV_BASE.G,    CARD_HV_BASE.B);
            BR_NP_BG.Color      = Windows.UI.Color.FromArgb(150, NP_BG_BASE.R,      NP_BG_BASE.G,      NP_BG_BASE.B);
            // On utilise un blanc transparent natif pour les contr�les (Mica/Acrylic) afin de ne pas garder
            // la teinte color�e de l'accent qui �tait calcul�e en haut.
            BR_CONTROL.Color    = isLight ? Windows.UI.Color.FromArgb(179, 255, 255, 255) : Windows.UI.Color.FromArgb(15, 255, 255, 255);
        }
        else
        {
            // Mode Solide : surfaces pleinement opaques, teint�es par l'accent
            // choisi (voir ApplyAccent) pour que le changement de couleur soit
            // visible partout, pas seulement sur la barre de titre.
            BR_BG.Color         = BG_BASE;
            BR_SIDEBAR_BG.Color = SIDEBAR_BG_BASE;
            BR_CARD.Color       = CARD_BASE;
            BR_CARD_HV.Color    = CARD_HV_BASE;
            BR_NP_BG.Color      = NP_BG_BASE;
            BR_CONTROL.Color    = isLight ? Windows.UI.Color.FromArgb(255, 222, 222, 228) : Windows.UI.Color.FromArgb(255, rCtl, gCtl, bCtl);
        }

        // Accent colors (et re-teinte des fonds solides selon l'accent)
        ApplyAccent(cfg.AccentColor);
    }

    /// <summary>M�lange lin�airement deux couleurs (t=0 ? a, t=1 ? b), alpha ignor� (toujours 255).</summary>
    static Windows.UI.Color Lerp(Windows.UI.Color a, Windows.UI.Color b, double t)
    {
        byte L(byte x, byte y) => (byte)(x + (y - x) * t);
        return Windows.UI.Color.FromArgb(255, L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    private void ApplyAccent(string name)
    {
        var mode = _vm.Config.BackdropMode;
        if (mode == "mica" || mode == "mica_alt" || mode == "acrylic")
        {
            name = "sombre";
        }

        if (!AccentPresets.TryGetValue(name, out var preset)) return;
        
        var acc = preset.Accent;
        var acc2 = preset.Accent2;
        bool isLight = name == "blanc" && (mode == "solid" || string.IsNullOrEmpty(mode));
        if (isLight)
        {
            acc = C(30, 30, 35);
            acc2 = C(60, 60, 65);
        }

        MainWindow.BR_ACCENT.Color     = acc;
        MainWindow.BR_ACCENT2.Color    = acc2;
        MainWindow.BR_ACCENT_DIM.Color = (mode == "mica" || mode == "mica_alt" || mode == "acrylic") ? Windows.UI.Color.FromArgb(100, preset.Dim.R, preset.Dim.G, preset.Dim.B) : preset.Dim;
        MainWindow.BR_ACCENT_TRANSLUCENT.Color = Windows.UI.Color.FromArgb(40, acc.R, acc.G, acc.B);
        MainWindow.BR_ACCENT_TXT.Color = (acc.R > 200 && acc.G > 200 && acc.B > 200) ? C(15,15,20) : C(255,255,255);

        // En mode Solide, teinte légèrement les fonds (base neutre sombre + une
        // pointe de la couleur accent) pour que "changer la couleur" ait un
        // effet visible sur l'ensemble de l'interface, pas seulement sur les
        // boutons/bordures accentu�s.
        if (_vm.Config.BackdropMode == "solid" || string.IsNullOrEmpty(_vm.Config.BackdropMode))
        {
            BR_BG.Color         = preset.Dim;
            BR_SIDEBAR_BG.Color = Lerp(preset.Dim, acc, 0.1);
            BR_CARD.Color       = Lerp(preset.Dim, acc, 0.2);
            BR_CARD_HV.Color    = Lerp(preset.Dim, preset.Accent, 0.4);
            BR_NP_BG.Color      = Lerp(preset.Dim, preset.Accent, 0.15);
        }

        // Update XAML resource colors
        if (Application.Current != null)
        {
            Application.Current.Resources["SystemAccentColor"] = acc;
            Application.Current.Resources["SystemAccentColorLight1"] = acc;
            Application.Current.Resources["SystemAccentColorLight2"] = acc;
            Application.Current.Resources["SystemAccentColorDark1"] = acc;
            Application.Current.Resources["SystemAccentColorDark2"] = acc;
            Application.Current.Resources["TextControlBorderBrushFocused"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(acc);
            Application.Current.Resources["TextControlBorderBrushPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(acc2);
            if (Application.Current.Resources.TryGetValue("AccentColor", out var _))
            {
                Application.Current.Resources["AccentColor"]     = acc;
                Application.Current.Resources["Accent2Color"]    = acc2;
                Application.Current.Resources["AccentDimColor"]  = preset.Dim;
            }
        }
        
        if (Content is FrameworkElement root)
        {
            var old = root.RequestedTheme;
            root.RequestedTheme = old == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = old;
        }
        // Update title bar
        try
        {
            var aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var tb = aw.TitleBar;
                tb.ButtonBackgroundColor      = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                tb.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                tb.ButtonForegroundColor      = TXT;
                tb.ButtonHoverBackgroundColor = preset.Accent2;
                tb.ButtonForegroundColor      = TXT;
                tb.ButtonPressedBackgroundColor = preset.Accent;
                tb.ButtonForegroundColor      = TXT;
            }
        }
        catch { }
    }


        private async void InitializeOnce()
    {
        var log = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck", "crash.log");
        try
        {
            System.IO.File.AppendAllText(log, "Init: overlay + tray\n");
            _overlay = new QuickSearchOverlay(_vm);
            _vm.SetOverlayToggle(() => DispatcherQueue.TryEnqueue(_overlay.ToggleOverlay));
            var iconHandle = TrayIconService.LoadAppIcon();
            _tray = new TrayIconService(_hwnd, iconHandle, "Soundeck");
            _tray.Show();
            System.IO.File.AppendAllText(log, "Init: RegisterAllHotkeys\n");
            _vm.RegisterAllHotkeys(_hwnd);
            System.IO.File.AppendAllText(log, "Init: RebuildSidebar\n");
            RebuildSidebar();
            System.IO.File.AppendAllText(log, "Init: RefreshGrid\n");
            RefreshGrid();
            System.IO.File.AppendAllText(log, "Init: VbCable check\n");
            _vbBanner.Visibility = Visibility.Collapsed;
            System.IO.File.AppendAllText(log, "Init: RestartEngine\n");
            await _vm.RestartEngineAsync();
            System.IO.File.AppendAllText(log, "Init: UpdateEngineStatus\n");
            UpdateEngineStatus();
            System.IO.File.AppendAllText(log, "Init: DONE\n");
        }
        catch (Exception ex) { System.IO.File.AppendAllText(log, $"[Init crash]\n{ex}\n"); }
    }

    public void StartHiddenInTray()
    {
        var wid = Win32Interop.GetWindowIdFromWindow(_hwnd);
        AppWindow.GetFromWindowId(wid).Hide();
    }


    
    private static long _lastAnimTime = 0;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _pageTransitionSb;
    private void AnimatePageTransition()
    {
        long now = System.DateTime.Now.Ticks / System.TimeSpan.TicksPerMillisecond;
        if (now - _lastAnimTime < 100) return; // Debounce 100ms
        _lastAnimTime = now;

        if (_pageTransitionSb != null) _pageTransitionSb.Stop();
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        _pageTransitionSb = sb;
        
        void AddAnim(Microsoft.UI.Xaml.UIElement el, double delayMs = 0)
        {
            if (el == null) return;
            
            var op = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames();
            op.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.DiscreteDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(System.TimeSpan.Zero), Value = 0 });
            op.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.DiscreteDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(System.TimeSpan.FromMilliseconds(delayMs)), Value = 0 });
            op.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(System.TimeSpan.FromMilliseconds(delayMs + 250)), Value = 1 });
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(op, el);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(op, "Opacity");
            sb.Children.Add(op);
            
            if (!(el.RenderTransform is Microsoft.UI.Xaml.Media.TranslateTransform))
                el.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform();
            
            var trans = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames();
            trans.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.DiscreteDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(System.TimeSpan.Zero), Value = 15 });
            trans.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.DiscreteDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(System.TimeSpan.FromMilliseconds(delayMs)), Value = 15 });
            trans.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame { KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(System.TimeSpan.FromMilliseconds(delayMs + 400)), Value = 0, EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut } });
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(trans, el);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(trans, "(UIElement.RenderTransform).(TranslateTransform.Y)");
            sb.Children.Add(trans);
        }
        
        if (_titleRow != null) AddAnim(_titleRow, 0);
        AddAnim(_actionRow, 20);
        if (_searchBox != null) AddAnim(_searchBox, 40);
        AddAnim(_pageContainer, 60);
        
        sb.Begin();
    }

    private async void SwitchToPage(Page page)
    {
        System.Diagnostics.Debug.WriteLine($"SwitchToPage({page}) called at {System.DateTime.Now.Ticks}");
        if (_pageContainer != null) _pageContainer.Opacity = 0;
        if (_titleRow != null) _titleRow.Opacity = 0;
        if (_actionRow != null) _actionRow.Opacity = 0;
        if (_searchBox != null) _searchBox.Opacity = 0;

        _searchBox.Visibility = Visibility.Collapsed;
        if (_actionRow != null) _actionRow.Visibility = page == Page.Sounds ? Visibility.Visible : Visibility.Collapsed;

        _currentPage = page;
        RebuildSidebar();
        await System.Threading.Tasks.Task.Delay(15);

        switch (page)
        {
            case Page.Sounds:
                _onPcTab = false;
                _searchBox.Visibility = Visibility.Visible;
                _vm.ActiveCollection = _vm.ActiveCollection; // refresh
                _vwg = null; // Recreate grid for Entrance animation
                RefreshGrid();
                break;
            case Page.PcBrowser:
                _onPcTab = true;
                if (_pcBrowserPanel != null)
                {
                    if (_pageContainer.Content != _pcBrowserPanel) _pageContainer.Content = _pcBrowserPanel;
                    _pcBrowserPanel.Visibility = Visibility.Visible;
                    _pcBrowserPanel.OnShow();
                }
                break;
            case Page.OnlineSearch:
                if (_onlineSearchPanel != null)
                {
                    if (_pageContainer.Content != _onlineSearchPanel) _pageContainer.Content = _onlineSearchPanel;
                    _onlineSearchPanel.Visibility = Visibility.Visible;
                    _onlineSearchPanel.OnShow();
                }
                break;
            case Page.Settings:
                if (_settingsPanel != null)
                {
                    if (_pageContainer.Content != _settingsPanel) _pageContainer.Content = _settingsPanel;
                    _settingsPanel.Visibility = Visibility.Visible;
                    _settingsPanel.OnShow();
                }
                break;
        }
        AnimatePageTransition();

        // Mettre � jour le titre et le compteur
        if (_mainTitle != null)
            _mainTitle.Text = page switch
            {
                Page.Sounds => _vm.ActiveCollection == "__FAVORITES__" ? I18n.T("Favoris") : _vm.ActiveCollection,
                Page.PcBrowser => I18n.T("Fichiers du PC"),
                Page.OnlineSearch => I18n.T("Recherche en ligne"),
                Page.Settings => I18n.T("Paramètres"),
                _ => ""
            };
        if (_mainCount != null)
            _mainCount.Text = page == Page.Sounds
                ? $"{_vm.FilteredSounds.Count} {(_vm.FilteredSounds.Count != 1 ? I18n.T("sons") : I18n.T("son"))}"
                : "";

        RebuildTabs();
        AnimatePageTransition();
    }

    private void SwitchToCollection(string col)
    {
        _vm.ActiveCollection = col;
        SwitchToPage(Page.Sounds);
    }

    private void SwitchToPcTab() => SwitchToPage(Page.PcBrowser);
    private void SwitchToSettings() => SwitchToPage(Page.Settings);
    private void SwitchToOnlineSearch() => SwitchToPage(Page.OnlineSearch);

    private void ShowTabMenu(string col, FrameworkElement anchor)
    {
        if (col == "Default") return;
        var m = new MenuFlyout();
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, MainWindow.BR_CARD));
        presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, MainWindow.BR_ACCENT_DIM));
        presenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        m.MenuFlyoutPresenterStyle = presenterStyle;

        var r = new MenuFlyoutItem { 
            Text = I18n.T("Renommer"),
            Icon = new FontIcon { Glyph = "✏️", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1.5, 0, 0) }
        };
        if (App.Current.Resources.TryGetValue("SoundDeckMenuItemStyle", out var sR) && sR is Style stR) r.Style = stR;
        r.Click += async (_, _) =>
        {
            var box = new TextBox { Text = col };
            if (await Dlg(I18n.T("Renommer"), box) && !string.IsNullOrWhiteSpace(box.Text))
            {
                foreach (var s in _vm.AllSounds.Where(s => s.Collection == col)) s.Collection = box.Text;
                int idx = _vm.Collections.IndexOf(col); if (idx >= 0) _vm.Collections[idx] = box.Text;
                if (_vm.ActiveCollection == col) _vm.ActiveCollection = box.Text;
                _vm.Save(); RebuildTabs();
            }
        };
        var d = new MenuFlyoutItem { 
            Text = I18n.T("Supprimer (garder les sons)"),
            Icon = new FontIcon { Glyph = "🗑️", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1.5, 0, 0) }
        };
        if (App.Current.Resources.TryGetValue("SoundDeckMenuItemStyle", out var sD) && sD is Style stD) d.Style = stD;
        d.Click += async (_, _) => { if (await Confirm($"{I18n.T("Supprimer")} \"{col}\" ?")) { _vm.RemoveCollection(col, false); RebuildTabs(); RefreshGrid(); } };
        var da = new MenuFlyoutItem { 
            Text = I18n.T("Supprimer (supprimer les sons)"), 
            Foreground = B(RED),
            Icon = new FontIcon { Glyph = "❌", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1.5, 0, 0) }
        };
        if (App.Current.Resources.TryGetValue("SoundDeckMenuItemStyle", out var sDa) && sDa is Style stDa) da.Style = stDa;
        da.Click += async (_, _) => { if (await Confirm($"{I18n.T("Supprimer")} \"{col}\" {I18n.T("et tous ses sons ?")}")) { _vm.RemoveCollection(col, true); RebuildTabs(); RefreshGrid(); } };
        m.Items.Add(r); m.Items.Add(new MenuFlyoutSeparator()); m.Items.Add(d); m.Items.Add(da);
        m.ShowAt(anchor);
    }

    // -- Engine ----------------------------------------------------------------
    private void UpdateEngineStatus()
    {
        bool on = _vm.IsEngineRunning;
        _engineDot.Fill        = B(on ? GREEN : TXT3);
        _engineStatusText.Text     = on ? I18n.T("Engine actif") : I18n.T("Engine arrêté");
        _engineStatusText.Foreground = B(on ? GREEN : TXT3);
        _statusBar.Text        = _vm.StatusText;
    }

    // -- Actions ---------------------------------------------------------------
    private async Task AddSounds()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        InitializeWithWindow.Initialize(picker, _hwnd);
        foreach (var ext in AudioExts()) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files == null) return;
        foreach (var f in files) _vm.AddSound(f.Path);
        RefreshGrid();
    }

    private async Task AddCollection()
    {
        var box = new TextBox { PlaceholderText = I18n.T("Nom de la collection :") };
        if (await Dlg(I18n.T("Nouvelle collection"), box) && !string.IsNullOrWhiteSpace(box.Text))
        { _vm.AddCollection(box.Text.Trim()); _vm.ActiveCollection = box.Text.Trim(); RebuildTabs(); RefreshGrid(); }
    }

    private void OpenOnlineSearch() => SwitchToPage(Page.OnlineSearch);

    private void OpenSettings() => SwitchToPage(Page.Settings);

    private async Task InstallVbCable()
    {
        if (!await Confirm(I18n.T("Installer VB-Cable ? (Gratuit, nécessite les droits admin)"))) return;
        var prog = new Progress<string>(m => _statusBar.Text = m);
        var (ok, err) = await VbCableService.InstallAsync(prog);
        if (ok) { _vm.CheckVbCable(); _vbBanner.Visibility = Visibility.Collapsed; _statusBar.Text = I18n.T("VB-Cable installé !");
        if (_settingsPanel != null) _ = _settingsPanel.ForceRefresh(); }
        else await ShowMsg(I18n.T("Échec"), err ?? I18n.T("Erreur inconnue"));
    }

    public void RefreshVbBanner() =>
        _vbBanner.Visibility = _vm.IsVbCableInstalled ? Visibility.Collapsed : Visibility.Visible;

    private async Task RenameSound()
    {
        if (_contextSound == null) return;
        var box = new TextBox { Text = _contextSound.Name };
        if (await Dlg(I18n.T("Renommer"), box) && !string.IsNullOrWhiteSpace(box.Text))
        { _contextSound.Name = box.Text.Trim(); _vm.Save(); RefreshGrid(); }
    }

    private async Task SetHotkey()
    {
        if (_contextSound == null) return;
        var dlg = new HotkeyDialog(_contextSound, _hwnd) { XamlRoot = Content.XamlRoot, RequestedTheme = GetCurrentTheme() };
        var result = await dlg.ShowAsync();
        // Refresh on Primary (save) OR Secondary (clear) � both change the hotkey state
        if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            _vm.RegisterAllHotkeys(_hwnd);
            _vm.Save();
            RefreshGrid(true);
        }
    }

    private async Task ChangeFile()
    {
        if (_contextSound == null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        InitializeWithWindow.Initialize(picker, _hwnd);
        foreach (var ext in AudioExts()) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file != null) { _contextSound.Filepath = file.Path; _vm.Save(); RefreshGrid(); }
    }

    private async Task MoveSound()
    {
        if (_contextSound == null) return;
        var combo = new ComboBox { ItemsSource = _vm.Collections.ToList(), SelectedItem = _contextSound.Collection };
        if (await Dlg(I18n.T("Déplacer vers"), combo) && combo.SelectedItem is string col)
        { _contextSound.Collection = col; _vm.Save(); RefreshGrid(); }
    }

    private async Task DeleteSound()
    {
        if (_contextSound == null) return;
        if (await Confirm(I18n.T("Supprimer") + " \"" + _contextSound.Name + "\" ?\n(" + I18n.T("Le fichier audio n'est PAS supprimé.") + ")"))
        { _vm.RemoveSound(_contextSound); _contextSound = null; RefreshGrid(); }
    }

    private const uint WM_GETMINMAXINFO = 0x0024;
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT2 { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT2 ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    // -- WndProc ---------------------------------------------------------------
    private nint WndProc(nint h, uint m, nuint w, nint l)
    {
        if (m == WM_HOTKEY)    { _vm.HotkeySvc.HandleHotKeyMessage((int)w); return 0; }
        if (m == WM_DROPFILES) { HandleDrop(w); return 0; }
        // Suppress the Windows "ding".
        // WM_SYSCHAR  ? always swallow (it's what produces the ding sound).
        // WM_SYSKEYDOWN ? only swallow when Alt is held (bit 29 of lParam set)
        //   AND the key is not VK_F10 (0x79) or VK_MENU (0x12), which are the
        //   two keys Windows handles as real menu activators. This prevents the
        //   ding without breaking Alt+letter hotkeys registered via RegisterHotKey.
        if (m == WM_SYSCHAR) return 0;
        if (m == WM_SYSDEADCHAR) return 0;
        if (m == WM_SYSKEYDOWN)
        {
            bool altDown = ((uint)l & 0x20000000u) != 0;
            uint vk = (uint)w;
            bool isMenuKey = vk == 0x12 /*VK_MENU*/ || vk == 0x79 /*VK_F10*/;
            if (altDown && !isMenuKey) return 0;
        }
        if (m == WM_SYSKEYUP)
        {
            uint vk = (uint)w;
            // Supprimer WM_SYSKEYUP pour toutes les touches sauf Alt lui-m�me (0x12)
            // C'est le SYSKEYUP qui déclenche le ding sur certains systèmes
            if (vk != 0x12 /*VK_MENU*/) return 0;
        }
        if (m == WM_GETMINMAXINFO && l != 0)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(l);
            mmi.ptMinTrackSize.x = _minWidth;
            mmi.ptMinTrackSize.y = _minHeight;
            Marshal.StructureToPtr(mmi, l, false);
            return 0;
        }
        if (m == TrayIconService.WM_TRAYICON)
        {
            uint msg = (uint)l;
            if (msg == TrayIconService.WM_LBUTTONUP || msg == TrayIconService.WM_LBUTTONDBLCLK)
            {
                DispatcherQueue.TryEnqueue(RestoreFromTray);
            }
            else if (msg == TrayIconService.WM_RBUTTONUP && _tray != null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var choice = _tray.ShowContextMenu();
                    if (choice == TrayIconService.MENU_ID_SHOW) RestoreFromTray();
                    else if (choice == TrayIconService.MENU_ID_QUIT) QuitFromTray();
                });
            }
            return 0;
        }
        return CallWindowProc(_oldWndProc, h, m, w, l);
    }

    private void RestoreFromTray()
    {
        var wid = Win32Interop.GetWindowIdFromWindow(_hwnd);
        var aw  = AppWindow.GetFromWindowId(wid);
        aw.Show();
        Activate();
    }

    private void QuitFromTray()
    {
        _forceClose = true;
        var wid = Win32Interop.GetWindowIdFromWindow(_hwnd);
        AppWindow.GetFromWindowId(wid).Destroy();
    }

    private void HandleDrop(nuint hDrop)
    {
        uint n = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
        var exts = AudioExts();
        for (uint i = 0; i < n; i++)
        {
            var buf = new char[260];
            DragQueryFile(hDrop, i, buf, 260);
            var path = new string(buf).TrimEnd('\0');
            if (exts.Contains(IO.Path.GetExtension(path)))
                DispatcherQueue.TryEnqueue(() => _vm.AddSound(path));
        }
        DragFinish(hDrop);
        DispatcherQueue.TryEnqueue(() => RefreshGrid());
    }

    // -- Helpers ---------------------------------------------------------------
    private static HashSet<string> AudioExts() =>
        new(StringComparer.OrdinalIgnoreCase) { ".wav",".mp3",".flac",".ogg",".m4a",".aac",".wma",".opus" };

    private Button Btn(string text, Windows.UI.Color bg, double fs, Thickness pad) => new()
    {
        Content = text, Background = B(bg), Foreground = B(WHITE),
        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        Padding = pad, FontSize = fs, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
    };

    private Button ActionBtn(string text, Windows.UI.Color bg, bool accent, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Content = text,
            Background  = accent ? MainWindow.BR_ACCENT : MainWindow.BR_GLASSY,
            Foreground  = accent ? B(WHITE)  : MainWindow.BR_TXT2,
            BorderBrush = accent ? B(TRANSP) : MainWindow.BR_ACCENT_DIM,
            BorderThickness = new Thickness(accent ? 0 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12,7,12,7), FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        btn.Click += click;
        return btn;
    }

    private void MI(MenuFlyout menu, string icon, string text, RoutedEventHandler click, double fontSize = 13.5)
    {
        Thickness iconMargin = icon switch
        {
            "⌨️" => new Thickness(0, -3.5, 0, 0),
            "📁" or "📂" => new Thickness(0, -2.5, 0, 0),
            _ => new Thickness(0, -1.5, 0, 0)
        };
        var item = new MenuFlyoutItem { 
            Text = text,
            Icon = new FontIcon { 
                Glyph = icon, 
                FontFamily = new FontFamily("Segoe UI Emoji"), 
                FontSize = fontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = iconMargin
            }
        };
        if (App.Current.Resources.TryGetValue("SoundDeckMenuItemStyle", out var s) && s is Style st) item.Style = st;
        item.Click += click;
        menu.Items.Add(item);
    }
    
    private void MIFluent(MenuFlyout menu, string icon, string text, RoutedEventHandler click, Brush? foreground = null, double fontSize = 12)
    {
        var fontIcon = new FontIcon { 
            Glyph = icon, 
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), 
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (foreground != null) fontIcon.Foreground = foreground;
        var item = new MenuFlyoutItem { 
            Text = text,
            Icon = fontIcon
        };
        if (App.Current.Resources.TryGetValue("SoundDeckMenuItemStyle", out var s) && s is Style st) item.Style = st;
        item.Click += click;
        menu.Items.Add(item);
    }

    private async Task<bool> Dlg(string title, UIElement content)
        => await DialogHelper.ShowCustomDialog(Content.XamlRoot, title, content, "OK", I18n.T("Annuler"));

    private async Task<bool> Confirm(string message)
        => await DialogHelper.ShowCustomDialog(Content.XamlRoot, I18n.T("Confirmation"), new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, I18n.T("Oui"), I18n.T("Non"));

    private async Task ShowMsg(string title, string msg)
        => await DialogHelper.ShowCustomDialog(Content.XamlRoot, title, new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }, "OK");
}


// -- PcBrowserPanel.cs -------------------------------------------

/// <summary>
/// Embedded panel shown when the "💻 PC" pseudo-collection tab is active.
/// Scans common user folders for audio files and lets you search/play them
/// directly, without having to import them into a collection first.
/// Mirrors the original Tkinter app's LocalBrowserPanel.
/// </summary>
public sealed class PcBrowserPanel : Grid
{
    private readonly MainViewModel _vm;
    private List<(string Name, string Path)> _allFiles = new();
    private List<(string Name, string Path)> _filtered = new();
    private List<string> _displayStrings = new();   // parallel to _filtered, used by ItemClick
    private bool _scanning;
    private CancellationTokenSource? _scanCts;

    private TextBox _searchBox = null!;
    private Button _rescanBtn = null!;
    private TextBlock _status = null!;
    private TextBlock _nowPlaying = null!;
    private ListView _listView = null!;

    static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
    static SolidColorBrush B(Windows.UI.Color c) => new(c);
    static readonly Windows.UI.Color CARD   = C(30, 30, 50);
    static readonly Windows.UI.Color BORDER = C(42, 42, 69);
    static readonly Windows.UI.Color TXT    = C(232, 232, 248);
    static readonly Windows.UI.Color TXT2   = C(144, 144, 176);
    static readonly Windows.UI.Color TXT3   = C(96, 96, 128);
    static readonly Windows.UI.Color RED    = C(239, 68, 68);
    static readonly Windows.UI.Color BG     = C(15, 15, 25);

    public PcBrowserPanel(MainViewModel vm)
    {
        _vm = vm;
        Background = MainWindow.BR_BG;
        BuildUi();
    }

    private void BuildUi()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // search bar
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status row
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // footer

        // Search bar
        var bar = new Grid { Height = 50, Padding = new Thickness(10, 8, 10, 8), ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _searchBox = new TextBox
        {
            PlaceholderText = "\U0001F50D  " + I18n.T("Rechercher un fichier audio sur ce PC..."),
            Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, Foreground = MainWindow.BR_TXT,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6)
        };
        _searchBox.TextChanged += (_, _) => Filter();
        Grid.SetColumn(_searchBox, 0);

        _rescanBtn = new Button
        {
            Content = "\U0001F504  " + I18n.T("Rescanner"), Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(8), FontSize = 11,
            Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1),
            Foreground = MainWindow.BR_TXT2
        };
        _rescanBtn.Click += (_, _) => StartScan();
        Grid.SetColumn(_rescanBtn, 1);

        bar.Children.Add(_searchBox);
        bar.Children.Add(_rescanBtn);
        Grid.SetRow(bar, 0);
        Children.Add(bar);

        // Status row
        var statusRow = new Grid { Padding = new Thickness(12, 4, 12, 4) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _status = new TextBlock { Text = I18n.T("En attente..."), FontSize = 11, Foreground = MainWindow.BR_TXT3, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_status, 0);

        var stopLink = new TextBlock { Text = "\u25A0 " + I18n.T("Tout arrêter"), FontSize = 11, Foreground = B(RED), VerticalAlignment = VerticalAlignment.Center };
        stopLink.Tapped += (_, _) => { _vm.StopAllSounds(); _nowPlaying.Text = I18n.T("En attente..."); };
        Grid.SetColumn(stopLink, 1);

        statusRow.Children.Add(_status);
        statusRow.Children.Add(stopLink);
        Grid.SetRow(statusRow, 1);
        Children.Add(statusRow);

        // List
        _listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            IsItemClickEnabled = true   // ? click single ou double déclenche ItemClick
        };
        _listView.ItemClick += (_, e) =>
        {
            // Find the index of the clicked display string in the current display list
            if (e.ClickedItem is string s)
            {
                var idx = _displayStrings.IndexOf(s);
                if (idx >= 0 && idx < _filtered.Count) PlayAt(idx);
            }
        };
        _listView.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) PlaySelectedKey(); };
        Grid.SetRow(_listView, 2);
        Children.Add(_listView);

        // Footer
        _nowPlaying = new TextBlock
        {
            Text = I18n.T("Double-cliquez ou Entrée pour jouer"),
            FontSize = 11, FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = MainWindow.BR_TXT3, Margin = new Thickness(12, 6, 12, 6)
        };
        Grid.SetRow(_nowPlaying, 3);
        Children.Add(_nowPlaying);
    }

    public void OnShow()
    {
        if (!_scanning && _allFiles.Count == 0)
            StartScan();
    }

    public void StartScan()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _scanning = true;
        _allFiles = new();
        _listView.ItemsSource = null;
        _status.Text = "Scan en cours...";
        _rescanBtn.IsEnabled = false;
        _rescanBtn.Content = "⏳";

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _ = Task.Run(() =>
        {
            try
            {
                var roots = PcScanService.GetScanRoots();
                var scanned = PcScanService.ScanAudioFiles(roots, ct: ct);
                var known = _vm.AllSounds.Select(s => s.Filepath);
                var merged = PcScanService.MergeWithKnownSounds(scanned, known);

                if (ct.IsCancellationRequested) return;

                dispatcher.TryEnqueue(() =>
                {
                    _allFiles = merged;
                    _scanning = false;
                    _status.Text = $"{merged.Count} {I18n.T("fichier(s) trouvé(s)")}";
                    _rescanBtn.IsEnabled = true;
                    _rescanBtn.Content = "\U0001F504  " + I18n.T("Rescanner");
                    Filter();
                });
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                dispatcher.TryEnqueue(() =>
                {
                    _scanning = false;
                    _status.Text = $"{I18n.T("Erreur")} : {ex.Message}";
                    _rescanBtn.IsEnabled = true;
                    _rescanBtn.Content = "\U0001F504  " + I18n.T("Rescanner");
                });
            }
        }, ct);
    }

    private void Filter()
    {
        var q = (_searchBox.Text ?? "").Trim().ToLowerInvariant();
        _filtered = string.IsNullOrEmpty(q)
            ? _allFiles.Take(300).ToList()
            : _allFiles.Where(f => f.Name.ToLowerInvariant().Contains(q)).Take(300).ToList();

        _displayStrings = _filtered
            .Select(f => $"  {System.IO.Path.GetFileNameWithoutExtension(f.Name)}   ·   " +
                         $"{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(f.Path) ?? "")}")
            .ToList();

        _listView.ItemsSource = _displayStrings;
    }

    private async void PlayAt(int idx)
    {
        if (idx < 0 || idx >= _filtered.Count) return;
        var (name, path) = _filtered[idx];

        if (!System.IO.File.Exists(path))
        {
            _nowPlaying.Text = "? " + I18n.T("Fichier introuvable");
            _nowPlaying.Foreground = B(RED);
            return;
        }

        await _vm.PlayFileAsync(path);
        _nowPlaying.Text = $"?  {System.IO.Path.GetFileNameWithoutExtension(name)}";
        _nowPlaying.Foreground = MainWindow.BR_ACCENT;
    }

    // Keyboard Enter: play highlighted item
    private void PlaySelectedKey()
    {
        PlayAt(_listView.SelectedIndex);
    }
}


// -- QuickSearchOverlay.cs ---------------------------------------

/// <summary>
/// Floating always-on-top overlay for quick sound search (library + PC files).
///
/// FIX: AppWindow.Hide() requires Windows App SDK 1.4+ and can be unreliable
/// on first call before the window is ever shown. We use ShowWindow P/Invoke
/// instead, which is always available and works immediately.
/// Visibility is tracked manually with _isVisible.
/// </summary>
public sealed class QuickSearchOverlay : Window
{
    [DllImport("user32.dll")] static extern bool ShowWindow(nint h, int n);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint h, nint hInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5;
    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;

    private readonly MainViewModel _vm;
    private readonly TextBox _searchBox;
    private readonly ListView _resultList;
    private readonly TextBlock _statusText;
    private readonly nint _hwnd;

    private record ResultItem(string Display, string FilePath, string? SoundId);

    private List<(string Name, string Path)> _pcFiles = new();
    private bool _pcScanned;
    private List<ResultItem> _filtered = new();
    private bool _isVisible;
    private bool _initialized;

    public QuickSearchOverlay(MainViewModel vm)
    {
        _vm = vm;
        Title = "";
        _hwnd = WindowNative.GetWindowHandle(this);

        var wid      = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        var appWindow = AppWindow.GetFromWindowId(wid);
        try {
            string ip = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (System.IO.File.Exists(ip)) appWindow.SetIcon(ip);
        } catch { }
        appWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            appWindow.TitleBar.ExtendsContentIntoTitleBar    = true;
            appWindow.TitleBar.ButtonBackgroundColor         = Windows.UI.Color.FromArgb(0,0,0,0);
            appWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0,0,0,0);
        }

        appWindow.Resize(new SizeInt32(480, 440));

        var root = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(245, 18, 18, 32)),
            Padding = new Thickness(12)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _searchBox = new TextBox
        {
            PlaceholderText = "\U0001F50D  Recherche rapide — bibliothèque + PC...",
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 30, 50)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 60, 100)),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _searchBox.TextChanged += (_, _) => RefreshResults(_searchBox.Text);
        _searchBox.KeyDown += OnSearchKeyDown;
        Grid.SetRow(_searchBox, 0);

        _resultList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)),
            ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection { new Microsoft.UI.Xaml.Media.Animation.RepositionThemeTransition() }
        };
        _resultList.DoubleTapped += (_, _) => PlaySelected();
        _resultList.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)  PlaySelected();
            if (e.Key == Windows.System.VirtualKey.Escape) HideOverlay();
        };
        Grid.SetRow(_resultList, 1);

        _statusText = new TextBlock
        {
            Text = "Double-clic ou Entrée pour jouer  ·  Esc pour fermer",
            Foreground = MainWindow.BR_TXT3,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };
        Grid.SetRow(_statusText, 2);

        root.Children.Add(_searchBox);
        root.Children.Add(_resultList);
        root.Children.Add(_statusText);
        Content = root;

        // Ensure topmost when shown
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
    }

    // -- Show / Hide -----------------------------------------------------------

    public void ShowOverlay()
    {
        if (!_initialized)
        {
            // First-ever show: activate once so WinUI initialises the rendering,
            // then immediately hide. Subsequent shows use ShowWindow for speed.
            _initialized = true;
            Activate();
            _searchBox.Text = "";
            RefreshResults("");
            _searchBox.Focus(FocusState.Programmatic);
            _isVisible = true;
            if (!_pcScanned) ScanPcInBackground();
            return;
        }

        _searchBox.Text = "";
        RefreshResults("");
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        ShowWindow(_hwnd, SW_SHOW);
        Activate();
        _searchBox.Focus(FocusState.Programmatic);
        _isVisible = true;

        if (!_pcScanned) ScanPcInBackground();
    }

    public void HideOverlay()
    {
        ShowWindow(_hwnd, SW_HIDE);
        _isVisible = false;
    }

    public void ToggleOverlay()
    {
        if (_isVisible) HideOverlay();
        else ShowOverlay();
    }

    // -- PC scan ---------------------------------------------------------------

    private void ScanPcInBackground()
    {
        _statusText.Text = "Scan du PC en cours...";
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            try
            {
                var roots   = PcScanService.GetScanRoots();
                var scanned = PcScanService.ScanAudioFiles(roots);
                dispatcher.TryEnqueue(() =>
                {
                    _pcFiles  = scanned;
                    _pcScanned = true;
                    _statusText.Text = "Double-clic ou Entrée pour jouer  ·  Esc pour fermer";
                    RefreshResults(_searchBox.Text);
                });
            }
            catch
            {
                dispatcher.TryEnqueue(() =>
                    _statusText.Text = "Double-clic ou Entrée pour jouer  ·  Esc pour fermer");
            }
        });
    }

    // -- Results ---------------------------------------------------------------

    private void RefreshResults(string query)
    {
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ResultItem>();

        // Library sounds first (favorites prioritized)
        foreach (var s in _vm.AllSounds.OrderByDescending(s => s.Favorite).ThenBy(s => s.Name))
        {
            if (!string.IsNullOrWhiteSpace(query) &&
                !s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            items.Add(new ResultItem($"{s.Name}   ·   {s.Collection}", s.Filepath, s.Id));
            if (!string.IsNullOrEmpty(s.Filepath)) seen.Add(s.Filepath);
        }

        // PC-scanned files � deduplicated
        foreach (var (name, path) in _pcFiles)
        {
            if (seen.Contains(path)) continue;
            var label  = System.IO.Path.GetFileNameWithoutExtension(name);
            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "");
            if (!string.IsNullOrWhiteSpace(query) &&
                !label.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

            items.Add(new ResultItem($"{label}   ·   {folder}", path, null));
        }

        _filtered = items.Take(100).ToList();
        _resultList.ItemsSource = _filtered.Select(i => i.Display).ToList();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape: HideOverlay(); break;
            case Windows.System.VirtualKey.Enter:  PlaySelected(); break;
            case Windows.System.VirtualKey.Down:   _resultList.Focus(FocusState.Keyboard); break;
        }
    }

    private async void PlaySelected()
    {
        // Default to first result when Enter is pressed without explicit selection
        int idx = _resultList.SelectedIndex;
        if (idx < 0 && _filtered.Count > 0) idx = 0;
        if (idx < 0 || idx >= _filtered.Count) return;

        var item = _filtered[idx];
        if (string.IsNullOrEmpty(item.FilePath)) return;

        if (item.SoundId != null)
        {
            var sound = _vm.AllSounds.FirstOrDefault(s => s.Id == item.SoundId);
            if (sound != null) await _vm.PlaySoundAsync(sound);
        }
        else
        {
            await _vm.PlayFileAsync(item.FilePath);
        }
        HideOverlay();
    }
}
} // namespace Soundeck.Views


// -- HotkeyDialog.cs ---------------------------------------------

namespace Soundeck.Views.Dialogs
{
/// <summary>
/// Hotkey assignment dialog for a single sound.
///
/// Key capture works by tracking currently held keys with a HashSet.
/// The combo is re-evaluated on every KeyDown so you always see the
/// full combination while keys are held.
/// A valid combo MUST contain at least one non-modifier key.
///
/// ALT+KEY "DING" FIX
/// ------------------
/// Setting e.Handled in WinUI suppresses the routed event but Windows still
/// posts WM_SYSCHAR / WM_SYSKEYDOWN to the HWND, which triggers the system
/// "ding". The only reliable fix is to swallow those messages in the window's
/// own WndProc before they reach DefWindowProc.
/// We subclass the ContentDialog's HWND (obtained after Loaded) and drop
/// WM_SYSCHAR (0x0106) and WM_SYSKEYDOWN (0x0104) entirely while the dialog
/// is open.
/// </summary>
public sealed class HotkeyDialog : ContentDialog
{
    // -- Win32 pour suppression du ding Alt ------------------------------------
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint h, int i, nint v);
    [DllImport("user32.dll")] private static extern nint CallWindowProc(nint p, nint h, uint m, nuint w, nint l);
    private const int GWLP_WNDPROC   = -4;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP   = 0x0105;
    private const uint WM_SYSCHAR    = 0x0106;

    private delegate nint WndProcDelegate(nint h, uint m, nuint w, nint l);
    private WndProcDelegate? _wndProc;
    private nint _oldWndProc;
    private nint _ownerHwnd;

    private nint DialogWndProc(nint h, uint m, nuint w, nint l)
    {
        if (m is WM_SYSKEYDOWN or WM_SYSKEYUP or WM_SYSCHAR)
            return 0;
        return CallWindowProc(_oldWndProc, h, m, w, l);
    }

    // -- Fields ----------------------------------------------------------------
    private readonly Sound _sound;
    private List<string>? _captured;
    private readonly TextBlock _hkLabel;
    private readonly TextBlock _hkHint;
    private readonly HashSet<VirtualKey> _heldNonMod = new();
    private readonly List<string> _heldModsOrdered = new(); // ordre de pression réel

    private static readonly Windows.UI.Color CARD   = Windows.UI.Color.FromArgb(255,30,30,50);
    private static readonly Windows.UI.Color BORDER = Windows.UI.Color.FromArgb(255,42,42,69);
    private static readonly Windows.UI.Color TXT    = Windows.UI.Color.FromArgb(255,232,232,248);
    private static readonly Windows.UI.Color TXT3   = Windows.UI.Color.FromArgb(255,96,96,128);

    public HotkeyDialog(Sound sound, nint ownerHwnd = 0)
    {
        _sound = sound;
        _ownerHwnd = ownerHwnd;
        Title = new TextBlock { Text = I18n.T("Raccourci") + $" - {sound.Name}", Foreground = MainWindow.BR_TXT };
        PrimaryButtonText = I18n.T("Enregistrer");
        SecondaryButtonText = I18n.T("Effacer");
        CloseButtonText = I18n.T("Annuler");
        DefaultButton = ContentDialogButton.Primary;
        Background = MainWindow.BR_CONTROL;
        BorderBrush = MainWindow.BR_ACCENT_DIM;
        BorderThickness = new Thickness(1);

        _captured = sound.Hotkey != null ? new List<string>(sound.Hotkey) : null;

        var badge = new Border
        {
            Background = MainWindow.BR_GLASSY,
            BorderBrush = MainWindow.BR_ACCENT_DIM,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 16, 0, 0)
        };

        _hkLabel = new TextBlock
        {
            Text = _captured != null
                ? HotkeyService.ComboToDisplayString(_captured)
                : I18n.T("Appuyez sur une combinaison..."),
            FontSize = 18,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            Foreground = _captured != null ? MainWindow.BR_ACCENT : new SolidColorBrush(TXT3),
            TextAlignment = TextAlignment.Center
        };
        badge.Child = _hkLabel;

        _hkHint = new TextBlock
        {
            Text = I18n.T("Maintenez les modificateurs (Ctrl, Alt, Shift) puis appuyez sur la touche."),
            Foreground = new SolidColorBrush(TXT3),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(badge);
        panel.Children.Add(_hkHint);
        Content = panel;

        SecondaryButtonClick += (_, _) => { _captured = null; _sound.Hotkey = null; };
        PrimaryButtonClick   += (_, _) => { if (_captured?.Count > 0) _sound.Hotkey = _captured; };

        // Subclass le HWND de la fenêtre parente pour avaler WM_SYSCHAR/WM_SYSKEYDOWN
        // (ContentDialog n'a pas son propre HWND � il partage celui de la Window parente).
        Loaded += (_, _) =>
        {
            try
            {
                if (_ownerHwnd == 0) return;
                _wndProc    = DialogWndProc;
                _oldWndProc = SetWindowLongPtr(_ownerHwnd, GWLP_WNDPROC,
                    System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc));
            }
            catch { }
        };
        // Restaurer le WndProc original à la fermeture
        Closed += (_, _) =>
        {
            try
            {
                if (_ownerHwnd != 0 && _oldWndProc != 0)
                    SetWindowLongPtr(_ownerHwnd, GWLP_WNDPROC, _oldWndProc);
            }
            catch { }
        };

        // AddHandler handledEventsToo=true so WinUI's own dialog shortcut handling
        // doesn't swallow our key events before we see them.
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        AddHandler(KeyUpEvent,   new KeyEventHandler(OnKeyUp),   handledEventsToo: true);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;

        var key = e.Key;

        // Normalise Left/Right variants ? token canonique
        string? modToken = key switch
        {
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "key:ctrl",
            VirtualKey.Menu    or VirtualKey.LeftMenu    or VirtualKey.RightMenu    => "key:alt",
            VirtualKey.Shift   or VirtualKey.LeftShift   or VirtualKey.RightShift   => "key:shift",
            VirtualKey.LeftWindows or VirtualKey.RightWindows                        => "key:cmd",
            _ => null
        };

        bool isMod = modToken != null;
        if (isMod)
        {
            // Ajouter dans l'ordre de pression si pas déjà pr�sent
            if (!_heldModsOrdered.Contains(modToken!))
                _heldModsOrdered.Add(modToken!);
        }
        else
        {
            _heldNonMod.Clear();
            _heldNonMod.Add(key);
        }

        bool hasNonMod = _heldNonMod.Count > 0;

        if (hasNonMod)
        {
            // Combo complet : mods dans l'ordre de saisie + touche principale
            var combo = new List<string>(_heldModsOrdered);
            foreach (var k in _heldNonMod) combo.Add(VkToToken(k));
            _captured           = combo;
            _hkLabel.Text       = string.Join(" + ", combo.Select(TokenToDisplay));
            _hkLabel.Foreground = MainWindow.BR_ACCENT;
            _hkHint.Text        = "? Relâchez les touches pour confirmer.";
        }
        else
        {
            // Afficher les mods dans l'ordre de saisie
            _hkLabel.Text = _heldModsOrdered.Count > 0
                ? string.Join(" + ", _heldModsOrdered.Select(TokenToDisplay)) + " + …"
                : I18n.T("Appuyez sur une combinaison...");
            _hkLabel.Foreground = new SolidColorBrush(TXT3);
        }
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        _heldNonMod.Remove(e.Key);

        string? modToken = e.Key switch
        {
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "key:ctrl",
            VirtualKey.Menu    or VirtualKey.LeftMenu    or VirtualKey.RightMenu    => "key:alt",
            VirtualKey.Shift   or VirtualKey.LeftShift   or VirtualKey.RightShift   => "key:shift",
            VirtualKey.LeftWindows or VirtualKey.RightWindows                        => "key:cmd",
            _ => null
        };
        if (modToken != null) _heldModsOrdered.Remove(modToken);
    }

    /// <summary>Preview en ordre de saisie (non utilisée mais conserv�e).</summary>
    private string? BuildModifierPreviewString()
    {
        return _heldModsOrdered.Count > 0
            ? string.Join(" + ", _heldModsOrdered.Select(TokenToDisplay)) + " + …"
            : null;
    }

    // -- Key mapping -----------------------------------------------------------

    /// <summary>Converts a WinUI VirtualKey to the string token used in config + HotkeyService.</summary>
    public static string VkToToken(VirtualKey key) => key switch
    {
        // Modifiers
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "key:ctrl",
        VirtualKey.Menu    or VirtualKey.LeftMenu    or VirtualKey.RightMenu    => "key:alt",
        VirtualKey.Shift   or VirtualKey.LeftShift   or VirtualKey.RightShift   => "key:shift",
        VirtualKey.LeftWindows or VirtualKey.RightWindows                        => "key:cmd",

        // Function keys F1-F24 (VK 112-135)
        _ when (int)key >= 112 && (int)key <= 135 => $"key:f{(int)key - 111}",

        // Letters A-Z (VK 65-90)
        _ when (int)key >= 65 && (int)key <= 90 => $"char:{(char)key}",

        // Top-row digits 0-9 (VK 48-57)
        _ when (int)key >= 48 && (int)key <= 57 => $"char:{(char)key}",

        // Numpad 0-9 (VK 96-105)
        _ when (int)key >= 96 && (int)key <= 105 => $"numpad:{(int)key - 96}",

        // Navigation / editing
        VirtualKey.Space      => "key:space",
        VirtualKey.Enter      => "key:enter",
        VirtualKey.Tab        => "key:tab",
        VirtualKey.Back       => "key:backspace",
        VirtualKey.Delete     => "key:delete",
        VirtualKey.Insert     => "key:insert",
        VirtualKey.Home       => "key:home",
        VirtualKey.End        => "key:end",
        VirtualKey.PageUp     => "key:page_up",
        VirtualKey.PageDown   => "key:page_down",
        VirtualKey.Up         => "key:up",
        VirtualKey.Down       => "key:down",
        VirtualKey.Left       => "key:left",
        VirtualKey.Right      => "key:right",
        VirtualKey.Escape     => "key:esc",
        VirtualKey.Pause      => "key:pause",

        // OEM keys � layout-independent by VK code
        (VirtualKey)0xBA => "vk:186", // ;:
        (VirtualKey)0xBB => "vk:187", // =+
        (VirtualKey)0xBC => "vk:188", // ,<
        (VirtualKey)0xBD => "vk:189", // -_
        (VirtualKey)0xBE => "vk:190", // .>
        (VirtualKey)0xBF => "vk:191", // /?
        (VirtualKey)0xC0 => "vk:192", // `~
        (VirtualKey)0xDB => "vk:219", // [{
        (VirtualKey)0xDC => "vk:220", // \|
        (VirtualKey)0xDD => "vk:221", // ]}
        (VirtualKey)0xDE => "vk:222", // '"

        // Fallback � raw VK code
        _ => $"vk:{(int)key}"
    };

    /// <summary>Human-readable label for a token (used for display in the badge).</summary>
    public static string TokenToDisplay(string token) => token switch
    {
        "key:ctrl"  => "Ctrl",
        "key:alt"   => "Alt",
        "key:shift" => "Shift",
        "key:cmd"   => "Win",
        "key:space" => "Space",
        "key:enter" => "Enter",
        "key:tab"   => "Tab",
        "key:backspace" => "Backspace",
        "key:delete"    => "Delete",
        "key:insert"    => "Ins",
        "key:home"      => "Home",
        "key:end"       => "End",
        "key:page_up"   => "PgUp",
        "key:page_down" => "PgDn",
        "key:up"    => "?",
        "key:down"  => "?",
        "key:left"  => "?",
        "key:right" => "?",
        "key:esc"   => "Esc",
        _ when token.StartsWith("key:f")   => token[4..].ToUpper(),
        _ when token.StartsWith("char:")   => token[5..].ToUpper(),
        _ when token.StartsWith("vk:")     => OemVkLabel(token[3..]),
        _ => token.ToUpper()
    };

    private static string OemVkLabel(string vkStr) => vkStr switch
    {
        "186" => ";", "187" => "=", "188" => ",", "189" => "-",
        "190" => ".", "191" => "/", "192" => "`", "219" => "[",
        "220" => "\\", "221" => "]", "222" => "'",
        _ => $"VK{vkStr}"
    };
}


// -- OnlineSearchPanel.cs ---------------------------------------

public sealed class OnlineSearchPanel : UserControl
{
    private readonly MainViewModel _vm;
    private readonly Action _onSoundAdded;
    private bool _isFreesound = true;

    private Button      _tabFs = null!, _tabYt = null!;
    private TextBox     _searchInput = null!;
    private ComboBox    _durationCombo = null!, _countCombo = null!;
    private StackPanel  _resultsList = null!;
    private ScrollViewer _resultsScroll = null!;
    private TextBlock   _noResultsText = null!, _statusText = null!;
    private StackPanel  _loadingPanel = null!;
    private TextBlock   _loadingText = null!;

    static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255,r,g,b);
    static Microsoft.UI.Xaml.Media.SolidColorBrush B(Windows.UI.Color c) => new(c);
    static readonly Windows.UI.Color CARD   = C(30,30,50);
    static readonly Windows.UI.Color BORDER = C(42,42,69);
    static readonly Windows.UI.Color TXT    = C(232,232,248);
    static readonly Windows.UI.Color TXT2   = C(144,144,176);
    static readonly Windows.UI.Color TXT3   = C(96,96,128);
    static readonly Windows.UI.Color RED    = C(239,68,68);
    static readonly Windows.UI.Color WHITE  = Windows.UI.Color.FromArgb(255,255,255,255);
    static readonly Windows.UI.Color TRANSP = Windows.UI.Color.FromArgb(0,0,0,0);

    public OnlineSearchPanel(MainViewModel vm, Action onSoundAdded)
    {
        _vm = vm;
        _onSoundAdded = onSoundAdded;
        Content = BuildContent();
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public void OnShow()
    {
        ClearResults();
        _searchInput.Text = "";
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Padding = new Thickness(16, 12, 16, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // tabs
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // search bar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // results
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status

        // Tabs
        var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,12) };
        _tabFs = TabBtn("\U0001F3B5 Freesound", true);
        _tabYt = TabBtn("\u25B6 YouTube", false);
        _tabFs.Click += (_, _) => SwitchTab(true);
        _tabYt.Click += (_, _) => SwitchTab(false);
        tabRow.Children.Add(_tabFs); tabRow.Children.Add(_tabYt);
        Grid.SetRow(tabRow, 0); root.Children.Add(tabRow);

        // Search bar � padding pour ne pas coller aux bords
        var searchRow = new Grid { Margin = new Thickness(0,0,0,12), ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _searchInput = new TextBox
        {
            PlaceholderText = I18n.T("Rechercher des sons..."),
            Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, Foreground = MainWindow.BR_TXT,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12,8,12,8)
        };
        _searchInput.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) _ = DoSearch(); };
        Grid.SetColumn(_searchInput, 0);

        _durationCombo = new ComboBox { MinWidth = 110, MinHeight = 36, Padding = new Thickness(8,6,8,6), Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1), Foreground = MainWindow.BR_TXT };
        _durationCombo.Items.Add(new ComboBoxItem { Content = "Max 10s",  Tag = "10"  });
        _durationCombo.Items.Add(new ComboBoxItem { Content = I18n.T("Max 30s"),  Tag = "30"  });
        _durationCombo.Items.Add(new ComboBoxItem { Content = "Max 60s",  Tag = "60"  });
        _durationCombo.Items.Add(new ComboBoxItem { Content = I18n.T("Illimité"), Tag = "600" });
        _durationCombo.SelectedIndex = 1;
        Grid.SetColumn(_durationCombo, 1);

        // S�lecteur du nombre de résultats � m�me style que _durationCombo
        _countCombo = new ComboBox { MinWidth = 100, MinHeight = 36, Padding = new Thickness(8,6,8,6), Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1), Foreground = MainWindow.BR_TXT };
        _countCombo.Items.Add(new ComboBoxItem { Content = I18n.T("10 résultats"), Tag = "10" });
        _countCombo.Items.Add(new ComboBoxItem { Content = I18n.T("15 résultats"), Tag = "15" });
        _countCombo.Items.Add(new ComboBoxItem { Content = I18n.T("25 résultats"), Tag = "25" });
        _countCombo.Items.Add(new ComboBoxItem { Content = I18n.T("50 résultats"), Tag = "50" });
        _countCombo.SelectedIndex = 1;
        Grid.SetColumn(_countCombo, 2);

        var searchBtn = new Button
        {
            Content = I18n.T("Rechercher"), Background = MainWindow.BR_ACCENT_TRANSLUCENT, Foreground = MainWindow.BR_ACCENT_TXT,
            BorderBrush = MainWindow.BR_ACCENT, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14,8,14,8), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        searchBtn.Click += async (_, _) => await DoSearch();
        Grid.SetColumn(searchBtn, 3);

        searchRow.Children.Add(_searchInput);
        searchRow.Children.Add(_durationCombo);
        searchRow.Children.Add(_countCombo);
        searchRow.Children.Add(searchBtn);
        Grid.SetRow(searchRow, 1); root.Children.Add(searchRow);

        // Results area
        var resultsArea = new Grid();

        _loadingPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 12, Visibility = Visibility.Collapsed };
        _loadingPanel.Children.Add(new ProgressRing { IsActive = true, Width = 40, Height = 40 });
        _loadingText = new TextBlock { Text = I18n.T("Recherche..."), Foreground = MainWindow.BR_TXT3, HorizontalAlignment = HorizontalAlignment.Center };
        _loadingPanel.Children.Add(_loadingText);

        _noResultsText = new TextBlock { Text = I18n.T("Recherchez des sons pour commencer"), Foreground = MainWindow.BR_TXT3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        _resultsList = new StackPanel { Spacing = 8, ChildrenTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection() };
        _resultsScroll = new ScrollViewer { Content = _resultsList, Visibility = Visibility.Collapsed, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };

        resultsArea.Children.Add(_loadingPanel);
        resultsArea.Children.Add(_noResultsText);
        resultsArea.Children.Add(_resultsScroll);
        Grid.SetRow(resultsArea, 2); root.Children.Add(resultsArea);

        // Status
        _statusText = new TextBlock { Margin = new Thickness(0,8,0,0), FontSize = 11, Foreground = MainWindow.BR_TXT3 };
        Grid.SetRow(_statusText, 3); root.Children.Add(_statusText);

        return root;
    }

    private Button TabBtn(string text, bool active) => new()
    {
        Content = text, Background = B(TRANSP),
        BorderBrush = active ? MainWindow.BR_ACCENT : B(TRANSP),
        BorderThickness = new Thickness(0,0,0,2),
        Foreground = active ? MainWindow.BR_ACCENT : MainWindow.BR_TXT2,
        Padding = new Thickness(16,8,16,8), FontSize = 13, CornerRadius = new CornerRadius(0)
    };

    private void SwitchTab(bool freesound)
    {
        _isFreesound = freesound;
        _tabFs.BorderBrush = freesound ? MainWindow.BR_ACCENT : B(TRANSP); _tabFs.Foreground = freesound ? MainWindow.BR_ACCENT : MainWindow.BR_TXT2;
        _tabYt.BorderBrush = freesound ? B(TRANSP) : MainWindow.BR_ACCENT; _tabYt.Foreground = freesound ? MainWindow.BR_TXT2 : MainWindow.BR_ACCENT;
        _searchInput.Text = "";
        _searchInput.PlaceholderText = freesound
            ? I18n.T("Rechercher des sons... (ex: explosion, pluie, applaudissements)")
            : I18n.T("Coller une URL YouTube ou saisir des mots-clés...");
        _durationCombo.Visibility = freesound ? Visibility.Visible : Visibility.Collapsed;
        ClearResults();
    }

    private async Task DoSearch()
    {
        var query = _searchInput.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;
        ClearResults();
        SetLoading(true);
        if (_isFreesound) await SearchFreesound(query);
        else await SearchYouTube(query);
        SetLoading(false);
    }

    private async Task SearchFreesound(string query)
    {
        var apiKey = _vm.Config.FreesoundApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            _statusText.Text = "⚠ Clé API Freesound requise — configurez-la dans Paramètres.";
            _noResultsText.Text = "⚠ Clé API Freesound requise — configurez-la dans Paramètres.";
            _noResultsText.Visibility = Visibility.Visible;
            return;
        }
        var tag = (_durationCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "30";
        float maxDur = float.Parse(tag);
        int maxResults = (_countCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() is string cs && int.TryParse(cs, out var cn) ? cn : 15;
        var results = await _vm.FreesoundSvc.SearchAsync(query, apiKey, maxDur, maxResults);

        if (results.Count == 0) { _noResultsText.Text = "Aucun résultat."; _noResultsText.Visibility = Visibility.Visible; _statusText.Text = "Aucun résultat."; return; }

        _statusText.Text = $"{results.Count} résultat(s)";
        _noResultsText.Visibility = Visibility.Collapsed;
        _resultsScroll.Visibility = Visibility.Visible;

        foreach (var r in results)
            _resultsList.Children.Add(BuildFreesoundCard(r));
    }

    private UIElement BuildFreesoundCard(FreesoundService.FreesoundResult r)
    {
        var border = new Border { Background = MainWindow.BR_CARD, CornerRadius = new CornerRadius(10), Padding = new Thickness(14,10,14,10) };
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 3 };
        info.Children.Add(new TextBlock { Text = r.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, Foreground = MainWindow.BR_TXT, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = $"{r.Username}  ·  {r.Duration:0.0}s  ·  ⭐ {r.AverageRating:0.0}", FontSize = 11, Foreground = MainWindow.BR_TXT3 });
        info.Children.Add(new TextBlock { Text = r.Tags, FontSize = 10, Foreground = MainWindow.BR_TXT3, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(info, 0);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var previewBtn = new Button { Content = "🔊 Aperçu", Padding = new Thickness(10,5,10,5), CornerRadius = new CornerRadius(6), FontSize = 11 };
        previewBtn.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.PreviewUrl) { UseShellExecute = true }); } catch { } };

        var addBtn = new Button { Content = "+ Ajouter", Background = MainWindow.BR_ACCENT_TRANSLUCENT, Foreground = MainWindow.BR_ACCENT_TXT, BorderBrush = MainWindow.BR_ACCENT, BorderThickness = new Thickness(1), Padding = new Thickness(10,5,10,5), CornerRadius = new CornerRadius(6), FontSize = 11 };
        addBtn.Click += async (_, _) =>
        {
            var col = await PickCollection(addBtn);
            if (col == null) return;
            addBtn.IsEnabled = false; addBtn.Content = "⏳..."; _statusText.Text = $"Téléchargement de '{r.Name}'...";
            var apiKey = _vm.Config.FreesoundApiKey ?? "";
            var ext = r.PreviewUrl.Contains(".mp3") ? ".mp3" : ".ogg";
            var dest = IO.Path.Combine(ConfigService.DownloadsDir, $"fs_{r.Id}{ext}");
            var result = await _vm.FreesoundSvc.DownloadAsync(r.PreviewUrl, apiKey, dest);
            if (result != null) { _vm.AddSound(result, r.Name, col); addBtn.Content = "✅ Ajouté"; _statusText.Text = $"? '{r.Name}' ajouté à '{col}'."; _onSoundAdded(); }
            else { addBtn.Content = "❌ Erreur"; addBtn.IsEnabled = true; _statusText.Text = "Échec du téléchargement."; }
        };

        btns.Children.Add(previewBtn); btns.Children.Add(addBtn);
        Grid.SetColumn(btns, 1);
        g.Children.Add(info); g.Children.Add(btns);
        border.Child = g;
        return border;
    }

    private async Task SearchYouTube(string query)
    {
        if (query.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { await DownloadYtUrl(query); return; }
        _statusText.Text = "Recherche YouTube...";
        int maxResults = (_countCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() is string cs && int.TryParse(cs, out var cn) ? cn : 15;
        var results = await YouTubeService.SearchAsync(query, maxResults);
        if (results.Count == 0) { _noResultsText.Text = "Aucun résultat (yt-dlp requis)."; _noResultsText.Visibility = Visibility.Visible; _statusText.Text = "Aucun résultat."; return; }
        _statusText.Text = $"{results.Count} résultat(s)";
        _noResultsText.Visibility = Visibility.Collapsed;
        _resultsScroll.Visibility = Visibility.Visible;
        foreach (var (title, id, duration, _) in results)
            _resultsList.Children.Add(BuildYtCard(title, duration, $"https://www.youtube.com/watch?v={id}"));
    }

    private UIElement BuildYtCard(string title, string duration, string url)
    {
        var border = new Border { Background = MainWindow.BR_CARD, CornerRadius = new CornerRadius(10), Padding = new Thickness(14,10,14,10) };
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { Spacing = 3 };
        info.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, Foreground = MainWindow.BR_TXT, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = duration, FontSize = 11, Foreground = MainWindow.BR_TXT3 });
        Grid.SetColumn(info, 0);
        var addBtn = new Button { Content = "? Télécharger", Background = MainWindow.BR_ACCENT_TRANSLUCENT, Foreground = MainWindow.BR_ACCENT_TXT, BorderBrush = MainWindow.BR_ACCENT, BorderThickness = new Thickness(1), Padding = new Thickness(10,5,10,5), CornerRadius = new CornerRadius(6), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        addBtn.Click += async (_, _) =>
        {
            var col = await PickCollection(addBtn);
            if (col == null) return;
            addBtn.IsEnabled = false; addBtn.Content = "⏳...";
            await DownloadYtUrl(url, title, addBtn, col);
        };
        Grid.SetColumn(addBtn, 1);
        g.Children.Add(info); g.Children.Add(addBtn);
        border.Child = g;
        return border;
    }

    private async Task DownloadYtUrl(string url, string? title = null, Button? btn = null, string? collection = null)
    {
        _statusText.Text = I18n.T("Téléchargement...");
        var prog = new Progress<string>(m => _statusText.Text = m);
        var (ok, filePath, err) = await YouTubeService.DownloadAsync(url, progress: prog);
        if (ok && filePath != null) { _vm.AddSound(filePath, title ?? IO.Path.GetFileNameWithoutExtension(filePath), collection); if (btn != null) btn.Content = "✅ Ajouté"; _statusText.Text = $"? Ajouté à '{collection}'."; _onSoundAdded(); }
        else { if (btn != null) { btn.Content = "❌ Erreur"; btn.IsEnabled = true; } _statusText.Text = $"Erreur: {err}"; }
    }

    private void ClearResults()
    {
        _resultsList.Children.Clear();
        _resultsScroll.Visibility = Visibility.Collapsed;
        _noResultsText.Visibility = Visibility.Visible;
        _noResultsText.Text = I18n.T("Recherchez des sons pour commencer");
        _statusText.Text = "";
    }

    private void SetLoading(bool loading)
    {
        _loadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading)
        {
            _noResultsText.Visibility = Visibility.Collapsed;
            _resultsScroll.Visibility = Visibility.Collapsed;
        }
        // Quand loading passe � false, on ne touche plus � _noResultsText / _resultsScroll :
        // SearchFreesound/SearchYouTube ont déjà mis le bon �tat (résultats visibles XOR message vide).
        _loadingText.Text = _isFreesound ? "Recherche sur Freesound..." : "Recherche sur YouTube...";
    }

    /// <summary>Affiche un menu flyout pour choisir la collection de destination.</summary>
    /// <returns>Le nom de la collection, ou null si annul�.</returns>
    private async Task<string?> PickCollection(FrameworkElement anchor)
    {
        var tcs = new TaskCompletionSource<string?>();
        var flyout = new MenuFlyout();
        foreach (var col in _vm.Collections)
        {
            var c = col;
            var item = new MenuFlyoutItem { Text = c };
            item.Click += (_, _) => tcs.TrySetResult(c);
            flyout.Items.Add(item);
        }
        // Option pour créer une nouvelle collection
        flyout.Items.Add(new MenuFlyoutSeparator());
        var newCol = new MenuFlyoutItem { Text = "+  " + I18n.T("Nouvelle collection") + "..." };
        newCol.Click += async (_, _) =>
        {
            var box = new TextBox { PlaceholderText = "Nom" };
            if (await DialogHelper.ShowCustomDialog(XamlRoot, I18n.T("Nouvelle collection"), box, I18n.T("Créer"), I18n.T("Annuler")) && !string.IsNullOrWhiteSpace(box.Text))
            {
                var name = box.Text.Trim();
                _vm.AddCollection(name);
                tcs.TrySetResult(name);
            }
            else tcs.TrySetResult(null);
        };
        flyout.Items.Add(newCol);

        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
        // Si l'user clique ailleurs, on annule après un d�lai
        flyout.Closed += (_, _) => tcs.TrySetResult(null);
        return await tcs.Task;
    }
}


// -- SettingsPanel.cs -------------------------------------------

public sealed class SettingsPanel : UserControl
{
    private const double PanelWidth = 720;

    static Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
    static Microsoft.UI.Xaml.Media.SolidColorBrush B(Windows.UI.Color c) => new(c);
    static readonly Windows.UI.Color WHITE  = Windows.UI.Color.FromArgb(255, 255, 255, 255);
    static readonly Windows.UI.Color TRANSP = Windows.UI.Color.FromArgb(0, 0, 0, 0);

    private readonly MainViewModel _vm;
    private readonly Action _onSaved;
    private readonly Action? _onThemeChanged;
    private readonly Action? _onRefreshGrid;
    private ComboBox _micCombo = null!, _vbCombo = null!, _monCombo = null!;
    private ToggleSwitch _autoDucking = null!;
    private Slider _monitorVolume = null!;
    private Slider _voiceTargetRatio = null!;
    private Slider _duckingStrength = null!;
    private ToggleSwitch _poly = null!, _norm = null!, _tray = null!, _startup = null!, _startMinimized = null!;
    private TextBox _freesoundKey = null!;
    private List<string>? _stopAllHotkey;
    private List<string>? _searchHotkey;
    private TextBlock _stopAllLabel = null!;
    private TextBlock _searchHotkeyLabel = null!;
    private Button _refreshBtn = null!;
    private Button _starterBtn = null!;
    private ComboBox _backdropCombo = null!;
    private Dictionary<string, Button> _accentButtons = null!;
    // Emp�che AutoSave()/RestartEngine() de se déclencher quand on change
    // SelectedItem par programme (refresh des combos), au lieu d'une vraie
    // interaction utilisateur. Avant, republier les combos d�clenchait
    // SelectionChanged -> AutoSave() -> RestartEngine() (synchrone, sur le
    // thread UI), ce qui freezait l'app pile au moment de l'affichage de la
    // page Paramètres.
    private bool _suppressAutoSave;

    public SettingsPanel(MainViewModel vm, Action onSaved, Action? onThemeChanged = null, Action? onRefreshGrid = null)
    {
        var __sw = System.Diagnostics.Stopwatch.StartNew();
        var __logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soundeck", "crash.log");
        void __Log(string msg) { try { System.IO.File.AppendAllText(__logPath, $"[Settings +{__sw.ElapsedMilliseconds}ms] {msg}\n"); } catch { } }
        __Log("ctor start");

        _vm = vm;
        _onSaved = onSaved;
        _onThemeChanged = onThemeChanged;
        _onRefreshGrid = onRefreshGrid;
        Content = BuildContent();
        __Log("BuildContent() done");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // Le cache de périphériques (AudioDeviceCache.WarmupAsync) est lanc� en
        // fire-and-forget au démarrage de l'app et peut ne pas être terminé quand
        // ce panneau est construit � ce qui laissait les combos vides ("rien
        // détecté") jusqu'� un clic manuel sur Actualiser. On force donc toujours
        // un refresh silencieux � l'ouverture du panneau, qui ré-énumère les vrais
        // périphériques Windows et republie les combos une fois pr�ts.
        _ = SilentInitialRefreshAsync().ContinueWith(_ => __Log("SilentInitialRefreshAsync() done"));
        __Log("ctor end (SilentInitialRefreshAsync fired)");
    }

    /// <summary>Appel� � chaque fois que l'utilisateur navigue vers l'onglet Paramètres
    /// (pas seulement � la cr�ation du panneau) � garantit que les périphériques
    /// branch�s/d�branch�s depuis la derni�re ouverture sont bien � jour.</summary>
    public void OnShow()
    {
        _ = SilentInitialRefreshAsync();
        if (_starterBtn != null)
        {
            bool installed = StarterPackService.IsInstalled(_vm);
            _starterBtn.Content = installed ? "✅  " + I18n.T("Starter Pack installé") : "🎁  " + I18n.T("Télécharger le Starter Pack");
            _starterBtn.IsEnabled = !installed;
        }
    }

    /// <summary>Attend que le cache de périphériques soit chaud (ou le r�chauffe
    /// lui-m�me s'il ne l'est pas encore), puis republie les combos sans toucher
    /// au bouton Actualiser ni perturber une sélection déjà en cours par l'utilisateur.</summary>
    public Task ForceRefresh() => RefreshDevicesAsync();

    private async Task SilentInitialRefreshAsync()
    {
        try
        {
            // Important : on attend WarmupAsync (qui ne fait la v�ritable �num�ration
            // WASAPI qu'une seule fois, gr�ce au cache interne) et non RefreshAsync
            // directement � sinon chaque ouverture de l'onglet Paramètres relancerait
            // une �num�ration compl�te et freezerait l'UI � chaque fois.
            await AudioDeviceCache.WarmupAsync();

            var prevMic = _micCombo.SelectedItem as string;
            var prevVb  = _vbCombo.SelectedItem as string;
            var prevMon = _monCombo.SelectedItem as string;

            var inputs  = CachedInputNames();
            var outputs = CachedOutputNames();

            // Republier ItemsSource/SelectedItem déclenche SelectionChanged m�me
            // quand la valeur effective ne change pas pour l'utilisateur (ex: passage
            // de I18n.T("(Aucun)") � la vraie valeur sauvegard�e au tout premier refresh).
            // On suspend AutoSave() le temps de cette mise � jour programmatique pour
            // éviter un RestartEngine() inattendu (et le freeze associ�) juste �
            // l'ouverture de la page.
            _suppressAutoSave = true;
            try
            {
                _micCombo.ItemsSource = inputs;
                _vbCombo.ItemsSource  = outputs;
                _monCombo.ItemsSource = outputs;

                // Priorit� : config (sauvegard�e) > sélection de session > I18n.T("(Aucun)").
                // Sur la toute premi�re ouverture au démarrage, le cache est froid et
                // prevMic vaut I18n.T("(Aucun)") (BuildContent avec cache vide) ; on pr�f�re
                // donc d'abord la config sauvegard�e, qui contient les vrais devices.
                _micCombo.SelectedItem = inputs.Contains(_vm.Config.MicDevice) ? _vm.Config.MicDevice : (inputs.Contains(prevMic) ? prevMic : I18n.T("(Aucun)"));
                _vbCombo.SelectedItem  = outputs.Contains(_vm.Config.VirtDevice) ? _vm.Config.VirtDevice : (outputs.Contains(prevVb) ? prevVb : I18n.T("(Aucun)"));
                _monCombo.SelectedItem = outputs.Contains(_vm.Config.MonitorDevice) ? _vm.Config.MonitorDevice : (outputs.Contains(prevMon) ? prevMon : I18n.T("(Aucun)"));
            }
            finally { _suppressAutoSave = false; }
        }
        catch { }
    }

    private UIElement BuildContent()
    {
        double W(double fr, double en) => I18n.CurrentLanguage == "en" ? en : fr;
        var scroll = new ScrollViewer { Padding = new Thickness(0, 0, 16, 0) };
        var mainStack = new StackPanel { Spacing = 24, Width = 650, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 16, 4, 48), Margin = new Thickness(32, 0, 0, 0) };
        Button StyledBtn(Button b) { 
            b.Background = MainWindow.BR_CONTROL; 
            b.BorderBrush = MainWindow.BR_ACCENT_DIM; 
            b.BorderThickness = new Thickness(1); 
            b.Foreground = MainWindow.BR_TXT; 
            b.VerticalContentAlignment = VerticalAlignment.Center;
            b.Padding = new Thickness(14, 5, 14, 7);
            b.Resources["ButtonBackgroundPointerOver"] = MainWindow.BR_ACCENT_DIM;
            b.Resources["ButtonBackgroundPressed"] = MainWindow.BR_ACCENT;
            b.Resources["ButtonForegroundPointerOver"] = MainWindow.BR_TXT;
            b.Resources["ButtonForegroundPressed"] = MainWindow.BR_TXT;
            b.Resources["ButtonBorderBrushPointerOver"] = MainWindow.BR_ACCENT_DIM;
            b.Resources["ButtonBorderBrushPressed"] = MainWindow.BR_ACCENT_DIM;
            return b; 
        }
        ComboBox StyledCb(ComboBox c) { c.Background = MainWindow.BR_CONTROL; c.BorderBrush = MainWindow.BR_ACCENT_DIM; c.BorderThickness = new Thickness(1); c.Foreground = MainWindow.BR_TXT; return c; }
        TextBox StyledTb(TextBox t) { t.Background = MainWindow.BR_CONTROL; t.BorderBrush = MainWindow.BR_ACCENT_DIM; t.BorderThickness = new Thickness(1); t.Foreground = MainWindow.BR_TXT; return t; }

        // Helper
        StackPanel CreateCard(string title, string description, params UIElement[] items)
        {
            var card = new StackPanel { 
                Background = MainWindow.BR_CONTROL, 
                CornerRadius = new CornerRadius(8), 
                Padding = new Thickness(24), 
                BorderBrush = MainWindow.BR_ACCENT_DIM,
                BorderThickness = new Thickness(1)
            };
            if (!string.IsNullOrEmpty(title))
                card.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = MainWindow.BR_TXT, Margin = new Thickness(0,0,0,description == null ? 16 : 4) });
            if (!string.IsNullOrEmpty(description))
                card.Children.Add(new TextBlock { Text = description, FontSize = 13, Foreground = MainWindow.BR_TXT3, Margin = new Thickness(0, 0, 0, 20), TextWrapping = TextWrapping.Wrap });

            var contentStack = new StackPanel { Spacing = 16 };
            foreach (var item in items) if (item != null) contentStack.Children.Add(item);
            card.Children.Add(contentStack);
            return card;
        }

        UIElement SettingRow(string label, FrameworkElement control, double colWidth = 220)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            
            var tb = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = MainWindow.BR_TXT2, FontSize = 14, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(tb, 0);
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            
            grid.Children.Add(tb);
            grid.Children.Add(control);
            return grid;
        }

        // 1. Audio Devices
        _micCombo = StyledCb(new ComboBox { Width = 340 }); _micCombo.SelectionChanged += (_, _) => AutoSave();
        _vbCombo  = StyledCb(new ComboBox { Width = 340 }); _vbCombo.SelectionChanged  += (_, _) => AutoSave();
        _monCombo = StyledCb(new ComboBox { Width = 340 }); _monCombo.SelectionChanged += (_, _) => AutoSave();
        
        _refreshBtn = StyledBtn(new Button { Content = BuildRefreshBtnContent(false), Padding = new Thickness(14, 8, 14, 8) });
        _refreshBtn.Click += async (_, _) => await RefreshDevicesAsync();

        var audioCard = CreateCard(I18n.T("Périphériques Audio"), I18n.T("Sélectionnez vos entrées et sorties pour le routage audio."),
            SettingRow(I18n.T("Microphone (Entrée) :"), _micCombo, W(190, 210)),
            SettingRow(I18n.T("Mic virtuel (VB-Cable) :"), _vbCombo, W(190, 210)),
            SettingRow(I18n.T("Sortie monitor :"), _monCombo, W(190, 210)),
            _refreshBtn
        );
        mainStack.Children.Add(audioCard);

        _poly = new ToggleSwitch { IsOn = _vm.Config.Polyphony }; _poly.Toggled += (_, _) => AutoSave();
        _norm = new ToggleSwitch { IsOn = _vm.Config.Normalize };
        _norm.Toggled += (_, _) =>
        {
            if (_suppressAutoSave) return;
            _vm.UpdateNormalization(_norm.IsOn);
            AutoSave();
        };
        
        _monitorVolume = new Slider { Minimum = 0, Maximum = 200, Value = _vm.Config.MonitorVolume * 100, Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var monVolVal = new TextBlock { Text = $"{_monitorVolume.Value:0}%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _monitorVolume.ValueChanged += (_, e) => { monVolVal.Text = $"{e.NewValue:0}%"; _vm.Engine.UpdateMonitorVolume((float)(e.NewValue / 100.0)); };
        _monitorVolume.PointerCaptureLost += (_, _) => SaveConfigOnly();
        var monPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        monPanel.Children.Add(_monitorVolume); monPanel.Children.Add(monVolVal);

        var playbackCard = CreateCard(I18n.T("Lecture & Volumes"), null,
            SettingRow(I18n.T("Polyphonie (plusieurs sons simultanés) :"), _poly, W(270, 290)),
            SettingRow(I18n.T("Normalisation automatique du volume :"), _norm, W(270, 290)),
            SettingRow(I18n.T("Volume de l'application (retour) :"), monPanel, W(270, 290))
        );
        mainStack.Children.Add(playbackCard);

        // 3. Microphone Calibration
        var calibBtn = StyledBtn(new Button { Content = I18n.T("Démarrer le Calibrage"), Width = 200 });
        var resetCalibBtn = StyledBtn(new Button { Content = I18n.T("Réinitialiser") });
        
        var calibBtnsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        calibBtnsPanel.Children.Add(calibBtn);
        calibBtnsPanel.Children.Add(resetCalibBtn);
        
        var calibProgress = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed };
        var calibResult = new TextBlock { Text = _vm.Config.MicPeakLevel > 0 ? $"{I18n.T("Niveau : ")}{(_vm.Config.MicPeakLevel*100):0}%" : "", Foreground = B(C(255, 200, 100)) };
        
        _voiceTargetRatio = new Slider { Minimum = 10, Maximum = 150, StepFrequency = 1, Value = _vm.Config.VoiceTargetRatio * 100, Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var targetRatioValue = new TextBlock { Text = $"{_voiceTargetRatio.Value:0}%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var ratioPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        ratioPanel.Children.Add(_voiceTargetRatio); ratioPanel.Children.Add(targetRatioValue);

        _voiceTargetRatio.ValueChanged += (_, e) => { 
            targetRatioValue.Text = $"{e.NewValue:0}%"; 
            _vm.Engine.UpdateVoiceTargetRatio((float)(e.NewValue / 100.0));
        };
        _voiceTargetRatio.PointerCaptureLost += (_, _) => SaveConfigOnly();

        var calibActionPanel = new StackPanel { Spacing = 12 };
        calibActionPanel.Children.Add(calibBtnsPanel);
        calibActionPanel.Children.Add(calibProgress);
        calibActionPanel.Children.Add(calibResult);

        var calibCard = CreateCard(I18n.T("Calibrage du micro"), I18n.T("Le calibrage de la voix permet d'équilibrer tous les sons pour qu'ils ne soient jamais plus forts que vous."),
            calibActionPanel,
            SettingRow(I18n.T("Volume des sons par rapport à la voix :"), ratioPanel, W(280, 290))
        );

        if (_vm.Config.MicPeakLevel <= 0)
        {
            _voiceTargetRatio.Value = 30; targetRatioValue.Text = "30%";
            _voiceTargetRatio.IsEnabled = false;
        }

        resetCalibBtn.Click += (_, _) =>
        {
            _vm.Config.MicPeakLevel = 0f;
            _voiceTargetRatio.Value = 30;
            targetRatioValue.Text = "30%";
            _voiceTargetRatio.IsEnabled = false;
            calibResult.Text = "";
            AutoSave();
        };

        calibBtn.Click += async (_, _) =>
        {
            calibBtn.IsEnabled = false;
            calibProgress.Visibility = Visibility.Visible;
            calibResult.Text = "";
            float maxMeasured = 0;
            for (int i = 40; i > 0; i--)
            {
                calibBtn.Content = $"Parlez... ({i/10.0:0.0}s)";
                await Task.Delay(100);
                float lvl = Soundeck.Services.AudioEngine.CurrentMicLevel;
                if (lvl > maxMeasured) maxMeasured = lvl;
                calibProgress.Value = lvl * 100;
            }
            if (maxMeasured <= 0) maxMeasured = 0.05f;
            _vm.Config.MicPeakLevel = maxMeasured;
            _voiceTargetRatio.IsEnabled = true;
            _voiceTargetRatio.Value = 85;
            targetRatioValue.Text = "85%";
            AutoSave();
            calibProgress.Visibility = Visibility.Collapsed;
            calibBtn.Content = I18n.T("Calibrage terminé !");
            calibResult.Text = $"{I18n.T("Niveau : ")}{(maxMeasured*100):0}%";
            await Task.Delay(2000);
            calibBtn.Content = I18n.T("Démarrer le Calibrage");
            calibBtn.IsEnabled = true;
        };
        mainStack.Children.Add(calibCard);

        // 4. Auto-Ducking
        _autoDucking = new ToggleSwitch { IsOn = _vm.Config.AutoDucking };
        _autoDucking.Toggled += (_, _) => SaveConfigOnly();
        
        _duckingStrength = new Slider { Minimum = 0.1, Maximum = 1.0, StepFrequency = 0.05, Value = _vm.Config.DuckingStrength, Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var duckingStrengthValue = new TextBlock { Text = $"{(_duckingStrength.Value*100):0}%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _duckingStrength.ValueChanged += (_, e) => { duckingStrengthValue.Text = $"{(e.NewValue*100):0}%"; };
        _duckingStrength.PointerCaptureLost += (_, _) => SaveConfigOnly();
        var duckPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        duckPanel.Children.Add(_duckingStrength); duckPanel.Children.Add(duckingStrengthValue);

        var duckTip = new TextBlock { 
            Text = I18n.T("Astuce : Réglez le volume des sons par rapport à la voix sur 100% (rubrique Calibrage)."), 
            FontSize = 12, Foreground = MainWindow.BR_TXT3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,-8,0,0) 
        };

        var duckingCard = CreateCard(I18n.T("Auto-Ducking"), I18n.T("Baisse automatiquement le volume des sons lorsque vous parlez."),
            SettingRow(I18n.T("Activer l'Auto-Ducking :"), _autoDucking, W(180, 190)),
            duckTip,
            SettingRow(I18n.T("Force de l'atténuation :"), duckPanel, W(180, 190))
        );
        mainStack.Children.Add(duckingCard);

        // 5. Hotkeys
        _stopAllHotkey = _vm.Config.StopAllHotkey;
        _searchHotkey = _vm.Config.SearchHotkey;
        _stopAllLabel = new TextBlock { Text = DisplayOrNone(_stopAllHotkey), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _searchHotkeyLabel = new TextBlock { Text = DisplayOrNone(_searchHotkey), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

        var hotkeysCard = CreateCard(I18n.T("Raccourcis globaux"), null,
            BuildHotkeySection(I18n.T("Stopper tous les sons :"), _stopAllLabel,
                hk => { _stopAllHotkey = hk; _stopAllLabel.Text = DisplayOrNone(hk); SaveConfigOnly(); },
                () => { _stopAllHotkey = null; _stopAllLabel.Text = I18n.T("(aucun)"); SaveConfigOnly(); }),
            BuildHotkeySection(I18n.T("Ouvrir la barre de recherche :"), _searchHotkeyLabel,
                hk => { _searchHotkey = hk; _searchHotkeyLabel.Text = DisplayOrNone(hk); SaveConfigOnly(); },
                () => { _searchHotkey = null; _searchHotkeyLabel.Text = I18n.T("(aucun)"); SaveConfigOnly(); })
        );
        mainStack.Children.Add(hotkeysCard);

        // 6. Integrations
        _freesoundKey = StyledTb(new TextBox { Text = _vm.Config.FreesoundApiKey ?? "", Width = 300, PlaceholderText = I18n.T("Clé API Freesound") });
        _freesoundKey.LostFocus += (_, _) => SaveConfigOnly();
        var fsBtn = StyledBtn(new Button { Content = I18n.T("Obtenir une clé") });
        fsBtn.Click += async (_, _) => { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://freesound.org/help/developers/")); };
        var fsStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fsStack.Children.Add(_freesoundKey); fsStack.Children.Add(fsBtn);
        var integrationCard = CreateCard(I18n.T("Intégrations (Optionnel)"), I18n.T("Clés API pour les recherches en ligne."),
            SettingRow(I18n.T("Clé API Freesound :"), fsStack, W(130, 160))
        );
        mainStack.Children.Add(integrationCard);

        // 7. System
        _tray = new ToggleSwitch { IsOn = _vm.Config.MinimizeToTray }; _tray.Toggled += (_, _) => { _startMinimized.IsEnabled = _tray.IsOn && _startup.IsOn; if(!_tray.IsOn) _startMinimized.IsOn = false; SaveConfigOnly(); };
        _startup = new ToggleSwitch { IsOn = _vm.Config.StartWithWindows }; 
        _startup.Toggled += async (_, _) => 
        { 
            bool wasOff = !_vm.Config.StartWithWindows;
            _startMinimized.IsEnabled = _tray.IsOn && _startup.IsOn; 
            if(!_startup.IsOn) _startMinimized.IsOn = false; 
            SaveConfigOnly(); 
            
            if (wasOff && _startup.IsOn)
            {
                var txt = new TextBlock { Text = I18n.T("Si le dossier de l'application est déplacé, il suffit de relancer l'application manuellement pour mettre à jour le chemin de démarrage."), TextWrapping = TextWrapping.Wrap };
                await DialogHelper.ShowCustomDialog(XamlRoot, I18n.T("Démarrage avec Windows"), txt, "OK");
            }
        };
        _startMinimized = new ToggleSwitch { IsOn = _vm.Config.StartMinimizedInTray, IsEnabled = _tray.IsOn && _startup.IsOn }; _startMinimized.Toggled += (_, _) => SaveConfigOnly();
        
        var coverToggle = new ToggleSwitch { IsOn = _vm.Config.AutoFetchCovers };
        coverToggle.Toggled += (_, _) => { _vm.Config.AutoFetchCovers = coverToggle.IsOn; _vm.Save(); _onRefreshGrid?.Invoke(); };
        
        var langCombo = StyledCb(new ComboBox { Width = 140 });
        langCombo.Items.Add("Français");
        langCombo.Items.Add("English");
        langCombo.SelectedItem = _vm.Config.Language == "en" ? "English" : "Français";
        langCombo.SelectionChanged += async (_, _) => {
            if (langCombo.SelectedItem == null) return;
            string sel = langCombo.SelectedItem.ToString();
            string code = sel == "English" ? "en" : "fr";
            if (_vm.Config.Language != code)
            {
                _vm.Config.Language = code;
                SaveConfigOnly();
                var txt = new TextBlock { Text = "Veuillez redémarrer l'application pour appliquer le changement de langue. / Please restart the app to apply language changes.", TextWrapping = TextWrapping.Wrap };
                await DialogHelper.ShowCustomDialog(XamlRoot, I18n.T("Langue"), txt, "OK");
            }
        };

        var sysCard = CreateCard(I18n.T("Système & Options"), null,
            SettingRow(I18n.T("Langue :"), langCombo, 310),
            SettingRow(I18n.T("Lancer au démarrage de Windows :"), _startup, 310),
            SettingRow(I18n.T("Réduire dans la zone de notification (Tray) :"), _tray, 310),
            SettingRow(I18n.T("Démarrer réduit dans la zone de notification :"), _startMinimized, 310),
            SettingRow(I18n.T("Pochettes automatiques (iTunes) :"), coverToggle, 310)
        );
        mainStack.Children.Add(sysCard);

        // 8. Starter Pack
        var starterProgress = new ProgressBar { IsIndeterminate = false, Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed };
        var starterStatus = new TextBlock { Foreground = MainWindow.BR_TXT3, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _starterBtn = StyledBtn(new Button { Content = StarterPackService.IsInstalled(_vm) ? "✅  " + I18n.T("Starter Pack installé") : "🎁  " + I18n.T("Télécharger le Starter Pack"), IsEnabled = !StarterPackService.IsInstalled(_vm) });
        _starterBtn.Click += async (s, _) =>
        {
            _starterBtn.IsEnabled = false; _starterBtn.Content = I18n.T("Téléchargement...");
            starterProgress.Visibility = Visibility.Visible;
            var progress = new Progress<(int Current, int Total, string Name)>(p =>
            {
                starterProgress.Maximum = p.Total;
                starterProgress.Value = p.Current;
                starterStatus.Text = $"{p.Current}/{p.Total} - {p.Name}";
            });
            var (ok, err) = await StarterPackService.DownloadAsync(progress);
            starterProgress.Visibility = Visibility.Collapsed;
            if (ok) {
                _starterBtn.Content = "✅ " + I18n.T("Starter Pack installé");
                starterStatus.Text = I18n.T("Sons ajoutés à la collection par défaut !");
                if (!_vm.Collections.Contains(StarterPackService.CollectionName)) _vm.AddCollection(StarterPackService.CollectionName);
                var existingIds = _vm.AllSounds.Select(s2 => s2.Filepath).ToHashSet();
                foreach (var sound in StarterPackService.GetDownloadedSounds()) {
                    if (!existingIds.Contains(sound.Filepath)) {
                        sound.Collection = StarterPackService.CollectionName;
                        _vm.AllSounds.Add(sound);
                    }
                }
                _vm.Save();
                _onRefreshGrid?.Invoke();
            } else {
                _starterBtn.Content = "\U0001F504  " + I18n.T("Réessayer"); _starterBtn.IsEnabled = true;
                starterStatus.Text = err;
            }
        };
        var starterCard = CreateCard(I18n.T("Contenu Additionnel"), null,
            _starterBtn,
            starterProgress,
            starterStatus
        );
        mainStack.Children.Add(starterCard);

        // 9. Appearance
        _backdropCombo = StyledCb(new ComboBox { Width = 140 });
        _backdropCombo.Items.Add(I18n.T("Mica"));
        _backdropCombo.Items.Add(I18n.T("Mica Alt"));
        _backdropCombo.Items.Add(I18n.T("Acrylique"));
        _backdropCombo.Items.Add(I18n.T("Solide"));
        _backdropCombo.SelectedItem = _vm.Config.BackdropMode switch { "acrylic" => I18n.T("Acrylique"), "mica_alt" => I18n.T("Mica Alt"), "solid" => I18n.T("Solide"), _ => I18n.T("Mica") };
        _backdropCombo.SelectionChanged += (_, _) => AutoSave();

        var accentPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _accentButtons = new Dictionary<string, Button>();
        foreach (var kv in MainWindow.AccentPresets)
        {
            if (kv.Key == "blanc") continue;
            var btn = new Button { Background = B(kv.Value.Accent), Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Padding = new Thickness(0), BorderThickness = new Thickness(2), BorderBrush = B(_vm.Config.AccentColor == kv.Key ? WHITE : TRANSP) };
            string n = kv.Key; btn.Click += (_, _) => SelectAccent(n);
            _accentButtons[n] = btn;
            accentPanel.Children.Add(btn);
        }

        var accentLabel = new TextBlock { Text = I18n.T("Couleur d'accentuation :"), VerticalAlignment = VerticalAlignment.Center, Foreground = MainWindow.BR_TXT2, FontSize = 14 };

        var themeCard = CreateCard(I18n.T("Apparence"), null,
            SettingRow(I18n.T("Effet de fond :"), _backdropCombo, W(110, 140)),
            accentLabel,
            accentPanel
        );
        mainStack.Children.Add(themeCard);

        scroll.Content = mainStack;
        return scroll;
    }

    private void SelectAccent(string name)
    {
        foreach (var kv in _accentButtons)
            kv.Value.BorderBrush = new SolidColorBrush(kv.Key == name ? WHITE : TRANSP);
        _vm.Config.AccentColor = name;
        _onThemeChanged?.Invoke();
        AutoSave();
    }

    /// <summary>Sauvegarde automatiquement tous les changements (auto-save).</summary>
    
    private void SaveConfigOnly()
    {
        if (_suppressAutoSave) return;
        var cfg = _vm.Config;
        cfg.MonitorVolume = (float)(_monitorVolume.Value / 100.0);
        cfg.VoiceTargetRatio = (float)(_voiceTargetRatio.Value / 100.0);
        cfg.AutoDucking   = _autoDucking.IsOn;
        cfg.DuckingStrength = (float)_duckingStrength.Value;
        
        cfg.StopAllHotkey = _stopAllHotkey;
        cfg.SearchHotkey  = _searchHotkey;
        cfg.MinimizeToTray      = _tray.IsOn;
        cfg.StartWithWindows    = _startup.IsOn;
        cfg.StartMinimizedInTray = _tray.IsOn && _startMinimized.IsOn;
        cfg.FreesoundApiKey  = string.IsNullOrWhiteSpace(_freesoundKey.Text) ? null : _freesoundKey.Text.Trim();

        _vm.Save();
        _onSaved?.Invoke();
    }

    private async void AutoSave()
    {
        // Ignorer les changements d�clench�s par programme (refresh des combos),
        // qui ne doivent pas redémarrer le moteur audio. Voir _suppressAutoSave.
        if (_suppressAutoSave) return;

        var cfg = _vm.Config;
        var prevMic = cfg.MicDevice;
        var prevVb  = cfg.VirtDevice;
        var prevMon = cfg.MonitorDevice;

        var newMic = _micCombo.SelectedItem as string == I18n.T("(Aucun)") ? null : _micCombo.SelectedItem as string;
        var newVb  = _vbCombo.SelectedItem  as string == I18n.T("(Aucun)") ? null : _vbCombo.SelectedItem  as string;
        var newMon = _monCombo.SelectedItem as string == I18n.T("(Aucun)") ? null : _monCombo.SelectedItem as string;

        bool devicesChanged = (newMic != prevMic) || (newVb != prevVb) || (newMon != prevMon);
        bool shouldRestartEngine = devicesChanged || (!_vm.IsEngineRunning && newMic != null && newVb != null && newMon != null);

        cfg.MicDevice     = newMic;
        cfg.VirtDevice    = newVb;
        cfg.MonitorDevice = newMon;
        cfg.Polyphony     = _poly.IsOn;
        if (cfg.Normalize != _norm.IsOn)
        {
            cfg.Normalize = _norm.IsOn;
            _vm.Engine.UpdateNormalization(cfg.Normalize);
        }
        cfg.MonitorVolume = (float)(_monitorVolume.Value / 100.0);
        cfg.VoiceTargetRatio = (float)(_voiceTargetRatio.Value / 100.0);
        cfg.AutoDucking   = _autoDucking.IsOn;
        cfg.DuckingStrength = (float)_duckingStrength.Value;
        _vm.Engine.UpdateMonitorVolume(cfg.MonitorVolume);
        
        cfg.StopAllHotkey = _stopAllHotkey;
        cfg.SearchHotkey  = _searchHotkey;
        cfg.MinimizeToTray      = _tray.IsOn;
        cfg.StartWithWindows    = _startup.IsOn;
        cfg.StartMinimizedInTray = _tray.IsOn && _startMinimized.IsOn;
        cfg.FreesoundApiKey  = string.IsNullOrWhiteSpace(_freesoundKey.Text) ? null : _freesoundKey.Text.Trim();

        var sel = (string)_backdropCombo.SelectedItem;
        if (sel == I18n.T("Mica")) cfg.BackdropMode = "mica";
        else if (sel == I18n.T("Mica Alt")) cfg.BackdropMode = "mica_alt";
        else if (sel == I18n.T("Acrylique")) cfg.BackdropMode = "acrylic";
        else cfg.BackdropMode = "solid";
        cfg.AccentColor = _vm.Config.AccentColor;

        // Startup registry
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null)
            {
                if (cfg.StartWithWindows)
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    var arg = cfg.StartMinimizedInTray ? " --minimized" : "";
                    key.SetValue("Soundeck", $"\"{exePath}\"{arg}");
                }
                else key.DeleteValue("Soundeck", false);
            }
        }
        catch { }

        _vm.Engine.Polyphony = cfg.Polyphony;
        _vm.Engine.Normalize = cfg.Normalize;
        _vm.Save();
        _onThemeChanged?.Invoke();

        // RestartEngineAsync() s'ex�cute sur un thread STA dédié (voir
        // MainViewModel.RunOnAudioThreadAsync) : le thread UI reste r�actif
        // pendant toute la r�initialisation WASAPI, au lieu de freezer comme
        // avec l'ancien RestartEngine() synchrone appel� directement ici.
        if (shouldRestartEngine)
        {
            await _vm.RestartEngineAsync();
        }
        _onSaved();
    }

    // -- Device cache helpers -------------------------------------------------
    private static List<string> CachedInputNames() =>
        AudioDeviceCache.Inputs.Select(d => d.Name).Prepend(I18n.T("(Aucun)")).ToList();

    private static List<string> CachedOutputNames() =>
        AudioDeviceCache.Outputs.Select(d => d.Name).Prepend(I18n.T("(Aucun)")).ToList();

    private FrameworkElement BuildRefreshBtnContent(bool refreshing)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 8 };
        if (refreshing)
        {
            var ring = new ProgressRing { Width = 14, Height = 14, IsActive = true, VerticalAlignment = VerticalAlignment.Center };
            var txt  = new TextBlock { Text = I18n.T("Actualisation..."), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(ring); row.Children.Add(txt);
        }
        else
        {
            var icon = new FontIcon { Glyph = "\uE72C", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var txt  = new TextBlock { Text = I18n.T("Actualiser les périphériques"), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon); row.Children.Add(txt);
        }
        return row;
    }

    private async Task RefreshDevicesAsync()
    {
        // Preserve current selections — previously this reset every combo to
        // I18n.T("(Aucun)") because re-assigning ItemsSource drops the selection.
        var prevMic = _micCombo.SelectedItem as string;
        var prevVb  = _vbCombo.SelectedItem as string;
        var prevMon = _monCombo.SelectedItem as string;

        _refreshBtn.IsEnabled = false;
        _refreshBtn.Content = BuildRefreshBtnContent(true);

        await AudioDeviceCache.RefreshAsync();

        var inputs  = CachedInputNames();
        var outputs = CachedOutputNames();

        // Voir le commentaire dans SilentInitialRefreshAsync : republier les combos
        // ne doit pas déclencher AutoSave()/RestartEngine() tout seul.
        _suppressAutoSave = true;
        try
        {
            _micCombo.ItemsSource = inputs;
            _vbCombo.ItemsSource  = outputs;
            _monCombo.ItemsSource = outputs;

            _micCombo.SelectedItem = inputs.Contains(prevMic)  ? prevMic  : I18n.T("(Aucun)");
            _vbCombo.SelectedItem  = outputs.Contains(prevVb)  ? prevVb  : I18n.T("(Aucun)");
            _monCombo.SelectedItem = outputs.Contains(prevMon) ? prevMon : I18n.T("(Aucun)");
        }
        finally { _suppressAutoSave = false; }

        _refreshBtn.IsEnabled = true;
        _refreshBtn.Content = BuildRefreshBtnContent(false);
    }

    private StackPanel BuildHotkeySection(string label, TextBlock valueLabel, Action<List<string>?> onSet, Action onClear)
    {
        var outer = new StackPanel { Spacing = 6 };

        // -- Static row ----------------------------------------------------
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        var badge = new Border
        {
            Background = MainWindow.BR_GLASSY,
            BorderBrush = MainWindow.BR_ACCENT_DIM,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10,6,10,6), Child = valueLabel, MaxWidth = 220,
            Margin = new Thickness(0,0,12,0)
        };
        var setBtn   = new Button { Content = I18n.T("Définir"), Padding = new Thickness(10,5,10,5), CornerRadius = new CornerRadius(6), Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1), Foreground = MainWindow.BR_TXT };
        setBtn.Resources["ButtonBackgroundPointerOver"] = MainWindow.BR_ACCENT_DIM;
        setBtn.Resources["ButtonBackgroundPressed"] = MainWindow.BR_ACCENT;
        setBtn.Resources["ButtonForegroundPointerOver"] = MainWindow.BR_TXT;
        setBtn.Resources["ButtonForegroundPressed"] = MainWindow.BR_TXT;
        setBtn.Resources["ButtonBorderBrushPointerOver"] = MainWindow.BR_ACCENT_DIM;
        setBtn.Resources["ButtonBorderBrushPressed"] = MainWindow.BR_ACCENT_DIM;
        
        var clearBtn = new Button { Content = I18n.T("Effacer"), Padding = new Thickness(10,5,10,5), CornerRadius = new CornerRadius(6), Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1), Foreground = MainWindow.BR_TXT };
        clearBtn.Resources["ButtonBackgroundPointerOver"] = MainWindow.BR_ACCENT_DIM;
        clearBtn.Resources["ButtonBackgroundPressed"] = MainWindow.BR_ACCENT;
        clearBtn.Resources["ButtonForegroundPointerOver"] = MainWindow.BR_TXT;
        clearBtn.Resources["ButtonForegroundPressed"] = MainWindow.BR_TXT;
        clearBtn.Resources["ButtonBorderBrushPointerOver"] = MainWindow.BR_ACCENT_DIM;
        clearBtn.Resources["ButtonBorderBrushPressed"] = MainWindow.BR_ACCENT_DIM;
        
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        btnRow.Children.Add(setBtn); btnRow.Children.Add(clearBtn);

        Grid.SetColumn(lbl, 0); Grid.SetColumn(badge, 1); Grid.SetColumn(btnRow, 2);
        row.Children.Add(lbl); row.Children.Add(badge); row.Children.Add(btnRow);

        // -- Inline capture panel ------------------------------------------
        var capturePanel = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255,20,20,40)),
            BorderBrush = MainWindow.BR_ACCENT,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            IsTabStop = true,
            TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Once
        };

        var capInner = new Grid();
        capInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        capInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var capLabel = new TextBlock
        {
            Text = I18n.T("Appuyez sur une combinaison..."),
            FontSize = 13,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas, monospace"),
            Foreground = MainWindow.BR_TXT3,
            VerticalAlignment = VerticalAlignment.Center
        };
        var okBtn = new Button
        {
            Content = "OK", IsEnabled = false,
            Padding = new Thickness(12,5,12,5), CornerRadius = new CornerRadius(6),
            Background = MainWindow.BR_ACCENT_TRANSLUCENT, Foreground = MainWindow.BR_ACCENT_TXT, BorderBrush = MainWindow.BR_ACCENT, BorderThickness = new Thickness(1)
        };
        Grid.SetColumn(capLabel, 0); Grid.SetColumn(okBtn, 1);
        capInner.Children.Add(capLabel); capInner.Children.Add(okBtn);

        capturePanel.Child = capInner;

        // -- Key capture via P/Invoke GetKeyState � fiable pour Alt, lettres, etc. --
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetKeyState(int vk);
        static bool IsDown(Windows.System.VirtualKey vk) => (GetKeyState((int)vk) & 0x8000) != 0;

        var held    = new HashSet<Windows.System.VirtualKey>();
        List<string>? pending = null;
        bool capturing = false;

        void RebuildCombo()
        {
            // Modifiers via GetKeyState (fiable m�me dans un ContentDialog)
            var combo = new List<string>();
            if (IsDown(Windows.System.VirtualKey.Control) || IsDown(Windows.System.VirtualKey.LeftControl) || IsDown(Windows.System.VirtualKey.RightControl))
                combo.Add("key:ctrl");
            if (IsDown(Windows.System.VirtualKey.Menu) || IsDown(Windows.System.VirtualKey.LeftMenu) || IsDown(Windows.System.VirtualKey.RightMenu))
                combo.Add("key:alt");
            if (IsDown(Windows.System.VirtualKey.Shift) || IsDown(Windows.System.VirtualKey.LeftShift) || IsDown(Windows.System.VirtualKey.RightShift))
                combo.Add("key:shift");
            if (IsDown(Windows.System.VirtualKey.LeftWindows) || IsDown(Windows.System.VirtualKey.RightWindows))
                combo.Add("key:cmd");

            foreach (var k in held)
                combo.Add(HotkeyDialog.VkToToken(k));

            bool hasNonMod = combo.Any(t => t is not ("key:ctrl" or "key:alt" or "key:shift" or "key:cmd"));
            if (hasNonMod)
            {
                pending = combo;
                capLabel.Text = HotkeyService.ComboToDisplayString(pending);
                capLabel.Foreground = MainWindow.BR_ACCENT;
                okBtn.IsEnabled = true;
            }
            else
            {
                var modNames = combo.Select(t => t switch { "key:ctrl" => "Ctrl", "key:alt" => "Alt", "key:shift" => "Shift", "key:cmd" => "Win", _ => t });
                capLabel.Text = combo.Count > 0 ? string.Join(" + ", modNames) + " + …" : I18n.T("Appuyez sur une combinaison...");
                capLabel.Foreground = MainWindow.BR_TXT3;
            }
        }

        // Using AddHandler with handledEventsToo:true ensures we receive ALL key events
        // including Alt+key combinations that WinUI would otherwise swallow as menu
        // accelerators � and setting e.Handled=true FIRST suppresses the Windows "ding".
        capturePanel.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
        {
            if (!capturing) return;
            e.Handled = true; // MUST be first to suppress Alt+key ding
            var key = e.Key;
            bool isMod = key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.LeftControl or Windows.System.VirtualKey.RightControl
                              or Windows.System.VirtualKey.Menu    or Windows.System.VirtualKey.LeftMenu    or Windows.System.VirtualKey.RightMenu
                              or Windows.System.VirtualKey.Shift   or Windows.System.VirtualKey.LeftShift   or Windows.System.VirtualKey.RightShift
                              or Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows;
            if (!isMod) { held.Clear(); held.Add(key); }
            RebuildCombo();
        }), handledEventsToo: true);
        capturePanel.AddHandler(UIElement.KeyUpEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
        {
            if (!capturing) return;
            e.Handled = true;
            held.Remove(e.Key);
        }), handledEventsToo: true);

        setBtn.Click += (_, _) =>
        {
            pending = null; held.Clear(); capturing = true;
            capLabel.Text = I18n.T("Appuyez sur une combinaison...");
            capLabel.Foreground = MainWindow.BR_TXT3;
            okBtn.IsEnabled = false;
            capturePanel.Visibility = Visibility.Visible;
            setBtn.IsEnabled = false;
            capturePanel.Focus(FocusState.Programmatic);
        };

        okBtn.Click += (_, _) =>
        {
            capturing = false;
            capturePanel.Visibility = Visibility.Collapsed;
            setBtn.IsEnabled = true;
            if (pending?.Count > 0) onSet(pending);
        };

        clearBtn.Click += (_, _) =>
        {
            capturing = false;
            capturePanel.Visibility = Visibility.Collapsed;
            setBtn.IsEnabled = true;
            onClear();
        };

        outer.Children.Add(row);
        outer.Children.Add(capturePanel);
        return outer;
    }

    private static string DisplayOrNone(List<string>? hotkey) =>
        HotkeyService.ComboToDisplayString(hotkey) is { Length: > 0 } s ? s : I18n.T("(aucun)");

    // helpers
    private static TextBlock Section(string t) => new() { Text = t, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = MainWindow.BR_TXT };
    private static Border Divider() => new() { Height = 1, Background = MainWindow.BR_ACCENT_DIM };

    private static ComboBox Combo(List<string> items, string selected)
    {
        return new ComboBox
        {
            ItemsSource = items,
            SelectedItem = (!string.IsNullOrEmpty(selected) && items.Contains(selected)) ? selected : items[0],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = MainWindow.BR_CONTROL, BorderBrush = MainWindow.BR_ACCENT_DIM, BorderThickness = new Thickness(1), Foreground = MainWindow.BR_TXT
        };
    }

    private static Grid LabeledRow(string label, FrameworkElement ctrl)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Opacity = 0.7 };
        Grid.SetColumn(lbl, 0); Grid.SetColumn(ctrl, 1);
        g.Children.Add(lbl); g.Children.Add(ctrl);
        return g;
    }

    private static Border WrapToggle(ToggleSwitch ts)
    {
        ts.Margin = new Thickness(0);
        return new Border
        {
            Child = ts,
            Background = MainWindow.BR_GLASSY,
            BorderBrush = MainWindow.BR_ACCENT_DIM,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
    }
}
} // namespace Soundeck.Views.Dialogs














