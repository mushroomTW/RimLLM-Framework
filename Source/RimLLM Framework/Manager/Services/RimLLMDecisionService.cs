using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework;
using RimLLM_Framework.Core;
using TypeSafeAI;

namespace RimLLM_Framework.Manager.Services;

/// <summary>
/// 決策請求的結果＋在請求開始時捕捉的歸屬資訊。供應商與模型在連線解析當下就固定下來，
/// await 之後不得重讀設定：請求途中玩家切換啟用供應商時，用量才不會記到新供應商
/// （與 RimLLMEmbeddingClient 對 RimLLMEmbeddingResult 的同一個考量）。
/// </summary>
internal sealed record DecisionEvaluation(QuestionSetResult Result, string ProviderId, string ModelName);

/// <summary>
/// 設定頁預覽某個決策供應商時要送出的參數。抓模型清單與連線測試共用同一組參數來源，
/// 因此這三個值只由 <see cref="RimLLMDecisionService.GetPreviewArguments"/> 一處決定。
/// </summary>
internal sealed record DecisionPreviewArguments(string Endpoint, string Model, string ApiKey);

/// <summary>
/// 以 TypeSafeAI SDK（TypeSafe System One / Jev）為後端的決策服務。
/// 僅提供通用的 EvaluateAsync 與模型清單查詢；不內建任何情境判斷、分流或安全閘門。
/// 所有失敗都收斂為 <see cref="RimLLMException"/>（對話與 embedding 的同一份契約），
/// 只有呼叫端取消維持 <see cref="OperationCanceledException"/>。
/// </summary>
public class RimLLMDecisionService
{
    private readonly IRimLLMSettings _settings;

    public RimLLMDecisionService(IRimLLMSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 決策供應商實際要用的金鑰。Gemini 的決策金鑰留空時沿用對話的 Gemini 金鑰
    /// （與 Embedding 沿用對話金鑰的規則相同），玩家只要貼一次 Gemini 金鑰。
    /// 其他決策供應商與對話供應商是不同服務，不沿用。
    /// </summary>
    internal static string GetEffectiveApiKey(IRimLLMSettings settings, string providerId)
    {
        string key = settings.GetDecisionApiKey(providerId);
        string chatProviderId = GetChatKeyProviderId(providerId);
        if (string.IsNullOrWhiteSpace(key) && chatProviderId != null)
        {
            key = settings.GetActiveApiKey(chatProviderId);
        }
        return key ?? "";
    }

    /// <summary>該決策供應商在目錄中宣告可沿用的對話供應商；未知或不沿用時為 null。</summary>
    private static string GetChatKeyProviderId(string providerId)
    {
        return RimLLMDecisionProviderCatalog.TryGet(providerId, out RimLLMDecisionProvider provider)
            ? provider.ChatKeyProviderId
            : null;
    }

    /// <summary>
    /// 與 <see cref="GetEffectiveApiKey"/> 同一規則的「有沒有金鑰」查詢。
    /// 不呼叫 GetActiveApiKey，免得純查詢也推進多金鑰的輪替位置。
    /// </summary>
    internal static bool HasEffectiveApiKey(IRimLLMSettings settings, string providerId)
    {
        if (!string.IsNullOrWhiteSpace(settings.GetDecisionApiKey(providerId)))
        {
            return true;
        }
        string chatProviderId = GetChatKeyProviderId(providerId);
        return chatProviderId != null && !string.IsNullOrWhiteSpace(settings.GetApiKey(chatProviderId));
    }

    /// <summary>模型清單在設定檔中的快取鍵，與 Embedding 的清單分開存放。</summary>
    public static string GetModelListKey(string providerId)
    {
        return "decision:" + (providerId ?? "");
    }

    /// <summary>
    /// 把使用者貼上的完整 API 位址（/v1/systemone，或 Gemini 的 /v1beta/decisions、/v1beta）
    /// 縮回 TypeSafeClient 需要的 origin。
    /// </summary>
    internal static string NormalizeBaseEndpoint(string endpoint, string providerId)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = RimLLMDecisionProviderCatalog.GetDefaultEndpoint(providerId);
        }

