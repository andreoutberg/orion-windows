[CmdletBinding()]
param([Parameter(Mandatory)] [int] $BrowserProcessId)

$ErrorActionPreference = 'Stop'
$broker = Get-CimInstance Win32_Process -Filter "ProcessId=$BrowserProcessId"
if (!$broker -or $broker.Name -ne 'OrionEngineB.exe' -or $broker.CommandLine -match '(?:^|\s)--type=') {
    throw 'Supply the process ID of the Orion sandbox broker.'
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class OrionSandboxToken {
    [DllImport("kernel32", SetLastError=true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("advapi32", SetLastError=true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32")] private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32", SetLastError=true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int info, IntPtr data, int size, out int required);
    [DllImport("advapi32")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

    public static int Verify(int processId) {
        using (var process = OpenProcess(0x1000, false, processId)) {
            SafeAccessTokenHandle token;
            if (process.IsInvalid || !OpenProcessToken(process, 8, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token) {
                if (!IsTokenRestricted(token)) throw new Exception("Renderer token is unrestricted.");
                int bytes;
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out bytes); // TokenIntegrityLevel
                IntPtr data = Marshal.AllocHGlobal(bytes);
                try {
                    if (!GetTokenInformation(token, 25, data, bytes, out bytes))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var sid = Marshal.ReadIntPtr(data);
                    int last = Marshal.ReadByte(GetSidSubAuthorityCount(sid)) - 1;
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, last));
                } finally { Marshal.FreeHGlobal(data); }
            }
        }
    }
}
'@

$renderers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$BrowserProcessId" |
    Where-Object { $_.CommandLine -match '(?:^|\s)--type=renderer(?:\s|$)' })
if (!$renderers.Count) { throw 'No renderer is running. Load a test page in the isolated browser first.' }
foreach ($renderer in $renderers) {
    if ($renderer.ExecutablePath -ne $broker.ExecutablePath -or $renderer.CommandLine -match '(?:^|\s)--no-sandbox(?:\s|$)') {
        throw "Renderer $($renderer.ProcessId) did not launch through the expected sandbox broker."
    }
    $integrity = [OrionSandboxToken]::Verify($renderer.ProcessId)
    if ($integrity -gt 0x1000) { throw "Renderer $($renderer.ProcessId) has integrity RID $integrity." }
    [pscustomobject]@{ Browser = $BrowserProcessId; Renderer = $renderer.ProcessId; Restricted = $true; IntegrityRID = $integrity }
}
