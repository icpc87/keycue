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

# 仮想カメラ（Camera Extension）。Apple Developer で作ったプロファイル（Developer ID）が
# ~/Library/Developer/KeyCue/profiles/KeyCue.provisionprofile（jp.ic2.keycue・System Extension 付き）と
# （拡張の KeyCueCamera.provisionprofile はあれば使う）にあるときだけ入れる。KEYCUE_PROFILES で場所を変えられる
PROFILES="${KEYCUE_PROFILES:-$HOME/Library/Developer/KeyCue/profiles}"
CAMBIN="$(dirname "$BIN")/KeyCueCamera"
APP_ENT=Resources/KeyCue.entitlements
SYSEXT=""
if [[ -f "$PROFILES/KeyCue.provisionprofile" && -x "$CAMBIN" ]]; then
  TEAM=$(security cms -D -i "$PROFILES/KeyCue.provisionprofile" | plutil -extract TeamIdentifier.0 raw -)
  SYSEXT="$APP/Contents/Library/SystemExtensions/jp.ic2.keycue.camera.systemextension"
  mkdir -p "$SYSEXT/Contents/MacOS"
  cp "$CAMBIN" "$SYSEXT/Contents/MacOS/jp.ic2.keycue.camera"
  sed "s/TEAMID/$TEAM/" Resources/KeyCueCamera-Info.plist > "$SYSEXT/Contents/Info.plist"
  /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $(tr -d '[:space:]' < VERSION)" "$SYSEXT/Contents/Info.plist"
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $(tr -d '[:space:]' < BUILD_NUMBER)" "$SYSEXT/Contents/Info.plist"
  # 拡張のプロファイルはなくてもよい（サンドボックスとチーム名付きの App Group だけなので）
  CAMPROFILE=0
  [[ -f "$PROFILES/KeyCueCamera.provisionprofile" ]] && { cp "$PROFILES/KeyCueCamera.provisionprofile" "$SYSEXT/Contents/embedded.provisionprofile"; CAMPROFILE=1; }
  cp "$PROFILES/KeyCue.provisionprofile" "$APP/Contents/embedded.provisionprofile"
  # 本体：拡張を入れる権限を足す
  APP_ENT="$STAGE/KeyCue.entitlements"
  cp Resources/KeyCue.entitlements "$APP_ENT"
  /usr/libexec/PlistBuddy -c "Add :com.apple.developer.system-extension.install bool true" \
    -c "Add :com.apple.application-identifier string $TEAM.jp.ic2.keycue" \
    -c "Add :com.apple.developer.team-identifier string $TEAM" "$APP_ENT" >/dev/null
  # 拡張：サンドボックスと、Mach サービス名に使う App Group
  CAM_ENT="$STAGE/KeyCueCamera.entitlements"
  rm -f "$CAM_ENT"
  /usr/libexec/PlistBuddy -c "Add :com.apple.security.app-sandbox bool true" \
    -c "Add :com.apple.security.application-groups array" \
    -c "Add :com.apple.security.application-groups:0 string $TEAM.jp.ic2.keycue.camera" \
    "$CAM_ENT" >/dev/null
  if [[ $CAMPROFILE == 1 ]]; then
    /usr/libexec/PlistBuddy -c "Add :com.apple.application-identifier string $TEAM.jp.ic2.keycue.camera" \
      -c "Add :com.apple.developer.team-identifier string $TEAM" "$CAM_ENT" >/dev/null
  fi
  echo "virtual camera: included (team $TEAM)"
else
  echo "注意: 仮想カメラのプロファイルがありません（$PROFILES）。仮想カメラなしで作ります"
fi

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
  [[ -n "$SYSEXT" ]] && codesign --force --options runtime --entitlements "$CAM_ENT" "${TS[@]}" --sign "$IDENTITY" "$SYSEXT" >/dev/null
  codesign --force --options runtime --entitlements "$APP_ENT" "${TS[@]}" \
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
