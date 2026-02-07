@echo off
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
echo Building TestApp
echo ========================================
echo.

dotnet build .\TestApp -c Debug

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Failed to build TestApp
    exit /b 1
)

echo.
echo Copying GC DLL to TestApp directory...
copy .\ManagedDotnetGC\bin\Debug\net10.0\win-x64\publish\* .\TestApp\bin\Debug\net10.0\win-x64\

echo.
echo ========================================
echo Running Test Harness
echo ========================================
echo.

@set DOTNET_GCName=ManagedDotnetGC.dll
@set DOTNET_gcConservative=0

.\TestApp\bin\Debug\net10.0\win-x64\TestApp.exe

set TEST_EXIT_CODE=%ERRORLEVEL%

echo.
echo ========================================
echo Test run completed with exit code: %TEST_EXIT_CODE%
echo ========================================

exit /b %TEST_EXIT_CODE%
