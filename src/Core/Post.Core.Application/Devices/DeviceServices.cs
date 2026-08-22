using System.Text.Json;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Devices;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application.Devices;

public sealed class DeviceManagementService : IDeviceManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDeviceConfigStore _store;
    private readonly ISecretHasher _secretHasher;
    private readonly IRequestHasher _requestHasher;
    private readonly IIdempotencyService _idempotencyService;
    private readonly TimeProvider _timeProvider;

    public DeviceManagementService(
        IDeviceConfigStore store,
        ISecretHasher secretHasher,
        IRequestHasher requestHasher,
        IIdempotencyService idempotencyService,
        TimeProvider timeProvider)
    {
        _store = store;
        _secretHasher = secretHasher;
        _requestHasher = requestHasher;
        _idempotencyService = idempotencyService;
        _timeProvider = timeProvider;
    }

    public async Task<RegisteredDeviceResult> RegisterAsync(
        RegisterDeviceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        IdempotencyRecord? idempotency = null;
        string? requestHash = null;
        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var key = new IdempotencyKey(command.IdempotencyKey);
            requestHash = _requestHasher.Hash(new
            {
                Operation = "device.register",
                command.Name,
                command.DeviceType,
                command.LogicalCode,
                command.Owner,
                command.ExchangeCenterCode,
                command.Description
            });
            idempotency = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
            var decision = _idempotencyService.Evaluate(idempotency, requestHash);
            if (decision == IdempotencyDecision.Replay)
            {
                return Replay(idempotency!);
            }

            if (decision == IdempotencyDecision.InProgress)
            {
                throw new CoreConflictException("Device registration with this idempotency key is still in progress.");
            }

            idempotency = IdempotencyRecord.Start(key, requestHash, _timeProvider.GetUtcNow());
        }

        var logicalCode = new LogicalDeviceCode(command.LogicalCode);
        if (await _store.FindDeviceByLogicalCodeAsync(logicalCode, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new CoreConflictException("Logical device code must be globally unique.");
        }

        var rawSecret = _secretHasher.GenerateSecret();
        var device = SortingDevice.Create(
            DeviceId.New(),
            command.Name,
            command.DeviceType,
            logicalCode,
            command.Owner,
            new ExchangeCenterCode(command.ExchangeCenterCode),
            command.Description ?? string.Empty,
            _secretHasher.Hash(rawSecret));
        _store.AddDevice(device);
        if (idempotency is not null)
        {
            idempotency.Complete(
                JsonSerializer.Serialize(new SecretOperationReceipt(device.Id.Value), JsonOptions),
                _timeProvider.GetUtcNow());
            _store.AddIdempotency(idempotency);
        }

        _store.AddOutbox(OutboxMessage.Create(
            "device.registered",
            JsonSerializer.Serialize(new
            {
                DeviceId = device.Id.Value,
                LogicalCode = device.LogicalCode.Value,
                Center = device.ExchangeCenterCode.Value,
                Status = device.ActivationStatus
            }),
            new CorrelationId(command.CorrelationId),
            _timeProvider.GetUtcNow()));

        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Logical device code must be globally unique.", exception);
        }

        // The raw secret is returned once. Only its PBKDF2 verifier is persisted.
        return new RegisteredDeviceResult(device.Id.Value, rawSecret);
    }

    public async Task<RegisteredDeviceResult> RotateSecretAsync(
        RotateDeviceSecretCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = new IdempotencyKey(command.IdempotencyKey);
        var requestHash = _requestHasher.Hash(new { Operation = "device.rotate-secret", command.DeviceId });
        var existing = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(existing, requestHash);
        if (decision == IdempotencyDecision.Replay)
        {
            return Replay(existing!);
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("Device secret rotation with this idempotency key is still in progress.");
        }

        var device = await _store.FindDeviceAsync(new DeviceId(command.DeviceId), cancellationToken)
            .ConfigureAwait(false) ?? throw new CoreNotFoundException("Sorting device does not exist.");
        var rawSecret = _secretHasher.GenerateSecret();
        var now = _timeProvider.GetUtcNow();
        device.RotateSecretVerifier(
            _secretHasher.Hash(rawSecret),
            now,
            command.GracePeriod ?? SortingDevice.DefaultSecretRotationGrace);
        var latest = await _store.GetLatestSnapshotAsync(device.ExchangeCenterCode, cancellationToken).ConfigureAwait(false);
        if (latest is not null)
        {
            var devices = await _store.ListDevicesAsync(device.ExchangeCenterCode, cancellationToken).ConfigureAwait(false);
            _store.AddSnapshot(ConfigurationSnapshot.Create(
                device.ExchangeCenterCode,
                latest.Version.Next(),
                now,
                devices,
                latest.PostalCodes.Select(x => x.PostalCode),
                latest.Settings));
        }

        var idempotency = IdempotencyRecord.Start(key, requestHash, now);
        idempotency.Complete(
            JsonSerializer.Serialize(new SecretOperationReceipt(device.Id.Value), JsonOptions),
            now);
        _store.AddIdempotency(idempotency);
        _store.AddOutbox(OutboxMessage.Create(
            "device.secret-rotated",
            JsonSerializer.Serialize(new { command.DeviceId, command.Actor }),
            new CorrelationId(command.CorrelationId),
            _timeProvider.GetUtcNow()));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Device secret rotation conflicts with an already committed operation.", exception);
        }

        return new RegisteredDeviceResult(device.Id.Value, rawSecret);
    }

    public async Task UpdateAsync(UpdateDeviceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = new IdempotencyKey(command.IdempotencyKey);
        var requestHash = _requestHasher.Hash(new
        {
            Operation = "device.update",
            command.DeviceId,
            command.Name,
            command.DeviceType,
            command.Owner,
            command.Description
        });
        var existing = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(existing, requestHash);
        if (decision == IdempotencyDecision.Replay)
        {
            return;
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("Device update with this idempotency key is still in progress.");
        }

        var device = await _store.FindDeviceAsync(new DeviceId(command.DeviceId), cancellationToken)
            .ConfigureAwait(false) ?? throw new CoreNotFoundException("Sorting device does not exist.");
        device.UpdateDescriptiveInformation(command.Name, command.DeviceType, command.Owner, command.Description ?? string.Empty);
        var now = _timeProvider.GetUtcNow();
        var idempotency = IdempotencyRecord.Start(key, requestHash, now);
        idempotency.Complete("{}", now);
        _store.AddIdempotency(idempotency);
        _store.AddOutbox(OutboxMessage.Create(
            "device.updated",
            JsonSerializer.Serialize(new { command.DeviceId, command.Actor }),
            new CorrelationId(command.CorrelationId),
            now));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Device update conflicts with an already committed operation.", exception);
        }
    }

    public async Task SetActivationAsync(
        Guid deviceId,
        bool active,
        string idempotencyKey,
        string actor,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var key = new IdempotencyKey(idempotencyKey);
        var requestHash = _requestHasher.Hash(new { Operation = "device.set-activation", DeviceId = deviceId, Active = active });
        var existing = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(existing, requestHash);
        if (decision == IdempotencyDecision.Replay)
        {
            return;
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("Device activation change with this idempotency key is still in progress.");
        }

        var id = new DeviceId(deviceId);
        var device = await _store.FindDeviceAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new CoreNotFoundException("Sorting device does not exist.");

        if (active)
        {
            device.Activate();
        }
        else
        {
            device.Deactivate();
        }

        var latest = await _store.GetLatestSnapshotAsync(device.ExchangeCenterCode, cancellationToken).ConfigureAwait(false);
        if (latest is not null)
        {
            var devices = await _store.ListDevicesAsync(device.ExchangeCenterCode, cancellationToken).ConfigureAwait(false);
            var snapshot = ConfigurationSnapshot.Create(
                device.ExchangeCenterCode,
                latest.Version.Next(),
                _timeProvider.GetUtcNow(),
                devices,
                latest.PostalCodes.Select(x => x.PostalCode),
                latest.Settings);
            _store.AddSnapshot(snapshot);
        }

        var now = _timeProvider.GetUtcNow();
        var idempotency = IdempotencyRecord.Start(key, requestHash, now);
        idempotency.Complete("{}", now);
        _store.AddIdempotency(idempotency);
        _store.AddOutbox(OutboxMessage.Create(
            active ? "device.activated" : "device.deactivated",
            JsonSerializer.Serialize(new { DeviceId = id.Value, Actor = actor }),
            new CorrelationId(correlationId),
            now));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Device activation change conflicts with an already committed operation.", exception);
        }
    }

    private static RegisteredDeviceResult Replay(IdempotencyRecord record)
    {
        var receipt = JsonSerializer.Deserialize<SecretOperationReceipt>(record.ResponseSnapshot, JsonOptions)
            ?? throw new CoreConflictException("Stored device provisioning receipt is invalid.");
        return new RegisteredDeviceResult(receipt.DeviceId, null, true, true);
    }

    private sealed record SecretOperationReceipt(Guid DeviceId);
}

