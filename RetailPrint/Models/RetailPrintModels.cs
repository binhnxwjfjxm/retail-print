using System.Text.Json.Serialization;

namespace RetailPrint.Models;

public sealed class DeviceIdentity
{
    public string DeviceId { get; set; } = "";
    public string Credential { get; set; } = "";
}

public sealed class PairingResult
{
    public string AgentId { get; set; } = "";
    public string PairingCode { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public string DeviceName { get; set; } = "";
}

public sealed class RetailPrintJob
{
    public string JobId { get; set; } = "";
    public RetailPrintPayload Payload { get; set; } = new();
    public int DeliveryAttempt { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

public sealed class RetailPrintPayload
{
    public string DocumentType { get; set; } = "";
    public string Paper { get; set; } = "";
    public int Copies { get; set; } = 1;
    public string? Heading { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? DocumentNumber { get; set; }
    public List<RetailPrintMeta> Meta { get; set; } = [];
    public List<string> Columns { get; set; } = [];
    public List<List<string>> Rows { get; set; } = [];
    public List<RetailPrintTotal> Totals { get; set; } = [];
    public List<string>? Footer { get; set; }
}

public sealed class RetailPrintMeta
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class RetailPrintTotal
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ApiEnvelope<T>
{
    public T? Data { get; set; }
    public ApiError? Error { get; set; }
}

public sealed class ApiError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
    public bool Retryable { get; set; }
}

public enum LocalPrintJobState
{
    Processing,
    Printed,
    Unknown
}

public sealed class LocalPrintJobRecord
{
    public string JobId { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LocalPrintJobState State { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
