param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $projectRoot 'examples\demo-bank.docx' }
$questions = Get-Content -LiteralPath (Join-Path $projectRoot 'examples\sample-questions.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$paragraphs = [System.Collections.Generic.List[string]]::new()
$paragraphs.Add('离线搜题演示题库')
$paragraphs.Add('本文件是独立编写的功能演示数据，不包含用户原题库。')
$section = ''
foreach ($question in $questions) {
    if ($section -ne $question.section) { $section = $question.section; $paragraphs.Add($section) }
    $paragraphs.Add([string]$question.number + '、' + $question.stem)
    for ($i = 0; $i -lt $question.options.Count; $i++) {
        $paragraphs.Add([string][char](65 + $i) + '、' + $question.options[$i])
    }
    $paragraphs.Add('答案：' + $question.answer)
}
$body = [System.Text.StringBuilder]::new()
foreach ($paragraph in $paragraphs) {
    $escaped = [System.Security.SecurityElement]::Escape($paragraph)
    [void]$body.Append('<w:p><w:pPr><w:spacing w:after="80"/></w:pPr><w:r><w:rPr><w:rFonts w:ascii="Calibri" w:eastAsia="Microsoft YaHei"/><w:sz w:val="22"/></w:rPr><w:t xml:space="preserve">' + $escaped + '</w:t></w:r></w:p>')
}
$document = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>' + $body.ToString() + '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1000" w:right="1000" w:bottom="1000" w:left="1000"/></w:sectPr></w:body></w:document>'
$types = '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
$relationships = '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$parent = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @{ '[Content_Types].xml' = $types; '_rels/.rels' = $relationships; 'word/document.xml' = $document }
        foreach ($name in $entries.Keys) {
            $entry = $zip.CreateEntry($name)
            $writer = [System.IO.StreamWriter]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false))
            try { $writer.Write($entries[$name]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Write-Output ('Created demo bank: ' + $OutputPath)
