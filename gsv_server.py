# -*- coding: utf-8 -*-
"""ViaTTS 的 GPT-SoVITS 合成服务。

由界面程序在需要时启动，运行在 GPT-SoVITS 整合包自带的 Python 里，只负责「文字 → 音频」，
播放、设备、快捷键都在界面程序里。只用标准库起 HTTP 服务，不依赖整合包里的 Web 框架版本。

接口（只监听 127.0.0.1）：
  GET  /status            {"ready", "loading", "error", "kind"}
  POST /load   {model}    加载引擎或切换模型（后台进行，用 /status 查进度）
  POST /synth  {text, model, speed, text_lang} -> audio/wav（16-bit PCM）
  GET  /logs?after=N      [{"seq", "text"}]
"""
import argparse
import collections
import contextlib
import ctypes
import io
import itertools
import json
import logging
import logging.handlers
import os
import sys
import threading
import warnings
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse


class _Null:
    def write(self, *_):
        pass

    def flush(self):
        pass

    def isatty(self):
        return False


if sys.stdout is None:  # pythonw 下没有控制台，GPT-SoVITS 的 print 会崩
    sys.stdout = _Null()
if sys.stderr is None:
    sys.stderr = _Null()

log = logging.getLogger("gsv")
_seq = itertools.count(1)
_logs = collections.deque(maxlen=500)


class _MemHandler(logging.Handler):
    def emit(self, record):
        _logs.append((next(_seq), self.format(record)))


@contextlib.contextmanager
def _quiet():
    d = io.StringIO()
    o, e = sys.stdout, sys.stderr
    try:
        sys.stdout, sys.stderr = d, d
        yield d
    finally:
        sys.stdout, sys.stderr = o, e


class EngineError(RuntimeError):
    """kind：model_missing（模型文件不全）/ 空。"""

    def __init__(self, message, kind=""):
        super().__init__(message)
        self.kind = kind


class Engine:
    def __init__(self, home, device):
        self.home, self.device = home, device
        self.tts = None
        self.loaded = None  # (gpt, sovits)
        self.loading = False
        self.error, self.kind = "", ""
        self.lock = threading.Lock()

    @staticmethod
    def check(m):
        if not m:
            raise EngineError("未选择模型", "model_missing")
        names = {"gpt": "GPT 权重", "sovits": "SoVITS 权重", "ref": "参考音频"}
        missing = [names[k] for k in ("gpt", "sovits", "ref") if not m.get(k) or not os.path.isfile(m[k])]
        if missing:
            raise EngineError("「%s」缺少%s" % (m.get("name", "模型"), "、".join(missing)), "model_missing")

    def _init_tts(self, m):
        for sub in ("", "GPT_SoVITS", os.path.join("GPT_SoVITS", "eres2net")):
            path = os.path.join(self.home, sub)
            if path not in sys.path:
                sys.path.insert(0, path)
        os.environ["TQDM_DISABLE"] = "1"
        warnings.filterwarnings("ignore")
        os.chdir(self.home)  # GPT-SoVITS 的配置里全是相对路径
        with _quiet() as out:
            try:
                from GPT_SoVITS.TTS_infer_pack.TTS import TTS, TTS_Config
                c = TTS_Config(os.path.join(self.home, "GPT_SoVITS", "configs", "tts_infer.yaml"))
                c.device = self.device
                c.is_half = self.device == "cuda"
                c.t2s_weights_path = m["gpt"]
                c.vits_weights_path = m["sovits"]
                # 权重版本（v2 / v2Pro / v4 …）由 GPT-SoVITS 加载时从文件里自动识别
                return TTS(c)
            except Exception as e:
                raise EngineError("加载 GPT-SoVITS 失败：%s\n%s" % (e, out.getvalue()[-2000:]))

    def _switch(self, m):
        with _quiet() as out:
            try:
                if self.loaded is None or self.loaded[1] != m["sovits"]:
                    self.tts.init_vits_weights(m["sovits"])
                if self.loaded is None or self.loaded[0] != m["gpt"]:
                    self.tts.init_t2s_weights(m["gpt"])
            except Exception as e:
                self.loaded = None
                raise EngineError("切换模型失败：%s\n%s" % (e, out.getvalue()[-2000:]))

    def load(self, m):
        """加载引擎（首次）或切换到模型 m。在后台线程里调用。"""
        self.loading = True
        try:
            self.check(m)
            with self.lock:
                if self.tts is None:
                    log.info("加载 GPT-SoVITS：%s", m.get("name"))
                    tts = self._init_tts(m)
                    self.loaded = (m["gpt"], m["sovits"])
                    self._run(tts, m, "预热。", 1.0, "zh")  # 首次推理要分配显存，跑完才算就绪
                    self.tts = tts
                elif self.loaded != (m["gpt"], m["sovits"]):
                    log.info("切换模型：%s", m.get("name"))
                    self._switch(m)
                    self.loaded = (m["gpt"], m["sovits"])
            self.error, self.kind = "", ""
            log.info("就绪：%s", m.get("name"))
        except EngineError as e:
            self.error, self.kind = str(e).splitlines()[0], e.kind
            (log.warning if e.kind else log.error)(str(e))
        except Exception as e:
            self.error, self.kind = str(e).splitlines()[0], ""
            log.exception("加载失败")
        finally:
            self.loading = False

    def synth(self, m, text, speed, text_lang):
        if self.tts is None:
            raise EngineError("GPT-SoVITS 未就绪")
        self.check(m)
        with self.lock:
            if self.loaded != (m["gpt"], m["sovits"]):
                self._switch(m)
                self.loaded = (m["gpt"], m["sovits"])
            return self._run(self.tts, m, text, speed, text_lang)

    @staticmethod
    def _run(tts, m, text, speed, text_lang):
        req = {
            "text": text, "text_lang": text_lang or "zh",
            "ref_audio_path": m["ref"], "prompt_text": m.get("ref_text", ""),
            "prompt_lang": m.get("ref_lang") or "zh",
            "top_k": 15, "top_p": 1.0, "temperature": 1.0, "speed_factor": float(speed or 1.0),
            "text_split_method": "cut5", "batch_size": 1,
            "split_bucket": False, "parallel_infer": True, "fragment_interval": 0.3,
        }
        with _quiet():
            sr, audio = next(tts.run(req))
        return int(sr), audio  # int16


