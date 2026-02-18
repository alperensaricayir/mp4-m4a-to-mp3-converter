$project = "Mp3Converter/Mp3Converter.csproj"
$outDir = "PublishOutput"

Write-Host "Publishing application to $outDir..."

# Remove old output
if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}

# Publish command
# --self-contained true: Includes .NET runtime, so target machine doesn't need it installed.
# -p:PublishSingleFile=false: Keeps files separate (easier for debugging and tools folder).
# -r win-x64: Targets Windows 64-bit.
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $outDir

# Verify tools
if ((Test-Path "$outDir/tools/ffmpeg.exe") -and (Test-Path "$outDir/tools/ffprobe.exe")) {
    Write-Host "SUCCESS: Tools (ffmpeg/ffprobe) were copied successfully." -ForegroundColor Green
    Write-Host "You can zip the '$outDir' folder and send it to another computer." -ForegroundColor Cyan
} else {
    Write-Host "WARNING: Tools folder might be missing or incomplete." -ForegroundColor Yellow
}
