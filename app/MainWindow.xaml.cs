using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AulaControl.Hid;
using AulaControl.Protocol;

namespace AulaControl;

public partial class MainWindow : Window
{
    private readonly AulaDevice _device = new();
    private AulaProtocol? _protocol;
    private byte _r = 255, _g = 0, _b = 0;
    private double _hue = 0, _sat = 1, _val = 1; // color picker state, kept in sync with _r/_g/_b
    private bool _draggingSatVal, _draggingHue;
    private bool _loaded;
    private bool _suppressColorSync;
    private readonly HashSet<KeyDef> _selectedActuationKeys = new();
    private readonly Dictionary<KeyDef, Border> _actuationKeyVisuals = new();
    private readonly HashSet<KeyDef> _selectedLightingKeys = new();
    private readonly Dictionary<KeyDef, TextBlock> _keyTravelLabels = new();
    private readonly Dictionary<KeyDef, (byte r, byte g, byte b)> _keyColors = new();
    private readonly Dictionary<KeyDef, Border> _lightingKeyVisuals = new();
    private readonly List<(KeyDef key1, KeyDef key2, AulaProtocol.SocdModel model)> _socdPairs = new();

    public MainWindow()
    {
        InitializeComponent();
        BuildKeyboardVisual();
        BuildLightingKeyboardVisual();
        EffectCombo.ItemsSource = Enum.GetValues<AulaProtocol.LightMode>();
        EffectCombo.SelectedItem = AulaProtocol.LightMode.Static;
        InitSocdUi();
        SyncColorUi();
        _loaded = true;
    }

