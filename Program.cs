using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;

var cancellationToken = CancellationToken.None;

try
{
    // 実行時引数とサンプルクエリを読み込み、実際に投げる Resource Graph クエリを確定する。
    AppArguments arguments = AppArguments.Parse(args);
    QuerySample querySample = QuerySample.Load(arguments.QuerySamplePath);

    // サブスクリプション ID は明示引数を優先し、未指定の場合はサンプルファイルから補完する。
    string subscriptionId = arguments.SubscriptionId ?? querySample.SubscriptionId
        ?? throw new InvalidOperationException("Subscription ID was not provided and could not be extracted from the sample file.");

    string queryText = querySample.BuildQuery(arguments.TargetResourceId);

    // 認証の種類を切り替えられるようにしつつ、ARM クライアントから現在のテナントを取得する。
    TokenCredential credential = CredentialFactory.Create(arguments.CredentialMode);
    var armClient = new ArmClient(credential);
    TenantResource tenant = await GetTenantAsync(armClient, cancellationToken);

    // --diagnose-auth 指定時は、本問い合わせを行う前に認証状態の切り分け情報だけを表示して終了する。
    if (arguments.DiagnoseAuth)
    {
        await DiagnosticsPrinter.PrintAsync(credential, armClient, tenant, subscriptionId, arguments.TargetResourceId, cancellationToken);
        return;
    }

    // Resource Graph への問い合わせ条件を組み立て、応答をアプリ用の表示モデルへ変換する。
    var request = new ResourceQueryContent(queryText)
    {
        Options = new ResourceQueryRequestOptions
        {
            Top = arguments.Top,
            ResultFormat = ResultFormat.ObjectArray,
        },
    };
    request.Subscriptions.Add(subscriptionId);

    Response<ResourceQueryResult> response = await tenant.GetResourcesAsync(request, cancellationToken);
    IReadOnlyList<ScaleLogEntry> entries = ScaleLogEntryParser.Parse(response.Value.Data);

    // 先頭で問い合わせ条件の概要を出し、その後に表形式または JSON 形式で結果を表示する。
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine($"Credential   : {arguments.CredentialMode}");
    Console.WriteLine($"Subscription : {subscriptionId}");
    Console.WriteLine($"Target       : {querySample.ExtractTargetResourceId(queryText) ?? "(not found in query)"}");
    Console.WriteLine($"Rows         : {entries.Count}");
    Console.WriteLine();

    if (arguments.JsonOutput)
    {
        JsonConsole.Print(entries);
    }
    else
    {
        ConsoleTable.Print(
            entries,
            static entry =>
            [
                entry.ChangeTime,
                entry.Operation,
                entry.TargetName,
                entry.SettingDiff,
                entry.Execution,
                entry.Actor,
            ],
            ["Change Time (UTC)", "Operation", "Target", "Setting Diff", "Execution", "Actor"]);
    }
}
catch (RequestFailedException exception) when (exception.Status == 403 || string.Equals(exception.ErrorCode, "AccessDenied", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Azure Resource Graph returned AccessDenied.");
    Console.Error.WriteLine("The current credential does not have the required access for the subscription or target resource in the query.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Check the following:");
    Console.Error.WriteLine("  1. The signed-in identity in the selected credential is the one you expect.");
    Console.Error.WriteLine("  2. That identity has at least Reader access on the target subscription, resource group, or App Service Plan.");
    Console.Error.WriteLine("  3. The identity can read Azure Resource Graph resource changes (Microsoft.ResourceGraph/resourceChanges/read).");
    Console.Error.WriteLine("  4. If Azure CLI can see the subscription but DefaultAzureCredential cannot, rerun with --credential azurecli.");
    Console.Error.WriteLine("  5. If you are using the sample query as-is, replace the hard-coded subscription and target resource with your own values.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Example:");
    Console.Error.WriteLine("  dotnet run -- --credential azurecli --subscription-id <your-subscription-id> --target-resource-id <your-app-service-plan-resource-id>");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"ErrorCode : {exception.ErrorCode}");
    Console.Error.WriteLine($"Message   : {exception.Message}");
    Environment.ExitCode = 1;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine("Failed to query Azure Resource Graph.");
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}

/// <summary>
/// 現在の資格情報から最初のテナントを取得する。
/// Resource Graph の呼び出しにはテナント スコープのクライアントが必要なため使用する。
/// </summary>
/// <param name="armClient">Azure Resource Manager クライアント。</param>
/// <param name="cancellationToken">キャンセル トークン。</param>
/// <returns>取得した <see cref="TenantResource"/>。</returns>
/// <exception cref="InvalidOperationException">テナントが 1 件も見つからない場合。</exception>
static async Task<TenantResource> GetTenantAsync(ArmClient armClient, CancellationToken cancellationToken)
{
    // Resource Graph の呼び出しにはテナント スコープのクライアントが必要なため、見えている先頭テナントを採用する。
    await foreach (TenantResource tenant in armClient.GetTenants().GetAllAsync(cancellationToken: cancellationToken))
    {
        return tenant;
    }

    throw new InvalidOperationException("No Azure tenant was found for the current credential.");
}

