#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet_bin="${DOTNET_BIN:-${DOTNET_ROOT:-/usr/local/share/dotnet}/dotnet}"
rid="${MACOS_RID:-osx-arm64}"
configuration="${CONFIGURATION:-Release}"
publish_dir="$repo_root/artifacts/macos-publish/$rid"
app_dir="$repo_root/dist/ClapBack.app"

if [[ ! -x "$dotnet_bin" ]]; then
  echo "找不到 .NET SDK：$dotnet_bin" >&2
  exit 2
fi

echo "发布 ClapBack.Desktop（${rid}，${configuration}）…"
"$dotnet_bin" publish "$repo_root/src/ClapBack.Desktop/ClapBack.Desktop.csproj" \
  -c "$configuration" -r "$rid" --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false \
  -o "$publish_dir"

mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"

# Copy the complete self-contained publish output into the bundle's executable
# directory. Keeping the native Avalonia libraries beside the host executable
# makes the .app launchable by Finder without a shell or a PATH configuration.
cp -R "$publish_dir/." "$app_dir/Contents/MacOS/"
rm -f "$app_dir/Contents/MacOS/ClapBack.icns"
if [[ -f "$app_dir/Contents/MacOS/ClapBack.Desktop" ]]; then
  mv -f "$app_dir/Contents/MacOS/ClapBack.Desktop" "$app_dir/Contents/MacOS/ClapBack"
fi
cp "$repo_root/packaging/macos/Info.plist" "$app_dir/Contents/Info.plist"
cp "$repo_root/packaging/macos/ClapBack.icns" "$app_dir/Contents/Resources/ClapBack.icns"
chmod +x "$app_dir/Contents/MacOS/ClapBack"

echo
echo "已生成：$app_dir"
echo "双击该文件即可启动；首次录音时请在 macOS 设置中允许麦克风权限。"
echo "注意：语音识别仍需要先准备仓库的 .venv-funasr："
echo "  $repo_root/scripts/macos-funasr-setup.sh"
