$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $projectDir 'CodexUsageOrb.exe'
$source = Join-Path $projectDir 'Program.cs'
$references = @(
    '/reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll',
    '/reference:C:\Windows\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll',
    '/reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Core.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll',
    '/reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll'
)

& $compiler /nologo /target:winexe /optimize+ /platform:anycpu "/out:$output" @references $source
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
Write-Host "Build complete: $output"
