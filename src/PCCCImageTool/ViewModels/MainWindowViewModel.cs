using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCCCComm;
using PCCCComm.Core;
using PCCCImageTool.Models;
using PCCCImageTool.Services;
using ReactiveUI;

namespace PCCCImageTool.ViewModels;

public class MainWindowViewModel : ReactiveObject, IDisposable
{
    // ─── Private state ────────────────────────────────────────────────────────
    private global::PCCCComm.PCCCComm? _df1;
    private CancellationTokenSource? _cts;
    private PlcInfo _currentPlcInfo = new PlcInfo(0, "Unknown", false, "Unknown", string.Empty, 0, 0, "UNKNOWN");
    private readonly IDialogService _dialogService;
    private bool _disposed;

    // Log buffer – capped at MaxLogLines
    private readonly StringBuilder _logBuffer = new();
    private int _logLineCount;
    private const int MaxLogLines = 500;

    // ─── Backing fields ───────────────────────────────────────────────────────
    private bool    _isBusy;
    private bool    _isConnected;
    private string  _statusText      = "Not connected";
    private double  _progressValue;
    private string  _progressMessage = string.Empty;
    private string  _selectedPort    = string.Empty;
    private int     _selectedBaud    = 19200;
    private string  _selectedParity  = "None";
    private string  _selectedChecksum  = "Crc";
    private decimal _targetNode      = 1;
    private decimal _myNode          = 0;
    private string  _logText         = string.Empty;

    private string _eipHost    = "127.0.0.1";
    private int    _eipPort    = 44818;
    private int    _eipTimeout = 5000;

    private TransportType _transportType = TransportType.Df1FullDuplex;
    public List<TransportType> TransportOptions { get; } = new() 
    { 
        TransportType.Df1FullDuplex, 
        TransportType.Df1HalfDuplex,
        TransportType.Eip 
    };

    // ─── Half-duplex specific ────────────────────────────────────────────────
    private string _rs485Mode = "Auto";
    private bool _echoSuppression = false;
    private int _rtsAssertDelay = 1;
    private int _rtsDeassertDelay = 5;
    public List<string> Rs485Modes { get; } = new() { "Auto", "Rts", "Dtr" };

    // ─── Log throttling fields ──────────────────────────────────────────────
    private readonly StringBuilder _pendingLogBatch = new();
    private readonly object _logBatchLock = new();
    private DateTime _lastLogFlush = DateTime.MinValue;
    private const int LogFlushIntervalMs = 50;  // Max 20 updates per second

