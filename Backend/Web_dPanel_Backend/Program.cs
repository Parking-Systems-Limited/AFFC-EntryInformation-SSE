using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using carLocation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SseOptions>(builder.Configuration.GetSection("Sse"));
builder.Services.Configure<LprsApiOptions>(builder.Configuration.GetSection("LprsApi"));

var sseOptions = builder.Configuration.GetSection("Sse").Get<SseOptions>() ?? new SseOptions();
var lprsOptions = builder.Configuration.GetSection("LprsApi").Get<LprsApiOptions>() ?? new LprsApiOptions();

builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(sseOptions.ListenUrl)
    ? "http://localhost:5100"
    : sseOptions.ListenUrl);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});
builder.Services.AddSingleton<MessageHub>();

builder.Services.AddHttpClient("LprsApi", (sp, client) =>
{
    var opt = sp.GetRequiredService<IOptions<LprsApiOptions>>().Value ?? new LprsApiOptions();
    var baseUrl = string.IsNullOrWhiteSpace(opt.BaseUrl)
        ? "https://192.168.103.59:8012"
        : opt.BaseUrl.TrimEnd('/');
    client.BaseAddress = new Uri(baseUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 3, 120));
    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

    var apiKey = !string.IsNullOrWhiteSpace(opt.ApiKey) ? opt.ApiKey.Trim()
        : (!string.IsNullOrWhiteSpace(opt.SiteKey) ? opt.SiteKey.Trim() : "");
    var headerName = string.IsNullOrWhiteSpace(opt.ApiKeyHeaderName) ? "x-api-key" : opt.ApiKeyHeaderName.Trim();
    if (!string.IsNullOrWhiteSpace(apiKey))
        client.DefaultRequestHeaders.TryAddWithoutValidation(headerName, apiKey);
})
.ConfigurePrimaryHttpMessageHandler(sp =>
{
    var opt = sp.GetRequiredService<IOptions<LprsApiOptions>>().Value ?? new LprsApiOptions();
    var handler = new HttpClientHandler();
    if (opt.IgnoreSslErrors)
    {
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }
    return handler;
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/sse"),
    appBuilder => appBuilder.Use((ctx, next) =>
    {
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        return next();
    }));

// ---- Legacy ingest (kept for compatibility) ----
app.MapPost("/api/message", async (HttpContext ctx, MessageHub hub, ILogger<Program> logger) =>
{
    try
    {
        var dto = await ctx.Request.ReadFromJsonAsync<MessageDto>();
        if (dto == null || string.IsNullOrWhiteSpace(dto.Tid))
            return Results.BadRequest(new { error = "Tid (tranid) is required" });

        LogFile.Write($"=== RECEIVED DTO === Tid:{dto.Tid} | ClientKey:{dto.ClientKey} | Lpn:{dto.Lpn} | Type:{dto.Type} | Msg:{dto.Msg} | Fee:{dto.Fee} | EntryTime:{dto.EntryTime} | ExitTime:{dto.ExitTime} | ParkingDuration:{dto.ParkingDuration} | QrcodePayment:{dto.QrcodePayment} | QrcodeReceipt:{dto.QrcodeReceipt}");
        logger.LogInformation("=== RECEIVED DTO === Tid:{Tid} | ClientKey:{ClientKey} | Lpn:{Lpn} | Type:{Type} | Msg:{Msg} | Fee:{Fee} | EntryTime:{EntryTime} | ExitTime:{ExitTime} | ParkingDuration:{ParkingDuration} | QrcodePayment:{QrcodePayment} | QrcodeReceipt:{QrcodeReceipt}",
            dto.Tid, dto.ClientKey, dto.Lpn, dto.Type, dto.Msg, dto.Fee, dto.EntryTime, dto.ExitTime, dto.ParkingDuration, dto.QrcodePayment, dto.QrcodeReceipt);

        hub.MergeTransaction(dto.Tid, dto);
        var state = hub.GetTransaction(dto.Tid);

        if (state == null || string.IsNullOrWhiteSpace(state.ClientKey))
            return Results.BadRequest(new { error = "ClientKey is required (cannot broadcast without clientKey)" });

        bool isEntrancePanel = state.ClientKey.ToUpper().StartsWith("EN");
        logger.LogInformation("=== ENTRANCE/EXIT check === ClientKey={ClientKey} => isEntrancePanel={IsEntrance}", state.ClientKey, isEntrancePanel);
        LogFile.Write($"=== ENTRANCE/EXIT check === ClientKey={state.ClientKey} => isEntrancePanel={isEntrancePanel}");

        if (isEntrancePanel)
        {
            string payload = $"LPN:{state.Lpn ?? ""} | TYPE:{state.Type ?? ""} | ENTRYTIME:{state.EntryTime ?? ""} | MSG:{state.Msg ?? ""}";
            hub.Publish(state.ClientKey, "info", payload);
            logger.LogInformation("Entrance SENT => Tid:{Tid} | ClientKey:{ClientKey} | Payload: {Payload}", dto.Tid, state.ClientKey, payload);
            LogFile.Write($"Entrance SENT => Tid:{dto.Tid} | ClientKey:{state.ClientKey} | Payload: {payload}");
            return Results.Ok(new { status = "sent", type = "entrance", tid = dto.Tid, clientKey = state.ClientKey, lpn = state.Lpn, vehicleType = state.Type, msg = state.Msg, entryTime = state.EntryTime, payload });
        }
        else
        {
            string exitTime = !string.IsNullOrWhiteSpace(dto.ExitTime) ? dto.ExitTime :
                              (!string.IsNullOrWhiteSpace(state.ExitTime) ? state.ExitTime : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            string payload = $"Fee:{state.Fee ?? 0} | LPN:{state.Lpn} | TYPE:{state.Type ?? ""}";
            if (!string.IsNullOrWhiteSpace(state.EntryTime))
                payload += $" | Entry:{state.EntryTime}";
            payload += $" | ExitTime:{exitTime}";
            if (!string.IsNullOrWhiteSpace(state.ParkingDuration))
                payload += $" | ParkingDuration:{state.ParkingDuration}";
            if (!string.IsNullOrWhiteSpace(state.Msg))
                payload += $" | Msg:{state.Msg}";
            if (!string.IsNullOrWhiteSpace(state.QrcodeReceipt))
                payload += $" | QR_Receipt:{state.QrcodeReceipt}";
            else if (!string.IsNullOrWhiteSpace(state.QrcodePayment))
                payload += $" | QR_Payment:{state.QrcodePayment}";

            hub.Publish(state.ClientKey, "success", payload);
            hub.Publish(state.ClientKey, "info", $"QR Payment ready: {state.QrcodePayment ?? "N/A"} | QR Receipt ready: {state.QrcodeReceipt ?? "N/A"} | Msg: {state.Msg ?? "N/A"} | ExitTime: {exitTime}");
            logger.LogInformation("Exit SENT => Tid:{Tid} | ClientKey:{ClientKey} | ExitTime:{ExitTime} | Payload: {Payload}", dto.Tid, state.ClientKey, exitTime, payload);
            LogFile.Write($"Exit SENT => Tid:{dto.Tid} | ClientKey:{state.ClientKey} | ExitTime:{exitTime} | Payload: {payload} | QrcodePayment:{state.QrcodePayment} | QrcodeReceipt:{state.QrcodeReceipt}");
            return Results.Ok(new { status = "sent", type = "exit", tid = dto.Tid, clientKey = state.ClientKey, lpn = state.Lpn, vehicleType = state.Type, fee = state.Fee, entryTime = state.EntryTime, exitTime = exitTime, parkingDuration = state.ParkingDuration, qrcode_payment = state.QrcodePayment, qrcode_receipt = state.QrcodeReceipt, msg = state.Msg, payload });
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in /api/message");
        LogFile.Error("Error in /api/message: " + ex.ToString());
        return Results.BadRequest(new { error = "Invalid JSON or server error" });
    }
});

// ---- Primary path: plate -> LPRS -> SSE ----
async Task<IResult> HandleVehicleLookup(
    VehicleLookupDto? dto,
    HttpContext ctx,
    MessageHub hub,
    IHttpClientFactory httpClientFactory,
    IOptions<LprsApiOptions> lprsOpt,
    ILogger logger)
{
    try
    {
        dto ??= await ctx.Request.ReadFromJsonAsync<VehicleLookupDto>();
        if (dto == null)
            return Results.BadRequest(new { error = "Request body is required" });

        var clientKey = (dto.ClientKey ?? "").Trim();
        var lpn = (dto.Lpn ?? dto.Plate ?? dto.VehicleNo ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(clientKey))
            return Results.BadRequest(new { error = "clientKey is required" });
        if (string.IsNullOrWhiteSpace(lpn))
            return Results.BadRequest(new { error = "lpn/plate is required" });

        var opt = lprsOpt.Value ?? new LprsApiOptions();
        var pathTemplate = string.IsNullOrWhiteSpace(opt.VehiclePathTemplate)
            ? "/api/LprsEvents/Vehicle/{lpn}"
            : opt.VehiclePathTemplate;
        var relativePath = pathTemplate
            .Replace("{lpn}", Uri.EscapeDataString(lpn), StringComparison.OrdinalIgnoreCase)
            .Replace("{LPN}", Uri.EscapeDataString(lpn), StringComparison.OrdinalIgnoreCase)
            .TrimStart('/');

        var client = httpClientFactory.CreateClient("LprsApi");
        logger.LogInformation("LPRS lookup ClientKey={ClientKey} Lpn={Lpn} Path={Path} Tid={Tid}", clientKey, lpn, relativePath, dto.Tid);
        LogFile.Write($"LPRS lookup ClientKey={clientKey} Lpn={lpn} Path={relativePath} Tid={dto.Tid}");

        HttpResponseMessage resp;
        string rawBody;
        try
        {
            resp = await client.GetAsync(relativePath, ctx.RequestAborted);
            rawBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, "LPRS timeout ClientKey={ClientKey} Lpn={Lpn}", clientKey, lpn);
            LogFile.Error($"LPRS timeout ClientKey={clientKey} Lpn={lpn}: {ex}");
            var timeoutErr = JsonSerializer.Serialize(new
            {
                error = "LPRS API timeout",
                clientKey,
                lpn,
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            hub.Publish(clientKey, "error", timeoutErr);
            return Results.Json(new { error = "LPRS API timeout", clientKey, lpn }, statusCode: 504);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "LPRS unreachable ClientKey={ClientKey} Lpn={Lpn}", clientKey, lpn);
            LogFile.Error($"LPRS unreachable ClientKey={clientKey} Lpn={lpn}: {ex}");
            var netErr = JsonSerializer.Serialize(new
            {
                error = "LPRS API unreachable",
                detail = ex.Message,
                clientKey,
                lpn,
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            hub.Publish(clientKey, "error", netErr);
            return Results.Json(new { error = "LPRS API unreachable", detail = ex.Message, clientKey, lpn }, statusCode: 502);
        }

        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("LPRS HTTP {Status} ClientKey={ClientKey} Lpn={Lpn} Body={Body}", (int)resp.StatusCode, clientKey, lpn, rawBody);
            LogFile.Write($"LPRS HTTP {(int)resp.StatusCode} ClientKey={clientKey} Lpn={lpn} Body={rawBody}");
            var httpErr = JsonSerializer.Serialize(new
            {
                error = "LPRS API HTTP error",
                statusCode = (int)resp.StatusCode,
                clientKey,
                lpn,
                body = Truncate(rawBody, 500),
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            hub.Publish(clientKey, "error", httpErr);
            return Results.Json(new { error = "LPRS API HTTP error", statusCode = (int)resp.StatusCode, clientKey, lpn, body = Truncate(rawBody, 2000) }, statusCode: 502);
        }

        LprsEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<LprsEnvelope>(rawBody, LprsJson.Options);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LPRS JSON parse failed ClientKey={ClientKey} Lpn={Lpn}", clientKey, lpn);
            LogFile.Error($"LPRS JSON parse failed ClientKey={clientKey} Lpn={lpn}: {ex}");
            var parseErr = JsonSerializer.Serialize(new
            {
                error = "LPRS response JSON parse failed",
                clientKey,
                lpn,
                body = Truncate(rawBody, 500),
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
            hub.Publish(clientKey, "error", parseErr);
            return Results.Json(new { error = "LPRS response JSON parse failed", clientKey, lpn, body = Truncate(rawBody, 2000) }, statusCode: 502);
        }

        if (envelope == null)
        {
            var nullErr = JsonSerializer.Serialize(new { error = "Empty LPRS response", clientKey, lpn, ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
            hub.Publish(clientKey, "error", nullErr);
            return Results.Json(new { error = "Empty LPRS response", clientKey, lpn }, statusCode: 502);
        }

        // Business-level failure (e.g. resCode 997 Invalid API Key) — do NOT invent data
        if (envelope.Data == null || (envelope.ResCode.HasValue && envelope.ResCode.Value != 0 && envelope.ResCode.Value != 1))
        {
            // Treat missing data / non-success resCode as error. Some APIs use 0=ok; if data present with null resCode, allow.
            var looksFailed = envelope.Data == null ||
                              (envelope.ResCode.HasValue && envelope.ResCode.Value >= 100) ||
                              string.Equals(envelope.ResMsg, "Invalid API Key/Not Authorized", StringComparison.OrdinalIgnoreCase);

            if (looksFailed)
            {
                logger.LogWarning("LPRS business error resCode={ResCode} resMsg={ResMsg} ClientKey={ClientKey} Lpn={Lpn}",
                    envelope.ResCode, envelope.ResMsg, clientKey, lpn);
                LogFile.Write($"LPRS business error resCode={envelope.ResCode} resMsg={envelope.ResMsg} ClientKey={clientKey} Lpn={lpn}");
                var bizErr = JsonSerializer.Serialize(new
                {
                    error = "LPRS API returned error",
                    resCode = envelope.ResCode,
                    resMsg = envelope.ResMsg,
                    clientKey,
                    lpn,
                    ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
                hub.Publish(clientKey, "error", bizErr);
                return Results.Json(new
                {
                    error = "LPRS API returned error",
                    resCode = envelope.ResCode,
                    resMsg = envelope.ResMsg,
                    clientKey,
                    lpn
                }, statusCode: 502);
            }
        }

        var panel = VehiclePanelMapper.Map(envelope.Data!, opt, lpn, clientKey, dto.Tid);
        var panelJson = JsonSerializer.Serialize(panel, LprsJson.Options);

        hub.Publish(clientKey, "vehicle", panelJson);
        // Also publish as "info" JSON for frontends that only listen to info
        hub.Publish(clientKey, "info", panelJson);

        logger.LogInformation("Vehicle SENT => ClientKey={ClientKey} Lpn={Lpn} Payload={Payload}", clientKey, lpn, panelJson);
        LogFile.Write($"Vehicle SENT => ClientKey={clientKey} Lpn={lpn} Payload={panelJson}");

        return Results.Ok(new
        {
            status = "sent",
            type = "vehicle",
            clientKey,
            lpn,
            tid = dto.Tid,
            resCode = envelope.ResCode,
            resMsg = envelope.ResMsg,
            payload = panel
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in vehicle lookup");
        LogFile.Error("Error in vehicle lookup: " + ex);
        return Results.BadRequest(new { error = "Invalid JSON or server error", detail = ex.Message });
    }
}

static string Truncate(string? s, int max)
{
    if (string.IsNullOrEmpty(s)) return "";
    return s.Length <= max ? s : s.Substring(0, max) + "...";
}

app.MapPost("/api/vehicle", async (HttpContext ctx, MessageHub hub, IHttpClientFactory httpFactory, IOptions<LprsApiOptions> lprsOpt, ILogger<Program> logger) =>
    await HandleVehicleLookup(null, ctx, hub, httpFactory, lprsOpt, logger));

app.MapPost("/api/plate", async (HttpContext ctx, MessageHub hub, IHttpClientFactory httpFactory, IOptions<LprsApiOptions> lprsOpt, ILogger<Program> logger) =>
    await HandleVehicleLookup(null, ctx, hub, httpFactory, lprsOpt, logger));

app.MapPost("/api/clear/{clientKey}", (string clientKey, MessageHub hub, ILogger<Program> logger) =>
{
    hub.Clear(clientKey);
    logger.LogInformation("Display has been cleared for ClientKey {ClientKey}", clientKey);
    LogFile.Write($"Display has been cleared for ClientKey {clientKey}");
    return Results.Ok(new { status = "cleared", clientKey, message = "Screen cleared (hasData=false, QR/text cleared)" });
});

app.MapGet("/sse/{clientKey}", async (string clientKey, HttpContext ctx, MessageHub hub, IOptions<SseOptions> sseOpt, ILogger<Program> logger) =>
{
    clientKey = clientKey.Trim();
    var opt = sseOpt.Value ?? new SseOptions();

    var remoteIp = IpHelper.Normalize(ctx.Connection.RemoteIpAddress);
    var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
    var clientIp = IpHelper.PickClientIp(remoteIp, forwarded);
    var isKnownKiosk = IpHelper.IsAllowed(clientIp, opt.KioskIps);

    logger.LogInformation(
        "SSE CONNECT ClientKey={ClientKey} RemoteIp={RemoteIp} ClientIp={ClientIp} X-Forwarded-For={Forwarded} KnownKiosk={KnownKiosk} Allowed={Allowed}",
        clientKey, remoteIp, clientIp, forwarded, isKnownKiosk, string.Join(",", opt.KioskIps ?? Array.Empty<string>()));
    LogFile.Write($"SSE CONNECT ClientKey={clientKey} RemoteIp={remoteIp} ClientIp={clientIp} X-Forwarded-For={forwarded} KnownKiosk={isKnownKiosk}");

    if (opt.RestrictByIp && !isKnownKiosk)
    {
        logger.LogWarning("SSE REJECT IP ClientKey={ClientKey} ClientIp={ClientIp}", clientKey, clientIp);
        LogFile.Write($"SSE REJECT IP ClientKey={clientKey} ClientIp={clientIp}");
        return Results.Json(new { error = "IP not allowed", ip = clientIp }, statusCode: 403);
    }

    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/event-stream; charset=utf-8";
    ctx.Response.Headers.Append("Cache-Control", "no-cache");
    ctx.Response.Headers.Append("Connection", "keep-alive");
    ctx.Response.Headers.Append("X-Accel-Buffering", "no");
    await ctx.Response.Body.FlushAsync();

    // Replay latest events including vehicle
    foreach (var type in new[] { "vehicle", "info", "alert", "success", "warning", "error" })
    {
        var last = hub.GetLast(clientKey, type);
        if (!string.IsNullOrEmpty(last))
        {
            await ctx.Response.WriteAsync($"event: {type}\ndata: {last}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }

    try
    {
        await foreach (var update in hub.Subscribe(clientKey, clientIp, ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"event: {update.Type}\ndata: {update.Text}\n\n");
            await ctx.Response.Body.FlushAsync();

            if (string.Equals(update.Type, "ping", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("SSE PING sent ClientKey={ClientKey} ClientIp={ClientIp} Data={Data}",
                    clientKey, clientIp, update.Text);
                LogFile.Write($"SSE PING sent ClientKey={clientKey} ClientIp={clientIp} Data={update.Text}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("SSE connection closed for ClientKey {ClientKey} ClientIp={ClientIp}", clientKey, clientIp);
        LogFile.Write($"SSE connection closed for ClientKey {clientKey} ClientIp={clientIp}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "SSE error for ClientKey {ClientKey} ClientIp={ClientIp}", clientKey, clientIp);
        LogFile.Error($"SSE error for ClientKey {clientKey} ClientIp={clientIp}: {ex}");
    }

    return Results.Empty;
});

app.MapGet("/health", (MessageHub hub, IOptions<SseOptions> sseOpt, IOptions<LprsApiOptions> lprsOpt) =>
{
    var opt = sseOpt.Value ?? new SseOptions();
    var lprs = lprsOpt.Value ?? new LprsApiOptions();
    return Results.Ok(new
    {
        status = "running",
        listenUrl = opt.ListenUrl,
        time = DateTime.Now,
        pingIntervalSeconds = opt.PingIntervalSeconds,
        restrictByIp = opt.RestrictByIp,
        expectedKioskIps = opt.KioskIps,
        activeClientKeys = hub.GetActiveClientKeys(),
        lprs = new
        {
            baseUrl = lprs.BaseUrl,
            siteKey = lprs.SiteKey,
            vehiclePathTemplate = lprs.VehiclePathTemplate,
            timeoutSeconds = lprs.TimeoutSeconds,
            ignoreSslErrors = lprs.IgnoreSslErrors,
            apiKeyConfigured = !string.IsNullOrWhiteSpace(lprs.ApiKey) || !string.IsNullOrWhiteSpace(lprs.SiteKey)
        }
    });
});

app.Run();

public static class LprsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public class SseOptions
{
    public string ListenUrl { get; set; } = "http://localhost:5100";
    public int PingIntervalSeconds { get; set; } = 20;
    public bool RestrictByIp { get; set; } = false;
    public string[] KioskIps { get; set; } = new[] { "10.33.13.231", "10.33.13.236" };
}

public class LprsApiOptions
{
    public string BaseUrl { get; set; } = "https://192.168.103.59:8012";
    /// <summary>Swagger: GET /api/LprsEvents/Vehicle/{LPN}. {lpn} is replaced with the plate.</summary>
    public string VehiclePathTemplate { get; set; } = "/api/LprsEvents/Vehicle/{lpn}";
    /// <summary>Site identifier (e.g. HK1). Used as x-api-key fallback when ApiKey is empty.</summary>
    public string SiteKey { get; set; } = "HK1";
    /// <summary>Value for x-api-key header. Leave empty to fall back to SiteKey.</summary>
    public string ApiKey { get; set; } = "";
    public string ApiKeyHeaderName { get; set; } = "x-api-key";
    public int TimeoutSeconds { get; set; } = 15;
    public bool IgnoreSslErrors { get; set; } = true;
    public string EntryImagePathTemplate { get; set; } = "/api/ManagementSystem/Summaries/Vehicle/EntryImage/{lpn}";
}

public static class IpHelper
{
    public static string Normalize(IPAddress? ip)
    {
        if (ip == null) return "unknown";
        if (ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4().ToString();
        return ip.ToString();
    }

    public static string PickClientIp(string remoteIp, string forwarded)
    {
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var first = forwarded.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }
        return string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp;
    }

    public static bool IsAllowed(string clientIp, string[]? allowed)
    {
        if (allowed == null || allowed.Length == 0) return true;
        var ip = (clientIp ?? "").Trim();
        return allowed.Any(a => string.Equals((a ?? "").Trim(), ip, StringComparison.OrdinalIgnoreCase));
    }
}

public record MessageDto
{
    [JsonPropertyName("tid")]
    public string? Tid { get; init; }

    [JsonPropertyName("clientKey")]
    public string? ClientKey { get; init; } = null;

    [JsonPropertyName("lpn")]
    public string? Lpn { get; init; } = null;

    [JsonPropertyName("type")]
    public string? Type { get; init; } = null;

    [JsonPropertyName("fee")]
    public decimal? Fee { get; init; } = null;

    [JsonPropertyName("entryTime")]
    public string? EntryTime { get; init; } = null;

    [JsonPropertyName("exitTime")]
    public string? ExitTime { get; init; } = null;

    [JsonPropertyName("parkingDuration")]
    public string? ParkingDuration { get; init; } = null;

    [JsonPropertyName("QrcodePayment")]
    public string? QrcodePayment { get; init; } = null;

    [JsonPropertyName("QrcodeReceipt")]
    public string? QrcodeReceipt { get; init; } = null;

    [JsonPropertyName("msg")]
    public string? Msg { get; init; } = null;
};

public record VehicleLookupDto
{
    [JsonPropertyName("clientKey")]
    public string? ClientKey { get; init; }

    [JsonPropertyName("lpn")]
    public string? Lpn { get; init; }

    [JsonPropertyName("plate")]
    public string? Plate { get; init; }

    [JsonPropertyName("vehicleNo")]
    public string? VehicleNo { get; init; }

    [JsonPropertyName("tid")]
    public string? Tid { get; init; }
}

public record MessageUpdate(string Type, string Text);

public class TransactionState
{
    public string ClientKey { get; set; } = string.Empty;
    public string Lpn { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Msg { get; set; } = string.Empty;
    public decimal? Fee { get; set; }
    public string EntryTime { get; set; } = string.Empty;
    public string ExitTime { get; set; } = string.Empty;
    public string ParkingDuration { get; set; } = string.Empty;
    public string QrcodePayment { get; set; } = string.Empty;
    public string QrcodeReceipt { get; set; } = string.Empty;
}

// ---- LPRS DTOs (from AffcManagementSystem.InternalApi swagger GetVehicleSummary*) ----
public class LprsEnvelope
{
    public int? ResCode { get; set; }
    public string? ResMsg { get; set; }
    public DateTime? TransmissionTimestamp { get; set; }
    public LprsVehicleSummaryData? Data { get; set; }
}

public class LprsVehicleSummaryData
{
    public string? Lpn { get; set; }
    public LprsPresentVehicleInfo? PresentVehicleInfo { get; set; }
    public LprsBookingInfo? CurrentBookingInfo { get; set; }
    public LprsBookingTimeline? CurrentBookingTimeline { get; set; }
    public List<LprsBookingInfo>? SubBookingRecords { get; set; }
    public List<LprsPaymentRecord>? PaymentRecords { get; set; }
}

public class LprsPresentVehicleInfo
{
    public bool IsPresent { get; set; }
    public string? Lpn { get; set; }
    public DateTime? EntryTime { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? IdentityName { get; set; }
    public string? Floor { get; set; }
    public string? EntryImagePath { get; set; }
    public string? CarBodyImagePath { get; set; }
}

public class LprsBookingInfo
{
    public Guid? RegistrationSubBookingId { get; set; }
    public Guid? RegistrationId { get; set; }
    public DateTime? CreatedTime { get; set; }
    public DateTime? ScheduledEntryTime { get; set; }
    public string? Lpn { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Remarks { get; set; }
    public int? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public int? Status { get; set; }
    public DateTime? ApprovedTime { get; set; }
    public DateTime? PayTime { get; set; }
    public DateTime? EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public DateTime? EntryTime3F { get; set; }
    public DateTime? ExitTime3F { get; set; }
    public int? AssignedWarehouseId { get; set; }
    public string? AssignedBayNumber { get; set; }
    public string? WarehouseName { get; set; }
    public string? WarehouseFloor { get; set; }
    public DateTime? RejectedTime { get; set; }
    public int? VehicleType { get; set; }
}

public class LprsBookingTimeline
{
    public DateTime? Created { get; set; }
    public DateTime? Entered3F { get; set; }
    public DateTime? Approved { get; set; }
    public DateTime? Paid { get; set; }
    public DateTime? EntryPermitted { get; set; }
    public DateTime? Exited3F { get; set; }
    public DateTime? EnteredWarehouse { get; set; }
    public DateTime? ExitedWarehouse { get; set; }
}

public class LprsPaymentRecord
{
    public Guid? PaymentId { get; set; }
    public double? PaidAmount { get; set; }
    public DateTime? PaidTime { get; set; }
    public string? PaymentStation { get; set; }
    public double? TotalFee { get; set; }
    public string? VehicleTypeName { get; set; }
    public string? Floor { get; set; }
}

public static class VehiclePanelMapper
{
    public static object Map(LprsVehicleSummaryData data, LprsApiOptions opt, string requestedLpn, string clientKey, string? tid)
    {
        var present = data.PresentVehicleInfo;
        var booking = data.CurrentBookingInfo;
        var timeline = data.CurrentBookingTimeline;
        var payments = data.PaymentRecords;

        var lpn = FirstNonEmpty(data.Lpn, present?.Lpn, booking?.Lpn, requestedLpn);
        var entryTime = FormatDt(present?.EntryTime ?? booking?.EntryTime ?? booking?.EntryTime3F ?? timeline?.Entered3F);
        var identity = present?.IdentityName ?? "";
        var vehicleType = present?.VehicleTypeName
            ?? payments?.FirstOrDefault()?.VehicleTypeName
            ?? "";
        var warehouseDest = BuildWarehouseDest(booking?.WarehouseFloor, booking?.WarehouseName, present?.Floor);

        string approvalStatus;
        if (booking?.RejectedTime != null)
            approvalStatus = "已拒絕";
        else if (booking?.ApprovedTime != null || timeline?.Approved != null)
            approvalStatus = "已批核";
        else
            approvalStatus = "待批核";

        DateTime? payDt = booking?.PayTime
            ?? timeline?.Paid
            ?? payments?.OrderByDescending(p => p.PaidTime).FirstOrDefault()?.PaidTime;
        var paymentStatus = payDt != null ? "已付款" : "未付款";
        var paymentTime = FormatDt(payDt);

        var photo = ResolvePhotoUrl(present?.EntryImagePath, present?.CarBodyImagePath, opt, lpn);

        // hasBooking when CurrentBookingInfo exists with id or other meaningful fields
        var hasBooking = booking != null && (
            booking.RegistrationSubBookingId != null
            || booking.RegistrationId != null
            || booking.Status != null
            || booking.CreatedTime != null
            || booking.ApprovedTime != null
            || booking.PayTime != null
            || booking.RejectedTime != null
            || !string.IsNullOrWhiteSpace(booking.CompanyName)
            || !string.IsNullOrWhiteSpace(booking.WarehouseName)
            || !string.IsNullOrWhiteSpace(booking.WarehouseFloor)
            || !string.IsNullOrWhiteSpace(booking.Lpn));
        var bookingId = (booking?.RegistrationSubBookingId ?? booking?.RegistrationId)?.ToString() ?? "";

        return new
        {
            clientKey,
            tid,
            // English JSON keys only (no Chinese aliases; no PHOTO twin)
            photo,
            lpn,
            entryTime,
            identity,
            vehicleType,
            warehouseDestination = warehouseDest,
            approvalStatus,
            paymentStatus,
            paymentTime,
            hasBooking,
            bookingId,
            // Extra context for debugging / richer UIs
            floor = present?.Floor,
            warehouseName = booking?.WarehouseName,
            warehouseFloor = booking?.WarehouseFloor,
            companyName = booking?.CompanyName,
            isPresent = present?.IsPresent,
            rawStatus = booking?.Status,
            ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    static string BuildWarehouseDest(string? floor, string? name, string? presentFloor)
    {
        var f = FirstNonEmpty(floor, presentFloor);
        var n = (name ?? "").Trim();
        if (!string.IsNullOrEmpty(f) && !f.EndsWith("層", StringComparison.Ordinal) && f.Length <= 2)
            f = f + "層";
        if (!string.IsNullOrEmpty(f) && !string.IsNullOrEmpty(n))
            return $"{f} / {n}";
        if (!string.IsNullOrEmpty(f)) return f;
        if (!string.IsNullOrEmpty(n)) return n;
        return "";
    }

    static string? ResolvePhotoUrl(string? entryImagePath, string? carBodyImagePath, LprsApiOptions opt, string lpn)
    {
        var path = FirstNonEmpty(entryImagePath, carBodyImagePath);
        if (!string.IsNullOrEmpty(path))
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return path;

            var baseUrl = (opt.BaseUrl ?? "").TrimEnd('/');
            if (path.StartsWith("/"))
                return baseUrl + path;
            return baseUrl + "/" + path;
        }

        // Fallback absolute EntryImage endpoint (may require separate auth)
        if (!string.IsNullOrWhiteSpace(opt.EntryImagePathTemplate) && !string.IsNullOrWhiteSpace(lpn))
        {
            var rel = opt.EntryImagePathTemplate
                .Replace("{lpn}", Uri.EscapeDataString(lpn), StringComparison.OrdinalIgnoreCase)
                .Replace("{LPN}", Uri.EscapeDataString(lpn), StringComparison.OrdinalIgnoreCase);
            var baseUrl = (opt.BaseUrl ?? "").TrimEnd('/');
            return baseUrl + (rel.StartsWith("/") ? rel : "/" + rel);
        }
        return null;
    }

    static string FormatDt(DateTime? dt)
    {
        if (dt == null) return "";
        return dt.Value.ToString("yyyy-MM-dd HH:mm:ss");
    }

    static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return "";
    }
}

public class MessageHub
{
    private readonly ConcurrentDictionary<(string ClientKey, string Type), string> _latest = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ChannelWriter<MessageUpdate>, byte>> _activeWriters = new();
    private readonly ConcurrentDictionary<string, TransactionState> _transactions = new();
    private readonly ILogger<MessageHub> _logger;
    private readonly SseOptions _sseOptions;

    public MessageHub(ILogger<MessageHub> logger, IOptions<SseOptions> sseOptions)
    {
        _logger = logger;
        _sseOptions = sseOptions.Value ?? new SseOptions();
    }

    public int PingIntervalMs => Math.Max(5, _sseOptions.PingIntervalSeconds) * 1000;

    public string[] GetActiveClientKeys()
    {
        return _activeWriters
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => kv.Key)
            .OrderBy(k => k)
            .ToArray();
    }

    public void MergeTransaction(string tid, MessageDto dto)
    {
        var state = _transactions.GetOrAdd(tid, _ => new TransactionState());
        string oldMsg = state.Msg;
        string oldQrPayment = state.QrcodePayment;

        _logger.LogInformation("=== MERGE TRANSACTION === Tid:{Tid} | dto.Msg='{Msg}' | dto.QrcodePayment='{QrPay}' | state.Msg='{OldMsg}' | QrcodePayment='{OldQrPay}'",
            tid, dto.Msg, dto.QrcodePayment, oldMsg, oldQrPayment);
        LogFile.Write($"=== MERGE TRANSACTION === Tid:{tid} | dto.Msg='{dto.Msg}' | dto.QrcodePayment='{dto.QrcodePayment}' | state.Msg='{oldMsg}' | QrcodePayment='{oldQrPayment}'");

        if (!string.IsNullOrWhiteSpace(dto.ClientKey))
            state.ClientKey = dto.ClientKey;
        if (!string.IsNullOrWhiteSpace(dto.Lpn))
            state.Lpn = dto.Lpn;
        if (!string.IsNullOrWhiteSpace(dto.Type))
            state.Type = dto.Type;
        if (dto.Msg is not null)
            state.Msg = dto.Msg;
        if (dto.Fee.HasValue)
            state.Fee = dto.Fee;
        if (!string.IsNullOrWhiteSpace(dto.EntryTime))
            state.EntryTime = dto.EntryTime;
        if (!string.IsNullOrWhiteSpace(dto.ExitTime))
            state.ExitTime = dto.ExitTime;
        if (!string.IsNullOrWhiteSpace(dto.ParkingDuration))
            state.ParkingDuration = dto.ParkingDuration;
        if (!string.IsNullOrWhiteSpace(dto.QrcodePayment))
            state.QrcodePayment = dto.QrcodePayment;
        if (!string.IsNullOrWhiteSpace(dto.QrcodeReceipt))
            state.QrcodeReceipt = dto.QrcodeReceipt;

        _logger.LogInformation("=== MERGE AFTER === Tid:{Tid} | state.Msg='{NewMsg}' | QrcodePayment='{NewQrPay}' | QrcodeReceipt='{NewQrReceipt}' | ParkingDuration='{NewDuration}'",
            tid, state.Msg, state.QrcodePayment, state.QrcodeReceipt, state.ParkingDuration);
        LogFile.Write($"=== MERGE AFTER === Tid:{tid} | state.Msg='{state.Msg}' | QrcodePayment='{state.QrcodePayment}' | QrcodeReceipt='{state.QrcodeReceipt}' | ParkingDuration='{state.ParkingDuration}'");
    }

    public TransactionState? GetTransaction(string tid)
    {
        _transactions.TryGetValue(tid, out var state);
        return state;
    }

    public void Publish(string clientKey, string type, string text)
    {
        var key = (clientKey, type);
        _latest[key] = text;

        if (_activeWriters.TryGetValue(clientKey, out var writers))
        {
            foreach (var kv in writers.ToArray())
            {
                var writer = kv.Key;
                if (!writer.TryWrite(new MessageUpdate(type, text)))
                {
                    writers.TryRemove(writer, out _);
                }
            }
        }
    }

    public void Clear(string clientKey)
    {
        var keysToRemove = _latest.Keys.Where(k => k.ClientKey == clientKey).ToList();
        foreach (var key in keysToRemove)
        {
            _latest.TryRemove(key, out _);
        }

        if (_activeWriters.TryGetValue(clientKey, out var writers))
        {
            foreach (var kv in writers.ToArray())
            {
                var writer = kv.Key;
                if (!writer.TryWrite(new MessageUpdate("clear", "CLEAR_SCREEN")))
                {
                    writers.TryRemove(writer, out _);
                }
            }
        }
    }

    public string? GetLast(string clientKey, string type)
    {
        var key = (clientKey, type);
        return _latest.TryGetValue(key, out var msg) ? msg : null;
    }

    public IAsyncEnumerable<MessageUpdate> Subscribe(string clientKey, string remoteIp, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<MessageUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var writer = channel.Writer;
        var writersForKey = _activeWriters.GetOrAdd(clientKey, _ => new ConcurrentDictionary<ChannelWriter<MessageUpdate>, byte>());
        writersForKey.TryAdd(writer, 0);

        ct.Register(() =>
        {
            writersForKey.TryRemove(writer, out _);
            writer.TryComplete();
            _logger.LogInformation("Client writer removed for ClientKey {ClientKey} RemoteIp={RemoteIp}", clientKey, remoteIp);
            LogFile.Write($"Client writer removed for ClientKey {clientKey} RemoteIp={remoteIp}");
        });

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(PingIntervalMs, ct);
                    var pingData = $"{{\"ts\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\"clientKey\":\"{clientKey}\",\"ip\":\"{remoteIp}\"}}";
                    if (!writer.TryWrite(new MessageUpdate("ping", pingData)))
                    {
                        _logger.LogWarning("SSE PING TryWrite failed ClientKey={ClientKey} RemoteIp={RemoteIp}", clientKey, remoteIp);
                        LogFile.Write($"SSE PING TryWrite failed ClientKey={clientKey} RemoteIp={remoteIp}");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSE ping loop error ClientKey={ClientKey} RemoteIp={RemoteIp}", clientKey, remoteIp);
                LogFile.Error($"SSE ping loop error ClientKey={clientKey} RemoteIp={remoteIp}: {ex}");
            }
        }, ct);

        return channel.Reader.ReadAllAsync(ct);
    }
}
