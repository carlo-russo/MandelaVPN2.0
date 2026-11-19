using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MandelaVpnGuard;

public static class NativeRas
{
    public const string DefaultVpnName = "Mandela VPN";
    public const string DefaultServer = "vpn.nmmu.ac.za";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct RASDIALPARAMS
    {
        public int dwSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string szEntryName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string szPhoneNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string szCallbackNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string szUserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string szPassword;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string szDomain;
        public int dwSubEntry;
        public IntPtr dwCallbackId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct RASCONNW
    {
        public int dwSize;
        public IntPtr hrasconn;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
        public string szEntryName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
        public string szDeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string szDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPhonebook;
        public int dwSubEntry;
        public Guid guidEntry;
        public int dwFlags;
        public long luid;
        public Guid guidCorrelationId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct RASCONNSTATUSW
    {
        public int dwSize;
        public int rasconnstate;
        public int dwError;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
        public string szDeviceType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string szDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string szPhoneNumber;
    }

    public delegate void RasDialFunc(uint unMsg, int rasconnstate, uint dwError);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RasGetEntryDialParamsW(
        string? lpszPhonebook,
        ref RASDIALPARAMS lprasdialparams,
        [MarshalAs(UnmanagedType.Bool)] out bool lpfPassword);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RasSetEntryDialParamsW(
        string? lpszPhonebook,
        ref RASDIALPARAMS lprasdialparams,
        [MarshalAs(UnmanagedType.Bool)] bool fRemovePassword);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RasDialW(
        IntPtr lpRasDialExtensions,
        string? lpszPhonebook,
        ref RASDIALPARAMS lprasdialparams,
        uint dwNotifierType,
        RasDialFunc lpvNotifier,
        out IntPtr lphRasConn);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RasEnumConnectionsW(
        [In, Out] RASCONNW[] lprasconn,
        ref int lpcb,
        ref int lpcConnections);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RasGetConnectStatusW(
        IntPtr hrasconn,
        ref RASCONNSTATUSW lprasconnstatus);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RasHangUpW(IntPtr hRasConn);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RasGetErrorStringW(
        uint uErrorValue,
        StringBuilder lpszErrorString,
        int cBufSize);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RasValidateEntryNameW(string? lpszPhonebook, string lpszEntry);

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();

    private static IntPtr _hRasConn = IntPtr.Zero;
    private static RasDialFunc? _pinnedCallback;
    private static int _dialGeneration;

    public static volatile bool IsDialing;
    public static volatile bool DialSucceeded;
    public static volatile string StateText = "Idle";
    public static volatile string ErrorMessage = "";

    public static void EnsureProfileExists(string entryName = DefaultVpnName, string server = DefaultServer)
    {
        const uint ErrorAlreadyExists = 183;
        uint validation = RasValidateEntryNameW(null, entryName);
        if (validation == ErrorAlreadyExists)
            return;
        if (validation != 0)
            throw new InvalidOperationException(FormatRasError(validation));

        string safeEntryName = entryName.Replace("'", "''", StringComparison.Ordinal);
        string safeServer = server.Replace("'", "''", StringComparison.Ordinal);
        string script =
            $"try {{ Add-VpnConnection -Name '{safeEntryName}' -ServerAddress '{safeServer}' " +
            "-TunnelType Automatic -EncryptionLevel Optional -AuthenticationMethod MSChapv2 " +
            "-SplitTunneling $true -RememberCredential $true -Force -ErrorAction Stop; exit 0 } " +
            "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        RunPowerShell(script);
    }

    private static void RunPowerShell(string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start PowerShell to create the VPN profile.");
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000))
        {
            process.Kill(true);
            throw new TimeoutException("Timed out while creating the VPN profile.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Could not create the VPN profile: {error.Trim()}");
    }

    private static IntPtr FindActiveRasHandle(string entryName)
    {
        int itemSize = Marshal.SizeOf<RASCONNW>();
        var conns = new RASCONNW[16];
        for (int i = 0; i < conns.Length; i++)
            conns[i].dwSize = itemSize;

        int cb = conns.Length * itemSize;
        int count = 0;
        uint result = RasEnumConnectionsW(conns, ref cb, ref count);
        if (result != 0)
            return IntPtr.Zero;

        for (int i = 0; i < count; i++)
        {
            if (string.Equals(conns[i].szEntryName, entryName, StringComparison.OrdinalIgnoreCase))
                return conns[i].hrasconn;
        }
        return IntPtr.Zero;
    }

    public static bool IsVpnConnected(string entryName = DefaultVpnName)
    {
        IntPtr hConn = FindActiveRasHandle(entryName);
        if (hConn == IntPtr.Zero)
            hConn = _hRasConn;
        if (hConn == IntPtr.Zero)
            return false;

        var status = new RASCONNSTATUSW { dwSize = Marshal.SizeOf<RASCONNSTATUSW>() };
        if (RasGetConnectStatusW(hConn, ref status) == 0)
            return status.rasconnstate == 0x2000 && status.dwError == 0;
        return false;
    }

    public static string GetSavedUsername(string entryName = DefaultVpnName)
    {
        var parameters = NewDialParams(entryName);
        if (RasGetEntryDialParamsW(null, ref parameters, out _) == 0)
            return parameters.szUserName?.Trim() ?? "";
        return "";
    }

    public static bool HasSavedCredentials(string entryName = DefaultVpnName)
    {
        var parameters = NewDialParams(entryName);
        return RasGetEntryDialParamsW(null, ref parameters, out bool hasPassword) == 0
               && hasPassword
               && !string.IsNullOrWhiteSpace(parameters.szUserName);
    }

    private static RASDIALPARAMS NewDialParams(string entryName) => new()
    {
        dwSize = Marshal.SizeOf<RASDIALPARAMS>(),
        szEntryName = entryName,
        szPhoneNumber = "",
        szCallbackNumber = "",
        szUserName = "",
        szPassword = "",
        szDomain = ""
    };

    private static string FormatRasError(uint code)
    {
        if (code == 0)
            return "";
        var buffer = new StringBuilder(512);
        return RasGetErrorStringW(code, buffer, buffer.Capacity) == 0
            ? $"{buffer.ToString().Trim()} (Error {code})"
            : $"RAS Error {code}";
    }

    private static string MapRasState(int state) => state switch
    {
        0 => "Opening WAN Miniport...",
        1 => "Port opened...",
        2 => "Connecting to gateway...",
        3 => "Gateway connected...",
        4 => "All devices connected...",
        5 => "Authenticating credentials...",
        7 => "Retrying authentication...",
        12 => "Projecting network routes...",
        14 => "Authenticated...",
        0x2000 => "Connected",
        0x2001 => "Disconnected",
        _ => $"Negotiating tunnel (state {state})..."
    };

    public static void StartDialAsync(string entryName, string username, string password)
    {
        if (IsDialing)
            return;

        HangUpAndClosePort(entryName);
        int generation = Interlocked.Increment(ref _dialGeneration);
        IsDialing = true;
        DialSucceeded = false;
        ErrorMessage = "";
        StateText = "Loading entry parameters...";

        var dialThread = new Thread(() =>
        {
            try
            {
                var parameters = NewDialParams(entryName);
                uint readResult = RasGetEntryDialParamsW(null, ref parameters, out _);
                if (readResult != 0 && readResult != 623)
                    throw new InvalidOperationException(FormatRasError(readResult));

                if (!string.IsNullOrWhiteSpace(username))
                    parameters.szUserName = username.Trim();

                if (!string.IsNullOrEmpty(password))
                {
                    parameters.szPassword = password;
                    uint saveResult = RasSetEntryDialParamsW(null, ref parameters, false);
                    if (saveResult != 0)
                        throw new InvalidOperationException(FormatRasError(saveResult));
                }

                _pinnedCallback = (_, state, error) =>
                {
                    if (generation != Volatile.Read(ref _dialGeneration))
                        return;

                    StateText = MapRasState(state);
                    if (error != 0)
                    {
                        ErrorMessage = FormatRasError(error);
                        DialSucceeded = false;
                        IsDialing = false;
                    }
                    else if (state == 0x2000)
                    {
                        DialSucceeded = true;
                        IsDialing = false;
                    }
                    else if (state == 0x2001)
                    {
                        IsDialing = false;
                    }
                };

                uint result = RasDialW(IntPtr.Zero, null, ref parameters, 0, _pinnedCallback, out IntPtr hConn);
                _hRasConn = hConn;
                if (result != 0)
                    throw new InvalidOperationException(FormatRasError(result));
            }
            catch (Exception ex)
            {
                if (generation == Volatile.Read(ref _dialGeneration))
                {
                    ErrorMessage = ex.Message;
                    IsDialing = false;
                }
            }
        })
        { IsBackground = true, Name = "Mandela VPN RAS dial" };

        dialThread.Start();
    }

    public static void HangUpAndClosePort(string entryName = DefaultVpnName)
    {
        Interlocked.Increment(ref _dialGeneration);
        IntPtr tracked = Interlocked.Exchange(ref _hRasConn, IntPtr.Zero);
        if (tracked != IntPtr.Zero)
            RasHangUpW(tracked);

        IntPtr active = FindActiveRasHandle(entryName);
        if (active != IntPtr.Zero && active != tracked)
            RasHangUpW(active);

        for (int i = 0; i < 30; i++)
        {
            if (FindActiveRasHandle(entryName) == IntPtr.Zero)
                break;
            Thread.Sleep(50);
        }

        DialSucceeded = false;
        IsDialing = false;
        StateText = "Disconnected";
    }

    public static void FlushDnsCache()
    {
        if (!DnsFlushResolverCache())
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not flush the Windows DNS resolver cache.");
    }
}
