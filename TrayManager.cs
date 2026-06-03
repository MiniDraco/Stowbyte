using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
// Disambiguate the handful of names that exist in both WPF and WinForms -> use the WPF ones.
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Cursors = System.Windows.Input.Cursors;

namespace Loadout;

/// <summary>
/// Owns the tray icon and the pop-up grid of app cards. Left-click the tray icon to
/// toggle the grid. Each card shows the app's real icon with a translucent green box
/// when it's "alive" on the fast drive, or red when it's offloaded to D.
/// </summary>
public class TrayManager : IDisposable
{
    private WinForms.NotifyIcon _ni = null!;
    private AppConfig _cfg = new();

    private Window? _popup;
    private WrapPanel? _grid;
    private readonly HashSet<string> _busy = new();              // AnchorPaths with a move in flight
    private readonly Dictionary<string, int> _pct = new();        // AnchorPath -> current move %
    private readonly Dictionary<string, TextBlock> _pctLabels = new(); // live % label per visible card
    private bool _suspendHide;                                    // true while we intentionally show a dialog

    public void Init()
    {
        _cfg = ConfigStore.Load();
        EnsureSetup();

        _ni = new WinForms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Visible = true,
            Text = "Stowbyte"
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => TogglePopup());
        menu.Items.Add("Add app…", null, (_, _) => AddApp());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings(false));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => System.Windows.Application.Current.Shutdown());
        _ni.ContextMenuStrip = menu;

        _ni.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) TogglePopup();
        };
    }

    // ---------- popup ----------

    private void BuildPopup()
    {
        _grid = new WrapPanel { Margin = new Thickness(10), Width = 452 };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 440,
            Content = _grid
        };

        var title = new TextBlock
        {
            Text = "Stowbyte",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var gearBtn = new Button { Content = "⚙", Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Settings" };
        gearBtn.Click += (_, _) => OpenSettings(false);
        DockPanel.SetDock(gearBtn, Dock.Right);

        var addBtn = new Button { Content = "+ Add app", Padding = new Thickness(8, 2, 8, 2) };
        addBtn.Click += (_, _) => AddApp();
        DockPanel.SetDock(addBtn, Dock.Right);

        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 2) };
        header.Children.Add(gearBtn);
        header.Children.Add(addBtn);
        header.Children.Add(title);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(scroller);

        var panel = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(238, 26, 28, 34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = root
        };

        _popup = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
            Width = 480,
            Title = "Stowbyte",
            Content = panel
        };
        // Only auto-hide on real focus loss — not when WE pop a dialog (Add app / Settings / message).
        _popup.Deactivated += (_, _) => { if (!_suspendHide) _popup?.Hide(); };
    }

    private void TogglePopup()
    {
        if (_popup == null) BuildPopup();
        if (_popup!.IsVisible) { _popup.Hide(); return; }

        Refresh();
        _popup.Show();
        _popup.UpdateLayout();

        var wa = SystemParameters.WorkArea;
        _popup.Left = wa.Right - _popup.ActualWidth - 12;
        _popup.Top = wa.Bottom - _popup.ActualHeight - 12;
        _popup.Activate();
    }

    private void Refresh()
    {
        if (_grid == null) return;
        _grid.Children.Clear();

        if (_cfg.Apps.Count == 0)
        {
            _grid.Children.Add(new TextBlock
            {
                Text = "No apps yet. Click “+ Add app” and pick a program’s folder.",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                TextWrapping = TextWrapping.Wrap,
                Width = 430,
                Margin = new Thickness(6, 10, 6, 18)
            });
            return;
        }

        // One process snapshot for the whole refresh (cheap per-card "running" checks after this).
        var running = Engine.RunningModulePaths();
        foreach (var a in _cfg.Apps)
            _grid.Children.Add(BuildCard(a, running));
    }

    /// <summary>Runs a modal dialog without the popup auto-hiding behind it.</summary>
    private T WithDialog<T>(Func<T> show)
    {
        bool prevSuspend = _suspendHide;
        bool wasTopmost = _popup?.Topmost ?? false;
        _suspendHide = true;
        if (_popup != null) _popup.Topmost = false; // let the dialog come to the front
        try { return show(); }
        finally
        {
            _suspendHide = prevSuspend;
            if (_popup != null)
            {
                _popup.Topmost = wasTopmost;
                if (_popup.IsVisible) _popup.Activate();
            }
        }
    }

    private void WithDialog(Action show) => WithDialog(() => { show(); return true; });

    private UIElement BuildCard(ManagedApp a, IReadOnlyCollection<string> runningPaths)
    {
        bool busy = _busy.Contains(a.AnchorPath);
        AppState state = busy ? AppState.Busy : Engine.GetState(a);
        // A loaded app that's currently running can't be frozen (its files are locked).
        bool running = !busy && state == AppState.Loaded && Engine.IsInUse(a, runningPaths);

        var card = new Border
        {
            Width = 102,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255))
        };

        var stack = new StackPanel { Margin = new Thickness(8, 10, 8, 8) };

        // icon + status overlay
        var iconGrid = new Grid { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center };

        var img = new System.Windows.Controls.Image
        {
            Source = IconHelper.GetIcon(a.ExePath),
            Width = 48,
            Height = 48,
            Cursor = Cursors.Hand
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        img.MouseLeftButtonUp += (_, _) => Launch(a);
        iconGrid.Children.Add(img);

        var overlay = new Border
        {
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
            Background = new SolidColorBrush(state switch
            {
                AppState.Loaded => Color.FromArgb(96, 40, 220, 120),   // translucent green = alive on C
                AppState.Offloaded => Color.FromArgb(70, 235, 70, 70), // faint red = parked on D
                AppState.Busy => Color.FromArgb(90, 250, 200, 60),     // amber = moving
                _ => Color.FromArgb(80, 130, 130, 130)                 // gray = missing
            })
        };
        iconGrid.Children.Add(overlay);

        // live % while moving (sits on top of the yellow overlay)
        _pctLabels.Remove(a.AnchorPath);
        if (state == AppState.Busy)
        {
            int p = _pct.TryGetValue(a.AnchorPath, out var v) ? v : 0;
            var pctText = new TextBlock
            {
                Text = p + "%",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 4,
                    ShadowDepth = 0,
                    Opacity = 0.9
                }
            };
            iconGrid.Children.Add(pctText);
            _pctLabels[a.AnchorPath] = pctText;
        }

        stack.Children.Add(iconGrid);

        // name
        stack.Children.Add(new TextBlock
        {
            Text = a.Name,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 86,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 2)
        });

        // state label
        stack.Children.Add(new TextBlock
        {
            Text = state switch
            {
                AppState.Loaded => "♨ thawed on C",
                AppState.Offloaded => "❄ frozen on D",
                AppState.Busy => "moving…",
                _ => "missing"
            },
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            Width = 86,
            FontSize = 9,
            Margin = new Thickness(0, 0, 0, 4)
        });

        // running indicator (only when it's live on C — that's when freezing is blocked)
        if (running)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "● running",
                Foreground = new SolidColorBrush(Color.FromArgb(230, 90, 230, 130)),
                TextAlignment = TextAlignment.Center,
                Width = 86,
                FontSize = 9,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        // mode badge
        stack.Children.Add(new TextBlock
        {
            Text = a.Mode == AppMode.Shuttle ? "shuttle ⚡" : "park",
            Foreground = new SolidColorBrush(a.Mode == AppMode.Shuttle
                ? Color.FromArgb(210, 250, 210, 90)
                : Color.FromArgb(140, 255, 255, 255)),
            TextAlignment = TextAlignment.Center,
            Width = 86,
            FontSize = 9,
            Margin = new Thickness(0, 0, 0, 4)
        });

        // move button
        // Estimate from last time: when loaded the next action is Freeze, when frozen it's Defrost.
        string moveLabel = state == AppState.Loaded
            ? "❄ Freeze" + Est(a.LastFreezeSeconds)
            : "♨ Defrost" + Est(a.LastDefrostSeconds);

        var moveBtn = new Button
        {
            Content = moveLabel,
            FontSize = 10,
            Padding = new Thickness(4, 1, 4, 1),
            IsEnabled = (state is AppState.Loaded or AppState.Offloaded) && !running
        };
        if (running) moveBtn.ToolTip = $"Close \"{a.Name}\" first — it's running, so its files can't be frozen.";
        moveBtn.Click += (_, _) => ToggleMove(a);
        stack.Children.Add(moveBtn);

        // right-click menu: mode toggle, launch, move, remove
        var menu = new ContextMenu();
        var shuttleItem = new MenuItem
        {
            Header = "Shuttle to C while in use",
            IsCheckable = true,
            IsChecked = a.Mode == AppMode.Shuttle
        };
        shuttleItem.Click += (_, _) => ToggleMode(a);
        menu.Items.Add(shuttleItem);
        menu.Items.Add(new Separator());
        var launchItem = new MenuItem { Header = "Launch" };
        launchItem.Click += (_, _) => Launch(a);
        menu.Items.Add(launchItem);
        var setExeItem = new MenuItem { Header = "Set launch .exe…" };
        setExeItem.Click += (_, _) => SetLaunchExe(a);
        menu.Items.Add(setExeItem);
        var moveItem = new MenuItem
        {
            Header = state == AppState.Loaded
                ? "❄ Freeze to D" + Est(a.LastFreezeSeconds)
                : "♨ Defrost to C" + Est(a.LastDefrostSeconds),
            IsEnabled = (state is AppState.Loaded or AppState.Offloaded) && !running
        };
        moveItem.Click += (_, _) => ToggleMove(a);
        menu.Items.Add(moveItem);
        menu.Items.Add(new Separator());
        var removeItem = new MenuItem { Header = "Remove from Stowbyte" };
        removeItem.Click += (_, _) => RemoveApp(a);
        menu.Items.Add(removeItem);
        card.ContextMenu = menu;

        // Hover clock: time for the action that applies to THIS state (freeze if on C, defrost if on D).
        double estSecs = state switch
        {
            AppState.Loaded => a.LastFreezeSeconds,
            AppState.Offloaded => a.LastDefrostSeconds,
            _ => 0
        };
        string clockLine = estSecs > 0
            ? $"\n🕐 {Dur(estSecs)} to {(state == AppState.Loaded ? "freeze to D" : "defrost to C")} (last time)"
            : "";

        card.Child = stack;
        ToolTipService.SetToolTip(card,
            $"{a.Name}\n{StateText(state)}{clockLine}\nMode: {(a.Mode == AppMode.Shuttle ? "Shuttle (auto-load on launch)" : "Park (manual move)")}\nClick icon to launch • right-click for options\n{a.ExePath}");
        return card;
    }

    /// <summary>Bare "~Xm" duration from a prior move; empty if never measured.</summary>
    private static string Dur(double seconds)
    {
        if (seconds <= 0) return "";
        if (seconds < 90) return $"~{Math.Max(1, (int)Math.Round(seconds))}s";
        int mins = (int)Math.Round(seconds / 60.0);
        if (mins < 60) return $"~{mins}m";
        int h = mins / 60, m = mins % 60;
        return m == 0 ? $"~{h}h" : $"~{h}h{m}m";
    }

    /// <summary>Subtle " · ~Xm" estimate suffix for buttons; empty if never measured.</summary>
    private static string Est(double seconds)
    {
        string d = Dur(seconds);
        return d == "" ? "" : " · " + d;
    }

    private static string StateText(AppState s) => s switch
    {
        AppState.Loaded => "On C (fast drive)",
        AppState.Offloaded => "Offloaded to D (runs via junction)",
        AppState.Busy => "Move in progress",
        _ => "Not found on C or D"
    };

    // ---------- actions ----------

    private void Launch(ManagedApp a)
    {
        try
        {
            // Shuttle apps parked on D: pull to C (yellow + live %), AUTO-START when ready (green),
            // then offload again when they close. Park/loaded apps launch straight away
            // (the junction makes the exe reachable even while parked on D).
            if (a.Mode == AppMode.Shuttle && Engine.GetState(a) == AppState.Offloaded)
            {
                RunMove(a, load: true, after: () =>
                {
                    var proc = StartProcess(a);
                    if (proc != null) OffloadWhenClosed(a, proc);
                });
                return;
            }

            StartProcess(a);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Stowbyte");
        }
    }

    private Process? StartProcess(ManagedApp a)
    {
        if (!File.Exists(a.ExePath))
        {
            System.Windows.MessageBox.Show("Can't find the exe:\n" + a.ExePath, "Stowbyte");
            return null;
        }
        var psi = new ProcessStartInfo(a.ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(a.ExePath) ?? a.AnchorPath
        };
        return Process.Start(psi);
    }

    private void OffloadWhenClosed(ManagedApp a, Process proc)
    {
        Task.Run(() =>
        {
            try { proc.WaitForExit(); } catch { }

            // The launcher may have handed off to a child (the real game) that's still running under
            // the app folder. Wait until nothing under the folder is running for a couple of
            // consecutive checks before offloading — never move a live app.
            int idle = 0;
            while (idle < 2)
            {
                System.Threading.Thread.Sleep(2500);
                if (Engine.IsInUse(a)) idle = 0;
                else idle++;
            }

            _popup?.Dispatcher.Invoke(() => RunMove(a, load: false));
        });
    }

    private void ToggleMove(ManagedApp a)
        => RunMove(a, load: Engine.GetState(a) != AppState.Loaded);

    private void ToggleMode(ManagedApp a)
    {
        a.Mode = a.Mode == AppMode.Shuttle ? AppMode.Park : AppMode.Shuttle;
        ConfigStore.Save(_cfg);
        Refresh();
    }

    private void RemoveApp(ManagedApp a) => WithDialog(() =>
    {
        if (System.Windows.MessageBox.Show(
                $"Stop managing \"{a.Name}\"?\n\nThis only removes it from Stowbyte — it does NOT move or delete any files. " +
                (Engine.GetState(a) == AppState.Offloaded ? "It is currently parked on D and will stay there." : ""),
                "Stowbyte", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            return;
        _cfg.Apps.Remove(a);
        ConfigStore.Save(_cfg);
        Refresh();
    });

    /// <summary>
    /// Runs a (slow) move off the UI thread with live % reporting. The card goes yellow with a
    /// running % overlay; on success it runs <paramref name="after"/> and refreshes (green).
    /// </summary>
    private void RunMove(ManagedApp a, bool load, Action? after = null)
    {
        if (!_busy.Add(a.AnchorPath)) return; // already moving
        _pct[a.AnchorPath] = 0;
        Refresh();

        var progress = new Progress<int>(p =>
        {
            _pct[a.AnchorPath] = p;
            if (_pctLabels.TryGetValue(a.AnchorPath, out var lbl)) lbl.Text = p + "%";
        });

        Task.Run(() =>
        {
            Exception? err = null;
            var sw = Stopwatch.StartNew();
            try
            {
                if (load) Engine.Load(a, progress);
                else Engine.Offload(a, progress);
            }
            catch (Exception ex) { err = ex; }
            sw.Stop();

            // Remember how long this move took so the button can show an estimate next time.
            if (err == null)
            {
                double secs = sw.Elapsed.TotalSeconds;
                if (load) a.LastDefrostSeconds = secs;
                else a.LastFreezeSeconds = secs;
                try { ConfigStore.Save(_cfg); } catch { }
            }

            _popup?.Dispatcher.Invoke(() =>
            {
                _busy.Remove(a.AnchorPath);
                _pct.Remove(a.AnchorPath);
                if (err != null) { System.Windows.MessageBox.Show(err.Message, "Stowbyte"); Refresh(); return; }
                after?.Invoke();
                Refresh();
            });
        });
    }

    // ---------- setup / settings ----------

    private void EnsureSetup()
    {
        if (_cfg.Settings.SetupComplete && !string.IsNullOrWhiteSpace(_cfg.Settings.OffloadRoot))
            return;

        bool saved = OpenSettings(firstRun: true);
        if (!saved)
        {
            // Skipped setup: fall back to a sensible default so the app still works.
            _cfg.Settings.OffloadRoot = DriveHelper.SuggestOffloadRoot();
        }
        _cfg.Settings.SetupComplete = true;
        ConfigStore.Save(_cfg);
    }

    private bool OpenSettings(bool firstRun) => WithDialog(() =>
    {
        var w = new SettingsWindow(_cfg.Settings.OffloadRoot, _cfg.Settings.SourceZone, firstRun);
        bool ok = w.ShowDialog() == true;
        if (ok)
        {
            _cfg.Settings.OffloadRoot = w.OffloadRoot;
            _cfg.Settings.SourceZone = w.SourceZone;
            _cfg.Settings.SetupComplete = true;
            ConfigStore.Save(_cfg);
            if (_popup != null && _popup.IsVisible) Refresh();
        }
        return ok;
    });

    private string OffloadRoot =>
        string.IsNullOrWhiteSpace(_cfg.Settings.OffloadRoot)
            ? DriveHelper.SuggestOffloadRoot()
            : _cfg.Settings.OffloadRoot;

    private void AddApp() => WithDialog(() =>
    {
        // Pick the app's FOLDER (that's what we tier). Start browsing from the install zone on C.
        string start = string.IsNullOrWhiteSpace(_cfg.Settings.SourceZone)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            : _cfg.Settings.SourceZone;

        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Pick the program's folder (the whole folder gets tiered to D)",
            UseDescriptionForTitle = true
        };
        try { if (Directory.Exists(start)) dlg.SelectedPath = start; } catch { }
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;

        string folder = dlg.SelectedPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        // Auto-detect the main exe; if we can't, let the user point at it.
        string? exe = FindMainExe(folder);
        if (exe == null)
        {
            var ofd = new WinForms.OpenFileDialog
            {
                Title = "Couldn't find the main program — pick its .exe",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                InitialDirectory = folder
            };
            if (ofd.ShowDialog() != WinForms.DialogResult.OK) return;
            exe = ofd.FileName;
        }

        string name = new DirectoryInfo(folder).Name;
        string slowRoot = OffloadRoot;

        var app = new ManagedApp
        {
            Name = name,
            AnchorPath = folder,
            ExePath = exe,
            SlowPath = Path.Combine(slowRoot, name),
            Mode = AppMode.Park
        };

        // avoid dupes by anchor
        _cfg.Apps.RemoveAll(x => string.Equals(x.AnchorPath, app.AnchorPath, StringComparison.OrdinalIgnoreCase));
        _cfg.Apps.Add(app);
        ConfigStore.Save(_cfg);
        Refresh();
    });

    /// <summary>
    /// Best-guess the launch exe inside a program folder: skip installer/updater/helper junk,
    /// prefer an exe whose name matches the folder, otherwise take the largest remaining exe.
    /// Returns null if nothing usable is found (caller can then ask the user to pick).
    /// </summary>
    private static string? FindMainExe(string folder)
    {
        string[] skip = { "unins", "setup", "install", "vcredist", "vc_redist", "dxsetup",
                          "crashpad", "crashhandler", "crashreport", "redist", "helper",
                          "update", "updater", "report", "notification", "service" };

        List<FileInfo> exes;
        try
        {
            exes = Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .Where(fi =>
                {
                    string n = fi.Name.ToLowerInvariant();
                    return !skip.Any(s => n.Contains(s));
                })
                .ToList();
        }
        catch { return null; }

        if (exes.Count == 0) return null;

        string folderName = new DirectoryInfo(folder).Name.ToLowerInvariant();

        // 1) exe sitting at the folder root whose name matches the folder
        var rootMatch = exes
            .Where(fi => string.Equals(Path.GetDirectoryName(fi.FullName), folder, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(fi => Path.GetFileNameWithoutExtension(fi.Name)
                .ToLowerInvariant().Contains(folderName)
                || folderName.Contains(Path.GetFileNameWithoutExtension(fi.Name).ToLowerInvariant()));
        if (rootMatch != null) return rootMatch.FullName;

        // 2) any exe whose name matches the folder
        var nameMatch = exes.FirstOrDefault(fi =>
            Path.GetFileNameWithoutExtension(fi.Name).ToLowerInvariant().Contains(folderName));
        if (nameMatch != null) return nameMatch.FullName;

        // 3) prefer exes at the folder root, then fall back to all, taking the largest
        var rootExes = exes
            .Where(fi => string.Equals(Path.GetDirectoryName(fi.FullName), folder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pool = rootExes.Count > 0 ? rootExes : exes;
        return pool.OrderByDescending(fi => fi.Length).First().FullName;
    }

    /// <summary>Lets the user override which exe a card launches/pulls its icon from.</summary>
    private void SetLaunchExe(ManagedApp a) => WithDialog(() =>
    {
        string start = Directory.Exists(a.AnchorPath) ? a.AnchorPath
            : (Directory.Exists(a.SlowPath) ? a.SlowPath : "");
        var ofd = new WinForms.OpenFileDialog
        {
            Title = $"Pick the .exe to launch for \"{a.Name}\"",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        try { if (Directory.Exists(start)) ofd.InitialDirectory = start; } catch { }
        if (ofd.ShowDialog() != WinForms.DialogResult.OK) return;

        a.ExePath = ofd.FileName;
        ConfigStore.Save(_cfg);
        Refresh();
    });

    // ---------- tray icon bitmap ----------

    private static Drawing.Icon MakeTrayIcon()
    {
        var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(40, 200, 110));
            g.FillEllipse(bg, 2, 2, 28, 28);
            using var f = new Drawing.Font("Segoe UI", 13, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var sf = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("S", f, Drawing.Brushes.White, new Drawing.RectangleF(2, 2, 28, 28), sf);
        }
        IntPtr h = bmp.GetHicon();
        using var tmp = Drawing.Icon.FromHandle(h);
        var icon = (Drawing.Icon)tmp.Clone();
        bmp.Dispose();
        return icon;
    }

    public void Dispose()
    {
        if (_ni != null)
        {
            _ni.Visible = false;
            _ni.Dispose();
        }
    }
}