        string normalized = endpoint.Trim().TrimEnd('/');
        // /v1beta 只對 Gemini 主機有意義；其他閘道可能就掛在 /v1beta 底下，不能拿掉。
        string[] suffixes = GeminiDecisionsAdapterHandler.Targets(normalized)
            ? ["/v1/systemone", "/v1beta/decisions", "/v1beta"]
            : ["/v1/systemone"];
        string suffix = suffixes.FirstOrDefault(s => normalized.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if (suffix != null)
        {
            normalized = normalized.Substring(0, normalized.Length - suffix.Length).TrimEnd('/');
        }
        return normalized;
    }

    private static readonly object HttpClientLock = new();
    private static Func<HttpMessageHandler> _httpClientFactoryKey;
    private static HttpClient _defaultHttpClient;
    private static HttpClient _geminiHttpClient;

    /// <summary>
    /// 取得共用的 HttpClient。TypeSafeClient 不會釋放外部傳入的 HttpClient（見其建構子文件），
    /// 因此可以跨請求重用；每次新建 handler 會讓每個決策請求都重做 TCP／TLS 握手。
    /// 依端點是否為 Gemini 分成兩條固定的 handler 鏈；兩個轉接 handler 都沒有逐請求狀態，可安全共用。
    /// <see cref="MessageHandlerFactory"/> 被換掉時（測試接縫）重建，舊的 client 不主動釋放，
    /// 避免打斷其他執行緒上仍在進行的請求。
    /// </summary>
    private static HttpClient GetHttpClient(string endpoint)
    {
        bool gemini = GeminiDecisionsAdapterHandler.Targets(endpoint);
        lock (HttpClientLock)
        {
            if (!ReferenceEquals(_httpClientFactoryKey, MessageHandlerFactory))
            {
                _httpClientFactoryKey = MessageHandlerFactory;
                _defaultHttpClient = null;
                _geminiHttpClient = null;
            }

            return gemini
                ? _geminiHttpClient ??= CreateHttpClient(gemini: true)
                : _defaultHttpClient ??= CreateHttpClient(gemini: false);
        }
    }

    private static HttpClient CreateHttpClient(bool gemini)
    {
        // TypeSafeClient 自己管理逾時；HttpClient 的逾時必須關掉，否則會比 SDK 先中斷。
        HttpMessageHandler handler = MessageHandlerFactory();
        if (gemini)
        {
            // Gemini Decisions API 的路徑、金鑰標頭與欄位名稱和 System One 不同，在傳輸層互轉。
            handler = new GeminiDecisionsAdapterHandler { InnerHandler = handler };
        }
        else
        {
            // OpenRouter 的路徑與 System One 不同；只對 openrouter.ai 主機生效，其他端點原樣放行。
            handler = new OpenRouterDecisionsAdapterHandler { InnerHandler = handler };
        }
        // 本機服務與 OpenRouter 的 /v1/models 是 OpenAI 格式，轉成 SDK 能解析的形狀。
        handler = new OpenAiModelListAdapterHandler { InnerHandler = handler };
        // SDK 豁免：要注入上面的傳輸層 handler，只能用 TypeSafeClient 接收 HttpClient 的建構子；請求本身仍由 SDK 發出。
#pragma warning disable RS0030
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
#pragma warning restore RS0030
    }

    /// <summary>
    /// 建立送出決策請求的底層 HTTP 處理器。預設走真實網路；
    /// 測試以這個內部接縫換掉它，讓假端點不必連網就能攔截請求並回應。
    /// 連線會被共用的 HttpClient 長期持有，因此設定連線壽命，讓遊戲長時間執行時仍會重新解析 DNS。
    /// </summary>
    internal static Func<HttpMessageHandler> MessageHandlerFactory { get; set; } =
        () => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

    private TypeSafeClientOptions CreateOptions(string endpoint, string model, string apiKey)
    {
        return new TypeSafeClientOptions
        {
            // 明文本機端點與 HTTPS 端點都原樣交給 SDK。
            BaseUrl = DecisionEndpointRouting.GetBaseUrl(endpoint),
            // 沒有預設模型：只抓模型清單時模型可以是空的，送決策前由 RequireModel 擋下。
            DefaultModel = model ?? "",
            ApiKey = apiKey ?? "",
            Timeout = TimeSpan.FromSeconds(_settings.ApiTimeout > 0 ? _settings.ApiTimeout : 30),
            // SDK 預設會重試 2 次；框架自己有備援鏈，重試交給它，否則一次失敗會被放大成三倍等待。
            MaxRetries = 0
        };
    }

