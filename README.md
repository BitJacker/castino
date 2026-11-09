<p align="center">
  <img src="assets/logo.png" width="112" alt="Logo di Lindo">
</p>

<h1 align="center">Lindo</h1>

<p align="center">
  <b>Disinstalla le app di Windows senza lasciare residui.</b><br>
  Vedi quanto spazio occupa ogni app, la rimuovi con un clic e Lindo ripulisce anche le cartelle,<br>
  i collegamenti e le chiavi di registro che i programmi lasciano in giro.
</p>

<p align="center">
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4">
  <img alt="Dimensione" src="https://img.shields.io/badge/dimensione-~350%20KB-7C5CFF">
  <img alt="Nessun runtime" src="https://img.shields.io/badge/runtime-incluso%20in%20Windows-22D3EE">
  <img alt="Lingua" src="https://img.shields.io/badge/lingua-italiano-34D399">
</p>

---

## Perché

Quando disinstalli un programma da Windows spesso restano cartelle in `Programmi`, `AppData`
e `ProgramData`, collegamenti rotti nel menu Start e voci nel registro. Su un disco quasi pieno
quei residui pesano. **Lindo** trova e rimuove tutto, senza toccare le altre app.

## Schermate

| Applicazioni installate | Conferma di disinstallazione |
|---|---|
| ![Elenco delle app](docs/screenshots/1-applicazioni.png) | ![Dialogo di conferma](docs/screenshots/2-conferma.png) |

| Residui e verifica | Pulizia rapida |
|---|---|
| ![Residui](docs/screenshots/3-residui.png) | ![Pulizia rapida](docs/screenshots/4-pulizia.png) |

> Le schermate vengono scattate automaticamente su una vera macchina Windows dalla
> [CI di GitHub](.github/workflows/build.yml) a ogni modifica.

## Cosa fa

- **Elenco di tutte le app**: programmi desktop e app del Microsoft Store, con icona, editore,
  versione, data di installazione e **spazio realmente occupato sul disco** (misurato, non stimato).
  Puoi ordinarle per peso, cercarle e filtrarle.
- **Disinstallazione completa**, in tre passaggi:
  1. avvia il programma di disinstallazione **ufficiale** dell'app (o la rimozione di Windows per le app dello Store);
  2. cerca i residui: cartella di installazione, dati in `AppData` / `ProgramData`, collegamenti nel menu Start
     e sul Desktop, chiavi di registro e la voce rimasta nell'elenco delle app;
  3. ti mostra cosa ha trovato, con quanto pesa e quanto è sicuro che appartenga a quell'app: **decidi tu cosa eliminare**.
- **Verifica finale**: dopo la pulizia ricontrolla il sistema e ti dice se è rimasto qualcosa, quanto spazio hai liberato
  e se qualche file in uso verrà eliminato al riavvio.
- **Rimozione forzata** per le app "rotte" il cui disinstallatore non esiste più.
- **Residui e verifica**: analizza il PC e trova le cartelle lasciate da programmi **già** disinstallati
  e le voci "fantasma" nell'elenco delle app.
- **Pulizia rapida**: file temporanei, cestino, cache delle miniature, rapporti di errore e pacchetti di Windows Update già installati.

## Sicurezza: come evita di cancellare altro

Ogni eliminazione passa da più controlli, scritti in modo prudente e coperti da test automatici:

- **Solo nelle cartelle dei programmi**: Lindo può eliminare solo dentro `Programmi`, `Programmi (x86)`, `ProgramData`,
  `AppData` (Local, LocalLow, Roaming), menu Start e Desktop. Mai in `Windows`, mai le cartelle radice, mai i tuoi Documenti,
  Download, Immagini, ecc.
- **Cartelle condivise protette**: `Microsoft`, `Common Files`, `WindowsApps`, `Packages` e simili non vengono mai proposte.
- **Rispetto delle altre app**: se una cartella contiene (o sta dentro) quella di un altro programma installato, o se il suo nome
  corrisponde anche a un'altra app, **non viene proposta**. Le cartelle dell'editore (es. `Google` con dentro Chrome e Drive)
  non vengono mai cancellate in blocco: Lindo entra e prende solo la sottocartella giusta.
- **Livelli di affidabilità**: *Sicuro* e *Probabile* sono preselezionati, *Da verificare* no. Nella sezione
  "Residui e verifica" nulla è preselezionato.
- **Registro con copia di sicurezza**: prima di eliminare una chiave Lindo ne salva un backup `.reg` in
  `%LOCALAPPDATA%\Lindo\Backup registro`; basta un doppio clic per ripristinarla. Le chiavi di sistema
  (`Microsoft`, `Windows`, `Classes`, `Policies`…) sono intoccabili.
- **Collegamenti simbolici e junction** non vengono mai seguiti durante l'eliminazione.
- Tutto viene annotato in `%LOCALAPPDATA%\Lindo\lindo.log`.

## Installazione

### ⬇️ Download

- **[Lindo-1.0.0.msi](../../raw/HEAD/download/Lindo-1.0.0.msi)**: installer
- **[Lindo.exe](../../raw/HEAD/download/Lindo.exe)**: versione portatile

Gli stessi file sono anche nella pagina [Releases](../../releases/latest).

| File | Quando usarlo |
|---|---|
| `Lindo-x.y.z.msi` | Installazione classica: collegamenti nel menu Start e sul Desktop, compare tra le app installate. |
| `Lindo.exe` | Versione portatile: nessuna installazione, basta avviarla. |

Lindo chiede i permessi di amministratore all'avvio: servono per disinstallare i programmi e pulire
`Programmi` e `ProgramData`.

**Requisiti:** Windows 10 (versione 1903 o successiva) o Windows 11. Usa .NET Framework 4.8, che è già
incluso in queste versioni di Windows ([Microsoft Learn](https://learn.microsoft.com/dotnet/framework/install/guide-for-developers)),
quindi non serve scaricare nient'altro e l'app occupa circa **350 KB**.

> Windows SmartScreen potrebbe mostrare "PC protetto da Windows" perché l'eseguibile non è firmato digitalmente:
> clicca **Ulteriori informazioni → Esegui comunque**.

## Compilare da soli

Serve il [.NET SDK 8](https://dotnet.microsoft.com/download) (quello ufficiale Microsoft) e, per l'MSI, `wixl`
(pacchetto `wixl` su Ubuntu/Debian). Funziona anche su Linux:

```bash
./build.sh
```

I file finiscono in `dist/`. Su Windows basta `dotnet build src/Lindo -c Release`.

Opzioni da riga di comando, utili per i test:

```bash
Lindo.exe --selftest report.txt      # legge app e residui reali, verifica le regole di sicurezza, NON elimina nulla
Lindo.exe --screenshot cartella      # salva le schermate dell'interfaccia e si chiude
```

## Struttura del progetto

```
src/Lindo.Core/        logica pura (riconoscimento dei residui, regole di sicurezza) — testata
src/Lindo/             app WPF: interfaccia, lettura del registro, disinstallazione, pulizia
tests/Lindo.Core.Tests test automatici (xUnit)
installer/Lindo.wxs    definizione dell'installer MSI
assets/                icona e logo (rigenerabili con assets/make_icon.py)
```
