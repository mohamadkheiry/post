using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Devices;
using Post.Core.Application.Operations;
using Post.Core.Application.Parcels;
using Post.Core.Application.Security;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application;

public enum CoreApplicationProfile
{
    All = 0,
    ParcelLifecycle = 1,
    DeviceConfig = 2,
    DataIngestion = 3,
    ObjectStorage = 4,
    EdgeHealth = 5,
    OperationalResult = 6,
    Identity = 7
}

public static class DependencyInjection
{
    public static IServiceCollection AddCoreApplication(
        this IServiceCollection services,
        CoreApplicationProfile profile = CoreApplicationProfile.All)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(BarcodeValidationPolicy.Default);
        services.TryAddSingleton(OperationalSettings.Default);
        services.TryAddSingleton<BarcodeValidator>();
        services.TryAddSingleton<IDuplicateReadingClassifier, DuplicateReadingClassifier>();
        services.TryAddSingleton<IDiscrepancyChecker, DiscrepancyChecker>();
        services.TryAddSingleton<ICoreDeviceStatusMapper, CoreDeviceStatusMapper>();
        services.TryAddSingleton<IRequestHasher, CanonicalJsonRequestHasher>();
        services.TryAddSingleton<ICorrelationService, CorrelationService>();
        services.TryAddSingleton<IIdempotencyService, IdempotencyService>();

        if (profile is CoreApplicationProfile.All or CoreApplicationProfile.DataIngestion)
        {
            services.AddScoped<IInboundValidationService, InboundValidationService>();
        }

        if (profile is CoreApplicationProfile.All or CoreApplicationProfile.ParcelLifecycle)
        {
            services.AddScoped<IInboundQueryService, InboundQueryService>();
            services.AddScoped<IParcelEventConsumer, ParcelEventConsumer>();
            services.AddScoped<IAttachmentLinkingService, AttachmentLinkingService>();
            services.AddScoped<IParcelTrackingQueryService, ParcelTrackingQueryService>();
            services.AddScoped<IBagStorageService, BagStorageService>();
            services.AddScoped<IDispatchStorageService, DispatchStorageService>();
            services.AddScoped<IBagDispatchQueryService, BagDispatchQueryService>();
        }

        if (profile is CoreApplicationProfile.All or CoreApplicationProfile.DeviceConfig)
        {
            services.AddScoped<IDeviceManagementService, DeviceManagementService>();
            services.AddScoped<IDeviceQueryService, DeviceQueryService>();
            services.AddScoped<IConfigurationService, ConfigurationService>();
            services.AddScoped<IConfigurationQueryService, ConfigurationQueryService>();
        }

        if (profile is CoreApplicationProfile.All or CoreApplicationProfile.ObjectStorage)
        {
            services.AddScoped<IPresignedUrlService, PresignedUrlService>();
        }

        if (profile is CoreApplicationProfile.All or CoreApplicationProfile.EdgeHealth)
        {
            services.AddScoped<IEdgeHealthService, EdgeHealthService>();
            services.AddScoped<IEdgeHealthMonitor, EdgeHealthMonitor>();
        }

        if (profile is CoreApplicationProfile.All or CoreApplicationProfile.OperationalResult)
        {
            services.AddScoped<IOperationalResultService, OperationalResultService>();
        }

        if (profile is CoreApplicationProfile.All or CoreApplicationProfile.Identity)
        {
            services.AddScoped<IEdgeAuthService, EdgeAuthService>();
            services.AddScoped<IAdminAuthService, AdminAuthService>();
            services.AddScoped<IAdminProvisioningService, AdminProvisioningService>();
            services.AddScoped<IAuditService, AuditService>();
        }

        if (profile is CoreApplicationProfile.All or
            CoreApplicationProfile.ParcelLifecycle or
            CoreApplicationProfile.DeviceConfig or
            CoreApplicationProfile.EdgeHealth or
            CoreApplicationProfile.OperationalResult or
            CoreApplicationProfile.Identity)
        {
            services.AddScoped<IOutboxPublisher, OutboxPublisher>();
        }

        return services;
    }
}
