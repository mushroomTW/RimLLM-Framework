using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 框架內建的本機 OpenAI 相容端點。第三方 Mod 只要能設定 Base URL，就把它指向這裡，
/// 之後該 Mod 的所有模型呼叫都由框架的備援鏈處理，不需要為每個 Mod、每項功能寫專屬補丁。
/// </summary>
/// <remarks>
/// <para>
/// 路徑格式：<c>/t/{sessionToken}/m/{modId}/v1/{chat/completions | responses | models}</c>。以 modId 區分來源，
/// 才能套用該 Mod 的接管開關、專用模型與用量歸屬；sessionToken 為每次啟動隨機產生，防止本機其他程序未經授權存取。
/// </para>
/// <para>
/// 只綁定 127.0.0.1 並自動挑選空閒埠；帶有 <c>Origin</c> 標頭或 <c>Host</c> 不符的請求一律拒絕，
/// 避免外部網頁或非本機來源借用玩家的金鑰額度。
/// </para>
/// </remarks>
internal static class LocalOpenAIEndpoint
{
    internal delegate Task<string> Handler(string payloadJson, bool chatProtocol, CancellationToken cancellationToken);

    private const int MaxBodyBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim RequestConcurrencyGate = new(32, 32);
    private static readonly string SessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private static readonly byte[] SessionTokenBytes = Encoding.UTF8.GetBytes(SessionToken);

    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();
    private static HttpListener? _listener;
    private static CancellationTokenSource? _shutdown;
    private static int _port;

    public static void Register(string modId, Handler handler)
    {
        lock (Gate)
        {
            Handlers[modId] = handler;
        }
    }

    public static string? GetBaseUrl(string modId)
    {
        int port = EnsureStarted();
        return port == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/t/{SessionToken}/m/{Uri.EscapeDataString(modId)}/v1");
    }

    public static bool IsOwnBaseUrl(string? baseUrl, string modId)
    {
        if (string.IsNullOrEmpty(baseUrl)) return false;

        return baseUrl.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
            && (baseUrl.IndexOf($"/m/{modId}/v1", StringComparison.Ordinal) > 0
                || baseUrl.IndexOf($"/m/{Uri.EscapeDataString(modId)}/v1", StringComparison.Ordinal) > 0);
    }

    internal static void Stop()
    {
        HttpListener? listener;
        CancellationTokenSource? shutdown;
        lock (Gate)
        {
            listener = _listener;
            shutdown = _shutdown;
            _listener = null;
            _shutdown = null;
            _port = 0;
        }

        if (listener == null) return;

        try
        {
            listener.Stop();
            listener.Close();
        }
        catch (Exception)
        {
            // 關閉期間安全忽略已釋放的監聽器。
        }

        if (shutdown == null) return;

        // 進行中的請求還在花玩家的 token；監聽器關閉後回應也送不出去，直接取消。
        // 取消會同步執行下游登記的回呼，其中擲錯不能影響上面已完成的關閉。
        try
        {
            shutdown.Cancel();
        }
        catch (AggregateException ex)
        {
            RimLLMLog.Warning($"[RimLLM] Local endpoint shutdown cancellation callback failed: {ex.Message}");
        }
        finally
        {
            shutdown.Dispose();
        }
    }

