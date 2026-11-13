using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimLLM_Framework.Manager;
using RimLLM_Framework.Mod;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 框架內建的本機 OpenAI 相容端點。第三方 Mod 只要能設定 Base URL，就把它指向這裡，
/// 之後該 Mod 的所有模型呼叫都由框架的備援鏈處理，不需要為每個 Mod、每項功能寫專屬補丁。
/// </summary>
/// <remarks>
/// <para>
/// 路徑格式：<c>/m/{modId}/v1/{chat/completions | responses | models}</c>。以 modId 區分來源，
/// 才能套用該 Mod 的接管開關、專用模型與用量歸屬。
/// </para>
/// <para>
/// 只綁定 127.0.0.1 並自動挑選空閒埠；帶有 <c>Origin</c> 標頭的請求（瀏覽器網頁發出的）一律拒絕，
/// 避免外部網頁借用玩家的金鑰額度。
/// </para>
/// </remarks>
internal static class LocalOpenAIEndpoint
{
    /// <summary>處理一筆請求本文，回傳同協議的回應 JSON（成功或含 <c>error</c> 的錯誤）。</summary>
    internal delegate Task<string> Handler(string payloadJson, bool chatProtocol, CancellationToken cancellationToken);

    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static HttpListener _listener;
    private static int _port;

    /// <summary>登錄某個 Mod 的處理器。可重複呼叫，後者覆蓋前者。</summary>
    public static void Register(string modId, Handler handler)
    {
        lock (Gate)
        {
            Handlers[modId] = handler;
        }
    }

    /// <summary>該 Mod 該寫進設定的 Base URL；必要時啟動端點。啟動失敗回傳 null。</summary>
    public static string GetBaseUrl(string modId)
    {
        int port = EnsureStarted();
        return port == 0 ? null : $"http://127.0.0.1:{port}/m/{modId}/v1";
    }

    /// <summary>是否為框架自己先前寫入的位址（埠號每次啟動不同，需要更新時才認得出來）。</summary>
    public static bool IsOwnBaseUrl(string baseUrl, string modId)
    {
        if (string.IsNullOrEmpty(baseUrl)) return false;

        // 先前版本寫入的固定佔位位址，同樣視為框架自己的。
        if (string.Equals(baseUrl.TrimEnd('/'), "http://127.0.0.1:1/v1", StringComparison.OrdinalIgnoreCase)) return true;

        return baseUrl.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
            && baseUrl.IndexOf($"/m/{modId}/v1", StringComparison.Ordinal) > 0;
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
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    listener.Close();
                    Log.Warning($"[RimLLM] Local endpoint failed to listen on port {port}: {ex.Message}");
                    continue;
                }

                _listener = listener;
                _port = port;
                _ = Task.Run(() => AcceptLoopAsync(listener));
                Log.Message($"[RimLLM] Local OpenAI-compatible endpoint listening on 127.0.0.1:{port}");
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

    private static async Task AcceptLoopAsync(HttpListener listener)
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
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private static async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Headers["Origin"] != null)
            {
                await WriteJsonAsync(context, 403, ErrorBody("forbidden", "Browser requests are not allowed.")).ConfigureAwait(false);
                return;
            }

            // /m/{modId}/v1/{endpoint...}
            string[] segments = context.Request.Url.AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 4 || segments[0] != "m" || segments[2] != "v1")
            {
                await WriteJsonAsync(context, 404, ErrorBody("not_found", "Unknown path.")).ConfigureAwait(false);
                return;
            }

            string modId = Uri.UnescapeDataString(segments[1]);
            string endpoint = string.Join("/", segments, 3, segments.Length - 3);

            Handler handler;
            lock (Gate)
            {
                Handlers.TryGetValue(modId, out handler);
            }
            if (handler == null)
            {
                await WriteJsonAsync(context, 404, ErrorBody("not_found", "No handler registered for this mod.")).ConfigureAwait(false);
                return;
            }

            RimLLMFrameworkSettings settings = RimLLMFrameworkMod.Settings;
            if (settings == null || !settings.IsCompatTakeoverEnabled(modId))
            {
                // 用永久性錯誤（不含 server／network 等字樣）並回 403，讓對方不要白白重試。
                await WriteJsonAsync(context, 403, ErrorBody("takeover_disabled", "RimLLM takeover is turned off for this mod. Set up its own API settings instead.")).ConfigureAwait(false);
                return;
            }

            if (endpoint == "models" && context.Request.HttpMethod == "GET")
            {
                await WriteJsonAsync(context, 200, ModelsBody(CompatSeedUtil.ModelLabel(modId))).ConfigureAwait(false);
                return;
            }

            bool chat = endpoint == "chat/completions";
            if (context.Request.HttpMethod != "POST" || (!chat && endpoint != "responses"))
            {
                await WriteJsonAsync(context, 404, ErrorBody("not_found", "Unsupported endpoint.")).ConfigureAwait(false);
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            string json = await handler(body, chat, CancellationToken.None).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(context, 500, ErrorBody("server", ex.Message)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 連線已斷，無法回覆
            }
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

    /// <summary>回應含 <c>error</c> 時，依錯誤類型映射成 HTTP 狀態碼，讓第三方 Mod 的重試與降級邏輯照常運作。</summary>
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

            string type = error.ValueKind == JsonValueKind.Object
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
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string)null } }
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
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
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
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        context.Response.Close();
    }
}
