"""
Thin wrapper over the Google Gen AI SDK (`google-genai`) used by the breakdown agent.

Backend selection, in order:
  1. Vertex AI  — when a GCP project is configured. Uses Application Default
     Credentials, which is what the Cloud Run / Agent Engine deployment will use.
  2. Gemini Developer API — when GEMINI_API_KEY or GOOGLE_API_KEY is set instead.

No other AI vendor is permitted in this project (hackathon rules): Google Cloud only.
"""

import logging
import os
from dataclasses import dataclass
from typing import Any, Optional, Type

from google import genai
from google.genai import types
from pydantic import BaseModel

logger = logging.getLogger("GeminiClient")

DEFAULT_MODEL = "gemini-2.5-flash"
DEFAULT_LOCATION = "global"


class GeminiConfigError(RuntimeError):
    """No usable Google Cloud AI credentials are configured."""


@dataclass
class GeminiResult:
    """A single structured-output generation, plus what it cost."""
    parsed: Any
    raw_text: str
    model: str
    backend: str
    prompt_tokens: int
    total_tokens: int


class GeminiClient:
    def __init__(
        self,
        model: Optional[str] = None,
        project: Optional[str] = None,
        location: Optional[str] = None,
        api_key: Optional[str] = None,
    ):
        self.model = model or os.getenv("STRIPBOARD_GEMINI_MODEL", DEFAULT_MODEL)
        self.project = project or os.getenv("GOOGLE_CLOUD_PROJECT")
        self.location = location or os.getenv("GOOGLE_CLOUD_LOCATION", DEFAULT_LOCATION)
        self.api_key = api_key or os.getenv("GEMINI_API_KEY") or os.getenv("GOOGLE_API_KEY")
        self._client: Optional[genai.Client] = None

    @property
    def backend(self) -> str:
        return "vertex-ai" if self.project else "gemini-developer-api"

    @classmethod
    def is_configured(cls) -> bool:
        """True when a call can be attempted without raising GeminiConfigError."""
        return bool(
            os.getenv("GOOGLE_CLOUD_PROJECT")
            or os.getenv("GEMINI_API_KEY")
            or os.getenv("GOOGLE_API_KEY")
        )

    def _ensure_client(self) -> genai.Client:
        if self._client is not None:
            return self._client

        if self.project:
            logger.info(
                "Using Vertex AI backend (project=%s, location=%s, model=%s)",
                self.project, self.location, self.model,
            )
            self._client = genai.Client(
                vertexai=True, project=self.project, location=self.location
            )
        elif self.api_key:
            logger.info("Using Gemini Developer API backend (model=%s)", self.model)
            self._client = genai.Client(api_key=self.api_key)
        else:
            raise GeminiConfigError(
                "No Google Cloud AI credentials found. Set GOOGLE_CLOUD_PROJECT (with "
                "Application Default Credentials via `gcloud auth application-default "
                "login`) to use Vertex AI, or set GEMINI_API_KEY to use the Gemini "
                "Developer API."
            )
        return self._client

    def transcribe_document(
        self,
        data: bytes,
        mime_type: str,
        prompt: str,
        temperature: float = 0.0,
    ) -> GeminiResult:
        """
        Read a document — a PDF screenplay, typically — and return it as text.

        This is the one place the model is asked to look at something rather than reason
        about it. A scanned script has no text layer, so transcription is the only way in;
        once it is text, the ordinary deterministic pipeline takes over and page lengths
        are measured rather than guessed.
        """
        client = self._ensure_client()

        response = client.models.generate_content(
            model=self.model,
            contents=[
                types.Part.from_bytes(data=data, mime_type=mime_type),
                prompt,
            ],
            config=types.GenerateContentConfig(temperature=temperature),
        )

        usage = response.usage_metadata
        return GeminiResult(
            parsed=None,
            raw_text=response.text or "",
            model=self.model,
            backend=self.backend,
            prompt_tokens=getattr(usage, "prompt_token_count", 0) or 0,
            total_tokens=getattr(usage, "total_token_count", 0) or 0,
        )

    def generate_structured(
        self,
        prompt: str,
        response_schema: Type[BaseModel],
        temperature: float = 0.1,
        system_instruction: Optional[str] = None,
    ) -> GeminiResult:
        """
        One structured-output call. The schema is enforced by the model, so `parsed`
        comes back as an instance of `response_schema` rather than as free text.
        """
        client = self._ensure_client()

        response = client.models.generate_content(
            model=self.model,
            contents=prompt,
            config=types.GenerateContentConfig(
                response_mime_type="application/json",
                response_schema=response_schema,
                temperature=temperature,
                system_instruction=system_instruction,
            ),
        )

        usage = response.usage_metadata
        return GeminiResult(
            parsed=response.parsed,
            raw_text=response.text or "",
            model=self.model,
            backend=self.backend,
            prompt_tokens=getattr(usage, "prompt_token_count", 0) or 0,
            total_tokens=getattr(usage, "total_token_count", 0) or 0,
        )
