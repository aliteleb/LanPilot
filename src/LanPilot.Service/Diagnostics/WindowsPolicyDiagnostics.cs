using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LanPilot.Service.Diagnostics;

// Explicit export only; reads LanPilot-owned rules, never changes policy or
// probes the Internet. Timeout/output limits keep diagnostics best-effort.
public static class WindowsPolicyDiagnostics
{
    public static async Task<object> CaptureAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $report = @{}
            Add-Type -AssemblyName System.ServiceProcess
            $report.services = @('npcap','BFE','MpsSvc','WinDivert','WinDivert14' | ForEach-Object {
                $serviceName = $_
                $service = New-Object System.ServiceProcess.ServiceController($serviceName)
                try { @{ name = $serviceName; status = $service.Status.ToString() } }
                catch { @{ name = $serviceName; unavailable = $true; error = $_.Exception.GetType().FullName } }
                finally { $service.Dispose() }
            })
            try {
                $queryErrors = @()
                $report.firewallRules = @(Get-NetFirewallRule -DisplayName 'LanPilot App *' -ErrorAction SilentlyContinue -ErrorVariable queryErrors |
                    Where-Object { $_.DisplayName -match '^LanPilot App [a-fA-F0-9]+ (In|Out)$' } |
                    Select-Object -First 128 DisplayName,@{n='Enabled';e={$_.Enabled.ToString()}},@{n='Direction';e={$_.Direction.ToString()}},@{n='Action';e={$_.Action.ToString()}})
                $report.firewallQueryErrors = @($queryErrors | Select-Object -First 8 @{n='category';e={$_.CategoryInfo.Category.ToString()}},@{n='type';e={$_.Exception.GetType().FullName}})
            } catch { $report.firewallError = $_.Exception.GetType().FullName }
            try {
                $report.qosRules = @(Get-NetQosPolicy -PolicyStore ActiveStore |
                    Where-Object { $_.Name -match '^LanPilot-App-[a-fA-F0-9]+$' } |
                    Select-Object -First 128 Name,ThrottleRateActionBitsPerSecond)
            } catch { $report.qosError = $_.Exception.GetType().FullName }
            try {
                $report.firewallProfiles = @(Get-NetFirewallProfile |
                    Select-Object Name,@{n='Enabled';e={$_.Enabled.ToString()}},@{n='DefaultInboundAction';e={$_.DefaultInboundAction.ToString()}},@{n='DefaultOutboundAction';e={$_.DefaultOutboundAction.ToString()}})
            } catch { $report.profileError = $_.Exception.GetType().FullName }
            try {
                $report.defaultRoutes = @(Get-NetRoute -DestinationPrefix '0.0.0.0/0','::/0' -ErrorAction SilentlyContinue |
                    Select-Object -First 32 InterfaceIndex,NextHop,RouteMetric)
                $report.gatewayNeighbors = @($report.defaultRoutes | ForEach-Object {
                    Get-NetNeighbor -InterfaceIndex $_.InterfaceIndex -IPAddress $_.NextHop -ErrorAction SilentlyContinue |
                        Select-Object -First 1 InterfaceIndex,IPAddress,LinkLayerAddress,@{n='State';e={$_.State.ToString()}}
                })
            } catch { $report.routeError = $_.Exception.GetType().FullName }
            $report | ConvertTo-Json -Depth 5 -Compress
            """;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            Task<string> output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            Task<string> errors = ReadBoundedAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) return new { unavailable = true, exitCode = process.ExitCode };
            using JsonDocument document = JsonDocument.Parse(await output);
            return new { ruleLimit = 128, data = document.RootElement.Clone() };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new { unavailable = true, reason = "Timed out after 10 seconds" };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            return new { unavailable = true, reason = ex.GetType().Name };
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
        StringBuilder result = new();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (result.Length + read > 65536) throw new InvalidDataException("Diagnostics output exceeded 64 KiB.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
