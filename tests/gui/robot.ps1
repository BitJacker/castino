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
    if ($p.Current.ToggleState -ne 'On') { $p.Toggle() }
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

# Clicca i pulsanti delle procedure guidate degli altri programmi, come farebbe una persona
$wizardButtons = @('Uninstall', 'Disinstalla', '&Uninstall', 'Remove', 'Rimuovi', 'Yes', 'Sì', 'Si', 'OK', 'Next >', 'Next', 'Avanti >', 'Avanti',
                   'Finish', 'Fine', 'Close', 'Chiudi', 'Continue', 'Continua')
function Drive-Wizards {
    $lindoPid = $script:proc.Id
    $windows = $AE::RootElement.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $windows) {
        try {
            if ($w.Current.ProcessId -eq $lindoPid) { continue }
            $pname = (Get-Process -Id $w.Current.ProcessId -ErrorAction SilentlyContinue).ProcessName
            if ($pname -in @('explorer', 'pwsh', 'powershell', 'Runner.Worker', 'Runner.Listener', 'msedge', 'Greenshot', 'Everything', 'conhost', 'ShellExperienceHost', 'SearchHost', 'StartMenuExperienceHost', 'TextInputHost')) { continue }
            # Opzioni "mantieni le impostazioni": le lasciamo come sono
            foreach ($b in $wizardButtons) {
                $btn = $w.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition (
                        (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)),
                        (New-Object $PC ($AE::NameProperty, $b)))))
                if ($btn -and $btn.Current.IsEnabled -and -not $btn.Current.IsOffscreen) {
                    Log "  procedura guidata di '$($w.Current.Name)' ($pname): clic su '$b'"
                    Invoke $btn
                    Start-Sleep -Milliseconds 800
                    break
                }
            }
        } catch {}
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
    Invoke (Find-ById $script:win 'DlgConfirmButton')
    Log "Conferma premuta ($label)"
    $state = Wait-Dialog-End 900
    Log "Stato del dialogo: $state"
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
        Invoke (Find-ById $script:win 'DlgDeleteButton')
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
    Invoke (Find-ById $script:win 'DlgResultPrimary')
    Start-Sleep -Seconds 2
    return $title
}

function Wait-Apps-Loaded {
    Wait-For { $t = Text-Of $script:win 'AppsSummary'; $t -and $t -notlike 'Analisi*' } 180 'elenco app' | Out-Null
    Start-Sleep -Seconds 4
}

# ---------------------------------------------------------------- avvio
$before = @(InstallDirs)
Log "App installate con cartella: $($before.Count)"

