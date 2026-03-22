# Timberbot

C# mod + Python client that lets AI agents read and control a running Timberborn game over HTTP.

```
Timberborn (Unity)
  |-- Timberbot mod (port 8085)      read + write game state
  |-- Vanilla HTTP API (port 8080)   levers + adapters (built-in)

Python client
  |-- timberbot_cli/api.py           Timberbot API wrapper
  |-- timberbot_cli/cli.py           interactive REPL
  |-- timberbot_cli/watch.py         live terminal dashboard
```

## Quick start

```bash
# install the Python client
pip install ./timberbot_cli

# check connection (with Timberborn running + mod loaded)
timberbot summary

# live dashboard
timberbot watch

# control the game
timberbot set_speed 3
timberbot buildings
timberbot pause_building 12345
```

## Docs

- [Getting Started](docs/getting-started.md) -- install, first steps, examples
- [API Reference](docs/api-reference.md) -- all HTTP endpoints
- [Developing](docs/developing.md) -- build from source, add endpoints, Workshop publishing

## Requirements

- Timberborn (Steam)
- .NET SDK 6+ (to build the mod)
- Python 3.8+ (for the client, optional)
