using System.Reflection;
using Post.Contracts.V1.Device;
using Post.Core.Domain.Parcels;
using Post.Core.Infrastructure;
using Post.Edge.Application.Services;
using Post.Edge.Domain.Parcels;

namespace Post.Architecture.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void CoreDomainHasNoOutwardFrameworkOrLayerDependency()
    {
        AssertDoesNotReference(
            typeof(ParcelDossier).Assembly,
            "Post.Core.Application",
            "Post.Core.Infrastructure",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore");
    }

    [Fact]
    public void EdgeDomainHasNoOutwardFrameworkOrLayerDependency()
    {
        AssertDoesNotReference(
            typeof(EdgeParcel).Assembly,
            "Post.Edge.Application",
            "Post.Edge.Infrastructure",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore");
    }

    [Fact]
    public void ApplicationLayersDoNotReferenceInfrastructureOrApiHosts()
    {
        AssertDoesNotReference(
            typeof(Post.Core.Application.DependencyInjection).Assembly,
            "Post.Core.Infrastructure",
            ".Api");
        AssertDoesNotReference(
            typeof(IInboundRegistrationService).Assembly,
            "Post.Edge.Infrastructure",
            ".Api");
    }

    [Fact]
    public void CoreAndEdgeBoundedContextsRemainIndependent()
    {
        AssertDoesNotReference(typeof(ParcelDossier).Assembly, "Post.Edge");
        AssertDoesNotReference(typeof(EdgeParcel).Assembly, "Post.Core");
        AssertDoesNotReference(typeof(Post.Core.Application.DependencyInjection).Assembly, "Post.Edge");
        AssertDoesNotReference(typeof(IInboundRegistrationService).Assembly, "Post.Core");
    }

    [Fact]
    public void InfrastructureLayersDoNotReferenceApiHosts()
    {
        AssertDoesNotReference(typeof(CoreInfrastructureOptions).Assembly, ".Api");
        AssertDoesNotReference(typeof(Post.Edge.Infrastructure.DependencyInjection).Assembly, ".Api");
    }

    [Fact]
    public void ContractsRemainPortableAndLayerNeutral()
    {
        AssertDoesNotReference(
            typeof(DeviceEnvelopeV1<>).Assembly,
            "Post.Core",
            "Post.Edge",
            "Post.BuildingBlocks",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore");
    }

    private static void AssertDoesNotReference(Assembly assembly, params string[] forbiddenFragments)
    {
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
        foreach (var forbidden in forbiddenFragments)
        {
            Assert.DoesNotContain(references, reference => reference.Contains(forbidden, StringComparison.Ordinal));
        }
    }
}
