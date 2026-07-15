#!/usr/bin/env bash
# Release automation KLIP — local → GitHub → site, end to end.
# Uso: bash release.sh <versao> ["notas"]
set -e
VERSION="${1:?uso: release.sh <versao> [notas]}"
NOTES="${2:-Atualizacao $VERSION}"
ROOT="/c/Users/leone/klip"
OUT="/c/Users/leone/KlipAnimator"
DATE=$(date +%Y-%m-%d)

echo "== 1/6 publish single-file =="
powershell -c "Get-Process 'KLIP Animator' -ErrorAction SilentlyContinue | Stop-Process -Force" 2>/dev/null || true
sleep 2
cd "$ROOT/dotnet"
dotnet publish Klip.App/Klip.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -o "$OUT" 2>&1 | grep -iE "error|Klip.App ->" | tail -2

echo "== 2/6 rename + MD5 =="
[ -f "$OUT/Klip.App.exe" ] && mv -f "$OUT/Klip.App.exe" "$OUT/KLIP Animator.exe"
rm -f "$OUT"/*.pdb
cp -f "$OUT/KLIP Animator.exe" "$OUT/KLIP-Animator.exe"
MD5=$(md5sum "$OUT/KLIP-Animator.exe" | cut -d' ' -f1)
echo "   MD5=$MD5"

echo "== 3/6 version.json =="
cd "$ROOT/landing"
VERSION="$VERSION" DATE="$DATE" MD5="$MD5" NOTES="$NOTES" node -e '
const fs=require("fs"), f="version.json";
const v=JSON.parse(fs.readFileSync(f,"utf8"));
const e={version:process.env.VERSION,date:process.env.DATE,md5:process.env.MD5,notes:process.env.NOTES};
v.latest={...e,url:"https://github.com/leonelferreira0373/klip/releases/latest/download/KLIP-Animator.exe"};
v.history=[e,...(v.history||[]).filter(h=>h.version!==process.env.VERSION)];
fs.writeFileSync(f,JSON.stringify(v,null,2));
console.log("   version.json -> "+process.env.VERSION+" md5="+process.env.MD5.slice(0,12)+"...");
'

echo "== 4/6 git push (fonte + site) =="
cd "$ROOT"
git add -A
git commit -q -m "release v$VERSION" || echo "   (nada a commitar)"
git push -q origin master && echo "   pushed"

echo "== 5/6 GitHub Release =="
if gh release view "v$VERSION" --repo leonelferreira0373/klip >/dev/null 2>&1; then
  gh release upload "v$VERSION" --repo leonelferreira0373/klip "$OUT/KLIP-Animator.exe" --clobber
else
  gh release create "v$VERSION" --repo leonelferreira0373/klip --title "KLIP Animator $VERSION" \
    --notes "$NOTES"$'\n\n'"MD5: \`$MD5\`" "$OUT/KLIP-Animator.exe"
fi

echo "== 6/6 deploy site (Vercel) =="
cd "$ROOT/landing"
DEP=$(vercel deploy --prod --yes 2>&1 | grep -oE "https://[a-z0-9-]+\.vercel\.app" | tail -1)
[ -n "$DEP" ] && vercel alias set "$DEP" klipstudio.vercel.app >/dev/null 2>&1

# O KLIP-Animator.exe só existe porque o GitHub não gosta de espaços no nome do asset.
# Já foi enviado — deixá-lo aqui punha DOIS KLIPs na pasta do Leonel a cada release, e ele
# acabava a abrir o errado. A pasta instalada tem UM app; o resto vive na release.
rm -f "$OUT/KLIP-Animator.exe"

echo ""
echo "DONE v$VERSION · MD5 $MD5"
echo "  site:    https://klipstudio.vercel.app"
echo "  release: https://github.com/leonelferreira0373/klip/releases/tag/v$VERSION"
