#!/bin/zsh
# Windows の仮想カメラ（KeyCueCamera.dll）を Mac で作る。llvm-mingw（https://github.com/mstorsjo/llvm-mingw）を使う。
#   ./build.sh   → ~/Library/Developer/KeyCue/windows/camera/{x64,arm64}/KeyCueCamera.dll（KEYCUE_CAMERA_OUT で変えられる）
# （「書類」フォルダの中にはリンカーが書き込めないことがあるので外に作る）
# LLVM_MINGW で llvm-mingw の場所を変えられる（既定は ~/Library/Developer/KeyCue/llvm-mingw）
set -euo pipefail
cd "$(dirname "$0")"
OUT="${KEYCUE_CAMERA_OUT:-$HOME/Library/Developer/KeyCue/windows/camera}"
# セキュリティソフトがリンカーの書き込みを止めることがあるので、いったん一時フォルダに作ってコピーする
TMP=$(mktemp -d)
trap "rm -rf $TMP" EXIT
T="${LLVM_MINGW:-$HOME/Library/Developer/KeyCue/llvm-mingw}/bin"
for pair in x64:x86_64 arm64:aarch64; do
  arch=${pair%%:*}; triple=${pair##*:}
  mkdir -p "$OUT/$arch"
  "$T/$triple-w64-mingw32-clang++" -std=c++17 -O2 -shared -static -Wall -Wno-unknown-pragmas \
    -o "$TMP/$arch.dll" KeyCueCamera.cpp KeyCueCamera.def \
    -lmfplat -lmfuuid -lole32 -luuid -ladvapi32 -Wl,--exclude-all-symbols
  cp "$TMP/$arch.dll" "$OUT/$arch/KeyCueCamera.dll"
  echo "built $OUT/$arch/KeyCueCamera.dll"
done
