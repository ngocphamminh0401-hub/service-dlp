@echo off
call D:\tools\graalvm\graal-env.cmd >nul
cd /d D:\Service-DLP\tools\tika-native
java -cp "target\classes;target\libs\*" dlp.tika.ExtractCli D:\Service-DLP\tools\tika-bench\corpus D:\Service-DLP\tools\tika-bench\results\files.txt D:\Service-DLP\tools\tika-bench\results\tikajvm
