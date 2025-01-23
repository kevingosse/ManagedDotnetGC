dotnet publish .\ManagedDotnetGC /p:SelfContained=true -r win-x64 -c Release

copy .\ManagedDotnetGC\bin\Release\net9.0\win-x64\publish\* .\TestApp\bin\Debug\net9.0\win-x64\