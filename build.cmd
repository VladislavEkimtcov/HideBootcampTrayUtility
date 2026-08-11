@echo off
setlocal

rem Builds HideBootcampTrayUtility with the MSBuild that ships inside Windows itself.
rem No .NET SDK, no Visual Studio, no NuGet restore -- this works on a bare
rem Windows IoT Enterprise LTSC image with nothing installed.
rem
rem MSB3644 warnings ("reference assemblies not found") are expected: with no
rem targeting packs present MSBuild resolves the references from the GAC instead,
rem and the build succeeds.

set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not exist "%MSBUILD%" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"

if not exist "%MSBUILD%" (
    echo Could not find the in-box MSBuild 4.0. Is .NET Framework 4 installed?
    exit /b 1
)

"%MSBUILD%" "%~dp0HideBootcampTrayUtility.csproj" /nologo /v:minimal /p:Configuration=Release %*
if errorlevel 1 exit /b 1

echo.
echo Built: %~dp0bin\HideBootcampTrayUtility.exe
endlocal
