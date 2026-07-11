@echo off
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /win32icon:icon.ico /out:copy-to-ssh.exe copy-to-ssh.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo built copy-to-ssh.exe
