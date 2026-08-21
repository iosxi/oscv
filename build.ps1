# oscv をビルドして、README.txt と一緒に zip にする。
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# 出来上がるもの:
#   oscv.exe                  そのまま動く実行ファイル (ショートカットの向き先)
#   dist\oscv-<版>.zip        配布用 (oscv-<版>\ の中に oscv.exe と README.txt)
#
# 古い zip は新しい方から KEEP_ZIPS 個だけ残し、それより古いものは消す。

$ErrorActionPreference = 'Stop'
#: dist に残しておく配布物の数。1 つ前の版に戻れる余地は持たせておく
$KEEP_ZIPS = 3
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw 'csc.exe が見つかりません。.NET Framework 4 が入っていない環境です。'
}

# 動いている exe は上書きできない。先に気づかないと、古い exe が新しい
# 版番号の zip に入ってしまう
$running = Get-Process -Name oscv -ErrorAction SilentlyContinue
if ($running) {
    throw "oscv が起動中です (PID $($running.Id -join ', '))。終了してからビルドしてください。"
}

# 版番号は src\oscv.cs の App.Version を唯一の出どころにする (v1 から 1 ずつ)
$src = Join-Path $root 'src\oscv.cs'
$m = [regex]::Match([IO.File]::ReadAllText($src), 'public const int Version\s*=\s*(\d+)\s*;')
if (-not $m.Success) { throw "src\oscv.cs に App.Version が見つかりません。" }
$version = 'v' + $m.Groups[1].Value
Write-Host "oscv $version をビルドします"

$exe = Join-Path $root 'oscv.exe'
$dist = Join-Path $root 'dist'
$work = Join-Path $root 'build'
$started = Get-Date

# ico は 2 か所で使う。win32icon = エクスプローラーやショートカットに出る exe の顔、
# resource = 実行時にウィンドウとタスクバーへ出すもの (oscv.cs の SetAppIcon が読む)。
# 絵を差し替えるときは tools\make-icon.ps1 で ico を作り直す
$icon = Join-Path $root 'assets\oscv.ico'
if (-not (Test-Path $icon)) { throw "assets\oscv.ico がありません。tools\make-icon.ps1 で作ってください。" }

& $csc /nologo /target:winexe /optimize+ /codepage:65001 `
    /out:$exe `
    /win32icon:$icon `
    /resource:"$icon,oscv.ico" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $src
if ($LASTEXITCODE -ne 0) { throw "コンパイルに失敗しました (終了コード $LASTEXITCODE)。" }
if (-not (Test-Path $exe)) { throw 'exe が生成されませんでした。' }
# 前回の exe が残っているだけ、という取り違えを防ぐ
if ((Get-Item $exe).LastWriteTime -lt $started) {
    throw "exe が更新されていません。前回のものが残っています: $exe"
}

New-Item -ItemType Directory -Force $dist | Out-Null

# zip の中は oscv-<版>\ の 1 階層にまとめる (展開時に散らからないように)
$stage = Join-Path $work "package\oscv-$version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $exe $stage

# README.txt の版番号は置換で埋める (oscv.cs と二重管理にしないため)
$readme = [IO.File]::ReadAllText((Join-Path $root 'README.txt'))
if ($readme -notmatch '@VERSION@') { throw 'README.txt に @VERSION@ がありません。' }
$readme = $readme -replace '@VERSION@', $version.TrimStart('v')
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme, (New-Object Text.UTF8Encoding $true))

$zip = Join-Path $dist "oscv-$version.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $stage -DestinationPath $zip

# 古い配布物は溜め込まない。名前順だと v10 が v9 より前に来てしまうので、
# 作られた順で見る
$stale = Get-ChildItem -Path $dist -Filter 'oscv-v*.zip' |
    Sort-Object LastWriteTime -Descending | Select-Object -Skip $KEEP_ZIPS
foreach ($file in $stale) {
    Write-Host "古い配布物を消します: $($file.Name)"
    Remove-Item -Force $file.FullName
}

Write-Host ''
Write-Host '完了しました:'
Write-Host "  $exe"
Write-Host "  $zip"
