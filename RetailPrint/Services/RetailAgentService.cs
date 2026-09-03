using RetailPrint.Models;

namespace RetailPrint.Services;

public sealed class RetailAgentService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DeviceIdentityService _identityService;
    private readonly RetailApiClient _apiClient;
    private readonly PrinterClient _printerClient;
    private readonly PrintJobJournal _journal;
    private readonly object _pairingGate = new();
    private readonly SemaphoreSlim _connectionCodeGate = new(1, 1);
    private PairingResult? _currentPairing;
    private CancellationTokenSource? _cancellation;
    private Task? _loopTask;

    public event Action<bool?, string>? StatusChanged;
    public event Action<PairingResult?>? PairingChanged;

    public RetailAgentService(SettingsService settingsService, DeviceIdentityService identityService, RetailApiClient apiClient, PrinterClient printerClient, PrintJobJournal journal)
    {
        _settingsService = settingsService;
        _identityService = identityService;
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

    private async Task<PairingResult> EnsureConnectionCodeAsync(
        DeviceIdentity identity,
        CancellationToken cancellationToken)
    {
        lock (_pairingGate)
        {
            if (_currentPairing is not null && !string.IsNullOrWhiteSpace(_currentPairing.PairingCode))
                return _currentPairing;
        }

        await _connectionCodeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_pairingGate)
            {
                if (_currentPairing is not null && !string.IsNullOrWhiteSpace(_currentPairing.PairingCode))
                    return _currentPairing;
            }

            var deviceName = $"Retail Print - {Environment.MachineName}";
            if (deviceName.Length > 120) deviceName = deviceName[..120];
            SetStatus(null, "Đang tải mã kết nối…");
            var pairing = await _apiClient.GetConnectionCodeAsync(identity, deviceName, cancellationToken);
            lock (_pairingGate) _currentPairing = pairing;
            PairingChanged?.Invoke(pairing);
            SetStatus(null, "Mã kết nối đã sẵn sàng");
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
                SetStatus(false, "Chưa kết nối Retail — nhập mã 8 ký tự trên điện thoại");
                await DelayAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (RetailApiException error)
            {
                SetStatus(false, error.Retryable ? "Tạm mất kết nối Công Ty" : error.Message);
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

    private void SetStatus(bool? connected, string text) => StatusChanged?.Invoke(connected, text);

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
