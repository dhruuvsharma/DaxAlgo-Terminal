# Standalone CUDA build for the GPU parameter-sweep accelerator.
# Intentionally separate from the parent cpp-backtester CMake (which pulls Arrow/Eigen/etc.) so this
# only needs the CUDA Toolkit — nothing else.
#
#   cmake -B build -S . -DCMAKE_BUILD_TYPE=Release
#   cmake --build build --config Release
#
# Produces gpu_optimizer(.exe). Drop it next to the DaxAlgo app (or point the C# bridge at it) and
# the Studio uses the GPU automatically; if it's absent the C# side falls back to the CPU optimizer.

cmake_minimum_required(VERSION 3.18)
project(gpu_optimizer LANGUAGES CXX CUDA)

set(CMAKE_CUDA_STANDARD 17)
set(CMAKE_CXX_STANDARD 17)

add_executable(gpu_optimizer gpu_optimizer.cu)

# Real-world consumer GPUs. Adjust to your card's compute capability if needed
# (e.g. 89 = Ada/RTX 40xx, 86 = Ampere/RTX 30xx, 75 = Turing/RTX 20xx).
set_target_properties(gpu_optimizer PROPERTIES CUDA_ARCHITECTURES "75;86;89")
