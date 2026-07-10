@echo off
title DaxAlgo Terminal - Claude Code
rem One-click Claude Code session for this repo.
rem cd's to the repo root, then starts Claude Code with an initial prompt that pre-loads
rem the context layer (.claude/context/ index + symbols + deps). CLAUDE.md's pointer and
rem the SessionStart/Stop hooks handle the rest; PROTOCOL.md governs every change request.
cd /d "%~dp0"
where claude >nul 2>nul
if errorlevel 1 (
    echo Claude Code CLI not found on PATH. Install it first: https://claude.com/claude-code
    pause
    exit /b 1
)
claude "Session start: read .claude/context/index.md, .claude/context/symbols.md and .claude/context/deps.json now. Reply with one line confirming the context layer is loaded, then wait for my task. Every change request follows .claude/context/PROTOCOL.md."
