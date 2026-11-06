@echo off
rem Build thu vien native (DLL). Tham so tuy chon 1 = gioi han heap Java toi da (MB), vd:  build-dll.cmd 512
call D:\tools\graalvm\graal-env.cmd >nul
cd /d D:\Service-DLP\tools\tika-native
if not exist build mkdir build
set "HEAPARG="
if not "%~1"=="" set "HEAPARG=-R:MaxHeapSize=%~1m"
call native-image --shared --no-fallback -J-Xmx9g -H:+AddAllCharsets %HEAPARG% --initialize-at-build-time=org.apache.commons.compress.compressors.xz.XZUtils,org.apache.commons.compress.compressors.lzma.LZMAUtils,org.apache.commons.compress.compressors.zstandard.ZstdUtils,org.apache.commons.compress.compressors.brotli.BrotliUtils -H:ConfigurationFileDirectories=native-config -cp "target\classes;target\libs\*" -o build\tika_native
