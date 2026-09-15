@echo off
title AzerothManager - Build
setlocal

echo ========================================
echo   AzerothCore Admin Manager - Build
echo ========================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERREUR : dotnet SDK introuvable dans le PATH.
    echo Installez le SDK .NET 10 : https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

set PROJECT_DIR=%~dp0AzerothManager
set OUTPUT_DIR=%~dp0publish

echo [1/1] Compilation + publication...
dotnet publish "%PROJECT_DIR%\AzerothManager.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:PublishReadyToRun=true ^
    -o "%OUTPUT_DIR%"

if errorlevel 1 (
    echo.
    echo ERREUR de compilation !
    pause
    exit /b 1
)

echo.
echo ========================================
echo   BUILD REUSSI !
echo ========================================
echo   Executable : %OUTPUT_DIR%\AzerothManager.exe
echo ========================================
echo.

explorer "%OUTPUT_DIR%"
pause
