@echo off
call D:\tools\graalvm\graal-env.cmd >nul
cd /d D:\Service-DLP\tools\tika-native
if exist native-config rmdir /s /q native-config
java -agentlib:native-image-agent=config-output-dir=native-config -cp "target\classes;target\libs\*" dlp.tika.ExtractCli D:\ D:\Service-DLP\tools\tika-bench\results\train-list.txt D:\Service-DLP\tools\tika-bench\results\agentrun