/// <summary>
/// コマンドライン引数を解析した結果を保持するレコード。
/// </summary>
/// <param name="QuerySamplePath">クエリ サンプル ファイルのパス。</param>
/// <param name="SubscriptionId">対象サブスクリプション ID。省略時は <see langword="null"/>。</param>
/// <param name="TargetResourceId">クエリ内で上書きする対象リソース ID。省略時は <see langword="null"/>。</param>
/// <param name="Top">取得する最大件数。</param>
/// <param name="DiagnoseAuth">認証診断モードを有効にするかどうか。</param>
/// <param name="JsonOutput">JSON 形式で出力するかどうか。</param>
/// <param name="CredentialMode">使用する資格情報の種類 (<c>default</c> または <c>azurecli</c>)。</param>
internal sealed record AppArguments(string QuerySamplePath, string? SubscriptionId, string? TargetResourceId, int Top, bool DiagnoseAuth, bool JsonOutput, string CredentialMode)
{
    /// <summary>
    /// コマンドライン引数の配列を解析し、<see cref="AppArguments"/> のインスタンスを返す。
    /// </summary>
    /// <param name="args">コマンドライン引数の配列。</param>
    /// <returns>解析済みの引数を保持する <see cref="AppArguments"/>。</returns>
    /// <exception cref="ArgumentException">未知の引数またはオプション値が不正な場合。</exception>
    public static AppArguments Parse(string[] args)
    {
        // querysample.md を既定値にしつつ、必要なオプションだけを簡易な手動解析で受け取る。
        string querySamplePath = GetDefaultQuerySamplePath();
        string? subscriptionId = null;
        string? targetResourceId = null;
        int top = 50;
        bool diagnoseAuth = false;
        bool jsonOutput = false;
        string credentialMode = "default";

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];

            switch (argument)
            {
                case "--query-sample":
                    querySamplePath = GetRequiredValue(args, ref index, argument);
                    break;
                case "--subscription-id":
                    subscriptionId = GetRequiredValue(args, ref index, argument);
                    break;
                case "--target-resource-id":
                    targetResourceId = GetRequiredValue(args, ref index, argument);
                    break;
                case "--top":
                    top = int.Parse(GetRequiredValue(args, ref index, argument));
                    break;
                case "--diagnose-auth":
                    diagnoseAuth = true;
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--credential":
                    credentialMode = GetRequiredValue(args, ref index, argument).ToLowerInvariant();
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        ValidateCredentialMode(credentialMode);
        return new AppArguments(querySamplePath, subscriptionId, targetResourceId, top, diagnoseAuth, jsonOutput, credentialMode);
    }

    private static void ValidateCredentialMode(string credentialMode)
    {
        if (credentialMode is "default" or "azurecli")
        {
            return;
        }

        throw new ArgumentException("--credential must be either 'default' or 'azurecli'.");
    }

    private static string GetDefaultQuerySamplePath()
    {
        // リポジトリ直下での実行と、ビルド出力ディレクトリからの実行の両方を吸収する。
        string currentDirectoryPath = Path.Combine(Environment.CurrentDirectory, "querysample.md");
        if (File.Exists(currentDirectoryPath))
        {
            return currentDirectoryPath;
        }

        return Path.Combine(AppContext.BaseDirectory, "querysample.md");
    }

    private static string GetRequiredValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {optionName}");
        }

        index++;
        return args[index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  resourcegraphquery [--query-sample <path>] [--subscription-id <id>] [--target-resource-id <resourceId>] [--top <n>] [--diagnose-auth] [--json] [--credential <default|azurecli>]");
        Console.WriteLine();
        Console.WriteLine("The app reads the Azure CLI query from querysample.md by default.");
        Console.WriteLine("Credential modes: default = DefaultAzureCredential, azurecli = AzureCliCredential.");
        Console.WriteLine("--json outputs one change per JSON record in an array and uses syntax coloring on interactive terminals.");
    }
}

/// <summary>
/// 指定した認証モードに対応する <see cref="TokenCredential"/> を生成するファクトリ クラス。
/// </summary>
internal static class CredentialFactory
{
    // サンプル用途なので、利用者が意図した認証方式を明示的に選べるようにしている。
    /// <summary>
    /// 認証モードに対応する <see cref="TokenCredential"/> を生成して返す。
    /// </summary>
    /// <param name="credentialMode">
    /// 認証モード。<c>azurecli</c> を指定すると <see cref="AzureCliCredential"/>、
    /// それ以外は <see cref="DefaultAzureCredential"/> を返す。
    /// </param>
    /// <returns>生成した <see cref="TokenCredential"/>。</returns>
    public static TokenCredential Create(string credentialMode) => credentialMode switch
    {
        "azurecli" => new AzureCliCredential(),
        _ => new DefaultAzureCredential(),
    };
}

