@echo off
dotnet run --configuration Release --project "%~dp0src\GrimList.Desktop\GrimList.Desktop.csproj" -- --data-dir "%~dp0.local"
if errorlevel 1 pause
