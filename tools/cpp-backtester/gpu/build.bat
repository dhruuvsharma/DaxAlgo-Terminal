@echo off
rem Build gpu_optimizer.exe with nvcc + the MSVC host compiler (no cmake needed).
rem Edit VCVARS below for your Visual Studio install/edition if it differs.
cd /d "%~dp0"

set "VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" ( echo Could not find vcvars64.bat at "%VCVARS%" - edit this path. & exit /b 1 )
call "%VCVARS%" >nul

if not exist build mkdir build

rem The two override flags are only needed when MSVC is newer than the CUDA toolkit knows about
rem (e.g. CUDA 12.1 with VS 17.14 / MSVC 14.44). Harmless when versions already match.
rem Archs: Turing sm_75 (RTX 20xx / GTX 16xx), Ampere sm_86 (RTX 30xx), Ada sm_89 (RTX 40xx) + PTX.
nvcc -O3 -std=c++17 -allow-unsupported-compiler -D_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH -gencode arch=compute_75,code=sm_75 -gencode arch=compute_86,code=sm_86 -gencode arch=compute_89,code=sm_89 -gencode arch=compute_89,code=compute_89 -o build\gpu_optimizer.exe gpu_optimizer.cu

if %ERRORLEVEL% neq 0 ( echo nvcc build FAILED. & exit /b %ERRORLEVEL% )
echo Built build\gpu_optimizer.exe
