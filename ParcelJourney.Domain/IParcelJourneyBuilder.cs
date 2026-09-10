namespace ParcelJourney.Domain;

public interface IParcelJourneyBuilder
{
    Task<ParcelJourney> BuildAsync(JourneyQuery query, JourneyBuildOptions? options = null, CancellationToken cancellationToken = default);
}
