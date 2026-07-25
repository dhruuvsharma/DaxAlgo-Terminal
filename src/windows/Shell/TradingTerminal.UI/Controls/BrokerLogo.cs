using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.UI.Controls;

/// <summary>Renders a broker's identifying mark, or no image so the caller's monogram remains visible.</summary>
public sealed class BrokerLogo : Image
{
    private static readonly IReadOnlyDictionary<BrokerKind, string> Assets =
        new Dictionary<BrokerKind, string>
        {
            [BrokerKind.InteractiveBrokers] = "interactive-brokers.png",
            [BrokerKind.NinjaTrader] = "ninjatrader.png",
            [BrokerKind.CTrader] = "ctrader.png",
            [BrokerKind.Alpaca] = "alpaca.png",
            [BrokerKind.Binance] = "binance.png",
            [BrokerKind.IronBeam] = "ironbeam.png",
            [BrokerKind.LondonStrategicEdge] = "london-strategic-edge.png",
            [BrokerKind.Upstox] = "upstox.png",
            [BrokerKind.Coinbase] = "coinbase.png",
            [BrokerKind.Bybit] = "bybit.png",
            [BrokerKind.Kraken] = "kraken.png",
            [BrokerKind.Okx] = "okx.png",
        };

    private static readonly Dictionary<BrokerKind, ImageSource?> Cache = [];

    public BrokerLogo()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public static readonly DependencyProperty BrokerProperty = DependencyProperty.Register(
        nameof(Broker), typeof(BrokerKind), typeof(BrokerLogo),
        new PropertyMetadata(BrokerKind.Simulated, OnBrokerChanged));

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
        if (!Assets.TryGetValue(broker, out var asset)) return Cache[broker] = null;

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