/// <summary>
/// 認証診断情報をコンソールに出力するクラス。
/// 認証トークンのクレーム、見えているサブスクリプション一覧、および簡易クエリの実行結果を表示する。
/// </summary>
internal static class DiagnosticsPrinter
{
    /// <summary>
    /// 認証診断情報をコンソールに非同期で出力する。
    /// </summary>
    /// <param name="credential">診断対象の <see cref="TokenCredential"/>。</param>
    /// <param name="armClient">Azure Resource Manager クライアント。</param>
    /// <param name="tenant">現在のテナント リソース。</param>
    /// <param name="subscriptionId">確認対象のサブスクリプション ID。</param>
    /// <param name="targetResourceId">確認対象のリソース ID。省略時は <see langword="null"/>。</param>
    /// <param name="cancellationToken">キャンセル トークン。</param>
    public static async Task PrintAsync(
        TokenCredential credential,
        ArmClient armClient,
        TenantResource tenant,
        string subscriptionId,
        string? targetResourceId,
        CancellationToken cancellationToken)
    {
        // 認証トークンの主体情報と、現在の資格情報から見えているサブスクリプション一覧を表示する。
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Authentication diagnostics");
        Console.WriteLine();

        TokenClaims claims = await TokenClaims.GetAsync(credential, cancellationToken);
        Console.WriteLine($"Tenant ID        : {claims.TenantId}");
        Console.WriteLine($"Object ID        : {claims.ObjectId}");
        Console.WriteLine($"Application ID   : {claims.ApplicationId}");
        Console.WriteLine($"User Principal   : {claims.UserPrincipalName}");
        Console.WriteLine();

        List<SubscriptionSummary> visibleSubscriptions = await GetVisibleSubscriptionsAsync(armClient, cancellationToken);
        Console.WriteLine($"Visible subscriptions : {visibleSubscriptions.Count}");
        foreach (SubscriptionSummary visibleSubscription in visibleSubscriptions.Take(10))
        {
            Console.WriteLine($"  - {visibleSubscription.DisplayName} ({visibleSubscription.SubscriptionId})");
        }

        if (visibleSubscriptions.Count > 10)
        {
            Console.WriteLine($"  ... {visibleSubscriptions.Count - 10} more");
        }

        Console.WriteLine();
        bool targetSubscriptionVisible = visibleSubscriptions.Any(subscription => string.Equals(subscription.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"Requested subscription visible : {targetSubscriptionVisible}");

        try
        {
            // 本命の resourcechanges クエリの前に、単純な Resources クエリが成功するかで権限不足の層を切り分ける。
            Response<ResourceQueryResult> baselineResponse = await tenant.GetResourcesAsync(CreateBaselineRequest(subscriptionId, targetResourceId), cancellationToken);
            int baselineCount = ExtractObjectArrayCount(baselineResponse.Value.Data);
            Console.WriteLine($"Baseline resources query       : Succeeded ({baselineCount} row(s))");
            Console.WriteLine("resourcechanges failure after this point usually means missing Microsoft.ResourceGraph/resourceChanges/read.");
        }
        catch (RequestFailedException exception)
        {
            Console.WriteLine($"Baseline resources query       : Failed ({exception.Status} {exception.ErrorCode})");
            Console.WriteLine("This usually means the current identity cannot read the requested subscription or resource at all.");
        }
    }

    private static ResourceQueryContent CreateBaselineRequest(string subscriptionId, string? targetResourceId)
    {
        string query = string.IsNullOrWhiteSpace(targetResourceId)
            ? "Resources | project id, name, type | limit 1"
            : $"Resources | where id =~ '{targetResourceId}' | project id, name, type | limit 1";

        var request = new ResourceQueryContent(query)
        {
            Options = new ResourceQueryRequestOptions
            {
                Top = 1,
                ResultFormat = ResultFormat.ObjectArray,
            },
        };

        request.Subscriptions.Add(subscriptionId);
        return request;
    }

    private static int ExtractObjectArrayCount(BinaryData data)
    {
        JsonElement root = data.ToObjectFromJson<JsonElement>();
        return root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() : 0;
    }

    private static async Task<List<SubscriptionSummary>> GetVisibleSubscriptionsAsync(ArmClient armClient, CancellationToken cancellationToken)
    {
        var subscriptions = new List<SubscriptionSummary>();

        await foreach (SubscriptionResource subscription in armClient.GetSubscriptions().GetAllAsync(cancellationToken: cancellationToken))
        {
            subscriptions.Add(new SubscriptionSummary(subscription.Data.DisplayName ?? string.Empty, subscription.Data.SubscriptionId ?? string.Empty));
        }

        return subscriptions;
    }
}

/// <summary>
/// サブスクリプションの表示名と ID をまとめた簡易データ クラス。
/// </summary>
/// <param name="DisplayName">サブスクリプションの表示名。</param>
/// <param name="SubscriptionId">サブスクリプション ID。</param>
internal sealed record SubscriptionSummary(string DisplayName, string SubscriptionId);

/// <summary>
/// Azure アクセス トークンから読み取った主要な JWT クレームを保持するレコード。
/// </summary>
/// <param name="TenantId">テナント ID (<c>tid</c> クレーム)。</param>
/// <param name="ObjectId">オブジェクト ID (<c>oid</c> クレーム)。</param>
/// <param name="ApplicationId">アプリケーション ID (<c>appid</c> クレーム)。</param>
/// <param name="UserPrincipalName">ユーザー プリンシパル名 (<c>upn</c> または <c>preferred_username</c> クレーム)。</param>
internal sealed record TokenClaims(string TenantId, string ObjectId, string ApplicationId, string UserPrincipalName)
{
    /// <summary>
    /// 現在の資格情報からアクセス トークンを取得し、主要クレームを解析して返す。
    /// </summary>
    /// <param name="credential">クレーム取得に使用する <see cref="TokenCredential"/>。</param>
    /// <param name="cancellationToken">キャンセル トークン。</param>
    /// <returns>解析済みのクレームを保持する <see cref="TokenClaims"/>。</returns>
    public static async Task<TokenClaims> GetAsync(TokenCredential credential, CancellationToken cancellationToken)
    {
        // Azure 管理プレーン用トークンの JWT ペイロードを読み取り、診断に必要な代表的クレームだけを抜き出す。
        AccessToken token = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
        string[] tokenParts = token.Token.Split('.');
        if (tokenParts.Length < 2)
        {
            return new TokenClaims("", "", "", "");
        }

        byte[] payloadBytes = Base64UrlDecode(tokenParts[1]);
        using JsonDocument document = JsonDocument.Parse(payloadBytes);
        JsonElement root = document.RootElement;

        return new TokenClaims(
            GetClaim(root, "tid"),
            GetClaim(root, "oid"),
            GetClaim(root, "appid"),
            GetClaim(root, "upn", "preferred_username"));
    }

