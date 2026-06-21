# GPU parameter-sweep accelerator

A CUDA program that evaluates a grid of mean-reversion `(lookback, entryZ)` combinations in parallel
— one GPU thread per combination over a shared bid/ask series. It reproduces the C# engine's fill
arithmetic exactly (slippage 0, multiplier 1; an order decided at tick *i* fills at *i+1*, buys at the
ask and sells at the bid), so **net profit per combo should match the CPU `GridOptimizer` to floating
point**. That equality is your correctness check.

## Status

The C# side (`ProcessGpuOptimizer` + `HybridGridOptimizer`, with automatic CPU fallback) ships built
and tested. **This `.cu` is not built or run by CI** — build and validate it on your CUDA machine.

## Build (needs the CUDA Toolkit)

```bash
cd tools/cpp-backtester/gpu
cmake -B build -S . -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

Set your card's compute capability in `CMakeLists.txt` (`CUDA_ARCHITECTURES`) if the defaults
(`75;86;89`) don't cover it. The output is `gpu_optimizer(.exe)`.

## Wire it in

Point `ProcessGpuOptimizer` at the binary (or drop it next to the DaxAlgo app once the Studio resolves
it by convention). When present and the spec is GPU-portable (`meanReversion` + net-profit criterion +
lookback/entryZ axes), `HybridGridOptimizer` uses the GPU; otherwise — or on any failure — it falls
back to the CPU optimizer transparently.

## Validate (parity check)

Run the same spec through the CPU `GridOptimizer` and the GPU and compare net profit per combo; they
should agree to ~1e-9. If they don't, the kernel's fill sequencing has diverged from
`MeanReversionKernel` — check the "decide at *i*, fill at *i+1*" ordering in `sweepKernel`.

## Protocol

stdin (whitespace-delimited): `N <n> <bid0> <ask0> …  QTY <q>  EXITZ <x>  LOOKBACKS <m> <…>  ENTRYZS <p> <…>  END`
stdout: one line per combo — `<lookback> <entryZ> <netProfit> <trades>`.
