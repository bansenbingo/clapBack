#!/usr/bin/env python3
"""Record microphone input to a PCM WAV file until stdin receives ``stop``."""

from __future__ import annotations

import argparse
import json
import sys
import threading
import wave
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-seconds", type=float, default=30.0)
    parser.add_argument("--sample-rate", type=int, default=16_000)
    parser.add_argument("--channels", type=int, default=1)
    parser.add_argument("--device", type=int, default=None, help="sounddevice input device index")
    return parser.parse_args()


def select_input_device(sd: object, requested: int | None) -> int | None:
    if requested is not None:
        return requested

    # Do not trust the system default: game/streaming software can make a
    # virtual speaker the default input. Prefer the built-in microphone and
    # avoid known virtual routing devices.
    try:
        devices = sd.query_devices()  # type: ignore[attr-defined]
    except Exception:
        return None

    preferred = ("麦克风", "microphone", "built-in", "内置", "macbook", "airpods")
    excluded = ("steam", "streaming", "blackhole", "loopback", "aggregate")
    candidates: list[tuple[int, int]] = []
    for index, device in enumerate(devices):
        if int(device.get("max_input_channels", 0)) <= 0:
            continue
        name = str(device.get("name", "")).lower()
        if any(token in name for token in excluded):
            continue
        score = 100 if any(token in name for token in preferred) else 10
        candidates.append((score, index))
    if not candidates:
        return None
    return max(candidates)[1]


def main() -> int:
    args = parse_args()
    if args.max_seconds <= 0 or args.sample_rate <= 0 or args.channels <= 0:
        print("录音参数无效", file=sys.stderr)
        return 2

    try:
        import numpy as np
        import sounddevice as sd
    except ImportError as exc:
        print("未安装 sounddevice/numpy，请先运行 scripts/macos-funasr-setup.sh", file=sys.stderr)
        print(exc, file=sys.stderr)
        return 3

    stop_event = threading.Event()

    def wait_for_stop() -> None:
        try:
            sys.stdin.readline()
        finally:
            stop_event.set()

    threading.Thread(target=wait_for_stop, name="clapback-record-stop", daemon=True).start()
    chunks: list[np.ndarray] = []

    def callback(indata: np.ndarray, frames: int, stream_time: object, status: object) -> None:
        if status:
            print(str(status), file=sys.stderr, flush=True)
        chunks.append(indata.copy())

    input_device = select_input_device(sd, args.device)
    if input_device is not None:
        print(json.dumps({"status": "device", "index": input_device}), file=sys.stderr, flush=True)

    try:
        with sd.InputStream(
            device=input_device,
            samplerate=args.sample_rate,
            channels=args.channels,
            dtype="int16",
            callback=callback,
        ):
            print(json.dumps({"status": "recording"}), flush=True)
            stop_event.wait(args.max_seconds)
    except Exception as exc:
        print(f"无法打开 macOS 麦克风：{exc}", file=sys.stderr)
        return 4

    if not chunks:
        print("没有采集到音频数据", file=sys.stderr)
        return 5

    audio = np.concatenate(chunks, axis=0)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(args.output), "wb") as wav:
        wav.setnchannels(args.channels)
        wav.setsampwidth(2)
        wav.setframerate(args.sample_rate)
        wav.writeframes(audio.tobytes())
    print(json.dumps({"status": "saved", "seconds": round(len(audio) / args.sample_rate, 3)}), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
