using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace MandelaVpnGuard;

public partial class App : System.Windows.Application
{
    private const string VpnName = NativeRas.DefaultVpnName;
    private const string MutexName = @"Global\MandelaVpnGuard_Mutex";
    private const string WakeEventName = @"Global\MandelaVpnGuard_Wake";
    private const string ShutdownEventName = @"Global\MandelaVpnGuard_Shutdown";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _wakeEvent;
    private EventWaitHandle? _shutdownEvent;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _startupMenuItem;
    private Icon? _applicationIcon;
    private CancellationTokenSource? _cts;
    private LoginWindow? _mainWindow;
    private bool _isExiting;
    private int _intentionalDisconnect;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            if (e.Args.Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!RequestShutdownAndWait())
                        Environment.ExitCode = 1;
                }
                catch (Exception ex)
                {
                    Environment.ExitCode = 1;
                    MessageBox.Show(ex.Message, "Could not close Mandela VPN Guard", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                Shutdown();
                return;
            }

            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);

            if (!createdNew)
            {
                _wakeEvent.Set();
                _wakeEvent.Dispose();
                _wakeEvent = null;
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown();
                return;
            }

            _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
            NativeRas.EnsureProfileExists();
            LeakGuard.Enable(VpnName);
            SetupTrayIcon();

            bool connectedAtStart = NativeRas.IsVpnConnected(VpnName);
            bool hasSavedCredentials = NativeRas.HasSavedCredentials(VpnName);
            _mainWindow = new LoginWindow(
                VpnName,
                initiallyConnected: connectedAtStart,
                autoDialOnOpen: !connectedAtStart && hasSavedCredentials,
                showCredentials: !connectedAtStart && !hasSavedCredentials);
            MainWindow = _mainWindow;
            _mainWindow.Closed += (_, _) => ExitCleanly();
            _mainWindow.DisconnectRequested += (_, _) => DisconnectByUser();
            _mainWindow.Show();

            _cts = new CancellationTokenSource();
            StartWakeListener(_cts.Token);
            StartShutdownListener(_cts.Token);
            StartWatchdogLoop(_cts.Token, connectedAtStart);
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            try
            {
                NativeRas.HangUpAndClosePort(VpnName);
            }
            catch (Exception cleanupError)
            {
                message += Environment.NewLine + Environment.NewLine +
                           $"Closing the VPN port also failed: {cleanupError.Message}";
            }
            try
            {
                LeakGuard.Restore(VpnName);
            }
            catch (Exception cleanupError)
            {
                message += Environment.NewLine + Environment.NewLine +
                           $"Restoring network settings also failed: {cleanupError.Message}";
            }

            MessageBox.Show(message, "Mandela VPN Guard could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void SetupTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();

        var openItem = new Forms.ToolStripMenuItem("Open / Reconnect Tunnel");
        openItem.Click += (_, _) => ShowMainWindow();

        var disconnectItem = new Forms.ToolStripMenuItem("Disconnect & Close Port");
        disconnectItem.Click += (_, _) => DisconnectByUser();

        _startupMenuItem = new Forms.ToolStripMenuItem("Run at Windows Startup")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true
        };
        _startupMenuItem.Click += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(_startupMenuItem.Checked);
            }
            catch (Exception ex)
            {
                _startupMenuItem.Checked = !_startupMenuItem.Checked;
                MessageBox.Show(ex.Message, "Startup setting failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        var exitItem = new Forms.ToolStripMenuItem("Stop Watchdog & Close Port");
        exitItem.Click += (_, _) => ExitCleanly();

        menu.Items.Add(openItem);
        menu.Items.Add(disconnectItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the application path for its icon.");
        _applicationIcon = Icon.ExtractAssociatedIcon(executablePath)
            ?? throw new InvalidOperationException("Could not load the application icon.");
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = $"{VpnName} Guard • Active",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void StartWakeListener(CancellationToken token)
    {
        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_wakeEvent?.WaitOne(500) == true && !token.IsCancellationRequested)
                    Dispatcher.Invoke(ShowMainWindow);
            }
        }, token);
    }

    private void StartShutdownListener(CancellationToken token)
    {
        _ = Task.Run(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_shutdownEvent?.WaitOne(500) == true)
                    {
                        Dispatcher.Invoke(ExitCleanly);
                        return;
                    }
                }
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
            }
        }, token);
    }

    private static bool RequestShutdownAndWait()
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (createdNew)
        {
            try
            {
                LeakGuard.Restore(VpnName);
                return true;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        using var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        shutdownEvent.Set();
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(20)))
                return false;
            mutex.ReleaseMutex();
            return true;
        }
        catch (AbandonedMutexException)
        {
            LeakGuard.Restore(VpnName);
            mutex.ReleaseMutex();
            return true;
        }
    }

    private void StartWatchdogLoop(CancellationToken token, bool initiallyConnected)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                bool wasConnected = initiallyConnected;
                await Dispatcher.InvokeAsync(() =>
                {
                    _mainWindow?.UpdateWatchdogStatus(wasConnected);
                    UpdateTrayStatus(wasConnected);
                });

                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
                while (await timer.WaitForNextTickAsync(token))
                {
                    bool connected = NativeRas.IsVpnConnected(VpnName);
                    if (wasConnected && !connected)
                    {
                        bool intentionalDisconnect = Interlocked.Exchange(ref _intentionalDisconnect, 0) == 1;
                        string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                        bool autoReconnect = false;
                        if (!intentionalDisconnect)
                        {
                            NativeRas.FlushDnsCache();
                            NativeRas.HangUpAndClosePort(VpnName);
                            autoReconnect = NativeRas.HasSavedCredentials(VpnName);
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (intentionalDisconnect)
                                _mainWindow?.SetManuallyDisconnected();
                            else
                                _mainWindow?.UpdateWatchdogStatus(connected: false, isTrip: true);
                            UpdateTrayStatus(connected: false);
                            if (!intentionalDisconnect)
                            {
                                _trayIcon?.ShowBalloonTip(
                                    3000,
                                    $"{VpnName} Disconnected",
                                    $"Connection lost at {stamp}.",
                                    Forms.ToolTipIcon.Error);
                            }
                            if (autoReconnect)
                                _mainWindow?.BeginReconnect();
                        });
                    }
                    else if (connected != wasConnected)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _mainWindow?.UpdateWatchdogStatus(connected);
                            UpdateTrayStatus(connected);
                        });
                    }

                    wasConnected = connected;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(ex.Message, "VPN watchdog stopped", MessageBoxButton.OK, MessageBoxImage.Error);
                    ExitCleanly();
                });
            }
        }, token);
    }

    private void DisconnectByUser()
    {
        Interlocked.Exchange(ref _intentionalDisconnect, 1);
        try
        {
            NativeRas.HangUpAndClosePort(VpnName);
            NativeRas.FlushDnsCache();
            if (NativeRas.IsVpnConnected(VpnName))
            {
                Interlocked.Exchange(ref _intentionalDisconnect, 0);
                throw new InvalidOperationException("The VPN is still connected.");
            }

            _mainWindow?.SetManuallyDisconnected();
            UpdateTrayStatus(connected: false);
        }
        catch (Exception ex)
        {
            if (NativeRas.IsVpnConnected(VpnName))
                Interlocked.Exchange(ref _intentionalDisconnect, 0);
            MessageBox.Show(ex.Message, "Could not disconnect", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null)
            return;

        if (_startupMenuItem != null)
            _startupMenuItem.Checked = StartupManager.IsEnabled();

        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Activate();
    }

    private void UpdateTrayStatus(bool connected)
    {
        if (_trayIcon == null)
            return;
        _trayIcon.Text = connected
            ? $"{VpnName} • Connected (Protected)"
            : $"{VpnName} • Disconnected";
    }

    private void ExitCleanly()
    {
        if (_isExiting)
            return;
        _isExiting = true;

        _cts?.Cancel();
        _mainWindow?.AllowApplicationExit();
        var failures = new List<string>();
        try
        {
            NativeRas.HangUpAndClosePort(VpnName);
        }
        catch (Exception ex)
        {
            failures.Add($"VPN port: {ex.Message}");
        }
        try
        {
            LeakGuard.Restore(VpnName);
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _applicationIcon?.Dispose();
        _applicationIcon = null;

        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        _wakeEvent?.Dispose();
        _wakeEvent = null;
        _shutdownEvent?.Dispose();
        _shutdownEvent = null;
        _cts?.Dispose();
        _cts = null;

        if (failures.Count > 0)
            MessageBox.Show(string.Join(Environment.NewLine, failures), "Cleanup incomplete", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown();
    }
}
