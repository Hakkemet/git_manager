@echo off
echo Compilando GitManager.exe...
dotnet build src/GitManager/GitManager.csproj -c Debug
copy /Y "src\GitManager\bin\Debug\net8.0-windows\GitManager.*" . >nul
if not exist "Locales" mkdir "Locales"
copy /Y "src\GitManager\Locales\*.json" "Locales\" >nul
echo Concluido! GitManager.exe e arquivos Locales atualizados na raiz do projeto.
pause
