@echo off
setlocal enableextensions
title DaxAlgo Terminal - Claude Code contributor setup

rem ============================================================================
rem  One-time contributor setup for DaxAlgo Terminal's Claude Code workflow.
rem
rem  Two halves:
rem    A) TOOLING  - install what a contributor needs on their machine:
rem                    * Git           (version control + Claude Code's Bash tool)
rem                    * .NET 9 SDK    (build / run the app)
rem                    * Claude Code   (the AI dev CLI)
rem                    * PowerShell    (the project hooks are PowerShell scripts)
rem    B) WORKFLOW - the agents / skills / hooks / settings that drive the
rem                  multi-agent workflow are COMMITTED to this repo under
rem                  .claude\, so cloning already "installed" them. Claude Code
rem                  loads project-scoped .claude\ automatically when launched
rem                  in the repo root. This script just VERIFIES that config is
rem                  intact and reports what's wired, so you start with proof
rem                  the workflow is live - not a copy into your global config
rem                  (a global copy would drift from the repo and break the
rem                  agents' repo-relative paths).
rem
rem  Run it once after cloning. Lives in <repo>\scripts\ ; resolves the repo
rem  root from its own location, so it works no matter where it's launched from.
rem ============================================================================

rem Repo root = the folder above this script.
pushd "%~dp0.." || (echo [!] Could not locate the repo root.& goto :fail)
set "REPO_ROOT=%CD%"

echo ============================================================
echo   DaxAlgo Terminal - Claude Code contributor setup
echo   Repo: %REPO_ROOT%
echo ============================================================
echo.
echo  Part A installs tooling : Git, .NET 9 SDK, Claude Code, PowerShell.
echo  Part B verifies the agentic workflow that already ships in .claude\.
echo.
pause
echo.

rem ===========================================================================
rem  PART A - TOOLING
rem ===========================================================================
echo ------------------------------------------------------------
echo  PART A : tooling
echo ------------------------------------------------------------

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

rem ---- PowerShell (the .claude\hooks\*.ps1 run under it) ---------------------
where powershell >nul 2>&1
if errorlevel 1 (
    echo [!] Windows PowerShell not found on PATH. The project's session-start
    echo     and stop hooks are PowerShell scripts and will be skipped without it.
    echo     ^(It ships with Windows 10/11 - check C:\Windows\System32\WindowsPowerShell.^)
) else (
    echo [x] PowerShell is available ^(project hooks can run^).
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

rem ===========================================================================
rem  PART B - AGENTIC WORKFLOW (verify the committed .claude\ config)
rem ===========================================================================
echo ------------------------------------------------------------
echo  PART B : agentic workflow ^(ships in .claude\, auto-loads^)
echo ------------------------------------------------------------

if not exist "%REPO_ROOT%\.claude" (
    echo [!] No .claude\ folder here - is this the DaxAlgo Terminal repo root?
    echo     The agents/skills/hooks live in .claude\ and come WITH the clone.
    echo     Re-clone the repo, then run this from inside it.
    goto :fail
)

set "WF_OK=1"

rem ---- agents (one .md per agent, minus the README) -------------------------
set /a N_AGENTS=0
for %%f in ("%REPO_ROOT%\.claude\agents\*.md") do (
    if /i not "%%~nxf"=="README.md" set /a N_AGENTS+=1
)

rem ---- skills (one directory per skill) -------------------------------------
set /a N_SKILLS=0
for /d %%d in ("%REPO_ROOT%\.claude\skills\*") do set /a N_SKILLS+=1

rem ---- hooks (PowerShell scripts) ------------------------------------------
set /a N_HOOKS=0
for %%f in ("%REPO_ROOT%\.claude\hooks\*.ps1") do set /a N_HOOKS+=1

if %N_AGENTS% GEQ 1 (echo [x] Agents     : %N_AGENTS% subagents in .claude\agents\) else (echo [!] Agents     : none found in .claude\agents\& set "WF_OK=")
if %N_SKILLS% GEQ 1 (echo [x] Skills     : %N_SKILLS% lazy-loaded skills in .claude\skills\) else (echo [!] Skills     : none found in .claude\skills\& set "WF_OK=")
if %N_HOOKS% GEQ 1 (echo [x] Hooks      : %N_HOOKS% PowerShell hooks in .claude\hooks\) else (echo [!] Hooks      : none found in .claude\hooks\& set "WF_OK=")

call :checkfile "%REPO_ROOT%\.claude\settings.json"  "settings.json (shared permissions + hook wiring)"
call :checkfile "%REPO_ROOT%\.claude\MULTI-AGENT.md" "MULTI-AGENT.md (the manager->workers->gate spine)"
call :checkfile "%REPO_ROOT%\CLAUDE.md"              "CLAUDE.md (always-loaded project guide)"

echo.
if defined WF_OK (
    echo  Workflow config is intact. Claude Code loads it automatically when you
    echo  launch it from the repo root - nothing to copy or register globally.
    echo  Personal overrides ^(bypass mode, wider allowlist^) go in the git-ignored
    echo  .claude\settings.local.json - never the shared settings.json.
) else (
    echo  [!] Some workflow config is missing - your clone may be incomplete.
    echo      Try:  git status   and   git checkout -- .claude
)

rem ---- optional: warm up the build so the first Claude run is fast ----------
echo.
echo  Restoring NuGet packages ^(warms the build cache; optional^)...
where dotnet >nul 2>&1 && dotnet restore "%REPO_ROOT%" --nologo 1>nul 2>nul && echo  [x] Packages restored. || echo  [ ] Skipped restore ^(no .NET SDK yet, or run 'dotnet restore' later^).

echo.
echo ============================================================
echo   Setup complete.  Next steps:
echo.
echo   1. CLOSE and RE-OPEN your terminal (so PATH picks up the
echo      newly installed tools).
echo   2. Double-click  claude-here.bat  in the repo root, or run
echo      it from a terminal, to start Claude Code in the project.
echo      It loads CLAUDE.md + the %N_AGENTS% agents / %N_SKILLS% skills / %N_HOOKS% hooks.
echo   3. On first launch, sign in with your Claude Pro / Max /
echo      Team / Enterprise account in the browser.
echo      NOTE: Claude Code is NOT available on the free claude.ai
echo      plan - each contributor needs their own paid plan
echo      (or an API provider key).
echo.
echo   Try it: ask Claude  "load the navigator skill"  or
echo           "have the manager plan adding a notifier".
echo.
echo   Build sanity check (after reopening the terminal):
echo      dotnet build
echo ============================================================
popd
echo.
pause
exit /b 0

rem ---------------------------------------------------------------------------
:checkfile
if exist %1 (
    echo [x] %~2
) else (
    echo [!] MISSING: %~2
    set "WF_OK="
)
goto :eof

:fail
echo.
echo Setup failed.
popd 2>nul
pause
exit /b 1
