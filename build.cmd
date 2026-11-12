@echo off
rem Builds bin\StickyInk.exe. Needs the .NET SDK (https://dotnet.microsoft.com/download).
dotnet build "%~dp0StickyInk.csproj" -c Release -nologo -v quiet
if errorlevel 1 exit /b 1
echo.
echo Built: %~dp0bin\StickyInk.exe