    // ─── Observable collections ───────────────────────────────────────────────
    public ObservableCollection<string> AvailablePorts { get; } = new();
    public ObservableCollection<int>    BaudRates      { get; } = new() { 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
    public ObservableCollection<string> ParityOptions  { get; } = new() { "None", "Even", "Odd" };
    public ObservableCollection<string> ChecksumOptions { get; } = new() { "Crc", "Bcc" };

    // ─── Properties ───────────────────────────────────────────────────────────
    public PlcInfo CurrentPlcInfo
    {
        get => _currentPlcInfo;
        private set
        {
            if (!EqualityComparer<PlcInfo>.Default.Equals(_currentPlcInfo, value))
            {
                _currentPlcInfo = value;
                this.RaisePropertyChanged(nameof(CurrentPlcInfo));
                this.RaisePropertyChanged(nameof(CanUpload));
                this.RaisePropertyChanged(nameof(CanDownload));
                this.RaisePropertyChanged(nameof(CanCompare));
            }
        }
    }

    public TransportType TransportType
    {
        get => _transportType;
        set => this.RaiseAndSetIfChanged(ref _transportType, value);
    }

    public string SelectedPort
    {
        get => _selectedPort;
        set => this.RaiseAndSetIfChanged(ref _selectedPort, value);
    }

    public int SelectedBaud
    {
        get => _selectedBaud;
        set => this.RaiseAndSetIfChanged(ref _selectedBaud, value);
    }

    public string SelectedParity
    {
        get => _selectedParity;
        set => this.RaiseAndSetIfChanged(ref _selectedParity, value);
    }

    public string SelectedChecksum
    {
        get => _selectedChecksum;
        set => this.RaiseAndSetIfChanged(ref _selectedChecksum, value);
    }

    public string EipHost
    {
        get => _eipHost;
        set => this.RaiseAndSetIfChanged(ref _eipHost, value);
    }

    public int EipPort
    {
        get => _eipPort;
        set => this.RaiseAndSetIfChanged(ref _eipPort, value);
    }

    public int EipTimeout
    {
        get => _eipTimeout;
        set => this.RaiseAndSetIfChanged(ref _eipTimeout, value);
    }

    public decimal TargetNode
    {
        get => _targetNode;
        set => this.RaiseAndSetIfChanged(ref _targetNode, value);
    }

    public decimal MyNode
    {
        get => _myNode;
        set => this.RaiseAndSetIfChanged(ref _myNode, value);
    }

    // ─── Half-duplex properties ──────────────────────────────────────────────
    public string Rs485Mode
    {
        get => _rs485Mode;
        set => this.RaiseAndSetIfChanged(ref _rs485Mode, value);
    }

    public bool EchoSuppression
    {
        get => _echoSuppression;
        set => this.RaiseAndSetIfChanged(ref _echoSuppression, value);
    }

    public int RtsAssertDelay
    {
        get => _rtsAssertDelay;
        set => this.RaiseAndSetIfChanged(ref _rtsAssertDelay, value);
    }

    public int RtsDeassertDelay
    {
        get => _rtsDeassertDelay;
        set => this.RaiseAndSetIfChanged(ref _rtsDeassertDelay, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(CanUpload));
            this.RaisePropertyChanged(nameof(CanDownload));
            this.RaisePropertyChanged(nameof(CanCompare));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            this.RaisePropertyChanged(nameof(CanUpload));
            this.RaisePropertyChanged(nameof(CanDownload));
            this.RaisePropertyChanged(nameof(CanCompare));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set => this.RaiseAndSetIfChanged(ref _progressMessage, value);
    }

    /// <summary>
    /// Append-only log string bound to a read-only TextBox.
    /// Raises PropertyChanged on every append so the view can scroll to end.
    /// </summary>
    public string LogText
    {
        get => _logText;
        private set => this.RaiseAndSetIfChanged(ref _logText, value);
    }

    public bool CanUpload   => IsConnected && !IsBusy && _currentPlcInfo.SupportsUploadDownload;
    public bool CanDownload => IsConnected && !IsBusy && _currentPlcInfo.SupportsUploadDownload;
    public bool CanCompare => IsConnected && !IsBusy && _currentPlcInfo.SupportsUploadDownload;

    // ─── Commands ─────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> RefreshPortsCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectCommand      { get; }
    public ReactiveCommand<Unit, Unit> DisconnectCommand   { get; }
    public ReactiveCommand<Unit, Unit> UploadCommand       { get; }
    public ReactiveCommand<Unit, Unit> DownloadCommand     { get; }
    public ReactiveCommand<Unit, Unit> ClearLogCommand     { get; }
    public ReactiveCommand<Unit, Unit> CompareCommand      { get; }
    public ReactiveCommand<Unit, Unit> AboutCommand        { get; }

    private void OnRawFrameSent(object? sender, byte[] frame)     => AppendLog($"TX  {FrameDecoder.Hex(frame)}\n    {FrameDecoder.Decode(frame)}");
    private void OnRawFrameReceived(object? sender, byte[] frame) => AppendLog($"RX  {FrameDecoder.Hex(frame)}\n    {FrameDecoder.Decode(frame)}");

    // ─── Constructor ─────────────────────────────────────────────────────────
    public MainWindowViewModel(IDialogService? dialogService = null)
    {
        _dialogService = dialogService ?? new AvaloniaDialogService();

        var notBusy     = this.WhenAnyValue(x => x.IsBusy).Select(b => !b);
        var canConnect  = this.WhenAnyValue(x => x.IsBusy, x => x.IsConnected,
                              (busy, connected) => !busy && !connected);
        var canUpload   = this.WhenAnyValue(x => x.CanUpload);
        var canDownload = this.WhenAnyValue(x => x.CanDownload);

        RefreshPortsCommand = ReactiveCommand.Create(RefreshPorts, notBusy);
        ConnectCommand      = ReactiveCommand.CreateFromTask(ConnectAsync, canConnect);
        DisconnectCommand   = ReactiveCommand.Create(Disconnect,
                            this.WhenAnyValue(x => x.IsConnected, x => x.IsBusy,
                                (connected, busy) => connected && !busy));
        UploadCommand       = ReactiveCommand.CreateFromTask(UploadAsync,   canUpload);
        DownloadCommand     = ReactiveCommand.CreateFromTask(DownloadAsync, canDownload);
        ClearLogCommand     = ReactiveCommand.Create(ClearLog);
        CompareCommand      = ReactiveCommand.CreateFromTask(CompareAsync, this.WhenAnyValue(x => x.CanCompare));
        AboutCommand        = ReactiveCommand.Create(ShowAbout);

        RefreshPorts();
    }

    // ─── Logging ─────────────────────────────────────────────────────────────
    private void AppendLog(string line)
    {
        lock (_logBatchLock)
        {
            _pendingLogBatch.AppendLine($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        }
        
        // Throttle: flush only if enough time has passed or batch is getting large
        if ((DateTime.Now - _lastLogFlush).TotalMilliseconds >= LogFlushIntervalMs || 
            _pendingLogBatch.Length > 2000)
        {
            FlushPendingLogs();
        }
    }

    private void ClearLog()
    {
        // Flush any pending logs first
        FlushPendingLogs();
        
        _logBuffer.Clear();
        _logLineCount = 0;
        LogText = string.Empty;
        
        lock (_logBatchLock)
        {
            _pendingLogBatch.Clear();
        }
    }

    private void FlushPendingLogs()
    {
        string batch;
        lock (_logBatchLock)
        {
            if (_pendingLogBatch.Length == 0) return;
            batch = _pendingLogBatch.ToString();
            _pendingLogBatch.Clear();
        }
        
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _logBuffer.Append(batch);
            _logLineCount += batch.Count(c => c == '\n');
            
            // Trim if exceeding max lines
            while (_logLineCount > MaxLogLines)
            {
                string current = _logBuffer.ToString();
                int idx = current.IndexOf('\n');
                if (idx >= 0)
                    _logBuffer.Remove(0, idx + 1);
                else
                    _logBuffer.Clear();
                _logLineCount--;
            }
            
            LogText = _logBuffer.ToString();
            _lastLogFlush = DateTime.Now;
        });
    }

