using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TurzxDisplay.Controls;

/// <summary>
/// Soft-UI card with layered-XAML depth (template "SoftCardStyle" in App.xaml).
/// A plain ContentControl: Background/BorderBrush/CornerRadius/Padding/Content all work
/// identically from XAML and from code — no custom plumbing.
/// </summary>
public sealed class SoftCard : ContentControl
{
    public SoftCard()
    {
        if (Application.Current?.Resources.TryGetValue("SoftCardStyle", out var style) is true && style is Style s)
            Style = s;
    }
}
