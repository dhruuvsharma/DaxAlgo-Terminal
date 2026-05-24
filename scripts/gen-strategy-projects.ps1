# Generates the 21 per-strategy projects that wrap each IBacktestStrategy as a live
# signal-mode host (one window, one VM, one DI extension per strategy — same shape as
# TradingTerminal.Strategies.Rsi). Idempotent: re-running overwrites the generated files
# but leaves any user-customised additions you made inside the project folder alone.
#
# After running, you still need to:
#   - Add each new project to TradingTerminal.sln
#   - Add each as a ProjectReference in TradingTerminal.App.csproj
#   - Call services.AddXxxStrategy() from AppDependencyInjection.AddStrategyPlugins
# This script emits a snippet for each at the end so you can paste them in.

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$srcDir = Join-Path $root 'src'

# Project manifest: ProjectName | BacktestClass | LiveId | DisplayName | Description | Params (name,type,default;...)
$strategies = @(
    @{ Name='Microprice';            Bt='MicropriceStrategy';            Id='microprice.deviation';     Display='Microprice deviation (microstructure)'; Desc='Pure microstructure scalper. Trades the deviation between the size-weighted microprice and the simple mid.'; Params=@(
        @{ N='EntryThreshold'; T='double'; D='0.001' },
        @{ N='HoldTicks';      T='int';    D='50' },
        @{ N='Quantity';       T='long';   D='1' })
    },
    @{ Name='OrnsteinUhlenbeck';     Bt='OrnsteinUhlenbeckStrategy';     Id='ornstein.uhlenbeck';       Display='Ornstein-Uhlenbeck mean reversion'; Desc='OLS-fit AR(1)-as-OU on the rolling price window; trade z-score deviations with separate entry / exit / stop bands.'; Params=@(
        @{ N='Lookback';   T='int';    D='500' },
        @{ N='RefitEvery'; T='int';    D='50' },
        @{ N='EntryZ';     T='double'; D='2.0' },
        @{ N='ExitZ';      T='double'; D='0.25' },
        @{ N='StopZ';      T='double'; D='4.0' },
        @{ N='Quantity';   T='long';   D='1' })
    },
    @{ Name='AvellanedaStoikov';     Bt='AvellanedaStoikovStrategy';     Id='avellaneda.stoikov';       Display='Avellaneda-Stoikov market maker'; Desc='Inventory-shifted reservation price; symmetric limit quotes with EWMA-variance widening. Cancel + repost every N ticks.'; Params=@(
        @{ N='Gamma';             T='double'; D='0.1' },
        @{ N='K';                 T='double'; D='1.5' },
        @{ N='VarianceHalfLife';  T='double'; D='200.0' },
        @{ N='QuoteSize';         T='long';   D='1' },
        @{ N='MaxInventory';      T='long';   D='5' },
        @{ N='HorizonTicks';      T='int';    D='5000' },
        @{ N='RequoteEveryTicks'; T='int';    D='100' })
    },
    @{ Name='Twap';                  Bt='TwapExecutionStrategy';         Id='twap.execution';           Display='TWAP buy execution'; Desc='Splits a parent order into N equal market children, fired evenly. Mirrors broker TWAP algos.'; Params=@(
        @{ N='Side';           T='OrderSide'; D='OrderSide.Buy'; Initializer='OrderSide.Buy' },
        @{ N='ParentQuantity'; T='long';      D='100' },
        @{ N='Slices';         T='int';       D='10' })
    },
    @{ Name='Bollinger';             Bt='BollingerReversionStrategy';    Id='bollinger.reversion';      Display='Bollinger band reversion (forex)'; Desc='Long below the lower band, short above the upper; exit at SMA, stop at extreme-band breach.'; Params=@(
        @{ N='Period';             T='int';    D='20' },
        @{ N='EntryStd';           T='double'; D='2.0' },
        @{ N='StopBandMultiplier'; T='double'; D='3.0' },
        @{ N='Quantity';           T='long';   D='1' })
    },
    @{ Name='MaCrossover';           Bt='MovingAverageCrossoverStrategy';Id='ma.crossover';             Display='MA crossover / golden cross (forex)'; Desc='Fast / slow SMA crossover. Always-in-market: flips between long and short on every cross.'; Params=@(
        @{ N='FastPeriod'; T='int';  D='50' },
        @{ N='SlowPeriod'; T='int';  D='200' },
        @{ N='Quantity';   T='long'; D='1' })
    },
    @{ Name='ConnorsRsi2';           Bt='RsiTwoPeriodStrategy';          Id='connors.rsi2';             Display='Connors RSI(2) reversion (forex)'; Desc='Larry Connors RSI(2). Buy at RSI ≤ entry; exit at RSI ≥ exit OR close above 5-SMA.'; Params=@(
        @{ N='RsiPeriod';     T='int';    D='2' },
        @{ N='EntryRsi';      T='double'; D='10' },
        @{ N='ExitRsi';       T='double'; D='90' },
        @{ N='ExitSmaPeriod'; T='int';    D='5' },
        @{ N='Quantity';      T='long';   D='1' })
    },
    @{ Name='LondonOpenBreakout';    Bt='LondonOpenBreakoutStrategy';    Id='london.open.breakout';     Display='London-open breakout (forex)'; Desc='Asian-session range + 08:00 UTC breakout, ATR trailing stop, flat at 16:00 UTC.'; Params=@(
        @{ N='LondonOpenHourUtc';  T='int';    D='8' },
        @{ N='LondonCloseHourUtc'; T='int';    D='16' },
        @{ N='AtrStopMultiplier';  T='double'; D='2.0' },
        @{ N='AtrPeriod';          T='int';    D='50' },
        @{ N='Quantity';           T='long';   D='1' })
    },
    @{ Name='Macd';                  Bt='MacdCrossoverStrategy';         Id='macd.crossover';           Display='MACD signal crossover (forex)'; Desc='12/26/9 MACD vs signal line; flip direction on every cross.'; Params=@(
        @{ N='FastPeriod';   T='int';  D='12' },
        @{ N='SlowPeriod';   T='int';  D='26' },
        @{ N='SignalPeriod'; T='int';  D='9' },
        @{ N='Quantity';     T='long'; D='1' })
    },
    @{ Name='TrendFilter';           Bt='TrendFilterStrategy';           Id='trend.filter';             Display='200-SMA trend filter (index)'; Desc='Long when price > long SMA, flat otherwise (Faber 2007 tactical-AA overlay).'; Params=@(
        @{ N='Period';     T='int';  D='200' },
        @{ N='AllowShort'; T='bool'; D='false' },
        @{ N='Quantity';   T='long'; D='1' })
    },
    @{ Name='VolatilityTargeted';    Bt='VolatilityTargetedStrategy';    Id='vol.targeted';             Display='Volatility targeting (index)'; Desc='Position = target_vol / realised_vol_ewma. AQR-style risk-parity overlay.'; Params=@(
        @{ N='TargetVol';           T='double'; D='0.001' },
        @{ N='VolHalfLife';         T='double'; D='200.0' },
        @{ N='MaxQuantity';         T='long';   D='10' },
        @{ N='RebalanceEveryTicks'; T='int';    D='100' })
    },
    @{ Name='GapFade';               Bt='GapFadeStrategy';               Id='gap.fade';                 Display='Overnight gap fade (index)'; Desc='Detect overnight gap by inter-tick time delta + price jump; fade toward previous close.'; Params=@(
        @{ N='OvernightGapMinutes'; T='double'; D='60' },
        @{ N='MinGapPct';           T='double'; D='0.002' },
        @{ N='StopGapMultiples';    T='double'; D='1.5' },
        @{ N='MaxHoldTicks';        T='int';    D='1000' },
        @{ N='Quantity';            T='long';   D='1' })
    },
    @{ Name='EodMomentum';           Bt='EndOfDayMomentumStrategy';      Id='eod.momentum';             Display='End-of-day momentum (index)'; Desc='Take direction of day''s open-to-now return in the last fraction of the UTC session.'; Params=@(
        @{ N='LastFractionOfDay';   T='double'; D='0.10' },
        @{ N='MinDayReturn';        T='double'; D='0.0005' },
        @{ N='SessionStartHourUtc'; T='int';    D='13' },
        @{ N='SessionEndHourUtc';   T='int';    D='20' },
        @{ N='Quantity';            T='long';   D='1' })
    },
    @{ Name='PullbackContinuation';  Bt='PullbackContinuationStrategy';  Id='pullback.continuation';    Display='Trend pullback continuation (index)'; Desc='200-period trend filter + N-tick pullback + resumption entry. Buy-the-dip with a filter.'; Params=@(
        @{ N='TrendPeriod';     T='int';    D='200' },
        @{ N='PullbackWindow';  T='int';    D='20' },
        @{ N='PullbackPct';     T='double'; D='0.002' },
        @{ N='StopPct';         T='double'; D='0.005' },
        @{ N='TakeProfitPct';   T='double'; D='0.010' },
        @{ N='Quantity';        T='long';   D='1' })
    },
    @{ Name='BookPressure';          Bt='BookPressureStrategy';          Id='book.pressure';            Display='Order-book pressure / cumulative imbalance (L2)'; Desc='Multi-level queue imbalance signal (L1 approximation when no DepthSnapshot is available).'; Params=@(
        @{ N='EntryThreshold'; T='double'; D='0.35' },
        @{ N='HoldTicks';      T='int';    D='50' },
        @{ N='Quantity';       T='long';   D='1' })
    },
    @{ Name='LiquiditySweep';        Bt='LiquiditySweepStrategy';        Id='liquidity.sweep';          Display='Liquidity-sweep detector (L2)'; Desc='Detect rapid depletion of touch size combined with same-side price drop. Momentum follow.'; Params=@(
        @{ N='Lookback';    T='int';    D='100' },
        @{ N='SweepRatio';  T='double'; D='0.40' },
        @{ N='HoldTicks';   T='int';    D='50' },
        @{ N='Quantity';    T='long';   D='1' })
    },
    @{ Name='IcebergDetection';      Bt='IcebergDetectionStrategy';      Id='iceberg.detection';        Display='Iceberg / hidden-liquidity detector (L2)'; Desc='Sticky-touch heuristic - price stays unchanged on one side across N ticks => iceberg support.'; Params=@(
        @{ N='StickyTicks';            T='int';    D='200' },
        @{ N='PriceStabilityEpsilon';  T='double'; D='1e-9' },
        @{ N='HoldTicks';              T='int';    D='100' },
        @{ N='Quantity';               T='long';   D='1' })
    },
    @{ Name='OrderFlowToxicity';     Bt='OrderFlowToxicityStrategy';     Id='order.flow.toxicity';      Display='Order-flow toxicity / VPIN-style (L2)'; Desc='VPIN-style |Σ signed| / Σ|signed|. Mean-revert against high toxicity (Easley-LdP-O''Hara).'; Params=@(
        @{ N='WindowTicks';        T='int';    D='200' },
        @{ N='ToxicityThreshold';  T='double'; D='0.55' },
        @{ N='HoldTicks';          T='int';    D='100' },
        @{ N='Quantity';           T='long';   D='1' })
    },
    @{ Name='ThinBookFilter';        Bt='ThinBookFilterStrategy';        Id='thin.book.filter';         Display='Thin-book breakout filter (L2)'; Desc='Breakout entry gated by depth threshold — skips entries during liquidity droughts.'; Params=@(
        @{ N='BreakoutLookback'; T='int';    D='100' },
        @{ N='DepthLookback';    T='int';    D='200' },
        @{ N='MinDepthRatio';    T='double'; D='1.0' },
        @{ N='HoldTicks';        T='int';    D='200' },
        @{ N='Quantity';         T='long';   D='1' })
    },
    @{ Name='OnlineRegressionAlpha'; Bt='OnlineRegressionAlphaStrategy'; Id='online.regression.alpha';  Display='Online-regression alpha (RLS)'; Desc='Recursive least squares with forgetting on (microprice dev, queue imbalance, rolling vol). First ML-driven strategy.'; Params=@(
        @{ N='HoldTicks';      T='int';    D='50' },
        @{ N='EntryThreshold'; T='double'; D='0.0001' },
        @{ N='VolHalfLife';    T='double'; D='100.0' },
        @{ N='Lambda';         T='double'; D='0.99' },
        @{ N='Quantity';       T='long';   D='1' })
    },
    @{ Name='AnomalyDetector';       Bt='AnomalyDetectorStrategy';       Id='anomaly.detector';         Display='Rolling z-score anomaly detector'; Desc='Spread / queue-imbalance / 1-tick-return z-scores. Risk filter + exchange-glitch detector.'; Params=@(
        @{ N='Window';          T='int';    D='200' },
        @{ N='ZScoreThreshold'; T='double'; D='4.0' },
        @{ N='CooldownTicks';   T='int';    D='100' },
        @{ N='Quantity';        T='long';   D='1' })
    }
)

