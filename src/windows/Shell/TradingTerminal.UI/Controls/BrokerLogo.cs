using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Renders a broker's identifying mark, or no image so the caller's monogram remains visible.
///
/// <para>The mark is <c>{catalogue id}.png</c>, looked up through <see cref="BrokerCatalog"/> rather than a
/// table of its own. A hand-kept table here listed twelve brokers and fell behind the catalogue — Deribit,
/// Hyperliquid, Tradier and OANDA had marks on disk that were never shown. A broker without a mark
/// resolves to no image, and the monogram shows through.</para>
/// </summary>
public sealed class BrokerLogo : Image
{
    private static readonly Dictionary<BrokerKind, ImageSource?> Cache = [];

    public BrokerLogo()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        // The property's default is InteractiveBrokers, and setting a property to its default is not a
        // change — so for the IB row the change callback never ran and its mark was never shown.
        // Resolving the default here covers it.
        Source = Resolve(Broker);
    }

    public static readonly DependencyProperty BrokerProperty = DependencyProperty.Register(
        nameof(Broker), typeof(BrokerKind), typeof(BrokerLogo),
        new PropertyMetadata(BrokerKind.InteractiveBrokers, OnBrokerChanged));

    public BrokerKind Broker
    {
        get => (BrokerKind)GetValue(BrokerProperty);
        set => SetValue(BrokerProperty, value);
    }

    private static void OnBrokerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BrokerLogo)d).Source = Resolve((BrokerKind)e.NewValue);

    private static ImageSource? Resolve(BrokerKind broker)
    {
        if (Cache.TryGetValue(broker, out var cached)) return cached;
        if (BrokerCatalog.For(broker)?.Id is not { } id) return Cache[broker] = null;
        var asset = id + ".png";

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(
                $"pack://application:,,,/TradingTerminal.UI;component/Assets/Brokers/{asset}",
                UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return Cache[broker] = image;
        }
        catch
        {
            return Cache[broker] = null;
        }
    }
}
