using System.Reflection;
using Packet.SoundModem.Modems;

namespace Packet.SoundModem.Tests.Modems;

/// <summary>
/// Guards the rule the whole plugin mechanism rests on: every assembly a plugin can touch through
/// the contract is one the host provides, so the types are the same types on both sides.
/// </summary>
/// <remarks>
/// <para>The failure this prevents is silent and expensive. If the contract's surface grows a type
/// from an assembly <see cref="ModemPluginLoader.HostProvidedAssemblies"/> does not name, a plugin
/// resolves that assembly privately from its own folder or from the NuGet cache, gets a second
/// unrelated copy of the type, and the failure reads "X cannot be converted to X". Nothing about
/// the change that caused it looks related.</para>
/// <para>It is not hypothetical: <see cref="FrameQuality.HeaderType"/> is an <c>M0LTE.Il2p</c>
/// enum, so that package is already on the list, and it got there because this test was written
/// before anything had gone wrong rather than after.</para>
/// </remarks>
public class ModemPluginContractTests
{
    /// <summary>The types a plugin cannot avoid: it implements the first two and every signature
    /// on them drags in the rest.</summary>
    private static readonly Type[] Seam =
    [
        typeof(IModemPlugin),
        typeof(IModem),
        typeof(ModemDescriptor),
        typeof(ModemOptions),
        typeof(FrameQuality),
    ];

    private static readonly Assembly Contract = typeof(IModemPlugin).Assembly;

    [Fact]
    public void The_Contract_Assembly_Is_Host_Provided()
    {
        ModemPluginLoader.HostProvidedAssemblies.Should().Contain(Contract.GetName().Name!);
    }

    [Fact]
    public void Every_Assembly_The_Contract_Surface_Reaches_Is_Host_Provided()
    {
        string[] reached = [.. Surface()
            .Select(t => t.Assembly)
            .Where(a => a != Contract && !IsFramework(a))
            .Select(a => a.GetName().Name!)
            .Distinct()
            .Order(StringComparer.Ordinal)];

        reached.Should().OnlyContain(
            name => ModemPluginLoader.HostProvidedAssemblies.Contains(name),
            "a plugin resolving any of these privately gets a second copy of a contract type, and "
            + "the failure says nothing about which change caused it. Reached: {0}",
            string.Join(", ", reached));
    }

    [Fact]
    public void The_Surface_Walk_Actually_Reaches_Past_This_Assembly()
    {
        // The test above passes trivially if the walk finds nothing. It must at least find the
        // IL2P header type, which is the reason the list has a second entry.
        Surface().Should().Contain(typeof(M0LTE.Il2p.Il2pHeaderType));
    }

    /// <summary>
    /// Every type reachable from the seam's public signatures. Types declared in the contract
    /// assembly are expanded further, because a plugin that touches one touches its members too;
    /// anything else is recorded and left alone, because the question is only which assembly it
    /// came from.
    /// </summary>
    private static HashSet<Type> Surface()
    {
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>(Seam);
        while (pending.Count > 0)
        {
            foreach (Type type in Unwrap(pending.Dequeue()))
            {
                if (!seen.Add(type) || type.Assembly != Contract)
                {
                    continue;
                }

                foreach (Type next in Members(type))
                {
                    pending.Enqueue(next);
                }
            }
        }

        return seen;
    }

    /// <summary>A type and, for a constructed generic or an array or a by-ref, the types inside
    /// it: <c>Action&lt;byte[], FrameQuality&gt;</c> matters for the FrameQuality.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        Type bare = type.IsByRef || type.IsPointer || type.IsArray
            ? type.GetElementType() ?? type
            : type;

        yield return bare;
        foreach (Type argument in bare.IsGenericType ? bare.GetGenericArguments() : [])
        {
            foreach (Type inner in Unwrap(argument))
            {
                yield return inner;
            }
        }
    }

    private static IEnumerable<Type> Members(Type type)
    {
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (MethodBase method in type.GetMethods(Public).Cast<MethodBase>()
            .Concat(type.GetConstructors(Public)))
        {
            if (method is MethodInfo returning)
            {
                yield return returning.ReturnType;
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (PropertyInfo property in type.GetProperties(Public))
        {
            yield return property.PropertyType;
        }

        foreach (FieldInfo field in type.GetFields(Public))
        {
            yield return field.FieldType;
        }

        foreach (EventInfo raised in type.GetEvents(Public))
        {
            if (raised.EventHandlerType is Type handler)
            {
                yield return handler;
            }
        }
    }

    /// <summary>The shared framework, which every load context gets from the runtime whatever the
    /// plugin's folder holds.</summary>
    private static bool IsFramework(Assembly assembly)
    {
        string name = assembly.GetName().Name ?? "";
        return assembly == typeof(object).Assembly
            || name.StartsWith("System", StringComparison.Ordinal)
            || name is "netstandard" or "mscorlib";
    }
}
