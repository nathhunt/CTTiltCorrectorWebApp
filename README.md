# CT Tilt Corrector

A Blazor Server application for querying CT series from an ARIA DICOM server, passing them through a tilt-correction algorithm entirely in memory, and returning the corrected series to ARIA. No DICOM files are written to disk at any point.

---

## Prerequisites

- .NET 8 SDK
- Windows machine (or service account) with network access to the Active Directory domain
- ARIA DICOM server accessible on the local clinical network
- Firewall rule allowing inbound TCP on the DICOM SCP port (default: **11112**)
- SimpleITK 2.2.x native libraries (included — no separate install)

---

## 1 — Configure appsettings.json

Edit `appsettings.json` before first run. The minimum required changes are the DICOM networking values and your AD settings.

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
    "Domain": "YOURDOMAIN",
    "LdapServer": "192.168.1.10",
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
| `App:Domain` | Windows domain name prepended to usernames during LDAP authentication (e.g. `HOSPITAL`). |
| `App:LdapServer` | IP or hostname of the LDAP server used for AD group membership lookup. |
| `App:AllowedAdGroups` | AD groups whose members may access the app. User must be in at least one. Leave empty to allow any authenticated domain user (useful for development). |

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

The default implementation in `TiltCorrector.cs` uses SimpleITK resampling to produce a standard-orientation series from a tilted acquisition. To swap in a different algorithm, implement `ITiltCorrector` in a new class and update the registration in `Program.cs`:

```csharp
builder.Services.AddScoped<ITiltCorrector, YourTiltCorrector>();
```

The interface contract:

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

To allow all domain users during development, set `AllowedAdGroups` to an empty array and leave `Domain` and `LdapServer` empty:

```json
"App": {
  "Domain": "",
  "LdapServer": "",
  "AllowedAdGroups": []
}
```

This grants access to any authenticated domain account without performing LDAP group checks. Populate all three fields before going to production.

---

## Architecture

| Component | Technology |
|---|---|
| UI | Blazor Server + MudBlazor v7 |
| Web server | Kestrel (standalone, no IIS) |
| DICOM networking | fo-dicom 5.x |
| Authentication | Windows Negotiate / Kerberos + cookie |
| Job processing | `System.Threading.Channels` — single sequential queue |
| Database | SQLite via EF Core 8 |
| Image resampling | SimpleITK 2.2.x |
| Run logs | Per-run text file written to `App:LogRootPath` |

### Pages

| Page | Purpose |
|---|---|
| Search | Query ARIA by Patient ID, view CT series, submit a correction job |
| Monitor | Live scrolling log of the active job streamed in real time |
| History | Searchable table of all previous runs with inline log viewer |

---

### Job Pipeline

```mermaid
flowchart TD
    A([User clicks Correct Tilt on Search page]) --> B

    B["CorrectionJobQueue
    Channel&lt;QueuedJob&gt; — capacity 64
    Deduplicates by SeriesInstanceUID"]

    B -->|one job at a time| C

    C["CorrectionJobProcessor
    BackgroundService
    Creates a DI scope per job"]

    C --> D["store.Expect(seriesUid)
    Open the store for this series only"]

    D --> E["C-MOVE SCU
    Request series from ARIA"]

    E -->|ARIA pushes slices via C-STORE| F

    F["DicomStoreScp
    C-STORE SCP — HostedService
    No disk writes"]

    F --> G["InMemoryDicomStore
    Accumulate datasets in memory
    Slices for other series are dropped"]

    G --> H{Wait strategy}

    H -->|"NumberOfSeriesRelatedInstances > 0"| I["Wait for exact count
    polling every 1 s"]

    H -->|"Count unavailable"| J["Stability window
    wait 5 s with no change"]

    I --> K["store.Drain()
    Remove + sort by Instance Number"]
    J --> K

    K --> L["ITiltCorrector.CorrectAsync()
    Your algorithm — runs on thread pool"]

    subgraph "Correction Algorithm (TiltCorrector)"
        L --> L1["DicomSeriesLoader.Load()
        Validate series, sort by IPP-Z,
        compute tilt normal"]
        L1 --> L2{Already identity IOP?}
        L2 -->|"IOP = 1\0\0\0\1\0"| L3([Return slices unchanged])
        L2 -->|tilted| L4["SliceSpacingCalculator.Compute()
        Use SliceThickness tag
        Fallback: median IPP gap"]
        L4 --> L5["SimpleItkResampler.Resample()
        Build ITK volume with tilt direction
        Compute AABB via VolumeGeometry
        Resample to identity orientation
        Linear interpolation"]
        L5 --> L6["DicomSeriesWriter.BuildDatasets()
        New SeriesInstanceUID + SOPInstanceUIDs
        Identity IOP, corrected IPP per slice
        Copy non-geometry tags from nearest ref slice
        Inverse rescale → stored pixel values"]
        L6 --> L7([Return corrected datasets])
    end

    L3 --> M
    L7 --> M

    M["C-STORE SCU
    Upload corrected series to ARIA
    Up to 3 retries with 5 s delay"]

    M --> N[("SQLite
    Persist CorrectionRun
    status / log path")]

    M --> O([Job complete
    Monitor page shows final status])
```

---

### ARIA Query Pipeline (Search Page)

Each patient search runs a multi-tier filter chain to surface only diagnostic CT series, suppressing RT objects, scouts, DRRs, 4DCT, and CBCT.

```mermaid
flowchart LR
    A([Patient ID entered]) --> B

    B["Study C-FIND
    Patient demographics
    + all StudyInstanceUIDs"]

    B --> C["Series C-FIND per study
    Candidate series metadata
    + NumberOfSeriesRelatedInstances"]

    C --> D{"Modality = CT?"}
    D -->|no| X1([Excluded])
    D -->|yes| E

    E{"SOP class / description
    pre-filter"}

    E -->|"RT / 4DCT / CBCT / CBCT"| X2([Excluded])
    E -->|pass| F

    F["Image C-FIND per series
    Actual instance count
    + ProtocolName fallback"]

    F --> G{"≥ 10 images?
    (after excluding RT / SC SOPs)"}
    G -->|no| X3([Excluded scouts/DRRs])
    G -->|yes| H([Shown in UI])
```

---

### Concurrency

Multiple users can search (C-FIND) simultaneously without issue. Correction jobs are queued and processed strictly one at a time — the processor does not dequeue the next job until the current one has fully completed including upload.

The `InMemoryDicomStore` only accepts slices for the currently active series (registered via `Expect()`); any unexpected slices are silently dropped and logged as warnings. This prevents a queued job's C-MOVE response from contaminating the store while a prior job is still running.

Upload failures retry up to 3 times with a 5-second delay before the job is marked Failed.
