using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiteTerm.Core.Settings;

namespace LiteTerm.App;

public partial class TerminalAppearanceWindow : Window
{
    private static readonly TerminalAppearanceSettings[] Presets =
    [
        TerminalAppearanceSettings.Default,
        new("#F8FAFC", "#000000"),
        new("#D4D4D4", "#1E1E1E"),
        new("#1F2937", "#F8FAFC")
    ];

    private bool _updatingControls;

    public TerminalAppearanceWindow(TerminalAppearanceSettings currentSettings)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        Settings = currentSettings.Normalize();
        InitializeComponent();
        ForegroundTextBox.Text = Settings.ForegroundColor;
        BackgroundTextBox.Text = Settings.BackgroundColor;
        SelectMatchingPreset();
        UpdatePreview();
    }

    public TerminalAppearanceSettings Settings { get; private set; }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || ForegroundTextBox is null || PresetComboBox.SelectedIndex is < 0 or >= 4)
        {
            return;
        }

        _updatingControls = true;
        var preset = Presets[PresetComboBox.SelectedIndex];
        ForegroundTextBox.Text = preset.ForegroundColor;
        BackgroundTextBox.Text = preset.BackgroundColor;
        _updatingControls = false;
        UpdatePreview();
    }

    private void Color_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingControls || PresetComboBox is null)
        {
            return;
        }

        SelectMatchingPreset();
        UpdatePreview();
    }

    private void RestoreDefault_Click(object sender, RoutedEventArgs e)
    {
        _updatingControls = true;
        ForegroundTextBox.Text = TerminalAppearanceSettings.Default.ForegroundColor;
        BackgroundTextBox.Text = TerminalAppearanceSettings.Default.BackgroundColor;
        PresetComboBox.SelectedIndex = 0;
        _updatingControls = false;
        UpdatePreview();
    }

    private void ChooseForegroundColor_Click(object sender, RoutedEventArgs e)
    {
        ChooseColor(ForegroundTextBox);
    }

    private void ChooseBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        ChooseColor(BackgroundTextBox);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Settings = new TerminalAppearanceSettings(
                ForegroundTextBox.Text.Trim(),
                BackgroundTextBox.Text.Trim()).Normalize();
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "终端外观", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SelectMatchingPreset()
    {
        _updatingControls = true;
        var foreground = ForegroundTextBox.Text.Trim();
        var background = BackgroundTextBox.Text.Trim();
        var matchingIndex = Array.FindIndex(Presets, preset =>
            string.Equals(preset.ForegroundColor, foreground, StringComparison.OrdinalIgnoreCase)
            && string.Equals(preset.BackgroundColor, background, StringComparison.OrdinalIgnoreCase));
        PresetComboBox.SelectedIndex = matchingIndex >= 0 ? matchingIndex : 4;
        _updatingControls = false;
    }

    private void UpdatePreview()
    {
        if (!TryGetBrush(BackgroundTextBox.Text, out var background)
            || !TryGetBrush(ForegroundTextBox.Text, out var foreground))
        {
            return;
        }

        PreviewBorder.Background = background;
        PreviewTitle.Foreground = foreground;
        PreviewText.Foreground = foreground;
        ForegroundSwatch.Background = foreground;
        BackgroundSwatch.Background = background;
    }

    private void ChooseColor(System.Windows.Controls.TextBox targetTextBox)
    {
        System.Drawing.Color initialColor;
        try
        {
            initialColor = System.Drawing.ColorTranslator.FromHtml(targetTextBox.Text);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            var fallback = ReferenceEquals(targetTextBox, ForegroundTextBox)
                ? TerminalAppearanceSettings.Default.ForegroundColor
                : TerminalAppearanceSettings.Default.BackgroundColor;
            initialColor = System.Drawing.ColorTranslator.FromHtml(fallback);
        }

        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true,
            Color = initialColor
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        targetTextBox.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        targetTextBox.Focus();
        targetTextBox.CaretIndex = targetTextBox.Text.Length;
    }

    private static bool TryGetBrush(string value, out System.Windows.Media.Brush brush)
    {
        try
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or InvalidCastException)
        {
            brush = Brushes.Transparent;
            return false;
        }
    }
}
