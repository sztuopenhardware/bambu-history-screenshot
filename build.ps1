$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $fw "WPF"
$csc = Join-Path $fw "csc.exe"
$outExe = Join-Path $root "BambuHistoryScreenshot.exe"

$sources = @(
    (Join-Path $root "src\Program.cs"),
    (Join-Path $root "src\Models.cs"),
    (Join-Path $root "src\NativeMethods.cs"),
    (Join-Path $root "src\BambuUiaScanner.cs"),
    (Join-Path $root "src\BambuCloudClient.cs"),
    (Join-Path $root "src\CloudLoginForm.cs"),
    (Join-Path $root "src\ScreenshotCapturer.cs"),
    (Join-Path $root "src\ExcelExporter.cs"),
    (Join-Path $root "src\MainForm.cs")
)

$argsList = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/utf8output",
    "/out:$outExe",
    "/r:$fw\System.dll",
    "/r:$fw\System.Core.dll",
    "/r:$fw\System.Drawing.dll",
    "/r:$fw\System.IO.Compression.dll",
    "/r:$fw\System.Net.Http.dll",
    "/r:$fw\System.Web.Extensions.dll",
    "/r:$fw\System.Windows.Forms.dll",
    "/r:$wpf\WindowsBase.dll",
    "/r:$wpf\UIAutomationClient.dll",
    "/r:$wpf\UIAutomationTypes.dll"
) + $sources

Write-Host "Building BambuHistoryScreenshot.exe..."
& $csc @argsList
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Build succeeded: $outExe"
Get-Item $outExe | Select-Object Name, Length, LastWriteTime
