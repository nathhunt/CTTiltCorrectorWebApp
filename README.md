# CT Tilt Corrector

A Blazor Server application for querying CT series from an ARIA DICOM server, passing them through a tilt-correction algorithm entirely in memory, and returning the corrected series to ARIA. No DICOM files are written to disk at any point.

---

## Prerequisites

- .NET 8 SDK
- Windows machine joined to the Active Directory domain (required for Windows Authentication)
- ARIA DICOM server accessible on the local clinical network
- Firewall rule allowing inbound TCP on the DICOM SCP port (default: **11112**)

---

## 1 — Configure appsettings.json

Edit `appsettings.json` before first run. The minimum required changes are the DICOM networking values and your AD groups.

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"
      }
    }
  },
  "Dicom": {
    "LocalAeTitle": "CTTILTCORRECTOR",
    "LocalPort": 11112,
    "RemoteAeTitle": "ARIA_AE",
    "RemoteHost": "192.168.1.100",
    "RemotePort": 104,
    "MoveDestinationAeTitle": "CTTILTCORRECTOR",
    "ConnectionTimeoutSeconds": 30
  },
  "App": {
    "DatabasePath": "data/cttiltcorrector.db",
    "LogRootPath": "logs/corrections",
    "AllowedAdGroups": [
      "DOMAIN\\CT-TiltCorrector-Users"
    ]
  }
}
```

| Setting | Description |
|---|---|
| `Kestrel:Endpoints:Http:Url` | Address and port Kestrel listens on. HTTP only — internal network. |
| `Dicom:LocalAeTitle` | AE Title of this application. Must match what is registered in ARIA. |
| `Dicom:LocalPort` | Port ARIA pushes incoming images to (C-STORE SCP). |
| `Dicom:RemoteAeTitle` | ARIA's AE Title. |
| `Dicom:RemoteHost` | ARIA server IP or hostname. |
| `Dicom:RemotePort` | ARIA DICOM port (typically 104). |
| `Dicom:MoveDestinationAeTitle` | AE Title ARIA sends the C-MOVE response to. Usually the same as `LocalAeTitle`. |
| `App:DatabasePath` | Path to the SQLite database file. Created automatically on first run. |
| `App:LogRootPath` | Root directory for per-run log files. |
| `App:AllowedAdGroups` | AD groups allowed to access the app. User must be in at least one. Leave empty to allow any authenticated domain user (useful for testing). |

---

## 2 — ARIA DICOM Node Registration

Register this application as a DICOM node in ARIA before first use:

| Field | Value |
|---|---|
| AE Title | Value of `Dicom:LocalAeTitle` |
| IP Address | IP of the server running this application |
| Port | Value of `Dicom:LocalPort` |

---

## 3 — Plug In Your Tilt Corrector

Open `YourTiltCorrector.cs` and implement the `CorrectAsync` method:

```csharp
public Task<List<DicomDataset>> CorrectAsync(
    List<DicomDataset> slices,       // all slices, sorted by Instance Number
    IProgress<string> progress,      // report status to the Monitor UI
    CancellationToken ct)
{
    // your algorithm here
    // return the corrected datasets — UIDs and pixel data are your responsibility
}
```

The framework handles everything else: receiving slices from ARIA into memory, calling your function, and sending the returned datasets back to ARIA.

---

## 4 — Database

The SQLite database is created automatically on first startup — no manual steps required. One record is written per correction run containing the Patient ID, Series Instance UID, timestamp, username, job status, and a path to the run log file.

---

## 5 — Running

**Development:**
```bash
dotnet run
```
Navigate to `http://localhost:5000`.

**Production (Windows Service):**
```bash
dotnet publish -c Release -o ./publish
sc create CTTiltCorrector binPath="dotnet C:\path\to\publish\CTTiltCorrector.dll" start=auto
sc start CTTiltCorrector
```

Ensure the service account has write access to the folders defined in `App:DatabasePath` and `App:LogRootPath`.

---

## 6 — AD Group Testing

To allow all domain users during development, set `AllowedAdGroups` to an empty array:

```json
"AllowedAdGroups": []
```

This grants access to any authenticated domain account. Populate with specific groups before going to production.

---

## Architecture

| Component | Technology |
|---|---|
| UI | Blazor Server + MudBlazor v7 |
| Web server | Kestrel (standalone, no IIS) |
| DICOM networking | fo-dicom 5.x |
| Authentication | Windows Negotiate / Kerberos |
| Job processing | `System.Threading.Channels` — single sequential queue |
| Database | SQLite via EF Core 8 |
| Run logs | Per-run text file written to `App:LogRootPath` |

### Pages

| Page | Purpose |
|---|---|
| Search | Query ARIA by Patient ID, view CT series, submit a correction job |
| Monitor | Live scrolling log of the active job streamed in real time |
| History | Searchable table of all previous runs with inline log viewer |

### Job Pipeline

```
User clicks "Correct Tilt"
        │
        ▼
CorrectionJobQueue  (Channel<QueuedJob>, capacity 64)
        │
        ▼  one job at a time
CorrectionJobProcessor  (BackgroundService)
        │
        ├─ store.Expect(seriesUid)        open the store for this series only
        ├─ C-MOVE SCU ───────────────────► ARIA
        │                                    │
        │  C-STORE SCP (HostedService) ◄─────┘
        │  slices held in InMemoryDicomStore
        │  (slices for any other series are dropped)
        │
        ├─ poll until slice count stable (5 s window)
        ├─ Drain() → sorted List<DicomDataset>
        ├─ ITiltCorrector.CorrectAsync()  your algorithm
        ├─ SendToAriaAsync()              C-STORE SCU, up to 3 retries
        └─ persist CorrectionRun → SQLite
```

### Concurrency

Multiple users can search (C-FIND) simultaneously without issue. Correction jobs are queued and processed strictly one at a time — the processor does not dequeue the next job until the current one has fully completed including upload. The `InMemoryDicomStore` only accepts slices for the currently active series; any unexpected slices are silently dropped and logged as warnings. Upload failures retry up to 3 times with a 5-second delay before the job is marked Failed.
