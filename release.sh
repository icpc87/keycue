#!/bin/zsh
# 配布用の KeyCue を作る：両対応ビルド → Developer ID 署名（Hardened Runtime・時刻認証）→ 公証 → ステープル
#   → アップデート用 ZIP と配布用 DMG → サイト（Website/build_site.sh）
#   ./release.sh
# 公証の資格情報は Loopback Mix と同じプロファイル（LoopbackApp-notary）を使う。KEYCUE_NOTARY_PROFILE で変えられる。
# 出力は iCloud 同期の外（~/Library/Developer/KeyCue/release/<版>）。
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(tr -d '[:space:]' < VERSION)
BUILD=$(tr -d '[:space:]' < BUILD_NUMBER)
OUT="$HOME/Library/Developer/KeyCue/release/$VERSION"
NOTARY_PROFILE="${KEYCUE_NOTARY_PROFILE:-LoopbackApp-notary}"
IDENTITY=$(security find-identity -v -p codesigning | awk -F'"' '/Developer ID Application/ { print $2; exit }')
[[ -n "$IDENTITY" ]] || { echo "Developer ID Application の証明書がありません"; exit 1; }
xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" >/dev/null 2>&1 \
  || { echo "公証の資格情報（$NOTARY_PROFILE）がありません: xcrun notarytool store-credentials \"$NOTARY_PROFILE\" ..."; exit 1; }

rm -rf "$OUT"
mkdir -p "$OUT"
echo "==> KeyCue $VERSION（build $BUILD）"
KEYCUE_APP_DIR="$OUT" KEYCUE_UNIVERSAL=1 KEYCUE_TIMESTAMP=1 KEYCUE_SIGN_IDENTITY="$IDENTITY" ./build.sh
APP="$OUT/KeyCue.app"

notarize() {
  local file="$1" result
  echo "==> 公証: $(basename "$file")"
  result=$(xcrun notarytool submit "$file" --keychain-profile "$NOTARY_PROFILE" --wait 2>&1)
  echo "$result" | grep -E "id:|status:" | tail -2
  if ! echo "$result" | grep -q "status: Accepted"; then
    local id=$(echo "$result" | awk '/id:/ { print $2; exit }')
    [[ -n "$id" ]] && xcrun notarytool log "$id" --keychain-profile "$NOTARY_PROFILE" || true
    echo "公証に失敗しました"; exit 1
  fi
}

# 1. アプリを公証してステープル
ditto -c -k --keepParent "$APP" "$OUT/notarize.zip"
notarize "$OUT/notarize.zip"
rm "$OUT/notarize.zip"
xcrun stapler staple "$APP" >/dev/null && echo "  stapled: KeyCue.app"
spctl --assess --type execute -vv "$APP" 2>&1 | sed 's/^/  /'

# 2. アップデート用 ZIP（ステープル済みのアプリ）
ZIP="$OUT/KeyCue-$VERSION.zip"
ditto -c -k --keepParent "$APP" "$ZIP"

# 3. 配布用 DMG（アプリと「アプリケーション」へのリンク）
DMG="$OUT/KeyCue-$VERSION.dmg"
STAGE="$OUT/dmg"
mkdir -p "$STAGE"
ditto "$APP" "$STAGE/KeyCue.app"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "KeyCue $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$DMG" >/dev/null
rm -rf "$STAGE"
codesign --force --timestamp --sign "$IDENTITY" "$DMG"
notarize "$DMG"
xcrun stapler staple "$DMG" >/dev/null && echo "  stapled: $(basename "$DMG")"

# 4. サイト
./Website/build_site.sh "$OUT"
echo "完了: $OUT"
