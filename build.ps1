param([switch]$Fixture, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$metadata = Join-Path $env:WINDIR 'System32\WinMetadata'
$references = @(
    'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Xml.dll', 'System.Xml.Linq.dll', 'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll', 'System.Web.Extensions.dll',
    (Join-Path $framework 'System.Runtime.WindowsRuntime.dll'),
    (Join-Path $framework 'System.Runtime.dll'),
    (Join-Path $framework 'System.ObjectModel.dll'),
    (Join-Path $framework 'System.Collections.dll'),
    (Join-Path $framework 'System.Threading.Tasks.dll'),
    (Join-Path $framework 'System.Runtime.InteropServices.dll'),
    (Join-Path $framework 'System.Runtime.InteropServices.WindowsRuntime.dll'),
    (Join-Path $metadata 'Windows.Foundation.winmd'),
    (Join-Path $metadata 'Windows.Globalization.winmd'),
    (Join-Path $metadata 'Windows.Media.winmd'),
    (Join-Path $metadata 'Windows.Graphics.winmd'),
    (Join-Path $metadata 'Windows.Storage.winmd')
)
$destination = Join-Path $root '离线搜题.exe'
$entry = 'OfflineQuestions.Program'
if ($Fixture) { $destination = Join-Path $root 'verification\题面验证.exe'; $entry = 'OfflineQuestions.FixtureProgram' }
if ($OutputPath) { $destination = $OutputPath }
$argsList = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output',
    ('/out:' + $destination), ('/main:' + $entry),
    ('/win32manifest:' + (Join-Path $root 'src\app.manifest')))
$argsList += $references | ForEach-Object { '/reference:' + $_ }
$argsList += Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& (Join-Path $framework 'csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
