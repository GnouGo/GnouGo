param(
    [Parameter(Mandatory)][string]$InputFile,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$source = (Get-Item -LiteralPath $InputFile).FullName
$destination = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $destination) { throw 'Use a fresh output file.' }
Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
$existing = @(Get-Process POWERPNT -ErrorAction SilentlyContinue)
$app = New-Object -ComObject PowerPoint.Application
$deck = $null
$book = $null
try {
    $deck = $app.Presentations.Open($destination, 0, 0, 0)
    $charts = @($deck.Slides.Item(1).Shapes | Where-Object { $_.HasChart -eq -1 })
    if ($charts.Count -ne 1) { throw 'Expected one native chart.' }
    $chart = $charts[0].Chart
    $chart.ChartData.Activate()
    $book = $chart.ChartData.Workbook
    $cell = $book.Worksheets.Item(1).Range('B3')
    if ([double]$cell.Value2 -ne 11) { throw 'Unexpected worksheet value before editing.' }
    $cell.Value2 = [double]13
    $chart.Refresh()
    $book.Close($true)
    $book = $null
    $deck.Save()
    $deck.Close()
    $deck = $app.Presentations.Open($destination, -1, 0, 0)
    $chart = @($deck.Slides.Item(1).Shapes | Where-Object { $_.HasChart -eq -1 })[0].Chart
    $chart.ChartData.Activate()
    $book = $chart.ChartData.Workbook
    $observed = [double]$book.Worksheets.Item(1).Range('B3').Value2
    if ($observed -ne 13) { throw 'The exported chart worksheet lost the edit on reopen.' }
    [ordered]@{ application = $app.Name; version = $app.Version; build = $app.Build
        cell = 'Sheet1!B3'; initial = 11; reopened = $observed
        source_sha256 = (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
        edited_sha256 = (Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() } | ConvertTo-Json
} finally {
    if ($null -ne $book) { $book.Close($false) }
    if ($null -ne $deck) { $deck.Close() }
    if ($existing.Count -eq 0 -and $app.Presentations.Count -eq 0) { $app.Quit() }
}
