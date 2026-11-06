@echo off
rem [Thay doi] Build DLL THI NGHIEM ra thu muc rieng - KHONG dung toi build\ (ban dang deploy).
rem Tham so: %1 = heap toi da (MB, mac dinh 256), %2 = young gen (%% cua heap, mac dinh 10), %3 = thu muc ra (mac dinh build-exp).
rem Cong tac thi nghiem luc chay: DLP_TIKA_EXP_AUTODETECT, DLP_TIKA_EXP_DOM, DLP_TIKA_EXP_NFC, DLP_TIKA_EXP_GCSTATS (=1).
call D:\tools\graalvm\graal-env.cmd >nul
cd /d D:\Service-DLP\tools\tika-native
set "HEAP=%~1"
if "%HEAP%"=="" set "HEAP=256"
set "YOUNG=%~2"
if "%YOUNG%"=="" set "YOUNG=10"
set "OUT=%~3"
if "%OUT%"=="" set "OUT=build-exp"
if not exist %OUT% mkdir %OUT%
call native-image --shared --no-fallback -J-Xmx9g -H:+AddAllCharsets -R:MaxHeapSize=%HEAP%m -R:MaximumYoungGenerationSizePercent=%YOUNG% --initialize-at-build-time=org.apache.commons.compress.compressors.xz.XZUtils,org.apache.commons.compress.compressors.lzma.LZMAUtils,org.apache.commons.compress.compressors.zstandard.ZstdUtils,org.apache.commons.compress.compressors.brotli.BrotliUtils -H:ConfigurationFileDirectories=native-config -cp "target\classes;target\libs\*" -o %OUT%\tika_native
