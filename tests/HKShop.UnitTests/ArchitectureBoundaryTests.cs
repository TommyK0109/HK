using System.Reflection;
using HKShop.Application.Transportation;
using HKShop.Catalog.Api.Infrastructure;
using HKShop.Commerce;
using HKShop.Domain.Orders;
using HKShop.EOffice;
using HKShop.Gateway;
using HKShop.Identity.Api.Infrastructure;
using HKShop.Messaging;
using HKShop.Notification;
using HKShop.Ordering.Api.Infrastructure;
using HKShop.Payments.Api.Infrastructure;
using HKShop.ServiceDefaults;
using HKShop.Shipping.Api.Infrastructure;
using Xunit;

namespace HKShop.UnitTests;

public sealed class ArchitectureBoundaryTests
{
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void ServiceDefaultsAndGatewayDoNotReferenceDomainModulesOrWeb()
    {
        var defaultsRefs = typeof(ServiceDefaultsExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("HKShop.Contracts", defaultsRefs);
        Assert.DoesNotContain("HKShop.Commerce", defaultsRefs);
        Assert.DoesNotContain("HKShop.EOffice", defaultsRefs);
        Assert.DoesNotContain("HKShop.Infrastructure", defaultsRefs);
        Assert.DoesNotContain("HKShop.Web", defaultsRefs);

        var gatewayRefs = typeof(GatewayAssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("HKShop.ServiceDefaults", gatewayRefs);
        Assert.DoesNotContain("HKShop.Commerce", gatewayRefs);
        Assert.DoesNotContain("HKShop.EOffice", gatewayRefs);
        Assert.DoesNotContain("HKShop.Infrastructure", gatewayRefs);
        Assert.DoesNotContain("HKShop.Web", gatewayRefs);
    }

    [Fact]
    public void ExtractedServicesAndMessagingStayIsolatedFromMonolithAndEachOther()
    {
        var serviceAssemblies = new[]
        {
            typeof(CatalogDbContext).Assembly,
            typeof(IdentityServiceDbContext).Assembly,
            typeof(OrderingDbContext).Assembly,
            typeof(PaymentsDbContext).Assembly,
            typeof(ShippingDbContext).Assembly,
            typeof(NotificationDbContext).Assembly
        };
        var serviceNames = serviceAssemblies.Select(a => a.GetName().Name!).ToArray();

        var messagingRefs = typeof(OutboxRelayOptions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("HKShop.Contracts", messagingRefs);
        Assert.Contains("HKShop.ServiceDefaults", messagingRefs);
        foreach (var forbidden in new[] { "HKShop.Commerce", "HKShop.EOffice", "HKShop.Infrastructure", "HKShop.Web", "HKShop.Gateway" }.Concat(serviceNames))
            Assert.DoesNotContain(forbidden, messagingRefs);

        foreach (var assembly in serviceAssemblies)
        {
            var name = assembly.GetName().Name!;
            var refs = assembly.GetReferencedAssemblies().Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Contains("HKShop.Contracts", refs);
            Assert.Contains("HKShop.ServiceDefaults", refs);
            Assert.Contains("HKShop.Messaging", refs);
            Assert.DoesNotContain("HKShop.Commerce", refs);
            Assert.DoesNotContain("HKShop.EOffice", refs);
            Assert.DoesNotContain("HKShop.Infrastructure", refs);
            Assert.DoesNotContain("HKShop.Web", refs);
            Assert.DoesNotContain("HKShop.Gateway", refs);

            foreach (var otherService in serviceNames.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                Assert.DoesNotContain(otherService, refs);
        }
    }

    [Fact]
    public void CommerceDoesNotReferenceEOfficeOrInfrastructure()
    {
        var referenced = typeof(CommerceAssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("HKShop.EOffice", referenced);
        Assert.DoesNotContain("HKShop.Infrastructure", referenced);
        Assert.DoesNotContain("HKShop.Web", referenced);
        Assert.Contains("HKShop.Contracts", referenced);
    }

    [Fact]
    public void EOfficeDoesNotReferenceCommerceOrInfrastructure()
    {
        var referenced = typeof(EOfficeAssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("HKShop.Commerce", referenced);
        Assert.DoesNotContain("HKShop.Infrastructure", referenced);
        Assert.DoesNotContain("HKShop.Web", referenced);
        Assert.Contains("HKShop.Contracts", referenced);
    }

    [Fact]
    public void EOfficeOperationsDoesNotReferenceShippingInternals()
    {
        var eofficeAssembly = typeof(EOfficeAssemblyMarker).Assembly;
        var operationsTypes = eofficeAssembly.GetTypes().Where(IsOperationsType);

        foreach (var type in operationsTypes)
            AssertNoSignatureDependency(type, IsShippingType);
    }

    [Fact]
    public void ShippingTypesDoNotReferenceEOfficeOperations()
    {
        var eofficeAssembly = typeof(EOfficeAssemblyMarker).Assembly;
        var shippingTypes = eofficeAssembly.GetTypes().Where(IsShippingType);

        foreach (var type in shippingTypes)
            AssertNoSignatureDependency(type, IsOperationsType);
    }

    private static bool IsOperationsType(Type type) =>
        IsInNamespace(type, "HKShop.EOffice.Operations");

    private static bool IsShippingType(Type type) =>
        type.Assembly == typeof(EOfficeAssemblyMarker).Assembly &&
        (IsInNamespace(type, "HKShop.EOffice.Shipping") ||
         type.Namespace == typeof(TransportService).Namespace ||
         type.Namespace == typeof(TransportParcel).Namespace);

    private static bool IsInNamespace(Type type, string name) =>
        type.Namespace == name || type.Namespace?.StartsWith(name + ".", StringComparison.Ordinal) == true;

    private static void AssertNoSignatureDependency(Type type, Func<Type, bool> forbidden)
    {
        foreach (var field in type.GetFields(DeclaredMembers))
            AssertAllowedType(field, field.FieldType, forbidden);

        foreach (var constructor in type.GetConstructors(DeclaredMembers))
        {
            foreach (var parameter in constructor.GetParameters())
                AssertAllowedType(constructor, parameter.ParameterType, forbidden);
        }

        foreach (var method in type.GetMethods(DeclaredMembers))
        {
            AssertAllowedType(method, method.ReturnType, forbidden);
            foreach (var parameter in method.GetParameters())
                AssertAllowedType(method, parameter.ParameterType, forbidden);
        }
    }

    private static void AssertAllowedType(MemberInfo member, Type dependency, Func<Type, bool> forbidden)
    {
        Assert.False(forbidden(dependency),
            $"{member.DeclaringType?.FullName}.{member.Name} directly depends on {dependency.FullName}.");

        if (dependency.GetElementType() is { } elementType)
            AssertAllowedType(member, elementType, forbidden);

        foreach (var argument in dependency.GetGenericArguments())
            AssertAllowedType(member, argument, forbidden);
    }
}
