using System.ComponentModel;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using static Encspect.Tests.BoundaryScanner;

namespace Encspect.Tests;

// Small assemblies built with System.Reflection.Emit, in each of which exactly one way of reaching a type of the Decoding
// namespace exists: a C# compiler adds evidence of its own (a constructor calls the base constructor, a property has
// accessors that carry its type), so the samples in BoundarySamples cannot show that each way is needed. Here the host type
// Syn.Host reaches Syn.Decoding.T (or the type the case names) only through the path being tested, and
// DecodingBoundaryTests shows that scanning with that path disabled loses the pair.
internal static class BoundarySynthetic
{
    internal sealed record Case(string Name, ScanPath Path, Action<Builder> Build, string From = "Syn.Host", string To = "Syn.Decoding.T", Func<byte[], byte[]>? Patch = null);

    internal sealed class Builder
    {
        private readonly List<TypeBuilder> _types = [];

        public Builder()
        {
            Assembly = new PersistedAssemblyBuilder(new AssemblyName("Syn"), typeof(object).Assembly);
            Module = Assembly.DefineDynamicModule("Syn");
            Target = Define("Syn.Decoding.T", TypeAttributes.Public | TypeAttributes.Class);
            TargetConstructor = Target.DefineDefaultConstructor(MethodAttributes.Public);
            TargetMethod = Target.DefineMethod("Do", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
            TargetMethod.GetILGenerator().Emit(OpCodes.Ret);
            TargetField = Target.DefineField("F", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
            Interface = Define("Syn.Decoding.I", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
            Exception = Define("Syn.Decoding.E", TypeAttributes.Public | TypeAttributes.Class, typeof(Exception));
            Generic = Define("Syn.Decoding.G", TypeAttributes.Public | TypeAttributes.Class);
            GenericParameter = Generic.DefineGenericParameters("U")[0];
            GenericField = Generic.DefineField("F", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
            Attribute = Define("Syn.Decoding.Mark", TypeAttributes.Public | TypeAttributes.Class, typeof(Attribute));
            AttributeConstructor = Attribute.DefineDefaultConstructor(MethodAttributes.Public);
            Enum = Module.DefineEnum("Syn.Decoding.Mode", TypeAttributes.Public, typeof(int));
            Enum.DefineLiteral("Fast", 1);
        }

        public PersistedAssemblyBuilder Assembly { get; }

        public ModuleBuilder Module { get; }

        public TypeBuilder Target { get; }

        public ConstructorBuilder TargetConstructor { get; }

        public MethodBuilder TargetMethod { get; }

        public FieldBuilder TargetField { get; }

        public TypeBuilder Interface { get; }

        public TypeBuilder Exception { get; }

        public TypeBuilder Generic { get; }

        public GenericTypeParameterBuilder GenericParameter { get; }

        public FieldBuilder GenericField { get; }

        public TypeBuilder Attribute { get; }

        public ConstructorBuilder AttributeConstructor { get; }

        public EnumBuilder Enum { get; }

        public TypeBuilder Define(string name, TypeAttributes attributes, Type? parent = null)
        {
            TypeBuilder type = Module.DefineType(name, attributes, parent);
            _types.Add(type);
            return type;
        }

        public TypeBuilder Host(Type? parent = null) => Define("Syn.Host", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract, parent);

        public (MethodBuilder Method, ILGenerator Il) Method(TypeBuilder host, Type[]? parameters = null)
        {
            MethodBuilder method = host.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), parameters ?? Type.EmptyTypes);
            return (method, method.GetILGenerator());
        }

        // A method whose body is the IL the action emits, and a return.
        public void Body(Action<ILGenerator> emit)
        {
            (_, ILGenerator il) = Method(Host());
            emit(il);
            il.Emit(OpCodes.Ret);
        }

        public byte[] Save()
        {
            Enum.CreateType();
            foreach (TypeBuilder type in _types.Where(t => t.IsInterface || t.BaseType is null || !_types.Contains(t.BaseType)))
            {
                type.CreateType();
            }

            foreach (TypeBuilder type in _types.Where(t => !t.IsCreated()))
            {
                type.CreateType();
            }

            using var stream = new MemoryStream();
            Assembly.Save(stream);
            return stream.ToArray();
        }
    }

    // A custom attribute blob whose only argument is a Type given by its serialized name: prolog, string, no named arguments.
    public static byte[] TypeofBlob(string name) => [.. Prolog, .. SerString(name), 0, 0];

    // An object argument that is an enum of the given type (an argument of type object is written as its own type tag and value).
    public static byte[] EnumBlob(string typeName, int value) => [.. Prolog, 0x55, .. SerString(typeName), .. BitConverter.GetBytes(value), 0, 0];

    // An object argument that is an array of one Type.
    public static byte[] TypeArrayBlob(string name) => [.. Prolog, 0x1D, 0x50, .. BitConverter.GetBytes(1), .. SerString(name), 0, 0];

    private static readonly byte[] Prolog = [1, 0];

    private static byte[] SerString(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] length = utf8.Length < 0x80 ? [(byte)utf8.Length] : [(byte)(0x80 | (utf8.Length >> 8)), (byte)utf8.Length];
        return [.. length, .. utf8];
    }