def to_wav(sr, pcm16):
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(pcm16.tobytes())
    return buf.getvalue()


engine = None


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def _json(self, obj, code=200):
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n).decode("utf-8")) if n else {}

    def do_GET(self):
        url = urlparse(self.path)
        if url.path == "/status":
            self._json({"ready": engine.tts is not None, "loading": engine.loading,
                        "error": engine.error, "kind": engine.kind})
        elif url.path == "/logs":
            after = int(parse_qs(url.query).get("after", ["0"])[0])
            self._json([{"seq": s, "text": t} for s, t in list(_logs) if s > after])
        else:
            self._json({"error": "not found"}, 404)

    def do_POST(self):
        try:
            body = self._body()
            if self.path == "/load":
                if not engine.loading:
                    engine.loading = True
                    threading.Thread(target=engine.load, args=(body.get("model"),), daemon=True).start()
                self._json({"ok": True})
            elif self.path == "/synth":
                sr, pcm = engine.synth(body.get("model"), body.get("text", ""),
                                       body.get("speed", 1.0), body.get("text_lang", "zh"))
                data = to_wav(sr, pcm)
                self.send_response(200)
                self.send_header("Content-Type", "audio/wav")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)
            else:
                self._json({"error": "not found"}, 404)
        except EngineError as e:
            self._json({"error": str(e).splitlines()[0], "kind": e.kind}, 400)
        except Exception as e:
            log.exception("请求失败")
            self._json({"error": str(e).splitlines()[0], "kind": ""}, 500)


def watch_parent(pid):
    """界面程序退出（包括崩溃）时跟着退出，不留孤儿进程。"""
    k32 = ctypes.windll.kernel32
    h = k32.OpenProcess(0x00100000, False, pid)  # SYNCHRONIZE
    if h:
        k32.WaitForSingleObject(h, 0xFFFFFFFF)
    os._exit(0)


def main():
    global engine
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--home", required=True)
    ap.add_argument("--device", default="cuda")
    ap.add_argument("--parent-pid", type=int, default=0)
    ap.add_argument("--log", default="")
    args = ap.parse_args()

    fmt = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s", "%H:%M:%S")
    root = logging.getLogger()
    root.setLevel(logging.INFO)
    handlers = [_MemHandler()]
    if args.log:
        os.makedirs(os.path.dirname(args.log), exist_ok=True)
        handlers.append(logging.handlers.RotatingFileHandler(args.log, maxBytes=1 << 20, backupCount=1, encoding="utf-8"))
    for h in handlers:
        h.setFormatter(fmt)
        root.addHandler(h)

    if args.parent_pid:
        threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()
    engine = Engine(os.path.abspath(args.home), args.device)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    log.info("GPT-SoVITS 服务已启动，端口 %d", args.port)
    server.serve_forever()


if __name__ == "__main__":
    main()
