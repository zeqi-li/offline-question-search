param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $projectRoot 'dist'
$package = Join-Path $dist ('offline-question-search-v' + $Version + '-win-x64')
New-Item -ItemType Directory -Path $package -Force | Out-Null
& (Join-Path $projectRoot 'build.ps1') -OutputPath (Join-Path $package '离线搜题.exe')
Copy-Item -LiteralPath (Join-Path $projectRoot '离线搜题.exe.config'),(Join-Path $projectRoot 'README.md') -Destination $package
& (Join-Path $PSScriptRoot 'New-SampleBank.ps1') -OutputPath (Join-Path $package '演示题库.docx')
@'
解压后双击“离线搜题.exe”。
点击“截图搜题”，手动框选一道题的题干和完整选项。
结果显示选项字母和答案；“结束检测”关闭答案并取消当前识别。

本包只含独立编写的演示题库，不包含用户原题库。
使用自己的题库时，将符合 README 格式的 DOCX 放在程序同级目录。
建议先移走演示题库，再放入自己的题库并重启程序。

需要 Windows 10/11 64 位、.NET Framework 4.8 和简体中文 OCR 组件。
识别和检索在本地完成；程序不调用网络服务。
'@ | Set-Content -LiteralPath (Join-Path $package '先读我.txt') -Encoding UTF8
$archive = $package + '.zip'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archiveStream = [System.IO.File]::Open($archive, [System.IO.FileMode]::Create)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($archiveStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $allowedFiles = @('离线搜题.exe', '离线搜题.exe.config', '演示题库.docx', 'README.md', '先读我.txt')
        foreach ($name in $allowedFiles) {
            $entryName = [System.IO.Path]::GetFileName($package) + '/' + $name
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $package $name), $entryName)
        }
    } finally { $zip.Dispose() }
} finally { $archiveStream.Dispose() }
$hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
($hash.Hash.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
Write-Output ('Release ZIP: ' + $archive)
