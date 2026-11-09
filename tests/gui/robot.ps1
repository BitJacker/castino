<#
  Robot che usa Lindo come una persona (UI Automation di Windows) e poi verifica il sistema.
  Va eseguito con Windows PowerShell 5.1 come amministratore, con Lindo installato.

  Passi:
   1. apre Lindo e aspetta l'elenco delle app
   2. cerca "VLC", clicca Disinstalla, conferma, porta avanti la procedura guidata di VLC
      (Avanti / Disinstalla / Fine), elimina i residui proposti, legge il risultato
   3. seleziona 7-Zip e Notepad++ con le caselle e li disinstalla insieme
   4. Residui e verifica: analizza, "Seleziona tutti i sicuri", elimina
   5. Ottimizza: "Ottimizza ora" e poi "Ripristina tutto"
   6. Avvio e attività: spegne e riaccende un'app di prova all'avvio
   7. Errori e crash: dopo un crash provocato apposta, controlla che venga spiegato
  Dopo ogni passo controlla il sistema reale (registro, cartelle, altre app intatte).
#>
param(
    [string]$Out = "$PWD\gui-robot",
    [string]$Lindo = "$env:ProgramFiles\Lindo\Lindo.exe",
    [string]$ExpectOrphans = "",
    [string]$MustSurvive = ""
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
New-Item -ItemType Directory -Force $Out | Out-Null
$report = Join-Path $Out 'robot.txt'
Set-Content $report ''
$script:fail = 0
$script:shot = 0

function Log($m) { $l = (Get-Date -Format 'HH:mm:ss ') + $m; Add-Content $report $l; Write-Host $l }
function Check($ok, $what) { if ($ok) { Log "PASS $what" } else { Log "FAIL $what"; $script:fail++ } }

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$PC = [System.Windows.Automation.PropertyCondition]

function Find-ById($root, $id) {
    $root.FindFirst($TS::Descendants, (New-Object $PC ($AE::AutomationIdProperty, $id)))
}
function Find-ByName($root, $name) {
    $root.FindFirst($TS::Descendants, (New-Object $PC ($AE::NameProperty, $name)))
}
function Wait-For([scriptblock]$cond, [int]$seconds = 60, [string]$what = '') {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        try { $r = & $cond; if ($r) { return $r } } catch {}
        Start-Sleep -Milliseconds 500
    }
    Log "TIMEOUT: $what"
    return $null
}
function Visible($el) { $el -ne $null -and -not $el.Current.IsOffscreen }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RealMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, IntPtr e);
    // Premere e rilasciare ALT sblocca il cambio di finestra in primo piano da un altro processo
    public static void Key(byte k) { keybd_event(k, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(50); keybd_event(k, 0, 2, IntPtr.Zero); }
    public static void Front(IntPtr h) { keybd_event(0x12, 0, 0, IntPtr.Zero); keybd_event(0x12, 0, 2, IntPtr.Zero); SetForegroundWindow(h); }
    public static void Click(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
'@

# Clic vero col mouse, come una persona: porta Lindo davanti, scorre fino all'elemento e ci clicca sopra
function Click($el) {
    if ($el -eq $null) { throw "Elemento non trovato" }
    try { Close-Strays } catch {}
    if (-not $el.Current.IsEnabled) { throw "Elemento disattivato: $($el.Current.Name)" }
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { try { $p.ScrollIntoView(); Start-Sleep -Milliseconds 300 } catch {} }
    [RealMouse]::Front([IntPtr]$script:win.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 150
    $pt = New-Object System.Windows.Point
    if (-not $el.TryGetClickablePoint([ref]$pt)) {
        $r = $el.Current.BoundingRectangle
        if ($r.IsEmpty -or $r.Width -le 0) { throw "Elemento non visibile: $($el.Current.Name)" }
        $pt = New-Object System.Windows.Point ($r.X + $r.Width / 2), ($r.Y + $r.Height / 2)
    }
    [RealMouse]::Click([int]$pt.X, [int]$pt.Y)
    Start-Sleep -Milliseconds 400
}

# Prova un passo; se qualcosa va storto lo segna come errore e continua con il passo dopo
function Step($name, [scriptblock]$body) {
    Log "---- $name"
    try { Close-Strays } catch {}
    try { & $body } catch { Log "FAIL $name interrotto: $($_.Exception.Message)"; $script:fail++; Shot "errore" ; try { Close-Dialogs } catch {} }
}
function Close-Dialogs {
    foreach ($id in 'DlgSkipWait', 'DlgSkipLeftovers', 'DlgResultPrimary') {
        $b = Find-ById $script:win $id
        if (Visible $b) { Invoke $b; Start-Sleep -Seconds 1 }
    }
}

function Invoke($el) {
    if ($el -eq $null) { throw "Elemento non trovato" }
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return }
    throw "Elemento non cliccabile: $($el.Current.Name)"
}
function Toggle-On($el) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($p.Current.ToggleState -ne 'On') { Click $el }
    if ($p.Current.ToggleState -ne 'On') { throw "La casella '$($el.Current.Name)' non si è attivata" }
}
function Set-Text($el, $text) {
    $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
}
function Text-Of($root, $id) { $e = Find-ById $root $id; if ($e) { $e.Current.Name } else { '' } }

function Shot($name) {
    $script:shot++
    try {
        $r = $script:win.Current.BoundingRectangle
        $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
        $file = Join-Path $Out ("{0:00}-{1}.png" -f $script:shot, $name)
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    } catch { Log "screenshot non riuscito: $_" }
}

# Porta avanti le procedure guidate degli altri programmi, come farebbe una persona:
# solo finestre di processi nati dopo l'avvio del robot; prova a cliccare il pulsante giusto,
# altrimenti porta la finestra davanti e preme Invio (il pulsante predefinito: OK / Avanti / Disinstalla / Fine).
$wizardButtons = @('Uninstall', 'Disinstalla', '&Uninstall', 'Remove', 'Rimuovi', 'Yes', 'Sì', 'Si', 'OK', 'Next >', 'Next', 'Avanti >', 'Avanti',
                   'Finish', 'Fine', 'Close', 'Chiudi', 'Continue', 'Continua')
$script:seenWindows = @{}
$script:lastPoke = @{}
function Drive-Wizards {
    $lindoPid = $script:proc.Id
    $windows = $AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $windows) {
        try {
            $wpid = $w.Current.ProcessId
            if ($wpid -eq $lindoPid -or $script:basePids.Contains($wpid)) { continue }
            $pname = (Get-Process -Id $wpid -ErrorAction SilentlyContinue).ProcessName
            if ($pname -in @('explorer', 'pwsh', 'powershell', 'conhost', 'ShellExperienceHost', 'SearchHost', 'StartMenuExperienceHost', 'TextInputHost', 'msedge', 'Greenshot', 'Everything')) { continue }
            $key = "$wpid|$($w.Current.Name)"
            if (-not $script:seenWindows.ContainsKey($key)) { $script:seenWindows[$key] = 1; Log "  finestra di un altro programma: '$($w.Current.Name)' ($pname)" }
            if ($script:lastPoke.ContainsKey($key) -and ((Get-Date) - $script:lastPoke[$key]).TotalSeconds -lt 3) { continue }
            $script:lastPoke[$key] = Get-Date
            $done = $false
            foreach ($b in $wizardButtons) {
                $btn = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition (
                        (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
                        (New-Object $PC ($AE::NameProperty, $b)))))
                if ($btn -and $btn.Current.IsEnabled -and -not $btn.Current.IsOffscreen) {
                    Log "  procedura guidata '$($w.Current.Name)': clic su '$b'"
                    try { Invoke $btn; $done = $true } catch { Log "  clic non riuscito ($($_.Exception.Message)), provo con Invio" }
                    break
                }
            }
            if (-not $done) {
                Log "  procedura guidata '$($w.Current.Name)': premo Invio"
                [RealMouse]::Front([IntPtr]$w.Current.NativeWindowHandle)
                Start-Sleep -Milliseconds 300
                [RealMouse]::Key(0x0D)
            }
            Start-Sleep -Milliseconds 800
        } catch { Log "  errore con una finestra: $($_.Exception.Message)" }
    }
}

