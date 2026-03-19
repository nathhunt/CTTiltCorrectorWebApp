# CT Tilt Corrector — Setup Guide

## Prerequisites
- .NET 8 SDK
- Windows Server / IIS with Kerberos (for AD Auth)
- ARIA DICOM server accessible on the local clinical network
- fo-dicom compatible DICOM listener port (default: 11112) open in the firewall

---

## 1 — Clone & Configure

Edit `appsettings.json` before first run:

```json
{
  "Dicom": {
    "LocalAeTitle":  "CTTILTCORRECTOR",      // AE Title registered in ARIA
    "LocalPort":     11112,                   // Port ARIA will push images to
    "RemoteAeTitle": "ARIA_AE",               // ARIA's AE Title
    "RemoteHost":    "192.168.1.100",         // ARIA server IP
    "RemotePort":    104                      // ARIA DICOM port
  },
  "App": {
    "RequiredAdGroup": "DOMAIN\\CT-TiltCorrector-Users"
  }
}
```

---

## 2 — Database Migration

```bash
# From the project root:
dotnet ef migrations add InitialCreate
dotnet ef database update
```

Or migrations are applied automatically on startup via `db.Database.Migrate()` in `Program.cs`.

---

## 3 — Run (Development)

```bash
dotnet run
```

---

## 4 — Publish (IIS / Windows Service)

```bash
dotnet publish -c Release -o ./publish

# IIS: Point application pool to ./publish
# Enable Windows Authentication in IIS, disable Anonymous Auth
# Ensure app pool identity has write access to:
#   data/          (SQLite database)
#   logs/          (run logs + DICOM staging)
```

---

## 5 — ARIA DICOM Configuration

Register the application as a DICOM node in the ARIA system:
- AE Title : value of `Dicom:LocalAeTitle`
- IP       : server IP running this application
- Port     : value of `Dicom:LocalPort`

---

## Architecture Notes

| Component              | Technology                                |
|------------------------|-------------------------------------------|
| UI Framework           | Blazor Server + MudBlazor v7              |
| DICOM networking       | fo-dicom 5.x                              |
| Auth                   | Windows Negotiate / Kerberos              |
| Background processing  | `System.Threading.Channels` (serialised)  |
| Database               | SQLite via EF Core 8                      |
| Logging                | Per-run text file + ILogger               |

### Job Queue Flow
```
User clicks "Correct Tilt"
        │
        ▼
CorrectionJobQueue (Channel<QueuedJob>)
        │
        ▼  (single background thread)
CorrectionJobProcessor (BackgroundService)
        │
        ├─ C-MOVE SCU ──────────────────────► ARIA
        │                                       │
        ◄── C-STORE SCP (HostedService) ────────┘
        │
        ├─ Load DICOM datasets
        ├─ CalculateTilt (ImageOrientationPatient)
        ├─ ApplyTiltCorrection (rotation matrix)
        ├─ Save corrected files
        ├─ Upload back to ARIA (C-STORE SCU)
        └─ Persist CorrectionRun to SQLite
```

### Tilt Correction Algorithm
Tilt is derived from the Z-component of the column direction cosine in the
`ImageOrientationPatient` tag. The inverse rotation is applied to both the
`ImageOrientationPatient` and `ImagePositionPatient` tags of each slice.

**Production**: Pixel-level resampling (shear interpolation) should be added
using ITK.NET or a custom trilinear interpolation kernel operating on the
`DicomPixelData` extracted from each slice's `PixelData` tag.
