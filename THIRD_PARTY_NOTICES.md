# 使っているオープンソース

KeyCue は GNU General Public License version 3 以降（GPL-3.0-or-later）で公開しています（[LICENSE](LICENSE)）。
録画・配信・映像の読み込みに FFmpeg の GPL 版を使っているためです。

| 名前 | 使い道 | ライセンス | 入手先 |
| --- | --- | --- | --- |
| FFmpeg 8.1（Mac 版に同梱する `ffmpeg`） | 録画・配信のエンコード | GPL-3.0-or-later（`--enable-gpl`） | https://ffmpeg.org/releases/ffmpeg-8.1.tar.xz 。作り方は [Tools/build-ffmpeg-mac.sh](Tools/build-ffmpeg-mac.sh) |
| FFmpeg 8.1（Windows 版に同梱する DLL と `ffmpeg.exe`。x264・x265 などを含む） | 動画の読み込み、録画・配信 | GPL-3.0-or-later | BtbN/FFmpeg-Builds の `ffmpeg-n8.1-latest-win64-gpl-shared-8.1` / `winarm64`（https://github.com/BtbN/FFmpeg-Builds 。中に入っているライブラリとソースの入手先もここ） |
| FFmpeg.AutoGen 8.1.0 | Windows 版から FFmpeg を呼ぶ | LGPL-3.0 | https://github.com/Ruslan-B/FFmpeg.AutoGen |
| NAudio 2.2.1 | Windows 版の音声出力（WASAPI・ASIO） | MIT | https://github.com/naudio/NAudio |

配布しているアプリに入っている FFmpeg のソースは、このリポジトリの Releases にも同じ版を置きます。
