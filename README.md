# MP4/M4A to MP3 Converter

A fast, reliable Windows desktop application for batch converting MP4, M4A, and MP3 files to MP3 using FFmpeg.

## Prerequisites

1.  **FFmpeg**: 
    -   The application **automatically checks** for `ffmpeg.exe` and `ffprobe.exe` on startup.
    -   If they are missing (e.g., first run after cloning), the app will offer to **automatically download and install** them for you.
    -   You can also manually place them in the `Mp3Converter/tools` folder if you prefer.

## How to Build and Run

1.  Open the solution `Mp3Converter.sln` (or project) in Visual Studio or Trae.
2.  Ensure you have .NET 8 SDK installed.
3.  Build the solution:
    ```bash
    dotnet build
    ```
4.  Run the application:
    ```bash
    dotnet run --project Mp3Converter/Mp3Converter.csproj
    ```

## How to Publish (Single Folder)

To create a standalone distribution (easiest for sharing):

```bash
dotnet publish Mp3Converter/Mp3Converter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true
```

The output will be in `Mp3Converter/bin/Release/net8.0-windows/win-x64/publish/`.
Make sure the `tools` folder is present in the publish directory (it should be copied automatically).

## Features

-   **Batch Conversion**: Add multiple files or scan entire folders.
-   **Supports MP4**: Extracts audio from MP4 video files and converts to MP3.
-   **Quality Control**: CBR (Constant Bitrate) and VBR (Variable Bitrate) support.
-   **Progress Tracking**: Real-time progress bars and logs.
-   **Robustness**: Handles skips, cancellations, and errors gracefully.
