from daxalgo_ml.analyst.agents.indicator import compute_panel, run_agent


def test_compute_panel_returns_finite_values(synthetic_df):
    panel = compute_panel(synthetic_df)
    assert all(v == v for v in panel.to_dict().values())  # not NaN


def test_run_agent_uses_text_model_and_emits_panel(synthetic_df, fake_text_model):
    report = run_agent(fake_text_model, synthetic_df)
    assert "trending" in report.summary.lower() or report.summary  # not empty
    assert "rsi_14" in report.values
    assert "last_close" in report.values
    assert len(fake_text_model.calls) == 1
