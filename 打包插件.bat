@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 936 >nul
cd /d "%~dp0"

REM ============================================================
REM  插件打包 - 生成可直接上传 GitHub Release 的 zip
REM ============================================================
REM  用法：
REM    双击运行，按提示输入插件名和版本号
REM    或命令行：打包插件.bat TeleportSystem 1.2.0
REM  输出：
REM    dist\<插件名>-v<版本号>.zip
REM    tag: <插件名>-v<版本号>
REM ============================================================

echo.
echo  ============================================
echo    插件打包工具
echo  ============================================
echo.

set "NAME=%~1"
set "VER=%~2"

if "%NAME%"=="" set /p NAME=请输入插件名（plugins 下的文件夹名）:
if "%VER%"=="" set /p VER=请输入版本号（例如 1.0.0）:

set "NAME=%NAME: =%"
set "VER=%VER: =%"

if "%NAME%"=="" (
    echo  [错误] 插件名不能为空
    pause
    exit /b 1
)
if "%VER%"=="" (
    echo  [错误] 版本号不能为空
    pause
    exit /b 1
)

echo %VER%| findstr /r "^[0-9][0-9.]*$" >nul
if errorlevel 1 (
    echo  [错误] 版本号格式不正确，只能包含数字和小数点，例如 1.0.0
    pause
    exit /b 1
)

set "SRC_DIR=plugins\%NAME%"
if not exist "%SRC_DIR%\" (
    echo  [错误] 找不到插件目录：%SRC_DIR%
    echo.
    echo  plugins 下现有插件：
    for /d %%d in (plugins\*) do echo    - %%~nxd
    echo.
    pause
    exit /b 1
)

if not exist "%SRC_DIR%\%NAME%.cs" (
    if not exist "%SRC_DIR%\%NAME%.dll" (
        echo  [提示] 未找到 %NAME%.cs / %NAME%.dll，仍将打包该目录下所有文件
        echo.
    )
)

if not exist "dist\" mkdir "dist"
set "ZIP=dist\%NAME%-v%VER%.zip"
set "TAG=%NAME%-v%VER%"

if exist "%ZIP%" (
    echo  已存在：%ZIP%，将覆盖。
    del /f /q "%ZIP%"
)

echo  正在打包 %SRC_DIR% ...
powershell -NoProfile -Command "Compress-Archive -Path '%SRC_DIR%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
    echo.
    echo  [失败] 打包失败，请确认目录内至少有一个文件
    pause
    exit /b 1
)

if not exist "%ZIP%" (
    echo.
    echo  [失败] 未生成 zip 文件
    pause
    exit /b 1
)

for /f "delims=" %%s in ('powershell -NoProfile -Command "[math]::Round((Get-Item '%ZIP%').Length / 1KB, 1)"') do set "SIZE_KB=%%s"

echo.
echo  ============================================
echo   打包完成
echo   文件: %ZIP%  (%SIZE_KB% KB)
echo.
echo   下一步：到 GitHub 仓库新建 Release
echo   Tag:  %TAG%
echo   附件: 上传上面的 zip
echo  ============================================
echo.

set /p OPEN_DIR=是否打开 dist 目录？(Y/N):
if /i "%OPEN_DIR%"=="Y" start "" "dist"

pause
