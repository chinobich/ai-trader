using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FolderJump
{
    internal enum OpenMode { Open, Reveal, NewWindow }

    /// <summary>配色 (ダークテーマ + シアン→バイオレットのアクセント)。</summary>
    internal static class Palette
    {
        public static readonly Brush CardBg = Solid("#F2111318");
        public static readonly Brush FooterBg = Solid("#0AFFFFFF");
        public static readonly Brush Text1 = Solid("#F1F3F8");
        public static readonly Brush Text2 = Solid("#B4BCCB");
        public static readonly Brush Text3 = Solid("#7C8597");
        public static readonly Brush Text4 = Solid("#4E5566");
        public static readonly Brush Accent = Solid("#38BDF8");
        public static readonly Brush Accent2 = Solid("#A78BFA");
        public static readonly Brush Match = Solid("#67E8F9");
        public static readonly Brush Green = Solid("#34D399");
        public static readonly Brush Hover = Solid("#0FFFFFFF");
        public static readonly Brush Divider = Solid("#1AFFFFFF");
        public static readonly Brush ChipBg = Solid("#1F38BDF8");
        public static readonly Brush ChipBorder = Solid("#5538BDF8");
        public static readonly Brush KeyBorder = Solid("#33FFFFFF");
        public static readonly Brush Selection = Solid("#5538BDF8");
        public static readonly Brush CardBorder = Gradient("#7038BDF8", "#70A78BFA", 0);
        public static readonly Brush Selected = Gradient("#3038BDF8", "#18A78BFA", 0);
        public static readonly Brush AccentVertical = Gradient("#38BDF8", "#A78BFA", 90);
        public static readonly Brush Line = Gradient("#5538BDF8", "#00A78BFA", 0);

        static Brush Solid(string hex)
        {
            var b = new SolidColorBrush(C(hex));
            b.Freeze();
            return b;
        }

        static Brush Gradient(string a, string b, double angle)
        {
            var g = new LinearGradientBrush(C(a), C(b), angle);
            g.Freeze();
            return g;
        }

        public static Color C(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
    }

    /// <summary>
    /// ホットキーで呼び出すランチャー本体。
    /// 入力 → (バックグラウンドで) 検索 → 結果表示、を 1 キー入力ごとに行う。
    /// </summary>
    internal sealed class LauncherWindow : Window
    {
        const double WindowWidth = 820;
        const string DefaultPlaceholder = "フォルダ名・ファイル名で検索   (スペースで絞り込み / パスの貼り付けも OK)";

        static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI, Yu Gothic UI, Meiryo UI");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        sealed class Row
        {
            public Border Root;
            public Border Bar;
            public Grid Content;
            public Image Icon;
            public TextBlock Glyph;
            public TextBlock Title;
            public TextBlock Sub;
            public TextBlock Badge;
            public TextBlock Pin;
            public TextBlock Header;
            public ResultItem Item;
            public int Index;
        }

        sealed class Win32Owner : System.Windows.Forms.IWin32Window
        {
            public Win32Owner(IntPtr h) { Handle = h; }
            public IntPtr Handle { get; private set; }
        }

        readonly App app;
        TextBox box;
        TextBlock placeholder;
        StackPanel scopeChips;
        TextBlock countText;
        StackPanel list;
        ScrollViewer scroller;
        StackPanel emptyPanel;
        TextBlock emptyTitle, emptyBody;
        TextBlock statusText, toastText;
        Ellipse statusDot;
        StackPanel statusPanel;
        Rectangle scanGlow;
        TranslateTransform glowShift, slide;
        bool glowOn;

        readonly List<Row> rows = new List<Row>();
        List<ResultItem> items = new List<ResultItem>();
        FileIndex renderedIndex = FileIndex.Empty;
        int selected = -1;

        int scope = -1;
        string scopePath;
        FileIndex scopeIndex;
        int pendingSelectNode = -1;
        OpenMode? pendingMode;

        readonly DispatcherTimer debounce;
        readonly DispatcherTimer toastTimer;
        CancellationTokenSource cts;
        int queryVersion;
        bool inFlight;
        bool allowClose;

        public IntPtr PreviousExplorer;
        public bool SuppressAutoHide;

        public LauncherWindow(App app)
        {
            this.app = app;
            Title = "FolderJump";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = WindowWidth;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = UiFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Content = BuildUi();

            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            debounce.Tick += delegate { RunQuery(); };
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            toastTimer.Tick += delegate
            {
                toastTimer.Stop();
                toastText.Visibility = Visibility.Collapsed;
                statusPanel.Visibility = Visibility.Visible;
            };

            PreviewKeyDown += OnPreviewKeyDown;
            Deactivated += delegate { if (!SuppressAutoHide) HideLauncher(); };
        }

        public IntPtr Handle { get { return new WindowInteropHelper(this).Handle; } }

        // ------------------------------------------------------------------
        // 画面の組み立て
        // ------------------------------------------------------------------

        UIElement BuildUi()
        {
            slide = new TranslateTransform();
            var card = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = Palette.CardBg,
                BorderBrush = Palette.CardBorder,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(20, 14, 20, 26),
                RenderTransform = slide,
                Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.55, Color = Colors.Black }
            };

            var dock = new DockPanel { LastChildFill = true };

            // --- 検索バー ---
            var header = new Grid { Margin = new Thickness(20, 14, 18, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchGlyph = new TextBlock
            {
                FontFamily = IconFont, Text = "", FontSize = 19, Foreground = Palette.Accent,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 14, 0)
            };
            Grid.SetColumn(searchGlyph, 0);
            header.Children.Add(searchGlyph);

            scopeChips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            Grid.SetColumn(scopeChips, 1);
            header.Children.Add(scopeChips);

            var boxHost = new Grid();
            placeholder = new TextBlock
            {
                Text = DefaultPlaceholder, FontSize = 15, Foreground = Palette.Text4, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            box = new TextBox
            {
                FontSize = 21, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Palette.Text1,
                CaretBrush = Palette.Accent, SelectionBrush = Palette.Selection, VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0), Margin = new Thickness(0), MinHeight = 34, AcceptsTab = false
            };
            TextOptions.SetTextFormattingMode(box, TextFormattingMode.Ideal);
            box.TextChanged += delegate
            {
                placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                debounce.Stop();
                debounce.Start();
            };
            boxHost.Children.Add(placeholder);
            boxHost.Children.Add(box);
            Grid.SetColumn(boxHost, 2);
            header.Children.Add(boxHost);

            countText = new TextBlock { FontSize = 12, Foreground = Palette.Text3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 2, 0, 0) };
            Grid.SetColumn(countText, 3);
            header.Children.Add(countText);

            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            // --- 区切り線 (スキャン中は光が流れる) ---
            var lineHost = new Grid { Height = 1.5, ClipToBounds = true, Margin = new Thickness(14, 0, 14, 0) };
            lineHost.Children.Add(new Rectangle { Fill = Palette.Line });
            glowShift = new TranslateTransform(-260, 0);
            var glowStops = new GradientStopCollection
            {
                new GradientStop(Palette.C("#0038BDF8"), 0),
                new GradientStop(Palette.C("#FF38BDF8"), 0.55),
                new GradientStop(Palette.C("#FFA78BFA"), 0.8),
                new GradientStop(Palette.C("#00A78BFA"), 1)
            };
            scanGlow = new Rectangle
            {
                Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Fill = new LinearGradientBrush(glowStops, 0),
                RenderTransform = glowShift, Visibility = Visibility.Collapsed
            };
            lineHost.Children.Add(scanGlow);
            DockPanel.SetDock(lineHost, Dock.Top);
            dock.Children.Add(lineHost);

            // --- フッター ---
            var footer = new Border
            {
                Background = Palette.FooterBg, CornerRadius = new CornerRadius(0, 0, 16, 16),
                BorderBrush = Palette.Divider, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 9, 16, 10)
            };
            var fgrid = new Grid();
            fgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            statusDot = new Ellipse { Width = 7, Height = 7, Fill = Palette.Text4, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            statusText = new TextBlock { FontSize = 11.5, Foreground = Palette.Text3, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            statusPanel.Children.Add(statusDot);
            statusPanel.Children.Add(statusText);
            toastText = new TextBlock { FontSize = 11.5, Foreground = Palette.Match, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis };
            fgrid.Children.Add(statusPanel);
            fgrid.Children.Add(toastText);
            var hints = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            AddHint(hints, "Enter", "開く");
            AddHint(hints, "Shift+Enter", "場所");
            AddHint(hints, "Tab", "中へ");
            AddHint(hints, "Ctrl+D", "ピン");
            AddHint(hints, "Ctrl+C", "コピー");
            AddHint(hints, ">", "コマンド");
            Grid.SetColumn(hints, 1);
            fgrid.Children.Add(hints);
            footer.Child = fgrid;
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);

            // --- 結果リスト ---
            var body = new Grid();
            list = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            scroller = new ScrollViewer
            {
                Content = list, MaxHeight = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false
            };
            body.Children.Add(scroller);

            emptyPanel = new StackPanel { Margin = new Thickness(28, 10, 28, 18), Visibility = Visibility.Collapsed };
            emptyTitle = new TextBlock { FontSize = 15, Foreground = Palette.Text1, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            emptyBody = new TextBlock { FontSize = 12.5, Foreground = Palette.Text3, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            emptyPanel.Children.Add(emptyTitle);
            emptyPanel.Children.Add(emptyBody);
            var bodyStack = new StackPanel();
            bodyStack.Children.Add(body);
            bodyStack.Children.Add(emptyPanel);
            dock.Children.Add(bodyStack);

            card.Child = dock;
            return card;
        }

        static void AddHint(Panel host, string key, string label)
        {
            var cap = new Border
            {
                CornerRadius = new CornerRadius(4), BorderBrush = Palette.KeyBorder, BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = key, FontSize = 10.5, Foreground = Palette.Text2 }
            };
            host.Children.Add(cap);
            host.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11, Foreground = Palette.Text3, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 12, 0)
            });
        }

        Row CreateRow()
        {
            var r = new Row();
            r.Root = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
            var layers = new Grid();
            r.Bar = new Border
            {
                Width = 3, CornerRadius = new CornerRadius(2), Background = Palette.AccentVertical,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 9), Visibility = Visibility.Hidden
            };
            layers.Children.Add(r.Bar);

            r.Content = new Grid { Margin = new Thickness(12, 7, 12, 7) };
            r.Content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            r.Content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.Content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            r.Icon = new Image { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(r.Icon, BitmapScalingMode.HighQuality);
            r.Glyph = new TextBlock { FontFamily = IconFont, FontSize = 17, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
            r.Content.Children.Add(r.Icon);
            r.Content.Children.Add(r.Glyph);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            r.Title = new TextBlock { FontSize = 14.5, Foreground = Palette.Text1, TextTrimming = TextTrimming.CharacterEllipsis };
            r.Sub = new TextBlock { FontSize = 11.5, Foreground = Palette.Text3, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            texts.Children.Add(r.Title);
            texts.Children.Add(r.Sub);
            Grid.SetColumn(texts, 1);
            r.Content.Children.Add(texts);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            r.Pin = new TextBlock { FontFamily = IconFont, FontSize = 12, Text = "", Foreground = Palette.Accent2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            r.Badge = new TextBlock { FontSize = 11.5, Foreground = Palette.Text3, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(r.Pin);
            right.Children.Add(r.Badge);
            Grid.SetColumn(right, 2);
            r.Content.Children.Add(right);
            layers.Children.Add(r.Content);

            r.Header = new TextBlock
            {
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Text3,
                Margin = new Thickness(12, 10, 0, 3), Visibility = Visibility.Collapsed
            };
            layers.Children.Add(r.Header);
            r.Root.Child = layers;

            r.Root.MouseEnter += delegate
            {
                if (r.Item != null && r.Item.Selectable && r.Index != selected) r.Root.Background = Palette.Hover;
            };
            r.Root.MouseLeave += delegate
            {
                if (r.Index != selected) r.Root.Background = Brushes.Transparent;
            };
            r.Root.MouseLeftButtonDown += (s, e) =>
            {
                if (r.Item == null || !r.Item.Selectable) return;
                Select(r.Index);
                if (e.ClickCount >= 2) Execute(OpenMode.Open);
                e.Handled = true;
            };
            return r;
        }

        void Bind(Row r, ResultItem item, int index)
        {
            r.Item = item;
            r.Index = index;
            r.Root.Visibility = Visibility.Visible;
            if (item.Kind == ItemKind.Header)
            {
                r.Header.Text = item.Title;
                r.Header.Visibility = Visibility.Visible;
                r.Content.Visibility = Visibility.Collapsed;
                r.Bar.Visibility = Visibility.Hidden;
                r.Root.Background = Brushes.Transparent;
                r.Root.ToolTip = null;
                return;
            }
            r.Header.Visibility = Visibility.Collapsed;
            r.Content.Visibility = Visibility.Visible;

            ImageSource img = null;
            if (item.Kind == ItemKind.Entry && item.Glyph == null) img = ShellIcons.Get(item.Title ?? item.Path ?? "", item.IsDir);
            if (img != null)
            {
                r.Icon.Source = img;
                r.Icon.Visibility = Visibility.Visible;
                r.Glyph.Visibility = Visibility.Collapsed;
            }
            else
            {
                r.Icon.Visibility = Visibility.Collapsed;
                r.Glyph.Visibility = Visibility.Visible;
                r.Glyph.Text = item.Glyph ?? (item.IsDir ? "" : "");
            }

            SetTitle(r.Title, item);
            r.Sub.Text = item.Subtitle ?? "";
            r.Sub.Visibility = string.IsNullOrEmpty(item.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            r.Badge.Text = item.Badge ?? "";
            r.Pin.Visibility = item.Pinned ? Visibility.Visible : Visibility.Collapsed;
            r.Root.ToolTip = item.Path;
            ApplySelection(r, index == selected);
        }

        static void SetTitle(TextBlock tb, ResultItem item)
        {
            tb.Inlines.Clear();
            string t = item.Title ?? "";
            int[] h = item.Highlights;
            if (h == null || h.Length < 2)
            {
                tb.Inlines.Add(new Run(t));
                return;
            }
            int pos = 0;
            for (int i = 0; i + 1 < h.Length; i += 2)
            {
                int s = h[i], len = h[i + 1];
                if (s < pos || len <= 0 || s + len > t.Length) continue;
                if (s > pos) tb.Inlines.Add(new Run(t.Substring(pos, s - pos)));
                tb.Inlines.Add(new Run(t.Substring(s, len)) { Foreground = Palette.Match, FontWeight = FontWeights.SemiBold });
                pos = s + len;
            }
            if (pos < t.Length) tb.Inlines.Add(new Run(t.Substring(pos)));
        }

        static void ApplySelection(Row r, bool on)
        {
            r.Root.Background = on ? Palette.Selected : Brushes.Transparent;
            r.Bar.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }

        // ------------------------------------------------------------------
        // 表示 / 非表示
        // ------------------------------------------------------------------

        public void ShowLauncher(IntPtr previousExplorer)
        {
            PreviousExplorer = previousExplorer;
            if (scope >= 0) ClearScope();
            if (!IsVisible)
            {
                PlaceOnCursorMonitor();
                Opacity = 0;
                Show();
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                fade.Completed += delegate
                {
                    Opacity = 1;
                    BeginAnimation(OpacityProperty, null);
                };
                BeginAnimation(OpacityProperty, fade);
                slide.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            Activate();
            Native.SetForegroundWindow(Handle);
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
            RunQuery();
        }

        public void HideLauncher()
        {
            if (!IsVisible) return;
            pendingMode = null;
            if (cts != null) cts.Cancel();
            Hide();
        }

        public void Exit()
        {
            allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!allowClose)
            {
                e.Cancel = true; // Alt+F4 では終了せず隠すだけ
                HideLauncher();
            }
            base.OnClosing(e);
        }

        /// <summary>マウスカーソルがあるモニターの上寄り中央に出す (モニターごとの DPI に対応)。</summary>
        void PlaceOnCursorMonitor()
        {
            IntPtr hwnd = Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                Native.POINT pt;
                Native.GetCursorPos(out pt);
                IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
                var mi = new Native.MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
                if (!Native.GetMonitorInfo(mon, ref mi)) return;
                double scale = 1;
                try
                {
                    uint dx, dy;
                    if (Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) scale = dx / 96.0;
                }
                catch (Exception) { }
                Native.RECT wa = mi.rcWork;
                int waW = wa.Right - wa.Left, waH = wa.Bottom - wa.Top;
                int wPx = (int)Math.Round(Width * scale);
                int x = wa.Left + (waW - wPx) / 2;
                int y = wa.Top + (int)(waH * 0.13);
                const uint flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
                Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, flags);
                Native.RECT r;
                if (Native.GetWindowRect(hwnd, out r))
                {
                    int actual = r.Right - r.Left;
                    if (actual > 0 && Math.Abs(actual - wPx) > 2)
                        Native.SetWindowPos(hwnd, IntPtr.Zero, wa.Left + (waW - actual) / 2, y, 0, 0, flags);
                }
            }
            catch (Exception ex)
            {
                Log.Error("place window", ex);
            }
        }

        // ------------------------------------------------------------------
        // キー操作
        // ------------------------------------------------------------------

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Escape:
                    HideLauncher();
                    e.Handled = true;
                    break;
                case Key.Down:
                    Move(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    Move(-1);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    Move(8);
                    e.Handled = true;
                    break;
                case Key.PageUp:
                    Move(-8);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Execute(shift ? OpenMode.Reveal : ctrl ? OpenMode.NewWindow : OpenMode.Open);
                    e.Handled = true;
                    break;
                case Key.Tab:
                    if (shift) ScopeUp(); else ScopeInto();
                    e.Handled = true;
                    break;
                case Key.Back:
                    if (box.Text.Length == 0 && scope >= 0) { ScopeUp(); e.Handled = true; }
                    break;
                case Key.Right:
                    if (box.Text.Length == 0) { ScopeInto(); e.Handled = true; }
                    break;
                case Key.Left:
                    if (box.Text.Length == 0 && scope >= 0) { ScopeUp(); e.Handled = true; }
                    break;
                case Key.D:
                    if (ctrl) { TogglePin(); e.Handled = true; }
                    break;
                case Key.C:
                    if (ctrl && (shift || box.SelectionLength == 0)) { CopyPath(); e.Handled = true; }
                    break;
            }
        }

        ResultItem Current
        {
            get { return selected >= 0 && selected < items.Count ? items[selected] : null; }
        }

        int FirstSelectable()
        {
            for (int i = 0; i < items.Count; i++) if (items[i].Selectable) return i;
            return -1;
        }

        void Select(int i)
        {
            if (i >= items.Count || (i >= 0 && !items[i].Selectable)) return;
            int old = selected;
            selected = i;
            if (old >= 0 && old < rows.Count && old != i) ApplySelection(rows[old], false);
            if (i >= 0)
            {
                ApplySelection(rows[i], true);
                rows[i].Root.BringIntoView();
            }
        }

        void Move(int delta)
        {
            if (items.Count == 0) return;
            int step = delta > 0 ? 1 : -1;
            int remaining = Math.Abs(delta);
            int cur = selected, good = selected;
            while (remaining > 0)
            {
                cur += step;
                if (cur < 0 || cur >= items.Count) break;
                if (items[cur].Selectable) { good = cur; remaining--; }
            }
            if (good >= 0 && good != selected) Select(good);
            if (good == FirstSelectable()) scroller.ScrollToTop();
        }

        void Execute(OpenMode mode)
        {
            // 入力直後で検索がまだ終わっていなければ、結果が出てから実行する
            if (debounce.IsEnabled || inFlight)
            {
                pendingMode = mode;
                if (debounce.IsEnabled) RunQuery();
                return;
            }
            ResultItem item = Current;
            if (item == null) return;
            if (item.Kind == ItemKind.Command)
            {
                if (!item.KeepOpen) HideLauncher();
                try { item.Command(); }
                catch (Exception ex)
                {
                    Log.Error("command", ex);
                    Toast("失敗しました: " + ex.Message);
                }
                if (item.KeepOpen && IsVisible) RunQuery();
                return;
            }
            app.OpenItem(item, mode, PreviousExplorer);
        }

        // ------------------------------------------------------------------
        // フォルダの中へ / 上へ (キーボードだけで階層をたどる)
        // ------------------------------------------------------------------

        void ScopeInto()
        {
            ResultItem item = Current;
            if (item == null || item.Kind != ItemKind.Entry) return;
            FileIndex idx = renderedIndex;
            if (item.Node >= 0 && item.Node < idx.Count)
            {
                int node = idx.IsDir(item.Node) ? item.Node : idx.Parent[item.Node];
                if (node >= 0) { SetScope(node, idx, -1); return; }
            }
            string path = item.IsDir ? item.Path : PathUtil.Parent(item.Path);
            if (path != null) ResolveAndScope(path);
        }

        async void ResolveAndScope(string path)
        {
            FileIndex idx = app.Index;
            int node;
            try { node = await Task.Run(() => app.Engine.NodeOf(idx, path, CancellationToken.None)); }
            catch (Exception ex) { Log.Error("resolve", ex); node = -1; }
            if (node >= 0 && idx.IsDir(node)) SetScope(node, idx, -1);
            else Toast("このフォルダはまだインデックスにありません (スキャン完了後に使えます)");
        }

        void SetScope(int node, FileIndex idx, int selectAfter)
        {
            scope = node;
            scopeIndex = idx;
            scopePath = idx.FullPath(node);
            pendingSelectNode = selectAfter;
            UpdateScopeChip();
            box.Text = "";
            RunQuery();
        }

        void ScopeUp()
        {
            if (scope < 0 || scopeIndex == null) return;
            int from = scope;
            int p = scopeIndex.Parent[scope];
            if (p >= 0) SetScope(p, scopeIndex, from);
            else
            {
                ClearScope();
                box.Text = "";
                RunQuery();
            }
        }

        void ClearScope()
        {
            scope = -1;
            scopeIndex = null;
            scopePath = null;
            UpdateScopeChip();
        }

        void UpdateScopeChip()
        {
            scopeChips.Children.Clear();
            if (scope < 0)
            {
                scopeChips.Visibility = Visibility.Collapsed;
                placeholder.Text = DefaultPlaceholder;
                return;
            }
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { FontFamily = IconFont, Text = "", FontSize = 12, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = PathUtil.Pretty(scopePath, 34), FontSize = 13, Foreground = Palette.Text1, VerticalAlignment = VerticalAlignment.Center });
            scopeChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = Palette.ChipBg, BorderBrush = Palette.ChipBorder, BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 3, 10, 4), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = scopePath, Child = sp
            });
            scopeChips.Visibility = Visibility.Visible;
            placeholder.Text = "このフォルダの中を検索   (Backspace で上の階層へ)";
        }

        // ------------------------------------------------------------------
        // ピン / コピー / トースト
        // ------------------------------------------------------------------

        void TogglePin()
        {
            ResultItem item = Current;
            if (item == null || item.Kind != ItemKind.Entry || item.Path == null) return;
            bool pinned = app.Usage.TogglePin(item.Path, item.IsDir);
            item.Pinned = pinned;
            if (selected >= 0 && selected < rows.Count) rows[selected].Pin.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            Toast(pinned ? "ピン留めしました (何も入力していない時に一番上に出ます)" : "ピン留めを外しました");
        }

        void CopyPath()
        {
            ResultItem item = Current;
            if (item == null || item.Path == null) return;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetText(item.Path);
                    Toast("パスをコピーしました  " + item.Path);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(40); // 他のアプリがクリップボードを開いている
                }
            }
            Toast("クリップボードが使用中でコピーできませんでした");
        }

        public void Toast(string message)
        {
            toastText.Text = message;
            toastText.Visibility = Visibility.Visible;
            statusPanel.Visibility = Visibility.Hidden;
            toastTimer.Stop();
            toastTimer.Start();
        }

        public void SetStatus(string text, bool scanning, bool ready)
        {
            statusText.Text = text;
            statusDot.Fill = scanning ? Palette.Accent : ready ? Palette.Green : Palette.Text4;
            if (scanning == glowOn) return;
            glowOn = scanning;
            if (scanning)
            {
                scanGlow.Visibility = Visibility.Visible;
                glowShift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-260, WindowWidth, TimeSpan.FromMilliseconds(1700)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                glowShift.BeginAnimation(TranslateTransform.XProperty, null);
                scanGlow.Visibility = Visibility.Collapsed;
            }
        }

        public void PickFolder(Action<string> onPicked)
        {
            SuppressAutoHide = true;
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "検索対象にするフォルダを選んでください (ネットワーク → サーバー → 共有フォルダ)";
                    dlg.ShowNewFolderButton = false;
                    if (dlg.ShowDialog(new Win32Owner(Handle)) == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                        onPicked(dlg.SelectedPath);
                }
            }
            finally
            {
                SuppressAutoHide = false;
                if (IsVisible)
                {
                    Activate();
                    box.Focus();
                }
            }
        }

        /// <summary>インデックスが更新されたとき、操作中でなければ表示を更新する。</summary>
        public void RefreshIfIdle()
        {
            if (!IsVisible || scope >= 0) return;
            if (selected > FirstSelectable()) return; // 選択を動かしている最中は邪魔しない
            RunQuery();
        }

        // ------------------------------------------------------------------
        // 検索の実行と描画
        // ------------------------------------------------------------------

        public async void RunQuery()
        {
            debounce.Stop();
            string text = box.Text;
            int sc = scope;
            FileIndex idx = sc >= 0 && scopeIndex != null ? scopeIndex : app.Index;
            int ver = ++queryVersion;
            if (cts != null) cts.Cancel();
            var source = new CancellationTokenSource();
            cts = source;
            CancellationToken token = source.Token;
            inFlight = true;
            List<ResultItem> res;
            try
            {
                res = await Task.Run(() => app.Engine.Compute(text, sc, idx, token), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("query", ex);
                res = new List<ResultItem>();
            }
            if (ver != queryVersion) return;
            inFlight = false;
            renderedIndex = idx;
            Render(res, text);
        }

        void Render(List<ResultItem> res, string text)
        {
            items = res;
            while (rows.Count < items.Count)
            {
                Row r = CreateRow();
                rows.Add(r);
                list.Children.Add(r.Root);
            }
            selected = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (i < items.Count) Bind(rows[i], items[i], i);
                else
                {
                    rows[i].Root.Visibility = Visibility.Collapsed;
                    rows[i].Item = null;
                }
            }
            int target = FirstSelectable();
            if (pendingSelectNode >= 0)
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Node == pendingSelectNode && items[i].Selectable) { target = i; break; }
                pendingSelectNode = -1;
            }
            scroller.ScrollToTop();
            if (target >= 0) Select(target);

            int n = 0;
            foreach (ResultItem it in items) if (it.Kind == ItemKind.Entry) n++;
            countText.Text = n == 0 ? "" : n >= QueryEngine.MaxResults ? n + "+ 件" : n + " 件";
            UpdateEmpty(text);

            if (pendingMode.HasValue)
            {
                OpenMode m = pendingMode.Value;
                pendingMode = null;
                Execute(m);
            }
        }

        void UpdateEmpty(string text)
        {
            string title = null, body = null;
            bool typed = text.Trim().Length > 0;
            if (app.CurrentSettings.Roots.Count == 0 && !typed)
            {
                title = "まずは検索対象のフォルダを登録しましょう";
                body = "① エクスプローラーでファイルサーバーの共有フォルダを開き、アドレスバーのパスをコピー (例: \\\\fileserver\\share)\n"
                     + "② ここに貼り付けて「このフォルダを検索対象に追加」を選んで Enter\n"
                     + "   または上の「フォルダを選んで検索対象に追加…」で選ぶこともできます。";
            }
            else if (items.Count == 0)
            {
                if (scope >= 0 && !typed)
                {
                    title = "このフォルダは空です";
                    body = "Backspace で上の階層に戻れます。";
                }
                else if (app.IsScanning && app.Index.Count < 50)
                {
                    title = "スキャン中です…";
                    body = "見つかったものから順に検索できるようになります。初回は数分かかることがあります。";
                }
                else if (typed)
                {
                    title = "見つかりませんでした";
                    body = "言葉の一部だけ (例: 「見積 2024」) や、親フォルダの名前を足してみてください。\n"
                         + "「-旧」のように - を付けるとその言葉を含むものを除外できます。";
                }
            }
            if (title == null)
            {
                emptyPanel.Visibility = Visibility.Collapsed;
                return;
            }
            emptyTitle.Text = title;
            emptyBody.Text = body ?? "";
            emptyPanel.Visibility = Visibility.Visible;
        }
    }
}
