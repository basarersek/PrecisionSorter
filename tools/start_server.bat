@echo off
REM Local dev server for PrecisionSorter. Oxide is already installed in this folder.
cd /d C:\RustServer\server

RustDedicated.exe -batchmode ^
 +server.identity "precisionsorter" ^
 +server.hostname "PrecisionSorter Dev" ^
 +server.level "Procedural Map" ^
 +server.seed 12345 ^
 +server.worldsize 2000 ^
 +server.maxplayers 50 ^
 +server.port 28015 ^
 +server.queryport 28017 ^
 +rcon.port 28016 ^
 +rcon.password "devpassword" ^
 +rcon.web 1 ^
 +app.port 28018 ^
 +server.saveinterval 300 ^
 -logFile "Server.log"

pause
