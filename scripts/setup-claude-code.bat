@echo off
setlocal enableextensions
title DaxAlgo Terminal - contributor setup

rem ============================================================================
rem  One-time contributor setup for DaxAlgo Terminal.
rem
rem  Installs the tooling a contributor needs and nothing else - the project's
rem  Claude Code config (agents, skills, hooks, CLAUDE.md, .claude/settings.json)
rem  already ships inside this repo, so cloning it is all the "Claude setup"
rem  there is. This script just makes sure the tools exist:
rem      * Git           (version control + Claude Code's Bash tool)
rem      * .NET 9 SDK    (build / run the app)
rem      * Claude Code   (the AI dev CLI)
rem
rem  Run it once after cloning. Lives in <repo>\scripts\ ; resolves the repo
rem  root from its own location, so it works no matter where it's launched from.
rem ============================================================================

rem Repo root = the folder above this script.
pushd "%~dp0.." || (echo [!] Could not locate the repo root.& goto :fail)
set "REPO_ROOT=%CD%"

echo ============================================================
echo   DaxAlgo Terminal - contributor setup
echo   Repo: %REPO_ROOT%
echo ============================================================
echo.
echo This will check for / install: Git, .NET 9 SDK, Claude Code.
echo The Claude agents/skills/hooks/instructions already come with
echo the repo - nothing to install for those.
echo.
pause
echo.

rem ---- winget (used to install Git / .NET) ----------------------------------
set "HAS_WINGET="
where winget >nul 2>&1 && set "HAS_WINGET=1"
if not defined HAS_WINGET (
    echo [!] winget not found. Install "App Installer" from the Microsoft Store
    echo     ^(or install Git + the .NET 9 SDK by hand^), then re-run this script.
    echo     Claude Code can still install below via its own downloader.
    echo.
)

rem ---- Git ------------------------------------------------------------------
where git >nul 2>&1
if errorlevel 1 (
    echo [ ] Git not found - installing...
    if defined HAS_WINGET (
        winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements
    ) else (
        echo     Get it from https://git-scm.com/downloads/win
    )
) else (
    echo [x] Git is installed.
)

rem ---- .NET 9 SDK -----------------------------------------------------------
rem The app targets net9.0-windows, so we specifically want a 9.x SDK (it bundles
rem the matching Windows desktop runtime). A 10.x SDK alone is not enough to run it.
set "HAS_NET9="
for /f "tokens=1" %%v in ('dotnet --list-sdks 2^>nul') do (
    echo %%v | findstr /b /c:"9." >nul && set "HAS_NET9=1"
)
if not defined HAS_NET9 (
    echo [ ] .NET 9 SDK not found - installing...
    if defined HAS_WINGET (
        winget install --id Microsoft.DotNet.SDK.9 -e --source winget --accept-package-agreements --accept-source-agreements
    ) else (
        echo     Get it from https://dotnet.microsoft.com/download/dotnet/9.0
    )
) else (
    echo [x] .NET 9 SDK is installed.
)

rem ---- Claude Code ----------------------------------------------------------
where claude >nul 2>&1
if not errorlevel 1 (
    echo [x] Claude Code is installed.
) else if exist "%USERPROFILE%\.local\bin\claude.exe" (
    echo [x] Claude Code is installed ^(%USERPROFILE%\.local\bin^).
) else (
    echo [ ] Claude Code not found - installing the native build ^(auto-updates^)...
    curl -fsSL https://claude.ai/install.cmd -o "%TEMP%\claude-install.cmd"
    if errorlevel 1 (
        echo     [!] Download failed.
        if defined HAS_WINGET (
            echo     Trying winget instead...
            winget install --id Anthropic.ClaudeCode -e --accept-package-agreements --accept-source-agreements
        ) else (
            echo     Install manually: in PowerShell run  irm https://claude.ai/install.ps1 ^| iex
        )
    ) else (
        call "%TEMP%\claude-install.cmd"
        del "%TEMP%\claude-install.cmd" >nul 2>&1
    )
)

echo.
echo ============================================================
echo   Setup complete.  Next steps:
echo.
echo   1. CLOSE and RE-OPEN your terminal (so PATH picks up the
echo      newly installed tools).
echo   2. Double-click  claude-here.bat  in the repo root, or run
echo      it from a terminal, to start Claude Code in the project.
echo   3. On first launch, sign in with your Claude Pro / Max /
echo      Team / Enterprise account in the browser.
echo      NOTE: Claude Code is NOT available on the free claude.ai
echo      plan - each contributor needs their own paid plan
echo      (or an API provider key).
echo.
echo   Build sanity check (after reopening the terminal):
echo      dotnet build
echo ============================================================
popd
echo.
pause
exit /b 0

:fail
echo Setup failed.
pause
exit /b 1