# Alcuni disinstallatori aprono il browser alla fine (pagina "perché disinstalli?"): una persona lo chiude
function Close-Strays {
    foreach ($p in Get-Process msedge, chrome, firefox -ErrorAction SilentlyContinue) {
        if (-not $script:basePids.Contains($p.Id)) {
            if (-not $script:strayLogged) { Log "  chiudo il browser aperto da un disinstallatore"; $script:strayLogged = $true }
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Installed($pattern) {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    Get-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like $pattern }
}
function InstallDirs {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    Get-ItemProperty $keys -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -and $_.InstallLocation -and (Test-Path $_.InstallLocation.Trim('"')) } |
        ForEach-Object { [pscustomobject]@{ Name = $_.DisplayName; Dir = $_.InstallLocation.Trim('"') } }
}

# Attende la fine di un dialogo di Lindo (risultato), guidando nel frattempo le procedure degli altri programmi
function Wait-Dialog-End([int]$seconds = 600) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) {
        if (Visible (Find-ById $script:win 'DlgLeftoversTitle')) { return 'residui' }
        if (Visible (Find-ById $script:win 'DlgResultTitle')) { return 'risultato' }
        Drive-Wizards
        Start-Sleep -Seconds 1
    }
    return 'timeout'
}

function Uninstall-Flow($label) {
    Click (Find-ById $script:win 'DlgConfirmButton')
    Log "Conferma premuta ($label)"
    $state = Wait-Dialog-End 600
    Log "Stato del dialogo: $state"
    if ($state -eq 'timeout') {
        # Come farebbe una persona stanca di aspettare: "Ho finito, cerca i residui"
        Shot "$label-attesa-lunga"
        Log "Il disinstallatore non ha finito: premo 'Ho finito, cerca i residui'"
        Click (Find-ById $script:win 'DlgSkipWait')
        $state = Wait-Dialog-End 300
        Log "Stato del dialogo: $state"
    }
    Shot "$label-dopo-disinstallatore"
    if ($state -eq 'residui') {
        Log ("Residui proposti: " + (Text-Of $script:win 'DlgLeftoversText'))
        $list = Find-ById $script:win 'DlgLeftoversList'
        if ($list) {
            $items = $list.FindAll($TS::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
            foreach ($i in $items) {
                $st = $i.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
                Log "    [$st] $($i.Current.Name)"
            }
        }
        Click (Find-ById $script:win 'DlgDeleteButton')
        Log "Premuto 'Elimina selezionati'"
        $state = Wait-Dialog-End 300
    }
    $title = Text-Of $script:win 'DlgResultTitle'
    Log "Risultato mostrato da Lindo: $title"
    $checks = Find-ById $script:win 'DlgChecks'
    if ($checks) {
        $texts = $checks.FindAll($TS::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
        foreach ($t in $texts) { if ($t.Current.Name.Length -gt 2) { Log "    · $($t.Current.Name)" } }
    }
    Shot "$label-risultato"
    Click (Find-ById $script:win 'DlgResultPrimary')
    Start-Sleep -Seconds 2
    return $title
}

function Wait-Apps-Loaded {
    Wait-For { $t = Text-Of $script:win 'AppsSummary'; $t -and $t -notlike 'Scanning*' } 180 'elenco app' | Out-Null
    Start-Sleep -Seconds 4
}

# ---------------------------------------------------------------- avvio
$before = @(InstallDirs)
Log "App installate con cartella: $($before.Count)"

# App di prova all'avvio
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LindoProvaAvvio' -Value '"C:\Windows\System32\notepad.exe" /nonesiste'

$script:basePids = New-Object 'System.Collections.Generic.HashSet[int]'
foreach ($p in Get-Process) { [void]$script:basePids.Add($p.Id) }
$script:proc = Start-Process $Lindo -PassThru
$script:win = Wait-For {
    $AE::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.AndCondition (
        (New-Object $PC ($AE::ProcessIdProperty, $script:proc.Id)),
        (New-Object $PC ($AE::NameProperty, 'Lindo')))))
} 60 'finestra di Lindo'
Check ($script:win -ne $null) "Lindo si apre"
if (-not $script:win) { exit 1 }
$script:win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState('Maximized')
Wait-Apps-Loaded
Log ("Riepilogo: " + (Text-Of $script:win 'AppsSummary'))
Shot 'elenco-app'

