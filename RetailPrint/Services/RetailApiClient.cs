using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class RetailApiException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }
    public HttpStatusCode StatusCode { get; }

    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;

    public RetailApiException(
        string code,
        string message,
        bool retryable,
        HttpStatusCode statusCode)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
        StatusCode = statusCode;
    }
}

public sealed class RetailApiClient : IDisposable
{
    private const string ProductionBaseUrl = "https://hung-phat-945da1547594.herokuapp.com/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;

    public RetailApiClient()
    {
        var configured = Environment.GetEnvironmentVariable("RETAIL_PRINT_API_URL");
        var baseUrl = Uri.TryCreate(configured, UriKind.Absolute, out var custom)
            ? custom
            : new Uri(ProductionBaseUrl);

        _httpClient = new HttpClient
        {
            BaseAddress = baseUrl,
            Timeout = TimeSpan.FromSeconds(35)
        };
    }

    public async Task<PairingResult> StartPairingAsync(
        DeviceIdentity identity,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            deviceId = identity.DeviceId,
            deviceName,
            protocolVersion = "1",
            credentialHash = DeviceIdentityService.CredentialHash(identity),
            pairingProofHash = DeviceIdentityService.CreatePairingProofHash()
        };

        using var request = CreateRequest(
            HttpMethod.Post,
            "api/retail/print-agent/agent/pairing",
            identity: null,
            body);

        var result = await SendAsync<PairingResult>(request, cancellationToken);
        return result ?? throw new RetailApiException(
            "PAIRING_RESPONSE_INVALID",
            "Công Ty chưa trả về mã kết nối hợp lệ.",
            true,
            HttpStatusCode.ServiceUnavailable);
    }

    public async Task HeartbeatAsync(
        DeviceIdentity identity,
        PrinterSettings settings,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            printerName = settings.PrinterName,
            paperWidthMm = settings.PaperWidthMm
        };

        using var request = CreateRequest(
            HttpMethod.Post,
            "api/retail/print-agent/agent/heartbeat",
            identity,
            body);

        await SendAsync<object>(request, cancellationToken);
    }

    public async Task<RetailPrintJob?> ClaimJobAsync(
        DeviceIdentity identity,
        int waitSeconds = 20,
        CancellationToken cancellationToken = default)
    {
        waitSeconds = Math.Clamp(waitSeconds, 0, 20);
        using var request = CreateRequest(
            HttpMethod.Get,
            $"api/retail/print-agent/agent/jobs?wait={waitSeconds}",
            identity,
            body: null);

        return await SendAsync<RetailPrintJob?>(request, cancellationToken);
    }

    public async Task CompleteJobAsync(
        DeviceIdentity identity,
        string jobId,
        bool success,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            success,
            errorCode,
            errorMessage
        };

        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/retail/print-agent/agent/jobs/{Uri.EscapeDataString(jobId)}/result",
            identity,
            body);

        await SendAsync<object>(request, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        DeviceIdentity? identity,
        object? body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.ParseAdd("application/json");

        if (identity is not null)
        {
            request.Headers.TryAddWithoutValidation("x-retail-print-device-id", identity.DeviceId);
            request.Headers.TryAddWithoutValidation("x-retail-print-credential", identity.Credential);
        }

        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        return request;
    }

    private async Task<T?> SendAsync<T>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RetailApiException(
                "RETAIL_PRINT_API_TIMEOUT",
                "Kết nối Công Ty bị quá thời gian.",
                true,
                HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException)
        {
            throw new RetailApiException(
                "RETAIL_PRINT_API_UNAVAILABLE",
                "Chưa thể kết nối tới Công Ty.",
                true,
                HttpStatusCode.ServiceUnavailable);
        }

        using (response)
        {
            ApiEnvelope<T>? envelope = null;
            try
            {
                envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(
                    JsonOptions,
                    cancellationToken);
            }
            catch (JsonException)
            {
                // Xử lý bên dưới bằng thông báo ổn định, không lộ nội dung máy chủ.
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RetailApiException(
                    envelope?.Error?.Code ?? "RETAIL_PRINT_API_REQUEST_FAILED",
                    envelope?.Error?.Message ?? "Công Ty chưa thể xử lý yêu cầu Retail Print.",
                    envelope?.Error?.Retryable == true || (int)response.StatusCode >= 500,
                    response.StatusCode);
            }

            return envelope is null ? default : envelope.Data;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
