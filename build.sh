#!/bin/zsh
# KeyCue.app をビルドして置く。
#   ./build.sh                  build/KeyCue.app（手元で使う版）
#   KEYCUE_APP_DIR=<dir> ./build.sh   <dir>/KeyCue.app に作る（配布用は release.sh から）
# バージョンは VERSION（表示用）と BUILD_NUMBER（通し番号）から入れる。
set -euo pipefail
cd "$(dirname "$0")"

# KEYCUE_UNIVERSAL=1 で Apple Silicon と Intel の両対応にする（配布用）
ARCHS=()
[[ "${KEYCUE_UNIVERSAL:-0}" == "1" ]] && ARCHS=(--arch arm64 --arch x86_64)
swift build -c release "${ARCHS[@]}" 2>&1 | grep -v "x86_64 architecture is deprecated" || true
BIN="$(swift build -c release "${ARCHS[@]}" --show-bin-path)/KeyCue"
[[ -x "$BIN" ]] || { echo "ビルドに失敗しました"; exit 1; }

DEST="${KEYCUE_APP_DIR:-build}/KeyCue.app"
# iCloud 同期の「書類」フォルダの中では macOS が付加情報を付け直して署名が通らないので、
# 同期の外で組み立てて署名し、できたものをコピーする
STAGE="$HOME/Library/Developer/KeyCue/stage"
APP="$STAGE/KeyCue.app"
mkdir -p "$STAGE"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/KeyCue"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
# 録画・配信に使う ffmpeg（GPL・Tools/build-ffmpeg-mac.sh で作る）。KEYCUE_FFMPEG で場所を変えられる
FFMPEG="${KEYCUE_FFMPEG:-$HOME/Library/Developer/KeyCue/mac-deps/out/ffmpeg}"
if [[ -x "$FFMPEG" ]]; then
  cp "$FFMPEG" "$APP/Contents/MacOS/ffmpeg"
else
  echo "注意: ffmpeg がありません（$FFMPEG）。録画・配信は使えません。Tools/build-ffmpeg-mac.sh で作れます"
  [[ "${KEYCUE_TIMESTAMP:-0}" == "1" ]] && { echo "配布用には ffmpeg が要ります"; exit 1; }
fi
cp LICENSE "$APP/Contents/Resources/LICENSE.txt"
cp THIRD_PARTY_NOTICES.md "$APP/Contents/Resources/THIRD_PARTY_NOTICES.md"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $(tr -d '[:space:]' < VERSION)" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $(tr -d '[:space:]' < BUILD_NUMBER)" "$APP/Contents/Info.plist"

# 署名：Developer ID があればそれで、Hardened Runtime を付けて署名する（公証に必要）。
# 同じ証明書なら、ビルドし直しても同じアプリと見なされ、セキュリティソフトや macOS の許可が続く。
# なければ ad-hoc。KEYCUE_SIGN_IDENTITY で指定もできる。配布用は KEYCUE_TIMESTAMP=1 で時刻認証を付ける
xattr -cr "$APP"
IDENTITY="${KEYCUE_SIGN_IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null | awk -F'"' '/Developer ID Application/ { print $2; exit }')}"
if [[ -n "$IDENTITY" ]]; then
  TS=(--timestamp=none)
  [[ "${KEYCUE_TIMESTAMP:-0}" == "1" ]] && TS=(--timestamp)
  # 中の ffmpeg を先に署名する（公証は中の実行ファイルもすべて見る）
  [[ -f "$APP/Contents/MacOS/ffmpeg" ]] && codesign --force --options runtime "${TS[@]}" --sign "$IDENTITY" "$APP/Contents/MacOS/ffmpeg" >/dev/null
  codesign --force --options runtime --entitlements Resources/KeyCue.entitlements "${TS[@]}" \
    --sign "$IDENTITY" "$APP" >/dev/null
  echo "signed: $IDENTITY"
else
  [[ -f "$APP/Contents/MacOS/ffmpeg" ]] && codesign --force --sign - "$APP/Contents/MacOS/ffmpeg" >/dev/null
  codesign --force --sign - "$APP" >/dev/null
  echo "signed: ad-hoc"
fi

codesign --verify --strict "$APP"
mkdir -p "$(dirname "$DEST")"
rm -rf "$DEST"
ditto "$APP" "$DEST"
echo "built: $(cd "$(dirname "$DEST")" && pwd)/KeyCue.app ($(tr -d '[:space:]' < VERSION) build $(tr -d '[:space:]' < BUILD_NUMBER))"
