$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'src\windows\Program.cs'
$outputDir = Join-Path $repoRoot 'dist\windows'
$output = Join-Path $outputDir 'CodexUsageOrb.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$references = @(
    '/reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll',
    '/reference:C:\Windows\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll',
    '/reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Core.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll'
)

if (-not (Test-Path $source)) { throw "Source file not found: $source" }
if (-not (Test-Path $compiler)) { throw "C# compiler not found: $compiler" }

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/out:$output" @references $source
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

Write-Host "Build complete: $output"
