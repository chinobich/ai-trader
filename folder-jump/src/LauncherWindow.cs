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

    /// <summary>
    /// ホットキーで呼び出すランチャー本体。
    /// 入力 → (バックグラウンドで) 検索 → 結果表示、を 1 キー入力ごとに行う。
    /// </summary>
    internal sealed class LauncherWindow : Window
    {
        const string DefaultPlaceholder = "フォルダ名・ファイル名で検索   (スペースで絞り込み / ? で書き方の一覧)";
        const string DialogPlaceholder = "移動先のフォルダを検索   (Enter でこの画面を移動 / Ctrl+Enter はエクスプローラー)";

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
        Theme T;
        TextBox box;
        TextBlock placeholder;
        StackPanel scopeChips;
        Border dialogBanner;
        TextBlock dialogText;
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
        string lastStatus = "";
        bool lastScanning, lastReady;

        readonly List<Row> rows = new List<Row>();
        List<ResultItem> items = new List<ResultItem>();
        FileIndex renderedIndex = FileIndex.Empty;
        int selected = -1;

        int scope = -1;
        string scopePath;
        FileIndex scopeIndex;
        int pendingSelectNode = -1;
        int pendingSelectIndex = -1;
        OpenMode? pendingMode;

        readonly DispatcherTimer debounce;
        readonly DispatcherTimer toastTimer;
        CancellationTokenSource cts;
        int queryVersion;
        bool inFlight;
        bool allowClose;

        public IntPtr PreviousExplorer;
        public IntPtr PreviousDialog;
        public bool SuppressAutoHide;

        public LauncherWindow(App app, Theme theme)
        {
            this.app = app;
            T = theme;
            Title = "FolderJump";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = UiFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Width = (T.CardWidth + 40) * T.Zoom;
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

        /// <summary>見た目の設定が変わったら画面を作り直す (入力中の文字や階層はそのまま)。</summary>
        public void ApplyTheme(Theme theme)
        {
            if (theme.Signature == T.Signature) return;
            string text = box.Text;
            T = theme;
            rows.Clear();
            items = new List<ResultItem>();
            selected = -1;
            glowOn = false;
            Width = (T.CardWidth + 40) * T.Zoom;
            Content = BuildUi();
            box.Text = text;
            box.CaretIndex = text.Length;
            UpdateScopeChip();
            UpdateDialogBanner();
            SetStatus(lastStatus, lastScanning, lastReady);
            if (IsVisible)
            {
                box.Focus();
                Keyboard.Focus(box);
                // 幅や大きさが変わったら中央に置き直す
                Dispatcher.BeginInvoke(new Action(PlaceOnCursorMonitor), DispatcherPriority.Loaded);
            }
            RunQuery();
        }

        // ------------------------------------------------------------------
        // 画面の組み立て
        // ------------------------------------------------------------------

        UIElement BuildUi()
        {
            slide = new TranslateTransform();
            var card = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = T.CardBg,
                BorderBrush = T.CardBorder,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(20, 14, 20, 26),
                RenderTransform = slide,
                Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = T.ShadowOpacity, Color = Colors.Black }
            };

            var dock = new DockPanel { LastChildFill = true };

            // --- 保存ダイアログから呼ばれたときの案内 ---
            dialogText = new TextBlock { FontSize = 11.5, Foreground = T.Accent, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var dialogRow = new StackPanel { Orientation = Orientation.Horizontal };
            dialogRow.Children.Add(new TextBlock { FontFamily = IconFont, Text = "", FontSize = 12, Foreground = T.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
            dialogRow.Children.Add(dialogText);
            dialogBanner = new Border { Margin = new Thickness(21, 11, 18, 0), Child = dialogRow, Visibility = Visibility.Collapsed };
            DockPanel.SetDock(dialogBanner, Dock.Top);
            dock.Children.Add(dialogBanner);

            // --- 検索バー ---
            var header = new Grid { Margin = new Thickness(20, 14, 18, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var searchGlyph = new TextBlock
            {
                FontFamily = IconFont, Text = "", FontSize = 19, Foreground = T.Accent,
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
                Text = DefaultPlaceholder, FontSize = 15, Foreground = T.Text4, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            };
            box = new TextBox
            {
                FontSize = 21, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = T.Text1,
                CaretBrush = T.Accent, SelectionBrush = T.Selection, VerticalContentAlignment = VerticalAlignment.Center,
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

            countText = new TextBlock { FontSize = 12, Foreground = T.Text3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 2, 0, 0) };
            Grid.SetColumn(countText, 3);
            header.Children.Add(countText);

            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            // --- 区切り線 (スキャン中は光が流れる) ---
            var lineHost = new Grid { Height = 1.5, ClipToBounds = true, Margin = new Thickness(14, 0, 14, 0) };
            lineHost.Children.Add(new Rectangle { Fill = T.Line });
            glowShift = new TranslateTransform(-260, 0);
            Color a = T.AccentColor, b = T.Accent2Color;
            var glowStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(0, a.R, a.G, a.B), 0),
                new GradientStop(a, 0.55),
                new GradientStop(b, 0.8),
                new GradientStop(Color.FromArgb(0, b.R, b.G, b.B), 1)
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
                Background = T.FooterBg, CornerRadius = new CornerRadius(0, 0, 16, 16),
                BorderBrush = T.Divider, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 9, 16, 10)
            };
            var fgrid = new Grid();
            fgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            statusDot = new Ellipse { Width = 7, Height = 7, Fill = T.Text4, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            statusText = new TextBlock { FontSize = 11.5, Foreground = T.Text3, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            statusPanel.Children.Add(statusDot);
            statusPanel.Children.Add(statusText);
            toastText = new TextBlock { FontSize = 11.5, Foreground = T.Match, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis };
            fgrid.Children.Add(statusPanel);
            fgrid.Children.Add(toastText);
            var hints = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            AddHint(hints, "Enter", "開く");
            AddHint(hints, "Shift+Enter", "場所");
            AddHint(hints, "Tab", "中へ");
            AddHint(hints, "Ctrl+D", "ピン");
            AddHint(hints, "Ctrl+C", "コピー");
            AddHint(hints, "?", "書き方");
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
                Content = list, MaxHeight = T.Compact ? 520 : 470, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false
            };
            body.Children.Add(scroller);

            emptyPanel = new StackPanel { Margin = new Thickness(28, 10, 28, 18), Visibility = Visibility.Collapsed };
            emptyTitle = new TextBlock { FontSize = 15, Foreground = T.Text1, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            emptyBody = new TextBlock { FontSize = 12.5, Foreground = T.Text3, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            emptyPanel.Children.Add(emptyTitle);
            emptyPanel.Children.Add(emptyBody);
            var bodyStack = new StackPanel();
            bodyStack.Children.Add(body);
            bodyStack.Children.Add(emptyPanel);
            dock.Children.Add(bodyStack);

            card.Child = dock;
            var root = new Grid { LayoutTransform = new ScaleTransform(T.Zoom, T.Zoom) };
            root.Children.Add(card);
            return root;
        }

        void AddHint(Panel host, string key, string label)
        {
            var cap = new Border
            {
                CornerRadius = new CornerRadius(4), BorderBrush = T.KeyBorder, BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 0, 5, 1), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = key, FontSize = 10.5, Foreground = T.Text2 }
            };
            host.Children.Add(cap);
            host.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11, Foreground = T.Text3, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 11, 0)
            });
        }

        Row CreateRow()
        {
            var r = new Row();
            r.Root = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
            var layers = new Grid();
            r.Bar = new Border
            {
                Width = 3, CornerRadius = new CornerRadius(2), Background = T.AccentVertical,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = T.Compact ? new Thickness(0, 6, 0, 6) : new Thickness(0, 9, 0, 9),
                Visibility = Visibility.Hidden
            };
            layers.Children.Add(r.Bar);

            r.Content = new Grid { Margin = T.Compact ? new Thickness(12, 4, 12, 4) : new Thickness(12, 7, 12, 7) };
            r.Content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(T.Compact ? 30 : 36) });
            r.Content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.Content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            double iconSize = T.Compact ? 18 : 22;
            r.Icon = new Image { Width = iconSize, Height = iconSize, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(r.Icon, BitmapScalingMode.HighQuality);
            r.Glyph = new TextBlock { FontFamily = IconFont, FontSize = T.Compact ? 15 : 17, Foreground = T.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
            r.Content.Children.Add(r.Icon);
            r.Content.Children.Add(r.Glyph);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            r.Title = new TextBlock { FontSize = T.Compact ? 14 : 14.5, Foreground = T.Text1, TextTrimming = TextTrimming.CharacterEllipsis };
            r.Sub = new TextBlock { FontSize = 11.5, Foreground = T.Text3, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            texts.Children.Add(r.Title);
            texts.Children.Add(r.Sub);
            Grid.SetColumn(texts, 1);
            r.Content.Children.Add(texts);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            r.Pin = new TextBlock { FontFamily = IconFont, FontSize = 12, Text = "", Foreground = T.Accent2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            r.Badge = new TextBlock { FontSize = 11.5, Foreground = T.Text3, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(r.Pin);
            right.Children.Add(r.Badge);
            Grid.SetColumn(right, 2);
            r.Content.Children.Add(right);
            layers.Children.Add(r.Content);

            r.Header = new TextBlock
            {
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = T.Text3,
                Margin = new Thickness(12, 10, 0, 3), Visibility = Visibility.Collapsed
            };
            layers.Children.Add(r.Header);
            r.Root.Child = layers;

            r.Root.MouseEnter += delegate
            {
                if (r.Item != null && r.Item.Selectable && r.Index != selected) r.Root.Background = T.Hover;
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
            // コンパクト表示ではパスを出さない (コマンドの説明は出す)
            bool showSub = !string.IsNullOrEmpty(item.Subtitle) && (!T.Compact || item.Kind == ItemKind.Command);
            r.Sub.Text = item.Subtitle ?? "";
            r.Sub.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;
            r.Badge.Text = item.Badge ?? "";
            r.Pin.Visibility = item.Pinned ? Visibility.Visible : Visibility.Collapsed;
            r.Root.ToolTip = item.Path;
            ApplySelection(r, index == selected);
        }

        void SetTitle(TextBlock tb, ResultItem item)
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
                tb.Inlines.Add(new Run(t.Substring(s, len)) { Foreground = T.Match, FontWeight = FontWeights.SemiBold });
                pos = s + len;
            }
            if (pos < t.Length) tb.Inlines.Add(new Run(t.Substring(pos)));
        }

        void ApplySelection(Row r, bool on)
        {
            r.Root.Background = on ? T.Selected : Brushes.Transparent;
            r.Bar.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }

        // ------------------------------------------------------------------
        // 表示 / 非表示
        // ------------------------------------------------------------------

        public void ShowLauncher(IntPtr previousExplorer, IntPtr previousDialog)
        {
            PreviousExplorer = previousExplorer;
            PreviousDialog = previousDialog;
            UpdateDialogBanner();
            if (scope >= 0) ClearScope();
            if (!IsVisible)
            {
                PlaceOnCursorMonitor();
                if (T.Animations)
                {
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
                else
                {
                    Opacity = 1;
                    Show();
                }
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

        void UpdateDialogBanner()
        {
            if (PreviousDialog != IntPtr.Zero)
            {
                dialogText.Text = "「" + FileDialogBridge.TitleOf(PreviousDialog) + "」の移動先を選んでいます。Enter でこの画面がフォルダへ移動します";
                dialogBanner.Visibility = Visibility.Visible;
                if (scope < 0) placeholder.Text = DialogPlaceholder;
            }
            else
            {
                dialogBanner.Visibility = Visibility.Collapsed;
                if (scope < 0) placeholder.Text = DefaultPlaceholder;
            }
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
                int keep = selected;
                if (!item.KeepOpen) HideLauncher();
                try { item.Command(); }
                catch (Exception ex)
                {
                    Log.Error("command", ex);
                    Toast("失敗しました: " + ex.Message);
                }
                if (item.KeepOpen && IsVisible)
                {
                    pendingSelectIndex = keep; // 「大きくする」を続けて押せるように同じ行を選んだままにする
                    RunQuery();
                }
                return;
            }
            app.OpenItem(item, mode, PreviousExplorer, PreviousDialog);
        }

        /// <summary>入力欄の文字を置き換えて検索し直す (「?」の例を選んだとき等)。</summary>
        public void SetQuery(string text)
        {
            box.Text = text;
            box.CaretIndex = text.Length;
            box.Focus();
            pendingSelectIndex = -1;
            RunQuery();
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
                placeholder.Text = PreviousDialog != IntPtr.Zero ? DialogPlaceholder : DefaultPlaceholder;
                return;
            }
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { FontFamily = IconFont, Text = "", FontSize = 12, Foreground = T.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = PathUtil.Pretty(scopePath, 34), FontSize = 13, Foreground = T.Text1, VerticalAlignment = VerticalAlignment.Center });
            scopeChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = T.ChipBg, BorderBrush = T.ChipBorder, BorderThickness = new Thickness(1),
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
            lastStatus = text;
            lastScanning = scanning;
            lastReady = ready;
            statusText.Text = text;
            statusDot.Fill = scanning ? T.Accent : ready ? T.Green : T.Text4;
            if (scanning == glowOn) return;
            glowOn = scanning;
            if (scanning)
            {
                scanGlow.Visibility = Visibility.Visible;
                if (T.Animations)
                    glowShift.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(-260, T.CardWidth, TimeSpan.FromMilliseconds(1700)) { RepeatBehavior = RepeatBehavior.Forever });
                else
                    glowShift.X = T.CardWidth * 0.35;
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
            bool dialogMode = PreviousDialog != IntPtr.Zero;
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
                res = await Task.Run(() => app.Engine.Compute(text, sc, idx, token, dialogMode), token);
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
            if (pendingSelectIndex >= 0)
            {
                if (pendingSelectIndex < items.Count && items[pendingSelectIndex].Selectable) target = pendingSelectIndex;
                pendingSelectIndex = -1;
            }
            scroller.ScrollToTop();
            if (target >= 0) Select(target);

            int n = 0;
            foreach (ResultItem it in items) if (it.Kind == ItemKind.Entry) n++;
            string count = n == 0 ? "" : n >= QueryEngine.MaxResults ? n + "+ 件" : n + " 件";
            string filter = DescribeFilters(text);
            countText.Text = filter.Length == 0 ? count : (count.Length == 0 ? filter : count + "  ·  " + filter);
            UpdateEmpty(text);

            if (pendingMode.HasValue)
            {
                OpenMode m = pendingMode.Value;
                pendingMode = null;
                Execute(m);
            }
        }

        static string DescribeFilters(string text)
        {
            string t = text.Trim();
            if (t.Length == 0 || t[0] == '>' || t[0] == '＞' || t[0] == '?' || t[0] == '？' || PathInput.TryParse(t) != null) return "";
            return Query.Parse(t).Describe();
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
                         + "「-旧」で除外、「.xlsx」で拡張子、「@今週」で更新日の絞り込みができます。? で書き方の一覧を表示します。";
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