    private static string GetClaim(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (root.TryGetProperty(name, out JsonElement property))
            {
                return property.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        // JWT は Base64Url 形式なので、通常の Base64 に戻してからデコードする。
        string normalized = value.Replace('-', '+').Replace('_', '/');
        int padding = 4 - normalized.Length % 4;
        if (padding is > 0 and < 4)
        {
            normalized = normalized.PadRight(normalized.Length + padding, '=');
        }

        return Convert.FromBase64String(normalized);
    }
}

/// <summary>
/// Markdown 形式のサンプル ファイルから Azure CLI クエリを読み取り、
/// アプリ向けに正規化した Resource Graph クエリを提供するクラス。
/// </summary>
internal sealed class QuerySample
{
    // Azure CLI のサンプル コマンドから -q と --subscriptions を抜き出すための正規表現。
    private static readonly Regex QueryRegex = new("-q\\s+\"(?<query>[\\s\\S]*?)\"\\s*(?:\\\\\\s*)?--subscriptions", RegexOptions.Compiled);
    private static readonly Regex SubscriptionRegex = new(@"--subscriptions\s+(?<subscription>[0-9a-fA-F-]+)", RegexOptions.Compiled);
    private static readonly Regex TargetResourceIdRegex = new(@"'(?<resourceId>[^']+)'\s*=~\s*tostring\(properties\.targetResourceId\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string DefaultProjection = "project changeTime, properties.changeType, properties.targetResourceId, properties.changes";
    private const string NormalizedProjection = "project changeTime, changeType=tostring(properties.changeType), targetResourceId=tostring(properties.targetResourceId), changeAttributes=properties.changeAttributes, changes=properties.changes";

    private QuerySample(string rawText, string queryText, string? subscriptionId)
    {
        RawText = rawText;
        QueryText = queryText;
        SubscriptionId = subscriptionId;
    }

    /// <summary>サンプル ファイルの元テキスト。</summary>
    public string RawText { get; }

    /// <summary>サンプル ファイルから抽出した Resource Graph クエリ テキスト。</summary>
    public string QueryText { get; }

    /// <summary>サンプル ファイルから抽出したサブスクリプション ID。指定がない場合は <see langword="null"/>。</summary>
    public string? SubscriptionId { get; }

    /// <summary>
    /// 指定パスのサンプル ファイルを読み取り、<see cref="QuerySample"/> のインスタンスを生成して返す。
    /// </summary>
    /// <param name="path">Markdown 形式のサンプル ファイルのパス。</param>
    /// <returns>読み取り済みの <see cref="QuerySample"/>。</returns>
    /// <exception cref="FileNotFoundException">ファイルが存在しない場合。</exception>
    /// <exception cref="InvalidOperationException">クエリ テキストを抽出できない場合。</exception>
    public static QuerySample Load(string path)
    {
        string fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Query sample file was not found: {fullPath}");
        }

        string rawText = File.ReadAllText(fullPath);
        Match queryMatch = QueryRegex.Match(rawText);

        // querysample.md は Azure CLI コマンド例をそのまま置く前提なので、-q の中身をクエリ本文として扱う。
        if (!queryMatch.Success)
        {
            throw new InvalidOperationException("Could not extract the -q query text from the sample file.");
        }

        string queryText = queryMatch.Groups["query"].Value;
        string? subscriptionId = SubscriptionRegex.Match(rawText).Groups["subscription"].Value;
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            subscriptionId = null;
        }

        return new QuerySample(rawText, queryText, subscriptionId);
    }

