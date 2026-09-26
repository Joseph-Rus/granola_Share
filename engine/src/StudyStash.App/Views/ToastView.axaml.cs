using Avalonia;
using Avalonia.Controls;

namespace StudyStash.App.Views;

public partial class ToastView : UserControl
{
    public ToastView()
    {
        InitializeComponent();
        ActionButton.Click += (_, _) => Acted?.Invoke();
        CloseButton.Click += (_, _) => Dismissed?.Invoke();
        bool mac = Skin.Current == SkinKind.Mac;
        Card.CornerRadius = new CornerRadius(mac ? 14 : 8);
        Bind(Card, Border.BackgroundProperty, mac ? "GlassSolid" : "Acrylic");
        Bind(Card, Border.BoxShadowProperty, mac ? "GlassShadow" : "ShadowLg");
        if (!mac)
        {
            Bind(Card, Border.BorderBrushProperty, "FlyStroke");
            Card.BorderThickness = new Thickness(1);
            ActionButton.CornerRadius = new CornerRadius(4);
        }
    }

    static void Bind(Control c, AvaloniaProperty p, string key) => c.Bind(p, c.GetResourceObservable(key));

    public string Title
    {
        get => TitleText.Text ?? "";
        set => TitleText.Text = value;
    }

    public string Text
    {
        get => BodyText.Text ?? "";
        set => BodyText.Text = value;
    }

    public string? ActionLabel
    {
        get => ActionText.Text;
        set
        {
            ActionText.Text = value;
            ActionButton.IsVisible = !string.IsNullOrEmpty(value);
        }
    }

    public event Action? Acted;
    public event Action? Dismissed;
}
