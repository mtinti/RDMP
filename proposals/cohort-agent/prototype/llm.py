"""Model-agnostic chat client.

Targets any OpenAI-compatible server. Defaults to a local LM Studio instance, so
prototyping costs nothing and never touches the Claude API. Point LLM_BASE_URL at
Ollama, vLLM, or a hosted endpoint to swap models without changing any other code.
"""
from __future__ import annotations

import json
import os
import re

from openai import OpenAI

# --- config (env-driven; sensible local defaults) ---------------------------
BASE_URL = os.environ.get("LLM_BASE_URL", "http://localhost:1234/v1")  # LM Studio default
API_KEY = os.environ.get("LLM_API_KEY", "lm-studio")  # LM Studio ignores the value
MODEL = os.environ.get("LLM_MODEL", "local-model")  # the model id loaded in LM Studio
TEMPERATURE = float(os.environ.get("LLM_TEMPERATURE", "0.2"))
# Reasoning models (e.g. Nemotron) spend tokens "thinking" before the answer, so give
# generous headroom or the final content can come back empty/truncated.
MAX_TOKENS = int(os.environ.get("LLM_MAX_TOKENS", "12000"))

_client = OpenAI(base_url=BASE_URL, api_key=API_KEY)


def chat(system: str, user: str) -> str:
    """One round-trip. Returns the assistant's final answer (message.content).

    We do NOT use response_format/grammar constraints: on reasoning models that conflicts
    with the chain-of-thought step and returns empty content. Instead we prompt for the
    desired format and parse defensively. Chain-of-thought lands in `reasoning_content`,
    which LM Studio keeps out of `content`, so `content` is already clean.
    """
    resp = _client.chat.completions.create(
        model=MODEL,
        temperature=TEMPERATURE,
        max_tokens=MAX_TOKENS,
        messages=[
            {"role": "system", "content": system},
            {"role": "user", "content": user},
        ],
    )
    return resp.choices[0].message.content or ""


# --- helpers to pull structured artifacts out of free-text replies ----------
_FENCE = re.compile(r"```(?:yaml|yml)?\s*\n(.*?)```", re.DOTALL)


def extract_code_block(text: str) -> str:
    """Return the contents of the first fenced code block, else the whole text."""
    m = _FENCE.search(text)
    return (m.group(1) if m else text).strip()


def extract_json(text: str) -> dict:
    """Best-effort JSON object extraction from a model reply."""
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass
    start, depth = text.find("{"), 0
    if start == -1:
        raise ValueError(f"No JSON object found in reply:\n{text}")
    for i in range(start, len(text)):
        depth += text[i] == "{"
        depth -= text[i] == "}"
        if depth == 0:
            return json.loads(text[start : i + 1])
    raise ValueError(f"Unbalanced JSON in reply:\n{text}")
