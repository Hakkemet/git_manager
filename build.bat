@echo off
echo Compilando GitManager.exe...
dotnet build src/GitManager/GitManager.csproj -c Debug
copy /Y "src\GitManager\bin\Debug\net8.0-windows\GitManager.*" . >nul
echo Concluido! GitManager.exe atualizado na raiz do projeto.
pause
