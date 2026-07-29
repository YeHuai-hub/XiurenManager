@echo off
chcp 65001 >nul
cd /d "%~dp0"
start "" "%~dp0publish-v3\XiurenManager.exe"