    /// <summary>
    /// Waits for all pending UI thread operations (including log updates) to complete.
    /// Call this before showing dialogs to ensure logs are fully rendered.
    /// </summary>
    private async Task FlushLogsAsync()
    {
        // Flush any pending log batches
        FlushPendingLogs();
        
        // Wait for UI thread to process all queued operations
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
    }

    // ─── About ───────────────────────────────────────────────────────────────
    private async void ShowAbout()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "1.0";
        
        await _dialogService.ShowMessageAsync(
            "About PCCC Image Transfer Tool",
            $"PCCC Image Transfer Tool\n" +
            $"Version {version}\n\n" +
            "A simple tool to upload/download SLC/MicroLogix PLC programs via supported protocol.\n\n" +
            "© 2026 Ketut Kumajaya, Samator Indo Gas\n\n" +
            "License: GPL-3.0-or-later");
    }

    // ─── Port management ─────────────────────────────────────────────────────
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        var ports = SerialPort.GetPortNames();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (ports == null || ports.Length == 0)
            {
                ports = new[] { "/dev/ttyS0", "/dev/ttyUSB0", "/dev/ttyACM0", "/dev/ttyS31" };
            }
            else
            {
                var list = new List<string>(ports);
                foreach (var p in new[] { "/dev/ttyS0", "/dev/ttyUSB0", "/dev/ttyACM0", "/dev/ttyS31" })
                {
                    if (!list.Contains(p)) list.Add(p);
                }
                ports = list.ToArray();
            }
        }

        foreach (var p in ports)
            AvailablePorts.Add(p);

        if (AvailablePorts.Count > 0)
            SelectedPort = AvailablePorts[0];
    }

    // ─── Connect / Disconnect ────────────────────────────────────────────────
    private async Task ConnectAsync()
    {
        if (TransportType == TransportType.Df1FullDuplex && string.IsNullOrEmpty(SelectedPort))
        {
            await _dialogService.ShowMessageAsync("Error", "Select a COM port first.");
            return;
        }
        if (TransportType == TransportType.Df1HalfDuplex && string.IsNullOrEmpty(SelectedPort))
        {
            await _dialogService.ShowMessageAsync("Error", "Select a COM port first.");
            return;
        }
        if (TransportType == TransportType.Eip && string.IsNullOrEmpty(EipHost))
        {
            await _dialogService.ShowMessageAsync("Error", "Enter PLC IP address.");
            return;
        }

        IsBusy = true;
        StatusText = "Connecting…";

        if (TransportType == TransportType.Df1FullDuplex)
            AppendLog($"Connecting to {SelectedPort} @ {SelectedBaud} baud ({SelectedParity} parity, {SelectedChecksum} checksum)…");
        else if (TransportType == TransportType.Df1HalfDuplex)
            AppendLog($"Connecting to {SelectedPort} @ {SelectedBaud} baud ({SelectedParity} parity, {SelectedChecksum} checksum) as DF1 half-duplex master…");
        else
            AppendLog($"Connecting to {EipHost}:{EipPort} via EtherNet/IP (timeout {EipTimeout} ms)…");

        try
        {
            DisposeDF1();

            if (TransportType == TransportType.Df1FullDuplex)
            {
                Parity parity = SelectedParity switch
                {
                    "Even" => Parity.Even,
                    "Odd"  => Parity.Odd,
                    _      => Parity.None
                };

                _df1 = new global::PCCCComm.PCCCComm(SelectedPort, SelectedBaud, parity)
                {
                    TargetNode = (int)TargetNode,
                    MyNode     = (int)MyNode,
                    CheckSum   = SelectedChecksum == "Crc" ? CheckSumOptions.Crc : CheckSumOptions.Bcc,
                    Protocol   = "DF1"
                };
            }
            else if (TransportType == TransportType.Df1HalfDuplex)
            {
                Parity parity = SelectedParity switch
                {
                    "Even" => Parity.Even,
                    "Odd"  => Parity.Odd,
                    _      => Parity.None
                };

                var rs485ModeEnum = Rs485Mode switch
                {
                    "Rts" => DF1HalfDuplexTransport.Rs485ControlMode.Rts,
                    "Dtr" => DF1HalfDuplexTransport.Rs485ControlMode.Dtr,
                    _     => DF1HalfDuplexTransport.Rs485ControlMode.Auto
                };

                _df1 = new global::PCCCComm.PCCCComm(SelectedPort, SelectedBaud, parity)
                {
                    TargetNode = (int)TargetNode,
                    MyNode     = (int)MyNode,
                    CheckSum   = SelectedChecksum == "Crc" ? CheckSumOptions.Crc : CheckSumOptions.Bcc,
                    Protocol   = "DF1Master",
                    SlaveAddress = (int)TargetNode,
                    Rs485Mode = rs485ModeEnum,
                    EchoSuppression = EchoSuppression,
                    Rs485AssertDelayMs = RtsAssertDelay,
                    Rs485DeassertDelayMs = RtsDeassertDelay
                };
            }
            else // EIP
            {
                _df1 = new global::PCCCComm.PCCCComm(EipHost, EipPort, EipTimeout)
                {
                    TargetNode = (int)TargetNode,
                    MyNode = (int)MyNode
                };
            }

            await Task.Run(() => _df1.OpenComms());
            AppendLog("Port opened.");

            var plcInfo = await PlcIdentifier.IdentifyAsync(_df1);
            CurrentPlcInfo = plcInfo;
            AppendLog($"Identified: {plcInfo.Name} (0x{plcInfo.ProcessorType:X2}) " +
                      $"Upload/Download={plcInfo.SupportsUploadDownload}");

            string transportName = TransportType switch
            {
                TransportType.Df1FullDuplex => "DF1 Full-Duplex",
                TransportType.Df1HalfDuplex => "DF1 Half-Duplex",
                _ => "EtherNet/IP"
            };

            if (plcInfo.SupportsUploadDownload)
            {
                string modeStr = plcInfo.ModeStr;
                StatusText = $"{transportName} Connected | {plcInfo.Name} (0x{plcInfo.ProcessorType:X2}) | {modeStr}";
                AppendLog($"Mode: {modeStr}");
            }
            else
            {
                StatusText = $"{transportName} Connected | {plcInfo.Name} (upload/download not supported)";
            }

            IsConnected = true;

            _df1.RawFrameSent     += OnRawFrameSent;
            _df1.RawFrameReceived += OnRawFrameReceived;
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            DisposeDF1();
            StatusText = "Connection failed";
            await _dialogService.ShowMessageAsync("Connection Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Disconnect()
    {
        AppendLog("Disconnecting…");
        DisposeDF1();
        CurrentPlcInfo = new PlcInfo(0, "Unknown", false, "Unknown", string.Empty, 0, 0, "UNKNOWN");
        IsConnected     = false;
        StatusText      = "Disconnected";
        AppendLog("Disconnected.");
    }

    /// <summary>
    /// Refresh PLC status (mode and processor info) after upload/download.
    /// Updates StatusText and optionally _currentPlcInfo if processor type changed.
    /// </summary>
    private async Task RefreshPlcStatusAsync()
    {
        if (_df1 == null) return;
        try
        {
            byte[]? data = await Task.Run(() => _df1.GetDiagnosticStatusRaw());
            if (data != null && data.Length > 18)
            {
                byte modeByte = data[18];
                string modeStr = PlcIdentifier.DecodeModeString(modeByte);
                CurrentPlcInfo = CurrentPlcInfo with { ModeStr = modeStr };
                string transportName = TransportType switch
                {
                    TransportType.Df1FullDuplex => "DF1 Full-Duplex",
                    TransportType.Df1HalfDuplex => "DF1 Half-Duplex",
                    _ => "EtherNet/IP"
                };
                StatusText = $"{transportName} Connected | {_currentPlcInfo.Name} (0x{_currentPlcInfo.ProcessorType:X2}) | {modeStr}";
            }
            else
            {
                AppendLog("Failed to refresh PLC status: invalid diagnostic data");
            }
        }
        catch (Exception ex)
        {
            // Log error but don't disrupt UI
            AppendLog($"Failed to refresh PLC status: {ex.Message}");
        }
    }

    // ─── Upload ──────────────────────────────────────────────────────────────
    private async Task UploadAsync()
    {
        if (_df1 == null) return;

        string defaultFileName = _currentPlcInfo.GetDefaultFileName(_currentPlcInfo.ModeStr);
        string? filePath = await _dialogService.SaveFilePickerAsync("Save PLC Program", defaultFileName);
        if (filePath == null) return;

        await RunTransferAsync(async (progressMsg, progressPct, ct) =>
        {
            var svc = new ProgramTransferService(_df1!, progressMsg, progressPct, ct, _currentPlcInfo);
            await svc.UploadToFileAsync(filePath);
        }, "Upload");
    }

    // ─── Download ────────────────────────────────────────────────────────────
    private async Task DownloadAsync()
    {
        if (_df1 == null) return;

        string? filePath = await _dialogService.OpenFilePickerAsync("Select PLC Program File");
        if (filePath == null) return;

        bool confirmed = await _dialogService.ShowConfirmAsync(
            "Confirm Download",
            "This will overwrite the PLC program with the file contents.\n\n" +
            "The PLC will be switched to PROGRAM mode during download.\n\n" +
            "Continue?");
        if (!confirmed) return;

        // Set to PROGRAM mode NOW and refresh UI
        AppendLog("Setting PLC to Program mode…");
        await Task.Run(() => _df1.SetProgramMode());
        AppendLog("PLC switched to PROGRAM mode.");
        await RefreshPlcStatusAsync();  // StatusText now shows PROGRAM
        await FlushLogsAsync();

        int targetProc = _currentPlcInfo.ProcessorType;
        string targetBulletin = _currentPlcInfo.Bulletin;

        await RunTransferAsync(async (progressMsg, progressPct, ct) =>
        {
            var svc = new ProgramTransferService(_df1!, progressMsg, progressPct, ct, _currentPlcInfo);
            await svc.DownloadFromFileAsync(filePath, targetProc, targetBulletin, skipSetProgramMode: true);
        }, "Download");
    }

    // ─── Compare ─────────────────────────────────────────────────────────────
    private async Task CompareAsync()
    {
        if (_df1 == null) return;

        string? filePath = await _dialogService.OpenFilePickerAsync("Select backup file to compare");
        if (filePath == null) return;

        await RunTransferAsync(async (progressMsg, progressPct, ct) =>
        {
            var svc = new ProgramTransferService(_df1!, progressMsg, progressPct, ct, _currentPlcInfo);
            var results = await svc.CompareFullAsync(filePath);
            await _dialogService.ShowCompareResultsAsync(results);
        }, "Compare");
    }

    // ─── Shared transfer runner ───────────────────────────────────────────────
    private async Task RunTransferAsync(
        Func<IProgress<string>, IProgress<double>, CancellationToken, Task> work,
        string operationName)
    {
        // Clear log before starting new transfer to avoid UI lag from accumulated lines
        ClearLog();

        IsBusy          = true;
        ProgressValue   = 0;
        ProgressMessage = string.Empty;
        AppendLog($"--- {operationName} started ---");

        var cts = new CancellationTokenSource();
        var prevCts = Interlocked.Exchange(ref _cts, cts);
        prevCts?.Cancel();
        prevCts?.Dispose();

        var progressMsg = new Progress<string>(msg =>
        {
            ProgressMessage = msg;
            AppendLog(msg);
        });
        var progressPct = new Progress<double>(pct => ProgressValue = pct);

        try
        {
            await work(progressMsg, progressPct, _cts.Token);
            AppendLog($"--- {operationName} complete ---");
            await RefreshPlcStatusAsync();
            await FlushLogsAsync();  // Ensure all logs are rendered
            
            if (operationName == "Download")
            {
                await FlushLogsAsync();  // Extra flush before confirmation dialog
                bool switchToRun = await _dialogService.ShowConfirmAsync(
                    "Download Complete",
                    "Download finished successfully.\n\nSwitch to RUN mode now?");
                
                if (switchToRun)
                {
                    try
                    {
                        await Task.Run(() => _df1!.SetRunMode());
                        AppendLog("Switched to RUN mode.");
                        await RefreshPlcStatusAsync();
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Failed to switch to RUN mode: {ex.Message}");
                    }
                }
                else
                {
                    AppendLog("PLC remains in PROGRAM mode.");
                }
            }
            
            await FlushLogsAsync();  // Flush before success dialog
            await _dialogService.ShowMessageAsync($"{operationName} Complete",
                $"{operationName} finished successfully.");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"--- {operationName} cancelled ---");
            await FlushLogsAsync();
            await _dialogService.ShowMessageAsync("Cancelled", $"{operationName} was cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            await FlushLogsAsync();
            await _dialogService.ShowMessageAsync($"{operationName} Error", ex.Message);
        }
        finally
        {
            IsBusy          = false;
            ProgressValue   = 0;
            ProgressMessage = string.Empty;
            var doneCts = Interlocked.Exchange(ref _cts, null);
            doneCts?.Dispose();
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────
    private void DisposeDF1()
    {
        if (_df1 == null) return;
        _df1.RawFrameSent     -= OnRawFrameSent;
        _df1.RawFrameReceived -= OnRawFrameReceived;
        try { _df1.CloseComms(); } catch { /* ignore */ }
        _df1.Dispose();
        _df1 = null;
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        DisposeDF1();
    }
}
