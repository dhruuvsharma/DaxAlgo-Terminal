// gpu_optimizer.cu — CUDA parameter-sweep accelerator for the mean-reversion kernel.
//
// One GPU thread evaluates one (lookback, entryZ) parameter combination over a shared bid/ask
// series held in device memory, reproducing the C# MeanReversionKernel + L1 fill semantics exactly
// (slippage 0, multiplier 1): an order decided at tick i fills at tick i+1 — buys at the ask, sells
// at the bid — and net profit is the sum of closed round-trips. Because the arithmetic mirrors the
// managed engine, GPU net-profit per combo should match the C# GridOptimizer to floating point.
//
// Protocol — stdin (whitespace-delimited tokens):
//     N <n> <bid0> <ask0> ... <bid_{n-1}> <ask_{n-1}>
//     QTY <qty>  EXITZ <exitZ>
//     LOOKBACKS <m> <l0..l_{m-1}>
//     ENTRYZS  <p> <e0..e_{p-1}>
//     END
// stdout — one line per combo (lookback outer, entryZ inner):
//     <lookback> <entryZ> <netProfit> <trades>
//
// Build: see CMakeLists.txt in this folder (requires the CUDA Toolkit).

#include <cuda_runtime.h>
#include <cstdio>
#include <cstdlib>
#include <iostream>
#include <string>
#include <vector>

static void cudaCheck(cudaError_t err, const char* what) {
    if (err != cudaSuccess) {
        std::cerr << "CUDA error (" << what << "): " << cudaGetErrorString(err) << "\n";
        std::exit(2);
    }
}

__global__ void sweepKernel(
    const double* __restrict__ bid,
    const double* __restrict__ ask,
    int n,
    const int* __restrict__ lookbacks,
    const double* __restrict__ entryZs,
    int numLookbacks,
    int numEntryZs,
    double exitZ,
    long long qty,
    double* __restrict__ outNetProfit,
    int* __restrict__ outTrades)
{
    int idx = blockIdx.x * blockDim.x + threadIdx.x;
    int total = numLookbacks * numEntryZs;
    if (idx >= total) return;

    int li = idx / numEntryZs;
    int ei = idx % numEntryZs;
    int L = lookbacks[li];
    double E = entryZs[ei];

    double netProfit = 0.0;
    int trades = 0;
    int pos = 0;            // signed units: 0 / +qty / -qty
    double entryFill = 0.0;
    int pendingSide = 0;    // 0 none, +1 buy, -1 sell (decided at i, fills at i+1)

    for (int i = 0; i < n; ++i) {
        // 1. Fill an order decided on the previous tick, at this tick's touch.
        if (pendingSide != 0) {
            double fill = pendingSide > 0 ? ask[i] : bid[i];
            if (pos == 0) {
                pos = pendingSide > 0 ? (int)qty : -(int)qty;
                entryFill = fill;
            } else {
                double pnl = pos > 0 ? (fill - entryFill) * (double)qty
                                     : (entryFill - fill) * (double)qty;
                netProfit += pnl;
                trades += 1;
                pos = 0;
            }
            pendingSide = 0;
        }

        // 2. Need a full lookback window [i-L+1 .. i] before trading.
        if (i + 1 < L) continue;

        double sum = 0.0;
        for (int j = i - L + 1; j <= i; ++j) sum += 0.5 * (bid[j] + ask[j]);
        double mean = sum / (double)L;
        double var = 0.0;
        for (int j = i - L + 1; j <= i; ++j) {
            double m = 0.5 * (bid[j] + ask[j]);
            var += (m - mean) * (m - mean);
        }
        var /= (double)L;
        double sd = sqrt(var);
        if (sd <= 0.0) continue;

        double mid = 0.5 * (bid[i] + ask[i]);
        double z = (mid - mean) / sd;

        // 3. Decide the order for the next tick (mirrors MeanReversionKernel).
        if (pos == 0) {
            if (z <= -E) pendingSide = +1;
            else if (z >= E) pendingSide = -1;
        } else if (pos > 0 && z >= -exitZ) {
            pendingSide = -1;
        } else if (pos < 0 && z <= exitZ) {
            pendingSide = +1;
        }
    }

    outNetProfit[idx] = netProfit;
    outTrades[idx] = trades;
}

