#!/bin/zsh
# サイトの公開用フォルダを作る（release.sh から呼ぶ）。
#   ./Website/build_site.sh <release.sh の出力フォルダ>
# 出力: ~/Library/Developer/KeyCue/site（iCloud 同期の外）。中身は deploy_site.sh で送る。
set -euo pipefail
cd "$(dirname "$0")/.."
REL="${1:?release.sh の出力フォルダを指定してください}"
VERSION=$(tr -d '[:space:]' < VERSION)
BUILD=$(tr -d '[:space:]' < BUILD_NUMBER)
SITE="https://keycue.ic2.jp"
OUT="$HOME/Library/Developer/KeyCue/site"
DMG="KeyCue-$VERSION.dmg"
ZIP="KeyCue-$VERSION.zip"
DATE=$(date +%Y-%m-%d)
NOTES_FILE="Website/notes/$VERSION.txt"
[[ -f "$REL/$DMG" && -f "$REL/$ZIP" ]] || { echo "$REL に $DMG / $ZIP がありません"; exit 1; }
[[ -f "$NOTES_FILE" ]] || { echo "$NOTES_FILE（この版の変更点、1 行に 1 つ）を書いてください"; exit 1; }

mkdir -p "$OUT/images" "$OUT/old"
# 前の版の DMG は old/ に残す（古い版が必要な人のため）
for f in "$OUT"/KeyCue-*.dmg(N); do [[ "$(basename "$f")" == "$DMG" ]] || mv "$f" "$OUT/old/"; done
rm -f "$OUT"/KeyCue-*.zip(N)
cp "$REL/$DMG" "$REL/$ZIP" "$OUT/"
cp Website/images/*.png "$OUT/images/"

SIZE=$(perl -e 'printf("%.1f MB", (-s $ARGV[0]) / 1000000)' "$OUT/$DMG")
# ページ（変更点は 1 行ずつ <li> に）
perl -CSD -Mutf8 -e '
  my ($tpl, $notes, $version, $date, $dmg, $size) = @ARGV;
  open my $t, "<:encoding(UTF-8)", $tpl or die; local $/; my $html = <$t>;
  open my $n, "<:encoding(UTF-8)", $notes or die; my @lines = grep { /\S/ } split /\n/, <$n>;
  for (@lines) { s/&/&amp;/g; s/</&lt;/g; s/>/&gt;/g; }
  my $li = join "\n", map { "        <li>$_</li>" } @lines;
  $html =~ s/\{\{VERSION\}\}/$version/g; $html =~ s/\{\{DATE\}\}/$date/g;
  $html =~ s/\{\{DMG\}\}/$dmg/g; $html =~ s/\{\{SIZE\}\}/$size/g; $html =~ s/\{\{NOTES\}\}/$li/g;
  print $html;
' Website/index.template.html "$NOTES_FILE" "$VERSION" "$DATE" "$DMG" "$SIZE" > "$OUT/index.html"

# Windows 版（windows/release-windows.sh で作ったもの）があれば、zip と更新のお知らせを入れ、ページに Windows の欄を出す
WREL="$HOME/Library/Developer/KeyCue/windows/release/$VERSION"
if [[ -f "$WREL/version-windows.json" ]]; then
  mkdir -p "$OUT/windows"
  rm -f "$OUT"/windows/KeyCue-Windows-*.zip(N)
  rm -f "$OUT"/windows/KeyCue-Windows-*-setup.exe(N)
  cp "$WREL"/KeyCue-Windows-$VERSION-*.zip "$WREL"/KeyCue-Windows-$VERSION-*-setup.exe "$OUT/windows/"
  cp "$WREL/version-windows.json" "$OUT/version-windows.json"
  WX64="windows/KeyCue-Windows-$VERSION-x64.zip"
  WARM="windows/KeyCue-Windows-$VERSION-arm64.zip"
  WX64S="windows/KeyCue-Windows-$VERSION-x64-setup.exe"
  WARMS="windows/KeyCue-Windows-$VERSION-arm64-setup.exe"
  mb() { perl -e 'printf("%.0f MB", (-s $ARGV[0]) / 1000000)' "$OUT/$1" }
  perl -CSD -Mutf8 -i -pe 'BEGIN { ($v, $d, $x, $xs, $a, $as, $xi, $xis, $ai, $ais) = splice(@ARGV, 0, 10) } s/\{\{WIN_VERSION\}\}/$v/g; s/\{\{WIN_DATE\}\}/$d/g; s/\{\{WIN_X64\}\}/$x/g; s/\{\{WIN_X64_SIZE\}\}/$xs/g; s/\{\{WIN_ARM64\}\}/$a/g; s/\{\{WIN_ARM64_SIZE\}\}/$as/g; s/\{\{WIN_X64_SETUP\}\}/$xi/g; s/\{\{WIN_X64_SETUP_SIZE\}\}/$xis/g; s/\{\{WIN_ARM64_SETUP\}\}/$ai/g; s/\{\{WIN_ARM64_SETUP_SIZE\}\}/$ais/g; s/<!--\/?WIN-->//g;' \
    "$VERSION" "$DATE" "$WX64" "$(mb $WX64)" "$WARM" "$(mb $WARM)" "$WX64S" "$(mb $WX64S)" "$WARMS" "$(mb $WARMS)" "$OUT/index.html"
else
  rm -f "$OUT/version-windows.json" "$OUT"/windows/KeyCue-Windows-*.zip(N) "$OUT"/windows/KeyCue-Windows-*-setup.exe(N)
  perl -0 -i -pe 's/<!--WIN-->.*?<!--\/WIN-->//gs' "$OUT/index.html"
fi

# アプリが見るお知らせ
perl -CSD -MJSON::PP -Mutf8 -e '
  my ($notes, $version, $build, $date, $site, $dmg, $zip) = @ARGV;
  open my $n, "<:encoding(UTF-8)", $notes or die; local $/; my @lines = grep { /\S/ } split /\n/, <$n>;
  my $json = JSON::PP->new->utf8(0)->pretty->canonical;
  print $json->encode({ version => $version, build => $build + 0, date => $date,
    url => "$site/$dmg", zip => "$site/$zip", page => "$site/#download",
    minimumSystemVersion => "14.0", notes => \@lines });
' "$NOTES_FILE" "$VERSION" "$BUILD" "$DATE" "$SITE" "$DMG" "$ZIP" > "$OUT/version.json"

cat > "$OUT/.htaccess" <<'HT'
AddType application/x-apple-diskimage .dmg
AddType application/zip .zip
AddType application/vnd.microsoft.portable-executable .exe
AddType application/json .json
<FilesMatch "^version(-windows)?\.json$">
  Header set Cache-Control "no-cache, no-store, must-revalidate"
</FilesMatch>
HT
echo "site: $OUT"
ls -la "$OUT"
