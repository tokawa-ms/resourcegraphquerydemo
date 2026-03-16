# Resource Graph Query Sample App

このコンソール アプリは [querysample.md](querysample.md) にある Azure CLI の Resource Graph クエリを読み取り、既定では `DefaultAzureCredential` で認証して App Service Plan の変更ログを表形式で表示します。

## 前提条件

- .NET 10 SDK
- Azure に対して `DefaultAzureCredential` が利用できること
  - 例: `az login` 済み、Visual Studio サインイン済み、または環境変数でサービス プリンシパル設定済み
- 対象サブスクリプションで Azure Resource Graph を実行できる RBAC

## 実行

```powershell
dotnet run -- --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id>
```

`DefaultAzureCredential` が別のキャッシュ済み資格情報を拾ってしまう場合は、Azure CLI のログインを明示的に使えます。

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id>
```

JSON 形式で確認したい場合は `--json` を付けます。各変更は配列内の 1 レコードとして出力され、対話端末では色付きで表示されます。

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id> --json
```

`querysample.md` のサブスクリプション ID と対象リソース ID をそのまま使う場合は、引数なしでも実行できます。

```powershell
dotnet run
```

別のサンプルファイルを使う場合:

```powershell
dotnet run -- --query-sample .\querysample.md --top 20
```

認証の切り分け:

```powershell
dotnet run -- --credential azurecli --subscription-id <subscription-id> --target-resource-id <app-service-plan-resource-id> --diagnose-auth
```

## 使っているパッケージ

- `Azure.Identity`
- `Azure.ResourceManager.ResourceGraph`

## 参考情報

- https://learn.microsoft.com/dotnet/api/overview/azure/resourcemanager.resourcegraph-readme?view=azure-dotnet
- https://learn.microsoft.com/dotnet/api/overview/azure/identity-readme?view=azure-dotnet
- https://learn.microsoft.com/azure/governance/resource-graph/concepts/azure-resource-graph-get-list-api