static bool expect(const std::string& got, const char* want) {
    if (got != want) {
        std::cerr << "Protocol error: expected '" << want << "', got '" << got << "'\n";
        return false;
    }
    return true;
}

int main() {
    std::string tok;
    int n = 0;
    if (!(std::cin >> tok) || !expect(tok, "N") || !(std::cin >> n) || n <= 0) return 1;

    std::vector<double> bid(n), ask(n);
    for (int i = 0; i < n; ++i) std::cin >> bid[i] >> ask[i];

    long long qty = 1;
    double exitZ = 0.0;
    if (!(std::cin >> tok) || !expect(tok, "QTY") || !(std::cin >> qty)) return 1;
    if (!(std::cin >> tok) || !expect(tok, "EXITZ") || !(std::cin >> exitZ)) return 1;

    int m = 0, p = 0;
    if (!(std::cin >> tok) || !expect(tok, "LOOKBACKS") || !(std::cin >> m) || m <= 0) return 1;
    std::vector<int> lookbacks(m);
    for (int i = 0; i < m; ++i) std::cin >> lookbacks[i];
    if (!(std::cin >> tok) || !expect(tok, "ENTRYZS") || !(std::cin >> p) || p <= 0) return 1;
    std::vector<double> entryZs(p);
    for (int i = 0; i < p; ++i) std::cin >> entryZs[i];
    if (!(std::cin >> tok) || !expect(tok, "END")) return 1;

    int total = m * p;

    double *dBid, *dAsk, *dEntry, *dNet;
    int *dLook, *dTrades;
    cudaCheck(cudaMalloc(&dBid, n * sizeof(double)), "malloc bid");
    cudaCheck(cudaMalloc(&dAsk, n * sizeof(double)), "malloc ask");
    cudaCheck(cudaMalloc(&dLook, m * sizeof(int)), "malloc look");
    cudaCheck(cudaMalloc(&dEntry, p * sizeof(double)), "malloc entry");
    cudaCheck(cudaMalloc(&dNet, total * sizeof(double)), "malloc net");
    cudaCheck(cudaMalloc(&dTrades, total * sizeof(int)), "malloc trades");

    cudaCheck(cudaMemcpy(dBid, bid.data(), n * sizeof(double), cudaMemcpyHostToDevice), "cpy bid");
    cudaCheck(cudaMemcpy(dAsk, ask.data(), n * sizeof(double), cudaMemcpyHostToDevice), "cpy ask");
    cudaCheck(cudaMemcpy(dLook, lookbacks.data(), m * sizeof(int), cudaMemcpyHostToDevice), "cpy look");
    cudaCheck(cudaMemcpy(dEntry, entryZs.data(), p * sizeof(double), cudaMemcpyHostToDevice), "cpy entry");

    int threads = 128;
    int blocks = (total + threads - 1) / threads;
    sweepKernel<<<blocks, threads>>>(dBid, dAsk, n, dLook, dEntry, m, p, exitZ, qty, dNet, dTrades);
    cudaCheck(cudaGetLastError(), "kernel launch");
    cudaCheck(cudaDeviceSynchronize(), "kernel sync");

    std::vector<double> net(total);
    std::vector<int> trades(total);
    cudaCheck(cudaMemcpy(net.data(), dNet, total * sizeof(double), cudaMemcpyDeviceToHost), "cpy net");
    cudaCheck(cudaMemcpy(trades.data(), dTrades, total * sizeof(int), cudaMemcpyDeviceToHost), "cpy trades");

    for (int li = 0; li < m; ++li)
        for (int ei = 0; ei < p; ++ei) {
            int idx = li * p + ei;
            std::printf("%d %.10g %.10g %d\n", lookbacks[li], entryZs[ei], net[idx], trades[idx]);
        }

    cudaFree(dBid); cudaFree(dAsk); cudaFree(dLook); cudaFree(dEntry); cudaFree(dNet); cudaFree(dTrades);
    return 0;
}