    /// <summary>
    /// クエリの射影を正規化し、必要に応じて対象リソース ID の条件を上書きして返す。
    /// </summary>
    /// <param name="targetResourceIdOverride">
    /// 上書きする対象リソース ID。<see langword="null"/> または空白の場合は上書きしない。
    /// </param>
    /// <returns>アプリ向けに正規化した Resource Graph クエリ テキスト。</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="targetResourceIdOverride"/> が指定されたが、クエリ内に上書き可能な条件が見つからない場合。
    /// </exception>
    public string BuildQuery(string? targetResourceIdOverride)
    {
        // 生のサンプル クエリでは後続処理しづらい列名があるため、表示側で扱いやすい射影へ正規化する。
        string normalizedQuery = NormalizeProjection(QueryText);

        if (string.IsNullOrWhiteSpace(targetResourceIdOverride))
        {
            return normalizedQuery;
        }

        // サンプル内の targetResourceId 条件だけを差し替え、他の絞り込み条件はそのまま維持する。
        if (!TargetResourceIdRegex.IsMatch(normalizedQuery))
        {
            throw new InvalidOperationException("The sample query does not contain a targetResourceId predicate that can be overridden.");
        }

        return TargetResourceIdRegex.Replace(
            normalizedQuery,
            _ => $"'{targetResourceIdOverride}' =~ tostring(properties.targetResourceId)",
            1);
    }

    private static string NormalizeProjection(string queryText)
    {
        return queryText.Contains(DefaultProjection, StringComparison.Ordinal)
            ? queryText.Replace(DefaultProjection, NormalizedProjection, StringComparison.Ordinal)
            : queryText;
    }

    /// <summary>
    /// クエリ テキストから対象リソース ID を抽出して返す。
    /// </summary>
    /// <param name="queryText">対象リソース ID を検索するクエリ テキスト。</param>
    /// <returns>抽出したリソース ID。見つからない場合は <see langword="null"/>。</returns>
    public string? ExtractTargetResourceId(string queryText)
    {
        Match match = TargetResourceIdRegex.Match(queryText);
        return match.Success ? match.Groups["resourceId"].Value : null;
    }
}

/// <summary>
/// 1 つのプロパティに対する変更前後の値を保持するレコード。
/// </summary>
/// <param name="Field">変更されたプロパティ名。</param>
/// <param name="Label">表示用のラベル名。</param>
/// <param name="Before">変更前の値。</param>
/// <param name="After">変更後の値。</param>
internal sealed record SettingChange(string Field, string Label, string Before, string After);

/// <summary>
/// App Service Plan のスケール変更ログの 1 エントリを表すレコード。
/// </summary>
/// <param name="ChangeTime">変更時刻 (UTC、<c>yyyy-MM-dd HH:mm:ss</c> 形式)。</param>
/// <param name="ChangeType">変更種別 (<c>Update</c> など)。</param>
/// <param name="Operation">変更操作の説明文 (スケールアップ・ダウンなど)。</param>
/// <param name="TargetName">対象リソースの短縮名。</param>
/// <param name="TargetResourceId">対象リソースの完全 ID。</param>
/// <param name="Execution">実行モード (<c>人間</c> または <c>自動</c>)。</param>
/// <param name="Actor">変更実行者。自動実行の場合は空文字列。</param>
/// <param name="Changes">変更されたプロパティの一覧。</param>
internal sealed record ScaleLogEntry(
    string ChangeTime,
    string ChangeType,
    string Operation,
    string TargetName,
    string TargetResourceId,
    string Execution,
    string Actor,
    IReadOnlyList<SettingChange> Changes)
{
    // 表形式では 1 セルに収まる要約が必要なので、変更差分を短い文章にまとめて返す。
    /// <summary>
    /// 変更差分を表形式の 1 セルに収まる短い要約文字列として返す。
    /// </summary>
    public string SettingDiff => FormatSettingDiff(Changes);

    // JSON 出力では、CLI で見やすいように入れ子構造へ整形してからシリアライズする。
    /// <summary>
    /// JSON 出力向けに入れ子構造へ整形した匿名オブジェクトを返す。
    /// </summary>
    /// <returns>シリアライズ用の匿名オブジェクト。</returns>
    public object ToJsonRecord() => new
    {
        changeTime = ChangeTime,
        changeType = ChangeType,
        operation = Operation,
        target = new
        {
            name = TargetName,
            resourceId = TargetResourceId,
        },
        execution = new
        {
            mode = Execution,
            actor = string.IsNullOrWhiteSpace(Actor) ? null : Actor,
        },
        diffs = Changes.Select(static change => new
        {
            field = change.Field,
            label = change.Label,
            before = change.Before,
            after = change.After,
        }),
    };

    private static string FormatSettingDiff(IReadOnlyList<SettingChange> changes)
    {
        if (changes.Count == 0)
        {
            return string.Empty;
        }

        const int maxFields = 6;
        string summary = string.Join("; ", changes.Take(maxFields).Select(static change => $"{change.Label}: {change.Before} -> {change.After}"));

        if (changes.Count > maxFields)
        {
            summary += $"; +{changes.Count - maxFields} more";
        }

        return summary;
    }
}

/// <summary>
/// Azure Resource Graph の応答 JSON を <see cref="ScaleLogEntry"/> のリストへ変換するパーサー クラス。
/// ObjectArray 形式と Table 形式の両方に対応する。
/// </summary>
internal static class ScaleLogEntryParser
{
    // App Service Plan の変更のうち、スケール関連として見せたい代表的なプロパティ名を分類しておく。
    private static readonly HashSet<string> ScaleInOutProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "sku.capacity",
        "properties.currentNumberOfWorkers",
        "properties.numberOfWorkers",
        "properties.maximumElasticWorkerCount",
        "properties.preWarmedInstanceCount",
    };

