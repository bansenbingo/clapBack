#!/usr/bin/env python3
"""Keep FunASR loaded and transcribe newline-delimited JSON requests."""

from __future__ import annotations

import argparse
import json
import re
import sys
from contextlib import redirect_stdout
from pathlib import Path

PROTOCOL_PREFIX = "CLAPBACK_JSON:"


def emit(payload: object) -> None:
    # FunASR/tqdm may write progress text to stdout on some versions. A
    # prefix makes the machine-readable sidecar protocol unambiguous.
    print(PROTOCOL_PREFIX + json.dumps(payload, ensure_ascii=False), flush=True)


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
        emit({"ready": False, "error": str(exc)})
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
        emit({"ready": False, "error": str(exc)})
        return 4

    emit({"ready": True, "device": device})
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
            emit({"ok": True, "text": extract_text(result)})
        except Exception as exc:
            emit({"ok": False, "error": str(exc)})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
