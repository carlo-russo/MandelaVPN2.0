using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace MandelaVpnGuard;

public static class LeakGuard
{
    private const string DnsPolicyPath = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
    private const string DnsValueName = "DisableSmartNameResolution";
    private const string StatePath = @"SOFTWARE\MandelaVpnGuard\LeakGuard";
    private const string StateValueName = "Snapshot";

    private sealed record AdapterBindingState(string Name, bool Enabled);
    private sealed record GuardSnapshot(bool DnsValueExisted, int DnsValue, List<AdapterBindingState> Adapters);

    public static void Enable(string vpnName)
    {
        _ = vpnName;
        GuardSnapshot snapshot = LoadSnapshot() ?? CaptureSnapshot();
        SaveSnapshot(snapshot);

        using (var key = Registry.LocalMachine.CreateSubKey(DnsPolicyPath)
            ?? throw new InvalidOperationException("Could not open the Windows DNS policy registry key."))
        {
            key.SetValue(DnsValueName, 1, RegistryValueKind.DWord);
        }

        foreach (AdapterBindingState adapter in snapshot.Adapters.Where(adapter => adapter.Enabled))
            SetIpv6Binding(adapter.Name, enabled: false);

        NativeRas.FlushDnsCache();
    }

    public static void Restore(string vpnName)
    {
        _ = vpnName;
        GuardSnapshot? snapshot = LoadSnapshot();
        if (snapshot is null)
            return;

        var failures = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DnsPolicyPath, writable: true);
            if (snapshot.DnsValueExisted)
            {
                if (key is null)
                    throw new InvalidOperationException("Could not open the Windows DNS policy registry key to restore it.");
                key.SetValue(DnsValueName, snapshot.DnsValue, RegistryValueKind.DWord);
            }
            else
            {
                key?.DeleteValue(DnsValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"DNS policy: {ex.Message}");
        }

        foreach (AdapterBindingState adapter in snapshot.Adapters)
        {
            try
            {
                SetIpv6Binding(adapter.Name, adapter.Enabled);
            }
            catch (Exception ex)
            {
                failures.Add($"{adapter.Name}: {ex.Message}");
            }
        }

        try
        {
            NativeRas.FlushDnsCache();
        }
        catch (Exception ex)
        {
            failures.Add($"DNS cache: {ex.Message}");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("Leak-guard settings could not be fully restored: " + string.Join("; ", failures));

        using var stateKey = Registry.LocalMachine.OpenSubKey(StatePath, writable: true)
            ?? throw new InvalidOperationException("Could not remove the leak-guard recovery state after restoring settings.");
        stateKey.DeleteValue(StateValueName, throwOnMissingValue: false);
    }

    private static GuardSnapshot CaptureSnapshot()
    {
        bool valueExisted = false;
        int dnsValue = 0;
        using (var dnsKey = Registry.LocalMachine.OpenSubKey(DnsPolicyPath))
        {
            if (dnsKey?.GetValue(DnsValueName, null) is object existingValue)
            {
                if (dnsKey.GetValueKind(DnsValueName) != RegistryValueKind.DWord)
                    throw new InvalidOperationException($"The existing {DnsValueName} policy is not a DWORD and cannot be safely preserved.");
                valueExisted = true;
                dnsValue = Convert.ToInt32(existingValue);
            }
        }

        const string script =
            "$ErrorActionPreference = 'Stop'; " +
            "$result = @(Get-NetAdapter -Physical | ForEach-Object { " +
            "$adapter = $_; Get-NetAdapterBinding -Name $adapter.Name -ComponentID ms_tcpip6 " +
            "| Select-Object @{Name='Name';Expression={$adapter.Name}}, Enabled }); " +
            "ConvertTo-Json -InputObject $result -Compress";
        string json = RunPowerShell(script);
        var adapters = JsonSerializer.Deserialize<List<AdapterBindingState>>(json)
            ?? throw new InvalidOperationException("Windows returned an invalid physical-adapter binding list.");
        return new GuardSnapshot(valueExisted, dnsValue, adapters);
    }

    private static GuardSnapshot? LoadSnapshot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(StatePath);
        string? json = key?.GetValue(StateValueName) as string;
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<GuardSnapshot>(json);
    }

    private static void SaveSnapshot(GuardSnapshot snapshot)
    {
        using var key = Registry.LocalMachine.CreateSubKey(StatePath)
            ?? throw new InvalidOperationException("Could not save the leak-guard recovery state.");
        key.SetValue(StateValueName, JsonSerializer.Serialize(snapshot), RegistryValueKind.String);
    }

    private static void SetIpv6Binding(string adapterName, bool enabled)
    {
        string safeName = adapterName.Replace("'", "''", StringComparison.Ordinal);
        string cmdlet = enabled ? "Enable-NetAdapterBinding" : "Disable-NetAdapterBinding";
        string script =
            "$ErrorActionPreference = 'Stop'; " +
            $"if (Get-NetAdapter -Name '{safeName}' -ErrorAction SilentlyContinue) {{ " +
            $"{cmdlet} -Name '{safeName}' -ComponentID ms_tcpip6 -Confirm:$false -ErrorAction Stop }}";
        RunPowerShell(script);
    }

    private static string RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start PowerShell for network configuration.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(true);
            throw new TimeoutException("Timed out while applying network configuration.");
        }

        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(stderr.Result.Trim());
        return stdout.Result.Trim();
    }
}

public static class StartupManager
{
    private const string TaskName = "MandelaVpnGuard";

    public static bool IsEnabled()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Query /TN \"{TaskName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not query the Windows startup task.");
        if (!process.WaitForExit(5000))
        {
            process.Kill(true);
            throw new TimeoutException("Timed out while querying the Windows startup task.");
        }
        return process.ExitCode == 0;
    }

    public static void SetEnabled(bool enable)
    {
        if (enable)
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the application path.");
            RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        }
        else
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }
    }

    private static void RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the Windows Task Scheduler command.");
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            throw new TimeoutException("Timed out while updating the Windows startup task.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Trim());
    }
}
