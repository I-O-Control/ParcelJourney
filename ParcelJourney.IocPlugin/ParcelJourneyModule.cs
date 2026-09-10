using IocOrchestrator.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ParcelHistoryExplorer.Core;
using ParcelJourney.Core;
using ParcelJourney.Domain;

namespace ParcelJourney.IocPlugin;

public sealed class ParcelJourneyModule : IocModuleBase
{
    public const string PageKey = "ParcelJourney";
    public override string Id => "parcel-journey";
    public override string DisplayName => "Parcel Journey";

    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<LogHistoryQueryEngine>();
        services.AddSingleton<IParcelJourneyBuilder, ParcelJourneyBuilder>();
    }

    public override void ContributeNavigation(INavigationRegistry navigation) =>
        navigation.Register(new ModuleNavEntry(PageKey, DisplayName, "bi-box-seam", "/plugins/parcel-journey", "Logistics"));
}
