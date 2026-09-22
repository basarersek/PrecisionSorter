@echo off
REM Local test server for PrecisionSorter. Oxide is installed in this folder.
REM Small world so the client joins fast. Connect with: client.connect 127.0.0.1:28015
cd /d C:\RustServer\server

RustDedicated.exe -batchmode ^
 +server.identity "precisionsorter" ^
 +server.hostname "PrecisionSorter Test" ^
 +server.level "Procedural Map" ^
 +server.seed 12345 ^
 +server.worldsize 1000 ^
 +server.maxplayers 20 ^
 +server.port 28015 ^
 +server.queryport 28017 ^
 +rcon.port 28016 ^
 +rcon.password "devpassword" ^
 +rcon.web 1 ^
 +app.port 28018 ^
 +server.saveinterval 300 ^
 +craft.instant true ^
 -logFile "Server.log"

pause