public sealed class ConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDeviceConfigStore _store;
    private readonly IRequestHasher _requestHasher;
    private readonly IIdempotencyService _idempotencyService;
    private readonly TimeProvider _timeProvider;

    public ConfigurationService(
        IDeviceConfigStore store,
        IRequestHasher requestHasher,
        IIdempotencyService idempotencyService,
        TimeProvider timeProvider)
    {
        _store = store;
        _requestHasher = requestHasher;
        _idempotencyService = idempotencyService;
        _timeProvider = timeProvider;
    }

    public async Task<ConfigurationSnapshotDto> PublishAsync(
        PublishConfigurationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.PostalCodes);
        var center = new ExchangeCenterCode(command.ExchangeCenterCode);
        var key = new IdempotencyKey(command.IdempotencyKey);
        var requestHash = _requestHasher.Hash(new
        {
            Operation = "configuration.publish",
            ExchangeCenterCode = center.Value,
            PostalCodes = command.PostalCodes.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            command.ShortReadingThreshold,
            command.ReturnThreshold,
            command.WeightToleranceKg,
            command.DimensionToleranceCm,
            command.AutoDispatchEnabled,
            MaximumOpenBagDuration = command.MaximumOpenBagDuration ??
                                     OperationalSettings.DefaultMaximumOpenBagDuration
        });
        var existing = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(existing, requestHash);
        if (decision == IdempotencyDecision.Replay)
        {
            return JsonSerializer.Deserialize<ConfigurationSnapshotDto>(existing!.ResponseSnapshot, JsonOptions)
                ?? throw new CoreConflictException("Stored configuration publication response is invalid.");
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("Configuration publication with this idempotency key is still in progress.");
        }

        var devices = await _store.ListDevicesAsync(center, cancellationToken).ConfigureAwait(false);
        var previous = await _store.GetLatestSnapshotAsync(center, cancellationToken).ConfigureAwait(false);
        var version = previous?.Version.Next() ?? new ConfigurationVersion(1);
        var settings = new OperationalSettings(
            command.ShortReadingThreshold,
            command.ReturnThreshold,
            new DiscrepancyTolerance(command.WeightToleranceKg, command.DimensionToleranceCm),
            command.AutoDispatchEnabled,
            command.MaximumOpenBagDuration);
        var snapshot = ConfigurationSnapshot.Create(
            center,
            version,
            _timeProvider.GetUtcNow(),
            devices,
            command.PostalCodes.Select(x => new ExchangeCenterCode(x)),
            settings);
        var result = Map(snapshot);
        var now = _timeProvider.GetUtcNow();
        _store.AddSnapshot(snapshot);
        var idempotency = IdempotencyRecord.Start(key, requestHash, now);
        idempotency.Complete(JsonSerializer.Serialize(result, JsonOptions), now);
        _store.AddIdempotency(idempotency);
        _store.AddOutbox(OutboxMessage.Create(
            "config.published",
            JsonSerializer.Serialize(new
            {
                Center = center.Value,
                Version = version.Value,
                Actor = command.Actor
            }),
            new CorrelationId(command.CorrelationId),
            now));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Configuration publication conflicts with an already committed operation.", exception);
        }

        return result;
    }

    public async Task<ConfigurationSnapshotDto> GetBootstrapAsync(
        string exchangeCenterCode,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _store.GetLatestSnapshotAsync(
            new ExchangeCenterCode(exchangeCenterCode),
            cancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? throw new CoreNotFoundException("No configuration snapshot exists for the exchange center.")
            : Map(snapshot);
    }

    internal static ConfigurationSnapshotDto Map(ConfigurationSnapshot snapshot) =>
        new(
            snapshot.ExchangeCenterCode.Value,
            snapshot.Version.Value,
            snapshot.GeneratedAt,
            snapshot.Devices.Select(x => new ConfigurationDeviceDto(
                x.DeviceId.Value,
                x.LogicalCode.Value,
                x.Name,
                x.DeviceType,
                x.ActivationStatus,
                x.SecretAlgorithm,
                x.SecretIterations,
                x.SecretSalt,
                x.SecretHash,
                x.PreviousSecretAlgorithm,
                x.PreviousSecretIterations,
                x.PreviousSecretSalt,
                x.PreviousSecretHash,
                x.PreviousSecretValidUntil)).ToArray(),
            snapshot.PostalCodes.Select(x => x.PostalCode.Value).ToArray(),
            snapshot.Settings.ShortReadingThreshold,
            snapshot.Settings.ReturnThreshold,
            snapshot.Settings.DiscrepancyTolerance.WeightKg,
            snapshot.Settings.DiscrepancyTolerance.DimensionCm,
            snapshot.Settings.AutoDispatchEnabled,
            checked((int)snapshot.Settings.MaximumOpenBagDuration.TotalMinutes));
}