function ParamFieldDecl($p) {
    $defaultExpr = if ($p.ContainsKey('Initializer')) { $p.Initializer } else { $p.D }
    "    [ObservableProperty] private $($p.T) _$(($p.N).Substring(0,1).ToLower() + ($p.N).Substring(1)) = $defaultExpr;"
}

function CtorArgs($params) {
    ($params | ForEach-Object { $_.N }) -join ', '
}

function ParamXamlField($p) {
@"
                <TextBlock Text="$($p.N)" Margin="0,0,0,2" />
                <TextBox Text="{Binding $($p.N), Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                         IsEnabled="{Binding IsStreaming, Converter={StaticResource InverseBooleanConverter}}"
                         FontFamily="Consolas"
                         Margin="0,0,0,8" />
"@
}

foreach ($s in $strategies) {
    $projDir = Join-Path $srcDir "TradingTerminal.Strategies.$($s.Name)"
    New-Item -ItemType Directory -Path $projDir -Force | Out-Null

    # csproj
    @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <UseWPF>true</UseWPF>
    <Description>$($s.Display) — live signal-mode wrapper around the $($s.Bt) backtest implementation.</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\TradingTerminal.Core\TradingTerminal.Core.csproj" />
    <ProjectReference Include="..\TradingTerminal.Infrastructure\TradingTerminal.Infrastructure.csproj" />
    <ProjectReference Include="..\TradingTerminal.UI\TradingTerminal.UI.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="MahApps.Metro" Version="2.4.10" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
  </ItemGroup>

</Project>
"@ | Out-File -FilePath (Join-Path $projDir "TradingTerminal.Strategies.$($s.Name).csproj") -Encoding utf8 -NoNewline

    # ITradingStrategy descriptor
    @"
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.$($s.Name);

public sealed class $($s.Name)Strategy : ITradingStrategy
{
    public string Id => `"$($s.Id)`";
    public string DisplayName => `"$($s.Display)`";
    public string Description => `"$($s.Desc)`";
}
"@ | Out-File -FilePath (Join-Path $projDir "$($s.Name)Strategy.cs") -Encoding utf8 -NoNewline

    # ViewModel
    $fieldDecls = ($s.Params | ForEach-Object { ParamFieldDecl $_ }) -join "`n"
    $ctorArgsList = CtorArgs $s.Params
    @"
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest.Strategies;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.$($s.Name);

/// <summary>
/// Live signal-mode VM for $($s.Display). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref=`"$($s.Bt)`"/> constructor — add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class $($s.Name)StrategyViewModel : LiveSignalStrategyViewModelBase
{
$fieldDecls

    public $($s.Name)StrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<$($s.Name)StrategyViewModel> logger)
        : base(
            strategyId: `"$($s.Id)`",
            strategyDisplayName: `"$($s.Display)`",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.$($s.Bt)(contract, $ctorArgsList);
}
"@ | Out-File -FilePath (Join-Path $projDir "$($s.Name)StrategyViewModel.cs") -Encoding utf8 -NoNewline

    # Window XAML
    $paramFields = ($s.Params | ForEach-Object { ParamXamlField $_ }) -join "`n"
    @"
<mah:MetroWindow x:Class="TradingTerminal.Strategies.$($s.Name).$($s.Name)StrategyWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                 xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                 xmlns:mah="http://metro.mahapps.com/winfx/xaml/controls"
                 mc:Ignorable="d"
                 Title="{Binding StrategyDisplayName}"
                 Height="640" Width="980"
                 WindowStartupLocation="CenterOwner"
                 Background="{DynamicResource Background.Primary}"
                 Foreground="{DynamicResource Text.Primary}">
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="{Binding StrategyDisplayName}" FontSize="18" FontWeight="SemiBold" />
        <TextBlock Grid.Row="0" Margin="0,28,0,0"
                   Text="Live signal mode — orders are surfaced as notifications, never executed."
                   Foreground="{DynamicResource Text.Secondary}" TextWrapping="Wrap" />

        <Grid Grid.Row="1" Margin="0,12,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column="0" Text="Instrument" VerticalAlignment="Center" Margin="0,0,8,0" />
            <ComboBox Grid.Column="1" ItemsSource="{Binding Instruments}"
                      SelectedItem="{Binding SelectedInstrument}"
                      DisplayMemberPath="DisplayName"
                      IsEnabled="{Binding IsStreaming, Converter={StaticResource InverseBooleanConverter}}"
                      Margin="0,0,12,0" />
            <Button Grid.Column="2" Content="Start" Command="{Binding StartCommand}"
                    IsEnabled="{Binding IsStreaming, Converter={StaticResource InverseBooleanConverter}}"
                    Padding="16,4" Margin="0,0,4,0" />
            <Button Grid.Column="3" Content="Stop" Command="{Binding StopCommand}"
                    IsEnabled="{Binding IsStreaming}" Padding="16,4" Margin="0,0,4,0" />
            <Button Grid.Column="4" Content="Clear log" Command="{Binding ClearSignalsCommand}"
                    Padding="16,4" />
        </Grid>

        <!-- Parameters — edit this block to surface the knobs you care about. -->
        <Border Grid.Row="2"
                BorderBrush="{DynamicResource Border.Brush}" BorderThickness="1"
                Padding="10" Margin="0,0,0,8">
            <StackPanel>
                <TextBlock Text="Parameters" FontWeight="SemiBold" Margin="0,0,0,6" />
$paramFields
            </StackPanel>
        </Border>

        <Border Grid.Row="3"
                BorderBrush="{DynamicResource Border.Brush}" BorderThickness="1"
                Padding="8" Margin="0,0,0,8">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0">
                    <TextBlock Text="Status" Foreground="{DynamicResource Text.Secondary}" FontSize="10" />
                    <TextBlock Text="{Binding Status}" FontFamily="Consolas" />
                </StackPanel>
                <StackPanel Grid.Column="1">
                    <TextBlock Text="Ticks" Foreground="{DynamicResource Text.Secondary}" FontSize="10" />
                    <TextBlock Text="{Binding TicksSeen}" FontFamily="Consolas" />
                </StackPanel>
                <StackPanel Grid.Column="2">
                    <TextBlock Text="Bid" Foreground="{DynamicResource Text.Secondary}" FontSize="10" />
                    <TextBlock Text="{Binding LastBid, StringFormat=F4}" FontFamily="Consolas" />
                </StackPanel>
                <StackPanel Grid.Column="3">
                    <TextBlock Text="Ask" Foreground="{DynamicResource Text.Secondary}" FontSize="10" />
                    <TextBlock Text="{Binding LastAsk, StringFormat=F4}" FontFamily="Consolas" />
                </StackPanel>
                <StackPanel Grid.Column="4">
                    <TextBlock Text="Signals" Foreground="{DynamicResource Text.Secondary}" FontSize="10" />
                    <TextBlock Text="{Binding Signals.Count}" FontFamily="Consolas" />
                </StackPanel>
            </Grid>
        </Border>

        <DataGrid Grid.Row="4" ItemsSource="{Binding Signals}"
                  AutoGenerateColumns="False" IsReadOnly="True" HeadersVisibility="Column"
                  GridLinesVisibility="Horizontal" CanUserAddRows="False" CanUserResizeRows="False" RowHeaderWidth="0">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Time"  Binding="{Binding TimeText}" Width="110" />
                <DataGridTextColumn Header="Side"  Binding="{Binding SideText}" Width="60" />
                <DataGridTextColumn Header="Qty"   Binding="{Binding Quantity}" Width="60" />
                <DataGridTextColumn Header="Type"  Binding="{Binding TypeText}" Width="80" />
                <DataGridTextColumn Header="Price" Binding="{Binding Price, StringFormat=F4}" Width="100" />
                <DataGridTextColumn Header="Mid"   Binding="{Binding Mid, StringFormat=F4}"   Width="100" />
                <DataGridTextColumn Header="Note"  Binding="{Binding Note}" Width="*" />
            </DataGrid.Columns>
        </DataGrid>

        <TextBlock Grid.Row="5" Text="{Binding ValidationError}" Foreground="#E06C75"
                   Margin="0,6,0,0"
                   Visibility="{Binding ValidationError, Converter={StaticResource StringToVisibilityConverter}}" />
    </Grid>
</mah:MetroWindow>
"@ | Out-File -FilePath (Join-Path $projDir "$($s.Name)StrategyWindow.xaml") -Encoding utf8 -NoNewline

    # Window code-behind
    @"
using MahApps.Metro.Controls;

namespace TradingTerminal.Strategies.$($s.Name);

public partial class $($s.Name)StrategyWindow : MetroWindow
{
    public $($s.Name)StrategyWindow() { InitializeComponent(); }
}
"@ | Out-File -FilePath (Join-Path $projDir "$($s.Name)StrategyWindow.xaml.cs") -Encoding utf8 -NoNewline

    # DependencyInjection extension
    @"
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.$($s.Name);

public static class DependencyInjection
{
    public static IServiceCollection Add$($s.Name)Strategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, $($s.Name)Strategy>();
        services.AddTransient<$($s.Name)StrategyViewModel>();
        services.AddTransient<$($s.Name)StrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: `"$($s.Id)`",
            ViewFactory: sp => sp.GetRequiredService<$($s.Name)StrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<$($s.Name)StrategyViewModel>()));
        return services;
    }
}
"@ | Out-File -FilePath (Join-Path $projDir "DependencyInjection.cs") -Encoding utf8 -NoNewline

    Write-Host "  + $($s.Name)"
}

Write-Host ""
Write-Host 'App.csproj - ProjectReferences:'
foreach ($s in $strategies) {
    $line = '    ' + '<ProjectReference Include="..\TradingTerminal.Strategies.' + $s.Name + '\TradingTerminal.Strategies.' + $s.Name + '.csproj" ' + '/' + '>'
    Write-Host $line
}
Write-Host ''
Write-Host 'AppDependencyInjection.cs - usings + calls:'
foreach ($s in $strategies) { Write-Host "using TradingTerminal.Strategies.$($s.Name);" }
Write-Host ''
foreach ($s in $strategies) { Write-Host "        services.Add$($s.Name)Strategy();" }
