# Strategies.OrderFlowCube — 3D regime cube (Helix)

Shape: `modules/Strategies-family.md`. **1,502 LOC / 10 files.**
- First of the regime-cube family: 3-axis order-flow state cube rendered with HelixToolkit.Wpf
  (NU1701 warning expected). Load `regime-cube-strategy` skill BEFORE editing; 3D geometry
  conventions in `quant-math`.
- Needs tape + depth for full fidelity; check `DataRequirement` gating.
- Surface `symbols/Strategies.OrderFlowCube.md` (`OrderFlowCubePlugin` @ :8).
- 3D scene objects must be disposed/cleared on window close (Helix leaks otherwise).
