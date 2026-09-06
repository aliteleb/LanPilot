using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using LanPilot.Contracts;

namespace LanPilot.Service.Engine;

public sealed class ApplicationTrafficController(
    ApplicationDownloadLimiter downloadLimiter,
    ApplicationTrafficMonitor trafficMonitor,
    ILogger<ApplicationTrafficController> logger) : IApplicationPolicyController
{
    public Task<IReadOnlyList<LocalApplicationSnapshot>> DiscoverAsync(
        IReadOnlyDictionary<string, LocalApplicationPolicy> policies,
        CancellationToken cancellationToken) =>
        Task.Run(() => Discover(policies, trafficMonitor.CurrentRates, cancellationToken), cancellationToken);

    public async Task ApplyAsync(LocalApplicationPolicy policy, CancellationToken cancellationToken)
    {
        Validate(policy);
        EnsureAdministrator();
        string ruleBase = $"LanPilot App {policy.Id}";
        string qosName = $"LanPilot-App-{policy.Id}";
        string path = EscapePowerShellLiteral(policy.ExecutablePath);
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -eq '{{ruleBase}} Out' } | Remove-NetFirewallRule -ErrorAction Stop
            Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -eq '{{ruleBase}} In' } | Remove-NetFirewallRule -ErrorAction Stop
            Get-NetQosPolicy -PolicyStore localhost -ErrorAction Stop | Where-Object { $_.Name -eq '{{qosName}}' } | Remove-NetQosPolicy -Confirm:$false -ErrorAction Stop
            try {
                {{(policy.BlockInternet ? $"New-NetFirewallRule -DisplayName '{ruleBase} Out' -Direction Outbound -Program '{path}' -Action Block -Profile Any | Out-Null\n    New-NetFirewallRule -DisplayName '{ruleBase} In' -Direction Inbound -Program '{path}' -Action Block -Profile Any | Out-Null" : string.Empty)}}
                {{(policy.UploadLimitBitsPerSecond is long limit ? $"New-NetQosPolicy -Name '{qosName}' -AppPathNameMatchCondition '{path}' -ThrottleRateActionBitsPerSecond {limit} -PolicyStore localhost | Out-Null" : string.Empty)}}
            }
            catch {
                Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -eq '{{ruleBase}} Out' } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
                Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -eq '{{ruleBase}} In' } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
                Get-NetQosPolicy -PolicyStore localhost -ErrorAction Stop | Where-Object { $_.Name -eq '{{qosName}}' } | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue
                Write-Error -ErrorRecord $_
                exit 1
            }
            exit 0
            """;

        await RunPowerShellAsync(script, cancellationToken);
        await VerifyWindowsPolicyAsync(policy, cancellationToken);
        try
        {
            await downloadLimiter.UpsertAsync(policy, cancellationToken);
        }
        catch
        {
            await RemoveWindowsPolicyAsync(policy, CancellationToken.None);
            throw;
        }
    }

    public async Task RemoveAsync(LocalApplicationPolicy policy, CancellationToken cancellationToken)
    {
        EnsureAdministrator();
        await RemoveWindowsPolicyAsync(policy, cancellationToken);
        await downloadLimiter.RemoveAsync(policy.Id, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        downloadLimiter.StopAsync(cancellationToken);

    public async Task SuspendAllAsync(CancellationToken cancellationToken)
    {
        Exception? limiterError = null;
        try { await downloadLimiter.StopAsync(cancellationToken); }
        catch (Exception ex) { limiterError = ex; }
        await RunPowerShellAsync("""
            $ErrorActionPreference = 'Stop'
            $failures = @()
            try {
                Get-NetFirewallRule -ErrorAction Stop |
                    Where-Object { $_.DisplayName -match '^LanPilot App [a-fA-F0-9]{24} (In|Out)$' } |
                    Remove-NetFirewallRule -ErrorAction Stop
                $remaining = @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop |
                    Where-Object { $_.DisplayName -match '^LanPilot App [a-fA-F0-9]{24} (In|Out)$' })
                if ($remaining.Count) { throw 'LanPilot firewall rules remain active' }
            } catch { $failures += $_.Exception.Message }
            try {
                Get-NetQosPolicy -PolicyStore localhost -ErrorAction Stop |
                    Where-Object { $_.Name -match '^LanPilot-App-[a-fA-F0-9]{24}$' } |
                    Remove-NetQosPolicy -Confirm:$false -ErrorAction Stop
                $remaining = @(Get-NetQosPolicy -PolicyStore localhost -ErrorAction Stop |
                    Where-Object { $_.Name -match '^LanPilot-App-[a-fA-F0-9]{24}$' })
                if ($remaining.Count) { throw 'LanPilot QoS policies remain active' }
            } catch { $failures += $_.Exception.Message }
            if ($failures.Count) { throw ($failures -join '; ') }
            """, cancellationToken);
        if (limiterError is not null) throw new InvalidOperationException("Application limiter cleanup incomplete.", limiterError);
    }

    private async Task RemoveWindowsPolicyAsync(LocalApplicationPolicy policy, CancellationToken cancellationToken)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(policy.Id, "\\A[a-fA-F0-9]{24}\\z"))
            throw new InvalidDataException("The application policy identity is invalid.");
        string ruleBase = $"LanPilot App {policy.Id}";
        string qosName = $"LanPilot-App-{policy.Id}";
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -eq '{{ruleBase}} Out' } | Remove-NetFirewallRule -ErrorAction Stop
                Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -eq '{{ruleBase}} In' } | Remove-NetFirewallRule -ErrorAction Stop
                Get-NetQosPolicy -PolicyStore localhost -ErrorAction Stop | Where-Object { $_.Name -eq '{{qosName}}' } | Remove-NetQosPolicy -Confirm:$false -ErrorAction Stop
            }
            catch {
                Write-Error -ErrorRecord $_
                exit 1
            }
            exit 0
            """;
        await RunPowerShellAsync(script, cancellationToken);
        await VerifyWindowsPolicyAsync(policy with { BlockInternet = false, UploadLimitBitsPerSecond = null }, cancellationToken);
    }

    private Task VerifyWindowsPolicyAsync(LocalApplicationPolicy policy, CancellationToken token)
    {
        string path = EscapePowerShellLiteral(policy.ExecutablePath);
        return RunPowerShellAsync($$"""
            $ErrorActionPreference = 'Stop'
            $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { $_.DisplayName -eq 'LanPilot App {{policy.Id}} Out' -or $_.DisplayName -eq 'LanPilot App {{policy.Id}} In' })
            if ($rules.Count -ne {{(policy.BlockInternet ? 2 : 0)}}) { throw 'Firewall policy verification failed' }
            foreach ($rule in $rules) {
                if ($rule.Action -ne 'Block' -or $rule.Enabled -ne 'True') { throw 'Firewall policy is not enabled' }
                $application = $rule | Get-NetFirewallApplicationFilter -ErrorAction Stop
                if ($application.Program -ne '{{path}}') { throw 'Firewall executable mismatch' }
            }
            $qos = @(Get-NetQosPolicy -PolicyStore localhost -ErrorAction Stop | Where-Object { $_.Name -eq 'LanPilot-App-{{policy.Id}}' })
            if ($qos.Count -ne {{(policy.UploadLimitBitsPerSecond is null ? 0 : 1)}}) { throw 'QoS policy verification failed' }
            {{(policy.UploadLimitBitsPerSecond is long rate ? $"if ($qos[0].ThrottleRateAction -ne {rate}) {{ throw 'QoS rate verification failed' }}" : string.Empty)}}
            """, token);
    }

    public static string CreateId(string executablePath)
    {
        string normalized = Path.GetFullPath(executablePath).Trim().ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24].ToLowerInvariant();
    }

    public static void Validate(LocalApplicationPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.DisplayName) || policy.DisplayName.Length > 120)
            throw new InvalidDataException("An application name between 1 and 120 characters is required.");
        if (string.IsNullOrWhiteSpace(policy.ExecutablePath) ||
            !Path.IsPathFullyQualified(policy.ExecutablePath) ||
            !string.Equals(Path.GetExtension(policy.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(policy.ExecutablePath))
            throw new InvalidDataException("The application executable is unavailable.");
        if (!string.Equals(policy.Id, CreateId(policy.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The application identity is invalid.");
        if (IsSystemExecutable(policy.ExecutablePath))
            throw new InvalidDataException("Windows system applications cannot be controlled by LanPilot.");
        if (policy.UploadLimitBitsPerSecond is <= 0)
            throw new InvalidDataException("The upload limit must be positive or Unlimited.");
        if (policy.DownloadLimitBitsPerSecond is <= 0)
            throw new InvalidDataException("The download limit must be positive or Unlimited.");
    }

    private static IReadOnlyList<LocalApplicationSnapshot> Discover(
        IReadOnlyDictionary<string, LocalApplicationPolicy> policies,
        IReadOnlyDictionary<string, ApplicationTrafficRate> rates,
        CancellationToken cancellationToken)
    {
        Dictionary<string, (string Name, string Path, List<int> Pids)> running = new(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.Id == Environment.ProcessId ||
                        process.SessionId == 0) continue;
                    string? path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) ||
                        !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        IsSystemExecutable(path)) continue;
                    string id = CreateId(path);
                    string name = process.MainModule?.FileVersionInfo.FileDescription ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(path);
                    if (!running.TryGetValue(id, out var app)) app = (name, path, []);
                    app.Pids.Add(process.Id);
                    running[id] = app;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Protected system processes are intentionally skipped.
                }
            }
        }

        foreach (LocalApplicationPolicy policy in policies.Values)
        {
            if (!IsSystemExecutable(policy.ExecutablePath) && !running.ContainsKey(policy.Id))
                running[policy.Id] = (policy.DisplayName, policy.ExecutablePath, []);
        }

        return running.Select(item => new LocalApplicationSnapshot(
                item.Key,
                item.Value.Name,
                item.Value.Path,
                item.Value.Pids.Order().ToArray(),
                item.Value.Pids.Count > 0,
                policies.GetValueOrDefault(item.Key),
                rates.GetValueOrDefault(item.Key).DownloadBitsPerSecond,
                rates.GetValueOrDefault(item.Key).UploadBitsPerSecond))
            .OrderByDescending(item => item.IsRunning)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSystemExecutable(string executablePath)
    {
        string fileName = Path.GetFileName(executablePath);
        if (fileName.Equals("LanPilot.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("LanPilot.Service.exe", StringComparison.OrdinalIgnoreCase))
            return true;

        string windowsRoot = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(executablePath);
        return fullPath.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        string executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows network policy could not be started.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Task<string> output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            Task<string> error = ReadBoundedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0)
            {
                logger.LogWarning("Application network policy failed: {Error}", error.Result);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error.Result)
                    ? "Windows rejected the application network policy." : error.Result.Trim());
            }
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        StringBuilder text = new();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (text.Length + count > 65536) throw new InvalidDataException("Policy command output exceeded the safety limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static void EnsureAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "LanPilot Service must be running with administrator privileges to control application traffic.");
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
