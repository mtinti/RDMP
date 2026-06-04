"""Builder <-> Verifier loop.

    python harness.py triples/diabetes_example

Reads a requirement + catalogue manifest, asks the Builder (local LLM) for an
`rdmp cmd` script, runs it through the backend to get SQL, asks the Verifier to
critique that SQL against the requirement, and loops until 'pass' or MAX_ITERS.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

import llm
from rdmp_backend import get_backend

HERE = Path(__file__).parent
MAX_ITERS = int(os.environ.get("MAX_ITERS", "4"))


def _read(p: Path) -> str:
    return p.read_text(encoding="utf-8") if p.exists() else ""


def run(example_dir: Path) -> None:
    requirement = _read(example_dir / "requirement.md")
    if not requirement:
        sys.exit(f"No requirement.md in {example_dir}")

    # Catalogue manifest: the tables/columns/filters available to build with.
    # Per-example override falls back to a shared default.
    manifest = _read(example_dir / "manifest.yaml") or _read(HERE / "manifest.yaml")
    # Ground the Builder in the REAL rdmp command vocabulary so it stops inventing syntax.
    command_ref = _read(HERE / "commands_reference.md")
    builder_sys = _read(HERE / "prompts" / "builder.md")
    if command_ref:
        builder_sys += "\n\n# RDMP command reference (use ONLY these commands)\n" + command_ref
    verifier_sys = _read(HERE / "prompts" / "verifier.md")
    backend = get_backend()

    print(f"== model: {llm.MODEL} @ {llm.BASE_URL} | backend: {type(backend).__name__} ==\n")

    feedback = None
    prior_script = None
    for i in range(1, MAX_ITERS + 1):
        print(f"--- iteration {i} ---")

        builder_user = _builder_prompt(requirement, manifest, prior_script, feedback)
        reply = llm.chat(builder_sys, builder_user)
        script = llm.extract_code_block(reply)
        print("Builder script:\n" + script + "\n")

        result = backend.build_and_get_sql(script)
        print("Generated SQL:\n" + result.sql + "\n")

        verifier_user = (
            f"# Requirement\n{requirement}\n\n# Generated SQL\n```sql\n{result.sql}\n```"
        )
        v_reply = llm.chat(verifier_sys, verifier_user)
        verdict = llm.extract_json(v_reply)
        print("Verifier verdict:\n" + json.dumps(verdict, indent=2) + "\n")

        if verdict.get("verdict") == "pass":
            print(f"PASSED on iteration {i}.")
            (example_dir / "build.script.yaml").write_text(script, encoding="utf-8")
            return

        prior_script = script
        feedback = verdict

    print(f"Did not pass within {MAX_ITERS} iterations.")


def _builder_prompt(requirement, manifest, prior_script, feedback) -> str:
    parts = [
        f"# Requirement\n{requirement}",
        f"# Catalogue manifest (only use these)\n```yaml\n{manifest}\n```",
    ]
    if prior_script:
        parts.append(f"# Your previous script\n```yaml\n{prior_script}\n```")
    if feedback:
        parts.append(
            "# Verifier feedback - revise the script to address every point\n"
            + json.dumps(feedback, indent=2)
        )
    return "\n\n".join(parts)


if __name__ == "__main__":
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE / "triples" / "diabetes_example"
    run(target)
