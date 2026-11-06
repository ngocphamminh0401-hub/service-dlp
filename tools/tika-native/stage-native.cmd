@echo off
rem Chep tika_native.dll (va cac DLL phu cua JDK) tu thu muc build sang DlpService\native\tika de dong goi khi build/publish voi -p:BundleTikaNative=true
set "SRC=%~dp0build"
set "DST=%~dp0..\..\DlpService\native\tika"
if not exist "%SRC%\tika_native.dll" (echo Chua co %SRC%\tika_native.dll - hay chay build-dll.cmd truoc & exit /b 1)
if not exist "%DST%" mkdir "%DST%"
copy /y "%SRC%\*.dll" "%DST%\" >nul
echo Da chep vao %DST%
powershell -NoProfile -Command "Get-FileHash '%DST%\tika_native.dll' -Algorithm SHA256 | ForEach-Object { 'SHA-256 de ghim vao TikaNative:Sha256: ' + $_.Hash.ToLower() }"