    private static readonly HashSet<string> ScaleUpDownProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "sku.name",
        "sku.tier",
        "sku.size",
        "properties.workerTierName",
        "properties.hyperV",
    };

    private static readonly Dictionary<string, string> PropertyDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sku.capacity"] = "インスタンス数",
        ["properties.currentNumberOfWorkers"] = "現在のワーカー数",
        ["properties.numberOfWorkers"] = "ワーカー数",
        ["properties.maximumElasticWorkerCount"] = "最大 Elastic Worker 数",
        ["properties.preWarmedInstanceCount"] = "事前ウォーム数",
        ["sku.name"] = "SKU",
        ["sku.tier"] = "プラン階層",
        ["sku.size"] = "サイズ",
        ["properties.workerTierName"] = "ワーカー階層",
        ["properties.perSiteScaling"] = "Per-site scaling",
        ["properties.hyperV"] = "Hyper-V",
    };

    /// <summary>
    /// Resource Graph の応答 <see cref="BinaryData"/> を解析し、<see cref="ScaleLogEntry"/> のリストを返す。
    /// </summary>
    /// <param name="data">Resource Graph が返した応答データ。</param>
    /// <returns>変換済みの <see cref="ScaleLogEntry"/> リスト。</returns>
    /// <exception cref="InvalidOperationException">予期しない応答形式の場合。</exception>
    public static IReadOnlyList<ScaleLogEntry> Parse(BinaryData data)
    {
        JsonElement root = data.ToObjectFromJson<JsonElement>();

        // Resource Graph は ObjectArray と Table の両形式を返せるため、どちらでも同じ表示モデルに寄せる。
        return root.ValueKind switch
        {
            JsonValueKind.Array => ParseObjectArray(root),
            JsonValueKind.Object => ParseTable(root),
            _ => throw new InvalidOperationException("Unexpected Resource Graph response format."),
        };
    }

    private static IReadOnlyList<ScaleLogEntry> ParseObjectArray(JsonElement array)
    {
        var entries = new List<ScaleLogEntry>();

        foreach (JsonElement item in array.EnumerateArray())
        {
            entries.Add(CreateEntry(
                TryGetString(item, "changeTime"),
                TryGetString(item, "changeType"),
                TryGetString(item, "targetResourceId"),
                GetPropertyOrDefault(item, "changeAttributes"),
                GetPropertyOrDefault(item, "changes")));
        }

        return entries;
    }

    private static IReadOnlyList<ScaleLogEntry> ParseTable(JsonElement table)
    {
        if (!table.TryGetProperty("columns", out JsonElement columnsElement) ||
            !table.TryGetProperty("rows", out JsonElement rowsElement))
        {
            throw new InvalidOperationException("Table format response did not contain columns and rows.");
        }

        string[] columns = columnsElement
            .EnumerateArray()
            .Select(static column => column.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();

        int changeTimeIndex = Array.IndexOf(columns, "changeTime");
        int changeTypeIndex = Array.IndexOf(columns, "changeType");
        int targetResourceIdIndex = Array.IndexOf(columns, "targetResourceId");
        int changeAttributesIndex = Array.IndexOf(columns, "changeAttributes");
        int changesIndex = Array.IndexOf(columns, "changes");

        var entries = new List<ScaleLogEntry>();

        foreach (JsonElement row in rowsElement.EnumerateArray())
        {
            JsonElement[] cells = row.EnumerateArray().ToArray();

            entries.Add(CreateEntry(
                GetCell(cells, changeTimeIndex),
                GetCell(cells, changeTypeIndex),
                GetCell(cells, targetResourceIdIndex),
                GetCellElement(cells, changeAttributesIndex),
                GetCellElement(cells, changesIndex)));
        }

        return entries;
    }

    private static ScaleLogEntry CreateEntry(string changeTime, string changeType, string targetResourceId, JsonElement changeAttributes, JsonElement changes)
    {
        IReadOnlyList<SettingChange> parsedChanges = ParseSettingChanges(changes);

        // 表示時に必要な派生値もここで確定しておくと、出力レイヤーを単純に保てる。
        return new ScaleLogEntry(
            FormatTimestamp(changeTime),
            string.IsNullOrWhiteSpace(changeType) ? "不明" : changeType,
            DetermineOperation(changeType, changes),
            GetTargetName(targetResourceId),
            targetResourceId,
            DetermineExecution(changeAttributes),
            DetermineActor(changeAttributes),
            parsedChanges);
    }

    private static string GetCell(JsonElement[] cells, int index)
    {
        if (index < 0 || index >= cells.Length)
        {
            return string.Empty;
        }

        JsonElement value = cells[index];
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => value.ToString(),
        };
    }

    private static JsonElement GetCellElement(JsonElement[] cells, int index)
    {
        if (index < 0 || index >= cells.Length)
        {
            return default;
        }

        return cells[index];
    }

    private static JsonElement GetPropertyOrDefault(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out JsonElement property)
            ? property
            : default;
    }

    private static string TryGetString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out JsonElement property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => property.ToString(),
        };
    }

    private static IReadOnlyList<SettingChange> ParseSettingChanges(JsonElement changes)
    {
        if (changes.ValueKind == JsonValueKind.Undefined || changes.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (changes.ValueKind != JsonValueKind.Object)
        {
            return [new SettingChange("changes", "changes", "(null)", FormatValue(changes))];
        }

        // Resource Graph の changes オブジェクトを、UI 表示しやすい before / after の一覧へ変換する。
        SettingChange[] propertyChanges = changes
            .EnumerateObject()
            .Select(static property => CreateSettingChange(property.Name, property.Value))
            .Where(static change => change is not null)
            .Select(static change => change!)
            .ToArray();

        return propertyChanges;
    }

    private static string DetermineOperation(string changeType, JsonElement changes)
    {
        if (!string.Equals(changeType, "Update", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(changeType) ? "不明" : changeType;
        }

        // Update の中でも、変更されたプロパティ群から「スケールアップ・ダウン」等の説明文を推測する。
        if (changes.ValueKind != JsonValueKind.Object)
        {
            return "プラン変更";
        }

        bool hasScaleInOut = false;
        bool hasScaleUpDown = false;
        bool hasPlanChange = false;

        foreach (JsonProperty property in changes.EnumerateObject())
        {
            if (IsScaleInOutProperty(property.Name))
            {
                hasScaleInOut = true;
                continue;
            }

            if (IsScaleUpDownProperty(property.Name))
            {
                hasScaleUpDown = true;
                continue;
            }

            hasPlanChange = true;
        }

        var operations = new List<string>();
        if (hasScaleUpDown)
        {
            operations.Add("スケールアップ・ダウン");
        }

        if (hasScaleInOut)
        {
            operations.Add("スケールイン・アウト");
        }

        if (hasPlanChange || operations.Count == 0)
        {
            operations.Add("プラン変更");
        }

        return string.Join(" / ", operations);
    }

    private static bool IsScaleInOutProperty(string propertyName)
    {
        return ScaleInOutProperties.Contains(propertyName)
            || propertyName.Contains("numberOfWorkers", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("workerCount", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("preWarmedInstanceCount", StringComparison.OrdinalIgnoreCase)
            || propertyName.EndsWith(".capacity", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScaleUpDownProperty(string propertyName)
    {
        return ScaleUpDownProperties.Contains(propertyName)
            || propertyName.StartsWith("sku.", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("workerTier", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetermineExecution(JsonElement changeAttributes)
    {
        // changedByType=User のときだけ人手による変更と見なし、それ以外は自動実行として扱う。
        string changedByType = TryGetNestedString(changeAttributes, "changedByType");
        return string.Equals(changedByType, "User", StringComparison.OrdinalIgnoreCase)
            ? "人間"
            : "自動";
    }

    private static string DetermineActor(JsonElement changeAttributes)
    {
        string changedByType = TryGetNestedString(changeAttributes, "changedByType");
        if (!string.Equals(changedByType, "User", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return TryGetNestedString(changeAttributes, "changedBy");
    }

    private static string TryGetNestedString(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(propertyName, out JsonElement property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => property.ToString(),
        };
    }

    private static SettingChange? CreateSettingChange(string propertyName, JsonElement change)
    {
        string label = PropertyDisplayNames.TryGetValue(propertyName, out string? displayName)
            ? displayName
            : propertyName;

        // previousValue / newValue が空の項目は差分として表示しても情報量が少ないため省く。
        if (change.ValueKind != JsonValueKind.Object)
        {
            return new SettingChange(propertyName, label, "(null)", FormatValue(change));
        }

        string previousValue = TryGetNestedString(change, "previousValue");
        string newValue = TryGetNestedString(change, "newValue");

        if (string.IsNullOrWhiteSpace(previousValue) && string.IsNullOrWhiteSpace(newValue))
        {
            return null;
        }

        return new SettingChange(propertyName, label, NormalizeValue(previousValue), NormalizeValue(newValue));
    }

    private static string FormatValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeValue(value.GetString() ?? string.Empty),
            JsonValueKind.Null => "(null)",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString(),
        };
    }

    private static string NormalizeValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
            ? "(null)"
            : value;
    }

    private static string GetTargetName(string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return string.Empty;
        }

        string[] segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? resourceId : segments[^1];
    }

    private static string FormatTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : value;
    }
}

/// <summary>
/// <see cref="ScaleLogEntry"/> のリストを JSON 形式でコンソールに出力するクラス。
/// 対話端末では色付きで表示し、リダイレクト時は純粋な整形済み JSON を出力する。
/// </summary>
internal static class JsonConsole
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// <see cref="ScaleLogEntry"/> のリストを JSON 形式でコンソールに出力する。
    /// </summary>
    /// <param name="entries">出力する <see cref="ScaleLogEntry"/> のリスト。</param>
    public static void Print(IReadOnlyList<ScaleLogEntry> entries)
    {
        // リダイレクト時は純粋な JSON、対話端末では色付き整形を使い分ける。
        object[] records = entries.Select(static entry => entry.ToJsonRecord()).ToArray();

        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(JsonSerializer.Serialize(records, SerializerOptions));
            return;
        }

        JsonElement root = JsonSerializer.SerializeToElement(records, SerializerOptions);
        WriteElement(root, 0);
        Console.WriteLine();
    }

    private static void WriteElement(JsonElement element, int indent)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, indent);
                break;
            case JsonValueKind.Array:
                WriteArray(element, indent);
                break;
            case JsonValueKind.String:
                WriteColored(JsonSerializer.Serialize(element.GetString(), SerializerOptions), ConsoleColor.Yellow);
                break;
            case JsonValueKind.Number:
                WriteColored(element.GetRawText(), ConsoleColor.Magenta);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                WriteColored(element.GetRawText(), ConsoleColor.Cyan);
                break;
            case JsonValueKind.Null:
                WriteColored("null", ConsoleColor.DarkGray);
                break;
            default:
                Console.Write(element.GetRawText());
                break;
        }
    }

    private static void WriteObject(JsonElement element, int indent)
    {
        JsonProperty[] properties = element.EnumerateObject().ToArray();
        Console.WriteLine("{");

        for (int index = 0; index < properties.Length; index++)
        {
            JsonProperty property = properties[index];
            WriteIndent(indent + 1);
            WriteColored(JsonSerializer.Serialize(property.Name, SerializerOptions), ConsoleColor.Green);
            Console.Write(": ");
            WriteElement(property.Value, indent + 1);

            if (index < properties.Length - 1)
            {
                Console.Write(",");
            }

            Console.WriteLine();
        }

        WriteIndent(indent);
        Console.Write("}");
    }

    private static void WriteArray(JsonElement element, int indent)
    {
        JsonElement[] items = element.EnumerateArray().ToArray();
        Console.WriteLine("[");

        for (int index = 0; index < items.Length; index++)
        {
            WriteIndent(indent + 1);
            WriteElement(items[index], indent + 1);

            if (index < items.Length - 1)
            {
                Console.Write(",");
            }

            Console.WriteLine();
        }

        WriteIndent(indent);
        Console.Write("]");
    }

    private static void WriteIndent(int indent)
    {
        Console.Write(new string(' ', indent * 2));
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        ConsoleColor previousColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previousColor;
    }
}

