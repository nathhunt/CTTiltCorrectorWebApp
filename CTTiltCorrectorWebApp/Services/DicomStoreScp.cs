using CTTiltCorrector.Infrastructure;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Options;
using System.Text;

namespace CTTiltCorrector.Services;

// ─── HostedService ────────────────────────────────────────────────────────────

/// <summary>
/// Background C-STORE SCP. Listens for DICOM images pushed by ARIA in response
/// to a C-MOVE request and holds them in <see cref="InMemoryDicomStore"/>.
/// No files are ever written to disk.
/// </summary>
public class DicomStoreScp : IHostedService
{
    private readonly DicomConfig _cfg;
    private readonly ILogger<DicomStoreScp> _logger;
    private readonly InMemoryDicomStore _store;
    private IDicomServer? _server;

    public DicomStoreScp(
        IOptions<DicomConfig> cfg,
        ILogger<DicomStoreScp> logger,
        InMemoryDicomStore store)
    {
        _cfg    = cfg.Value;
        _logger = logger;
        _store  = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting DICOM SCP — AE={Ae} Port={Port}", _cfg.LocalAeTitle, _cfg.LocalPort);

        _server = DicomServerFactory.Create<CStoreScp>(
            _cfg.LocalPort,
            userState: new ScpContext(_cfg, _logger, _store));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping DICOM SCP.");
        _server?.Dispose();
        return Task.CompletedTask;
    }
}

// ─── Per-association context ──────────────────────────────────────────────────

internal record ScpContext(DicomConfig Config, ILogger Logger, InMemoryDicomStore Store);

// ─── C-STORE SCP handler (one instance per DICOM association) ────────────────

public class CStoreScp : DicomService, IDicomServiceProvider, IDicomCStoreProvider
{
    private static readonly DicomTransferSyntax[] AcceptedSyntaxes =
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
    };

    private readonly ScpContext _ctx;

    public CStoreScp(INetworkStream stream, Encoding fallbackEncoding,
        ILogger log, DicomServiceDependencies deps)
        : base(stream, fallbackEncoding, log, deps)
    {
        _ctx = (ScpContext)UserState!;
    }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
            foreach (var ts in AcceptedSyntaxes)
                pc.SetResult(DicomPresentationContextResult.Accept, ts);

        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync()
        => SendAssociationReleaseResponseAsync();

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        => _ctx.Logger.LogWarning("DICOM abort — Source={S} Reason={R}", source, reason);

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception is not null)
            _ctx.Logger.LogError(exception, "DICOM connection closed with error.");
    }

    /// <summary>
    /// Clones each incoming dataset into the in-memory store.
    /// The clone decouples the dataset from the underlying network buffer
    /// so it remains valid after the association closes.
    /// </summary>
    public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        try
        {
            var seriesUid = request.Dataset
                .GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "UnknownSeries");

            var accepted = _ctx.Store.Add(seriesUid, request.Dataset.Clone());

            if (accepted)
                _ctx.Logger.LogDebug(
                    "C-STORE received SOP={Sop} Series={Series}",
                    request.SOPInstanceUID.UID, seriesUid);
            else
                _ctx.Logger.LogWarning(
                    "C-STORE dropped unexpected SOP={Sop} Series={Series} — not registered",
                    request.SOPInstanceUID.UID, seriesUid);

            return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "C-STORE handler faulted.");
            return Task.FromResult(
                new DicomCStoreResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        _ctx.Logger.LogError(e, "C-STORE exception.");
        return Task.CompletedTask;
    }
}

// ─── In-memory slice store (singleton) ───────────────────────────────────────

/// <summary>
/// Thread-safe singleton that accumulates received <see cref="DicomDataset"/>
/// objects in memory, keyed by Series Instance UID.
///
/// Only series explicitly registered via <see cref="Expect"/> will accept
/// incoming slices. This prevents a queued job's C-MOVE response from landing
/// in the store while a prior job is still being processed, which would cause
/// mixed or contaminated datasets.
/// </summary>
public class InMemoryDicomStore
{
    private readonly Dictionary<string, List<DicomDataset>> _store = new();
    private readonly HashSet<string> _expected = new();
    private readonly object _lock = new();

    /// <summary>
    /// Registers a series UID as expected before the C-MOVE is issued.
    /// Slices will be silently dropped for any series not registered here.
    /// </summary>
    public void Expect(string seriesUid)
    {
        lock (_lock)
        {
            _expected.Add(seriesUid);
            _store.TryAdd(seriesUid, new List<DicomDataset>());
        }
    }

    /// <summary>
    /// Adds a received dataset. Returns false and discards if the series
    /// was not registered via <see cref="Expect"/> (stale or unexpected push).
    /// </summary>
    public bool Add(string seriesUid, DicomDataset dataset)
    {
        lock (_lock)
        {
            if (!_expected.Contains(seriesUid))
                return false;

            _store[seriesUid].Add(dataset);
            return true;
        }
    }

    /// <summary>Returns the current slice count without removing anything.</summary>
    public int Count(string seriesUid)
    {
        lock (_lock)
            return _store.TryGetValue(seriesUid, out var l) ? l.Count : 0;
    }

    /// <summary>
    /// Atomically removes and returns all buffered datasets for the series,
    /// sorted ascending by Instance Number. Also unregisters the series.
    /// </summary>
    public List<DicomDataset> Drain(string seriesUid)
    {
        lock (_lock)
        {
            _expected.Remove(seriesUid);
            if (!_store.Remove(seriesUid, out var datasets))
                return new List<DicomDataset>();

            datasets.Sort((a, b) =>
                a.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0)
                 .CompareTo(b.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0)));

            return datasets;
        }
    }

    /// <summary>Discards all buffered data for a series (cancellation / error).</summary>
    public void Discard(string seriesUid)
    {
        lock (_lock)
        {
            _expected.Remove(seriesUid);
            _store.Remove(seriesUid);
        }
    }
}