    private static readonly ConstructorInfo TypeConverter = typeof(TypeConverterAttribute).GetConstructor([typeof(Type)])!;

    private static readonly ConstructorInfo ObjectArgument = typeof(DefaultValueAttribute).GetConstructor([typeof(object)])!;

    public static IReadOnlyList<Case> Cases { get; } =
    [
        new("base type", ScanPath.BaseType, b =>
        {
            // A constructor that does not call the base constructor, so the type is the base's only trace.
            ConstructorBuilder constructor = b.Host(b.Target).DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            constructor.GetILGenerator().Emit(OpCodes.Ret);
        }),
        new("interface", ScanPath.Interfaces, b => b.Host().AddInterfaceImplementation(b.Interface), To: "Syn.Decoding.I"),
        new("type constraint", ScanPath.TypeConstraints, b => b.Host().DefineGenericParameters("X")[0].SetBaseTypeConstraint(b.Target)),
        new("method constraint", ScanPath.MethodConstraints, b =>
        {
            (MethodBuilder method, ILGenerator il) = b.Method(b.Host());
            method.DefineGenericParameters("X")[0].SetBaseTypeConstraint(b.Target);
            il.Emit(OpCodes.Ret);
        }),
        new("field signature", ScanPath.FieldSignature, b => b.Host().DefineField("f", b.Target, FieldAttributes.Public)),
        new("property signature", ScanPath.PropertySignature, b => b.Host().DefineProperty("P", PropertyAttributes.None, b.Target, null)),
        new("event type", ScanPath.EventType, b => b.Host().DefineEvent("E", EventAttributes.None, b.Target)),
        new("method signature", ScanPath.MethodSignature, b =>
        {
            (_, ILGenerator il) = b.Method(b.Host(), [b.Target]);
            il.Emit(OpCodes.Ret);
        }),
        new("local signature", ScanPath.LocalSignature, b => b.Body(il => il.DeclareLocal(b.Target))),
        new("catch type", ScanPath.CatchType, b => b.Body(il =>
        {
            il.BeginExceptionBlock();
            il.Emit(OpCodes.Nop);
            il.BeginCatchBlock(b.Exception);
            il.Emit(OpCodes.Pop);
            il.EndExceptionBlock();
        }), To: "Syn.Decoding.E"),
        new("call token", ScanPath.MethodDefToken, b => b.Body(il => il.Emit(OpCodes.Call, b.TargetMethod))),
        new("field token", ScanPath.FieldDefToken, b => b.Body(il =>
        {
            il.Emit(OpCodes.Ldsfld, b.TargetField);
            il.Emit(OpCodes.Pop);
        })),
        new("type token", ScanPath.TypeDefToken, b => b.Body(il =>
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Isinst, b.Target);
            il.Emit(OpCodes.Pop);
        })),
        new("type specification token", ScanPath.TypeSpecToken, b => b.Body(il =>
        {
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Isinst, b.Target.MakeArrayType());
            il.Emit(OpCodes.Pop);
        })),
        new("member reference on a type specification", ScanPath.MemberRefParent, b => b.Body(il =>
        {
            il.Emit(OpCodes.Ldsfld, TypeBuilder.GetField(b.Generic.MakeGenericType(typeof(int)), b.GenericField));
            il.Emit(OpCodes.Pop);
        }), To: "Syn.Decoding.G"),
        new("method instantiation", ScanPath.MethodSpecInstantiation, b =>
        {
            TypeBuilder host = b.Host();
            (MethodBuilder generic, ILGenerator genericIl) = b.Method(host);
            generic.DefineGenericParameters("X");
            genericIl.Emit(OpCodes.Ret);
            (_, ILGenerator il) = b.Method(host);
            il.Emit(OpCodes.Call, generic.MakeGenericMethod(b.Target));
            il.Emit(OpCodes.Ret);
        }),
        new("calli signature", ScanPath.CalliSignature, b => b.Body(il =>
        {
            // The persisted builder cannot write a type it has not saved into a calli signature, so the signature void(int, int) is
            // patched after saving into void(Syn.Decoding.T): see PatchCalli.
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.EmitCalli(OpCodes.Calli, CallingConventions.Standard, typeof(void), [typeof(int), typeof(int)], null);
        }), Patch: PatchCalli),
        new("attribute constructor", ScanPath.AttributeConstructor, b => b.Host().SetCustomAttribute(b.AttributeConstructor, [1, 0, 0, 0]), To: "Syn.Decoding.Mark"),
        new("typeof argument", ScanPath.AttributeTypeofArgument, b => b.Host().SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"))),
        new("enum argument", ScanPath.AttributeArgumentType, b => b.Host().SetCustomAttribute(ObjectArgument, EnumBlob("Syn.Decoding.Mode", 1)), To: "Syn.Decoding.Mode"),
        new("array argument", ScanPath.AttributeArrayArgument, b => b.Host().SetCustomAttribute(ObjectArgument, TypeArrayBlob("Syn.Decoding.T"))),
        new("typeof an array", ScanPath.AttributeTypeNameForms, b => b.Host().SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T[]"))),
        new("typeof a generic instance", ScanPath.AttributeTypeNameForms, b => b.Host().SetCustomAttribute(
            TypeConverter,
            TypeofBlob("System.Collections.Generic.List`1[[Syn.Decoding.T, Syn, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null]], System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e"))),
        new("attribute on the type", ScanPath.AttributesOnType, b => b.Host().SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"))),
        new("attribute on a field", ScanPath.AttributesOnField, b => b.Host().DefineField("f", typeof(int), FieldAttributes.Public).SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"))),
        new("attribute on a method", ScanPath.AttributesOnMethod, b =>
        {
            (MethodBuilder method, ILGenerator il) = b.Method(b.Host());
            method.SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"));
            il.Emit(OpCodes.Ret);
        }),
        new("attribute on a parameter", ScanPath.AttributesOnParameter, b =>
        {
            (MethodBuilder method, ILGenerator il) = b.Method(b.Host(), [typeof(int)]);
            method.DefineParameter(1, ParameterAttributes.None, "p").SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"));
            il.Emit(OpCodes.Ret);
        }),
        new("attribute on a property", ScanPath.AttributesOnProperty, b => b.Host().DefineProperty("P", PropertyAttributes.None, typeof(int), null).SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"))),
        new("attribute on an event", ScanPath.AttributesOnEvent, b => b.Host().DefineEvent("E", EventAttributes.None, typeof(EventHandler)).SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"))),
        new("attribute on a generic parameter", ScanPath.AttributesOnGenericParameter, b => b.Host().DefineGenericParameters("X")[0].SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"))),
        new("attribute on the assembly", ScanPath.AttributesOnAssembly, b =>
        {
            b.Host();
            b.Assembly.SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"));
        }, From: "<assembly>"),
        new("attribute on the module", ScanPath.AttributesOnModule, b =>
        {
            b.Host();
            b.Module.SetCustomAttribute(TypeConverter, TypeofBlob("Syn.Decoding.T"));
        }, From: "<assembly>"),
    ];

    public static byte[] Build(Case scenario)
    {
        var builder = new Builder();
        scenario.Build(builder);
        byte[] image = builder.Save();
        return scenario.Patch is null ? image : scenario.Patch(image);
    }

    // The blob of the calli signature void(int, int) is [5, 0, 2, 1, 8, 8] (length, default convention, two parameters, void,
    // int, int). The second parameter becomes the class in TypeDef row 2 (Syn.Decoding.T, the first type defined): [5, 0, 1, 1,
    // 0x12, 8] (ELEMENT_TYPE_CLASS and the coded index (2 << 2) | TypeDef).
    private static byte[] PatchCalli(byte[] image)
    {
        byte[] before = [5, 0, 2, 1, 8, 8];
        byte[] after = [5, 0, 1, 1, 0x12, 8];
        int at = image.AsSpan().IndexOf(before);
        if (at < 0 || image.AsSpan(at + 1).IndexOf(before) >= 0)
        {
            throw new InvalidOperationException("The calli signature blob is not in the image exactly once.");
        }

        after.CopyTo(image, at);
        return image;
    }
}
