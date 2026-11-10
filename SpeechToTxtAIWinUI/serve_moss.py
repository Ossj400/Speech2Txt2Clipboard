r"""
MOSS Transcribe-Diarize - lokalny serwer HTTP dla aplikacji C#.
Model laduje sie RAZ przy starcie i zostaje w pamieci GPU.

Uruchomienie:
    Z:\models\MOSS-Transcribe-Diarize\.venv\Scripts\python.exe serve_moss.py
    (albo uzyj serve_moss.bat)

Endpointy:
    GET  /health                -> {"status":"ok","device":"cuda:0","model_loaded":true}
    POST /transcribe            -> multipart form, pole "file" = audio/wideo
                                   zwraca {"ok":true,"text":..,"segments":[...],"elapsed_sec":..}

Domyslnie nasluchuje na http://127.0.0.1:8760
"""
import io
import os
import time
import tempfile
import contextlib
from contextlib import asynccontextmanager

import torch
from fastapi import FastAPI, UploadFile, File, HTTPException
from fastapi.responses import JSONResponse
import uvicorn

from moss_transcribe_diarize import parse_transcript
from moss_transcribe_diarize.inference_utils import (
    build_transcription_messages, generate_transcription, resolve_device,
)
from transformers import AutoModelForCausalLM, AutoProcessor

MODEL_ID = "OpenMOSS-Team/MOSS-Transcribe-Diarize"
HOST = os.environ.get("MOSS_HOST", "127.0.0.1")
PORT = int(os.environ.get("MOSS_PORT", "8760"))

# Prompt wymuszajacy jezyk polski (model inaczej sam zgaduje jezyk).
# Wymuszamy agresywnie: TYLKO polski, zadnych innych jezykow.
PROMPT_PL = (
    "Other language than Polish is mistake, so dont show it."
    "Transcribe the audio into Polish ONLY. "
    "The spoken language is Polish. "
    "All output text MUST be in Polish - never English, never any other language. "
    "If a word sounds foreign, transcribe it phonetically as Polish. "
    "For each segment, start with the timestamp and speaker ID "
    "([S01], [S02], [S03], ...), then the spoken text in Polish, "
    "and end with the segment timestamp."
    "Hotwords: wklej, wyczyść."
)

_state = {"model": None, "processor": None, "device": None, "dtype": None}


def load_model():
    if _state["model"] is not None:
        return
    print(f"[MOSS] Ladowanie modelu {MODEL_ID} ...", flush=True)
    t0 = time.time()
    device = resolve_device("auto")
    dtype = torch.bfloat16 if device.type == "cuda" else torch.float32
    model = AutoModelForCausalLM.from_pretrained(
        MODEL_ID, trust_remote_code=True, dtype="auto",
    ).to(dtype=dtype).to(device).eval()
    processor = AutoProcessor.from_pretrained(MODEL_ID, trust_remote_code=True)
    _state.update(model=model, processor=processor, device=device, dtype=dtype)
    print(f"[MOSS] Model zaladowany w {time.time()-t0:.1f}s na {device}", flush=True)


@asynccontextmanager
async def lifespan(app: FastAPI):
    load_model()
    yield
    # (opcjonalnie: zwalnianie GPU przy zamknieciu)
    _state.clear()


app = FastAPI(title="MOSS Transcribe-Diarize", lifespan=lifespan)


@app.get("/health")
def health():
    return {
        "status": "ok" if _state["model"] is not None else "loading",
        "device": str(_state["device"]),
        "model_loaded": _state["model"] is not None,
    }


@app.post("/transcribe")
async def transcribe(file: UploadFile = File(...), max_new_tokens: int = 8192,
                     lang: str = ""):
    if _state["model"] is None:
        raise HTTPException(status_code=503, detail="Model jeszcze sie laduje")

    data = await file.read()
    if not data:
        raise HTTPException(status_code=400, detail="Pusty plik")

    suffix = os.path.splitext(file.filename or "audio.wav")[1] or ".wav"
    tmp_path = None
    try:
        with tempfile.NamedTemporaryFile(delete=False, suffix=suffix) as tmp:
            tmp.write(data)
            tmp_path = tmp.name

        t0 = time.time()
        use_pl = lang.lower() in ("", "pl", "polish", "polski")
        messages = (build_transcription_messages(tmp_path, prompt=PROMPT_PL)
                    if use_pl else build_transcription_messages(tmp_path))
        result = generate_transcription(
            _state["model"], _state["processor"], messages,
            max_new_tokens=max_new_tokens, do_sample=False,
            device=_state["device"], dtype=_state["dtype"],
        )
        elapsed = time.time() - t0

        segments = [
            {"start": round(s.start, 2), "end": round(s.end, 2),
             "speaker": s.speaker, "text": s.text}
            for s in parse_transcript(result["text"])
        ]
        return {
            "ok": True,
            "text": result["text"],
            "segments": segments,
            "elapsed_sec": round(elapsed, 2),
            "device": str(_state["device"]),
        }
    except Exception as e:
        return JSONResponse(status_code=500,
                            content={"ok": False, "error": f"{type(e).__name__}: {e}"})
    finally:
        if tmp_path:
            with contextlib.suppress(OSError):
                os.remove(tmp_path)


if __name__ == "__main__":
    uvicorn.run(app, host=HOST, port=PORT, log_level="info")