# ---------------------------------------------------------------- 1. VLC da solo
Step '1. Disinstallo VLC dal suo pulsante' {
Set-Text (Find-ById $script:win 'SearchBox') 'VLC'
Start-Sleep -Seconds 2
Shot 'ricerca-vlc'
$btn = Wait-For { Find-ByName $script:win 'Uninstall VLC media player' } 20 'pulsante Disinstalla VLC'
Check ($btn -ne $null) "Trovo il pulsante Disinstalla di VLC"
if ($btn) {
    Click $btn
    Start-Sleep -Seconds 1
    Shot 'vlc-conferma'
    $vlcDir = (Installed 'VLC*').InstallLocation
    Uninstall-Flow 'vlc' | Out-Null
    Check (-not (Installed 'VLC*')) "VLC non è più tra le app installate"
    Check (-not (Test-Path "$env:ProgramFiles\VideoLAN\VLC")) "Cartella di VLC eliminata"
    Check (-not (Test-Path "$env:APPDATA\vlc")) "Impostazioni di VLC in AppData eliminate"
    Check (-not (Test-Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\VideoLAN")) "Collegamenti di VLC nel menu Start eliminati"
}
Set-Text (Find-ById $script:win 'SearchBox') ''
Wait-Apps-Loaded

}

# ---------------------------------------------------------------- 2. più app insieme
Step '2. Disinstallo 7-Zip e Notepad++ insieme' {
$sel = 0
foreach ($n in '7-Zip*', 'Notepad++*') {
    Set-Text (Find-ById $script:win 'SearchBox') $n.TrimEnd('*').Split(' ')[0]
    Start-Sleep -Seconds 2
    $cb = $script:win.FindFirst($TS::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
    $all = $script:win.FindAll($TS::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
    foreach ($c in $all) { if ($c.Current.Name -like "Select $n") { Toggle-On $c; $sel++; Log "Selezionata: $($c.Current.Name)"; break } }
}
Set-Text (Find-ById $script:win 'SearchBox') ''
Start-Sleep -Seconds 2
Check ($sel -eq 2) "Selezionate 2 app con le caselle"
Shot 'selezione-multipla'
$multi = Find-ById $script:win 'UninstallSelectedButton'
if ($multi) {
    # Il browser aperto in ritardo dal disinstallatore di VLC può coprire Lindo: se il clic non apre la conferma, lo chiudo e riprovo
    for ($try = 1; $try -le 3; $try++) {
        Click $multi
        if (Wait-For { Visible (Find-ById $script:win 'DlgConfirmButton') } 8 'conferma disinstallazione multipla') { break }
        Log "  la conferma non è comparsa (tentativo $try): chiudo eventuali finestre davanti e riprovo"
        Start-Sleep -Seconds 3
        Close-Strays
    }
    Start-Sleep -Seconds 1
    Shot 'multi-conferma'
    Uninstall-Flow 'multi' | Out-Null
    Check (-not (Installed '7-Zip*')) "7-Zip disinstallato"
    Check (-not (Installed 'Notepad++*')) "Notepad++ disinstallato"
    Check (-not (Test-Path "$env:ProgramFiles\7-Zip")) "Cartella di 7-Zip eliminata"
    Check (-not (Test-Path "$env:ProgramFiles\Notepad++")) "Cartella di Notepad++ eliminata"
}
Wait-Apps-Loaded

}

# ---------------------------------------------------------------- 3. Residui e verifica
Step '3. Residui e verifica' {
Click (Find-ById $script:win 'NavLeftovers')
Start-Sleep -Seconds 1
Click (Find-ById $script:win 'ScanOrphansButton')
Wait-For { ((Text-Of $script:win 'OrphansSelection') -like '*selected*') -or (Visible (Find-ById $script:win 'OrphansClean')) } 300 'analisi residui' | Out-Null
Start-Sleep -Seconds 2
Shot 'residui'
$selAll = Find-ById $script:win 'OrphansSelectAll'
if (Visible $selAll) {
    Toggle-On $selAll
    Start-Sleep -Seconds 1
    Log ("Selezione: " + (Text-Of $script:win 'OrphansSelection'))
    Shot 'residui-selezionati'
    Click (Find-ById $script:win 'DeleteOrphansButton')
    Start-Sleep -Seconds 1
    Shot 'residui-conferma'
    Click (Find-ById $script:win 'DlgResultPrimary')   # "Elimina"
    Start-Sleep -Seconds 2
    Wait-For { $t = Text-Of $script:win 'DlgResultTitle'; (Visible (Find-ById $script:win 'DlgResultTitle')) -and $t -notlike 'Delete *' } 300 'pulizia residui' | Out-Null
    Log ("Risultato: " + (Text-Of $script:win 'DlgResultTitle'))
    Shot 'residui-risultato'
    Click (Find-ById $script:win 'DlgResultPrimary')
    Start-Sleep -Seconds 3
}
foreach ($e in ($ExpectOrphans -split ';' | Where-Object { $_ })) { Check (-not (Test-Path $e)) "Residuo finto eliminato: $e" }

}

# ---------------------------------------------------------------- 4. Ottimizza
Step '4. Ottimizza e Ripristina' {
$dvrBefore = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -ErrorAction SilentlyContinue).AppCaptureEnabled
$mouseBefore = (Get-ItemProperty 'HKCU:\Control Panel\Mouse').MouseSpeed
Click (Find-ById $script:win 'NavOptimize')
Wait-For { $t = Text-Of $script:win 'OptimizeScore'; $t -and $t -ne 'Scanning…' } 60 'analisi ottimizzazioni' | Out-Null
$res = Wait-For { $c = Text-Of $script:win 'CpuValue'; $r = Text-Of $script:win 'RamValue'; if ($c -like '*%' -and $r -like '*%') { "$c|$r" } } 90 'lettura CPU e RAM'
Log ("Risorse lette da Lindo: CPU " + (Text-Of $script:win 'CpuValue') + ", RAM " + (Text-Of $script:win 'RamValue') + " · GPU: " + (Text-Of $script:win 'GpuNames'))
Check ($res -ne $null) "Lindo mostra l'uso di CPU e RAM"
$realRam = [math]::Round((Get-CimInstance Win32_OperatingSystem | ForEach-Object { 100 - 100 * $_.FreePhysicalMemory / $_.TotalVisibleMemorySize }))
$lindoRam = [int]((Text-Of $script:win 'RamValue').TrimEnd('%'))
Check ([math]::Abs($realRam - $lindoRam) -le 15) "RAM di Lindo ($lindoRam%) vicina a quella di Windows ($realRam%)"
Check ((Text-Of $script:win 'GpuNames') -notin @('', '…')) "Lindo riconosce la scheda video"
Shot 'ottimizza'
Click (Find-ById $script:win 'OptimizeButton')
$st = Wait-Dialog-End 300
Log ("Risultato ottimizzazione: " + (Text-Of $script:win 'DlgResultTitle'))
Shot 'ottimizza-risultato'
Click (Find-ById $script:win 'DlgResultPrimary')
Check ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR').AppCaptureEnabled -eq 0) "Registrazione in background di Game Bar disattivata"
Check ((Get-ItemProperty 'HKCU:\Control Panel\Mouse').MouseSpeed -eq '0') "Accelerazione del mouse disattivata"
Check ((powercfg /getactivescheme) -match '8c5e7fda') "Piano energetico Prestazioni elevate attivo"
Start-Sleep -Seconds 2
Click (Find-ByName $script:win 'Restore all')
Start-Sleep -Seconds 8
Shot 'ottimizza-ripristino'
Check ((Get-ItemProperty 'HKCU:\Control Panel\Mouse').MouseSpeed -eq $mouseBefore) "Mouse ripristinato com'era ($mouseBefore)"
$dvrAfter = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -ErrorAction SilentlyContinue).AppCaptureEnabled
Check ($dvrAfter -eq $dvrBefore) "Game Bar ripristinata com'era"

}

# ---------------------------------------------------------------- 5. Avvio
Step '5. App all''avvio' {
Click (Find-ById $script:win 'NavStartup')
$sw = Wait-For { Find-ByName $script:win 'Startup LindoProvaAvvio' } 120 'app di prova all''avvio'
Shot 'avvio'
Check ($sw -ne $null) "L'app di prova compare tra le app all'avvio"
if ($sw) {
    Click $sw
    Start-Sleep -Seconds 3
    $v = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -ErrorAction SilentlyContinue).LindoProvaAvvio
    Check ($v -and ($v[0] -band 1) -eq 1) "Disattivata come fa Gestione attività (StartupApproved = 03)"
    Check ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').LindoProvaAvvio -ne $null) "La voce originale non è stata cancellata"
    Shot 'avvio-disattivata'
    Click (Find-ByName $script:win 'Startup LindoProvaAvvio')
    Start-Sleep -Seconds 3
    $v = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run').LindoProvaAvvio
    Check ($v -and ($v[0] -band 1) -eq 0) "Riattivata"
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LindoProvaAvvio' -ErrorAction SilentlyContinue

}

# ---------------------------------------------------------------- 6. Errori e crash
Step '6. Errori e crash' {
Start-Process powershell -ArgumentList "-NoProfile -Command [Environment]::FailFast('crash di prova per Lindo')" -Wait -ErrorAction SilentlyContinue
Start-Sleep -Seconds 8
Click (Find-ById $script:win 'NavCrashes')
Start-Sleep -Seconds 1
Wait-For { -not (Visible (Find-ById $script:win 'CrashLoading')) } 120 'lettura crash' | Out-Null
Start-Sleep -Seconds 2
Shot 'crash'
$crash = Find-ByName $script:win 'powershell.exe closed unexpectedly'
Check ($crash -ne $null) "Il crash provocato compare e viene spiegato"

}

# ---------------------------------------------------------------- verifiche finali
Click (Find-ById $script:win 'NavApps')
Start-Sleep -Seconds 2
Shot 'fine'
$script:proc | Stop-Process -ErrorAction SilentlyContinue

$removed = @('VLC*', '7-Zip*', 'Notepad++*')
foreach ($b in $before) {
    if ($removed | Where-Object { $b.Name -like $_ }) { continue }
    Check (Test-Path $b.Dir) "Ancora intatta: $($b.Name)"
}
foreach ($m in ($MustSurvive -split ';' | Where-Object { $_ })) { Check (Test-Path $m) "Ancora presente: $m" }

Log ("RISULTATO: " + $(if ($script:fail -eq 0) { 'OK' } else { "$($script:fail) errori" }))
exit $script:fail
