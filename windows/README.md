# KeyCue for Windows

Mac 版 KeyCue と同じ機能の Windows 版（C# / .NET 10 / WPF）。キーマップ（`.keycue`）は Mac 版と同じ形式で、お互いに開ける（ファイルはキーマップからの相対パスで探す）。

## 構成

| プロジェクト | 中身 | 動く場所 |
| --- | --- | --- |
| `KeyCue.Core` | キーマップ（Mac 版と同じ JSON）、ルーティング、ミキサー（フェード・ルーティング・メーター・テストトーン・ライブ入力のリングバッファ）、キー配列（Mac のキー番号 ↔ Windows のスキャンコード）、pptx の解析（非表示・埋め込み動画・画面切り替え）、素材リストの決まり | どこでも（Mac でテスト） |
| `KeyCue.Media` | FFmpeg での読み込み：音の展開（全トラックを ch として並べる・サンプルレート変換）、動画の展開（別スレッド・GPU 展開・アルファ付き・ループ・シーク・長い動画の音の少しずつ読み）、サムネイル、キャプチャ機器の一覧 | どこでも（Mac では Homebrew の FFmpeg 8.1 でテスト） |
| `KeyCue.Windows` | WPF の画面・音の出力（NAudio：WASAPI / ASIO）・音声入力・映像出力（ウィンドウ・各ディスプレイの全画面・アプリ内モニター）・スライドの変換（PowerPoint／LibreOffice、PDF は Windows 内蔵）・アップデート・自己テスト | Windows |
| `KeyCue.Core.Tests` | 上の 2 つのテスト（xUnit） | どこでも |

画面はすべてコードで組んでいる（XAML のバインディングの書き間違いは実行するまで分からないため、Mac でのビルドで型のまちがいを見つけられるようにした）。

## Mac でビルド・テスト

```sh
cd windows
dotnet build KeyCue.Windows/KeyCue.Windows.csproj      # Windows 版のビルド（実行は Windows）
KEYCUE_TEST_MEDIA=<テスト素材> dotnet test KeyCue.Core.Tests
```

- `KEYCUE_TEST_MEDIA`：Mac 版の自己テストと同じ素材のフォルダ（`eight_ch.wav` `stereo_44k.wav` `four_tracks.mov` `clip.mp4` など）。指定しなければ素材を使うテストは飛ばす。`KEYCUE_TEST_REQUIRE=1` で、素材がないと失敗にする。
- `KEYCUE_TEST_ALPHA`：透明部分のある動画（QuickTime のアニメーション形式など）。
- ビルドの中間ファイルは `~/Library/Developer/KeyCue/windows/build`（iCloud 同期の外。`Directory.Build.props`）。
- FFmpeg の Windows 用 DLL（BtbN/FFmpeg-Builds の 8.1 LGPL 共有版、win64・winarm64）は `~/Library/Developer/KeyCue/windows-deps/x-win64` `x-winarm64` に置き、ビルドで `ffmpeg/` にコピーする。

## 配布用の zip を作る

```sh
./windows/release-windows.sh
```

テスト → x64 / ARM64 の自己完結パッケージ（.NET 同梱）→ `KeyCue-Windows-<版>-x64.zip` / `-arm64.zip` と SHA-256 → `version-windows.json`（アプリのアップデート確認用）。出力は `~/Library/Developer/KeyCue/windows/release/<版>/`。試す用に `windows/dist/` にもコピーする（`dist/testmedia/` は自己テスト用の素材）。版と build 番号は Mac 版と同じ `VERSION` / `BUILD_NUMBER`。

## Windows で確かめる

1. zip を展開して `KeyCue.exe` を開く（コード署名をしていないので、SmartScreen が出たら「詳細情報」→「実行」）。
2. 自己テスト：`KeyCue.exe --selftest <dist\testmedia のフォルダ>`。結果は `%LOCALAPPDATA%\KeyCue\selftest.log`。
3. 思わぬエラーは `%LOCALAPPDATA%\KeyCue\error.log` に記録される（本番中に落ちないよう、記録して続ける）。

## Mac 版との違い

- 操作キー：⌘ の代わりに Ctrl（保存 Ctrl+S、ページ Ctrl+← / →、新しいインスタンス Ctrl+Alt+N、設定 Ctrl+,）。カードの「選択だけ」は Ctrl+クリック。
- キーは物理的な位置（スキャンコード）で受けるので、日本語入力がオンでも、US / JIS どちらの配列でも同じキーで鳴る。F10 も使える。
- 音の出力：WASAPI（Windows の設定のチャンネル数）と ASIO（インターフェースのすべての出力）。
- pptx は PowerPoint（なければ LibreOffice）で変換。Keynote のファイルは変換できない（Mac で pptx / PDF に）。
- QuickTime のアニメーション形式・ProRes 4444 などの透明部分は FFmpeg でそのまま読める（Mac 版のような変換はしない）。
- ディスプレイは Windows の名前（例：`\\.\DISPLAY2`）で覚える。Mac で保存したキーマップのディスプレイ指定は「つながっていません」になる（ウィンドウ出力はそのまま）。
- アップデート：`https://keycue.ic2.jp/version-windows.json` を見て、zip をダウンロードし SHA-256 を確かめてから、KeyCue を閉じて入れ替える（フォルダのほかのファイルは消さない）。
