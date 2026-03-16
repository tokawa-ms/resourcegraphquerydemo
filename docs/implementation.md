# 実装メモ

このドキュメントは、`Program.cs` にまとまっているサンプル アプリの実装意図と処理の流れを、仕様として読み直しやすい形に整理したものです。

## アプリの役割

このアプリは `querysample.md` に書かれた Azure CLI の Resource Graph クエリ例を読み取り、App Service Plan に対する変更履歴を見やすい形式で表示します。

- 入力: `querysample.md` とコマンドライン引数
- 認証: `DefaultAzureCredential` または `AzureCliCredential`
- 取得先: Azure Resource Graph
- 出力: 表形式または JSON 形式

## 起動時の処理

起動時は、概ね次の順序で処理します。

1. `AppArguments.Parse` で CLI 引数を解釈する
2. `QuerySample.Load` で `querysample.md` から `-q` と `--subscriptions` を抽出する
3. `QuerySample.BuildQuery` で表示用に列名を正規化し、必要なら `targetResourceId` 条件を引数で上書きする
4. `CredentialFactory.Create` で認証方法を選び、`ArmClient` と `TenantResource` を作る
5. `TenantResource.GetResourcesAsync` で Resource Graph を実行する
6. `ScaleLogEntryParser.Parse` で応答 JSON を表示用モデルへ変換する
7. `ConsoleTable.Print` または `JsonConsole.Print` で結果を出力する

## 入力仕様

### 1. サンプル クエリ ファイル

既定では `querysample.md` を読みます。探索順は次の通りです。

1. カレント ディレクトリ直下
2. 実行ファイル配置ディレクトリ直下

`querysample.md` は Azure CLI コマンド例をそのまま置く想定で、次の要素を正規表現で抽出します。

- `-q "<query>"`
- `--subscriptions <subscription-id>`

### 2. コマンドライン引数

主な引数は次の通りです。

- `--query-sample`: 別のサンプルファイルを指定
- `--subscription-id`: 実行対象サブスクリプション ID を上書き
- `--target-resource-id`: クエリ内の対象 App Service Plan を上書き
- `--top`: 最大件数
- `--json`: JSON 形式で表示
- `--diagnose-auth`: 認証診断モード
- `--credential default|azurecli`: 利用する資格情報を選択

`--subscription-id` を省略した場合は、`querysample.md` から抽出した値を使います。

## クエリの正規化

`QuerySample.BuildQuery` では、サンプル クエリの射影をアプリ向けに正規化します。

- 元の列:
  - `properties.changeType`
  - `properties.targetResourceId`
  - `properties.changes`
- 正規化後の列:
  - `changeType`
  - `targetResourceId`
  - `changeAttributes`
  - `changes`

これにより後段の JSON 解析コードは、ObjectArray 形式でも Table 形式でも同じ列名を前提にできます。

## 認証仕様

認証方法は `--credential` で切り替えます。

- `default`: `DefaultAzureCredential`
- `azurecli`: `AzureCliCredential`

`--diagnose-auth` を付けた場合は本命の変更履歴クエリを実行せず、次の診断情報を表示します。

- アクセス トークンから読める主要クレーム
  - Tenant ID
  - Object ID
  - Application ID
  - User Principal Name
- 現在の資格情報から見えているサブスクリプション一覧
- 単純な `Resources` クエリの実行結果

この診断モードは、「サブスクリプションの参照権限が無い」のか「`resourcechanges/read` が無い」のかを切り分ける目的で使います。

## Resource Graph 応答の解釈

`ScaleLogEntryParser` は Resource Graph の応答を `ScaleLogEntry` へ変換します。

### 対応する応答形式

- ObjectArray
- Table

どちらの形式でも、最終的には同じ `ScaleLogEntry` の配列へ寄せます。

### 変更種別の見せ方

`changeType` が `Update` の場合は、変更されたプロパティ名から操作の説明文を推定します。

- スケールアップ・ダウン
  - `sku.name`
  - `sku.tier`
  - `sku.size`
  - `properties.workerTierName`
  - など
- スケールイン・アウト
  - `sku.capacity`
  - `properties.numberOfWorkers`
  - `properties.maximumElasticWorkerCount`
  - など
- 上記に当てはまらない変更は `プラン変更`

### 変更実行者の見せ方

`changeAttributes.changedByType` が `User` の場合は、実行モードを `人間` とし、`changedBy` を表示します。  
それ以外は `自動` として扱います。

### 差分表示

`changes` オブジェクトは `SettingChange` の配列へ変換します。

- `previousValue`
- `newValue`

の両方が空でないものを差分候補として扱い、表形式では `SettingDiff` として短く要約します。

## 出力仕様

### 表形式

既定の出力形式です。`ConsoleTable.Print` が次を担当します。

- 各列幅の自動計算
- 長い文字列の省略表示
- 見出し行と区切り線の描画

### JSON 形式

`--json` 指定時は、`ScaleLogEntry.ToJsonRecord` で整形したオブジェクトを出力します。

- リダイレクト時: そのまま整形済み JSON を出力
- 対話端末: `JsonConsole` が色付きで表示

## ソースコードの読み方

実装は 1 ファイル構成ですが、責務は次のように分かれています。

- `AppArguments`: CLI 引数処理
- `CredentialFactory`: 認証方法の切り替え
- `DiagnosticsPrinter`: 認証診断
- `QuerySample`: サンプル クエリ読み込みと上書き
- `ScaleLogEntryParser`: Resource Graph 応答の解析
- `JsonConsole` / `ConsoleTable`: 表示

今回、上記の責務が追いやすいように、`Program.cs` には主要な処理境界ごとに日本語コメントを追加しています。
