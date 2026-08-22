using Microsoft.Extensions.DependencyInjection;
using Post.Edge.Application.Ports;
using Post.Edge.Application.Services;

namespace Post.Edge.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddEdgeApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IDeviceCredentialVerifier, DeviceCredentialVerifier>();
        services.AddScoped<IDeviceConnectionService, DeviceConnectionService>();
        services.AddScoped<ILocalAdministratorService, LocalAdministratorService>();
        services.AddScoped<IInboundRegistrationService, InboundRegistrationService>();
        services.AddScoped<IDestinationAssignmentService, DestinationAssignmentService>();
        services.AddScoped<IParcelSelectionService, ParcelSelectionService>();
        services.AddScoped<IBagClosingService, BagClosingService>();
        services.AddScoped<IDispatchClosingService, DispatchClosingService>();
        services.AddScoped<IConfigurationSyncService, ConfigurationSyncService>();
        services.AddScoped<IDeviceNetworkBindingAdminService, DeviceNetworkBindingAdminService>();
        services.AddScoped<IPendingWorkService, PendingWorkService>();
        return services;
    }
}
