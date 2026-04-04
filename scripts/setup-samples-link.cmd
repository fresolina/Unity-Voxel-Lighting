@echo off
setlocal

set "REPO_ROOT=%~dp0.."
set "ASSETS_DIR=%REPO_ROOT%\project-demo\Assets"
set "LINK_NAME=_Samples"
set "LINK_PATH=%ASSETS_DIR%\%LINK_NAME%"
set "TARGET_PATH=..\..\package\Samples~"

if not exist "%ASSETS_DIR%" (
  echo Could not find "%ASSETS_DIR%".
  exit /b 1
)

if exist "%LINK_PATH%" (
  echo Removing existing "%LINK_PATH%"...
  rmdir "%LINK_PATH%" 2>nul
  if exist "%LINK_PATH%" del "%LINK_PATH%" 2>nul
)

pushd "%ASSETS_DIR%"
mklink /D "%LINK_NAME%" "%TARGET_PATH%"
set "EXIT_CODE=%ERRORLEVEL%"
popd

if not "%EXIT_CODE%"=="0" (
  echo Failed to create the symlink.
  echo Enable Windows Developer Mode or run the shell as Administrator, then try again.
  exit /b %EXIT_CODE%
)

echo Created project-demo\Assets\_Samples -^> %TARGET_PATH%