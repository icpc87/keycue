KeyCue for Windows
==================

キーボードのキーに音声・動画・画像・スライドを割り当てて、押すだけで出すアプリです（Mac 版 KeyCue と同じキーマップ .keycue を開けます）。

■ 始め方
1. この zip を好きな場所に展開します（例：ドキュメント\KeyCue）。
   ※ 「C:\Program Files」の中に置くと、アプリ内の自動アップデートが使えません。
2. KeyCue フォルダの KeyCue.exe を開きます。
   初めて開くとき「Windows によって PC が保護されました」と出たら、「詳細情報」→「実行」を押します
   （KeyCue はまだ Windows のコード署名をしていないためです）。

■ 動く環境
- Windows 10（64 ビット）／ Windows 11。x64 版と ARM64 版があります（Snapdragon などは ARM64 版）。
- .NET は同梱しています（別に入れる必要はありません）。
- pptx の変換には PowerPoint（なければ LibreOffice）が必要です。PDF はそのまま読めます。
  Keynote のファイルは Mac で pptx か PDF に書き出してください。

■ 音の出力
- 上の「出力デバイス」で選びます。8ch などの多チャンネルのオーディオインターフェースは、
  ASIO ドライバー（「ASIO: …」）を選ぶとすべての出力を使えます。

■ 動作の確認（自己テスト）
コマンドプロンプトで次のように実行すると、音・映像・スライド・保存などを自動で確かめ、結果を出します。
  KeyCue.exe --selftest <テスト用の素材フォルダ>
結果は %LOCALAPPDATA%\KeyCue\selftest.log に保存されます。

■ うまく動かないとき
エラーは %LOCALAPPDATA%\KeyCue\error.log に記録されます。この内容を添えて知らせてください。

■ ライセンス
動画・音声の読み込みに FFmpeg（LGPL）を使っています。ffmpeg フォルダの LICENSE.txt をご覧ください。
FFmpeg のソース: https://ffmpeg.org/ ／ ビルド: https://github.com/BtbN/FFmpeg-Builds
音の出力に NAudio（MIT ライセンス）を使っています。

https://keycue.ic2.jp/
