# Devage

Autonomous coding agents on Windows (.NET 8). Phase 1 foundation plus Phase 2 multi-agent Master approval and Windows Service autostart.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or a newer SDK that can target `net8.0`)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for local PostgreSQL)
- An [OpenAI API key](https://platform.openai.com/) (for plan generation / decision classification)
- Administrator shell (only if installing the Windows Service)

## Quick start

### 1. PostgreSQL

```powershell
cd C:\devage
docker compose up -d
```

Default connection (override with `DEVAGE_CONNECTION_STRING`):

```
Host=localhost;Port=5432;Database=devage;Username=devage;Password=devage
```

### 2. OpenAI

```powershell
$env:DEVAGE_OPENAI_API_KEY = "sk-..."
```

Optional: set `OpenAI:ApiKey` / `OpenAI:Model` in `src/Devage.Host/appsettings.json`.

### 3. Build

```powershell
dotnet build C:\devage\Devage.sln
```

EF Core migrations apply automatically when the Host starts.

### 4. Start Host + use CLI

```powershell
dotnet run --project C:\devage\src\Devage.Cli -- host start
dotnet run --project C:\devage\src\Devage.Cli -- -born
dotnet run --project C:\devage\src\Devage.Cli -- -start <name>
dotnet run --project C:\devage\src\Devage.Cli -- -list
dotnet run --project C:\devage\src\Devage.Cli -- -status <name>
dotnet run --project C:\devage\src\Devage.Cli -- -logs <name>
dotnet run --project C:\devage\src\Devage.Cli -- -stop <name>
dotnet run --project C:\devage\src\Devage.Cli -- -kill <name>
dotnet run --project C:\devage\src\Devage.Cli -- host stop
```

Or run the Host directly:

```powershell
dotnet run --project C:\devage\src\Devage.Host
```

Control API default: `http://127.0.0.1:5088` (override with `Host:Url` or `DEVAGE_HOST_URL` for the CLI).

## Interactive `-born`

Creates an agent in `Born` status and interactively asks:

1. Role (`Agent` | `Master`)
2. Optional Master (id/name) for subordinate Agents
3. Which tools to enable (from Host registry, including bundled `web`)
4. Per-tool configuration fields

## Phase 2 — Master approve / multi-agent

Agents **without** a Master stay autonomous: they only ask the user for Important/Critical steps.

Agents **with** `MasterId` create a plan, notify the Master, and **wait** until the Master Approves, Rejects, or Modifies the plan.

```powershell
# 1) Create a Master
dotnet run --project C:\devage\src\Devage.Cli -- -born --name boss --role Master

# 2) Create a subordinate Agent (or assign later)
dotnet run --project C:\devage\src\Devage.Cli -- -born --name worker --role Agent --master boss
# or:
dotnet run --project C:\devage\src\Devage.Cli -- -assign-master worker boss
dotnet run --project C:\devage\src\Devage.Cli -- -assign-master worker none   # detach → autonomous again

# 3) Start subordinate — it waits for Master plan review
dotnet run --project C:\devage\src\Devage.Cli -- -start worker

# 4) Master reviews pending plans from all subordinates
dotnet run --project C:\devage\src\Devage.Cli -- -pending-plans boss
dotnet run --project C:\devage\src\Devage.Cli -- -subordinates boss
dotnet run --project C:\devage\src\Devage.Cli -- -approve-plan <planId> --comment "LGTM"
dotnet run --project C:\devage\src\Devage.Cli -- -reject-plan <planId> --comment "Too risky"
dotnet run --project C:\devage\src\Devage.Cli -- -modify-plan <planId> --steps-json .\modified-steps.json --title "Revised" --comment "Drop step 2"
```

`--steps-json` is a JSON array:

```json
[
  {
    "title": "Research",
    "description": "Fetch docs",
    "toolName": "web",
    "toolInputJson": "{\"url\":\"https://learn.microsoft.com\"}",
    "decisionClass": "Routine",
    "justification": "Read-only"
  }
]
```

API surface (additive):

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/agents/{id}/master` | Assign / clear Master |
| GET | `/api/masters/{id}/agents` | List subordinates |
| GET | `/api/masters/{id}/pending-plans` | Pending plans for one Master |
| GET | `/api/plans/pending?master=` | Pending plans (optional filter) |
| POST | `/api/plans/{planId}/master-decision` | Approve / Reject / Modify |

## Phase 2 — Windows Service autostart

So Running agents resume after reboot, install Devage.Host as a Windows Service (elevated shell):

```powershell
# Requires Administrator
dotnet run --project C:\devage\src\Devage.Cli -- host install-service
dotnet run --project C:\devage\src\Devage.Cli -- host service-status
dotnet run --project C:\devage\src\Devage.Cli -- host uninstall-service
```

`install-service` publishes the Host to `%LOCALAPPDATA%\devage\service\`, creates service `DevageHost` (`start= auto`), and starts it. Set `DEVAGE_CONNECTION_STRING` and `DEVAGE_OPENAI_API_KEY` as **machine** (or service-account) environment variables so the service can reach Postgres and OpenAI after reboot.

For day-to-day development you can still use `devage host start` (foreground/background process via CLI) without the service.

## Runtime behavior

- **Plan-first**: on `-start`, Host builds a plan (LLM with fallback), then executes steps
- **Master gate**: if `MasterId` is set, plan stays `Pending` until Master Approve/Reject/Modify
- **Decision class**: `Routine` | `Important` | `Critical` — Important/Critical require user confirmation (even under a Master, after plan approval)
- **Sandbox**: file writes stay under each agent's `WorkspaceRoot`
- **Checkpoint / resume**: Host boot reloads agents with status `Running`
- **Triple logging**: console + `data/agent-*.log` + PostgreSQL `action_logs`
- **Plugins**: drop tool DLLs into `plugins/`; `AssemblyLoadContext` + `FileSystemWatcher` hot-load them

## Solution layout

```
src/Devage.Cli
src/Devage.Host
src/Devage.Core
src/Devage.Llm
src/Devage.Persistence
src/Devage.Tools.Abstractions
src/Devage.Tools.Web
plugins/
data/
docker-compose.yml
```

## Environment variables

| Variable | Used by | Purpose |
|----------|---------|---------|
| `DEVAGE_CONNECTION_STRING` | Host | PostgreSQL connection |
| `DEVAGE_OPENAI_API_KEY` | Host | OpenAI API key |
| `DEVAGE_HOST_URL` | CLI | Control API base URL |

## Roadmap

- **Phase 2** — Master role approve/reject, multi-agent coordination, Windows Service autostart ✅
- **Phase 3** — Email / Teams tools (Microsoft Graph)
- **Phase 4** — Live dashboard + hardening

## License

See [LICENSE](LICENSE).
