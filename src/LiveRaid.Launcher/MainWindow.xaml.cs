using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LiveRaid.Launcher
{
    public partial class MainWindow : Window
    {
        private static readonly Brush Ink = Hex("#DAD5C3");
        private static readonly Brush Amber = Hex("#E9A23B");
        private static readonly Brush Red = Hex("#E05A47");
        private static readonly Brush Green = Hex("#86D46F");
        private static readonly Brush Off = Hex("#3A3A33");
        private static readonly Brush Idle = Hex("#2C2E23");

        /// <summary>A game folder given on the command line (checks), used instead of looking for one.</summary>
        public string ForcedRoot;

        private GameInstall _game;
        private GameInstall.State _state;
        private bool _busy;
        private Mode _shown = Mode.Unknown;
        private readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _typer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
        private readonly Queue<(string text, Brush brush)> _pending = new Queue<(string, Brush)>();
        private TextBlock _typing;
        private string _typingText;
        private int _typed;

        public MainWindow()
        {
            InitializeComponent();
            L.Localize(this);
            Subtitle.Text = L.Tr("ПУЛЬТ РЕЖИМОВ  ·  ") + Version();
            Loaded += (s, e) =>
            {
                DrawTopo();
                Find();
            };
            _poll.Tick += (s, e) => Refresh(false);
            _typer.Tick += (s, e) => Type();
            _poll.Start();
        }

        private void OnLanguage(object sender, RoutedEventArgs e)
        {
            L.Set(!L.Ru);
            L.Localize(this);
            Subtitle.Text = L.Tr("ПУЛЬТ РЕЖИМОВ  ·  ") + Version();
            _shown = Mode.Unknown;
            Refresh(true);
        }

        private static string Version()
        {
            Version v = typeof(MainWindow).Assembly.GetName().Version;
            return $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        // ------------------------------------------------------------------ the game folder

        private static string Saved => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiveRaid Launcher", "game.txt");

        private void Find()
        {
            if (ForcedRoot != null)
            {
                Use(ForcedRoot);
                return;
            }

            string here = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string[] candidates =
            {
                here,
                System.IO.Path.GetDirectoryName(here),
                File.Exists(Saved) ? File.ReadAllText(Saved).Trim() : null,
                @"C:\Games\SPT",
                @"C:\SPT",
            };
            foreach (string c in candidates)
            {
                if (GameInstall.LooksLikeGame(c))
                {
                    Use(c);
                    return;
                }
            }

            Write(L.Tr("игра не найдена рядом с лаунчером - нажмите «ВЫБРАТЬ ИГРУ…» и укажите EscapeFromTarkov.exe"), 1);
        }

        private void Use(string root)
        {
            _game = new GameInstall(root);
            PathText.Text = root;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Saved));
                File.WriteAllText(Saved, root);
            }
            catch
            {
                // only a convenience
            }

            Write(L.Tr("игра: ") + root, 0);
            Refresh(true);
            GameInstall.State s = _state;
            if (s == null)
            {
                return;
            }

            Write(L.Tr("сейчас: ") + Describe(s), s.Mode == Mode.Mixed ? 1 : 3);
            if (!s.HasSingle || !s.HasOnline)
            {
                Write(L.Tr("в «LiveRaid Launcher\\editions» нет ") + (!s.HasSingle && !s.HasOnline ? L.Tr("обеих редакций") : !s.HasSingle ? L.Tr("одиночной редакции") : L.Tr("редакции Online")) +
                      L.Tr(" - распакуйте архив LiveRaid 0.9+ в папку игры целиком"), 1);
            }
        }

        private static string Describe(GameInstall.State s)
        {
            string mod = s.Edition == "" ? L.Tr("LiveRaid не установлен") : (s.Edition == "Online" ? "LiveRaid Online " : "LiveRaid ") + s.Version +
                         L.Tr(", ветка «") + GameInstall.Title(s.Channel) + "»";
            string fika = s.FikaLive ? "Fika " + s.FikaVersion + L.Tr(" в игре") : s.FikaParked ? "Fika " + s.FikaVersion + L.Tr(" отложена") : L.Tr("Fika нет");
            switch (s.Mode)
            {
                case Mode.Single:
                    return L.Tr("одиночный режим (") + mod + ", " + fika + ")";
                case Mode.Online:
                    return L.Tr("онлайн-режим (") + mod + ", " + fika + ")";
                case Mode.Mixed:
                    return L.Tr("НЕСОВПАДЕНИЕ: ") + mod + L.Tr(" при ") + fika + L.Tr(" - мод выключится; переключите режим, чтобы всё сошлось");
                default:
                    return mod + ", " + fika;
            }
        }

        // ------------------------------------------------------------------ state on screen

        private void Refresh(bool force)
        {
            Clock.Text = DateTime.Now.ToString("HH:mm:ss");
            if (_game == null || (_busy && !force))
            {
                return;
            }

            try
            {
                _state = _game.Read();
            }
            catch (Exception e)
            {
                Write(L.Tr("не удалось прочитать папку игры: ") + e.Message, 2);
                return;
            }

            GameInstall.State s = _state;
            Lamp(LampGame, s.GameRunning ? Amber : Off);
            ValGame.Text = s.GameRunning ? L.Tr("запущена") : L.Tr("закрыта");
            Lamp(LampServer, s.ServerRunning ? Green : Off);
            ValServer.Text = s.ServerRunning ? L.Tr("работает") : L.Tr("остановлен");
            Lamp(LampFika, s.FikaLive ? Green : s.FikaParked ? Amber : Off);
            ValFika.Text = s.FikaLive ? L.Tr("в игре ") + s.FikaVersion : s.FikaParked ? L.Tr("отложена ") + s.FikaVersion : L.Tr("нет");
            Lamp(LampMod, s.Mode == Mode.Mixed ? Red : s.Edition == "" ? Off : Green);
            ValMod.Text = s.Edition == "" ? L.Tr("нет") : (s.Edition == "Online" ? "Online " : L.Tr("Одиночный ")) + s.Version;

            Mode m = s.Mode == Mode.Mixed ? (s.Edition == "Online" ? Mode.Online : Mode.Single) : s.Mode;
            if (m != _shown || force)
            {
                Show(m, s.Mode == Mode.Mixed, animate: _shown != Mode.Unknown);
            }

            ShowChannel(s);
            LeverCaption.Text = s.Running.Length > 0 ? L.Tr("ЗАКРОЙТЕ: ") + s.Running.ToUpperInvariant() : s.Mode == Mode.Mixed ? L.Tr("РЕЖИМ НЕ СОШЁЛСЯ - ПЕРЕВЕДИТЕ") : L.Tr("ПЕРЕВЕДИТЕ ТУМБЛЕР");
            LeverCaption.Foreground = s.Running.Length > 0 || s.Mode == Mode.Mixed ? Amber : Hex("#8B8774");
        }

        private void ShowChannel(GameInstall.State s)
        {
            bool test = s.Channel == Channel.Test;
            bool split = s.Edition != "" && s.ServerVersion != "" && s.ServerChannel != s.Channel;
            ValChannel.Text = s.Edition == "" && s.ServerVersion == "" ? "—" : GameInstall.Title(s.Channel).ToUpperInvariant();
            ValChannel.Foreground = split ? Red : test ? Amber : Green;
            Branch(BtnStable, L.Tr("СТАБИЛЬНАЯ"), !test, s.HasStable);
            Branch(BtnTest, L.Tr("ТЕСТОВАЯ"), test, s.HasTest);
            if (split)
            {
                ChannelNote.Text = L.Tr("клиент LiveRaid и сервер из разных веток (сервер ") + s.ServerVersion + L.Tr(") - переключите ветку ещё раз, чтобы всё сошлось");
                ChannelNote.Foreground = Red;
            }
            else if (test)
            {
                ChannelNote.Text = L.Tr("в игре тестовая ветка (новые поведения ботов). Стабильная версия отложена и возвращается кнопкой «СТАБИЛЬНАЯ»; у каждой ветки свои настройки F12");
                ChannelNote.Foreground = Amber;
            }
            else
            {
                ChannelNote.Text = s.HasTest
                    ? L.Tr("в игре стабильная версия. Тестовая ветка установлена и ставится кнопкой «ТЕСТОВАЯ» (стабильная при этом сохраняется)")
                    : L.Tr("в игре стабильная версия. Тестовой ветки нет - распакуйте архив LiveRaid-Test в папку игры");
                ChannelNote.Foreground = Hex("#8B8774");
            }
        }

        private void Branch(Button b, string title, bool active, bool available)
        {
            b.Content = active ? "● " + title : title;
            if (active)
            {
                b.Foreground = Green;
            }
            else
            {
                b.ClearValue(ForegroundProperty);
            }

            b.IsEnabled = !_busy && (active || available);
        }

        private void Show(Mode m, bool mixed, bool animate)
        {
            _shown = m;
            bool online = m == Mode.Online;
            MoveLever(online ? 36 : -36, animate);
            Lamp(LedSolo, m == Mode.Single ? (mixed ? Red : Green) : Off);
            Lamp(LedFika, online ? (mixed ? Red : Green) : Off);
            Card(CardSingle, StampSingle, StampSingleScale, m == Mode.Single, animate);
            Card(CardOnline, StampOnline, StampOnlineScale, online, animate);
            Hint(HintSingle, m == Mode.Single, mixed);
            Hint(HintOnline, online, mixed);
        }

        private static void Hint(TextBlock hint, bool active, bool mixed)
        {
            hint.Text = active ? (mixed ? L.Tr("● НЕ СОШЛОСЬ: Fika и редакция LiveRaid разные") : L.Tr("● СЕЙЧАС В ИГРЕ")) : L.Tr("▸ НАЖМИТЕ, ЧТОБЫ ПЕРЕКЛЮЧИТЬСЯ");
            hint.Foreground = active ? (mixed ? Red : Green) : Hex("#8A6324");
        }

        private static void Lamp(Ellipse lamp, Brush brush)
        {
            lamp.Fill = brush;
            lamp.Effect = brush == Off ? null : new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = ((SolidColorBrush)brush).Color, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9,
            };
        }

        private void MoveLever(double angle, bool animate)
        {
            if (!animate)
            {
                StickAngle.BeginAnimation(RotateTransform.AngleProperty, null);
                StickAngle.Angle = angle;
                return;
            }

            DoubleAnimation a = new DoubleAnimation(angle, TimeSpan.FromMilliseconds(520))
            {
                EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut },
            };
            StickAngle.BeginAnimation(RotateTransform.AngleProperty, a);
        }

        private static void Card(Border card, Border stamp, ScaleTransform scale, bool active, bool animate)
        {
            card.BorderBrush = active ? Amber : Idle;
            card.Opacity = active ? 1 : 0.72;
            if (!active)
            {
                stamp.BeginAnimation(OpacityProperty, null);
                stamp.Opacity = 0;
                return;
            }

            if (!animate)
            {
                stamp.Opacity = 1;
                return;
            }

            // a rubber stamp: comes down big and lands
            stamp.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            DoubleAnimation land = new DoubleAnimation(2.2, 1, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, land);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, land);
        }

        // ------------------------------------------------------------------ switching

        private void OnLever(object sender, MouseButtonEventArgs e) => Switch(_shown == Mode.Online ? Mode.Single : Mode.Online);

        private void OnCardSingle(object sender, MouseButtonEventArgs e) => Switch(Mode.Single);

        private void OnCardOnline(object sender, MouseButtonEventArgs e) => Switch(Mode.Online);

        private async void Switch(Mode target)
        {
            if (_busy)
            {
                return;
            }

            if (_game == null)
            {
                Write(L.Tr("сначала укажите папку игры («ВЫБРАТЬ ИГРУ…»)"), 1);
                return;
            }

            Refresh(true);
            if (_state.Mode == target)
            {
                Write(L.Tr("режим «") + GameInstall.Title(target) + L.Tr("» уже включён"), 0);
                return;
            }

            _busy = true;
            Mode before = _shown;
            MoveLever(target == Mode.Online ? 36 : -36, true);
            LeverCaption.Text = L.Tr("ИДЁТ ПЕРЕКЛЮЧЕНИЕ…");
            LeverCaption.Foreground = Amber;
            Write(L.Tr("── переключение: «") + GameInstall.Title(before == Mode.Unknown ? (target == Mode.Online ? Mode.Single : Mode.Online) : before) + "» → «" + GameInstall.Title(target) + "»", 1);
            bool ok = false;
            try
            {
                GameInstall game = _game;
                await Task.Run(() => game.SwitchTo(target, (text, level) => Dispatcher.Invoke(() => Write(text, level))));
                ok = true;
            }
            catch (Exception ex)
            {
                if (!(ex is InvalidOperationException))
                {
                    Write(ex.GetType().Name + ": " + ex.Message, 2);
                }
                else
                {
                    Write(ex.Message, 2);
                }
            }

            _busy = false;
            if (ok)
            {
                Write(L.Tr("готово: режим «") + GameInstall.Title(target) + L.Tr("». Можно запускать сервер и игру"), 3);
                _shown = Mode.Unknown;
                Refresh(true);
                Show(target, _state.Mode == Mode.Mixed, animate: true);
            }
            else
            {
                Write(L.Tr("режим не переключён"), 2);
                MoveLever(before == Mode.Online ? 36 : -36, true);
                Refresh(true);
            }
        }

        private void OnStable(object sender, RoutedEventArgs e) => SwitchBranch(Channel.Stable);

        private void OnTest(object sender, RoutedEventArgs e) => SwitchBranch(Channel.Test);

        private async void SwitchBranch(Channel target)
        {
            if (_busy || _game == null)
            {
                return;
            }

            Refresh(true);
            bool split = _state.ServerVersion != "" && _state.ServerChannel != _state.Channel;
            if (_state.Channel == target && !split)
            {
                Write(L.Tr("ветка «") + GameInstall.Title(target) + L.Tr("» уже в игре"), 0);
                return;
            }

            _busy = true;
            BtnStable.IsEnabled = BtnTest.IsEnabled = false;
            Write(L.Tr("── ветка: «") + GameInstall.Title(_state.Channel) + "» → «" + GameInstall.Title(target) + "»", 1);
            bool ok = false;
            try
            {
                GameInstall game = _game;
                await Task.Run(() => game.SwitchChannel(target, (text, level) => Dispatcher.Invoke(() => Write(text, level))));
                ok = true;
            }
            catch (Exception ex)
            {
                Write(ex is InvalidOperationException ? ex.Message : ex.GetType().Name + ": " + ex.Message, 2);
            }

            _busy = false;
            Write(ok ? L.Tr("готово: ветка «") + GameInstall.Title(target) + L.Tr("». Можно запускать сервер и игру") : L.Tr("ветка не переключена"), ok ? 3 : 2);
            Refresh(true);
        }

        // ------------------------------------------------------------------ log

        private void Write(string text, int level)
        {
            Brush brush = level == 2 ? Red : level == 1 ? Amber : level == 3 ? Green : Ink;
            _pending.Enqueue(("[" + DateTime.Now.ToString("HH:mm:ss") + "]  " + text, brush));
            if (!_typer.IsEnabled)
            {
                _typer.Start();
            }
        }

        private void Type()
        {
            if (_typing == null)
            {
                if (_pending.Count == 0)
                {
                    _typer.Stop();
                    return;
                }

                (string text, Brush brush) = _pending.Dequeue();
                _typing = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12.5, Foreground = brush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                _typingText = text;
                _typed = 0;
                LogLines.Children.Add(_typing);
                while (LogLines.Children.Count > 300)
                {
                    LogLines.Children.RemoveAt(0);
                }
            }

            // faster when lines pile up
            _typed = Math.Min(_typingText.Length, _typed + 3 + _pending.Count * 4);
            _typing.Text = _typingText.Substring(0, _typed) + (_typed < _typingText.Length ? "▌" : "");
            LogScroll.ScrollToEnd();
            if (_typed >= _typingText.Length)
            {
                _typing = null;
            }
        }

        // ------------------------------------------------------------------ buttons

        private void OnStartServer(object sender, RoutedEventArgs e) => Start(_game.ServerFolder + @"\SPT.Server.exe", L.Tr("сервер SPT"));

        private void OnStartLauncher(object sender, RoutedEventArgs e) => Start(_game.ServerFolder + @"\SPT.Launcher.exe", L.Tr("лаунчер SPT"));

        private void Start(string rel, string what)
        {
            if (_game == null)
            {
                return;
            }

            if (_state?.Mode == Mode.Mixed)
            {
                Write(L.Tr("внимание: режим не сошёлся - LiveRaid в игре выключится; сначала переключите режим"), 1);
            }

            string exe = System.IO.Path.Combine(_game.Root, rel);
            if (!File.Exists(exe))
            {
                Write(L.Tr("не найден ") + rel, 2);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = System.IO.Path.GetDirectoryName(exe), UseShellExecute = true });
                Write(L.Tr("запущен ") + what, 0);
            }
            catch (Exception ex)
            {
                Write(L.Tr("не удалось запустить ") + what + ": " + ex.Message, 2);
            }
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            if (_game != null)
            {
                Process.Start(new ProcessStartInfo(_game.Root) { UseShellExecute = true });
            }
        }

        private void OnPickGame(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.Tr("Где EscapeFromTarkov.exe вашей SPT?"), Filter = "EscapeFromTarkov.exe|EscapeFromTarkov.exe", CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) == true)
            {
                string root = System.IO.Path.GetDirectoryName(dlg.FileName);
                if (GameInstall.LooksLikeGame(root))
                {
                    Use(root);
                }
                else
                {
                    Write(L.Tr("в этой папке нет BepInEx - это не SPT"), 2);
                }
            }
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed && !(e.OriginalSource is Ellipse) && e.Source == Frame)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // mouse already up
                }
            }
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        // ------------------------------------------------------------------ the map under it all

        /// <summary>Contour lines of a made-up terrain - a few hills, every ring a little uneven.</summary>
        private void DrawTopo()
        {
            Topo.Children.Clear();
            double w = Topo.ActualWidth > 0 ? Topo.ActualWidth : 1000;
            double h = Topo.ActualHeight > 0 ? Topo.ActualHeight : 650;
            Random rnd = new Random(1981);
            (double x, double y, int rings)[] hills = { (w * 0.12, h * 0.85, 11), (w * 0.86, h * 0.18, 9), (w * 0.58, h * 0.96, 7), (w * 0.4, h * 0.05, 5) };
            foreach (var hill in hills)
            {
                double p1 = rnd.NextDouble() * 6.28, p2 = rnd.NextDouble() * 6.28, p3 = rnd.NextDouble() * 6.28;
                for (int ring = 1; ring <= hill.rings; ring++)
                {
                    double r = ring * 34;
                    PathFigure fig = new PathFigure { IsClosed = true };
                    PolyLineSegment seg = new PolyLineSegment();
                    for (int i = 0; i <= 120; i++)
                    {
                        double a = i / 120.0 * Math.PI * 2;
                        double wob = 1 + 0.16 * Math.Sin(3 * a + p1 + ring * 0.25) + 0.09 * Math.Sin(5 * a + p2) + 0.05 * Math.Sin(9 * a + p3 + ring * 0.4);
                        Point pt = new Point(hill.x + Math.Cos(a) * r * wob * 1.25, hill.y + Math.Sin(a) * r * wob * 0.9);
                        if (i == 0)
                        {
                            fig.StartPoint = pt;
                        }
                        else
                        {
                            seg.Points.Add(pt);
                        }
                    }

                    fig.Segments.Add(seg);
                    Topo.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = new PathGeometry(new[] { fig }),
                        Stroke = ring % 5 == 0 ? Hex("#3A3320") : Hex("#22241A"),
                        StrokeThickness = ring % 5 == 0 ? 1.4 : 1,
                    });
                }
            }

            // a faint map grid
            for (double x = 0; x < w; x += 80)
            {
                Topo.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = h, Stroke = Hex("#14150F"), StrokeThickness = 1 });
            }

            for (double y = 0; y < h; y += 80)
            {
                Topo.Children.Add(new Line { X1 = 0, Y1 = y, X2 = w, Y2 = y, Stroke = Hex("#14150F"), StrokeThickness = 1 });
            }
        }

        private static SolidColorBrush Hex(string hex)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}
