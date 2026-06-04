# Cohort-agent prototype (local LLM, no Claude API spend)

A minimal Builder <-> Verifier loop you can run **today** against an open-source model
in **LM Studio**, with a **mock RDMP backend** so you need no metadata or NHS export yet.

```
requirement.md ─► Builder (local LLM) ─► rdmp cmd script ─► backend ─► SQL
                       ▲                                                │
                  JSON feedback ◄──── Verifier (local LLM) ◄────────────┘
```

## 1. Set up LM Studio
1. In LM Studio, load a model that is good at instruction-following + structured output
   (e.g. a Qwen2.5/3-Instruct, Llama-3.x-Instruct, or Hermes function-calling model).
2. Open the **Local Server** tab and **Start Server** (default `http://localhost:1234`).
3. Copy the model id it shows.

## 2. Set up the harness
```bash
cd proposals/cohort-agent/prototype
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
cp .env.example .env          # then set LLM_MODEL to your LM Studio model id
set -a; source .env; set +a   # export the vars
```

## 3. Run
```bash
python harness.py triples/diabetes_example
```
You'll see, per iteration: the Builder's `rdmp cmd` script, the (mock) SQL, and the
Verifier's JSON verdict. With the mock backend the SQL is a placeholder, so this run
shakes out **prompts + your local model's behaviour**, not SQL correctness yet.

## 4. Going real
1. Build the SQL-dump glue tool (see `../dump-cohort-sql.example.cs`).
2. Point RDMP at your exported metadata (SQL Server platform DBs, or a `--dir` YamlRepository).
3. In `.env`: `RDMP_BACKEND=real` and set `RDMP_DLL`, `RDMP_DIR`/conn-strings, `DUMP_SQL_CMD`.
4. Re-run. Now the SQL is real and the Verifier critiques the actual generated query.

## Files
| File | Role |
|------|------|
| `llm.py` | OpenAI-compatible client (swap model/endpoint via env) + artifact extraction |
| `rdmp_backend.py` | `MockRdmp` (default) and `RealRdmp` behind one interface |
| `harness.py` | the Builder/Verifier loop |
| `prompts/builder.md`, `prompts/verifier.md` | the two agents' system prompts (the real product - tune these) |
| `manifest.yaml` | sanitised catalogue metadata the Builder may use (replace with yours) |
| `triples/<name>/requirement.md` | one example; `build.script.yaml` is written on pass |

## Curating your examples
Each previously-built cohort becomes a folder under `triples/`:
- `requirement.md` - the natural-language ask
- `build.script.yaml` - known-good `rdmp cmd` script (few-shot + label)
- `expected.sql` - the SQL RDMP generated (held-out scoring target)

Later, `eval.py` (todo) scores Builder SQL vs `expected.sql` over held-out examples.
