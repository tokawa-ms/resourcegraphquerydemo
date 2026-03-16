# resourcegraphquery

[日本語](README.md) | [English](readme-en.md)

[![License: MIT](https://img.shields.io/github/license/tokawa-ms/resourcegraphquerydemo)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Azure Resource Graph](https://img.shields.io/badge/Azure-Resource%20Graph-0078D4?logo=microsoftazure&logoColor=white)](https://learn.microsoft.com/azure/governance/resource-graph/)
[![GitHub stars](https://img.shields.io/github/stars/tokawa-ms/resourcegraphquerydemo?style=social)](https://github.com/tokawa-ms/resourcegraphquerydemo)

A .NET 10 console app for inspecting App Service Plan change history in a readable format while reusing an Azure Resource Graph sample query as-is.

It reads the Azure CLI query defined in `querysample.md`, authenticates with `DefaultAzureCredential` by default, and renders the change log as either a console table or JSON.

## Highlights

- Reuses the Azure CLI query stored in `querysample.md`
- Switches between `DefaultAzureCredential` and `AzureCliCredential`
- Presents App Service Plan change history in a readable table
- Supports `--json` output for scripting and automation
- Supports `--diagnose-auth` for authentication and RBAC troubleshooting

## Quick Start

### Prerequisites

- .NET 10 SDK
- A working Azure credential through `DefaultAzureCredential` or `AzureCliCredential`
- RBAC permissions to run Azure Resource Graph queries against the target subscription

If you want to use Azure CLI credentials, sign in first.

```powershell
az login
```

Minimal example:

```powershell
dotnet run -- --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id>
```

If you want to reuse the subscription ID and target resource ID already embedded in `querysample.md`, you can run the app without arguments.

```powershell
dotnet run
```

## Usage

Run explicitly with Azure CLI credentials:

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id>
```

Render the result as JSON:

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id> --json
```

Use a different sample query file:

```powershell
dotnet run -- --query-sample .\querysample.md --top 20
```

Diagnose authentication visibility and access:

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id> --diagnose-auth
```

### Main Options

| Option                              | Description                                                    |
| ----------------------------------- | -------------------------------------------------------------- |
| `--query-sample <path>`             | Path to the sample query file                                  |
| `--subscription-id <id>`            | Target subscription ID                                         |
| `--target-resource-id <resourceId>` | Overrides the target App Service Plan resource ID in the query |
| `--top <n>`                         | Maximum number of records to retrieve                          |
| `--credential <default\|azurecli>`  | Selects the credential type                                    |
| `--json`                            | Outputs JSON instead of a table                                |
| `--diagnose-auth`                   | Prints authentication and subscription visibility diagnostics  |

## How It Works

The app is implemented in a single `Program.cs`, but the responsibilities are separated clearly.

- `AppArguments`: parses CLI arguments
- `QuerySample`: extracts and rewrites the query from `querysample.md`
- `CredentialFactory`: switches between `DefaultAzureCredential` and `AzureCliCredential`
- `DiagnosticsPrinter`: prints authentication and subscription visibility details
- `ScaleLogEntryParser`: converts Resource Graph responses into display-ready change log entries
- `ConsoleTable` / `JsonConsole`: renders table and JSON output

At runtime, the flow is straightforward.

1. Resolve the final query from `querysample.md` and CLI arguments.
2. Create the credential and execute Azure Resource Graph against the current tenant.
3. Interpret the response as App Service Plan change history.
4. Render the result as a table or JSON.

## Repository Layout

- [Program.cs](Program.cs): application entry point and core logic
- [querysample.md](querysample.md): Azure CLI sample query consumed by the app
- [docs/implementation.md](docs/implementation.md): implementation notes and design intent
- [exec.bat](exec.bat): helper script for running the app

## Notes

- If `DefaultAzureCredential` picks up an unexpected cached identity, `--credential azurecli` is usually the fastest way to isolate the issue.
- If you get `403 AccessDenied`, verify both subscription read access and `Microsoft.ResourceGraph/resourceChanges/read`.
- This repository intentionally keeps the sample in a single file to make the execution flow easy to inspect.

## Documentation

- [docs/implementation.md](docs/implementation.md): implementation details, data flow, and class responsibilities

## References

- https://learn.microsoft.com/dotnet/api/overview/azure/resourcemanager.resourcegraph-readme?view=azure-dotnet
- https://learn.microsoft.com/dotnet/api/overview/azure/identity-readme?view=azure-dotnet
- https://learn.microsoft.com/azure/governance/resource-graph/concepts/azure-resource-graph-get-list-api

## License

This project is licensed under the [MIT License](LICENSE).
