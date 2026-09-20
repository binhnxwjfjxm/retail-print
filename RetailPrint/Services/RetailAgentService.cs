using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class RetailAgentService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DeviceIdentityService _identityService;
    private readonly PairingCacheService _pairingCacheService;
    private readonly RetailApiClient _apiClient;
    private readonly PrinterClient _printerClient;
    private readonly PrintJobJournal _journal;
    private readonly object _pairingGate = new();
    private readonly object _statusGate = new();
    private readonly SemaphoreSlim _connectionCodeGate = new(1, 1);
    private PairingResult? _currentPairing;
    private string? _currentPairingDeviceId;
    private bool _forcePairingRefresh;
    private bool? _lastStatusConnected;
    private string? _lastStatusText;
    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;

    public event Action<bool?, string>? StatusChanged;
    public event Action<PairingResult?>? PairingChanged;

    public RetailAgentService(
        SettingsService settingsService,
        DeviceIdentityService identityService,
        PairingCacheService pairingCacheService,
        RetailApiClient apiClient,
        PrinterClient printerClient,
        PrintJobJournal journal)
    {
        _settingsService = settingsService;
        _identityService = identityService;
        _pairingCacheService = pairingCacheService;
        _apiClient = apiClient;
        _printerClient = printerClient;
        _journal = journal;
    }

    public void Start()
    {
        if (_loopTask is not null) return;
        _cancellation = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cancellation.Token));
    }

    internal static bool ConnectionCodeMatchesDevice(string? cachedDeviceId, DeviceIdentity identity) =>
        !string.IsNullOrWhiteSpace(cachedDeviceId)
        && string.Equals(cachedDeviceId, identity.DeviceId, StringComparison.OrdinalIgnoreCase);

    private PairingResult? ReadCachedConnectionCode(DeviceIdentity identity, out bool invalidated)
    {
        lock (_pairingGate)
        {
            invalidated = _currentPairing is not null
                && !ConnectionCodeMatchesDevice(_currentPairingDeviceId, identity);

            if (invalidated)
            {
                _currentPairing = null;
                _currentPairingDeviceId = null;
                return null;
            }

            return _currentPairing is not null
                && !string.IsNullOrWhiteSpace(_currentPairing.PairingCode)
                && ConnectionCodeMatchesDevice(_currentPairingDeviceId, identity)
                    ? _currentPairing
                    : null;
        }
    }

    private async Task<PairingResult> EnsureConnectionCodeAsync(
        DeviceIdentity identity,
        CancellationToken cancellationToken)
    {
        var cached = ReadCachedConnectionCode(identity, out var invalidated);
        if (invalidated) PairingChanged?.Invoke(null);
        if (cached is not null) return cached;

        await _connectionCodeGate.WaitAsync(cancellationToken);
        try
        {
            cached = ReadCachedConnectionCode(identity, out invalidated);
            if (invalidated) PairingChanged?.Invoke(null);
            if (cached is not null && !_forcePairingRefresh) return cached;

            var persisted = _pairingCacheService.Load(identity);
            if (persisted is not null)
            {
                PairingChanged?.Invoke(persisted);
                if (!_forcePairingRefresh)
                {
                    lock (_pairingGate)
                    {
                        _currentPairing = persisted;
                        _currentPairingDeviceId = identity.DeviceId;
                    }
                    return persisted;
                }
            }

            var deviceName = $"Retail Print - {Environment.MachineName}";
            if (deviceName.Length > 120) deviceName = deviceName[..120];
            var pairing = await _apiClient.GetConnectionCodeAsync(identity, deviceName, cancellationToken);
            _pairingCacheService.Save(identity, pairing);
            lock (_pairingGate)
            {
                _currentPairing = pairing;
                _currentPairingDeviceId = identity.DeviceId;
                _forcePairingRefresh = false;
            }
            PairingChanged?.Invoke(pairing);
            return pairing;
        }
        finally
        {
            _connectionCodeGate.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        SetStatus(null, "Đang kết nối Công Ty…");
        while (!cancellationToken.IsCancellationRequested)
        {
            var identity = _identityService.LoadOrCreate();
            var settings = _settingsService.Load();
            try
            {
                await EnsureConnectionCodeAsync(identity, cancellationToken);
                await _apiClient.HeartbeatAsync(identity, settings, cancellationToken);
                SetStatus(true, "Retail đang trực tuyến");
                var job = await _apiClient.ClaimJobAsync(identity, 20, cancellationToken);
                if (job is not null) await ProcessJobAsync(identity, settings, job, cancellationToken);
            }
            catch (RetailApiException error) when (error.IsUnauthorized)
            {
                RequirePairingRefresh(identity);
                SetStatus(false, "Chưa kết nối Retail — nhập mã 8 ký tự trên điện thoại");
                await DelayAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (RetailApiException error)
            {
                SetStatus(false, error.Message);
                await DelayAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch
            {
                SetStatus(false, "Retail Print đang chờ kết nối lại");
                await DelayAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }

    private async Task ProcessJobAsync(DeviceIdentity identity, PrinterSettings settings, RetailPrintJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.JobId)) return;
        var previousState = _journal.GetState(job.JobId);
        if (previousState == LocalPrintJobState.Printed)
        {
            await TryAcknowledgeAsync(identity, job.JobId, true, null, null, cancellationToken);
            return;
        }
        if (previousState is LocalPrintJobState.Processing or LocalPrintJobState.Unknown)
        {
            await TryAcknowledgeAsync(identity, job.JobId, false, "PRINT_RESULT_UNKNOWN", "Kết quả lần in trước chưa xác định. Retail Print không tự in lại để tránh trùng phiếu.", cancellationToken);
            return;
        }

        _journal.Mark(job.JobId, LocalPrintJobState.Processing);
        SetStatus(true, "Đang in chứng từ…");
        try
        {
            await _printerClient.PrintAsync(settings, job.Payload, cancellationToken);
            _journal.Mark(job.JobId, LocalPrintJobState.Printed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _journal.Mark(job.JobId, LocalPrintJobState.Unknown);
            throw;
        }
        catch (Exception error)
        {
            _journal.Mark(job.JobId, LocalPrintJobState.Unknown);
            await TryAcknowledgeAsync(identity, job.JobId, false, "PRINTER_SEND_FAILED", SafePrinterMessage(error.Message), cancellationToken);
            SetStatus(false, SafePrinterMessage(error.Message));
            return;
        }

        try
        {
            await _apiClient.CompleteJobAsync(identity, job.JobId, true, cancellationToken: cancellationToken);
            SetStatus(true, "Đã in xong");
        }
        catch (RetailApiException)
        {
            SetStatus(true, "Đã in, đang chờ xác nhận");
        }
    }

    private async Task TryAcknowledgeAsync(DeviceIdentity identity, string jobId, bool success, string? errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        try { await _apiClient.CompleteJobAsync(identity, jobId, success, errorCode, errorMessage, cancellationToken); }
        catch (RetailApiException) { }
    }

    private void RequirePairingRefresh(DeviceIdentity identity)
    {
        lock (_pairingGate)
        {
            if (!ConnectionCodeMatchesDevice(_currentPairingDeviceId, identity))
                return;

            _currentPairing = null;
            _currentPairingDeviceId = null;
            _forcePairingRefresh = true;
        }
    }

    private void SetStatus(bool? connected, string text)
    {
        lock (_statusGate)
        {
            if (_lastStatusConnected == connected
                && string.Equals(_lastStatusText, text, StringComparison.Ordinal))
            {
                return;
            }

            _lastStatusConnected = connected;
            _lastStatusText = text;
        }

        StatusChanged?.Invoke(connected, text);
    }

    private static string SafePrinterMessage(string message)
    {
        var value = string.IsNullOrWhiteSpace(message) ? "Không thể gửi chứng từ tới máy in." : message.Trim();
        return value.Length <= 180 ? value : value[..180];
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (_cancellation is null) return;
        _cancellation.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cancellation.Dispose();
        _cancellation = null;
        _loopTask = null;
        _connectionCodeGate.Dispose();
    }
}
