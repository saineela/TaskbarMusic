@echo off
echo Building TaskbarMusic exe...

dotnet restore TaskbarMusic.csproj || exit /b 1
dotnet publish TaskbarMusic.csproj -c Release -o builds --runtime win-x64 --self-contained true

if %errorlevel% equ 0 (
    echo.
    echo Build complete! Output: builds\TaskbarMusic.exe
    dir builds\*.exe
) else (
    echo Build failed!
    exit /b 1
)
