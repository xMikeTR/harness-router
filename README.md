# Harness Router

A chat interface above the agent harnesses. Laya classifies a new request's phase, a policy table picks the harness and model, and the conversation stays pinned to that route.

Current state: spike. Claude Code is the only harness. Copilot CLI and the own loop are not wired. Nothing is sandboxed: the agent runs as you, in `ROUTER_WORKSPACE`, with Claude Code's default permissions.

## Run

```bash
python -m venv .venv && .venv/bin/pip install laya==0.4.20
.venv/bin/python laya-service/server.py    # first run downloads the model
ROUTER_WORKSPACE=/path/to/repo dotnet run --project src/HarnessRouter
```

Open http://localhost:5180. Start a message with `/route opus|sonnet|haiku` to override the route. `router-ledger.jsonl` records metadata only, never prompts or output.

## Test

```bash
dotnet test
```
