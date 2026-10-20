#!/bin/zsh
# サイト（~/Library/Developer/KeyCue/site）を CORESERVER の keycue.ic2.jp へアップロードする。release.sh のあとに実行する。
#   ./deploy_site.sh            本番（アップロードする）
#   ./deploy_site.sh --dry-run  何が送られるかを表示するだけ
# 送る順番: 1. DMG・ZIP（新しい版と old/、Windows 版の zip とインストーラー）→ 2. ページ → 3. version.json と version-windows.json（最後。アプリがお知らせを出す時点でファイルが必ずあるように）
# サーバーにある、手元にないファイルは消さない。接続は ~/.ssh/config の Host loopbackmix-deploy（鍵認証）。
set -euo pipefail
cd "$(dirname "$0")"
HOST="${KEYCUE_DEPLOY_HOST:-loopbackmix-deploy}"
REMOTE="${KEYCUE_DEPLOY_PATH:-/home/ic2/domains/keycue.ic2.jp/public_html}"
OUT="$HOME/Library/Developer/KeyCue/site"
[[ -f "$OUT/version.json" ]] || { echo "$OUT がありません（先に ./release.sh を実行）"; exit 1; }

DRY=()
[[ "${1:-}" == "--dry-run" ]] && DRY=(--dry-run) && echo "（試し実行: 実際には送りません）"

ssh -o BatchMode=yes -o ConnectTimeout=15 "$HOST" "test -d '$REMOTE'" \
  || { echo "接続できないか、公開フォルダがありません: $HOST:$REMOTE（CORESERVER のコントロールパネルで keycue.ic2.jp を追加してください）"; exit 1; }

RSYNC=(rsync -rlt --checksum --itemize-changes "${DRY[@]}")
echo "==> 1. DMG・ZIP"
"${RSYNC[@]}" --include 'KeyCue-*.dmg' --include 'KeyCue-*.zip' --include 'old/' --include 'old/KeyCue-*.dmg' --include 'windows/' --include 'windows/KeyCue-Windows-*.zip' --include 'windows/KeyCue-Windows-*-setup.exe' --exclude '*' "$OUT/" "$HOST:$REMOTE/"
echo "==> 2. ページ"
"${RSYNC[@]}" --include 'index.html' --include '.htaccess' --include 'images/' --include 'images/*.png' --exclude '*' "$OUT/" "$HOST:$REMOTE/"
echo "==> 3. version.json（Windows 版があれば version-windows.json も）"
"${RSYNC[@]}" "$OUT/version.json" "$HOST:$REMOTE/version.json"
[[ -f "$OUT/version-windows.json" ]] && "${RSYNC[@]}" "$OUT/version-windows.json" "$HOST:$REMOTE/version-windows.json"
[[ ${#DRY[@]} -gt 0 ]] && exit 0

SITE="${KEYCUE_SITE_URL:-https://keycue.ic2.jp}"
VERSION=$(perl -MJSON::PP -e 'local $/; open my $f, "<", $ARGV[0]; print decode_json(<$f>)->{version}' "$OUT/version.json")
FILES=(version.json index.html "KeyCue-$VERSION.dmg" "KeyCue-$VERSION.zip")
[[ -f "$OUT/version-windows.json" ]] && FILES+=(version-windows.json "windows/KeyCue-Windows-$VERSION-x64.zip" "windows/KeyCue-Windows-$VERSION-arm64.zip" "windows/KeyCue-Windows-$VERSION-x64-setup.exe" "windows/KeyCue-Windows-$VERSION-arm64-setup.exe")
for f in "${FILES[@]}"; do
  local_sum=$(shasum -a 256 "$OUT/$f" | cut -d' ' -f1)
  remote_sum=$(curl -sfL --max-time 120 "$SITE/$f?nocache=$(date +%s)" | shasum -a 256 | cut -d' ' -f1)
  [[ "$local_sum" == "$remote_sum" ]] && echo "  一致: $f" || echo "  不一致: $f（キャッシュの可能性もあります）"
done
echo "完了: $SITE"
