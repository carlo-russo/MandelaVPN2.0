using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace MandelaVpnGuard;

public partial class App : System.Windows.Application
{
    private const string VpnName = NativeRas.DefaultVpnName;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _wakeEvent;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _startupMenuItem;
    private CancellationTokenSource? _cts;
    private bool _promptOpen;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _singleInstanceMutex = new Mutex(true, @"Global\MandelaVpnGuard_Mutex", out bool createdNew);
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\MandelaVpnGuard_Wake");

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

            NativeRas.EnsureProfileExists();
            LeakGuard.Enable(VpnName);
            SetupTrayIcon();

            _cts = new CancellationTokenSource();
            StartWakeListener(_cts.Token);
            StartWatchdogLoop(_cts.Token);
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
        openItem.Click += (_, _) => ShowPrompt(isTrip: false, stamp: "", autoDial: false);

        var disconnectItem = new Forms.ToolStripMenuItem("Disconnect & Close Port");
        disconnectItem.Click += (_, _) =>
        {
            NativeRas.HangUpAndClosePort(VpnName);
            NativeRas.FlushDnsCache();
        };

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

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = $"{VpnName} Guard • Active",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowPrompt(isTrip: false, stamp: "", autoDial: false);
    }

    private void StartWakeListener(CancellationToken token)
    {
        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_wakeEvent?.WaitOne(500) == true && !token.IsCancellationRequested)
                    Dispatcher.Invoke(() => ShowPrompt(isTrip: false, stamp: "", autoDial: false));
            }
        }, token);
    }

    private void StartWatchdogLoop(CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                bool wasConnected = NativeRas.IsVpnConnected(VpnName);
                if (!wasConnected)
                {
                    bool autoDial = NativeRas.HasSavedCredentials(VpnName);
                    await Dispatcher.InvokeAsync(() => ShowPrompt(isTrip: false, stamp: "", autoDial: autoDial));
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => UpdateTrayStatus(connected: true));
                }

                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
                while (await timer.WaitForNextTickAsync(token))
                {
                    bool connected = NativeRas.IsVpnConnected(VpnName);
                    if (wasConnected && !connected)
                    {
                        string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                        NativeRas.FlushDnsCache();
                        NativeRas.HangUpAndClosePort(VpnName);

                        await Dispatcher.InvokeAsync(() =>
                        {
                            UpdateTrayStatus(connected: false);
                            _trayIcon?.ShowBalloonTip(
                                3000,
                                $"{VpnName} Tripped!",
                                $"Disconnected at {stamp}. Reconnect prompt opened.",
                                Forms.ToolTipIcon.Error);
                            ShowPrompt(isTrip: true, stamp: stamp, autoDial: false);
                        });
                    }
                    else
                    {
                        await Dispatcher.InvokeAsync(() => UpdateTrayStatus(connected));
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

    private void ShowPrompt(bool isTrip, string stamp, bool autoDial)
    {
        if (_promptOpen)
            return;
        _promptOpen = true;

        try
        {
            if (_startupMenuItem != null)
                _startupMenuItem.Checked = StartupManager.IsEnabled();

            var window = new LoginWindow(VpnName, isTrip, stamp, autoDial);
            bool? connected = window.ShowDialog();

            if (window.UserRequestedExit)
            {
                ExitCleanly();
                return;
            }

            if (connected == true || NativeRas.IsVpnConnected(VpnName))
            {
                NativeRas.FlushDnsCache();
                UpdateTrayStatus(connected: true);
                _trayIcon?.ShowBalloonTip(
                    2500,
                    $"{VpnName} Connected",
                    "Tunnel active and leak guard armed.",
                    Forms.ToolTipIcon.Info);
            }
        }
        finally
        {
            _promptOpen = false;
        }
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
        _cts?.Cancel();
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

        if (_singleInstanceMutex != null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        _wakeEvent?.Dispose();
        _wakeEvent = null;
        _cts?.Dispose();
        _cts = null;

        if (failures.Count > 0)
            MessageBox.Show(string.Join(Environment.NewLine, failures), "Cleanup incomplete", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown();
    }
}
