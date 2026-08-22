using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Services;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Workers;

internal sealed class LocalAdministratorBootstrapHostedService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    IOptions<EdgeLocalAdministratorOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.AuthenticationEnabled) return;
        var initialPassword = configuration["ADMIN_INITIAL_PASSWORD"] ?? options.Value.InitialPassword;
        var initialUserName = configuration["ADMIN_INITIAL_USERNAME"] ?? options.Value.InitialUserName;
        await using var scope = serviceProvider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILocalAdministratorService>()
            .EnsureInitializedAsync(initialUserName, initialPassword, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
