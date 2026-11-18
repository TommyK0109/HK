using System.Reflection;
using HKShop.Application.Transportation;
using HKShop.Commerce;
using HKShop.Domain.Orders;
using HKShop.EOffice;
using Xunit;

namespace HKShop.UnitTests;

public sealed class ArchitectureBoundaryTests
{
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

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
