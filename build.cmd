@echo off
rem Builds dist\MonitorConfig.exe - one file, needs the .NET 7 Desktop Runtime.
dotnet publish "%~dp0src\MonitorConfig" -c Release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "%~dp0dist"
