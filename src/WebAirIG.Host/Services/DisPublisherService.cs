using Microsoft.Extensions.Options;
using WebAirIG.Dis;
using WebAirIG.Host.Options;

namespace WebAirIG.Host.Services;

public interface IDisStatus
{
    bool Enabled { get; }
    long PdusSent { get; }
    string Destination { get; }
}

/// <summary>Publishes a DIS 7 Entity State PDU for every simulated entity at a fixed rate.</summary>
public sealed class DisPublisherService : BackgroundService, IDisStatus
{
    private readonly DisOptions _opt;
    private readonly ISimulationEngine _engine;
    private readonly ISharedConfigService _config;
    private readonly ILogger<DisPublisherService> _log;
    private DisUdpPublisher? _publisher;

    public bool Enabled => _opt.Enabled;
    public long PdusSent => _publisher?.PdusSent ?? 0;
    public string Destination => $"{_opt.Address}:{_opt.Port} (exercise {_opt.ExerciseId}, site {_opt.SiteId}, app {_opt.ApplicationId})";

    public DisPublisherService(IOptions<WebAirIgOptions> options, ISimulationEngine engine, ISharedConfigService config, ILogger<DisPublisherService> log)
    {
        _opt = options.Value.Dis;
        _engine = engine;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("DIS output disabled");
            return;
        }
        _publisher = new DisUdpPublisher(_opt.Address, _opt.Port);
        _log.LogInformation("DIS output to {Dest}", Destination);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Max(0.1, _opt.RateHz)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var snap = _engine.Latest;
                var now = DateTime.UtcNow;
                foreach (var e in snap.Entities)
                {
                    var type = _config.Catalog.Find(e.TypeId)?.Dis;
                    var ecef = WebAirIG.Common.Geo.Wgs84.GeodeticToEcef(e.Geo);
                    var nedFromBody = WebAirIG.Common.Geo.Mat3d.FromYawPitchRoll(
                        e.YawDeg * Math.PI / 180, e.PitchDeg * Math.PI / 180, e.RollDeg * Math.PI / 180);
                    var (psi, theta, phi) = DisConversions.OrientationFromLocalNed(e.Geo, nedFromBody);
                    _publisher.Send(new EntityStatePdu
                    {
                        ExerciseId = _opt.ExerciseId,
                        Timestamp = DisConversions.AbsoluteTimestamp(now),
                        EntityId = new DisEntityId(_opt.SiteId, _opt.ApplicationId, (ushort)(e.EntityId + 1)),
                        ForceId = e.Force,
                        EntityType = type is null ? default : new DisEntityType(type.Kind, type.Domain, type.Country, type.Category, type.Subcategory, type.Specific, type.Extra),
                        LinearVelocity = DisConversions.VelocityEcef(e.Geo, e.VelNedLocal),
                        LocationX = ecef.X, LocationY = ecef.Y, LocationZ = ecef.Z,
                        Psi = psi, Theta = theta, Phi = phi,
                        DeadReckoningAlgorithm = 2,
                        Marking = e.Name,
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _publisher.Dispose();
        }
    }
}
