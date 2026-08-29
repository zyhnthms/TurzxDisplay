using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using TurzxDisplay.Services;

namespace TurzxDisplay.Modes;

/// <summary>
/// Sticky-notes canvas (800x480): up to six pastel cards in a 3x2 grid.
/// Content comes from the shared notes store; edits in the app raise
/// <see cref="ContentChanged"/> so the frame gets re-pushed to the device.
/// </summary>
public sealed partial class StickyNotesMode : UserControl, IDisplayMode
{
    private readonly SettingsService _settings;

    public string Key => "StickyNotes";
    public string Title => "便签";
    public string IconGlyph => "\uE70B";
    public FrameworkElement View => this;
    public bool PeriodicRefresh => false;
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(5);
    public event Action? ContentChanged;

    public StickyNotesMode(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        Rebuild();
        _settings.NotesChanged += () => DispatcherQueue.TryEnqueue(Rebuild);
    }

    public void Tick(DateTime now) { }
    public void OnActivated() => Rebuild();
    public void OnDeactivated() { }

    private void Rebuild()
    {
        NotesGrid.Children.Clear();
        var notes = _settings.Notes.ToList();
        CountText.Text = notes.Count switch
        {
            0 => "暂无便签",
            1 => "1 张便签",
            _ => $"{notes.Count} 张便签",
        };

        for (int i = 0; i < Math.Min(6, notes.Count); i++)
        {
            var note = notes[i];
            var card = new Controls.SoftCard
            {
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Parse(note.ColorHex)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(7),
                Padding = new Thickness(18, 14, 18, 14),
            };

            var text = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(note.Text) ? "…" : note.Text,
                FontSize = note.Text.Length > 90 ? 17 : note.Text.Length > 45 ? 20 : 24,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x47, 0x47, 0x50)),
                TextWrapping = TextWrapping.WrapWholeWords,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            // tape strip at the top for a soft "stuck on" look
            var panel = new Grid();
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var tape = new Border
            {
                CornerRadius = new CornerRadius(4),
                Width = 54, Height = 12,
                Background = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            panel.Children.Add(tape);
            panel.Children.Add(text);
            Grid.SetRow(text, 2);
            card.Content = panel;

            NotesGrid.Children.Add(card);
            Grid.SetRow(card, i / 3);
            Grid.SetColumn(card, i % 3);
        }

        ContentChanged?.Invoke();
    }

    private static Color Parse(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb(255,
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16));
        }
        catch { return Color.FromArgb(255, 0xFD, 0xF3, 0xBE); }
    }
}
