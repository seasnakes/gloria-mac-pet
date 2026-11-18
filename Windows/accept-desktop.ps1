# Interactive desktop acceptance test (run in a normal Windows PowerShell window on the user's desktop).
# Uses an isolated GLORIA_DATA_DIR (separate mutex & pipe), so it NEVER stops or modifies your running GLORIA.exe.
# Strictly verifies all 5 characters (gloria, doro, usagi, grokbot, fourteen) for:
#   1) Win32 kernel click-through at transparent corners (`WindowFromPoint`)
#   2) Win32 kernel hit at visible character body (`WindowFromPoint`)
#   3) Real OS cursor drag (`SetCursorPos` + `mouse_event` -> `mode == 'held'`)
#   4) Real window-top landing & sitting on the test window (seatOwner matches the PowerShell host)
#   5) Context menu & real desktop screenshot (`CopyFromScreen`)
param(
    [string]$AppDir = (Join-Path $env:LOCALAPPDATA 'GLORIA-14'),
    [string]$OutDir = (Join-Path $env:TEMP ('gloria-accept-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class AcceptWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    public delegate bool VisitWindow(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(VisitWindow visit, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT cursor);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static IntPtr FindPet(uint pid, string id) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr data) {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != pid || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().Contains(" " + id + " ")) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static PT FindBody(IntPtr window, Rect bounds) {
        int[] percents = { 50, 40, 60, 30, 70, 20, 80 };
        foreach (int y in percents) foreach (int x in percents) {
            var pt = new PT {
                X = bounds.Left + (bounds.Right - bounds.Left) * x / 100,
                Y = bounds.Top + (bounds.Bottom - bounds.Top) * y / 100
            };
            if (WindowFromPoint(pt) == window) return pt;
        }
        throw new InvalidOperationException("Could not find a visible body pixel.");
    }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT pt);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint cButtons, UIntPtr dwExtraInfo);
}
"@
[void][AcceptWin32]::SetProcessDPIAware()
$null = New-Item -Type Directory $OutDir -Force

$prevDataDir = $env:GLORIA_DATA_DIR
$env:GLORIA_DATA_DIR = Join-Path $OutDir ('profile-' + [guid]::NewGuid())
function Ctl([string]$json) {
    $out = $json | & "$AppDir\GLORIA-Hook.exe" --ctl -
    if ($LASTEXITCODE -ne 0) { throw "ctl failed: $json -> $out" }
    return ($out | ConvertFrom-Json)
}

$form = New-Object Windows.Forms.Form
$form.Text = 'GLORIA Acceptance Window'
$form.StartPosition = 'Manual'
$screen = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$form.SetBounds([int]($screen.Left + $screen.Width * 0.15), [int]($screen.Top + $screen.Height * 0.44),
                [int]($screen.Width * 0.70), [int]($screen.Height * 0.40))
$form.TopMost = $true
$form.BackColor = [Drawing.Color]::White
$form.Show()
$form.Activate()
[Windows.Forms.Application]::DoEvents()

