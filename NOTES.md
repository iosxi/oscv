# 開発メモ

ビルド手順と、実装で必要になった実測値・踏んだ罠の記録。使うだけなら読まなくてよい。

## ビルドと配布

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.bat` をダブルクリックしても同じ（中で build.ps1 を呼ぶだけ）。
.NET SDK 不要。Windows 同梱の `csc.exe`（.NET Framework 4）だけでビルドできる。
ソースは [src/oscv.cs](src/oscv.cs) 1 ファイル。

出来上がるもの:

| | |
|---|---|
| `oscv.exe` | そのまま動く実行ファイル（ショートカットの向き先） |
| `dist\oscv-<版>.zip` | 配布用。中は `oscv-<版>\` の 1 階層に `oscv.exe` と `README.txt` |

`dist/` はリポジトリに含めない。配布物は GitHub のリリースに添付する。
古い zip は新しい方から 3 個だけ残る。

### 版番号

**`src/oscv.cs` の `App.Version` が唯一の出どころ。** v1 から 1 ずつ上げる。
build.ps1 がここを読んで zip 名を決め、`README.txt` の `@VERSION@` を置換する。
ウィンドウのヘッダーにも `oscv v2` として出る。

修正したら `App.Version` を 1 つ上げてからビルドする。

### ビルド時のガード

古いバイナリを新しい版番号で配ってしまう事故を防ぐため、build.ps1 は次を確認する:

- oscv.exe が起動中なら中断する（起動中の exe は上書きできない）
- ビルド後、exe の更新時刻が開始時刻より古ければ中断する

## モニター側の仕様（実測）

LG UN700 (LG HDR 4K) で確認した VCP コード:

| 項目 | VCP | write | read |
|---|---|---|---|
| 明るさ | `0x10` | 0〜100 | そのまま |
| コントラスト | `0x12` | 0〜100 | そのまま |
| ブラックスタビライザー | `0xF9` | **0〜20** | **write 値 x 5** |

`0xF9` は LG 固有コード。**write と read でスケールが違う**（`write 16` → `read 80`）。
GUI は 0〜20 に統一して表示している。

対応コードの一覧は `CapabilitiesRequestAndCapabilitiesReply` で取れる（約 4.4 秒）。
項目を追加するときは、まずこれで対応 VCP を確認し、write/read のスケールが
一致するか実測してから実装すること。

速度は読み書きとも**約 60ms**、ハンドル取得は 5ms。起動から実値表示まで約 250ms。

## 実装上の罠

### SetVCPFeature は範囲外でも true を返す

`0xF9` に 25 や 90 を書いても、戻り値は true なのにモニターは黙って無視する。
これで一度「0xF9 は読み取り専用」と誤診した。実際は write スケールが 0〜20
だっただけ。**呼ぶ前に必ず clamp すること。** 戻り値は成否の判断に使えない。

### 物理モニターのハンドルは 0 が正当な値

このマシンでは `GetPhysicalMonitorsFromHMONITOR` が返すハンドルが 0 だった。
`IntPtr.Zero` を「未オープン」の目印に使っていたため、正常なハンドルを
未初期化と誤判定して即座に諦めていた。**開いているかどうかは専用の bool で持つ。**

### PHYSICAL_MONITOR は非 blittable

string を含むため、配列を `[Out] PHYSICAL_MONITOR[]` で受けると
ハンドルが正しく返らなかった。`AllocHGlobal` + `PtrToStructure` で
手動マーシャリングしている。

### 読み取りは一時的に失敗する

約 2.5%（1/40）の確率で `GetVCPFeatureAndVCPFeatureReply` が false を返す。
40ms 間隔でリトライすれば実質 0 になる（40 回試行して失敗 0）。

### DDC は同時アクセスに弱い

I2C のシリアルバスなので、別プロセスが同じモニターを叩いていると読み書きが
失敗する。検証中、こちらのテストスクリプトとアプリ本体が競合して両方失敗した。
OnScreen Control を常駐させない方が安定する。

### PowerShell 5.1 と .ps1 の文字コード

BOM 無し UTF-8 の `.ps1` を ANSI として読むため、日本語コメントが化けて
構文エラーになる。**`build.ps1` は UTF-8 BOM 付きで保存すること。**
`.bat` は ASCII のみにしてある。

## osccli を使わなくなった経緯

当初は LG の `OSCCLI.exe` にコマンドを投げる実装だった。これは

- **1 コマンドあたり約 3.5 秒**かかる
- OnScreen Control が常駐していないと動かない
- 失敗してもエラー終了せず、文字列を返して正常終了する
  （成功 `Brightness value is : 42` / 失敗 `OnScreen Control process is terminated.`）

という代物で、ドラッグ中は送信を抑制する、デバウンスする、OSC を自動起動・
復旧する、といった対処が必要だった。

調べたところ osccli は DDC/CI をそのまま中継していただけで、独自機能は
何もなかった。`OSCCLI.exe` 自体に DDC/I2C のコードはなく（IPC クライアント）、
実際の I2C は `RHubLib.dll` が `DeviceIoControl` で行っている。

DDC/CI に切り替えた結果、書き込みが 3500ms → 60ms になり、上記の対処は
すべて不要になった。LG のプロセス・サービスを全停止した状態で動作確認済み。
