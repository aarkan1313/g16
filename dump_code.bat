@echo off
setlocal enabledelayedexpansion
REM Concatenate all project source (C# + shaders) into one text file.
REM Excludes generated/build + worktree copies (.godot, .claude, bin, obj, .mono).
set "ROOT=%~dp0"
set "OUT=%ROOT%ALL_CODE.txt"
if exist "%OUT%" del "%OUT%"

set "COUNT=0"
for /f "delims=" %%F in ('dir /s /b "%ROOT%*.cs" "%ROOT%*.gdshader" "%ROOT%*.gdshaderinc" "%ROOT%*.glsl" "%ROOT%*.gd" 2^>nul') do (
    set "SKIP="
    echo %%F| find /i "\.godot" >nul && set "SKIP=1"
    echo %%F| find /i "\.claude" >nul && set "SKIP=1"
    echo %%F| find /i "\.mono" >nul && set "SKIP=1"
    echo %%F| find /i "\bin\Debug" >nul && set "SKIP=1"
    echo %%F| find /i "\obj\Debug" >nul && set "SKIP=1"
    if not defined SKIP (
        set /a COUNT+=1
        echo ========================================================================>>"%OUT%"
        echo FILE: %%F>>"%OUT%"
        echo ========================================================================>>"%OUT%"
        type "%%F">>"%OUT%"
        echo.>>"%OUT%"
        echo.>>"%OUT%"
    )
)

echo Done. !COUNT! files written to "%OUT%"
endlocal
