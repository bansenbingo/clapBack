#!/usr/bin/env python3
"""Run one local WAV transcription with FunASR's ModelScope SenseVoice model."""

from __future__ import annotations

import argparse
from contextlib import redirect_stdout
import json
import re
import sys
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("audio", type=Path, help="16 kHz mono WAV (other readable audio is also accepted)")
    parser.add_argument(
        "--model",
        default="iic/SenseVoiceSmall",
        help="ModelScope model id (default: iic/SenseVoiceSmall)",
    )
    parser.add_argument(
        "--device",
        choices=("auto", "cpu", "mps"),
        default="auto",
        help="Inference device; auto selects MPS when available",
    )
    parser.add_argument("--json", action="store_true", dest="as_json", help="Print the raw JSON result")
    return parser.parse_args()


def select_device(requested: str) -> str:
    if requested != "auto":
        return requested

    try:
        import torch

        if torch.backends.mps.is_available():
            return "mps"
    except Exception:
        pass
    return "cpu"


def extract_text(result: object) -> str:
    if isinstance(result, list):
        parts = [extract_text(item) for item in result]
        return "".join(part for part in parts if part)
    if isinstance(result, dict):
        value = result.get("text")
        if isinstance(value, str):
            return re.sub(r"<\|[^|]+\|>", "", value).strip()
        for value in result.values():
            text = extract_text(value)
            if text:
                return text
    return ""


def main() -> int:
    args = parse_args()
    if not args.audio.is_file():
        print(f"音频文件不存在：{args.audio}", file=sys.stderr)
        return 2

    try:
        with redirect_stdout(sys.stderr):
            from funasr import AutoModel
    except ImportError as exc:
        print("未安装 FunASR，请先运行 scripts/macos-funasr-setup.sh", file=sys.stderr)
        print(exc, file=sys.stderr)
        return 3

    device = select_device(args.device)
    print(f"加载 ModelScope 模型 {args.model}（device={device}）…", file=sys.stderr)
    with redirect_stdout(sys.stderr):
        model = AutoModel(
            model=args.model,
            hub="ms",
            device=device,
            disable_update=True,
            disable_pbar=True,
        )
        result = model.generate(
            input=str(args.audio),
            cache={},
            language="zh",
            use_itn=True,
            batch_size_s=30,
        )
    text = extract_text(result)
    if args.as_json:
        print(json.dumps({"text": text, "device": device, "model": args.model, "result": result}, ensure_ascii=False))
    else:
        print(text)
    return 0 if text else 1


if __name__ == "__main__":
    raise SystemExit(main())
