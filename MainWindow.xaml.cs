using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Shush
{
    public partial class MainWindow : Window
    {
        Settings S;
        Engine Eng;
        string profile;
        string page = "cards";
        Popup popup;
        int toastId, wizIdx;
        bool gamesOpen = true;
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        readonly List<Action> rowRefresh = new List<Action>();
        readonly Dictionary<string, bool> vis = new Dictionary<string, bool> { { "Editing", true }, { "Building", true }, { "Utility", true }, { "Framebreak", true } };
        readonly Dictionary<string, bool> collapsed = new Dictionary<string, bool> { { "Editing", false }, { "Building", false }, { "Utility", false }, { "Framebreak", false } };

        const double RowW = 372;
        const double PopRow = RowW - 16;
        const double CardW = 320;
        static readonly string[] Cats = { "Editing", "Building", "Utility", "Framebreak" };
        static readonly string[] GameCats = { "Framebreak" };
        static readonly Dictionary<string, string> CatIcon = new Dictionary<string, string>
        { { "Editing", "\u270E" }, { "Building", "\u25A3" }, { "Utility", "\u2699" }, { "Games", "\u265F" }, { "Framebreak", "\u25A6" } };

        static readonly string[] ThemeKeys =
        { "BgBrush", "SideBrush", "CardBrush", "RowBrush", "FieldBrush", "LineBrush", "TextBrush", "MutedBrush", "AccentBrush", "TrackBrush", "HoverBrush" };
        static readonly Dictionary<string, string[]> Themes = new Dictionary<string, string[]>
        {
            { "Light",    new[] { "#F3F5F9", "#FFFFFF", "#FFFFFF", "#EEF1F6", "#F6F7FA", "#D9DEE8", "#151922", "#667085", "#2563EB", "#D3D9E4", "#E2E7F0" } },
            { "Midnight", new[] { "#0B0D14", "#10131C", "#141824", "#1B2030", "#0E1119", "#242A3A", "#E8ECF5", "#7B859A", "#3B82F6", "#2A3145", "#222839" } },
            { "Crimson",  new[] { "#0E0A0B", "#140D0F", "#181012", "#211619", "#0C0809", "#331F23", "#F3E9EA", "#9A7F83", "#E11D48", "#3A262A", "#2B1A1E" } },
            { "Slate",    new[] { "#12161C", "#171C24", "#1B212B", "#232A36", "#0F1319", "#2F3846", "#E6EAF0", "#8591A3", "#6D8BB5", "#323B49", "#2A3340" } },
        };

        static readonly string[][] KeyList =
        {
            new[] { "edit", "Edit" }, new[] { "pickup", "Pick Up" },
            new[] { "slot1", "Slot 1" }, new[] { "slot2", "Slot 2" }, new[] { "slot3", "Slot 3" }, new[] { "slot4", "Slot 4" },
            new[] { "slot5", "Slot 5" }, new[] { "slot6", "Slot 6" }, new[] { "slot7", "Slot 7" },
            new[] { "wall", "Wall" }, new[] { "floor", "Floor" }, new[] { "ramp", "Ramp" }, new[] { "pyramid", "Pyramid" },
        };

        public MainWindow()
        {
            InitializeComponent();
            MinBtn.Content = "\uE921";     // set here instead of XAML so the XAML has no encoded characters
            CloseBtn.Content = "\uE8BB";
            Native.timeBeginPeriod(1);
            profile = Store.Active();
            S = Store.Load(profile);
            Input.Debug = S.Debug;
            Eng = new Engine(S);
            Eng.ToggleUi += () => Dispatcher.BeginInvoke(new Action(ToggleVisible));
            Eng.PanicEvt += () => Dispatcher.BeginInvoke(new Action(Quit));
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); Store.Save(profile, S); };
            ApplyUi();
            BuildAll();
            Eng.Start();
            Loaded += (s, e) => FadeIn(Root, 12, 320, 0);
            Toast("Macro system ready");
        }

        // ------------------------------------------------------------ small helpers
        TextBlock Txt(string text, string fg = "TextBrush", double size = 15, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = size,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, fg);
            return tb;
        }

        Border Box(string bg, string border, double radius)
        {
            var b = new Border { CornerRadius = new CornerRadius(radius) };
            b.SetResourceReference(Border.BackgroundProperty, bg);
            if (border != null)
            {
                b.BorderThickness = new Thickness(1);
                b.SetResourceReference(Border.BorderBrushProperty, border);
            }
            return b;
        }

        Style St(string key) { return (Style)FindResource(key); }

        static string OptHelp(string key)
        {
            string h;
            return Specs.OptHelp.TryGetValue(key, out h) ? h : null;
        }

        // round "?" badge: hover shows a themed tooltip explaining the feature / setting
        FrameworkElement HelpBadge(string title, string text)
        {
            var badge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1.2),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Cursor = Cursors.Help
            };
            badge.SetResourceReference(Border.BorderBrushProperty, "MutedBrush");
            var q = Txt("?", "MutedBrush", 11, true);
            q.HorizontalAlignment = HorizontalAlignment.Center;
            q.Margin = new Thickness(0, -1, 0, 0);
            badge.Child = q;

            var body = new StackPanel { MaxWidth = 290 };
            var ht = Txt(title, "AccentBrush", 14, true);
            ht.Margin = new Thickness(0, 0, 0, 4);
            body.Children.Add(ht);
            var bt = Txt(text, "TextBrush", 13);
            bt.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(bt);
            var box = Box("CardBrush", "AccentBrush", 10);
            box.Padding = new Thickness(12, 9, 12, 10);
            box.Child = body;
            TextElement.SetFontFamily(box, FontFamily);

            badge.ToolTip = new ToolTip
            {
                Content = box,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HasDropShadow = false,
                Placement = PlacementMode.Right,
                HorizontalOffset = 8
            };
            ToolTipService.SetInitialShowDelay(badge, 80);
            ToolTipService.SetShowDuration(badge, 60000);
            badge.MouseEnter += (s, e) =>
            {
                badge.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                q.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            };
            badge.MouseLeave += (s, e) =>
            {
                badge.SetResourceReference(Border.BorderBrushProperty, "MutedBrush");
                q.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            };
            return badge;
        }

        // a setting label, with a "?" after it when there is help text
        FrameworkElement LabelWith(string label, string help)
        {
            var t = Txt(label, "MutedBrush", 14);
            if (string.IsNullOrEmpty(help)) return t;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(t);
            sp.Children.Add(HelpBadge(label, help));
            return sp;
        }

        void ApplyUi()
        {
            string[] t;
            if (!Themes.TryGetValue(S.Theme, out t)) t = Themes["Midnight"];
            for (int i = 0; i < ThemeKeys.Length; i++)
                Application.Current.Resources[ThemeKeys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(t[i]));
            Opacity = Math.Max(0.3, S.Opacity / 100.0);
            double sc = Math.Max(0.7, S.Scale / 100.0);
            Root.LayoutTransform = new ScaleTransform(sc, sc);
            Width = 1260 * sc;
            Height = 720 * sc;
            ShowInTaskbar = !S.HideTaskbar;
        }

        void Changed()
        {
            Eng.RebuildBinds();
            saveTimer.Stop();
            saveTimer.Start();
        }

        void RefreshRows()
        {
            foreach (var a in rowRefresh) { try { a(); } catch { } }
        }

        void HintUpdate()
        {
            HintText.Text = S.UiKey + " hide / show   \u00B7   " + S.PanicKey + " panic exit";
        }

        void FadeIn(UIElement el, double fromY, int ms, int delayMs)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var begin = TimeSpan.FromMilliseconds(delayMs);
            var tt = new TranslateTransform(0, fromY);
            el.RenderTransform = tt;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease });
        }

        void Toast(string msg)
        {
            toastId++;
            ToastText.Text = msg;
            ToastBox.Visibility = Visibility.Visible;
            var tt = new TranslateTransform(40, 0);
            ToastBox.RenderTransform = tt;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            toastTimer.Stop();
            toastTimer.Tick -= ToastTick;
            toastTimer.Tick += ToastTick;
            toastTimer.Start();
        }

        void ToastTick(object sender, EventArgs e)
        {
            toastTimer.Stop();
            int id = toastId;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (s2, e2) => { if (id == toastId) ToastBox.Visibility = Visibility.Collapsed; };
            ToastBox.BeginAnimation(OpacityProperty, fade);
        }

        void ToggleVisible()
        {
            if (IsVisible) { CloseOptions(); Hide(); }
            else { Show(); Topmost = true; FadeIn(Root, 10, 220, 0); }
        }

        void Quit()
        {
            try { Store.Save(profile, S); Eng.Stop(); } catch { }
            Native.timeEndPeriod(1);
            Application.Current.Shutdown();
            Environment.Exit(0);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Quit();
        }

        void LoadProfile(string name)
        {
            Eng.StopAll();
            profile = name;
            S = Store.Load(name);
            Eng.S = S;
            Eng.RebuildBinds();
            Input.Debug = S.Debug;
            ApplyUi();
            BuildAll();
            Store.Save(name, S);
            Toast("Profile: " + name);
        }

        // ------------------------------------------------------------ window events
        void Title_Down(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
        void Min_Click(object sender, RoutedEventArgs e) { CloseOptions(); Hide(); }
        void Close_Click(object sender, RoutedEventArgs e) { Quit(); }
        void Wiz_Cancel(object sender, RoutedEventArgs e) { EndWizard("Key setup cancelled"); }

        void Start_Click(object sender, RoutedEventArgs e)
        {
            if (Eng.Running) { Eng.StopAll(); Toast("Macros stopped"); }
            else { Eng.Running = true; Toast("Macros running"); }
            RefreshRun();
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Store.Dir);
                Process.Start(new ProcessStartInfo { FileName = Store.Dir, UseShellExecute = true });
            }
            catch (Exception ex) { Log.E("open folder: " + ex.Message); }
        }

        void RefreshRun()
        {
            bool run = Eng.Running;
            StartText.Text = run ? "Stop Macro" : "Start Macro";
            Color target = run ? Color.FromRgb(0xEF, 0x44, 0x44) : ((SolidColorBrush)FindResource("AccentBrush")).Color;
            var brush = StartBtn.Background as SolidColorBrush;
            if (brush == null || brush.IsFrozen) { brush = new SolidColorBrush(target); StartBtn.Background = brush; }
            else brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(250)));
            var fx = StartBtn.Effect as DropShadowEffect;
            if (fx == null) { fx = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Opacity = 0.5, Color = target }; StartBtn.Effect = fx; }
            fx.Color = target;
            if (run) fx.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(10, 28, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else { fx.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null); fx.BlurRadius = 14; }
        }

        // ------------------------------------------------------------ pages
        void BuildAll()
        {
            BuildNav();
            BuildPages();
            RefreshRun();
            HintUpdate();
        }

        void NavItem(Panel host, string icon, string label, bool active, Action click, double indent = 0, double chevAngle = 0)
        {
            var item = Box(active ? "RowBrush" : "SideBrush", null, 12);
            item.Height = indent > 0 ? 44 : 50;
            item.Margin = new Thickness(indent, 0, 0, 6);
            item.Cursor = Cursors.Hand;
            var g = new Grid { Margin = new Thickness(14, 0, 12, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = Txt(icon, active ? "AccentBrush" : "MutedBrush", indent > 0 ? 18 : 20);
            ic.FontFamily = (FontFamily)FindResource("Symbols");
            var lb = Txt(label, active ? "AccentBrush" : "TextBrush", indent > 0 ? 15 : 16, true);
            var ch = Txt("\u203A", "MutedBrush", 22);
            if (chevAngle != 0)
            {
                ch.RenderTransformOrigin = new Point(0.5, 0.5);
                ch.RenderTransform = new RotateTransform(chevAngle);
            }
            Grid.SetColumn(lb, 1);
            Grid.SetColumn(ch, 2);
            g.Children.Add(ic);
            g.Children.Add(lb);
            g.Children.Add(ch);
            item.Child = g;
            item.MouseEnter += (s, e) => item.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            item.MouseLeave += (s, e) => item.SetResourceReference(Border.BackgroundProperty, active ? "RowBrush" : "SideBrush");
            item.MouseLeftButtonUp += (s, e) => click();
            host.Children.Add(item);
        }

        void CatNav(string c, double indent)
        {
            NavItem(NavHost, CatIcon[c], c, page == "cards" && vis[c], () =>
            {
                if (page == "cards") vis[c] = !vis[c]; else { page = "cards"; vis[c] = true; }
                BuildNav();
                BuildPages();
            }, indent);
        }

        void BuildNav()
        {
            NavHost.Children.Clear();
            MiscHost.Children.Clear();
            foreach (var cat in Cats)
            {
                if (Array.IndexOf(GameCats, cat) >= 0) continue;
                CatNav(cat, 0);
            }
            NavItem(NavHost, CatIcon["Games"], "Games", false, () => { gamesOpen = !gamesOpen; BuildNav(); }, 0, gamesOpen ? 90 : 0);
            if (gamesOpen)
                foreach (var cat in GameCats) CatNav(cat, 22);
            NavItem(MiscHost, "\u263A", "Profiles", page == "profiles", () => { page = "profiles"; BuildNav(); BuildPages(); });
            NavItem(MiscHost, "\u2630", "Settings", page == "settings", () => { page = "settings"; BuildNav(); BuildPages(); });
        }

        void BuildPages()
        {
            CloseOptions();
            rowRefresh.Clear();
            CardsHost.Visibility = page == "cards" ? Visibility.Visible : Visibility.Collapsed;
            SettingsHost.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            ProfilesHost.Visibility = page == "profiles" ? Visibility.Visible : Visibility.Collapsed;
            if (page == "cards") BuildCards();
            else if (page == "settings") { BuildSettings(); FadeIn(SettingsHost, 14, 240, 0); }
            else { BuildProfiles(); FadeIn(ProfilesHost, 14, 240, 0); }
        }

        // ------------------------------------------------------------ category cards
        void BuildCards()
        {
            CardsHost.Children.Clear();
            int idx = 0;
            foreach (var cat in Cats)
            {
                if (!vis[cat]) continue;
                var card = MakeCategoryCard(cat);
                CardsHost.Children.Add(card);
                FadeIn(card, 16, 260, 60 * idx);
                idx++;
            }
        }

        Border MakeCategoryCard(string cat)
        {
            var outer = Box("CardBrush", "LineBrush", 14);
            outer.Width = CardW;
            outer.Margin = new Thickness(0, 0, 12, 12);
            outer.VerticalAlignment = VerticalAlignment.Top;
            var st = new StackPanel();
            var head = new Grid { Margin = new Thickness(18, 14, 14, 10), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = Txt(CatIcon[cat], "AccentBrush", 21);
            ic.FontFamily = (FontFamily)FindResource("Symbols");
            ic.Margin = new Thickness(0, 0, 10, 0);
            left.Children.Add(ic);
            left.Children.Add(Txt(cat, "TextBrush", 18, true));
            head.Children.Add(left);
            var chev = Txt(collapsed[cat] ? "\u2304" : "\u2303", "MutedBrush", 16);
            chev.FontFamily = (FontFamily)FindResource("Symbols");
            chev.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(chev);
            head.MouseLeftButtonUp += (s, e) => { collapsed[cat] = !collapsed[cat]; BuildPages(); };
            st.Children.Add(head);
            if (!collapsed[cat])
            {
                var rows = new StackPanel { Margin = new Thickness(12, 0, 12, 6) };
                foreach (var spec in Specs.All)
                    if (spec.Cat == cat) rows.Children.Add(MacroRow(spec));
                st.Children.Add(rows);
            }
            outer.Child = st;
            return outer;
        }

        static bool AnyPiece(FeatureCfg f)
        {
            return Specs.Pieces.Any(p => !string.IsNullOrEmpty(f.Str["bind_" + p]));
        }

        string BindText(FeatureSpec spec, FeatureCfg f)
        {
            if (Specs.IsPieceKind(spec.Kind))
            {
                int n = Specs.Pieces.Count(p => !string.IsNullOrEmpty(f.Str["bind_" + p]));
                return n == 0 ? "-" : n + (n == 1 ? " key" : " keys");
            }
            return string.IsNullOrEmpty(f.Bind) ? "-" : f.Bind;
        }

        void SetEnabled(FeatureSpec spec, FeatureCfg f, bool on)
        {
            f.Enabled = on;
            if (!on) Eng.StopFeature(spec.Id);    // never leave a turned-off macro running or holding a click
            Changed();
            RefreshRows();
        }

        // one macro: name + "?" | on/off switch | key box | dots menu
        Border MacroRow(FeatureSpec spec)
        {
            var f = S.Features[spec.Id];
            var row = Box("RowBrush", null, 10);
            row.Height = 48;
            row.Margin = new Thickness(0, 0, 0, 8);
            row.Padding = new Thickness(12, 0, 6, 0);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var nt = Txt(spec.Name, "TextBrush", 14);
            nt.MaxWidth = 120;
            nt.TextTrimming = TextTrimming.CharacterEllipsis;
            nameBox.Children.Add(nt);
            if (!string.IsNullOrEmpty(spec.Help)) nameBox.Children.Add(HelpBadge(spec.Name, spec.Help));
            g.Children.Add(nameBox);

            // on/off switch on EVERY macro (turning off keeps the key)
            var sw = new CheckBox { Style = St("Switch"), IsChecked = f.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            sw.ToolTip = "Turn this macro on / off";
            Action dim = () => nt.Opacity = f.Enabled ? 1.0 : 0.5;
            dim();
            sw.Click += (s, e) => SetEnabled(spec, f, sw.IsChecked == true);
            rowRefresh.Add(() => { sw.IsChecked = f.Enabled; dim(); });
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);

            if (!spec.Auto)
            {
                var kb = new Button
                {
                    Style = St("FlatBtn"),
                    Padding = new Thickness(8, 4, 8, 4),
                    MinWidth = 54,
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Action show = () => kb.Content = BindText(spec, f);
                show();
                rowRefresh.Add(show);
                if (Specs.IsPieceKind(spec.Kind)) kb.Click += (s, e) => ShowOptions(spec.Id, kb);
                else kb.Click += (s, e) => CaptureKey(kb, () => f.Bind, v => { f.Bind = v; f.Enabled = v != ""; }, show, "trigger", null);
                Grid.SetColumn(kb, 2);
                g.Children.Add(kb);
            }

            var dots = new Button { Style = St("WinBtn"), Content = "\u22EE", FontFamily = (FontFamily)FindResource("Symbols"), FontSize = 18, Width = 28, Height = 30 };
            dots.Click += (s, e) => ShowOptions(spec.Id, dots);
            Grid.SetColumn(dots, 3);
            g.Children.Add(dots);
            row.Child = g;
            row.MouseEnter += (s, e) => row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
            row.MouseLeave += (s, e) => row.SetResourceReference(Border.BackgroundProperty, "RowBrush");
            return row;
        }

        // ------------------------------------------------------------ the dots menu
        void CloseOptions()
        {
            if (popup != null) { popup.IsOpen = false; popup = null; }
        }

        void ShowOptions(string id, FrameworkElement anchor)
        {
            CloseOptions();
            var spec = Specs.ById[id];
            var f = S.Features[id];
            var card = Box("CardBrush", "LineBrush", 14);
            card.Padding = new Thickness(18, 14, 18, 4);
            TextElement.SetFontFamily(card, FontFamily);
            TextElement.SetFontSize(card, 14);
            card.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            var st = new StackPanel { Width = RowW };
            var head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(Txt(spec.Name, "TextBrush", 18, true));
            if (!string.IsNullOrEmpty(spec.Help)) title.Children.Add(HelpBadge(spec.Name, spec.Help));
            head.Children.Add(title);
            var x = new Button { Style = St("WinBtn"), Content = "\uE8BB", HorizontalAlignment = HorizontalAlignment.Right, Width = 28, Height = 28 };
            x.Click += (s, e) => CloseOptions();
            head.Children.Add(x);
            st.Children.Add(head);

            st.Children.Add(SwitchRow("Enabled", f.Enabled, v => SetEnabled(spec, f, v), PopRow, "Turn this macro on or off without losing its key."));

            if (!spec.Auto && !Specs.IsPieceKind(spec.Kind))
                st.Children.Add(BindRow("Trigger key", () => f.Bind, v => { f.Bind = v; f.Enabled = v != ""; }, PopRow, "trigger", null));
            st.Children.Add(SliderRow(spec.DelayLabel, 0, spec.DelayMax, f.Delay, " ms", v => f.Delay = v, PopRow, null, OptHelp("delay")));
            foreach (var o in spec.Opts)
            {
                var op = o;
                string help = OptHelp(op.Key);
                if (op.Type == "slider")
                    st.Children.Add(SliderRow(op.Label, op.Min, op.Max, f.Num[op.Key], op.Unit, v => f.Num[op.Key] = v, PopRow, null, help));
                else if (op.Type == "switch")
                    st.Children.Add(SwitchRow(op.Label, f.Num[op.Key] == 1, v => f.Num[op.Key] = v ? 1 : 0, PopRow, help));
                else if (op.Type == "bind")
                    st.Children.Add(BindRow(op.Label, () => f.Str[op.Key],
                        v =>
                        {
                            bool had = AnyPiece(f);
                            f.Str[op.Key] = v;
                            bool has = AnyPiece(f);
                            if (!had && has) f.Enabled = true;
                            if (had && !has) f.Enabled = false;
                        }, PopRow, "trigger", null));
                else if (op.Type == "tiles")
                    st.Children.Add(TilesRow(op.Label, () => f.Str[op.Key], v => f.Str[op.Key] = v, PopRow));
                else
                    st.Children.Add(ChoiceRow(op.Label, op.Choices, f.Str[op.Key], v => f.Str[op.Key] = v, PopRow, help));
            }
            card.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, MaxHeight = 620, Content = st };
            popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Right,
                HorizontalOffset = 10,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = card
            };
            popup.IsOpen = true;
        }

        // ------------------------------------------------------------ settings page
        void BuildSettings()
        {
            SettingsHost.Children.Clear();
            double w = 468, rw = w - 56;

            var perf = Txt("", "MutedBrush", 13);
            perf.TextWrapping = TextWrapping.Wrap;
            perf.Margin = new Thickness(0, 0, 16, 14);
            Action upd = () =>
            {
                double fr = 1000.0 / Math.Max(30, S.Fps);
                perf.Text = string.Format("Frame time {0:0.0} ms. Minimums: key hold {1} ms, click hold {2} ms, grid delay {2} ms, gap {3} ms.",
                    fr, (int)Math.Ceiling(fr * 1.5), (int)Math.Ceiling(fr * 2), (int)Math.Ceiling(fr * 3));
            };
            upd();

            SettingsHost.Children.Add(Section("Appearance", w,
                ChoiceRow("Theme", Settings.ThemeNames, S.Theme,
                    v => { S.Theme = v; Dispatcher.BeginInvoke(new Action(() => { ApplyUi(); BuildAll(); })); }, rw),
                SliderRow("Window opacity", 30, 100, S.Opacity, "%", v => { S.Opacity = v; Opacity = v / 100.0; }, rw),
                SliderRow("UI size", 70, 150, S.Scale, "%", v => S.Scale = v, rw, () => ApplyUi())));

            SettingsHost.Children.Add(Section("Window", w,
                SwitchRow("Hide from taskbar", S.HideTaskbar, v => { S.HideTaskbar = v; ShowInTaskbar = !v; }, rw),
                BindRow("Show / hide UI key", () => S.UiKey, v => S.UiKey = v, rw, "ui", HintUpdate),
                BindRow("Panic key (exit)", () => S.PanicKey, v => S.PanicKey = v, rw, "panic", HintUpdate),
                SwitchRow("Only run while Roblox is focused", S.RobloxOnly, v => S.RobloxOnly = v, rw, "Macros only fire while the Roblox window is in front."),
                SwitchRow("Log every input sent (config/shush.log)", S.Debug, v => { S.Debug = v; Input.Debug = v; }, rw)));

            SettingsHost.Children.Add(Section("Performance", w,
                ChoiceRow("In-game FPS", new[] { "60", "120", "144", "165", "240", "360", "500" }, S.Fps.ToString(),
                    v => { S.Fps = int.Parse(v); upd(); }, rw, "Used by every macro that has no FPS slider of its own."),
                perf,
                SwitchRow("Enforce minimum timings", S.FpsFloor, v => S.FpsFloor = v, rw, "Raises any delay that is shorter than the game can read at your FPS. Macros with their own FPS slider always do this."),
                ButtonRow("Apply recommended timings", ApplyRecommended)));

            SettingsHost.Children.Add(Section("Reset method", w,
                ChoiceRow("Reset input", new[] { "Right Click", "Scroll Down" }, S.ResetMethod, v => S.ResetMethod = v, rw, "What Auto Reset presses to reset an edit."),
                SliderRow("Scroll reset duration", 0, 1000, S.ScrollMs, " ms", v => S.ScrollMs = v, rw),
                SliderRow("Key press duration", 1, 100, S.TapMs, " ms", v => S.TapMs = v, rw)));

            var keyRows = new List<FrameworkElement>();
            keyRows.Add(ButtonRow("Key setup wizard", StartWizard));
            keyRows.Add(ButtonRow("Tropical Royale defaults", () =>
            {
                foreach (var kv in Settings.DefaultKeys) S.Keys[kv.Key] = kv.Value;
                Changed();
                BuildPages();
                Toast("Default keybinds applied");
            }));
            foreach (var kv in KeyList)
            {
                var key = kv[0];
                keyRows.Add(BindRow(kv[1], () => S.Keys[key], v => S.Keys[key] = v, rw, "macro", null));
            }
            SettingsHost.Children.Add(Section("In-game keybinds", w, keyRows.ToArray()));
        }

        void BuildProfiles()
        {
            ProfilesHost.Children.Clear();
            double w = 468, rw = w - 56;
            var nameBox = new TextBox { Style = St("Field"), Width = rw - 16, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
            var saveAs = new Button { Style = St("FlatBtn"), Content = "Save as", Margin = new Thickness(0, 0, 8, 0) };
            saveAs.Click += (s, e) =>
            {
                var n = nameBox.Text.Trim();
                foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch.ToString(), "");
                if (n == "" || n == "_state") { Toast("Enter a valid profile name"); return; }
                Store.Save(n, Store.Clone(S));
                LoadProfile(n);
            };
            var del = new Button { Style = St("FlatBtn"), Content = "Delete", Margin = new Thickness(0, 0, 8, 0) };
            del.Click += (s, e) =>
            {
                if (profile == "default") { Toast("The default profile can't be deleted"); return; }
                Store.Delete(profile);
                LoadProfile("default");
            };
            var open = new Button { Style = St("FlatBtn"), Content = "Open folder" };
            open.Click += OpenFolder_Click;
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            btns.Children.Add(saveAs);
            btns.Children.Add(del);
            btns.Children.Add(open);
            var wrap = new StackPanel { Width = rw };
            wrap.Children.Add(nameBox);
            wrap.Children.Add(btns);
            ProfilesHost.Children.Add(Section("Profiles", w,
                ChoiceRow("Active profile", Store.Profiles().ToArray(), profile,
                    v => Dispatcher.BeginInvoke(new Action(() => LoadProfile(v))), rw),
                wrap));

            string text = "External input macro tool for Tropical Royale. It listens with low-level Windows hooks and sends input with the native SendInput API. It never reads or writes game memory and never injects anything.\n\nTip: create config/about.txt next to the exe and whatever you write in it shows up here.";
            try
            {
                var path = Path.Combine(Store.Dir, "about.txt");
                if (File.Exists(path)) text = File.ReadAllText(path);
            }
            catch { }
            var about = Txt(text, "MutedBrush", 15);
            about.TextWrapping = TextWrapping.Wrap;
            about.Margin = new Thickness(0, 0, 16, 16);
            ProfilesHost.Children.Add(Section("About", w, about));
        }

        // ------------------------------------------------------------ performance preset
        void ApplyRecommended()
        {
            double fr = 1000.0 / Math.Max(30, S.Fps);
            Func<double, int> c = m => (int)Math.Ceiling(fr * m);
            int fps = Math.Min(240, Math.Max(60, S.Fps));
            S.TapMs = c(1.5) + 1;
            foreach (var id in new[] { "drag_edit", "single_edit", "double_edit", "simple_edit", "fb_drag", "fb_double" })
            {
                var f = S.Features[id];
                f.Delay = c(2.5);
                SetNum(f, "click_hold", c(2.5));
                SetNum(f, "gap", c(4));
                SetNum(f, "release_delay", c(2.5));
                SetNum(f, "step_delay", c(1));
                SetNum(f, "tile_dwell", c(1.5));
                SetNum(f, "reset_delay", c(4));
                SetNum(f, "reset_step", c(3));
            }
            // macros with their own FPS slider: just match the slider to your FPS (they time themselves from it)
            foreach (var id in new[] { "fb_drag", "fb_rapid", "fb_instant", "fb_editreset" })
                SetNum(S.Features[id], "fps", fps);
            Changed();
            BuildPages();
            Toast("Timings set for " + S.Fps + " FPS");
        }

        static void SetNum(FeatureCfg f, string key, int v) { if (f.Num.ContainsKey(key)) f.Num[key] = v; }

        // ------------------------------------------------------------ key conflicts + setup wizard
        IEnumerable<string> AllBinds()
        {
            foreach (var kv in S.Features)
            {
                FeatureSpec spec;
                if (!Specs.ById.TryGetValue(kv.Key, out spec)) continue;
                if (Specs.IsPieceKind(spec.Kind))
                {
                    foreach (var p in Specs.Pieces)
                        if (!string.IsNullOrEmpty(kv.Value.Str["bind_" + p])) yield return kv.Value.Str["bind_" + p];
                }
                else if (!string.IsNullOrEmpty(kv.Value.Bind)) yield return kv.Value.Bind;
            }
        }

        // kind: "trigger" = a macro trigger, "macro" = an in-game key, "ui" / "panic" = the app keys
        string Conflict(string name, string kind)
        {
            if (kind != "ui" && name == S.UiKey) return "show/hide key";
            if (kind != "panic" && name == S.PanicKey) return "panic key";
            if ((kind == "ui" || kind == "panic") && AllBinds().Contains(name)) return "macro key";
            if (kind == "trigger" && AllBinds().Contains(name)) return "trigger for another macro";
            return null;
        }

        void StartWizard()
        {
            CloseOptions();
            wizIdx = 0;
            Wizard.Visibility = Visibility.Visible;
            FadeIn(Wizard, 10, 200, 0);
            WizardNext();
        }

        void WizardNext()
        {
            if (wizIdx >= KeyList.Length) { EndWizard("Keybinds saved"); return; }
            var k = KeyList[wizIdx];
            int my = wizIdx;
            WizTitle.Text = "Key setup   " + (wizIdx + 1) + " / " + KeyList.Length;
            WizBody.Text = "Press your in-game " + k[1] + " key now.";
            Action<string> cb = null;
            cb = name => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Wizard.Visibility != Visibility.Visible || my != wizIdx) return;
                if (name != "ESC")
                {
                    var why = Conflict(name, "macro");
                    if (why != null) { Toast("That is your " + why + " - pick another"); WizardNext(); return; }
                    S.Keys[k[0]] = name;
                }
                wizIdx++;
                WizardNext();
            }));
            Eng.Capture = cb;
        }

        void EndWizard(string msg)
        {
            Eng.Capture = null;
            Wizard.Visibility = Visibility.Collapsed;
            Changed();
            if (page == "settings") BuildPages();
            if (msg != null) Toast(msg);
        }

        // ------------------------------------------------------------ row helpers
        Border Section(string title, double w, params FrameworkElement[] rows)
        {
            var card = Box("CardBrush", "LineBrush", 14);
            card.Width = w;
            card.Margin = new Thickness(0, 0, 14, 14);
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Padding = new Thickness(20, 16, 20, 6);
            var st = new StackPanel();
            var h = Txt(title, "TextBrush", 19, true);
            h.Margin = new Thickness(0, 0, 0, 16);
            st.Children.Add(h);
            foreach (var r in rows) st.Children.Add(r);
            card.Child = st;
            return card;
        }

        FrameworkElement ButtonRow(string label, Action click)
        {
            var b = new Button { Style = St("FlatBtn"), Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
            b.Click += (s, e) => click();
            return b;
        }

        FrameworkElement BindRow(string label, Func<string> get, Action<string> set, double w, string kind, Action after)
        {
            var g = new Grid { Width = w, Margin = new Thickness(0, 0, 16, 14) };
            g.Children.Add(Txt(label, "MutedBrush", 14));
            var b = new Button { Style = St("FlatBtn"), MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Right };
            Action show = () => b.Content = string.IsNullOrEmpty(get()) ? "-" : get();
            show();
            b.Click += (s, e) => CaptureKey(b, get, set, show, kind, after);
            g.Children.Add(b);
            return g;
        }

        void CaptureKey(Button b, Func<string> get, Action<string> set, Action show, string kind, Action after)
        {
            b.Content = "press a key...";
            Action<string> cb = null;
            cb = name => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (name == "ESC" && (kind == "ui" || kind == "panic")) { show(); return; }
                if (name != "ESC" && name != get())     // re-pressing the key it already has is never a conflict
                {
                    var why = Conflict(name, kind);
                    if (why != null) { Toast("That key is already your " + why); show(); return; }
                }
                set(name == "ESC" ? "" : name);
                show();
                Changed();
                RefreshRows();
                if (after != null) after();
            }));
            Eng.Capture = cb;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                if (Eng.Capture == cb) { Eng.Capture = null; show(); }
            };
            t.Start();
        }

        FrameworkElement SwitchRow(string label, bool val, Action<bool> set, double w, string help = null)
        {
            var g = new Grid { Width = w, Margin = new Thickness(0, 0, 16, 14) };
            g.Children.Add(LabelWith(label, help));
            var c = new CheckBox { Style = St("Switch"), IsChecked = val, HorizontalAlignment = HorizontalAlignment.Right };
            c.Click += (s, e) => { set(c.IsChecked == true); Changed(); };
            g.Children.Add(c);
            return g;
        }

        FrameworkElement SliderRow(string label, int min, int max, int val, string unit, Action<int> set, double w, Action release = null, string help = null)
        {
            var p = new StackPanel { Width = w, Margin = new Thickness(0, 0, 16, 14) };
            var head = new Grid();
            head.Children.Add(LabelWith(label, help));
            var v = Txt(val + unit, "AccentBrush", 14, true);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(v);
            p.Children.Add(head);
            var sl = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = val,
                Style = St("Sl"),
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Margin = new Thickness(0, 6, 0, 0)
            };
            sl.ValueChanged += (s, e) =>
            {
                int x = (int)Math.Round(sl.Value);
                v.Text = x + unit;
                set(x);
                Changed();
            };
            if (release != null)
                sl.PreviewMouseLeftButtonUp += (s, e) => Dispatcher.BeginInvoke(release, DispatcherPriority.Background);
            p.Children.Add(sl);
            return p;
        }

        FrameworkElement ChoiceRow(string label, string[] choices, string cur, Action<string> set, double w, string help = null)
        {
            var p = new StackPanel { Width = w, Margin = new Thickness(0, 0, 16, 10) };
            p.Children.Add(LabelWith(label, help));
            Panel host;
            if (choices.Length == 9) host = new UniformGrid { Columns = 3, Width = 330, HorizontalAlignment = HorizontalAlignment.Left };
            else host = new WrapPanel();
            var chips = new List<ToggleButton>();
            foreach (var c in choices)
            {
                var val = c;
                var t = new ToggleButton { Content = c, IsChecked = c == cur, Style = St("Chip") };
                t.Click += (s, e) =>
                {
                    foreach (var o in chips) o.IsChecked = ReferenceEquals(o, t);
                    set(val);
                    Changed();
                };
                chips.Add(t);
                host.Children.Add(t);
            }
            p.Children.Add(host);
            return p;
        }

        FrameworkElement TilesRow(string label, Func<string> get, Action<string> set, double w)
        {
            var p = new StackPanel { Width = w, Margin = new Thickness(0, 0, 16, 12) };
            p.Children.Add(Txt(label, "MutedBrush", 14));
            var grid = new UniformGrid { Columns = 3, Width = 174, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            string mask = get();
            if (mask == null || mask.Length != 9) mask = "000000000";
            for (int i = 0; i < 9; i++)
            {
                int idx = i;
                var t = new ToggleButton { IsChecked = mask[i] == '1', Style = St("Chip"), Margin = new Thickness(0, 0, 6, 6), Width = 52, Height = 42 };
                t.Click += (s, e) =>
                {
                    var cur = get();
                    if (cur == null || cur.Length != 9) cur = "000000000";
                    var arr = cur.ToCharArray();
                    arr[idx] = t.IsChecked == true ? '1' : '0';
                    set(new string(arr));
                    Changed();
                };
                grid.Children.Add(t);
            }
            p.Children.Add(grid);
            return p;
        }
    }
}