# App di prova all'avvio
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LindoProvaAvvio' -Value '"C:\Windows\System32\notepad.exe" /nonesiste'

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
Set-Text (Find-ById $script:win 'SearchBox') 'VLC'
Start-Sleep -Seconds 2
Shot 'ricerca-vlc'
$btn = Wait-For { Find-ByName $script:win 'Disinstalla VLC media player' } 20 'pulsante Disinstalla VLC'
Check ($btn -ne $null) "Trovo il pulsante Disinstalla di VLC"
if ($btn) {
    Invoke $btn
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

# ---------------------------------------------------------------- 2. più app insieme
$sel = 0
foreach ($n in '7-Zip*', 'Notepad++*') {
    Set-Text (Find-ById $script:win 'SearchBox') $n.TrimEnd('*').Split(' ')[0]
    Start-Sleep -Seconds 2
    $cb = $script:win.FindFirst($TS::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
    $all = $script:win.FindAll($TS::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
    foreach ($c in $all) { if ($c.Current.Name -like "Seleziona $n") { Toggle-On $c; $sel++; Log "Selezionata: $($c.Current.Name)"; break } }
}
Set-Text (Find-ById $script:win 'SearchBox') ''
Start-Sleep -Seconds 2
Check ($sel -eq 2) "Selezionate 2 app con le caselle"
Shot 'selezione-multipla'
$multi = Find-ById $script:win 'UninstallSelectedButton'
if ($multi) {
    Invoke $multi
    Start-Sleep -Seconds 1
    Shot 'multi-conferma'
    Uninstall-Flow 'multi' | Out-Null
    Check (-not (Installed '7-Zip*')) "7-Zip disinstallato"
    Check (-not (Installed 'Notepad++*')) "Notepad++ disinstallato"
    Check (-not (Test-Path "$env:ProgramFiles\7-Zip")) "Cartella di 7-Zip eliminata"
    Check (-not (Test-Path "$env:ProgramFiles\Notepad++")) "Cartella di Notepad++ eliminata"
}
Wait-Apps-Loaded

# ---------------------------------------------------------------- 3. Residui e verifica
Invoke (Find-ById $script:win 'NavLeftovers')
Start-Sleep -Seconds 1
Invoke (Find-ById $script:win 'ScanOrphansButton')
Wait-For { Visible (Find-ById $script:win 'OrphansActionBar') -or (Visible (Find-ById $script:win 'OrphansClean')) } 300 'analisi residui' | Out-Null
Start-Sleep -Seconds 2
Shot 'residui'
$selAll = Find-ById $script:win 'OrphansSelectAll'
if (Visible $selAll) {
    Toggle-On $selAll
    Start-Sleep -Seconds 1
    Log ("Selezione: " + (Text-Of $script:win 'OrphansSelection'))
    Shot 'residui-selezionati'
    Invoke (Find-ById $script:win 'DeleteOrphansButton')
    Start-Sleep -Seconds 1
    Shot 'residui-conferma'
    Invoke (Find-ById $script:win 'DlgResultPrimary')   # "Elimina"
    Start-Sleep -Seconds 2
    Wait-For { $t = Text-Of $script:win 'DlgResultTitle'; (Visible (Find-ById $script:win 'DlgResultTitle')) -and $t -notlike 'Eliminare*' } 300 'pulizia residui' | Out-Null
    Log ("Risultato: " + (Text-Of $script:win 'DlgResultTitle'))
    Shot 'residui-risultato'
    Invoke (Find-ById $script:win 'DlgResultPrimary')
    Start-Sleep -Seconds 3
}
foreach ($e in ($ExpectOrphans -split ';' | Where-Object { $_ })) { Check (-not (Test-Path $e)) "Residuo finto eliminato: $e" }

# ---------------------------------------------------------------- 4. Ottimizza
$dvrBefore = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -ErrorAction SilentlyContinue).AppCaptureEnabled
$mouseBefore = (Get-ItemProperty 'HKCU:\Control Panel\Mouse').MouseSpeed
Invoke (Find-ById $script:win 'NavOptimize')
Wait-For { $t = Text-Of $script:win 'OptimizeScore'; $t -and $t -ne 'Analisi…' } 60 'analisi ottimizzazioni' | Out-Null
Shot 'ottimizza'
Invoke (Find-ById $script:win 'OptimizeButton')
$st = Wait-Dialog-End 300
Log ("Risultato ottimizzazione: " + (Text-Of $script:win 'DlgResultTitle'))
Shot 'ottimizza-risultato'
Invoke (Find-ById $script:win 'DlgResultPrimary')
Check ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR').AppCaptureEnabled -eq 0) "Registrazione in background di Game Bar disattivata"
Check ((Get-ItemProperty 'HKCU:\Control Panel\Mouse').MouseSpeed -eq '0') "Accelerazione del mouse disattivata"
Check ((powercfg /getactivescheme) -match '8c5e7fda') "Piano energetico Prestazioni elevate attivo"
Start-Sleep -Seconds 2
Invoke (Find-ByName $script:win 'Ripristina tutto')
Start-Sleep -Seconds 8
Shot 'ottimizza-ripristino'
Check ((Get-ItemProperty 'HKCU:\Control Panel\Mouse').MouseSpeed -eq $mouseBefore) "Mouse ripristinato com'era ($mouseBefore)"
$dvrAfter = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR' -ErrorAction SilentlyContinue).AppCaptureEnabled
Check ($dvrAfter -eq $dvrBefore) "Game Bar ripristinata com'era"

# ---------------------------------------------------------------- 5. Avvio
Invoke (Find-ById $script:win 'NavStartup')
$sw = Wait-For { Find-ByName $script:win 'Avvio LindoProvaAvvio' } 120 'app di prova all''avvio'
Shot 'avvio'
Check ($sw -ne $null) "L'app di prova compare tra le app all'avvio"
if ($sw) {
    Invoke $sw
    Start-Sleep -Seconds 3
    $v = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -ErrorAction SilentlyContinue).LindoProvaAvvio
    Check ($v -and ($v[0] -band 1) -eq 1) "Disattivata come fa Gestione attività (StartupApproved = 03)"
    Check ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').LindoProvaAvvio -ne $null) "La voce originale non è stata cancellata"
    Shot 'avvio-disattivata'
    Invoke (Find-ByName $script:win 'Avvio LindoProvaAvvio')
    Start-Sleep -Seconds 3
    $v = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run').LindoProvaAvvio
    Check ($v -and ($v[0] -band 1) -eq 0) "Riattivata"
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LindoProvaAvvio' -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 6. Errori e crash
Start-Process powershell -ArgumentList "-NoProfile -Command [Environment]::FailFast('crash di prova per Lindo')" -Wait -ErrorAction SilentlyContinue
Start-Sleep -Seconds 8
Invoke (Find-ById $script:win 'NavCrashes')
Start-Sleep -Seconds 1
Wait-For { -not (Visible (Find-ById $script:win 'CrashLoading')) } 120 'lettura crash' | Out-Null
Start-Sleep -Seconds 2
Shot 'crash'
$crash = Find-ByName $script:win 'powershell.exe si è chiuso all''improvviso'
Check ($crash -ne $null) "Il crash provocato compare e viene spiegato"

# ---------------------------------------------------------------- verifiche finali
Invoke (Find-ById $script:win 'NavApps')
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
