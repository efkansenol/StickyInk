@echo off
rem Builds StickyInk and runs its end-to-end self-test (about 7 seconds).
rem A test window appears, gets scrolled, and closes by itself. Do not cover it while it runs.
rem Prints a line starting with PASS or FAIL. Exit code 0 = pass.
setlocal
call "%~dp0build.cmd" >nul
if errorlevel 1 (
    echo BUILD FAILED - run build.cmd to see the errors.
    exit /b 1
)
taskkill /im StickyInk.exe /f >nul 2>&1
del "%~dp0bin\selftest.txt" >nul 2>&1
start "" /wait "%~dp0bin\StickyInk.exe" --selftest
if not exist "%~dp0bin\selftest.txt" (
    echo FAIL - the self-test did not produce a result.
    exit /b 1
)
type "%~dp0bin\selftest.txt"
echo.
findstr /b "PASS" "%~dp0bin\selftest.txt" >nul
exit /b %errorlevel%
