@echo off
rem Builds Clueless.exe with the C# compiler that ships with Windows (no SDK needed).
rem First run downloads:
rem   - the WebView2 SDK (Microsoft, nuget.org) that hosts the UI
rem   - whisper.cpp (github.com/ggml-org/whisper.cpp) + English Whisper models (huggingface.co/ggerganov)
rem     for transcribing your microphone offline
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WV=1.0.4191.47
set PKG=%~dp0packages\webview2-%WV%
set WHISPER_TAG=b5454
set WH=%~dp0packages\whisper-1.9.5
set WM=%~dp0packages\whisper-models
set HF=https://huggingface.co/ggerganov/whisper.cpp/resolve/main
cd /d "%~dp0"

if not exist "%PKG%\lib\net462\Microsoft.Web.WebView2.WinForms.dll" (
  echo Downloading WebView2 SDK %WV%...
  mkdir "%PKG%" 2>nul
  curl -sSL -o "%PKG%\webview2.zip" "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/%WV%" || exit /b 1
  tar -xf "%PKG%\webview2.zip" -C "%PKG%" || exit /b 1
)
copy /y "%PKG%\lib\net462\Microsoft.Web.WebView2.Core.dll" . >nul
copy /y "%PKG%\lib\net462\Microsoft.Web.WebView2.WinForms.dll" . >nul
copy /y "%PKG%\runtimes\win-x64\native\WebView2Loader.dll" . >nul

if not exist "%WH%\Release\whisper-server.exe" (
  echo Downloading whisper.cpp %WHISPER_TAG%...
  mkdir "%WH%" 2>nul
  curl -sSL -o "%WH%\whisper-bin-x64.zip" "https://github.com/ggml-org/whisper.cpp/releases/download/%WHISPER_TAG%/whisper-bin-x64.zip" || exit /b 1
  tar -xf "%WH%\whisper-bin-x64.zip" -C "%WH%" || exit /b 1
)
mkdir "%WM%" 2>nul
if not exist "%WM%\ggml-base.en-q5_1.bin" (
  echo Downloading Whisper base.en model...
  curl -sSL -o "%WM%\ggml-base.en-q5_1.bin" "%HF%/ggml-base.en-q5_1.bin" || exit /b 1
)
if not exist "%WM%\ggml-small.en-q5_1.bin" (
  echo Downloading Whisper small.en model ^(for the higher-accuracy setting^)...
  curl -sSL -o "%WM%\ggml-small.en-q5_1.bin" "%HF%/ggml-small.en-q5_1.bin" || exit /b 1
)
mkdir whisper\models 2>nul
copy /y "%WH%\Release\whisper-server.exe" whisper\ >nul
copy /y "%WH%\Release\whisper.dll" whisper\ >nul
copy /y "%WH%\Release\ggml*.dll" whisper\ >nul
if not exist whisper\models\ggml-base.en-q5_1.bin copy /y "%WM%\ggml-base.en-q5_1.bin" whisper\models\ >nul
if not exist whisper\models\ggml-small.en-q5_1.bin copy /y "%WM%\ggml-small.en-q5_1.bin" whisper\models\ >nul

"%FW%\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ /out:Clueless.exe /win32manifest:app.manifest ^
  /r:"%FW%\WPF\UIAutomationClient.dll" /r:"%FW%\WPF\UIAutomationTypes.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Xml.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll ^
  Clueless.cs
if errorlevel 1 exit /b 1
echo Built %~dp0Clueless.exe
