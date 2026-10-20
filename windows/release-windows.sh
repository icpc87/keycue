#!/bin/zsh
# Windows 版を作る：テスト → x64 / ARM64 の自己完結パッケージ → zip と SHA-256 → version-windows.json
#   ./windows/release-windows.sh
# 出力: ~/Library/Developer/KeyCue/windows/release/<版>/（iCloud 同期の外）。
#       試す用に windows/dist/ にも zip を置く（iCloud 経由で Windows から取れる）。
# サイトへの公開は別（deploy_site.sh）。
set -euo pipefail
cd "$(dirname "$0")"

# 仮想カメラの DLL（llvm-mingw で作る）
./KeyCueCamera/build.sh
VERSION=$(tr -d '[:space:]' < ../VERSION)
BUILD=$(tr -d '[:space:]' < ../BUILD_NUMBER)
SITE="https://keycue.ic2.jp"
REL="$HOME/Library/Developer/KeyCue/windows/release/$VERSION"
# Windows 版だけの変更点があればそれを、なければ Mac 版と同じ変更点を使う
NOTES_FILE="../Website/notes/windows-$VERSION.txt"
[[ -f "$NOTES_FILE" ]] || NOTES_FILE="../Website/notes/$VERSION.txt"
rm -rf "$REL"
mkdir -p "$REL"

echo "==> テスト"
dotnet test KeyCue.Core.Tests --nologo -v q

typeset -A SHA
for rid in win-x64 win-arm64; do
  arch=${rid#win-}
  echo "==> $rid"
  out="$REL/build-$arch/KeyCue"
  dotnet publish KeyCue.Windows/KeyCue.Windows.csproj -c Release -r $rid --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false --nologo -v q -o "$out"
  cp README-Windows.txt "$out/はじめにお読みください.txt"
  zip_name="KeyCue-Windows-$VERSION-$arch.zip"
  (cd "$REL/build-$arch" && rm -f "../$zip_name" && zip -qr -9 "../$zip_name" KeyCue)
  SHA[$arch]=$(shasum -a 256 "$REL/$zip_name" | cut -d' ' -f1)
  echo "    $zip_name  $(du -h "$REL/$zip_name" | cut -f1)  sha256 ${SHA[$arch]}"
done

# アプリが見る更新のお知らせ
perl -CSD -MJSON::PP -Mutf8 -e '
  my ($notes, $version, $build, $date, $site, $x64, $arm, $sx, $sa) = @ARGV;
  my @lines;
  if (open my $n, "<:encoding(UTF-8)", $notes) { local $/; @lines = grep { /\S/ } split /\n/, <$n>; }
  my $json = JSON::PP->new->utf8(0)->pretty->canonical;
  print $json->encode({ version => $version, build => $build + 0, date => $date,
    zipX64 => "$site/windows/$x64", zipArm64 => "$site/windows/$arm", sha256X64 => $sx, sha256Arm64 => $sa,
    page => "$site/#windows", notes => \@lines });
' "$NOTES_FILE" "$VERSION" "$BUILD" "$(date +%Y-%m-%d)" "$SITE" "KeyCue-Windows-$VERSION-x64.zip" "KeyCue-Windows-$VERSION-arm64.zip" "${SHA[x64]}" "${SHA[arm64]}" > "$REL/version-windows.json"

mkdir -p dist
rm -f dist/KeyCue-Windows-*.zip(N)
cp "$REL"/KeyCue-Windows-*.zip dist/
echo "==> 完了: $REL"
ls -la "$REL"
