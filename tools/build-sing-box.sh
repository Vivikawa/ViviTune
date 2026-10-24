#!/bin/sh
# Собирает sing-box.exe из podkop-engine (форк sing-box 1.14.3: https://github.com/podkop-net/sing-box)
# в src/ViviTune/third_party/. Нужны git и Go 1.26+. Код берётся из нашего зеркала https://github.com/Vivikawa/sing-box.
# Новый коммит форка: синхронизировать зеркало, поставить на коммит тег vivitune-<версия>, поменять COMMIT и VERSION.
set -e
COMMIT=1d47804dc7831d963936122e071573bd6619cd8b
VERSION=1.14.3-pdk-1d47804
dst="$(cd "$(dirname "$0")/.." && pwd)/src/ViviTune/third_party"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
git -C "$tmp" init -q
git -C "$tmp" fetch -q --depth 1 https://github.com/Vivikawa/sing-box.git "$COMMIT"
git -C "$tmp" checkout -q FETCH_HEAD
mkdir -p "$dst"
cd "$tmp"
CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build -trimpath \
  -tags with_gvisor,with_quic,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0 \
  -ldflags "-X 'github.com/sagernet/sing-box/constant.Version=$VERSION' $(cat release/LDFLAGS) -s -w -buildid=" \
  -o "$dst/sing-box.exe" ./cmd/sing-box
"$dst/sing-box.exe" version
