"""Swappable RDMP backend.

The Builder emits a YAML `rdmp cmd` script; the backend runs it and can return the
SQL that RDMP generates for the resulting Cohort Identification Configuration (CIC).

Two implementations behind one interface:
  * MockRdmp  - no RDMP/metadata needed. Lets the agent loop run today against your
                local LLM so you can shake out prompts + model behaviour for free.
  * RealRdmp  - shells out to the real `rdmp` CLI + the SQL-dump glue tool. Wire this
                in once the metadata DB (or a YamlRepository --dir) is available.
"""
from __future__ import annotations

import os
import re
import subprocess
import tempfile
from dataclasses import dataclass

import yaml


@dataclass
class BuildResult:
    cic_id: str | None
    sql: str
    log: str  # raw stdout/stderr or mock trace, for debugging


class RdmpBackend:
    def build_and_get_sql(self, script_yaml: str) -> BuildResult:  # pragma: no cover
        raise NotImplementedError


# ---------------------------------------------------------------------------
class MockRdmp(RdmpBackend):
    """Echoes the parsed script back as pseudo-SQL so the loop is runnable end-to-end.

    It does NOT reproduce RDMP's SQL generation - it exists only to exercise the
    Builder->run->Verifier plumbing and let you evaluate local-model output quality
    before any real RDMP/metadata is in place.
    """

    def build_and_get_sql(self, script_yaml: str) -> BuildResult:
        commands = self._parse_commands(script_yaml)
        lines = "\n".join(f"  -- {c}" for c in commands)
        sql = (
            "/* MOCK SQL - generated from the cmd script for plumbing only. */\n"
            "/* Commands the Builder produced:\n" + lines + "\n*/\n"
            "SELECT DISTINCT chi FROM <cohort> /* see commands above */"
        )
        return BuildResult("mock-1", sql, f"parsed {len(commands)} command(s)")

    @staticmethod
    def _parse_commands(script_yaml: str) -> list[str]:
        """Tolerant parse: try YAML's Commands list, else fall back to '- ' lines.

        The Builder often emits free-form command lines (with inline colons/comments)
        that aren't strict YAML, so we degrade gracefully instead of failing.
        """
        try:
            doc = yaml.safe_load(script_yaml)
            if isinstance(doc, dict) and isinstance(doc.get("Commands"), list):
                return [str(c) for c in doc["Commands"]]
            if isinstance(doc, list):
                return [str(c) for c in doc]
        except yaml.YAMLError:
            pass
        return [
            ln.strip()[1:].strip()
            for ln in script_yaml.splitlines()
            if ln.strip().startswith("-")
        ]


# ---------------------------------------------------------------------------
class RealRdmp(RdmpBackend):
    """Drives the actual rdmp CLI. Configure via env:

        RDMP_DLL        path to rdmp.dll (e.g. Tools/rdmp/bin/Release/net10.0/rdmp.dll)
        RDMP_DIR        YamlRepository folder  (use this OR the conn-string envs)
        RDMP_CATALOGUE / RDMP_DATAEXPORT   SQL Server connection strings
        DUMP_SQL_CMD    command template that prints CIC SQL given an id, e.g.
                        "dotnet tools/DumpCohortSql/bin/.../DumpCohortSql.dll {cic_id} --dir {dir}"
    """

    def __init__(self):
        self.dll = os.environ["RDMP_DLL"]
        self.dir = os.environ.get("RDMP_DIR")
        self.dump_tpl = os.environ.get("DUMP_SQL_CMD")

    def _base_args(self) -> list[str]:
        # `--dir` selects the file-backed YamlRepository; omit it to use Databases.yaml.
        return ["--dir", self.dir] if self.dir else []

    def build_and_get_sql(self, script_yaml: str) -> BuildResult:
        with tempfile.NamedTemporaryFile("w", suffix=".yaml", delete=False) as f:
            f.write(script_yaml)
            script_path = f.name

        run = subprocess.run(
            ["dotnet", self.dll, "cmd", "-f", script_path, *self._base_args()],
            capture_output=True, text=True,
        )
        log = run.stdout + run.stderr
        cic_id = self._parse_cic_id(log)

        sql = ""
        if cic_id and self.dump_tpl:
            cmd = self.dump_tpl.format(cic_id=cic_id, dir=self.dir or "")
            dump = subprocess.run(cmd, shell=True, capture_output=True, text=True)
            sql = dump.stdout
            log += "\n--- dump ---\n" + dump.stderr
        return BuildResult(cic_id, sql, log)

    @staticmethod
    def _parse_cic_id(log: str) -> str | None:
        # Adjust to however your CLI reports the created object id.
        m = re.search(r"CohortIdentificationConfiguration[:\s]+(\d+)", log)
        return m.group(1) if m else None


def get_backend() -> RdmpBackend:
    return RealRdmp() if os.environ.get("RDMP_BACKEND") == "real" else MockRdmp()
