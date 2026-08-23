#!/usr/bin/env python3
"""Keep FunASR loaded and transcribe newline-delimited JSON requests."""

from __future__ import annotations

import argparse
import json
import re
import sys
from contextlib import redirect_stdout
from pathlib import Path


def extract_text(result: object) -> str:
    if isinstance(result, list):
        return "".join(filter(None, (extract_text(item) for item in result)))
    if isinstance(result, dict):
        value = result.get("text")
        if isinstance(value, str):
            return re.sub(r"<\|[^|]+\|>", "", value).strip()
        for nested in result.values():
            text = extract_text(nested)
            if text:
                return text
    return ""


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="iic/SenseVoiceSmall")
    parser.add_argument("--device", choices=("auto", "cpu", "mps"), default="cpu")
    args = parser.parse_args()

    try:
        import torch
        from funasr import AutoModel
    except ImportError as exc:
        print(json.dumps({"ready": False, "error": str(exc)}, ensure_ascii=False), flush=True)
        return 3

    device = args.device
    if device == "auto":
        try:
            device = "mps" if torch.backends.mps.is_available() else "cpu"
        except Exception:
            device = "cpu"

    print(f"loading SenseVoiceSmall device={device}", file=sys.stderr, flush=True)
    try:
        with redirect_stdout(sys.stderr):
            model = AutoModel(model=args.model, hub="ms", device=device, disable_update=True)
    except Exception as exc:
        print(json.dumps({"ready": False, "error": str(exc)}, ensure_ascii=False), flush=True)
        return 4

    print(json.dumps({"ready": True, "device": device}), flush=True)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            audio = Path(str(request["audio"]))
            language = str(request.get("language", "zh"))
            with redirect_stdout(sys.stderr):
                result = model.generate(
                    input=str(audio),
                    cache={},
                    language=language,
                    use_itn=True,
                    batch_size_s=60,
                )
            print(json.dumps({"ok": True, "text": extract_text(result)}, ensure_ascii=False), flush=True)
        except Exception as exc:
            print(json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
