using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// The strategy catalog's picture tile: the user's screenshot when they set one, otherwise the DaxAlgo
/// mark on a lifted gradient (see the XAML — the mark is solid black on transparency, so it needs one
/// to be visible at all).
/// <para>
/// A path that no longer loads (deleted, renamed, not an image) falls back to the mark rather than
/// leaving a blank tile, and bitmaps are decoded up-front (<see cref="BitmapCacheOption.OnLoad"/>) so
/// the catalog never holds the user's file open — they can re-save the screenshot while the app runs.
/// </para>
/// </summary>
public partial class StrategyImageTile : UserControl
{
    private const string LogoUri =
        "pack://application:,,,/TradingTerminal.UI;component/Assets/DaxAlgoLogo-BackgroundLess.png";

    public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
        nameof(ImagePath), typeof(string), typeof(StrategyImageTile),
        new PropertyMetadata(null, (d, _) => ((StrategyImageTile)d).Refresh()));

    private static readonly DependencyPropertyKey IsFallbackKey = DependencyProperty.RegisterReadOnly(
        nameof(IsFallback), typeof(bool), typeof(StrategyImageTile), new PropertyMetadata(true));

    public static readonly DependencyProperty IsFallbackProperty = IsFallbackKey.DependencyProperty;

    /// <summary>Decoded once per process — every catalog row shares the one mark.</summary>
    private static readonly ImageSource Logo = LoadLogo();

    public StrategyImageTile()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>Absolute path to the user's screenshot; null/blank (or unreadable) shows the mark.</summary>
    public string? ImagePath
    {
        get => (string?)GetValue(ImagePathProperty);
        set => SetValue(ImagePathProperty, value);
    }

    /// <summary>True while the tile is showing the mark rather than a real screenshot — drives the
    /// lifted backdrop and the wider inset.</summary>
    public bool IsFallback
    {
        get => (bool)GetValue(IsFallbackProperty);
        private set => SetValue(IsFallbackKey, value);
    }

    private void Refresh()
    {
        var custom = TryLoad(ImagePath);
        IsFallback = custom is null;
        Picture.Source = custom ?? Logo;
    }

    private static ImageSource? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            return Decode(new Uri(path, UriKind.Absolute));
        }
        catch
        {
            // Unreadable, locked, or not an image — the mark is a better answer than a blank tile.
            return null;
        }
    }

    private static ImageSource LoadLogo() => Decode(new Uri(LogoUri, UriKind.Absolute));

    private static ImageSource Decode(Uri uri)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;   // decode now, don't hold the file open
        image.UriSource = uri;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
