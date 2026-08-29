using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace TurzxDisplay.Controls;

/// <summary>
/// Soft-UI (neumorphic) drop shadow attached to any Border-like element:
///   Border controls:SoftShadow.Enabled="True" BlurRadius="24" OffsetY="10" Tint="#637494"
/// The shadow is a real Composition DropShadow masked to the element's rounded shape.
/// </summary>
public sealed class SoftShadow : DependencyObject
{
    // ---------------- mask farm ----------------
    // GetAlphaMask must be called on a shape that is attached & laid out in the visual
    // tree (calling it on detached shapes caused native AVs). The farm is a 1x1 clipped
    // canvas hosting the mask shapes; shapes are cached per rounded size.
    private static Microsoft.UI.Xaml.Controls.Canvas? _farm;
    private static readonly System.Collections.Generic.Dictionary<string, CompositionBrush> _maskCache = new();

    public static void InstallFarm(Microsoft.UI.Xaml.Controls.Panel host)
    {
        if (_farm is not null) return;
        _farm = new Microsoft.UI.Xaml.Controls.Canvas
        {
            Width = 1,
            Height = 1,
            IsHitTestVisible = false,
            Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 1, 1) },
        };
        host.Children.Add(_farm);
    }

    private static CompositionBrush? GetMask(Compositor compositor, double w, double h, double radius)
    {
        if (_farm is null) return null;
        int iw = (int)Math.Round(w), ih = (int)Math.Round(h), ir = (int)Math.Round(radius);
        if (iw < 1 || ih < 1) return null;
        var key = $"{iw}x{ih}r{ir}";
        if (_maskCache.TryGetValue(key, out var cached)) return cached;

        var shape = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = iw,
            Height = ih,
            RadiusX = ir,
            RadiusY = ir,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(shape, 1000 + (_maskCache.Count % 4) * 64);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(shape, 1000);
        _farm.Children.Add(shape);
        _farm.UpdateLayout();

        CompositionBrush? mask = null;
        try { mask = shape.GetAlphaMask(); }
        catch { /* rectangular fallback */ }
        if (mask is not null) _maskCache[key] = mask;
        return mask;
    }

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SoftShadow), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty BlurRadiusProperty =
        DependencyProperty.RegisterAttached("BlurRadius", typeof(double), typeof(SoftShadow), new PropertyMetadata(22.0, OnChanged));

    public static readonly DependencyProperty OffsetYProperty =
        DependencyProperty.RegisterAttached("OffsetY", typeof(double), typeof(SoftShadow), new PropertyMetadata(9.0, OnChanged));

    public static readonly DependencyProperty OffsetXProperty =
        DependencyProperty.RegisterAttached("OffsetX", typeof(double), typeof(SoftShadow), new PropertyMetadata(3.0, OnChanged));

    public static readonly DependencyProperty OpacityProperty =
        DependencyProperty.RegisterAttached("Opacity", typeof(double), typeof(SoftShadow), new PropertyMetadata(0.45, OnChanged));

    public static readonly DependencyProperty TintProperty =
        DependencyProperty.RegisterAttached("Tint", typeof(string), typeof(SoftShadow), new PropertyMetadata("#5B6C8F", OnChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static double GetBlurRadius(DependencyObject o) => (double)o.GetValue(BlurRadiusProperty);
    public static void SetBlurRadius(DependencyObject o, double v) => o.SetValue(BlurRadiusProperty, v);
    public static double GetOffsetY(DependencyObject o) => (double)o.GetValue(OffsetYProperty);
    public static void SetOffsetY(DependencyObject o, double v) => o.SetValue(OffsetYProperty, v);
    public static double GetOffsetX(DependencyObject o) => (double)o.GetValue(OffsetXProperty);
    public static void SetOffsetX(DependencyObject o, double v) => o.SetValue(OffsetXProperty, v);
    public static double GetOpacity(DependencyObject o) => (double)o.GetValue(OpacityProperty);
    public static void SetOpacity(DependencyObject o, double v) => o.SetValue(OpacityProperty, v);
    public static string GetTint(DependencyObject o) => (string)o.GetValue(TintProperty);
    public static void SetTint(DependencyObject o, string v) => o.SetValue(TintProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= Attach;
        element.Loaded += Attach;
        element.Unloaded -= Detach;
        element.Unloaded += Detach;
        if (element.IsLoaded) Attach(element, null!);
    }

    private static void Attach(object sender, RoutedEventArgs _)
    {
        // Shadows are disabled by design: SetElementChildVisual draws the shadow sprite ON TOP
        // of the element, washing the whole canvas darker. The original look the user approved
        // was effectively shadow-free (flat cards + light border), so Attached stays a no-op.
        // To bring depth back later, use layered XAML borders behind the card instead.
        return;
#pragma warning disable CS0162 // unreachable code kept for the future implementation
        var element = (FrameworkElement)sender;
        if (!GetEnabled(element)) return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = (float)GetBlurRadius(element);
        shadow.Offset = new System.Numerics.Vector3(
            (float)GetOffsetX(element), (float)GetOffsetY(element), 0);
        shadow.Opacity = (float)GetOpacity(element);
        shadow.Color = ParseColor(GetTint(element));

        var host = compositor.CreateSpriteVisual();
        host.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(element, host);

        void Update()
        {
            try
            {
                host.Size = new System.Numerics.Vector2(
                    MathF.Max(0, (float)element.ActualWidth), MathF.Max(0, (float)element.ActualHeight));
                if (element.ActualWidth < 1 || element.ActualHeight < 1) return;

                double radius = element is Border b ? b.CornerRadius.TopLeft : 0;
                shadow.Mask = GetMask(compositor, element.ActualWidth, element.ActualHeight, radius);
            }
            catch { /* decorative only */ }
        }

        element.SizeChanged -= (_, _) => Update();
        element.SizeChanged += (_, _) => Update();
        try { Update(); }
        catch { /* shadow is decorative — never take the app down */ }
    }
#pragma warning restore CS0162

    private static void Detach(object sender, RoutedEventArgs _)
    {
        var element = (FrameworkElement)sender;
        ElementCompositionPreview.SetElementChildVisual(element, null);
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            hex = hex.TrimStart('#');
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return Color.FromArgb(255, r, g, b);
        }
        catch { return Color.FromArgb(255, 91, 108, 143); }
    }
}
