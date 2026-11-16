#!/bin/sh
# Builds the linux-x64 dedicated server package ON THE VM from the shipped commit (owner, 2026-11-08: the owner's PC no longer uploads it; the VM
# reads GitHub fast). docs/releasing.md, "Updating the Vultr server".
#
#   sh vm-build-server.sh <release tag | commit> [out dir]      (as root on the VM; out dir default /root)
#   bash /root/setup-smserver.sh /root/sm-reimpl-server-linux-x64-<version>.tar.gz annihilation
#
# <release tag>: tester-<date>-<short sha> as ship.cmd names it (the commit is its last part), or any commit or tag of Yeezima/sm-reimpl.
# Needs git and the .NET 10 SDK (once: curl -fsSLO https://dot.net/v1/dotnet-install.sh && bash dotnet-install.sh --channel 10.0
# --install-dir /usr/share/dotnet && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet).
# Writes <out dir>/sm-reimpl-server-linux-x64-<version>.tar.gz laid out like release.cmd -Linux builds it (a top folder with server.sh, SmServer and
# the self-contained runtime, LICENSE, THIRD_PARTY_NOTICES.md, VERSION.txt; 0755 on server.sh, SmServer and createdump), so setup-smserver.sh takes
# it unchanged. Built from a clean `git archive` of the commit, Release, no debug symbols, the source path mapped away, as release.ps1 does.
# The source clone lives in $SM_SRC (default /root/sm-reimpl-src); nothing else on the VM is touched.
set -eu
ref="${1:?usage: sh vm-build-server.sh <release tag or commit> [out dir]}"
out="${2:-/root}"
src="${SM_SRC:-/root/sm-reimpl-src}"
repo="${SM_REPO:-https://github.com/Yeezima/sm-reimpl.git}"
command -v git >/dev/null || { echo "git is missing (apt-get install -y git)" >&2; exit 2; }
command -v dotnet >/dev/null || { echo "the .NET 10 SDK is missing (see the top of this script)" >&2; exit 2; }

case "$ref" in
  tester-*-*) commit="${ref##*-}" ;;        # tester-2026-11-08-cd13588 -> cd13588
  *) commit="$ref" ;;
esac
if [ ! -d "$src/.git" ]; then git clone --quiet "$repo" "$src"; fi
git -C "$src" fetch --quiet --tags origin
full=$(git -C "$src" rev-parse --verify --quiet "$commit^{commit}") || { echo "commit $commit is not in $repo (pushed yet?)" >&2; exit 2; }
version=$(git -C "$src" describe --tags --always "$full" | tr -c 'A-Za-z0-9._\n-' '_')
built=$(git -C "$src" show -s --format=%cd --date=format:%Y-%m-%d "$full")
proto=$(git -C "$src" show "$full:src/SmNet/Protocol.cs" | sed -n 's/.*public const ushort Version *= *\([0-9]*\);.*/\1/p' | head -n 1)
name="sm-reimpl-server-linux-x64-$version"
echo "building $name (commit $full, protocol $proto, built $built)"

work=$(mktemp -d /tmp/smserver-build.XXXXXX)
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/src" "$work/$name"
git -C "$src" archive --format=tar "$full" | tar -x -C "$work/src"
dotnet publish "$work/src/src/SmServer/SmServer.csproj" -c Release -r linux-x64 --self-contained true -o "$work/$name" -nologo -v quiet \
  -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en "-p:InformationalVersion=$version|$built" \
  -p:IncludeSourceRevisionInInformationalVersion=false "-p:PathMap=$work/src=/sm-reimpl"
cp "$work/src/tools/release/package/server.sh" "$work/$name/server.sh"
cp "$work/src/LICENSE" "$work/$name/LICENSE"
cp "$work/src/THIRD_PARTY_NOTICES.md" "$work/$name/THIRD_PARTY_NOTICES.md"
printf 'sm-reimpl %s\ncommit: %s\nprotocol: %s\nbuilt: %s (UTC)\npackage: server-linux-x64 (built on the server)\n' "$version" "$full" "$proto" "$built" > "$work/$name/VERSION.txt"
chmod 0755 "$work/$name/server.sh" "$work/$name/SmServer"
[ -f "$work/$name/createdump" ] && chmod 0755 "$work/$name/createdump"
mkdir -p "$out"
tar -czf "$out/$name.tar.gz" -C "$work" "$name"
echo "$out/$name.tar.gz"
echo "next: bash /root/setup-smserver.sh $out/$name.tar.gz annihilation   (only while no one is playing)"
