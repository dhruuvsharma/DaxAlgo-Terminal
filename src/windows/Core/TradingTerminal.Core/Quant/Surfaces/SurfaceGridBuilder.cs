using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Quant.Surfaces;

/// <summary>One configured axis: the picked option id, the bin range (ignored when the option's
/// range is fixed), and — for Z/Color — an optional custom formula that overrides the picked
/// statistic (variables are metric ids, see <see cref="SurfaceFormula"/>).</summary>
public sealed record SurfaceAxisSpec(string OptionId, double Min, double Max, double Step, string? Formula = null);

/// <summary>A full surface request: mode + the four axis specs.</summary>
public sealed record SurfaceRequest(
    SurfaceMode Mode,
    SurfaceAxisSpec X,
    SurfaceAxisSpec Y,
    SurfaceAxisSpec Z,
    SurfaceAxisSpec W,
    double PeriodsPerYear);

/// <summary>The computed surface. Grids are indexed [yRow, xCol]; NaN cells are honest gaps
/// (empty buckets) and render as holes, never as zero.</summary>
public sealed record SurfaceGridResult(
    SurfaceMode Mode,
    string XName, SurfaceAxisFormat XFormat, double[] XValues, string[] XLabels,
    string YName, SurfaceAxisFormat YFormat, double[] YValues, string[] YLabels,
    string ZName, SurfaceAxisFormat ZFormat, double[,] Z,
    string WName, SurfaceAxisFormat WFormat, double[,] W,
    double[,] Robustness)
{
    public int Columns => XValues.Length;
    public int Rows => YValues.Length;
}

/// <summary>
/// Builds a <see cref="SurfaceGridResult"/> from bars (historical or a live rolling window) for
/// either surface mode. Pure, allocation-bounded, and CancellationToken-aware — a single pass
/// over the bars plus a per-cell statistics pass, cheap enough to re-run every live tick batch.
/// </summary>
public static class SurfaceGridBuilder
{
    /// <summary>Hard cap per axis so a careless bin step can't request a million cells.</summary>
    public const int MaxAxisPoints = 81;

    private const int VolatilityWindow = 20;
    private const int QuantileBuckets = 10;

    public static SurfaceGridResult Build(IReadOnlyList<Bar> bars, SurfaceRequest request, CancellationToken ct = default)
    {
        var zEval = CellEvaluator.Create(request.Z, "Z");
        var wEval = CellEvaluator.Create(request.W, "Color");

        var result = request.Mode switch
        {
            SurfaceMode.TemporalAggregation => BuildTemporal(bars, request, zEval, wEval, ct),
            _ => BuildCrossSection(bars, request, zEval, wEval, ct),
        };
        return result with { Robustness = SurfaceGridAnalysis.Robustness(result.Z) };
    }

    /// <summary>Annualization factor from the median bar spacing — works for 24/7 crypto and
    /// session-bound equities alike without a per-market table.</summary>
    public static double EstimatePeriodsPerYear(IReadOnlyList<Bar> bars)
    {
        if (bars.Count < 3) return 252;
        var gaps = new List<double>(bars.Count - 1);
        for (var i = 1; i < bars.Count; i++)
        {
            var s = (bars[i].TimestampUtc - bars[i - 1].TimestampUtc).TotalSeconds;
            if (s > 0) gaps.Add(s);
        }
        if (gaps.Count == 0) return 252;
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        return TimeSpan.FromDays(365).TotalSeconds / median;
    }

    // ── Temporal aggregation (seasonality) ────────────────────────────────────────────────────

