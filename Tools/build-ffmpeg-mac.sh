#!/bin/zsh
# Mac 版に同梱する ffmpeg（GPL）。ソースは https://ffmpeg.org/releases/ffmpeg-8.1.tar.xz を $HOME/Library/Developer/KeyCue/mac-deps/src に展開して使う。
# 出来上がりは mac-deps/out/ffmpeg（build.sh が KeyCue.app/Contents/MacOS に入れる）（Apple Silicon・Intel 両対応）。映像・音声のエンコードは VideoToolbox / AudioToolbox、RTMPS は SecureTransport
set -euo pipefail
D="$HOME/Library/Developer/KeyCue/mac-deps"
SRC="$D/src/ffmpeg-8.1"
for arch in arm64 x86_64; do
  B="$D/build-$arch"
  rm -rf "$B"; mkdir -p "$B"; cd "$B"
  extra=()
  [[ $arch == x86_64 ]] && extra=(--enable-cross-compile --disable-x86asm)
  "$SRC/configure" --prefix="$B/out" --arch=$arch --target-os=darwin --cc="clang -arch $arch" "${extra[@]}" \
    --extra-cflags="-mmacosx-version-min=14.0" --extra-ldflags="-mmacosx-version-min=14.0" \
    --enable-gpl --enable-static --disable-shared --disable-doc --disable-ffplay --disable-ffprobe --disable-debug \
    --disable-autodetect --enable-videotoolbox --enable-audiotoolbox --enable-securetransport --enable-zlib --enable-bzlib \
    --disable-libxcb --disable-sdl2 --disable-xlib > configure.log 2>&1
  make -j12 ffmpeg > make.log 2>&1
  echo "built $arch"
done
mkdir -p "$D/out"
lipo -create "$D/build-arm64/ffmpeg" "$D/build-x86_64/ffmpeg" -output "$D/out/ffmpeg"
strip -x "$D/out/ffmpeg" || true
echo "done: $(lipo -archs "$D/out/ffmpeg") $(du -h "$D/out/ffmpeg" | cut -f1)"
