using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TurzxDisplay.Modes;

public sealed partial class ClockCalendarMode : UserControl, IDisplayMode
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private DateTime _lastMonthBuilt = DateTime.MinValue;

    public string Key => "ClockCalendar";
    public string Title => "时钟 + 日历";
    public string IconGlyph => "\uE823";
    public FrameworkElement View => this;
    public bool PeriodicRefresh => true;
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);
    public event Action? ContentChanged { add { } remove { } }

    public ClockCalendarMode()
    {
        InitializeComponent();
        Update(DateTime.Now);
    }

    public void Tick(DateTime now) => Update(now);

    public void OnActivated() { }
    public void OnDeactivated() { }

    public void Update(DateTime now)
    {
        WeekdayText.Text = _culture.DateTimeFormat.DayNames[(int)now.DayOfWeek];
        TimeText.Text = now.ToString("HH:mm", _culture);
        SecondsText.Text = now.ToString("ss", _culture);
        DateText.Text = now.ToString(_culture.DateTimeFormat.LongDatePattern, _culture);
        PulseBar.Width = 30 + (now.Second / 60.0) * 110;

        if (now.Year != _lastMonthBuilt.Year || now.Month != _lastMonthBuilt.Month || _lastMonthBuilt == DateTime.MinValue)
        {
            _lastMonthBuilt = now;
            BuildCalendar(now);
        }
        else
        {
            MarkToday(now);
        }
    }

    private void BuildCalendar(DateTime now)
    {
        MonthTitle.Text = now.ToString("yyyy MMMM", _culture).ToUpper(_culture);
        TodayBadgeText.Text = now.Day.ToString(_culture);

        // Weekday header — first day per culture
        WeekdayHeader.Children.Clear();
        WeekdayHeader.ColumnDefinitions.Clear();
        int first = (int)_culture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            WeekdayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var day = (DayOfWeek)((first + i) % 7);
            var label = _culture.DateTimeFormat.AbbreviatedDayNames[(int)day];
            WeekdayHeader.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0xA9, 0xC2)),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
            });
            Grid.SetColumn((FrameworkElement)WeekdayHeader.Children[^1], i);
        }

        // Day grid — 6 rows to fit any month
        DaysGrid.Children.Clear();
        DaysGrid.ColumnDefinitions.Clear();
        DaysGrid.RowDefinitions.Clear();
        for (int c = 0; c < 7; c++) DaysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 6; r++) DaysGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var firstOfMonth = new DateTime(now.Year, now.Month, 1);
        int lead = ((int)firstOfMonth.DayOfWeek - first + 7) % 7;
        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        int slot = 0;

        for (int i = 0; i < lead; i++, slot++)
        {
            AddDay(null, slot);
        }
        for (int day = 1; day <= daysInMonth; day++, slot++)
        {
            AddDay(day, slot, isToday: day == now.Day);
        }
        while (slot < 42) AddDay(null, slot++);
    }

    private void AddDay(int? day, int slot, bool isToday = false)
    {
        var row = slot / 7;
        var col = slot % 7;

        FrameworkElement element;
        if (day is null)
        {
            // out-of-month blank: soft dot
            var dot = new Border
            {
                Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x9A, 0xA9, 0xC2)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            element = dot;
        }
        else if (isToday)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x7E, 0x96, 0xDA)),
                Width = 34, Height = 34,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = day.Value.ToString(_culture),
                    FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Segoe UI Variable Display"),
                },
            };
            element = badge;
        }
        else
        {
            element = new TextBlock
            {
                Text = day.Value.ToString(_culture),
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x5B, 0x6C, 0x8F)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display"),
            };
        }

        DaysGrid.Children.Add(element);
        Grid.SetRow(element, row);
        Grid.SetColumn(element, col);
    }

    private void MarkToday(DateTime now)
    {
        TodayBadgeText.Text = now.Day.ToString(_culture);
        // full rebuild only on month change; today highlight is rebuilt with the calendar
    }
}
