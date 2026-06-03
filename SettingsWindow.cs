using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
// Resolve names that exist in both WPF and WinForms -> use the WPF ones.
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Loadout;

/// <summary>
/// First-run setup / settings. Defines the OFFLOAD location (on D, where parked apps go) and the
/// optional INSTALL ZONE (on C, the "from" folder Add App starts browsing from).
/// Returns DialogResult == true when saved.
/// </summary>
public class SettingsWindow : Window
{
    private readonly TextBox _offloadBox;
    private readonly TextBox _zoneBox;
    private readonly TextBlock _freeInfo;
    private readonly TextBlock _warn;
    private readonly CheckBox _startupCheck;

    public string OffloadRoot => _offloadBox.Text.Trim();
    public string SourceZone => _zoneBox.Text.Trim();

    public SettingsWindow(string currentOffload, string currentZone, bool firstRun)
    {
        Title = firstRun ? "Stowbyte — first-time setup" : "Stowbyte — settings";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(28, 30, 36));
        ShowInTaskbar = true;

        var root = new StackPanel { Margin = new Thickness(22) };

        root.Children.Add(Heading(firstRun ? "Welcome to Stowbyte" : "Settings"));
        root.Children.Add(Intro(
            "Set where parked apps get offloaded to, and (optionally) the folder on C you usually "
            + "install programs into so Add App starts there."));

        // ---- Section 1: offload location (D) ----
        root.Children.Add(Label("Offload location  (where parked apps go — pick a roomy drive)"));
        _offloadBox = PathBox(string.IsNullOrWhiteSpace(currentOffload)
            ? DriveHelper.SuggestOffloadRoot() : currentOffload);
        _offloadBox.TextChanged += (_, _) => UpdateInfo();
        root.Children.Add(PathRow(_offloadBox, "Browse…", BrowseOffload));

        _freeInfo = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(170, 120, 230, 160)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 2)
        };
        root.Children.Add(_freeInfo);

        _warn = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(220, 245, 170, 70)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };
        root.Children.Add(_warn);

        // ---- Section 2: install zone (C) ----
        root.Children.Add(Label("Install zone on C  (optional — where you add programs from)"));
        _zoneBox = PathBox(string.IsNullOrWhiteSpace(currentZone)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : currentZone);
        root.Children.Add(PathRow(_zoneBox, "Browse…", BrowseZone));
        root.Children.Add(new TextBlock
        {
            Text = "Add App opens here by default. Leave it on Program Files, or point it at your own apps folder.",
            Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 20)
        });

        // ---- start on boot ----
        _startupCheck = new CheckBox
        {
            Content = "Start Stowbyte when Windows starts",
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 20),
            IsChecked = StartupHelper.IsEnabled()
        };
        root.Children.Add(_startupCheck);

        // ---- privacy / license / support ----
        root.Children.Add(InfoCard());

        // ---- buttons ----
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = firstRun ? "Get started" : "Save", Padding = new Thickness(16, 5, 16, 5), IsDefault = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        save.Click += (_, _) => OnSave();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        Content = root;
        UpdateInfo();
    }

    // ---- small UI builders ----

    private static TextBlock Heading(string t) => new()
    {
        Text = t,
        Foreground = Brushes.White,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static TextBlock Intro(string t) => new()
    {
        Text = t,
        Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 18)
    };

    private static TextBlock Label(string t) => new()
    {
        Text = t,
        Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
        FontSize = 12,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static TextBox PathBox(string text) => new()
    {
        Text = text,
        Padding = new Thickness(6, 5, 6, 5),
        FontSize = 13
    };

    private static DockPanel PathRow(TextBox box, string browseText, Action onBrowse)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        var browse = new Button { Content = browseText, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => onBrowse();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(box);
        return row;
    }

    // Privacy note + plain-language license + tip jar, in one quiet card.
    private static Border InfoCard()
    {
        var inner = new StackPanel();

        // privacy
        inner.Children.Add(SmallHead("Privacy"));
        inner.Children.Add(SmallBody("Stowbyte doesn’t collect any of your data. Nothing ever leaves your PC."));

        // license
        inner.Children.Add(SmallHead("License"));
        var licenseBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 12),
            Child = new TextBlock
            {
                Text = "This is my code — but you may share it freely, and I don’t mind if you modify it. "
                     + "If you get rich with it, toss me a bit please :)",
                Foreground = new SolidColorBrush(Color.FromArgb(205, 255, 255, 255)),
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap
            }
        };
        inner.Children.Add(licenseBox);

        // support / tip jar
        inner.Children.Add(SmallBody("If you like the software, consider buying me a coffee:"));
        inner.Children.Add(new TextBox
        {
            Text = "CashApp:  $minidraco711",
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 120, 230, 160)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.IBeam
        });

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 18),
            Child = inner
        };
    }

    private static TextBlock SmallHead(string t) => new()
    {
        Text = t,
        Foreground = Brushes.White,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 3)
    };

    private static TextBlock SmallBody(string t) => new()
    {
        Text = t,
        Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10)
    };

    // ---- browse handlers ----

    private void BrowseOffload()
    {
        string? picked = PickFolder("Choose where parked apps are offloaded to", _offloadBox.Text);
        if (picked != null) _offloadBox.Text = Path.Combine(picked, "Stowbyte");
    }

    private void BrowseZone()
    {
        string? picked = PickFolder("Choose your install zone on C", _zoneBox.Text);
        if (picked != null) _zoneBox.Text = picked;
    }

    private static string? PickFolder(string caption, string start)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = caption,
            UseDescriptionForTitle = true
        };
        try { if (Directory.Exists(start)) dlg.SelectedPath = start; } catch { }
        return dlg.ShowDialog() == WinForms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    private void UpdateInfo()
    {
        _freeInfo.Text = DriveHelper.FreeSpaceText(_offloadBox.Text);

        string warn = "";
        try
        {
            string? r = Path.GetPathRoot(Path.GetFullPath(_offloadBox.Text));
            if (string.Equals(r, "C:\\", StringComparison.OrdinalIgnoreCase))
                warn = "Heads up: this offload location is on your system drive (C:). Offloading here won't free space on C.";
        }
        catch { warn = "That doesn't look like a valid folder path."; }
        _warn.Text = warn;
    }

    private void OnSave()
    {
        string offload = OffloadRoot;
        if (string.IsNullOrWhiteSpace(offload))
        {
            System.Windows.MessageBox.Show("Please choose an offload location.", "Stowbyte");
            return;
        }
        try { Directory.CreateDirectory(offload); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Can't use that offload folder:\n" + ex.Message, "Stowbyte");
            return;
        }

        // Install zone is optional; if set, make sure it's usable.
        string zone = SourceZone;
        if (!string.IsNullOrWhiteSpace(zone))
        {
            try { Directory.CreateDirectory(zone); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Can't use that install zone:\n" + ex.Message, "Stowbyte");
                return;
            }
        }

        // Apply start-on-boot (Scheduled Task). Non-fatal if it fails.
        try { StartupHelper.SetEnabled(_startupCheck.IsChecked == true); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Saved, but couldn't update start-on-boot:\n" + ex.Message, "Stowbyte");
        }

        DialogResult = true;
        Close();
    }
}
