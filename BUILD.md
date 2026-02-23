# Building DepotDownloader

DepotDownloader is a .NET 9.0 console application. You can build it from source using the .NET SDK.

## Prerequisites

*   [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later.

## Building from Source

1.  Clone the repository:
    ```bash
    git clone https://github.com/PorcoDio00033/DepotDownloader.git
    cd DepotDownloader
    ```

2.  Build the project:
    ```bash
    dotnet build
    ```

3.  The compiled binaries will be located in `DepotDownloader/bin/Debug/net9.0/` (or `Release` if you built with `-c Release`).

## Publishing

To create a self-contained single-file executable:

### Windows (x64)
```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### Linux (x64)
```bash
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

### macOS (x64)
```bash
dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true
```

### macOS (ARM64 / Apple Silicon)
```bash
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

The published artifacts will be in `DepotDownloader/bin/Release/net9.0/<rid>/publish/`.