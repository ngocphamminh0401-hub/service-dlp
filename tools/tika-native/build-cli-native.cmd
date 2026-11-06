@echo off
rem Build ban native cua ExtractCli (Buoc 2.3) - thu nghiem kha thi
call D:\tools\graalvm\graal-env.cmd >nul
cd /d D:\Service-DLP\tools\tika-native
if not exist build mkdir build
call native-image --no-fallback -J-Xmx9g -H:+AddAllCharsets --initialize-at-build-time=org.apache.commons.compress.compressors.xz.XZUtils,org.apache.commons.compress.compressors.lzma.LZMAUtils,org.apache.commons.compress.compressors.zstandard.ZstdUtils,org.apache.commons.compress.compressors.brotli.BrotliUtils -H:ConfigurationFileDirectories=native-config -cp "target\classes;target\libs\*" dlp.tika.ExtractCli -o build\tikacli
