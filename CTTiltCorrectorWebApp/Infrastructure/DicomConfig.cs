namespace CTTiltCorrector.Infrastructure;

/// <summary>
/// Centralized DICOM networking configuration.
/// Bound from appsettings.json → "Dicom" section.
/// </summary>
public class DicomConfig
{
    public const string SectionName = "Dicom";

    /// <summary>Called AE Title of THIS application (SCP/SCU).</summary>
    public string LocalAeTitle { get; set; } = "CTTILTCORRECTOR";

    /// <summary>TCP port this app listens on for incoming C-STORE (SCP).</summary>
    public int LocalPort { get; set; } = 11112;

    /// <summary>AE Title of the remote ARIA DICOM server (C-FIND / C-MOVE target).</summary>
    public string RemoteAeTitle { get; set; } = "ARIA_AE";

    /// <summary>Hostname or IP of the ARIA DICOM server.</summary>
    public string RemoteHost { get; set; } = "localhost";

    /// <summary>Port of the ARIA DICOM server.</summary>
    public int RemotePort { get; set; } = 104;

    /// <summary>
    /// AE Title that ARIA will push the C-MOVE images to.
    /// Usually the same as <see cref="LocalAeTitle"/>.
    /// </summary>
    public string MoveDestinationAeTitle { get; set; } = "CTTILTCORRECTOR";

    /// <summary>Timeout in seconds for DICOM association requests.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// General application configuration.
/// Bound from appsettings.json → "App" section.
/// </summary>
public class AppConfig
{
    public const string SectionName = "App";

    /// <summary>Path to the SQLite database file.</summary>
    public string DatabasePath { get; set; } = "data/cttiltcorrector.db";

    /// <summary>Root directory where per-run log files are written.</summary>
    public string LogRootPath { get; set; } = "logs/corrections";

    /// <summary>
    /// Active Directory group whose members are allowed to use the application.
    /// Format: DOMAIN\\GroupName
    /// </summary>
    public List<string> AllowedAdGroups { get; set; } = [];
}