    private static SurfaceGridResult BuildTemporal(
        IReadOnlyList<Bar> bars, SurfaceRequest request, CellEvaluator zEval, CellEvaluator wEval, CancellationToken ct)
    {
        var ax = SurfaceAxisCatalog.ResolveTemporal(request.X.OptionId)
                 ?? throw new ArgumentException($"Unknown calendar axis '{request.X.OptionId}' on X.");
        var ay = SurfaceAxisCatalog.ResolveTemporal(request.Y.OptionId)
                 ?? throw new ArgumentException($"Unknown calendar axis '{request.Y.OptionId}' on Y.");
        if (ax.Id == ay.Id)
            throw new ArgumentException("X and Y must bucket different calendar dimensions.");

        var buckets = NewBucketGrid(ay.BucketCount, ax.BucketCount);
        var volumes = NewBucketGrid(ay.BucketCount, ax.BucketCount);
        for (var i = 1; i < bars.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (bars[i - 1].Close <= 0) continue;
            var t = bars[i].TimestampUtc;
            var col = ax.Selector(t);
            var row = ay.Selector(t);
            if (col < 0 || col >= ax.BucketCount || row < 0 || row >= ay.BucketCount) continue;
            buckets[row, col].Add(bars[i].Close / bars[i - 1].Close - 1);
            volumes[row, col].Add(bars[i].Close * Math.Max(bars[i].Volume, 0));
        }

        var (z, w) = EvaluateBuckets(buckets, volumes, request.PeriodsPerYear, zEval, wEval);
        var xs = Enumerable.Range(0, ax.BucketCount).Select(i => (double)i).ToArray();
        var ys = Enumerable.Range(0, ay.BucketCount).Select(i => (double)i).ToArray();
        return Assemble(request, ax.Name, SurfaceAxisFormat.Integer, xs, ax.Labels,
                        ay.Name, SurfaceAxisFormat.Integer, ys, ay.Labels, z, w, zEval, wEval);
    }

    // ── Cross-sectional conditioning ──────────────────────────────────────────────────────────

    private static SurfaceGridResult BuildCrossSection(
        IReadOnlyList<Bar> bars, SurfaceRequest request, CellEvaluator zEval, CellEvaluator wEval, CancellationToken ct)
    {
        var ax = SurfaceAxisCatalog.ResolveCrossSection(request.X.OptionId)
                 ?? throw new ArgumentException($"Unknown conditioning axis '{request.X.OptionId}' on X.");
        var ay = SurfaceAxisCatalog.ResolveCrossSection(request.Y.OptionId)
                 ?? throw new ArgumentException($"Unknown conditioning axis '{request.Y.OptionId}' on Y.");
        if (ax.Kind == CrossSectionVariable.TimeLag && ay.Kind == CrossSectionVariable.TimeLag)
            throw new ArgumentException("Only one axis can be a time lag.");
        if (ax.Id == ay.Id)
            throw new ArgumentException("X and Y must condition on different variables.");

        var n = bars.Count;
        var returns = new double[n];   // returns[i] = bar i's return vs bar i-1 (NaN at 0)
        returns[0] = double.NaN;
        for (var i = 1; i < n; i++)
            returns[i] = bars[i - 1].Close > 0 ? bars[i].Close / bars[i - 1].Close - 1 : double.NaN;

        // A lag axis shifts WHERE the other axis's conditioning variable is read (t−k); without
        // one, both variables are read at t and the cell holds the t+1 returns.
        var lagAxis = ax.Kind == CrossSectionVariable.TimeLag ? ax : ay.Kind == CrossSectionVariable.TimeLag ? ay : null;
        var varAxisIsX = ax.Kind != CrossSectionVariable.TimeLag;

        if (lagAxis is not null)
        {
            var varAxis = varAxisIsX ? ax : ay;
            var varSpec = varAxisIsX ? request.X : request.Y;
            var lagSpec = varAxisIsX ? request.Y : request.X;
            var lags = RangeValues(lagSpec, isInteger: true).Select(v => Math.Max(1, (int)v)).Distinct().ToArray();
            var (bucketOf, axisValues, axisLabels) = BucketAssigner(bars, returns, varAxis, varSpec);

            var cols = varAxisIsX ? axisValues.Length : lags.Length;
            var rows = varAxisIsX ? lags.Length : axisValues.Length;
            var buckets = NewBucketGrid(rows, cols);
            var volumes = NewBucketGrid(rows, cols);

            for (var li = 0; li < lags.Length; li++)
            {
                ct.ThrowIfCancellationRequested();
                var k = lags[li];
                for (var i = k; i < n - 1; i++)
                {
                    var b = bucketOf(i - k);
                    if (b < 0 || double.IsNaN(returns[i + 1])) continue;
                    var row = varAxisIsX ? li : b;
                    var col = varAxisIsX ? b : li;
                    buckets[row, col].Add(returns[i + 1]);
                    volumes[row, col].Add(bars[i].Close * Math.Max(bars[i].Volume, 0));
                }
            }

            var (z, w) = EvaluateBuckets(buckets, volumes, request.PeriodsPerYear, zEval, wEval);
            var lagValues = lags.Select(k => (double)k).ToArray();
            var lagLabels = lags.Select(k => $"t−{k}").ToArray();
            return varAxisIsX
                ? Assemble(request, varAxis.Name, varAxis.Format, axisValues, axisLabels,
                           lagAxis.Name, SurfaceAxisFormat.Integer, lagValues, lagLabels, z, w, zEval, wEval)
                : Assemble(request, lagAxis.Name, SurfaceAxisFormat.Integer, lagValues, lagLabels,
                           varAxis.Name, varAxis.Format, axisValues, axisLabels, z, w, zEval, wEval);
        }

        var (xBucketOf, xValues, xLabels) = BucketAssigner(bars, returns, ax, request.X);
        var (yBucketOf, yValues, yLabels) = BucketAssigner(bars, returns, ay, request.Y);
        var grid = NewBucketGrid(yValues.Length, xValues.Length);
        var vols = NewBucketGrid(yValues.Length, xValues.Length);

        for (var i = 1; i < n - 1; i++)
        {
            ct.ThrowIfCancellationRequested();
            var col = xBucketOf(i);
            var row = yBucketOf(i);
            if (col < 0 || row < 0 || double.IsNaN(returns[i + 1])) continue;
            grid[row, col].Add(returns[i + 1]);
            vols[row, col].Add(bars[i].Close * Math.Max(bars[i].Volume, 0));
        }

        var (zg, wg) = EvaluateBuckets(grid, vols, request.PeriodsPerYear, zEval, wEval);
        return Assemble(request, ax.Name, ax.Format, xValues, xLabels,
                        ay.Name, ay.Format, yValues, yLabels, zg, wg, zEval, wEval);
    }

