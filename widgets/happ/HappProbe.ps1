# Local-only UI Automation probe. Never reads Happ configuration, URLs or logs.
param([ValidateSet('Probe','Inspect','Open','ToggleWindow','Connect','Disconnect')][string]$Action = 'Probe', [string]$HappPath = '')
$ErrorActionPreference = 'Stop'
if ($env:WIDBAR_HAPP_ACTION) { $Action = $env:WIDBAR_HAPP_ACTION; $HappPath = $env:WIDBAR_HAPP_PATH }
$r = [ordered]@{ Running=$false; Visible=$false; State='Unknown'; Server=''; Mode=''; CanConnect=$false; CanDisconnect=$false; CanOpen=$false; CanHide=$false; Message=''; Details=''; Pid=0 }
function Emit { $r | ConvertTo-Json -Compress -Depth 3 }
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class HappWindows {
    private delegate bool Callback(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback cb, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static IntPtr[] Windows(int pid) {
        var list = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr p) { uint id; GetWindowThreadProcessId(h,out id); if(id == (uint)pid) list.Add(h); return true; }, IntPtr.Zero);
        return list.ToArray();
    }
    public static string Title(IntPtr h) { var b=new StringBuilder(512); GetWindowText(h,b,b.Capacity); return b.ToString(); }
    public static bool Owned(IntPtr h,int pid) { uint id; return GetWindowThreadProcessId(h,out id)!=0 && id==(uint)pid; }
}
'@
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $all = @(Get-Process -Name Happ -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
    if ($HappPath) {
        if (-not [IO.Path]::IsPathRooted($HappPath) -or [IO.Path]::GetFileName($HappPath) -ine 'Happ.exe') { throw 'Set a full path to Happ.exe.' }
        $HappPath = [IO.Path]::GetFullPath($HappPath)
        $all = @($all | Where-Object { try { $_.Path -and [IO.Path]::GetFullPath($_.Path) -ieq $HappPath } catch { $false } })
    }
    if ($all.Count -eq 0 -and $Action -in @('Open','ToggleWindow') -and $HappPath -and (Test-Path -LiteralPath $HappPath -PathType Leaf)) {
        # Never start another instance when a Happ process is already present.
        $other = @(Get-Process -Name Happ -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session })
        if ($other.Count -eq 0) { Start-Process -FilePath $HappPath; $r.Message='Launch requested; refresh after Happ starts.'; Emit; return }
    }
    if ($all.Count -ne 1) {
        $r.Message = if ($all.Count -gt 1) { 'Multiple Happ processes. Set the executable path; no action taken.' } else { 'Happ is not detected. Start it from the Start menu or set its path.' }
        Emit; return
    }
    $p = $all[0]; $r.Running=$true; $r.Pid=$p.Id
    $windows = @([HappWindows]::Windows($p.Id))
    $main = @($windows | Where-Object { [HappWindows]::Title($_) -match '^Happ\s+\d+(?:\.\d+)+' })
    $r.CanOpen = $main.Count -eq 1
    $r.CanHide = $main.Count -eq 1 -and [HappWindows]::IsWindowVisible($main[0])
    $r.Visible = $r.CanHide
    if ($Action -in @('Open','ToggleWindow')) {
        if ($main.Count -ne 1 -or -not [HappWindows]::Owned($main[0],$p.Id)) { $r.Message='Main window not uniquely identified. Use the Happ tray icon.'; Emit; return }
        if ($Action -eq 'ToggleWindow' -and $r.Visible -and -not [HappWindows]::IsIconic($main[0])) {
            if ([HappWindows]::PostMessage($main[0],0x0010,[IntPtr]::Zero,[IntPtr]::Zero)) {
                $r.Message='Normal close-to-tray requested. Connection state unchanged by widget.'
            } else { $r.Message='Close request failed. Use Happ tray or its window controls.' }
            Emit; return
        }
        $cmd = if ([HappWindows]::IsIconic($main[0])) { 9 } else { 5 }
        $null=[HappWindows]::ShowWindowAsync($main[0],$cmd)
        $null=[HappWindows]::SetForegroundWindow($main[0])
        $r.Message='Window restore requested. If not visible, use Happ tray > Show window.'
        Emit; return
    }
    if ($Action -in @('Connect','Disconnect') -and $main.Count -ne 1) { $r.Message='Main window not uniquely identified; nothing invoked.'; Emit; return }
    $wasHidden = $main.Count -eq 1 -and -not $r.Visible
    if ($Action -in @('Connect','Disconnect') -and $wasHidden) {
        if (-not [HappWindows]::Owned($main[0],$p.Id)) { throw 'Window changed.' }
        $null=[HappWindows]::ShowWindowAsync($main[0],5)
        $null=[HappWindows]::SetForegroundWindow($main[0])
        Start-Sleep -Milliseconds 600
    }
    # Automatic polling must never enter the Happ UI Automation provider or show its window.
    if ($Action -eq 'Probe') {
        $r.Message=if($r.Visible){'Happ window detected. Connection state not queried in background.'}else{'Happ is running in background. Connection state not queried.'}
        $r.Details="PID=$($p.Id); windows=$($windows.Count); main=$($main.Count); visible=$($r.Visible); passive=True"
        Emit; return
    }
    if ($Action -eq 'Inspect' -and (-not $r.Visible -or $main.Count -ne 1 -or [HappWindows]::IsIconic($main[0]))) {
        $r.Message='Open Happ normally, then run diagnostics. No window was opened by the probe.'
        $r.Details="PID=$($p.Id); windows=$($windows.Count); main=$($main.Count); visible=$($r.Visible)"
        Emit; return
    }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $roots = @($windows | Where-Object { ($_ -in $main) -or [HappWindows]::IsWindowVisible($_) })
    $walker=[Windows.Automation.TreeWalker]::RawViewWalker
    $queue=New-Object 'System.Collections.Generic.Queue[Windows.Automation.AutomationElement]'
    $rootError=$false
    foreach($h in $roots) { try { $e=[Windows.Automation.AutomationElement]::FromHandle($h); if($e -and $e.Current.ProcessId -eq $p.Id) { $queue.Enqueue($e) } else { $rootError=$true } } catch { $rootError=$true } }
    $connect=New-Object 'System.Collections.Generic.List[Windows.Automation.AutomationElement]'
    $disconnect=New-Object 'System.Collections.Generic.List[Windows.Automation.AutomationElement]'
    $states=New-Object 'System.Collections.Generic.List[string]'
    $servers=New-Object 'System.Collections.Generic.List[string]'
    $modes=New-Object 'System.Collections.Generic.List[string]'
    $notes=New-Object 'System.Collections.Generic.List[string]'
    $seen=New-Object 'System.Collections.Generic.HashSet[string]'
    $n=0; $watch=[Diagnostics.Stopwatch]::StartNew(); $incomplete=$rootError -or $queue.Count -eq 0
    while($queue.Count -gt 0 -and $n -lt 600 -and $watch.ElapsedMilliseconds -lt 3500) {
        $e=$queue.Dequeue(); $n++
        try {
            if($e.Current.ProcessId -ne $p.Id) { continue }
            $key=($e.GetRuntimeId() -join ','); if($key -and -not $seen.Add($key)) { continue }
            $name=([string]$e.Current.Name).Trim(); $type=$e.Current.ControlType.ProgrammaticName
            $off=$e.Current.IsOffscreen
            $pattern=$null
            $invokable=$false
            if($type -in @('ControlType.Button','ControlType.MenuItem') -and $e.Current.IsEnabled -and -not $off) {
                $invokable=$e.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)
            }
            if($name -match '^(Connect|Подключиться|Подключить)$' -and $invokable) { $connect.Add($e); $notes.Add("$type : Connect / Invoke") }
            if($name -match '^(Disconnect|Отключиться|Отключить)$' -and $invokable) { $disconnect.Add($e); $notes.Add("$type : Disconnect / Invoke") }
            # Only explicit status text; never treat process existence or a proxy flag as a connection.
            if(-not $off -and $type -in @('ControlType.Text','ControlType.MenuItem','ControlType.StatusBar','ControlType.Custom','ControlType.Button')) {
                if($name -match '^(Connected|Подключ[её]н|Подключено)$') { $states.Add('Connected') }
                elseif($name -match '^(Disconnected|Отключ[её]н|Отключено|Не подключ[её]н|Не подключено)$') { $states.Add('Disconnected') }
                elseif($name -match '^(Connected|Подключено)\s+(.{1,120})$' -and $type -eq 'ControlType.MenuItem') { $states.Add('Connected'); $servers.Add($Matches[2]) }
            }
            if($type -eq 'ControlType.ComboBox' -and $name -match '^(Proxy|Прокси|TUN|Mixed|Смешанный)$') { $modes.Add($name) }
            if($notes.Count -lt 35 -and $type -in @('ControlType.Button','ControlType.MenuItem') -and ($name -match '^(Connect|Disconnect|Подключ|Отключ|Показать окно|Show window|Сменить сервер|Change server|Режим транспорта|Transport mode|Маршрутизация|Routing)' -or -not $name)) {
                $label=if(-not $name){'<unnamed>'}elseif($name -match '^(Connect|Disconnect|Подключиться|Подключить|Отключиться|Отключить|Показать окно|Show window|Сменить сервер|Change server|Режим транспорта|Transport mode|Маршрутизация|Routing)$'){$name}else{'<other matching label>'}
                $notes.Add("$type : $label ; Invoke=$invokable ; Offscreen=$off")
            }
            $child=$walker.GetFirstChild($e); $siblings=0
            while($child -and $siblings -lt 150 -and $queue.Count -lt 600 -and $watch.ElapsedMilliseconds -lt 3500) { $queue.Enqueue($child); $siblings++; $child=$walker.GetNextSibling($child) }
            if($child) { $incomplete=$true }
        } catch { $incomplete=$true }
    }
    $incomplete=$incomplete -or $queue.Count -gt 0
    $unique=@($states | Select-Object -Unique)
    if(-not $incomplete -and $unique.Count -eq 1) { $r.State=$unique[0] }
    $srv=@($servers | Select-Object -Unique); if($r.State -eq 'Connected' -and $srv.Count -eq 1) { $r.Server=$srv[0] }
    $mode=@($modes | Select-Object -Unique); if($mode.Count -eq 1) { $r.Mode=$mode[0] }
    # Fail closed: exact unique action, complete scan, no contradictory state.
    $r.CanConnect= -not $incomplete -and $unique.Count -le 1 -and $r.State -ne 'Connected' -and $connect.Count -eq 1 -and $disconnect.Count -eq 0
    $r.CanDisconnect= -not $incomplete -and $unique.Count -le 1 -and $r.State -ne 'Disconnected' -and $disconnect.Count -eq 1 -and $connect.Count -eq 0
    $r.Details="PID=$($p.Id); windows=$($windows.Count); main=$($main.Count); nodes=$n; incomplete=$incomplete; connect=$($connect.Count); disconnect=$($disconnect.Count)`n"+($notes -join "`n")
    $r.Message=if($incomplete){'UI Automation scan incomplete; controls disabled.'}elseif($r.State -eq 'Unknown'){'Connection state not exposed. Open Happ on Servers and refresh.'}else{'Status read from Happ UI.'}
    if($Action -in @('Connect','Disconnect')) {
        $target=if($Action -eq 'Connect' -and $r.CanConnect){$connect[0]}elseif($Action -eq 'Disconnect' -and $r.CanDisconnect){$disconnect[0]}else{$null}
        if(-not $target) { $r.Message='Action not safely identified; nothing invoked.' }
        else {
            $expected=if($Action -eq 'Connect'){'^(Connect|Подключиться|Подключить)$'}else{'^(Disconnect|Отключиться|Отключить)$'}
            $invoke=$null
            if($target.Current.ProcessId -eq $p.Id -and $target.Current.Name.Trim() -match $expected -and $target.Current.IsEnabled -and -not $target.Current.IsOffscreen -and $target.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$invoke)) {
                $invoke.Invoke(); $r.State='Unknown'; $r.Server=''; $r.CanConnect=$false; $r.CanDisconnect=$false; $r.Message='Action invoked; verify in Happ. Close its window normally to return to tray.'
                if ($wasHidden) {
                    Start-Sleep -Milliseconds 500
                    if ([HappWindows]::Owned($main[0],$p.Id) -and [HappWindows]::PostMessage($main[0],0x0010,[IntPtr]::Zero,[IntPtr]::Zero)) { $r.Message='Action invoked; close-to-tray requested. Connection result not verified.' }
                }
            } else { $r.Message='Control changed; nothing invoked.' }
        }
    }
} catch { $r.State='Unknown'; $r.Server=''; $r.CanConnect=$false; $r.CanDisconnect=$false; $r.Message='Probe failed: '+$_.Exception.GetType().Name+'. Check Happ and WidBar privilege levels.' }
Emit
