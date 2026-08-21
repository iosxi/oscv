# assets\icon_SRC.png から assets\oscv.ico を作る。
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#
# 元絵は角丸の外側が「黒で塗られた不透明 PNG」なので、そのままでは
# アイコンの周りに黒い四角が出る。角丸の内側だけを残すマスクを自前で
# かけて、外側を透明にしてから各サイズに縮小する。
#
# 絵を描き直したときだけ実行する。ビルド時には走らない (ico をコミットしてある)。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$srcPng = Join-Path $root 'assets\icon_SRC.png'
$outIco = Join-Path $root 'assets\oscv.ico'

#: 元絵 (1254x1254) の中で角丸四角が占める範囲と角の半径。実測値。
#   閾値で走査した結果: 四角は (71,71)-(1182,1181)、半径は約 229px (辺の 0.206)
$CROP_X = 71
$CROP_Y = 71
$CROP_W = 1112
$RADIUS_RATIO = 0.206

#: ico に入れる大きさ。48 以下は無圧縮 (32bpp BGRA)、64 以上は PNG 圧縮で入れる。
#   元絵に粒状のノイズが乗っているぶん PNG が重く、256 を持つと ico だけで 98KB に
#   なる (exe には win32 リソースと管理リソースの 2 か所に入るので効き目が二重)。
#   実際に必要なのは 16/24/32/48 (タスクバー・一覧) と、大きい表示用の 1 枚だけ。
#   256 の表示は Windows が 128 から補間するので、そこは捨てて 128 止まりにした。
$SIZES = 16, 24, 32, 48, 64, 128
#: これ以上は PNG 圧縮で入れる
$PNG_FROM = 64

Add-Type -AssemblyName System.Drawing

# 角丸四角のパス (アンチエイリアス付きで塗るためのもの)
function New-RoundedPath([float]$size, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc(0, 0, $d, $d, 180, 90)
    $p.AddArc($size - $d, 0, $d, $d, 270, 90)
    $p.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $p.AddArc(0, $size - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# 指定サイズの ARGB ビットマップを作る。
# 縮小してからマスクをかける (先にマスクをかけると、GDI+ が透明部分の
# 黒を混ぜ込んで縁に黒い輪郭が出る)
function New-IconBitmap([System.Drawing.Bitmap]$src, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size),
        $CROP_X, $CROP_Y, $CROP_W, $CROP_W, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()

    # マスクを 8 倍解像度で作って縮める (GraphicsPath の塗りは縁が粗いため)
    $ss = 8
    $mask = New-Object System.Drawing.Bitmap ($size * $ss), ($size * $ss), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $mg = [System.Drawing.Graphics]::FromImage($mask)
    $mg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $path = New-RoundedPath ($size * $ss) ($size * $ss * $RADIUS_RATIO)
    $mg.FillPath([System.Drawing.Brushes]::White, $path)
    $path.Dispose(); $mg.Dispose()
    $small = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sg.DrawImage($mask, 0, 0, $size, $size)
    $sg.Dispose(); $mask.Dispose()

    # マスクの不透明度を絵のアルファに移す
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadWrite, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $md = $small.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $len = $bd.Stride * $size
    $bb = New-Object byte[] $len
    $mb = New-Object byte[] $len
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $bb, 0, $len)
    [System.Runtime.InteropServices.Marshal]::Copy($md.Scan0, $mb, 0, $len)
    for ($i = 3; $i -lt $len; $i += 4) { $bb[$i] = $mb[$i] }
    [System.Runtime.InteropServices.Marshal]::Copy($bb, 0, $bd.Scan0, $len)
    $bmp.UnlockBits($bd); $small.UnlockBits($md); $small.Dispose()
    return $bmp
}

# 32bpp BGRA の DIB (BITMAPINFOHEADER + XOR + AND マスク)。
# アルファを持つので AND マスクは全ビット 0 (全部不透明) でよい
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $len = $bd.Stride * $size
    $px = New-Object byte[] $len
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $px, 0, $len)
    $bmp.UnlockBits($bd)

    $andStride = [int](([int](($size + 31) / 32)) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($len + $andStride * $size))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    # DIB は下から上へ並べる
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($px, $y * $bd.Stride, $size * 4) }
    $w.Write((New-Object byte[] ($andStride * $size)), 0, $andStride * $size)
    $w.Flush()
    return $ms.ToArray()
}

$src = [System.Drawing.Bitmap]::FromFile($srcPng)
$frames = @()
foreach ($size in $SIZES) {
    $bmp = New-IconBitmap $src $size
    if ($size -ge $PNG_FROM) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = Get-DibBytes $bmp
    }
    $bmp.Dispose()
    $frames += , @{ Size = $size; Bytes = $bytes }
    Write-Host ("  {0,3}px  {1,7:N0} bytes" -f $size, $bytes.Length)
}
$src.Dispose()

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$f.Bytes.Length); $w.Write([int]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes, 0, $f.Bytes.Length) }
$w.Flush()
[IO.File]::WriteAllBytes($outIco, $out.ToArray())

Write-Host ''
Write-Host ("完了しました: {0} ({1:N0} bytes)" -f $outIco, (Get-Item $outIco).Length)
