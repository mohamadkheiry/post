using Post.Edge.Application.Contracts;

namespace Post.Edge.Application.Services;

public interface IDeviceConnectionService
{
    Task<Guid> ConnectedAsync(DeviceConnectedRequest request, CancellationToken cancellationToken = default);
    Task<DeviceAuthResponse> AuthenticateAsync(DeviceAuthRequest request, CancellationToken cancellationToken = default);
    Task DisconnectedAsync(DeviceDisconnectedRequest request, CancellationToken cancellationToken = default);
}

public interface ILocalAdministratorService
{
    Task EnsureInitializedAsync(string? initialUserName, string? initialPassword, CancellationToken cancellationToken);
    Task<LocalAdministratorLoginResult?> LoginAsync(
        LocalAdministratorLoginCommand command,
        TimeSpan sessionLifetime,
        CancellationToken cancellationToken);
    Task<string?> AuthenticateSessionAsync(string accessToken, CancellationToken cancellationToken);
    Task LogoutAsync(string accessToken, CancellationToken cancellationToken);
    Task<bool> RotatePasswordAsync(
        RotateLocalAdministratorPasswordCommand command,
        CancellationToken cancellationToken);
}

public interface IInboundRegistrationService
{
    Task<InboundParcelResponse> RegisterAsync(InboundParcelRequest request, CancellationToken cancellationToken = default);
}

public interface IDestinationAssignmentService
{
    Task<DestinationAssignResponse> AssignAsync(DestinationAssignRequest request, CancellationToken cancellationToken = default);
}

public interface IParcelSelectionService
{
    Task<IReadOnlyList<SelectedParcel>> SelectAsync(ParcelSelectionFilter filter, CancellationToken cancellationToken = default);
}

public interface IBagClosingService
{
    Task<CloseBagResponse> CloseAsync(CloseBagRequest request, CancellationToken cancellationToken = default);
}

public interface IDispatchClosingService
{
    Task<CloseDispatchResponse> CloseAsync(CloseDispatchRequest request, CancellationToken cancellationToken = default);
}

public interface IConfigurationSyncService
{
    Task<bool> SyncAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceNetworkBindingAdminService
{
    Task<ReplaceDeviceNetworkBindingsResult> ReplaceAsync(
        ReplaceDeviceNetworkBindingsCommand command,
        CancellationToken cancellationToken = default);
}

public interface IPendingWorkService
{
    Task<PendingWorkResult> ProcessInboundAsync(int batchSize, CancellationToken cancellationToken = default);
    Task<PendingWorkResult> ProcessBagClosuresAsync(int batchSize, CancellationToken cancellationToken = default);
}
