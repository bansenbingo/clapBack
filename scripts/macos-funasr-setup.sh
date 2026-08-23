#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
python_bin="${PYTHON_BIN:-python3}"
venv_dir="$repo_root/.venv-funasr"

if [[ ! -x "$venv_dir/bin/python" ]]; then
  "$python_bin" -m venv "$venv_dir"
fi

"$venv_dir/bin/python" -m pip install --upgrade pip
# The Douban mirror is used only as a faster fallback for the large dependency
# set on this macOS experiment; override PIP_INDEX_URL to use another mirror.
index_url="${PIP_INDEX_URL:-https://pypi.doubanio.com/simple}"
"$venv_dir/bin/python" -m pip install \
  --index-url "$index_url" \
  --prefer-binary \
  --timeout 120 \
  --retries 3 \
  -r "$repo_root/tools/requirements-macos-funasr.txt"

echo
echo "FunASR macOS 环境已准备完成。首次运行会从 ModelScope 下载 iic/SenseVoiceSmall："
echo "  $venv_dir/bin/python $repo_root/tools/macos_funasr_transcribe.py /path/to/audio.wav"
