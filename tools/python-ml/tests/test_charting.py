import base64

from daxalgo_ml.analyst.charting import (
    TrendChannel,
    bars_to_dataframe,
    render_candles,
    render_candles_with_trend,
)


def test_bars_to_dataframe_sets_date_index(synthetic_bars):
    df = bars_to_dataframe([b.model_dump(mode="json") for b in synthetic_bars])
    assert list(df.columns) == ["Open", "High", "Low", "Close", "Volume"]
    assert df.index.name == "Date"
    assert df.index.is_monotonic_increasing


def test_render_candles_returns_base64_png(synthetic_df):
    b64 = render_candles(synthetic_df)
    assert isinstance(b64, str) and len(b64) > 200
    # PNG magic bytes after base64 decode
    raw = base64.b64decode(b64)
    assert raw[:8] == b"\x89PNG\r\n\x1a\n"


def test_render_candles_with_trend_overlays_lines(synthetic_df):
    channel = TrendChannel(
        upper_intercept=110.0,
        upper_slope=0.1,
        lower_intercept=90.0,
        lower_slope=0.05,
        bar_count=len(synthetic_df),
    )
    b64 = render_candles_with_trend(synthetic_df, channel)
    raw = base64.b64decode(b64)
    assert raw[:8] == b"\x89PNG\r\n\x1a\n"
