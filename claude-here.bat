@echo off
rem ============================================================================
rem  Launch Claude Code inside the DaxAlgo Terminal project.
rem
rem  Double-click it, or run it from a terminal. It cd's to the repo root (this
rem  file's own folder) so Claude Code loads the project's .claude/ config -
rem  agents, skills, hooks, CLAUDE.md - and any arguments you pass are forwarded
rem  (e.g.  claude-here.bat --resume  or  claude-here.bat --continue).
rem
rem  First time? Run scripts\setup-claude-code.bat once, then sign in on the
rem  first launch. Claude Code needs a paid Claude plan (not the free tier).
rem ============================================================================
title DaxAlgo Terminal - Claude Code
cd /d "%~dp0"

where claude >nul 2>&1
if errorlevel 1 (
    if exist "%USERPROFILE%\.local\bin\claude.exe" (
        rem Native install isn't on PATH in this fresh session - add it.
        set "PATH=%USERPROFILE%\.local\bin;%PATH%"
    ) else (
        echo Claude Code is not installed yet.
        echo Run  scripts\setup-claude-code.bat  first, then reopen your terminal.
        echo.
        pause
        exit /b 1
    )
)

claude %*