public sealed class DeviceQueryService(IDeviceConfigStore store) : IDeviceQueryService
{
    public async Task<IReadOnlyCollection<DeviceDto>> ListAsync(
        AccessScope accessScope,
        string? exchangeCenterCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        var requestedCenter = string.IsNullOrWhiteSpace(exchangeCenterCode)
            ? (ExchangeCenterCode?)null
            : new ExchangeCenterCode(exchangeCenterCode);
        IReadOnlyCollection<ExchangeCenterCode>? centers;
        if (accessScope.IsGlobal)
        {
            centers = requestedCenter is null ? null : [requestedCenter.Value];
        }
        else
        {
            var allowed = accessScope.AllowedCenterCodes.Select(x => new ExchangeCenterCode(x)).ToArray();
            if (requestedCenter is { } requested && !allowed.Contains(requested))
            {
                return Array.Empty<DeviceDto>();
            }

            centers = requestedCenter is null ? allowed : [requestedCenter.Value];
        }

        var devices = await store.QueryDevicesAsync(centers, cancellationToken).ConfigureAwait(false);
        return devices.Select(Map).ToArray();
    }

    public async Task<PagedResult<DeviceDto>> ListPageAsync(
        AccessScope accessScope,
        string? exchangeCenterCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        ValidatePage(page, pageSize);
        var centers = ResolveCenters(accessScope, exchangeCenterCode, out var authorized);
        if (!authorized)
        {
            return new PagedResult<DeviceDto>([], 0);
        }

        var result = await store.QueryDevicesPageAsync(
            centers,
            checked((page - 1) * pageSize),
            pageSize,
            cancellationToken).ConfigureAwait(false);
        return new PagedResult<DeviceDto>(result.Items.Select(Map).ToArray(), result.TotalCount);
    }

