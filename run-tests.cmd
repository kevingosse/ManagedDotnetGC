@echo off
setlocal enabledelayedexpansion

REM Default values
set CONFIG=Release
set TEST_NAME=

REM Parse command-line arguments
:parse_args
if "%~1"=="" goto end_parse_args
if /i "%~1"=="--debug" (
    set CONFIG=Debug
    shift
    goto parse_args
)
if /i "%~1"=="--test" (
    set TEST_NAME=%~2
    shift
    shift
    goto parse_args
)
echo Unknown argument: %~1
echo.
echo Usage: run-tests.cmd [--debug] [--test TEST_NAME]
echo   --debug      Build TestApp in Debug configuration (default: Release)
echo   --test NAME  Run only the specified test (case-insensitive)
exit /b 1

:end_parse_args

echo ========================================
echo Building and Publishing ManagedDotnetGC
echo ========================================
echo.

dotnet publish .\ManagedDotnetGC /p:SelfContained=true -r win-x64 -c Debug

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Failed to publish ManagedDotnetGC
    exit /b 1
)

echo.
echo ========================================
echo Building TestApp (Configuration: %CONFIG%)
echo ========================================
echo.

dotnet build .\TestApp -c %CONFIG%

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Failed to build TestApp
    exit /b 1
)

echo.
echo Copying GC DLL to TestApp directory...
copy .\ManagedDotnetGC\bin\Debug\net10.0\win-x64\publish\* .\TestApp\bin\%CONFIG%\net10.0\win-x64\

echo.
echo ========================================
echo Running Tests
echo ========================================
echo.

@set DOTNET_GCName=ManagedDotnetGC.dll
@set DOTNET_gcConservative=0

if not "%TEST_NAME%"=="" (
    echo Running single test: %TEST_NAME%
    echo.
    .\TestApp\bin\%CONFIG%\net10.0\win-x64\TestApp.exe "%TEST_NAME%"
) else (
    echo Running all tests
    echo.
    .\TestApp\bin\%CONFIG%\net10.0\win-x64\TestApp.exe
)

set TEST_EXIT_CODE=%ERRORLEVEL%

echo.
echo ========================================
echo Test run completed with exit code: %TEST_EXIT_CODE%
echo ========================================

exit /b %TEST_EXIT_CODE%
