dotnet publish .\ManagedDotnetGC /p:SelfContained=true -r win-x64 -c Debug

copy .\ManagedDotnetGC\bin\Debug\net10.0\win-x64\publish\* .\TestApp\bin\Debug\net10.0\win-x64\