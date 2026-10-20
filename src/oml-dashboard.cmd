@echo off
rem Double-click to open the oml dashboard in your browser. Close this window, or press Ctrl+C, to stop it.
"%~dp0oml.exe" dashboard %*
if errorlevel 1 pause
