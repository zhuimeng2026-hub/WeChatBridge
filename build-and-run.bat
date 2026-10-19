@echo off
REM build-and-run.bat -- double-click wrapper for build-and-run.ps1.
REM
REM Pure ASCII on purpose: cmd reads a .bat as the system ANSI code page
REM (GBK on a Chinese Windows), so a Chinese byte in here is a mangled byte
REM in the console. All the Chinese lives in the .ps1, which is UTF-8 with
REM a BOM -- the same rule the rest of this repository follows.
REM
REM Why a .bat wraps the .ps1 instead of calling it directly: the .ps1 file
REM association is what Windows decides when you double-click, and a fresh
REM Windows install with no developer changes will not run .ps1 on a
REM double-click. A .bat, on the other hand, always runs. The .ps1 stays the
REM source of truth so CI can call it directly; this wrapper only adds the
REM human affordance.
REM
REM Double-clicking this file is the intended way to use it and always works.
REM Typing its bare name in a cmd window does not, on a machine where
REM NoDefaultCurrentDirectoryInExePath=1 -- that setting stops cmd from
REM searching the current directory, so it answers "not recognized as an
REM internal or external command" even though the file is right there. Use
REM ".\build-and-run.bat" there, or call the .ps1 directly.
REM
REM Arguments are passed straight through, so
REM   build-and-run.bat -Register
REM builds AND re-registers the WeChat share entry, and
REM   build-and-run.bat -NoRun
REM builds without starting the app.

setlocal

REM The script lives next to this .bat, so %~dp0 is the repository root.
set "SCRIPT_DIR=%~dp0"
set "PS1=%SCRIPT_DIR%build-and-run.ps1"

if not exist "%PS1%" (
    echo Cannot find %PS1%
    pause
    exit /b 1
)

REM -NoRun builds without starting the app, so the closing line must not
REM claim a window opened. The flag is read here rather than taken on faith
REM because the .ps1 owns the real decision.
set "APP_STARTED=1"
echo %* | find /i "-NoRun" >nul && set "APP_STARTED=0"

REM PowerShell from the standard install path. The wrapper only calls
REM -NoProfile and -ExecutionPolicy Bypass; the .ps1 owns every other flag.
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "EXITCODE=%ERRORLEVEL%"

REM A non-zero exit is the script's own throw surfacing. Surface it to the
REM caller the same way PowerShell would, then pause so a developer who
REM launched this by double-click can read the message before the window
REM disappears.
if not "%EXITCODE%"=="0" (
    echo.
    echo build-and-run.ps1 failed with exit code %EXITCODE%
    pause
    endlocal & exit /b %EXITCODE%
)

REM The app has its own window, so this console has nothing left to show.
REM Pause anyway, so the summary above (the exe path, the data directory,
REM whether the share entry got registered) does not scroll away before it
REM is read. Call the .ps1 directly when running from a shell.
echo.
if "%APP_STARTED%"=="1" (
    echo Build and run finished. The app window is open.
) else (
    echo Build finished. The app was not started.
)
pause
endlocal & exit /b 0