    private static ExchangeCenterCode[]? ResolveCenters(
        AccessScope accessScope,
        string? exchangeCenterCode,
        out bool authorized)
    {
        var requestedCenter = string.IsNullOrWhiteSpace(exchangeCenterCode)
            ? (ExchangeCenterCode?)null
            : new ExchangeCenterCode(exchangeCenterCode);
        authorized = true;
        if (accessScope.IsGlobal)
        {
            return requestedCenter is null ? null : [requestedCenter.Value];
        }

        var allowed = accessScope.AllowedCenterCodes.Select(x => new ExchangeCenterCode(x)).ToArray();
        if (requestedCenter is { } requested && !allowed.Contains(requested))
        {
            authorized = false;
            return [];
        }

        return requestedCenter is null ? allowed : [requestedCenter.Value];
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page <= 0 || pageSize is <= 0 or > 200)
        {
            throw new CoreValidationException("Page must be positive and pageSize must be between 1 and 200.");
        }
    }

    private static DeviceDto Map(SortingDevice device) => new(
        device.Id.Value,
        device.Name,
        device.DeviceType,
        device.LogicalCode.Value,
        device.Owner,
        device.ExchangeCenterCode.Value,
        device.Description,
        device.ActivationStatus);
}

public sealed class ConfigurationQueryService(IDeviceConfigStore store) : IConfigurationQueryService
{
    public async Task<ConfigurationSnapshotDto?> GetCurrentAsync(
        string exchangeCenterCode,
        AccessScope accessScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        var center = new ExchangeCenterCode(exchangeCenterCode);
        if (!accessScope.IsGlobal &&
            !accessScope.AllowedCenterCodes.Contains(center.Value, StringComparer.Ordinal))
        {
            return null;
        }

        var snapshot = await store.GetLatestSnapshotAsync(center, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : ConfigurationService.Map(snapshot);
    }
}