    /// <summary>
    /// 解析指定供應商要用的模型名稱：逐次覆寫優先，其次供應商自己的設定。
    /// 沒有預設模型，兩者都沒有就回傳空字串，由玩家到設定頁選擇。
    /// </summary>
    private string ResolveModel(string providerId, string modelOverride)
    {
        string model = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride
            : _settings.GetDecisionModel(providerId);
        return string.IsNullOrWhiteSpace(model) ? "" : model.Trim();
    }

    /// <summary>只用來建立查模型清單的 TypeSafeClient；清單請求不帶模型名稱，這個值不會送出。</summary>
    private const string ModelListPlaceholderModel = "model-list";

    /// <summary>送出決策前確認已選模型；沒有預設模型可退，未選就不送出請求。</summary>
    private static void RequireModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new RimLLMException(
                LLMError.Unknown,
                "No decision model is selected; pick one in the Decision settings tab first.");
        }
    }

    /// <summary>
    /// 解析指定供應商的連線資訊，並一併在這裡捕捉供歸屬用的供應商 id。
    /// <paramref name="providerId"/> 為空時取啟用中的供應商；
    /// 未啟用、供應商不存在或沒有金鑰都在此拒絕，不把無效請求送出去。
    /// </summary>
    /// <param name="providerId">要使用的決策供應商 id；null 或空字串代表使用啟用中的供應商。</param>
    /// <param name="modelOverride">逐次覆寫的模型名稱；為空時用該供應商設定中的模型。</param>
    private DecisionConnection ResolveConnection(string providerId, string modelOverride = null)
    {
        string resolvedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? _settings.DecisionProvider
            : providerId.Trim();

        // 全域停用是玩家的明確選擇：第三方 Mod 即使指定了供應商，也不得花用玩家留在設定裡的金鑰。
        if (string.IsNullOrEmpty(resolvedProviderId) ||
            string.Equals(resolvedProviderId, RimLLMDecisionProviderCatalog.Disabled, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(_settings.DecisionProvider) ||
            string.Equals(_settings.DecisionProvider, RimLLMDecisionProviderCatalog.Disabled, StringComparison.Ordinal))
        {
            throw new RimLLMException(
                LLMError.Unknown,
                "No decision provider is enabled; enable one in the Decision settings tab first.");
        }

        if (!RimLLMDecisionProviderCatalog.TryGet(resolvedProviderId, out _))
        {
            throw new RimLLMException(
                LLMError.Unknown,
                $"Unknown decision provider '{resolvedProviderId}'.");
        }

        string endpoint = NormalizeBaseEndpoint(_settings.GetDecisionEndpoint(resolvedProviderId), resolvedProviderId);
        string apiKey = GetEffectiveApiKey(_settings, resolvedProviderId);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // 本機架設的服務多半不驗證金鑰；遠端供應商少了金鑰就是不能送。
            if (!DecisionEndpointRouting.IsPlaintext(endpoint))
            {
                throw new RimLLMException(
                    LLMError.InvalidKey,
                    $"No API key is configured for the decision provider '{resolvedProviderId}'.");
            }
            apiKey = DecisionEndpointRouting.PlaintextPlaceholderKey;
        }

        return new DecisionConnection(
            resolvedProviderId, endpoint, ResolveModel(resolvedProviderId, modelOverride), apiKey);
    }

    /// <summary>
    /// 目前決策供應商的可用狀態。純查詢，不發任何網路請求。
    /// 未啟用決策供應商時回報 <c>Enabled = false</c>，不擲例外——
    /// 「玩家還沒設定」是查詢結果，不是錯誤。
    /// </summary>
    internal DecisionAvailability GetAvailability()
    {
        string providerId = _settings.DecisionProvider;
        if (string.IsNullOrEmpty(providerId) ||
            string.Equals(providerId, RimLLMDecisionProviderCatalog.Disabled, StringComparison.Ordinal) ||
            !RimLLMDecisionProviderCatalog.TryGet(providerId, out _))
        {
            // 設定若指向已不存在的供應商 id（自訂設定實作者才可能發生；內建設定在載入時
            // 就會把它正規化回 Disabled），同樣視為「沒有可用的決策供應商」。
            return new DecisionAvailability
            {
                Enabled = false,
                ProviderId = RimLLMDecisionProviderCatalog.Disabled,
                Model = "",
                HasKey = false,
                RequiresKey = false,
                Endpoint = ""
            };
        }

        string endpoint = NormalizeBaseEndpoint(_settings.GetDecisionEndpoint(providerId), providerId);
        return new DecisionAvailability
        {
            Enabled = true,
            ProviderId = providerId,
            Model = ResolveModel(providerId, null),
            // 只回報「有沒有金鑰」，金鑰本身絕不外洩。
            HasKey = HasEffectiveApiKey(_settings, providerId),
            // 明文本機端點是玩家自架的服務，可以不填金鑰。
            RequiresKey = !DecisionEndpointRouting.IsPlaintext(endpoint),
            Endpoint = endpoint
        };
    }

    /// <summary>
    /// 設定頁預覽某個供應商（可能尚未啟用）時要送出的端點、模型與金鑰。
    /// 設定頁的「抓取模型清單」與「連線測試」都走這裡，參數來源只有一處。
    /// </summary>
    internal DecisionPreviewArguments GetPreviewArguments(string providerId)
    {
        return new DecisionPreviewArguments(
            _settings.GetDecisionEndpoint(providerId),
            ResolveModel(providerId, null),
            GetEffectiveApiKey(_settings, providerId));
    }

    /// <summary>
    /// 把使用者輸入的端點與金鑰做基本檢查；參數化的 UI 路徑（抓清單、測試連線）走這裡，未啟用也可預先連線。
    /// 明文本機端點不要求金鑰，改用佔位值。
    /// </summary>
    private static string EnsureUsableKey(string apiKey, string endpoint)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        if (DecisionEndpointRouting.IsPlaintext(endpoint))
        {
            return DecisionEndpointRouting.PlaintextPlaceholderKey;
        }

        throw new RimLLMException(
            LLMError.InvalidKey,
            "No API key is configured for this decision provider.");
    }

    /// <summary>抓取目前啟用供應商的可用模型；模型清單查詢不消耗推論額度。</summary>
    public async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DecisionConnection connection = ResolveConnection(null);
            return await FetchModelsCoreAsync(
                    connection.Endpoint, connection.Model, connection.ApiKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    /// <summary>以指定端點與金鑰抓取模型清單，供設定頁在尚未啟用時預覽。</summary>
    public async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(
        string endpoint,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string normalizedEndpoint = NormalizeBaseEndpoint(endpoint, _settings.DecisionProvider);
            string usableKey = EnsureUsableKey(apiKey, normalizedEndpoint);
            return await FetchModelsCoreAsync(
                    normalizedEndpoint, model, usableKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    /// <summary>
    /// 抓取指定供應商的可用模型清單（回傳模型名稱字串）。
    /// 端點與金鑰取自該供應商在框架設定中保存的值；
    /// <paramref name="providerId"/> 為空時用啟用中的供應商。
    /// 模型清單查詢不消耗推論額度，也不計入每日預算。
    /// </summary>
    public async Task<IReadOnlyList<string>> FetchDecisionModelsAsync(
        string providerId, CancellationToken cancellationToken = default)
    {
        try
        {
            DecisionConnection connection = ResolveConnection(providerId);
            IReadOnlyList<ModelInfo> models = await FetchModelsCoreAsync(
                    connection.Endpoint, connection.Model, connection.ApiKey, cancellationToken)
                .ConfigureAwait(false);

            List<string> names = new(models?.Count ?? 0);
            if (models != null)
            {
                foreach (string name in models.Where(m => m != null && !string.IsNullOrEmpty(m.Name)).Select(m => m.Name))
                {
                    names.Add(name);
                }
            }
            return names;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    private async Task<IReadOnlyList<ModelInfo>> FetchModelsCoreAsync(
        string normalizedEndpoint, string model, string apiKey, CancellationToken cancellationToken)
    {
        HttpClient http = GetHttpClient(normalizedEndpoint);
        // TypeSafeClientOptions 要求 DefaultModel 非空，但查清單不會送出它；玩家還沒選模型時填佔位名稱。
        string clientModel = string.IsNullOrWhiteSpace(model) ? ModelListPlaceholderModel : model;
        using var client = new TypeSafeClient(http, CreateOptions(normalizedEndpoint, clientModel, apiKey));
        ModelsResponse response =
            await client.Models.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return response?.Models ?? [];
    }

    /// <summary>
    /// 送出一個最小的 Noul 問題，實際驗證端點、金鑰與回應格式。
    /// 與只查 /v1/models 相比，這能抓到「模型清單可用但決策端點不相容」的情況。
    /// </summary>
    public async Task TestConnectionAsync(
        string endpoint,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            RequireModel(model);
            var plan = new QuestionSet();
            plan.Noul("Is this request reachable?", id: "reachable");

            string normalizedEndpoint = NormalizeBaseEndpoint(endpoint, _settings.DecisionProvider);
            string usableKey = EnsureUsableKey(apiKey, normalizedEndpoint);
            HttpClient http = GetHttpClient(normalizedEndpoint);
            using var client = new TypeSafeClient(http, CreateOptions(normalizedEndpoint, model, usableKey));
            var requestOptions = new RequestOptions { Model = model };
            await client
                .SystemOneAsync("RimLLM decision connection test", plan, requestOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    /// <summary>執行一次決策請求。</summary>
    public async Task<QuestionSetResult> EvaluateAsync(
        string state,
        QuestionSet plan,
        string model = null,
        CancellationToken cancellationToken = default)
    {
        return (await EvaluateDetailedAsync(state, plan, model, cancellationToken).ConfigureAwait(false)).Result;
    }

    /// <summary>執行一次決策請求，並帶回在請求開始時捕捉的供應商／模型歸屬資訊。</summary>
    internal async Task<DecisionEvaluation> EvaluateDetailedAsync(
        string state,
        QuestionSet plan,
        string model = null,
        CancellationToken cancellationToken = default)
    {
        // 沒有指定供應商的請求才走備援鏈；呼叫端明確指定供應商的路徑
        //（EvaluateWithProviderDetailedAsync）是它自己的選擇，不替它換家。
        List<string> candidates = BuildFailoverCandidates();
        if (candidates.Count <= 1)
        {
            return await EvaluateWithProviderDetailedAsync(
                    state, plan, _settings.DecisionProvider, model, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ServiceProviderFailover.RunAsync(
            candidates,
            // 逐次覆寫的模型名稱是針對主供應商的，備援供應商用自己保存的模型。
            (providerId, _) => EvaluateWithProviderDetailedAsync(
                state, plan, providerId, providerId == candidates[0] ? model : null, cancellationToken),
            (providerId, ex) =>
            {
                if (RimLLMLog.Enabled)
                {
                    RimLLMLog.Warning(
                        $"[RimLLM] Decision provider '{providerId}' failed ({RimLLMLog.SanitizeForLog(ex.Message, 200)}); trying the next fallback provider.");
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// 依序嘗試的決策供應商：啟用中的供應商加上備援鏈。備援供應商沒選模型、沒有金鑰（本機明文端點除外）
    /// 或已不在目錄中就直接略過：打過去只會得到一個「尚未設定」的錯誤，還會蓋掉主供應商真正的失敗原因。
    /// </summary>
    private List<string> BuildFailoverCandidates()
    {
        List<string> all = ServiceProviderFailover.BuildCandidates(
            _settings.DecisionProvider, _settings.DecisionFallbackChain, RimLLMDecisionProviderCatalog.Disabled);
        var usable = new List<string>(all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            if (i == 0 || IsBackupUsable(all[i]))
            {
                usable.Add(all[i]);
            }
        }
        return usable;
    }

    private bool IsBackupUsable(string providerId)
    {
        if (!RimLLMDecisionProviderCatalog.TryGet(providerId, out _) ||
            string.IsNullOrWhiteSpace(ResolveModel(providerId, null)))
        {
            return false;
        }
        string endpoint = NormalizeBaseEndpoint(_settings.GetDecisionEndpoint(providerId), providerId);
        return HasEffectiveApiKey(_settings, providerId) || DecisionEndpointRouting.IsPlaintext(endpoint);
    }

    /// <summary>
    /// 以指定供應商執行一次決策請求，並帶回在請求開始時捕捉的供應商／模型歸屬資訊。
    /// 端點與金鑰取自該供應商在框架設定中保存的值；
    /// <paramref name="providerId"/> 為空時等同 <see cref="EvaluateDetailedAsync"/>（使用啟用中的供應商）。
    /// </summary>
    internal async Task<DecisionEvaluation> EvaluateWithProviderDetailedAsync(
        string state,
        QuestionSet plan,
        string providerId,
        string model = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(state)) throw new ArgumentNullException(nameof(state));
            ArgumentNullException.ThrowIfNull(plan);

            DecisionConnection connection = ResolveConnection(providerId, model);
            string selectedModel = connection.Model;
            RequireModel(selectedModel);

            HttpClient http = GetHttpClient(connection.Endpoint);
            using var client = new TypeSafeClient(http, CreateOptions(connection.Endpoint, selectedModel, connection.ApiKey));
            var requestOptions = new RequestOptions { Model = selectedModel };
            QuestionSetResult result = await client
                .SystemOneAsync(state, plan, requestOptions, cancellationToken)
                .ConfigureAwait(false);
            return new DecisionEvaluation(result, connection.ProviderId, selectedModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RimLLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw TranslateDecisionError(ex);
        }
    }

    /// <summary>
    /// 把 TypeSafeAI SDK 與 HTTP 層的例外收斂成 <see cref="RimLLMException"/>，與對話、embedding 的錯誤契約一致：
    /// 呼叫端 mod 依 README 以 <see cref="RimLLMException"/> + <see cref="LLMError"/> 分流，而不是按 SDK 的型別。
    /// 呼叫端取消（OperationCanceledException）與已是 RimLLMException 的例外由呼叫端的 catch 過濾器先行放行，
    /// 不會進到這裡。
    /// </summary>
    internal static Exception TranslateDecisionError(Exception ex)
    {
        return ex switch
        {
            // HTTP 狀態碼 → LLMError 的對照由既有 LLMErrorMapper 單一來源處理；
            // TypeSafeApiException 帶 StatusCode 與 Retry-After，正好可以完整重用。
            TypeSafeApiException apiError => LLMErrorMapper.CreateException(
                                (int)apiError.StatusCode,
                                $"Decision API: {RimLLMLog.SanitizeForLog(apiError.Message, 300)}",
                                retryAfter: apiError.RetryAfter,
                                innerException: apiError),
            // TypeSafeTimeoutException 繼承 TypeSafeConnectionException，必須排在前面。
            TypeSafeTimeoutException timeout => new RimLLMException(
                                LLMError.Timeout,
                                $"Decision API: {RimLLMLog.SanitizeForLog(timeout.Message, 300)}",
                                innerException: timeout),
            TypeSafeConnectionException connection => new RimLLMException(
                                LLMError.NetworkError,
                                $"Decision API: {RimLLMLog.SanitizeForLog(connection.Message, 300)}",
                                innerException: connection),
            TypeSafeResponseValidationException invalidResponse => new RimLLMException(
                                LLMError.InvalidResponse,
                                $"Decision API: {RimLLMLog.SanitizeForLog(invalidResponse.Message, 300)}",
                                innerException: invalidResponse),
            // TypeSafeClient 建立時的金鑰檢查：與 ResolveActiveConnection 的前置檢查同一語意，
            // 但 SDK 訊息是英文原文，統一成 InvalidKey 並保留原文供除錯。
            TypeSafeException missingKey when missingKey.Message != null &&
                                                               missingKey.Message.Contains("API key", StringComparison.Ordinal) => new RimLLMException(
                                LLMError.InvalidKey,
                                $"Decision API: {RimLLMLog.SanitizeForLog(missingKey.Message, 300)}",
                                innerException: missingKey),
            TypeSafeException sdkError => new RimLLMException(
                                LLMError.Unknown,
                                $"Decision API: {RimLLMLog.SanitizeForLog(sdkError.Message, 300)}",
                                innerException: sdkError),
            TimeoutException => new RimLLMException(
                                LLMError.Timeout,
                                $"Decision API: {RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                                innerException: ex),
            UriFormatException badUri => new RimLLMException(
                                LLMError.Unknown,
                                "Decision API: the configured endpoint is not a valid absolute URI. " +
                                RimLLMLog.SanitizeForLog(badUri.Message, 300),
                                innerException: badUri),
            _ => new RimLLMException(
                                LLMError.Unknown,
                                $"Decision API: {RimLLMLog.SanitizeForLog(ex.Message, 300)}",
                                innerException: ex),
        };
    }

    private sealed record DecisionConnection(string ProviderId, string Endpoint, string Model, string ApiKey);
}
