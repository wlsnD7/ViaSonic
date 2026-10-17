# -*- coding: utf-8 -*-
"""ViaSonic 的 RVC 实时变声进程。

改写自 RVC-Project/Retrieval-based-Voice-Conversion-WebUI 的 RVCRealtimeVST/worker/rvc_worker.py（MIT），
协议保持一致：界面程序创建共享内存和两个命名事件，把一块麦克风音频写进共享内存后触发 request，
这里处理完写回并触发 response。运行在 RVC 整合包自带的 Python 里（2026-07-18 及以后的整合包，需要 infer/rtrvc.py）。

共享内存布局（小端）：
  0 magic  4 version  8 status(int)  12 request seq  16 response seq  20 frames
  32 pitch(f)  36 formant(f)  40 index_rate(f)  44 rms_mix(f)  48 threshold(f)  56 infer ms(f)  64 f0 method
  128.. 状态文字（UTF-8，512 字节）  4096.. 输入  4096+MAX_FRAMES*4.. 输出
"""
from __future__ import annotations

import argparse
import ctypes
import json
import mmap
import os
import struct
import sys
import threading
import time
import traceback
from pathlib import Path

MAGIC = 0x50564352
PROTOCOL_VERSION = 1
HEADER_BYTES = 4096
MAX_FRAMES = 131072
MAP_BYTES = HEADER_BYTES + MAX_FRAMES * 4 * 2
INPUT_OFFSET = HEADER_BYTES
OUTPUT_OFFSET = HEADER_BYTES + MAX_FRAMES * 4
STATUS_TEXT_OFFSET = 128
STATUS_TEXT_BYTES = 512

STATUS_LOADING = 2
STATUS_READY = 3
STATUS_ERROR = -1
STATUS_STOP = -2

SYNCHRONIZE = 0x00100000
EVENT_MODIFY_STATE = 0x0002
WAIT_OBJECT_0 = 0
WAIT_TIMEOUT = 258


class _Null:
    def write(self, *_):
        pass

    def flush(self):
        pass

    def isatty(self):
        return False


if sys.stdout is None:  # pythonw 下没有控制台
    sys.stdout = _Null()
if sys.stderr is None:
    sys.stderr = _Null()


def write_value(shared, offset, fmt, value):
    struct.pack_into("<" + fmt, shared, offset, value)


def read_value(shared, offset, fmt):
    return struct.unpack_from("<" + fmt, shared, offset)[0]


def write_status(shared, state, message):
    write_value(shared, 8, "i", state)
    encoded = message.encode("utf-8", errors="replace")[: STATUS_TEXT_BYTES - 1]
    shared[STATUS_TEXT_OFFSET: STATUS_TEXT_OFFSET + STATUS_TEXT_BYTES] = b"\0" * STATUS_TEXT_BYTES
    shared[STATUS_TEXT_OFFSET: STATUS_TEXT_OFFSET + len(encoded)] = encoded
    shared.flush()


class WinEvent:
    def __init__(self, name):
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenEventW.argtypes = [ctypes.c_uint32, ctypes.c_int, ctypes.c_wchar_p]
        kernel32.OpenEventW.restype = ctypes.c_void_p
        self._wait = kernel32.WaitForSingleObject
        self._wait.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
        self._wait.restype = ctypes.c_uint32
        self._set = kernel32.SetEvent
        self._set.argtypes = [ctypes.c_void_p]
        self._set.restype = ctypes.c_int
        self._close = kernel32.CloseHandle
        self._close.argtypes = [ctypes.c_void_p]
        self._close.restype = ctypes.c_int
        self.handle = kernel32.OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, False, name)
        if not self.handle:
            raise OSError(ctypes.get_last_error(), "OpenEventW failed: " + name)

    def wait(self, timeout_ms):
        return int(self._wait(self.handle, timeout_ms))

    def set(self):
        self._set(self.handle)

    def close(self):
        if self.handle:
            self._close(self.handle)
            self.handle = None