/// <summary>
/// <see cref="ScaleLogEntry"/> のリストを固定幅の表形式でコンソールに出力するクラス。
/// 列幅を自動計算し、長い文字列は省略表示する。
/// </summary>
internal static class ConsoleTable
{
    /// <summary>
    /// 指定したアイテムのリストを固定幅の表形式でコンソールに出力する。
    /// </summary>
    /// <typeparam name="T">表示対象のアイテムの型。</typeparam>
    /// <param name="items">表示するアイテムのリスト。</param>
    /// <param name="mapRow">アイテムを列値の配列にマッピングする関数。</param>
    /// <param name="headers">列見出しの配列。</param>
    public static void Print<T>(IReadOnlyList<T> items, Func<T, string[]> mapRow, string[] headers)
    {
        // 各列の最大幅を先に求めておき、長い文字列は列ごとの上限で省略表示する。
        List<string[]> rows = items.Select(mapRow).ToList();

        int[] maxWidths = headers.Select(static header => header.Length).ToArray();

        foreach (string[] row in rows)
        {
            for (int index = 0; index < row.Length; index++)
            {
                int cappedLength = Math.Min(row[index].Length, GetMaxWidth(index));
                maxWidths[index] = Math.Max(maxWidths[index], cappedLength);
            }
        }

        PrintRow(headers, maxWidths);
        PrintSeparator(maxWidths);

        foreach (string[] row in rows)
        {
            string[] normalized = row
                .Select((value, index) => value.Length > GetMaxWidth(index) ? value[..(GetMaxWidth(index) - 1)] + "…" : value)
                .ToArray();
            PrintRow(normalized, maxWidths);
        }
    }

    private static int GetMaxWidth(int columnIndex) => columnIndex switch
    {
        0 => 19,
        1 => 30,
        2 => 24,
        3 => 110,
        4 => 8,
        5 => 40,
        _ => 80,
    };

    private static void PrintRow(string[] values, int[] widths)
    {
        string line = string.Join(" | ", values.Select((value, index) => value.PadRight(widths[index])));
        Console.WriteLine(line);
    }

    private static void PrintSeparator(int[] widths)
    {
        string line = string.Join("-+-", widths.Select(static width => new string('-', width)));
        Console.WriteLine(line);
    }
}
