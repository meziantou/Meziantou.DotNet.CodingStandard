$env:PATH="$HOME/.dotnet/tools:$env:PATH"
& dotnet tool update --global --no-cache depsupdater
& depsupdater update --directory $PSScriptRoot

dotnet run --project $PSScriptRoot/tools/ConfigFilesGenerator/ConfigFilesGenerator.csproj