def watch_parent(pid):
    """界面程序退出（包括崩溃）后自己退出，不留在后台占显存。"""
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    handle = kernel32.OpenProcess(SYNCHRONIZE, False, pid)
    if not handle:
        os._exit(0)
    kernel32.WaitForSingleObject(handle, 0xFFFFFFFF)
    os._exit(0)


class RVCStreamEngine:
    def __init__(self, cfg):
        self.root = Path(cfg["rvc_root"]).resolve()
        os.chdir(self.root)
        sys.path.insert(0, str(self.root))
        os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
        os.environ.setdefault("OMP_NUM_THREADS", "4")

        import librosa
        import numpy as np
        import torch
        import torch.nn.functional as F
        import torchaudio.transforms as tat

        from configs.config import Config
        from infer import rtrvc
        from tools.cuda_graph import run_cuda_graph

        self.librosa = librosa
        self.np = np
        self.torch = torch
        self.F = F
        self.run_cuda_graph = run_cuda_graph

        self.sample_rate = int(cfg["sample_rate"])
        self.zc = max(1, self.sample_rate // 100)
        self.block_frame = int(round(float(cfg["block_ms"]) / 1000 * self.sample_rate / self.zc) * self.zc)
        self.block_frame_16k = 160 * self.block_frame // self.zc
        self.crossfade_frame = int(round(float(cfg["crossfade_ms"]) / 1000 * self.sample_rate / self.zc) * self.zc)
        # 和官方实时界面一样：SOLA 重叠最多 40ms
        self.sola_buffer_frame = min(self.crossfade_frame, 4 * self.zc)
        self.sola_search_frame = self.zc
        self.extra_frame = int(round(float(cfg["extra_ms"]) / 1000 * self.sample_rate / self.zc) * self.zc)
        self.has_index = bool(cfg.get("index_path")) and Path(cfg["index_path"]).is_file()

        self.config = Config()
        self.rvc = rtrvc.RVC(
            0.0, 0.0,
            str(Path(cfg["model_path"]).resolve()),
            str(Path(cfg["index_path"]).resolve()) if self.has_index else "",
            0.0, self.config, None,
        )
        # rtrvc.RVC 出错时只打印、不抛异常，这里检查模型到底有没有加载上
        if getattr(self.rvc, "net_g", None) is None or not hasattr(self.rvc, "tgt_sr"):
            raise RuntimeError("model failed to load: " + str(cfg["model_path"]))

        dev = self.config.device
        total = self.extra_frame + self.crossfade_frame + self.sola_search_frame + self.block_frame
        self.input_wav = torch.zeros(total, device=dev, dtype=torch.float32)
        self.input_wav_res = torch.zeros(160 * total // self.zc, device=dev, dtype=torch.float32)
        self.rms_buffer = np.zeros(4 * self.zc, dtype="float32")
        self.sola_buffer = torch.zeros(self.sola_buffer_frame, device=dev, dtype=torch.float32)
        self.sola_den_kernel = torch.ones(1, 1, self.sola_buffer_frame, device=dev, dtype=torch.float32)
        self.skip_head = self.extra_frame // self.zc
        self.return_length = (self.block_frame + self.sola_buffer_frame + self.sola_search_frame) // self.zc
        self.fade_in_window = torch.sin(
            0.5 * np.pi * torch.linspace(0.0, 1.0, steps=self.sola_buffer_frame, device=dev)) ** 2
        self.fade_out_window = 1 - self.fade_in_window
        self.resampler = tat.Resample(orig_freq=self.sample_rate, new_freq=16000, dtype=torch.float32).to(dev)
        self.resampler2 = None
        if self.rvc.tgt_sr != self.sample_rate:
            self.resampler2 = tat.Resample(orig_freq=self.rvc.tgt_sr, new_freq=self.sample_rate, dtype=torch.float32).to(dev)
        self.last_pitch = None
        self.last_formant = None
        self.last_index_rate = None

    def prewarm(self):
        phase = self.torch.arange(self.block_frame, device=self.config.device, dtype=self.torch.float32)
        probe = 0.05 * self.torch.sin(2 * self.np.pi * 220.0 * phase / self.sample_rate)
        self.process(probe.cpu().numpy(), 12.0, 0.0, 0.0, 0.5, -60.0, 0)
        if self.torch.device(self.config.device).type == "cuda":
            self.torch.cuda.synchronize(self.config.device)
        self.input_wav.zero_()
        self.input_wav_res.zero_()
        self.rms_buffer.fill(0.0)
        self.sola_buffer.zero_()
        if hasattr(self.rvc, "cache_pitch"):
            self.rvc.cache_pitch.zero_()
        if hasattr(self.rvc, "cache_pitchf"):
            self.rvc.cache_pitchf.zero_()

    def process(self, audio, pitch, formant, index_rate, rms_mix, threshold, f0_method):
        np = self.np
        torch = self.torch
        F = self.F
        if len(audio) != self.block_frame:
            raise ValueError("block mismatch: worker=%d, request=%d" % (self.block_frame, len(audio)))
        if not self.has_index:
            index_rate = 0.0  # 没有索引文件时不检索

        if self.last_pitch != pitch:
            self.rvc.change_key(pitch)
            self.last_pitch = pitch
        if self.last_formant != formant:
            self.rvc.change_formant(formant)
            self.last_formant = formant
        if self.last_index_rate != index_rate:
            self.rvc.change_index_rate(index_rate)
            self.last_index_rate = index_rate

        indata = np.asarray(audio, dtype=np.float32).copy()
        if threshold > -60.0:
            gated = np.append(self.rms_buffer, indata)
            rms = self.librosa.feature.rms(y=gated, frame_length=4 * self.zc, hop_length=self.zc)[:, 2:]
            self.rms_buffer[:] = gated[-4 * self.zc:]
            gated = gated[2 * self.zc - self.zc // 2:]
            below = self.librosa.amplitude_to_db(rms, ref=1.0)[0] < threshold
            for i, mute in enumerate(below):
                if mute:
                    gated[i * self.zc: (i + 1) * self.zc] = 0
            indata = gated[self.zc // 2:]

        self.input_wav[:-self.block_frame] = self.input_wav[self.block_frame:].clone()
        # 开了阈值时 indata 比一块多 2*zc（连同上一块末尾一起门限过），和官方一样按实际长度写回
        self.input_wav[-indata.shape[0]:] = torch.from_numpy(indata).to(self.config.device)
        self.input_wav_res[:-self.block_frame_16k] = self.input_wav_res[self.block_frame_16k:].clone()
        resample_input = self.input_wav[-self.block_frame - 2 * self.zc:]
        resampled = self.run_cuda_graph(
            self.resampler, "viasonic-input-resample", lambda value: self.resampler(value), resample_input)[160:]
        self.input_wav_res[-self.block_frame_16k:] = resampled[-self.block_frame_16k:]

        method = ("rmvpe", "fcpe", "pm")[max(0, min(2, int(f0_method)))]
        infer_wav = self.rvc.infer(self.input_wav_res, self.block_frame_16k, self.skip_head, self.return_length, method)
        if self.resampler2 is not None:
            infer_wav = self.run_cuda_graph(
                self.resampler2, "viasonic-output-resample", lambda value: self.resampler2(value), infer_wav)

        if rms_mix < 1.0:
            input_tail = self.input_wav[self.extra_frame:]
            rms1 = self.librosa.feature.rms(
                y=input_tail[: infer_wav.shape[0]].cpu().numpy(), frame_length=4 * self.zc, hop_length=self.zc)
            rms1 = torch.from_numpy(rms1).to(self.config.device)
            rms1 = F.interpolate(rms1.unsqueeze(0), size=infer_wav.shape[0] + 1, mode="linear", align_corners=True)[0, 0, :-1]
            rms2 = self.librosa.feature.rms(y=infer_wav.cpu().numpy(), frame_length=4 * self.zc, hop_length=self.zc)
            rms2 = torch.from_numpy(rms2).to(self.config.device)
            rms2 = F.interpolate(rms2.unsqueeze(0), size=infer_wav.shape[0] + 1, mode="linear", align_corners=True)[0, 0, :-1]
            rms2 = torch.maximum(rms2, torch.full_like(rms2, 1e-3))
            infer_wav *= torch.pow(rms1 / rms2, 1.0 - rms_mix)

        conv_input = infer_wav[None, None, : self.sola_buffer_frame + self.sola_search_frame]
        cor_nom = F.conv1d(conv_input, self.sola_buffer[None, None, :])
        cor_den = torch.sqrt(F.conv1d(conv_input ** 2, self.sola_den_kernel) + 1e-8)
        sola_offset = int(torch.argmax(cor_nom[0, 0] / cor_den[0, 0]))
        infer_wav = infer_wav[sola_offset:]
        infer_wav[: self.sola_buffer_frame] *= self.fade_in_window
        infer_wav[: self.sola_buffer_frame] += self.sola_buffer * self.fade_out_window
        self.sola_buffer[:] = infer_wav[self.block_frame: self.block_frame + self.sola_buffer_frame]
        return infer_wav[: self.block_frame].float().cpu().numpy()


def run(args):
    shared = None
    request_event = None
    response_event = None
    threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()
    try:
        shared = mmap.mmap(-1, MAP_BYTES, tagname=args.map, access=mmap.ACCESS_WRITE)
        request_event = WinEvent(args.request)
        response_event = WinEvent(args.response)
        if read_value(shared, 0, "I") != MAGIC or read_value(shared, 4, "I") != PROTOCOL_VERSION:
            raise RuntimeError("protocol mismatch")
        write_status(shared, STATUS_LOADING, "loading")
        with open(args.config, "r", encoding="utf-8") as handle:
            cfg = json.load(handle)
        sys.argv = [sys.argv[0]]  # RVC 的 Config 会解析命令行参数
        engine = RVCStreamEngine(cfg)
        write_status(shared, STATUS_LOADING, "prewarm")
        engine.prewarm()
        write_status(shared, STATUS_READY, "ready")

        import numpy as np

        input_view = np.ndarray((MAX_FRAMES,), dtype=np.float32, buffer=shared, offset=INPUT_OFFSET)
        output_view = np.ndarray((MAX_FRAMES,), dtype=np.float32, buffer=shared, offset=OUTPUT_OFFSET)
        last_sequence = 0
        while True:
            result = request_event.wait(1000)
            if result == WAIT_TIMEOUT:
                continue
            if result != WAIT_OBJECT_0:
                raise OSError("WaitForSingleObject failed")
            if read_value(shared, 8, "i") == STATUS_STOP:
                break
            sequence = read_value(shared, 12, "I")
            if sequence == last_sequence:
                continue
            frames = read_value(shared, 20, "I")
            if frames <= 0 or frames > MAX_FRAMES:
                raise ValueError("invalid frame count: %d" % frames)
            started = time.perf_counter()
            output_view[:frames] = engine.process(
                input_view[:frames],
                read_value(shared, 32, "f"), read_value(shared, 36, "f"), read_value(shared, 40, "f"),
                read_value(shared, 44, "f"), read_value(shared, 48, "f"), read_value(shared, 64, "I"))
            write_value(shared, 56, "f", (time.perf_counter() - started) * 1000.0)
            write_value(shared, 16, "I", sequence)
            last_sequence = sequence
            response_event.set()
        return 0
    except BaseException:
        message = traceback.format_exc()
        try:
            Path(args.config + ".log").write_text(message, encoding="utf-8")
        except Exception:
            pass
        try:
            if shared is not None:
                lines = [x for x in message.strip().splitlines() if x.strip()]
                write_status(shared, STATUS_ERROR, lines[-1] if lines else "error")
            if response_event is not None:
                response_event.set()
        except Exception:
            pass
        return 1
    finally:
        for e in (request_event, response_event):
            if e is not None:
                e.close()
        if shared is not None:
            shared.close()


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--map", required=True)
    parser.add_argument("--request", required=True)
    parser.add_argument("--response", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--parent-pid", type=int, required=True)
    return parser.parse_args()


if __name__ == "__main__":
    raise SystemExit(run(parse_args()))