    // ---- Navigation --------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageDevice == null) return; // fires during InitializeComponent
        PageDevice.Visibility = NavDevice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageLighting.Visibility = NavLighting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageActuation.Visibility = NavActuation.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSocd.Visibility = NavSocd.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageConsole.Visibility = NavConsole.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Connection ---------------------------------------------------------

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_device.IsConnected)
        {
            _device.Disconnect();
            _protocol = null;
            SetConnectionState(connected: false, "Disconnected");
            ConnectButton.Content = "Connect";
            DeviceNameText.Text = "No device";
            ModelText.Text = "Model: -";
            BuildDateText.Text = "Firmware date: -";
            PathText.Text = "HID path: -";
            return;
        }

        SetConnectionState(connected: false, "Connecting...");
        var result = _device.TryConnect();
        if (result != ConnectResult.Success)
        {
            SetConnectionState(connected: false, "Disconnected");
            ShowToast(_device.LastError ?? "Could not connect.", isError: true);
            return;
        }

        _protocol = new AulaProtocol(_device);
        ConnectButton.Content = "Disconnect";

        var info = _protocol.GetDeviceInfo();
        if (info != null)
        {
            ModelText.Text = $"Model: {info.Model}";
            BuildDateText.Text = $"Firmware date: {info.BuildDate}";
            var shortModel = info.Model.Split(',').FirstOrDefault() ?? "AULA";
            DeviceNameText.Text = shortModel;
        }
        PathText.Text = $"HID path: {_device.DevicePath}";
        SetConnectionState(connected: true, "Connected");

        LoadLightingFromKeyboard(_protocol);
        LoadSocdFromKeyboard(_protocol);
        await ReadAllKeyTravelsAsync();
    }

    /// <summary>Persistent connection indicator in the sidebar — never overwritten by action results.</summary>
    private void SetConnectionState(bool connected, string text)
    {
        ConnectionText.Text = text;
        StatusDot.Fill = connected
            ? (SolidColorBrush)FindResource("Success")
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    }

    /// <summary>Transient feedback for the last action (e.g. "Lighting applied."), separate from connection state.</summary>
    private void ShowToast(string text, bool isError = false)
    {
        ActionToastText.Text = text;
        ActionToastText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xff, 0x6b, 0x6b))
            : (Brush)FindResource("TextPrimary");
        ActionToast.Visibility = Visibility.Visible;
    }

    private void RequireProtocol(Action<AulaProtocol> action)
    {
        if (_protocol == null)
        {
            MessageBox.Show("Connect to the keyboard first.", "Not connected");
            return;
        }
        action(_protocol);
    }

    // ---- Shared key-visual multi-select (drag over keys, or Ctrl+click) ------

    /// <summary>
    /// Wires a key's Border to support both interaction styles requested:
    /// click-and-drag across multiple keys, and Ctrl+click to toggle individual
    /// keys without losing the existing selection. `selected` is the owning
    /// page's selection set; `onChanged` repaints one key's selection outline.
    /// </summary>
    private void AttachSelectionHandlers(KeyDef key, Border border, HashSet<KeyDef> selected, Action<KeyDef> onChanged)
    {
        border.MouseLeftButtonDown += (s, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!selected.Remove(key)) selected.Add(key);
            }
            else
            {
                var previous = selected.ToList();
                selected.Clear();
                selected.Add(key);
                foreach (var k in previous) onChanged(k);
            }
            onChanged(key);
        };
        border.MouseEnter += (s, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (selected.Add(key)) onChanged(key);
        };
    }

    // ---- Lighting page -------------------------------------------------------

    private void SetColor(byte r, byte g, byte b)
    {
        _r = r; _g = g; _b = b;
        SyncColorUi();
    }

    private void SyncColorUi(bool fromPicker = false)
    {
        _suppressColorSync = true;
        if (!fromPicker) (_hue, _sat, _val) = RgbToHsv(_r, _g, _b);
        RSlider.Value = _r; GSlider.Value = _g; BSlider.Value = _b;
        RBox.Text = _r.ToString(); GBox.Text = _g.ToString(); BBox.Text = _b.ToString();
        HexBox.Text = $"{_r:X2}{_g:X2}{_b:X2}";
        ColorPreview.Background = new SolidColorBrush(Color.FromRgb(_r, _g, _b));
        BrightnessValueText.Text = $"{(int)BrightnessSlider.Value} / 4";
        UpdateColorPickerVisuals();
        _suppressColorSync = false;
    }

    // ---- Color picker (saturation/value square + hue bar) --------------------

    private static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;
        double h;
        if (delta == 0) h = 0;
        else if (max == rf) h = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf) h = 60 * (((bf - rf) / delta) + 2);
        else h = 60 * (((rf - gf) / delta) + 4);
        if (h < 0) h += 360;
        double s = max == 0 ? 0 : delta / max;
        return (h, s, max);
    }

    private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        double rf, gf, bf;
        if (h < 60) { rf = c; gf = x; bf = 0; }
        else if (h < 120) { rf = x; gf = c; bf = 0; }
        else if (h < 180) { rf = 0; gf = c; bf = x; }
        else if (h < 240) { rf = 0; gf = x; bf = c; }
        else if (h < 300) { rf = x; gf = 0; bf = c; }
        else { rf = c; gf = 0; bf = x; }
        return ((byte)Math.Round((rf + m) * 255), (byte)Math.Round((gf + m) * 255), (byte)Math.Round((bf + m) * 255));
    }

    private void UpdateColorPickerVisuals()
    {
        if (SatValCanvas == null) return; // guard: SyncColorUi can fire before the visual tree exists

        var hueRgb = HsvToRgb(_hue, 1, 1);
        SatValHueLayer.Fill = new SolidColorBrush(Color.FromRgb(hueRgb.r, hueRgb.g, hueRgb.b));

        double w = SatValCanvas.Width, h = SatValCanvas.Height;
        Canvas.SetLeft(SatValCursor, _sat * w - SatValCursor.Width / 2);
        Canvas.SetTop(SatValCursor, (1 - _val) * h - SatValCursor.Height / 2);

        double hw = HueCanvas.Width;
        Canvas.SetLeft(HueCursor, (_hue / 360.0) * hw - HueCursor.Width / 2);
    }

    private void UpdateSatValFromPoint(Point p)
    {
        _sat = Math.Clamp(p.X / SatValCanvas.Width, 0, 1);
        _val = Math.Clamp(1 - p.Y / SatValCanvas.Height, 0, 1);
        (_r, _g, _b) = HsvToRgb(_hue, _sat, _val);
        SyncColorUi(fromPicker: true);
    }

    private void UpdateHueFromPoint(Point p)
    {
        _hue = Math.Clamp(p.X / HueCanvas.Width, 0, 1) * 360;
        (_r, _g, _b) = HsvToRgb(_hue, _sat, _val);
        SyncColorUi(fromPicker: true);
    }

    private void SatValCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSatVal = true;
        SatValCanvas.CaptureMouse();
        UpdateSatValFromPoint(e.GetPosition(SatValCanvas));
    }

    private void SatValCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingSatVal) return;
        UpdateSatValFromPoint(e.GetPosition(SatValCanvas));
    }

    private void SatValCanvas_MouseUp(object sender, MouseButtonEventArgs e) => StopSatValDrag();
    private void SatValCanvas_MouseLeave(object sender, MouseEventArgs e) => StopSatValDrag();
    private void StopSatValDrag()
    {
        _draggingSatVal = false;
        SatValCanvas.ReleaseMouseCapture();
    }

    private void HueCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        HueCanvas.CaptureMouse();
        UpdateHueFromPoint(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingHue) return;
        UpdateHueFromPoint(e.GetPosition(HueCanvas));
    }

    private void HueCanvas_MouseUp(object sender, MouseButtonEventArgs e) => StopHueDrag();
    private void HueCanvas_MouseLeave(object sender, MouseEventArgs e) => StopHueDrag();
    private void StopHueDrag()
    {
        _draggingHue = false;
        HueCanvas.ReleaseMouseCapture();
    }

    private void ColorSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _suppressColorSync) return;
        _r = (byte)RSlider.Value; _g = (byte)GSlider.Value; _b = (byte)BSlider.Value;
        SyncColorUi();
    }

    private void ColorBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || _suppressColorSync) return;
        if (byte.TryParse(RBox.Text, out byte r) && byte.TryParse(GBox.Text, out byte g) && byte.TryParse(BBox.Text, out byte b))
        {
            _r = r; _g = g; _b = b;
            SyncColorUi();
        }
    }

    private void HexBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loaded || _suppressColorSync) return;
        var hex = HexBox.Text.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            _r = r; _g = g; _b = b;
            SyncColorUi();
        }
    }

    private void PresetRed_Click(object sender, RoutedEventArgs e) => SetColor(255, 0, 0);
    private void PresetOrange_Click(object sender, RoutedEventArgs e) => SetColor(255, 128, 0);
    private void PresetYellow_Click(object sender, RoutedEventArgs e) => SetColor(255, 230, 0);
    private void PresetGreen_Click(object sender, RoutedEventArgs e) => SetColor(0, 255, 0);
    private void PresetCyan_Click(object sender, RoutedEventArgs e) => SetColor(0, 230, 255);
    private void PresetBlue_Click(object sender, RoutedEventArgs e) => SetColor(0, 0, 255);
    private void PresetPurple_Click(object sender, RoutedEventArgs e) => SetColor(160, 0, 255);
    private void PresetWhite_Click(object sender, RoutedEventArgs e) => SetColor(255, 255, 255);

    private void SpeedSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedValueText == null) return;
        SpeedValueText.Text = $"{(int)SpeedSlider.Value} / 4";
    }

    private void EffectCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        bool isStatic = (AulaProtocol.LightMode?)EffectCombo.SelectedItem == AulaProtocol.LightMode.Static;
        SpeedSlider.IsEnabled = !isStatic;
    }

    private void ApplyLighting_Click(object sender, RoutedEventArgs e)
    {
        RequireProtocol(p =>
        {
            var mode = (AulaProtocol.LightMode)(EffectCombo.SelectedItem ?? AulaProtocol.LightMode.Static);
            byte brightness = (byte)BrightnessSlider.Value;
            byte speed = (byte)SpeedSlider.Value;
            byte fullColor = (byte)(RainbowCheck.IsChecked == true ? 1 : 0);
            bool ok = p.SetGlobalLighting(mode, speed, brightness, _r, _g, _b, fullColor: fullColor);
            ShowToast(ok ? $"{mode} applied." : $"{mode} sent, but no ack from the keyboard — check it changed.", isError: !ok);
        });
    }

    /// <summary>
    /// Pulls the keyboard's currently active lighting effect/speed/brightness/
    /// color and reflects it in the UI, so connecting doesn't silently reset
    /// what's shown to Static red. Uses AulaProtocol.ReadGlobalLighting (see
    /// re/protocol.md).
    /// </summary>
    private void LoadLightingFromKeyboard(AulaProtocol p)
    {
        var state = p.ReadGlobalLighting();
        if (state == null) return;

        if (Enum.IsDefined(typeof(AulaProtocol.LightMode), state.Mode))
            EffectCombo.SelectedItem = state.Mode;

        BrightnessSlider.Value = state.Brightness;
        SpeedSlider.Value = state.Speed;
        RainbowCheck.IsChecked = state.FullColor == 1;

        _r = state.FgR; _g = state.FgG; _b = state.FgB;
        SyncColorUi();
    }

    private void RefreshLighting_Click(object sender, RoutedEventArgs e)
    {
        RequireProtocol(p =>
        {
            LoadLightingFromKeyboard(p);
            ShowToast("Lighting refreshed from keyboard.");
        });
    }

    private void BuildLightingKeyboardVisual()
    {
        LightingKeyboardVisual.Children.Clear();
        const double unit = 42;
        const double gap = 4;

        foreach (var row in KeyboardLayout.Win60Rows)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, gap) };
            foreach (var key in row)
            {
                var border = new Border
                {
                    Width = key.Width * unit - gap,
                    Height = unit - gap,
                    Margin = new Thickness(0, 0, gap, 0),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    Background = key.Index.HasValue ? (Brush)FindResource("KeycapBg") : new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1c)),
                    BorderBrush = (Brush)FindResource("KeycapBorder"),
                    Cursor = key.Index.HasValue ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
                };
                var label = new TextBlock
                {
                    Text = key.Label,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = key.Index.HasValue ? (Brush)FindResource("TextPrimary") : (Brush)FindResource("TextSecondary"),
                };
                border.Child = label;

                if (key.Index.HasValue)
                {
                    _lightingKeyVisuals[key] = border;
                    if (_keyColors.TryGetValue(key, out var painted))
                        border.Background = new SolidColorBrush(Color.FromRgb(painted.r, painted.g, painted.b));
                    AttachSelectionHandlers(key, border, _selectedLightingKeys, RefreshLightingKeySelectionVisual);
                    border.ToolTip = "Click to select. Drag across keys or Ctrl+click to select several, then Paint Selected.";
                }
                else
                {
                    border.ToolTip = "Not mapped yet — see re/protocol.md";
                }
                rowPanel.Children.Add(border);
            }
            LightingKeyboardVisual.Children.Add(rowPanel);
        }
    }

    private void RefreshLightingKeySelectionVisual(KeyDef key)
    {
        if (!_lightingKeyVisuals.TryGetValue(key, out var border)) return;
        bool isSelected = _selectedLightingKeys.Contains(key);
        border.BorderBrush = isSelected ? (Brush)FindResource("Accent") : (Brush)FindResource("KeycapBorder");
        border.BorderThickness = new Thickness(isSelected ? 2 : 1);
    }

    private void PaintSelectedKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLightingKeys.Count == 0)
        {
            ShowToast("Select at least one key first (click, drag, or Ctrl+click).", isError: true);
            return;
        }
        foreach (var key in _selectedLightingKeys)
        {
            _keyColors[key] = (_r, _g, _b);
            if (_lightingKeyVisuals.TryGetValue(key, out var border))
                border.Background = new SolidColorBrush(Color.FromRgb(_r, _g, _b));
        }
        ShowToast($"Painted {_selectedLightingKeys.Count} key(s) — click Apply Key Colors to send to the keyboard.");
    }

    private void ApplyKeyColors_Click(object sender, RoutedEventArgs e)
    {
        RequireProtocol(p =>
        {
            var colors = _keyColors
                .Where(kv => kv.Key.Index.HasValue)
                .ToDictionary(kv => kv.Key.Index!.Value, kv => kv.Value);

            if (colors.Count == 0)
            {
                ShowToast("Paint at least one key first.", isError: true);
                return;
            }

            p.SetGlobalLighting(AulaProtocol.LightMode.Custom, 4, 4, 0, 0, 0);
            bool ok = p.SetCustomKeyColors(colors);
            ShowToast(ok
                ? $"Applied colors to {colors.Count} key(s)."
                : "Sent, but one or more chunks had no ack — check the keyboard.", isError: !ok);
        });
    }

    private void ClearKeyColors_Click(object sender, RoutedEventArgs e)
    {
        _keyColors.Clear();
        foreach (var (key, visual) in _lightingKeyVisuals)
            visual.Background = (Brush)FindResource("KeycapBg");
        var previouslySelected = _selectedLightingKeys.ToList();
        _selectedLightingKeys.Clear();
        foreach (var key in previouslySelected) RefreshLightingKeySelectionVisual(key);

        RequireProtocol(p =>
        {
            bool ok = p.SetCustomKeyColors(new Dictionary<int, (byte, byte, byte)>());
            ShowToast(ok ? "Cleared all key colors." : "Clear sent, but no ack — check the keyboard.", isError: !ok);
        });
    }

    // ---- Actuation page -------------------------------------------------------

    private void BuildKeyboardVisual()
    {
        KeyboardVisual.Children.Clear();
        const double unit = 42;
        const double gap = 4;

        foreach (var row in KeyboardLayout.Win60Rows)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, gap) };
            foreach (var key in row)
            {
                var border = new Border
                {
                    Width = key.Width * unit - gap,
                    Height = unit - gap,
                    Margin = new Thickness(0, 0, gap, 0),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1),
                    Background = key.IsMapped ? (Brush)FindResource("KeycapBg") : new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1c)),
                    BorderBrush = key.IsMapped ? (Brush)FindResource("KeycapBorder") : new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x28)),
                    Cursor = key.IsMapped ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
                    Tag = key,
                };
                var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var label = new TextBlock
                {
                    Text = key.Label,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = key.IsMapped
                        ? (Brush)FindResource("TextPrimary")
                        : (Brush)FindResource("TextSecondary"),
                };
                content.Children.Add(label);

                if (key.IsMapped)
                {
                    var travelLabel = new TextBlock
                    {
                        Text = FormatTravel(key.LastKnownTriggerMm),
                        FontSize = 8,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Brush)FindResource("Accent"),
                    };
                    content.Children.Add(travelLabel);
                    _keyTravelLabels[key] = travelLabel;
                    _actuationKeyVisuals[key] = border;

                    AttachSelectionHandlers(key, border, _selectedActuationKeys, RefreshActuationKeySelectionVisual);
                    border.ToolTip = "Click to select. Drag across keys or Ctrl+click to select several. Number shown is the last value this app wrote — not a live hardware read (no read command confirmed yet for this key).";
                }
                else
                {
                    border.ToolTip = "Row/col not confirmed yet for this key — see re/protocol.md";
                }
                border.Child = content;
                rowPanel.Children.Add(border);
            }
            KeyboardVisual.Children.Add(rowPanel);
        }
    }

    private void RefreshActuationKeySelectionVisual(KeyDef key)
    {
        if (_actuationKeyVisuals.TryGetValue(key, out var border))
        {
            bool isSelected = _selectedActuationKeys.Contains(key);
            border.BorderBrush = isSelected ? (Brush)FindResource("Accent") : (Brush)FindResource("KeycapBorder");
            border.BorderThickness = new Thickness(isSelected ? 2 : 1);
        }

        int count = _selectedActuationKeys.Count;
        SelectedKeyText.Text = count switch
        {
            0 => "No key selected",
            1 => $"Selected: {_selectedActuationKeys.First().Label}  (row={_selectedActuationKeys.First().Row}, col={_selectedActuationKeys.First().Col})",
            _ => $"{count} keys selected",
        };
        ApplyActuationButton.IsEnabled = count > 0;

        if (count == 1 && _selectedActuationKeys.First().LastKnownTriggerMm is double mm)
            TriggerSlider.Value = mm;
    }

    private void ActuationSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        TriggerValueText.Text = $"{TriggerSlider.Value:0.00}mm";
        ReleaseValueText.Text = $"{ReleaseSlider.Value:0.00}mm";
    }

    private static string FormatTravel(double? mm) => mm.HasValue ? $"{mm.Value:0.00}" : "—";

    private void RefreshKeyTravelLabel(KeyDef key)
    {
        if (_keyTravelLabels.TryGetValue(key, out var label))
            label.Text = FormatTravel(key.LastKnownTriggerMm);
    }

    private static bool WriteActuation(AulaProtocol p, KeyDef key, double triggerMm, double releaseMm) =>
        p.SetKeyActuationBitmask(key.Col!.Value, key.Row!.Value, triggerMm);

    /// <summary>Reads every mapped key's live actuation travel from hardware and updates the keycap labels.</summary>
    private async Task ReadAllKeyTravelsAsync()
    {
        if (_protocol == null) return;
        var protocol = _protocol;
        var mappedKeys = KeyboardLayout.Win60Rows.SelectMany(r => r).Where(k => k.IsMapped).ToList();

        var results = await Task.Run(() =>
        {
            var values = new List<(KeyDef key, double? mm)>();
            foreach (var key in mappedKeys)
                values.Add((key, protocol.ReadKeyActuationMm(key.Row!.Value, key.Col!.Value)));
            return values;
        });

        foreach (var (key, mm) in results)
        {
            key.LastKnownTriggerMm = mm;
            RefreshKeyTravelLabel(key);
        }
        ShowToast($"Read live actuation for {results.Count(r => r.mm.HasValue)}/{mappedKeys.Count} keys.");
    }

    private async void RefreshTravels_Click(object sender, RoutedEventArgs e)
    {
        if (_protocol == null)
        {
            MessageBox.Show("Connect to the keyboard first.", "Not connected");
            return;
        }
        await ReadAllKeyTravelsAsync();
    }

    private void ApplyActuation_Click(object sender, RoutedEventArgs e)
    {
        var targets = _selectedActuationKeys.Where(k => k.IsMapped).ToList();
        if (targets.Count == 0) return;
        RequireProtocol(p =>
        {
            int okCount = 0;
            foreach (var key in targets)
            {
                if (WriteActuation(p, key, TriggerSlider.Value, ReleaseSlider.Value))
                {
                    okCount++;
                    key.LastKnownTriggerMm = TriggerSlider.Value;
                    RefreshKeyTravelLabel(key);
                }
            }
            ShowToast(okCount == targets.Count
                ? $"Actuation applied to {okCount} key(s)."
                : $"Applied to {okCount}/{targets.Count} selected key(s) — check the keyboard.", isError: okCount < targets.Count);
        });
    }

    // ---- Raw console page -------------------------------------------------------

    private void ApplyActuationAll_Click(object sender, RoutedEventArgs e)
    {
        RequireProtocol(p =>
        {
            var mappedKeys = KeyboardLayout.Win60Rows.SelectMany(r => r).Where(k => k.IsMapped).ToList();
            int okCount = 0;
            foreach (var key in mappedKeys)
            {
                if (WriteActuation(p, key, TriggerSlider.Value, ReleaseSlider.Value))
                {
                    okCount++;
                    key.LastKnownTriggerMm = TriggerSlider.Value;
                    RefreshKeyTravelLabel(key);
                }
            }
            ShowToast($"Applied to {okCount}/{mappedKeys.Count} mapped keys.", isError: okCount < mappedKeys.Count);
        });
    }

    private void SendRaw_Click(object sender, RoutedEventArgs e)
    {
        RequireProtocol(p =>
        {
            var tokens = RawInputBox.Text.Trim().Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            byte[] payload;
            try
            {
                payload = tokens.Select(t => Convert.ToByte(t, 16)).ToArray();
            }
            catch
            {
                RawOutputBox.Text = "Invalid hex input.";
                return;
            }

            var resp = _device.SendRaw(payload);
            RawOutputBox.Text = resp == null
                ? "(no response / timeout)"
                : string.Join(" ", resp.Select(b => b.ToString("x2")));
        });
    }

    // ---- SOCD page -------------------------------------------------------

    private static readonly Dictionary<AulaProtocol.SocdModel, string> SocdModelDescriptions = new()
    {
        [AulaProtocol.SocdModel.FirstInterrupted] = "The key that triggered first is interrupted.",
        [AulaProtocol.SocdModel.Key1InterruptsKey2] = "Key1 interrupts the triggering of Key2.",
        [AulaProtocol.SocdModel.Key2InterruptsKey1] = "Key2 interrupts the triggering of Key1.",
        [AulaProtocol.SocdModel.LaterIgnored] = "Later-triggered key is not executed; only the first triggered key can interrupt.",
    };

    private List<KeyDef> _socdCapableKeys = new();

    private void InitSocdUi()
    {
        _socdCapableKeys = KeyboardLayout.Win60Rows.SelectMany(r => r).Where(k => k.HidCode.HasValue)
            .OrderBy(k => k.Label, StringComparer.OrdinalIgnoreCase).ToList();
        SocdKey1Combo.ItemsSource = _socdCapableKeys;
        SocdKey2Combo.ItemsSource = _socdCapableKeys;

        SocdModelCombo.ItemsSource = Enum.GetValues<AulaProtocol.SocdModel>();
        SocdModelCombo.SelectedItem = AulaProtocol.SocdModel.FirstInterrupted;
        SocdModelCombo.SelectionChanged += (s, e) =>
        {
            var model = (AulaProtocol.SocdModel)(SocdModelCombo.SelectedItem ?? AulaProtocol.SocdModel.FirstInterrupted);
            SocdModelDescText.Text = SocdModelDescriptions[model];
        };
        SocdModelDescText.Text = SocdModelDescriptions[AulaProtocol.SocdModel.FirstInterrupted];
    }

    /// <summary>
    /// Pulls the SOCD pairs and global switch state that are already stored
    /// on the keyboard and populates the UI with them, so existing config
    /// isn't hidden/overwritten just by connecting. Uses AulaProtocol's
    /// ReadSocdPairs/ReadSocdEnabled (see re/protocol.md).
    /// </summary>
    private void LoadSocdFromKeyboard(AulaProtocol p)
    {
        var enabled = p.ReadSocdEnabled();
        if (enabled.HasValue) SocdEnabledCheck.IsChecked = enabled.Value;

        _socdPairs.Clear();
        foreach (var raw in p.ReadSocdPairs())
        {
            var key1 = _socdCapableKeys.FirstOrDefault(k => k.HidCode == raw.Key1HidCode);
            var key2 = _socdCapableKeys.FirstOrDefault(k => k.HidCode == raw.Key2HidCode);
            if (key1 == null || key2 == null) continue;
            _socdPairs.Add((key1, key2, raw.Model));
        }
        RefreshSocdPairsList();
    }

    private void RefreshSocdPairsList()
    {
        SocdPairsList.ItemsSource = null;
        SocdPairsList.ItemsSource = _socdPairs
            .Select(p => $"{p.key1.Label}  ↔  {p.key2.Label}   ({p.model})")
            .ToList();
    }

    private void AddSocdPair_Click(object sender, RoutedEventArgs e)
    {
        var key1 = SocdKey1Combo.SelectedItem as KeyDef;
        var key2 = SocdKey2Combo.SelectedItem as KeyDef;
        var model = (AulaProtocol.SocdModel)(SocdModelCombo.SelectedItem ?? AulaProtocol.SocdModel.FirstInterrupted);

        if (key1 == null || key2 == null)
        {
            ShowToast("Pick both Key1 and Key2.", isError: true);
            return;
        }
        if (key1 == key2)
        {
            ShowToast("Key1 and Key2 must be different keys.", isError: true);
            return;
        }
        if (_socdPairs.Count >= 20)
        {
            ShowToast("Max 20 SOCD pairs.", isError: true);
            return;
        }

        _socdPairs.Add((key1, key2, model));
        RefreshSocdPairsList();
    }

    private void RemoveSocdPair_Click(object sender, RoutedEventArgs e)
    {
        int i = SocdPairsList.SelectedIndex;
        if (i < 0 || i >= _socdPairs.Count) return;
        _socdPairs.RemoveAt(i);
        RefreshSocdPairsList();
    }

    private void ApplySocdPairs_Click(object sender, RoutedEventArgs e)
    {
        RequireProtocol(p =>
        {
            var pairs = _socdPairs
                .Select(sp => (sp.key1.HidCode!.Value, sp.key2.HidCode!.Value, sp.model))
                .ToList();
            bool ok = p.SetSocdPairs(pairs);
            ShowToast(ok ? $"Applied {pairs.Count} SOCD pair(s)." : "Sent, but no ack on at least one packet.", isError: !ok);
        });
    }

    private void RefreshSocd_Click(object sender, RoutedEventArgs e)
    {
        RequireProtocol(p =>
        {
            LoadSocdFromKeyboard(p);
            ShowToast("SOCD refreshed from keyboard.");
        });
    }

    private void ApplySocdSwitch_Click(object sender, RoutedEventArgs e)
    {
        RequireProtocol(p =>
        {
            bool enabled = SocdEnabledCheck.IsChecked == true;
            bool ok = p.SetSocdEnabled(enabled);
            ShowToast(ok ? $"SOCD {(enabled ? "enabled" : "disabled")}." : "Sent, but no ack.", isError: !ok);
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _device.Dispose();
        base.OnClosed(e);
    }
}
