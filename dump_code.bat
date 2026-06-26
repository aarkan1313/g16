@echo off
setlocal enabledelayedexpansion
REM ============================================================================
REM  Concatenate project SOURCE (C# + shaders) into ALL_CODE.txt for code review.
REM
REM  DEFAULT (no args) = JUST THE CODE: drops build/generated output, the test
REM  scaffolding, and the peripheral subsystems (cloud/sky, water, material) so
REM  you get the core terrain/lab/lighting code only.
REM
REM  ADD groups back with flags (combine freely):
REM     dump_code.bat                 lean core code only  (default)
REM     dump_code.bat all             EVERYTHING (only build dirs excluded)
REM     dump_code.bat withcloud       + cloud / sky / atmosphere / luminary
REM     dump_code.bat withwater       + water / hydrology
REM     dump_code.bat withmaterial    + material board
REM     dump_code.bat withchecks      + the *Check / test scaffolding
REM     e.g.:  dump_code.bat withcloud withchecks
REM ============================================================================
set "ROOT=%~dp0"
set "OUT=%ROOT%ALL_CODE.txt"
if exist "%OUT%" del "%OUT%"

REM --- path fragments skipped entirely: build / generated / vcs / worktree copies ---
set "EXCLUDE_PATHS=\.godot \.claude \.mono \.git \.vs \bin\ \obj\"

REM --- groups excluded BY DEFAULT (cleared by the matching with-flag) ---
set "EX_CHECKS=1"
set "EX_CLOUD=1"
set "EX_WATER=1"
set "EX_MATERIAL=1"
for %%A in (%*) do (
    if /i "%%A"=="withchecks"   set "EX_CHECKS="
    if /i "%%A"=="withcloud"    set "EX_CLOUD="
    if /i "%%A"=="withwater"    set "EX_WATER="
    if /i "%%A"=="withmaterial" set "EX_MATERIAL="
    if /i "%%A"=="all"  set "EX_CHECKS="
    if /i "%%A"=="all"  set "EX_CLOUD="
    if /i "%%A"=="all"  set "EX_WATER="
    if /i "%%A"=="all"  set "EX_MATERIAL="
    if /i "%%A"=="full" set "EX_CHECKS="
    if /i "%%A"=="full" set "EX_CLOUD="
    if /i "%%A"=="full" set "EX_WATER="
    if /i "%%A"=="full" set "EX_MATERIAL="
)

REM --- build the name-exclude list from the active groups ---
set "EXCLUDE_NAMES="
set "INC="
if defined EX_CHECKS   ( set "EXCLUDE_NAMES=!EXCLUDE_NAMES! Check TestPaths SnapDiff LivePopMeter" ) else ( set "INC=!INC! +checks" )
if defined EX_CLOUD    ( set "EXCLUDE_NAMES=!EXCLUDE_NAMES! Cloud Godray Atmosphere Aerial Luminary SkyPresets" ) else ( set "INC=!INC! +cloud" )
if defined EX_WATER    ( set "EXCLUDE_NAMES=!EXCLUDE_NAMES! \hydrology\" ) else ( set "INC=!INC! +water" )
if defined EX_MATERIAL ( set "EXCLUDE_NAMES=!EXCLUDE_NAMES! MaterialBoard" ) else ( set "INC=!INC! +material" )
if not defined INC set "INC= (lean core only)"
echo Mode: default!INC!

set "COUNT=0"
set "SKIPPED=0"
for /f "delims=" %%F in ('dir /s /b "%ROOT%*.cs" "%ROOT%*.gdshader" "%ROOT%*.gdshaderinc" "%ROOT%*.glsl" "%ROOT%*.gd" 2^>nul') do (
    set "F=%%F"
    set "SKIP="
    for %%P in (!EXCLUDE_PATHS!) do if not defined SKIP if /i not "!F:%%P=!"=="!F!" set "SKIP=1"
    if defined EXCLUDE_NAMES for %%N in (!EXCLUDE_NAMES!) do if not defined SKIP if /i not "!F:%%N=!"=="!F!" set "SKIP=1"
    if defined SKIP (
        set /a SKIPPED+=1
    ) else (
        set /a COUNT+=1
        echo ========================================================================>>"%OUT%"
        echo FILE: !F!>>"%OUT%"
        echo ========================================================================>>"%OUT%"
        type "!F!">>"%OUT%"
        echo.>>"%OUT%"
        echo.>>"%OUT%"
    )
)

echo Done. !COUNT! files written, !SKIPPED! skipped -^> "%OUT%"
endlocal
