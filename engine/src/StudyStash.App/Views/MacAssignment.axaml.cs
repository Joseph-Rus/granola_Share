using Avalonia.Controls;
using Avalonia.Media;

namespace StudyStash.App.Views;

public partial class MacAssignment : UserControl
{
    public MacAssignment()
    {
        InitializeComponent();
        Fades.Under(BottomFade, "Win", 1);
        // The top fade is the mirror of Fades.Under's gradient (solid at the top, fading out by 30% down), which
        // that shared helper doesn't offer, so it's built the same way, here.
        TopFade.Bind(Border.BackgroundProperty, TopFade.GetResourceObservable("Win", v => v is ISolidColorBrush b ? TopGradient(b.Color) : null));
    }

    static LinearGradientBrush TopGradient(Color c) => new()
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = { new GradientStop(c, 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.3) },
    };
}
