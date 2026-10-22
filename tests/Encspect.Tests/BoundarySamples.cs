#pragma warning disable CA1822, CA1812, CS0169, CS0067, IDE0051, CA1707
using System.Collections;
using static Encspect.Tests.BoundarySamples.Decoding.ImportedTarget;

// The negative control of DecodingBoundaryTests: parser-side types that reach a type in the Decoding namespace, one sample
// for each path the check reads, and Decoding types that extend parser-side types. Nothing here runs; the compiled
// assembly is what the check scans, and it must report exactly the pairs ControlPairs lists.
namespace Encspect.Tests.BoundarySamples
{
    internal static class ControlPairs
    {
        // (parser-side or inverted type, type it reaches), by short names inside this namespace.
        public static readonly (string From, string To)[] Expected =
        [
            ("ViaStaticCall", "Decoding.StaticTarget"),
            ("ViaField", "Decoding.FieldTarget"),
            ("ViaConstructor", "Decoding.ConstructedTarget"),
            ("ViaNestedType", "Decoding.Outer+Inner"),
            ("ViaListOfDecodingType", "Decoding.ListArgument"),
            ("ViaGenericMember", "Decoding.Generic`1"),
            ("ViaMethodSpec", "Decoding.MethodArgument"),
            ("ViaTypeof", "Decoding.TypeofTarget"),
            ("ViaMethodGroup", "Decoding.DelegateTarget"),
            ("ViaFunctionPointer", "Decoding.PointerArgument"),
            ("ViaCatch", "Decoding.CaughtException"),
            ("ViaAttributeTypeof", "Decoding.AttributeArgument"),
            ("ViaAttributeEnum", "Decoding.AttributeMode"),
            ("ViaUsingStatic", "Decoding.ImportedTarget"),
            ("IDefaultBody", "Decoding.DefaultBodyTarget"),
            ("ViaIterator", "Decoding.IteratorTarget"),
            ("ViaAsync", "Decoding.AsyncTarget"),
            ("ViaLocalFunction", "Decoding.LocalTarget"),
            ("ViaLambda", "Decoding.LambdaTarget"),
            ("ViaBaseType", "Decoding.BaseTarget"),
            ("ViaInterface", "Decoding.InterfaceTarget"),
            ("ViaConstraint", "Decoding.ConstraintTarget"),
            ("ViaParameter", "Decoding.ParameterTarget"),
            ("ViaReturn", "Decoding.ReturnTarget"),
            ("ViaProperty", "Decoding.PropertyTarget"),
            ("ViaEvent", "Decoding.EventTarget"),
            ("ViaIsCheck", "Decoding.IsTarget"),
            ("ViaArray", "Decoding.ArrayElement"),
            ("Decoding.InvertedImplementation", "IParserSide"),
            ("Decoding.InvertedDerived", "ParserSideBase"),
        ];
    }

    internal interface IParserSide;

    internal class ParserSideBase;

    internal static class ViaStaticCall
    {
        public static void Run() => Decoding.StaticTarget.Do();
    }

    internal sealed class ViaField
    {
        private Decoding.FieldTarget? _field;
    }

    internal static class ViaConstructor
    {
        public static object Run() => new Decoding.ConstructedTarget();
    }

    internal static class ViaNestedType
    {
        public static void Run()
        {
            Decoding.Outer.Inner inner = default;
            GC.KeepAlive(inner);
        }
    }

    internal sealed class ViaListOfDecodingType
    {
        private List<Decoding.ListArgument>? _list;
    }

    internal static class ViaGenericMember
    {
        public static int Run() => Decoding.Generic<int>.Make();
    }

    internal static class Identity
    {
        public static T Of<T>(T value) => value;
    }

    internal static class ViaMethodSpec
    {
        public static void Run() => Identity.Of<Decoding.MethodArgument>(null!);
    }

    internal static class ViaTypeof
    {
        public static Type Run() => typeof(Decoding.TypeofTarget);
    }

    internal static class ViaMethodGroup
    {
        public static Action Run() => Decoding.DelegateTarget.Run;
    }

    internal static unsafe class ViaFunctionPointer
    {
        public static void Run()
        {
            delegate*<Decoding.PointerArgument, void> pointer = null;
            if (pointer != null)
            {
                pointer(default);
            }
        }
    }

