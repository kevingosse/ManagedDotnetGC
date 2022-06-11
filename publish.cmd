dotnet publish .\ManagedDotnetGC /p:NativeLib=Shared /p:SelfContained=true -r win-x64 -c Release

copy .\ManagedDotnetGC\bin\Release\net6.0\win-x64\publish\* .\TestApp\bin\Debug\net6.0\win-x64\