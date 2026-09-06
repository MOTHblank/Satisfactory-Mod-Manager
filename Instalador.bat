@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "APPVER=0.5.3"
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=win-x64"

echo =============================================
echo Satisfactory Mod Manager v%APPVER% - Publish
echo =============================================
echo.
echo Uso: publish.bat [win-x64^|win-x86^|win-arm64^|linux-x64^|all]
echo Alvo selecionado: %TARGET%
echo.

if /i "%TARGET%"=="linux-x64" goto :explain_linux
if /i "%TARGET%"=="linux" goto :explain_linux

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERRO: o comando 'dotnet' nao foi encontrado.
  echo Instale o .NET 8 SDK e execute este arquivo novamente.
  pause
  exit /b 1
)

echo [1/4] Verificando .NET SDK...
dotnet --version
echo.

if /i "%TARGET%"=="all" (
  call :build win-x64
  call :build win-x86
  call :build win-arm64
  goto :explain_linux_short
)

call :build %TARGET%
goto :end

:build
set "RID=%~1"
echo [2/4] Limpando saida anterior de %RID%...
if exist "bin\Release" rmdir /s /q "bin\Release"
if exist "obj\Release" rmdir /s /q "obj\Release"
if exist "publish-final-%RID%" rmdir /s /q "publish-final-%RID%"
if exist "SatisfactoryModManager-v%APPVER%-%RID%.zip" del /q "SatisfactoryModManager-v%APPVER%-%RID%.zip"
echo.

echo [3/4] Publicando para %RID%...
dotnet publish "SatisfactoryModManager.csproj" -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if errorlevel 1 (
  echo.
  echo ERRO: publicacao de %RID% falhou.
  exit /b 1
)
echo.

echo [4/4] Preparando pacote final de %RID%...
mkdir "publish-final-%RID%"
copy /y "bin\Release\net8.0-windows\%RID%\publish\SatisfactoryModManager.exe" "publish-final-%RID%\SatisfactoryModManager.exe" >nul
copy /y "README.md" "publish-final-%RID%\README.txt" >nul
copy /y "VERSION.txt" "publish-final-%RID%\VERSION.txt" >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'publish-final-%RID%\*' -DestinationPath 'SatisfactoryModManager-v%APPVER%-%RID%.zip' -Force"
if errorlevel 1 (
  echo AVISO: o EXE de %RID% foi gerado, mas nao foi possivel criar o ZIP.
  echo EXE: %CD%\publish-final-%RID%\SatisfactoryModManager.exe
  exit /b 1
)

echo Concluido: %CD%\SatisfactoryModManager-v%APPVER%-%RID%.zip
echo.
exit /b 0

:explain_linux
echo =============================================
echo Sobre um build para Linux
echo =============================================
echo.
echo Este aplicativo usa Windows Forms ^(UseWindowsForms=true, net8.0-windows^),
echo que depende do runtime "Windows Desktop" e so existe no Windows. Por isso
echo "dotnet publish -r linux-x64" deste projeto NAO compila: nao ha System.Windows.Forms
echo para Linux, entao nao seria um .exe/binario funcional com interface grafica.
echo.
echo Alternativas reais, caso voce precise de Linux:
echo  1^) Rodar o .exe publicado para Windows sob Wine/Proton no Linux
echo     ^(a interface e o instalador de mods funcionam bem sob Wine^).
echo  2^) Reescrever a interface com um framework multiplataforma ^(ex.: Avalonia UI^),
echo     o que e uma mudanca maior de arquitetura, fora do escopo deste script.
echo.
echo Nenhum arquivo foi gerado para "linux-x64" por esse motivo.
if /i "%TARGET%"=="linux-x64" goto :end
if /i "%TARGET%"=="linux" goto :end

:explain_linux_short
echo.
echo Observacao: build para Linux nao e suportado por este projeto WinForms.
echo Veja "publish.bat linux-x64" para o motivo detalhado.

:end
echo.
echo =============================================
echo Processo finalizado.
echo =============================================
pause
endlocal
