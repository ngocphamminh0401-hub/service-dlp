@echo off
rem Build ban JVM (Buoc 2.2): bien dich Java va chep thu vien vao target\libs
call D:\tools\graalvm\graal-env.cmd
cd /d "%~dp0"
call mvn -B -ntp package
