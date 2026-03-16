# resourcegraph query sample in .NET 10

[日本語](README.md) | [English](readme-en.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Azure Resource Graph](https://img.shields.io/badge/Azure-Resource%20Graph-0078D4?logo=microsoftazure&logoColor=white)](https://learn.microsoft.com/azure/governance/resource-graph/)
[![Repository](https://img.shields.io/badge/Repository-GitHub-181717?logo=github&logoColor=white)](https://github.com/tokawa-ms/resourcegraphquerydemo)

Azure Resource Graph のサンプルクエリをそのまま再利用しつつ、App Service Plan の変更履歴を見やすい形で確認するための .NET 10 コンソールアプリです。

`querysample.md` に書かれた Azure CLI のクエリを読み込み、既定では `DefaultAzureCredential` で認証して、変更ログを表形式または JSON 形式で出力します。

## Highlights

- `querysample.md` の Azure CLI クエリを読み込んでそのまま実行できる
- `DefaultAzureCredential` と `AzureCliCredential` を切り替えられる
- App Service Plan の変更履歴を表形式で見やすく表示できる
- `--json` でスクリプト処理向けの JSON 出力に切り替えられる
- `--diagnose-auth` で認証と RBAC の切り分けができる

## Quick Start

### Prerequisites

- .NET 10 SDK
- Azure に対して `DefaultAzureCredential` または `AzureCliCredential` が利用できること
- 対象サブスクリプションで Azure Resource Graph を実行できる RBAC

Azure CLI を使う場合は、先にログインしておきます。

```powershell
az login
```

最小実行例:

```powershell
dotnet run -- --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id>
```

`querysample.md` に含まれるサブスクリプション ID と対象リソース ID をそのまま使う場合は、引数なしでも動かせます。

```powershell
dotnet run
```

## Usage

Azure CLI の資格情報を明示して実行する場合:

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id>
```

JSON 形式で出力する場合:

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id> --json
```

別のサンプルファイルを使う場合:

```powershell
dotnet run -- --query-sample .\querysample.md --top 20
```

認証状態を診断する場合:

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id> --diagnose-auth
```

### Main Options

| Option                              | Description                              |
| ----------------------------------- | ---------------------------------------- |
| `--query-sample <path>`             | 読み込むサンプルクエリファイルを指定     |
| `--subscription-id <id>`            | 実行対象のサブスクリプション ID を指定   |
| `--target-resource-id <resourceId>` | クエリ内の対象 App Service Plan を上書き |
| `--top <n>`                         | 取得件数の上限を指定                     |
| `--credential <default\|azurecli>`  | 使用する資格情報を切り替え               |
| `--json`                            | JSON 形式で出力                          |
| `--diagnose-auth`                   | 認証情報と可視サブスクリプションを診断   |

## How It Works

アプリは単一の `Program.cs` にまとまっていますが、責務は明確に分かれています。

- `AppArguments`: CLI 引数の解析
- `QuerySample`: `querysample.md` からのクエリ抽出と上書き
- `CredentialFactory`: `DefaultAzureCredential` / `AzureCliCredential` の切り替え
- `DiagnosticsPrinter`: 認証状態と可視サブスクリプションの確認
- `ScaleLogEntryParser`: Resource Graph 応答を表示用の変更ログへ変換
- `ConsoleTable` / `JsonConsole`: 表形式と JSON 形式の出力

実行時の流れは次の 4 ステップです。

1. `querysample.md` と CLI 引数からクエリ文字列を確定する
2. 資格情報を生成し、現在のテナントで Resource Graph を実行する
3. 応答を App Service Plan の変更履歴として解釈する
4. 表形式または JSON 形式で表示する

## Repository Layout

- [Program.cs](Program.cs): アプリ本体
- [querysample.md](querysample.md): 読み込む Azure CLI クエリのサンプル
- [docs/implementation.md](docs/implementation.md): 実装意図と仕様メモ
- [exec.bat](exec.bat): 実行補助スクリプト

## Notes

- `DefaultAzureCredential` が意図しないキャッシュ済み資格情報を拾う場合は `--credential azurecli` を使うと切り分けしやすくなります。
- `403 AccessDenied` が返る場合は、サブスクリプション参照権限に加えて `Microsoft.ResourceGraph/resourceChanges/read` を確認してください。
- このリポジトリはサンプル実装として、読みやすさを優先して単一ファイル構成を採っています。

## Documentation

- [docs/implementation.md](docs/implementation.md): 実装仕様、データの流れ、主要クラスの役割

## References

- https://learn.microsoft.com/dotnet/api/overview/azure/resourcemanager.resourcegraph-readme?view=azure-dotnet
- https://learn.microsoft.com/dotnet/api/overview/azure/identity-readme?view=azure-dotnet
- https://learn.microsoft.com/azure/governance/resource-graph/concepts/azure-resource-graph-get-list-api

## License

このプロジェクトは [MIT License](LICENSE) の下で提供されています。