    /// <summary>Builds a bar-index → bucket-index function for a conditioning variable, plus the
    /// axis tick values/labels. Linear bins for prior-return; quantile deciles for vol/volume.</summary>
    private static (Func<int, int> BucketOf, double[] Values, string[] Labels) BucketAssigner(
        IReadOnlyList<Bar> bars, double[] returns, CrossSectionAxisDefinition axis, SurfaceAxisSpec spec)
    {
        var n = bars.Count;
        switch (axis.Kind)
        {
            case CrossSectionVariable.PriorReturnBin:
            {
                var edgesLo = spec.Min;
                var step = spec.Step > 0 ? spec.Step : 0.01;
                var count = Math.Min(MaxAxisPoints, Math.Max(2, (int)Math.Round((spec.Max - spec.Min) / step)));
                var centers = new double[count];
                for (var b = 0; b < count; b++) centers[b] = edgesLo + (b + 0.5) * step;
                return (i =>
                {
                    if (i < 1 || double.IsNaN(returns[i])) return -1;
                    var b = (int)Math.Floor((returns[i] - edgesLo) / step);
                    return b >= 0 && b < count ? b : -1;
                }, centers, Labels(centers, SurfaceAxisFormat.Percent));
            }

            case CrossSectionVariable.VolatilityBucket:
            {
                var vol = new double[n];
                Array.Fill(vol, double.NaN);
                for (var i = VolatilityWindow; i < n; i++)
                {
                    double sum = 0, ss = 0;
                    var m = 0;
                    for (var j = i - VolatilityWindow + 1; j <= i; j++)
                    {
                        if (double.IsNaN(returns[j])) continue;
                        sum += returns[j];
                        ss += returns[j] * returns[j];
                        m++;
                    }
                    if (m >= 2) vol[i] = Math.Sqrt(Math.Max(0, (ss - sum * sum / m) / (m - 1)));
                }
                return QuantileAssigner(vol, "Vol Q");
            }

            case CrossSectionVariable.VolumeDecile:
            {
                var volu = new double[n];
                for (var i = 0; i < n; i++) volu[i] = bars[i].Volume > 0 ? bars[i].Volume : double.NaN;
                return QuantileAssigner(volu, "Vol D");
            }

            default:
                throw new ArgumentException($"Axis kind {axis.Kind} cannot be bucketed directly.");
        }
    }