$buttonDown = $false
$startCursor = New-Object AcceptWin32+PT
$hasCursor = [AcceptWin32]::GetCursorPos([ref]$startCursor)
$proc = $null
try {
    $proc = Start-Process -FilePath "$AppDir\GLORIA.exe" -WorkingDirectory $AppDir -PassThru
    Start-Sleep -Milliseconds 1600
    $null = Ctl '{"ctl":"pref","key":"wander","on":false}'
    $null = Ctl '{"ctl":"pref","key":"chase","on":false}'
    $null = Ctl '{"ctl":"pref","key":"follow","on":false}'
    $null = Ctl '{"ctl":"pref","key":"music","on":false}'
    $null = Ctl '{"ctl":"pref","key":"nap","on":false}'
    $null = Ctl '{"ctl":"pref","key":"windowSit","on":true}'

    $ids = @('gloria', 'doro', 'usagi', 'grokbot', 'fourteen')
    $primary = [Windows.Forms.Screen]::PrimaryScreen
    $desk = Ctl '{"ctl":"desk"}'
    $display = $desk.screens | Where-Object device -eq $primary.DeviceName | Select-Object -First 1
    if (!$display) { throw "Could not find the test display." }
    $dpi = $primary.Bounds.Width / $display.bounds[2]
    $targetX = ($form.Left + $form.Width / 2) / $dpi
    $targetY = $form.Top / $dpi - 40
    $testOwner = (Get-Process -Id $PID).ProcessName
    foreach ($id in $ids) {
        $null = Ctl (@{ctl='show'; id=$id; on=$true} | ConvertTo-Json -Compress)
        foreach ($other in $ids) {
            if ($other -ne $id) { $null = Ctl (@{ctl='show'; id=$other; on=$false} | ConvertTo-Json -Compress) }
        }
        $null = Ctl (@{ctl='place'; pet=$id; x=$targetX; y=$targetY} | ConvertTo-Json -Compress)
        Start-Sleep -Milliseconds 1100

        $window = [AcceptWin32]::FindPet($proc.Id, $id)
        $bounds = New-Object AcceptWin32+Rect
        if ($window -eq [IntPtr]::Zero -or ![AcceptWin32]::GetWindowRect($window, [ref]$bounds)) {
            throw "[$id] Missing pet window."
        }
        $corner = New-Object AcceptWin32+PT -Property @{ X = $bounds.Left + 1; Y = $bounds.Top + 1 }
        if ([AcceptWin32]::WindowFromPoint($corner) -eq $window) { throw "[$id] Transparent corner intercepted input." }
        $body = [AcceptWin32]::FindBody($window, $bounds)
        $actual = New-Object AcceptWin32+PT
        if (![AcceptWin32]::SetCursorPos($body.X, $body.Y) -or ![AcceptWin32]::GetCursorPos([ref]$actual) -or
            $actual.X -ne $body.X -or $actual.Y -ne $body.Y -or [AcceptWin32]::WindowFromPoint($actual) -ne $window) {
            throw "[$id] Cursor or pet moved before the click."
        }
        [AcceptWin32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        $buttonDown = $true
        Start-Sleep -Milliseconds 300
        for ($k = 1; $k -le 12; $k++) {
            [void][AcceptWin32]::SetCursorPos($body.X + $k * 5, $body.Y - $k * 6)
            Start-Sleep -Milliseconds 28
        }
        Start-Sleep -Milliseconds 350
        $sHeld = Ctl "{`"ctl`":`"status`",`"pet`":`"$id`"}"
        [AcceptWin32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        $buttonDown = $false

        if ($sHeld.mode -ne 'held') { throw "[$id] Real OS drag did NOT enter held mode (mode=$($sHeld.mode))!" }

        $deadline = [DateTime]::UtcNow.AddSeconds(6)
        do {
            Start-Sleep -Milliseconds 120
            $sAfter = Ctl (@{ctl='status'; pet=$id} | ConvertTo-Json -Compress)
            $landed = $sAfter.seatOwner -eq $testOwner -and $sAfter.mode -eq 'perched' -and $sAfter.sitting
        } until ($landed -or [DateTime]::UtcNow -ge $deadline)
        if (!$landed) {
            throw "[$id] Expected to sit on $testOwner, got mode=$($sAfter.mode) seat=$($sAfter.seatOwner) sitting=$($sAfter.sitting)"
        }
        Write-Host ("[{0}] PASS: bodyHit=True clickThrough=True dragHeld=True landedWindow={1} sitting={2}" -f `
            $id, $sAfter.seatOwner, $sAfter.sitting)
    }

    $menu = Ctl '{"ctl":"menu","pet":"fourteen"}'
    if ($menu.items.Count -lt 15) { throw "Context menu incomplete ($($menu.items.Count) items)" }

    $captureTop = [math]::Max($screen.Top, $form.Top - [int](240 * $dpi))
    $bmp = New-Object Drawing.Bitmap($form.Width, ($form.Top - $captureTop + [int]($form.Height / 4)))
    $gfx = [Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($form.Left, $captureTop, 0, 0, $bmp.Size)
    $gfx.Dispose()
    $png = Join-Path $OutDir 'desktop-acceptance.png'
    $bmp.Save($png, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "ALL 5 CHARACTERS PASSED! Screenshot saved to: $png"
} finally {
    if ($buttonDown) {
        try { [AcceptWin32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) } catch {}
    }
    if ($hasCursor) { [void][AcceptWin32]::SetCursorPos($startCursor.X, $startCursor.Y) }
    $form.Close()
    if ($proc -and !$proc.HasExited) {
        try { $null = Ctl '{"ctl":"quit"}' } catch {}
        if (!$proc.WaitForExit(2000)) { $proc.Kill() }
    }
    if ($null -eq $prevDataDir) { Remove-Item Env:\GLORIA_DATA_DIR -ErrorAction SilentlyContinue }
    else { $env:GLORIA_DATA_DIR = $prevDataDir }
}