    private static int EnsureStarted()
    {
        lock (Gate)
        {
            if (_listener != null) return _port;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                int port = FindFreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    listener.Close();
                    RimLLMLog.Warning(string.Create(CultureInfo.InvariantCulture, $"[RimLLM] Local endpoint failed to listen on port {port}: {RimLLMLog.SanitizeForLog(ex.Message)}"));
                    continue;
                }

                var shutdown = new CancellationTokenSource();
                _listener = listener;
                _shutdown = shutdown;
                _port = port;
                _ = Task.Run(() => AcceptLoopAsync(listener, port, shutdown.Token), CancellationToken.None);
                RimLLMLog.Message(string.Create(CultureInfo.InvariantCulture, $"[RimLLM] Local OpenAI-compatible endpoint listening on 127.0.0.1:{port}"));
                return _port;
            }
            return 0;
        }
    }

    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static async Task AcceptLoopAsync(HttpListener listener, int port, CancellationToken shutdown)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // 監聽器已關閉
            }
            // 已接受的連線一定要回應或中止，不能因關閉而略過不跑。
            _ = Task.Run(() => HandleAsync(context, port, shutdown), CancellationToken.None);
        }
    }

    private static async Task HandleAsync(HttpListenerContext context, int port, CancellationToken shutdown)
    {
        bool acquired = false;
        try
        {
            (int Status, string Body)? rejection = Preflight(context, port, out string modId, out string endpoint);
            if (rejection is { } early)
            {
                await WriteJsonAsync(context, early.Status, early.Body).ConfigureAwait(false);
                return;
            }

            if (!await RequestConcurrencyGate.WaitAsync(TimeSpan.FromSeconds(2), shutdown).ConfigureAwait(false))
            {
                await WriteJsonAsync(context, 503, ErrorBody("server_busy", "Local endpoint is busy.")).ConfigureAwait(false);
                return;
            }
            acquired = true;

            rejection = Route(context, modId, endpoint, out Handler? handler);
            if (rejection is { } routed)
            {
                await WriteJsonAsync(context, routed.Status, routed.Body).ConfigureAwait(false);
                return;
            }

            bool chat = endpoint == "chat/completions";

            (string? body, bool tooLarge) = await ReadBodyAsync(context.Request.InputStream).ConfigureAwait(false);
            if (tooLarge || body == null)
            {
                await TryWriteErrorOrAbortAsync(context, 413, "payload_too_large", "Request body exceeds maximum allowed size.").ConfigureAwait(false);
                return;
            }
            string json = await handler!(body, chat, shutdown).ConfigureAwait(false);
            await WriteResultAsync(context, json, body, chat).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            TryAbort(context);
        }
        catch (OperationCanceledException)
        {
            await TryWriteErrorOrAbortAsync(context, 408, "request_timeout", "Request body read timed out.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string safeMessage = RimLLMLog.SanitizeForLog(ex.Message) ?? "Internal error.";
            await TryWriteErrorOrAbortAsync(context, 500, "server", safeMessage).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                RequestConcurrencyGate.Release();
            }
        }
    }

    private static (int Status, string Body)? Preflight(HttpListenerContext context, int port, out string modId, out string endpoint)
    {
        modId = endpoint = "";
        if (context.Request.Headers["Origin"] != null || !IsAllowedHost(context.Request.UserHostName, port))
        {
            return (403, ErrorBody("forbidden", "Forbidden request origin or host."));
        }

        ArgumentNullException.ThrowIfNull(context.Request.Url);
        if (!TryParseRoute(context.Request.Url, out string? parsedModId, out string? parsedEndpoint, out bool tokenValid))
        {
            return tokenValid
                ? (404, ErrorBody("not_found", "Unknown path."))
                : (403, ErrorBody("forbidden", "Invalid endpoint token."));
        }
        modId = parsedModId;
        endpoint = parsedEndpoint;
        return null;
    }

    private static (int Status, string Body)? Route(HttpListenerContext context, string modId, string endpoint, out Handler? handler)
    {
        handler = FindHandler(modId);
        if (handler == null)
        {
            return (404, ErrorBody("not_found", "No handler registered for this mod."));
        }

        RimLLMFrameworkSettings settings = RimLLMFrameworkMod.Settings;
        if (settings == null || !settings.IsCompatTakeoverEnabled(modId))
        {
            // 用永久性錯誤（不含 server／network 等字樣）並回 403，讓對方不要白白重試。
            return (403, ErrorBody("takeover_disabled", "RimLLM takeover is turned off for this mod. Set up its own API settings instead."));
        }

        if (endpoint == "models" && context.Request.HttpMethod == "GET")
        {
            return (200, ModelsBody(CompatSeedUtil.ModelLabel(modId)));
        }

        if (context.Request.HttpMethod != "POST" || (endpoint != "chat/completions" && endpoint != "responses"))
        {
            return (404, ErrorBody("not_found", "Unsupported endpoint."));
        }

        return context.Request.ContentLength64 > MaxBodyBytes
            ? (413, ErrorBody("payload_too_large", "Request body exceeds maximum allowed size."))
            : null;
    }

    private static async Task TryWriteErrorOrAbortAsync(HttpListenerContext context, int status, string type, string message)
    {
        try
        {
            await WriteJsonAsync(context, status, ErrorBody(type, message)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            TryAbort(context);
        }
    }

    private static void TryAbort(HttpListenerContext context)
    {
        try
        {
            context.Response.Abort();
        }
        catch (Exception)
        {
            // 連線已斷，忽略中止錯誤
        }
    }

    private static bool IsAllowedHost(string? host, int port)
    {
        if (string.IsNullOrEmpty(host)) return false;
        return string.Equals(host, string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{port}"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, string.Create(CultureInfo.InvariantCulture, $"localhost:{port}"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseRoute(Uri url, [NotNullWhen(true)] out string? modId, [NotNullWhen(true)] out string? endpoint, out bool tokenValid)
    {
        modId = null;
        endpoint = null;
        tokenValid = true;
        string[] segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 4 && segments[0] == "m" && segments[2] == "v1")
        {
            tokenValid = false;
            return false;
        }

        if (segments.Length < 6 || segments[0] != "t" || segments[2] != "m" || segments[4] != "v1")
        {
            return false;
        }

        byte[] candidateBytes = Encoding.UTF8.GetBytes(segments[1]);
        if (!CryptographicOperations.FixedTimeEquals(candidateBytes, SessionTokenBytes))
        {
            tokenValid = false;
            return false;
        }

        modId = Uri.UnescapeDataString(segments[3]);
        endpoint = string.Join('/', segments, 5, segments.Length - 5);
        return true;
    }

    private static Handler? FindHandler(string modId)
    {
        lock (Gate)
        {
            Handlers.TryGetValue(modId, out Handler? handler);
            return handler;
        }
    }

    private static async Task<(string? Body, bool TooLarge)> ReadBodyAsync(Stream input)
    {
        using var cts = new CancellationTokenSource(BodyReadTimeout);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await input.ReadAsync(chunk, cts.Token).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > MaxBodyBytes)
            {
                return (null, true);
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cts.Token).ConfigureAwait(false);
        }
        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, total), false);
    }

    private static async Task WriteResultAsync(HttpListenerContext context, string json, string body, bool chat)
    {
        if (TryGetErrorStatus(json, out int status))
        {
            await WriteJsonAsync(context, status, json).ConfigureAwait(false);
        }
        else if (WantsStream(body))
        {
            await WriteSseAsync(context, json, chat).ConfigureAwait(false);
        }
        else
        {
            await WriteJsonAsync(context, 200, json).ConfigureAwait(false);
        }
    }

    private static bool WantsStream(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("stream", out JsonElement s)
                && s.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryGetErrorStatus(string json, out int status)
    {
        status = 200;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                return false;
            }

            string? type = error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            status = type switch
            {
                "rate_limit" or "quota_exceeded" => 429,
                "invalid_key" => 401,
                "model_not_found" => 404,
                "timeout" => 504,
                "network" or "server" => 502,
                _ => 400,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>把完整回應包成單一事件的 SSE 串流：框架內部以整段結果為單位，不做逐字轉送。</summary>
    private static async Task WriteSseAsync(HttpListenerContext context, string fullJson, bool chat)
    {
        string text = ExtractText(fullJson, chat);
        var sb = new StringBuilder();
        if (chat)
        {
            sb.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } }
            })).Append("\n\n");
            sb.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
            })).Append("\n\n");
            sb.Append("data: [DONE]\n\n");
        }
        else
        {
            sb.Append("event: response.output_text.delta\ndata: ").Append(JsonSerializer.Serialize(new
            {
                type = "response.output_text.delta",
                delta = text
            })).Append("\n\n");
            // 完成事件附上完整的 Response 物件（含 output），讓從 response.completed 取最終結果的客戶端也拿得到文字。
            sb.Append("event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":")
                .Append(fullJson).Append("}\n\n");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/event-stream";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes.AsMemory(), CancellationToken.None).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string ExtractText(string json, bool chat)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (chat)
        {
            return root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        }
        return root.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
    }

    private static string ModelsBody(string model)
    {
        return JsonSerializer.Serialize(new
        {
            @object = "list",
            data = new[] { new { id = model, @object = "model", owned_by = "rimllm" } }
        });
    }

    private static string ErrorBody(string type, string message)
    {
        return JsonSerializer.Serialize(new { error = new { type, message } });
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes.AsMemory(), CancellationToken.None).ConfigureAwait(false);
        context.Response.Close();
    }
}