    internal static class ViaCatch
    {
        public static void Run()
        {
            try
            {
                Environment.GetEnvironmentVariable("ENCSPECT_BOUNDARY_SAMPLE");
            }
            catch (Decoding.CaughtException)
            {
            }
        }
    }

    [AttributeUsage(AttributeTargets.All)]
    internal sealed class TypeArgumentAttribute(Type type) : Attribute
    {
        public Type Type => type;
    }

    [AttributeUsage(AttributeTargets.All)]
    internal sealed class ObjectArgumentAttribute(object value) : Attribute
    {
        public object Value => value;
    }

    [TypeArgument(typeof(Decoding.AttributeArgument))]
    internal sealed class ViaAttributeTypeof;

    [ObjectArgument(Decoding.AttributeMode.Fast)]
    internal sealed class ViaAttributeEnum;

    internal static class ViaUsingStatic
    {
        public static void Run() => Imported();
    }

    internal interface IDefaultBody
    {
        void Run() => Decoding.DefaultBodyTarget.Do();
    }

    internal static class ViaIterator
    {
        public static IEnumerable<int> Run()
        {
            yield return Decoding.IteratorTarget.Value;
        }
    }

    internal static class ViaAsync
    {
        public static async Task Run()
        {
            await Task.Yield();
            Decoding.AsyncTarget.Do();
        }
    }

    internal static class ViaLocalFunction
    {
        public static void Run()
        {
            Local();

            static void Local() => Decoding.LocalTarget.Do();
        }
    }

    internal static class ViaLambda
    {
        public static Func<int> Run(int offset) => () => Decoding.LambdaTarget.Value + offset;
    }

    internal sealed class ViaBaseType : Decoding.BaseTarget;

    internal sealed class ViaInterface : Decoding.InterfaceTarget;

    internal static class ViaConstraint
    {
        public static void Run<T>()
            where T : Decoding.ConstraintTarget
        {
        }
    }

    internal static class ViaParameter
    {
        public static void Run(Decoding.ParameterTarget? target)
        {
        }
    }

    internal static class ViaReturn
    {
        public static Decoding.ReturnTarget? Run() => null;
    }

    internal sealed class ViaProperty
    {
        public Decoding.PropertyTarget? Value { get; set; }
    }

    internal sealed class ViaEvent
    {
        public event Action<Decoding.EventTarget>? Raised;
    }

    internal static class ViaIsCheck
    {
        public static bool Run(object value) => value is Decoding.IsTarget;
    }

    internal static class ViaArray
    {
        public static int Run(Decoding.ArrayElement[]? elements) => elements?.Length ?? 0;
    }
}

namespace Encspect.Tests.BoundarySamples.Decoding
{
    internal static class StaticTarget
    {
        public static void Do()
        {
        }
    }

    internal sealed class FieldTarget;

    internal sealed class ConstructedTarget;

    internal static class Outer
    {
        public struct Inner;
    }

    internal sealed class ListArgument;

    internal static class Generic<T>
    {
        public static int Make() => 0;
    }

    internal sealed class MethodArgument;

    internal sealed class TypeofTarget;

    internal static class DelegateTarget
    {
        public static void Run()
        {
        }
    }

    internal struct PointerArgument;

    internal sealed class CaughtException : Exception;

    internal sealed class AttributeArgument;

    internal enum AttributeMode : byte
    {
        Slow,
        Fast,
    }

    internal static class ImportedTarget
    {
        public static void Imported()
        {
        }
    }

    internal static class DefaultBodyTarget
    {
        public static void Do()
        {
        }
    }

    internal static class IteratorTarget
    {
        public static int Value => 1;
    }

    internal static class AsyncTarget
    {
        public static void Do()
        {
        }
    }

    internal static class LocalTarget
    {
        public static void Do()
        {
        }
    }

    internal static class LambdaTarget
    {
        public static int Value => 1;
    }

    internal class BaseTarget;

    internal interface InterfaceTarget;

    internal class ConstraintTarget;

    internal sealed class ParameterTarget;

    internal sealed class ReturnTarget;

    internal sealed class PropertyTarget;

    internal sealed class EventTarget;

    internal sealed class IsTarget;

    internal sealed class ArrayElement;

    // A Decoding type that extends a parser-side type: parser code could call into it through the base.
    internal sealed class InvertedImplementation : IParserSide;

    internal sealed class InvertedDerived : ParserSideBase;
}