    private static (Func<int, int>, double[], string[]) QuantileAssigner(double[] values, string labelPrefix)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).ToArray();
        Array.Sort(sorted);
        var axisValues = Enumerable.Range(1, QuantileBuckets).Select(b => (double)b).ToArray();
        var labels = Enumerable.Range(1, QuantileBuckets).Select(b => $"{labelPrefix}{b}").ToArray();
        if (sorted.Length < QuantileBuckets)
            return (_ => -1, axisValues, labels);

        var cuts = new double[QuantileBuckets - 1];
        for (var b = 1; b < QuantileBuckets; b++)
            cuts[b - 1] = sorted[Math.Min(sorted.Length - 1, b * sorted.Length / QuantileBuckets)];

        return (i =>
        {
            var v = values[i];
            if (double.IsNaN(v)) return -1;
            var idx = Array.BinarySearch(cuts, v);
            if (idx < 0) idx = ~idx;
            return Math.Min(idx, QuantileBuckets - 1);
        }, axisValues, labels);
    }

    // ── Shared plumbing ────────────────────────────────────────────────────────────────────────

    private static (double[,] Z, double[,] W) EvaluateBuckets(
        List<double>[,] buckets, List<double>[,] volumes, double ppy, CellEvaluator zEval, CellEvaluator wEval)
    {
        var rows = buckets.GetLength(0);
        var cols = buckets.GetLength(1);
        var z = NewGrid(rows, cols);
        var w = NewGrid(rows, cols);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                if (buckets[r, c].Count == 0) continue;
                var sample = new SurfaceCellSample(buckets[r, c].ToArray(), volumes[r, c].ToArray(), ppy);
                z[r, c] = zEval.Evaluate(sample);
                w[r, c] = wEval.Evaluate(sample);
            }
        }
        return (z, w);
    }

    private static double[] RangeValues(SurfaceAxisSpec spec, bool isInteger)
    {
        var step = spec.Step;
        if (step <= 0 || spec.Max <= spec.Min)
            throw new ArgumentException($"Axis '{spec.OptionId}' needs Min < Max and Step > 0.");
        var count = (int)Math.Floor((spec.Max - spec.Min) / step + 1e-9) + 1;
        if (count > MaxAxisPoints) count = MaxAxisPoints;
        if (count < 2) count = 2;
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            var v = spec.Min + i * step;
            values[i] = isInteger ? Math.Round(v) : v;
        }
        return isInteger ? values.Distinct().ToArray() : values;
    }

    private static string[] Labels(double[] values, SurfaceAxisFormat format) =>
        values.Select(v => SurfaceAxisFormats.Format(v, format)).ToArray();

    private static double[,] NewGrid(int rows, int cols)
    {
        var g = new double[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                g[r, c] = double.NaN;
        return g;
    }

    private static List<double>[,] NewBucketGrid(int rows, int cols)
    {
        var g = new List<double>[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                g[r, c] = new List<double>();
        return g;
    }

    private static SurfaceGridResult Assemble(
        SurfaceRequest request,
        string xName, SurfaceAxisFormat xFormat, double[] xs, string[] xLabels,
        string yName, SurfaceAxisFormat yFormat, double[] ys, string[] yLabels,
        double[,] z, double[,] w, CellEvaluator zEval, CellEvaluator wEval) =>
        new(request.Mode,
            xName, xFormat, xs, xLabels,
            yName, yFormat, ys, yLabels,
            zEval.Name, zEval.Format, z,
            wEval.Name, wEval.Format, w,
            new double[ys.Length, xs.Length]);

    /// <summary>Resolves a Z/W axis spec into "statistic or formula" once, up front, so a bad
    /// formula fails the request instead of producing a silent NaN surface.</summary>
    private sealed class CellEvaluator
    {
        private readonly SurfaceMetricDefinition? _metric;
        private readonly SurfaceFormula? _formula;

        public string Name { get; }
        public SurfaceAxisFormat Format { get; }

        private CellEvaluator(SurfaceMetricDefinition? metric, SurfaceFormula? formula, string name, SurfaceAxisFormat format)
        {
            _metric = metric;
            _formula = formula;
            Name = name;
            Format = format;
        }

        public static CellEvaluator Create(SurfaceAxisSpec spec, string roleName)
        {
            if (!string.IsNullOrWhiteSpace(spec.Formula))
            {
                var formula = SurfaceFormula.TryParse(spec.Formula!, out var error)
                              ?? throw new ArgumentException($"{roleName} formula: {error}");
                return new CellEvaluator(null, formula, spec.Formula!.Trim(), SurfaceAxisFormat.Number);
            }
            var metric = SurfaceMetricRegistry.Resolve(spec.OptionId)
                         ?? throw new ArgumentException($"Unknown statistic '{spec.OptionId}' on {roleName}.");
            return new CellEvaluator(metric, null, metric.Name, metric.Format);
        }

        public double Evaluate(SurfaceCellSample sample)
        {
            if (_metric is not null) return _metric.Compute(sample);
            // Lazy per-cell metric cache: formulas usually touch 1–3 variables.
            var cache = new Dictionary<string, double>(4, StringComparer.Ordinal);
            return _formula!.Evaluate(id =>
            {
                if (cache.TryGetValue(id, out var v)) return v;
                v = SurfaceMetricRegistry.Resolve(id)!.Compute(sample);
                cache[id] = v;
                return v;
            });
        }
    }
}

/// <summary>Post-processing over a computed grid: peak/trough finding and the robustness
/// (spike-detection) score used by the Surface Lab's robustness color mode.</summary>
public static class SurfaceGridAnalysis
{
    /// <summary>Location + value of one grid extremum.</summary>
    public readonly record struct GridPoint(int Row, int Col, double Value)
    {
        public bool IsValid => !double.IsNaN(Value);
    }

    public static GridPoint FindMax(double[,] z) => Find(z, max: true);
    public static GridPoint FindMin(double[,] z) => Find(z, max: false);

    private static GridPoint Find(double[,] z, bool max)
    {
        var best = new GridPoint(-1, -1, double.NaN);
        for (var r = 0; r < z.GetLength(0); r++)
        {
            for (var c = 0; c < z.GetLength(1); c++)
            {
                var v = z[r, c];
                if (double.IsNaN(v)) continue;
                if (!best.IsValid || (max ? v > best.Value : v < best.Value))
                    best = new GridPoint(r, c, v);
            }
        }
        return best;
    }

    /// <summary>
    /// Per-cell robustness score in [0, 1]: the RMS difference between a cell and its (up to 8)
    /// neighbors, normalized by the global Z range. 0 ⇒ flat plateau (a stable effect, colored
    /// green by the view); 1 ⇒ isolated spike whose neighbors drop away (noise/outlier, colored
    /// red). NaN cells and cells with no valid neighbor stay NaN.
    /// </summary>
    public static double[,] Robustness(double[,] z)
    {
        var rows = z.GetLength(0);
        var cols = z.GetLength(1);
        var result = new double[rows, cols];

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in z)
        {
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        var range = max - min;

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                if (double.IsNaN(z[r, c]) || range <= 0)
                {
                    result[r, c] = double.NaN;
                    continue;
                }
                double ss = 0;
                var n = 0;
                for (var dr = -1; dr <= 1; dr++)
                {
                    for (var dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int nr = r + dr, nc = c + dc;
                        if (nr < 0 || nr >= rows || nc < 0 || nc >= cols) continue;
                        if (double.IsNaN(z[nr, nc])) continue;
                        var d = (z[r, c] - z[nr, nc]) / range;
                        ss += d * d;
                        n++;
                    }
                }
                result[r, c] = n == 0 ? double.NaN : Math.Clamp(Math.Sqrt(ss / n) * 3, 0, 1);
            }
        }
        return result;
    }

    /// <summary>Cross-section of the grid at a fixed column (values along Y).</summary>
    public static double[] SliceAtColumn(double[,] z, int col)
    {
        var rows = z.GetLength(0);
        var slice = new double[rows];
        for (var r = 0; r < rows; r++) slice[r] = z[r, col];
        return slice;
    }

    /// <summary>Cross-section of the grid at a fixed row (values along X).</summary>
    public static double[] SliceAtRow(double[,] z, int row)
    {
        var cols = z.GetLength(1);
        var slice = new double[cols];
        for (var c = 0; c < cols; c++) slice[c] = z[row, c];
        return slice;
    }
}
