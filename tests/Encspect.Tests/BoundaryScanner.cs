using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Encspect.Tests;

// One edge the boundary forbids. From is the outermost type the edge starts in (a parser type; for an inversion, the
// Decoding type), To the type it reaches, Member where in From.
internal readonly record struct BoundaryViolation(string From, string Member, string To, bool IsInversion);

// Reads a compiled assembly's metadata and IL for every way parser code can name a type of the Decoding namespace of its
// own assembly (docs/design/decoders.md, "How a test enforces it"). The IL walker is hand-written because
// System.Reflection.Metadata has no IL reader: it takes the operand sizes from System.Reflection.Emit.OpCodes and reads
// the tokens of the operands that hold one. An operand-size mistake loses the walk's place: it then reads an undefined opcode
// or runs past the end of the body, and the scan fails with a message that names the method and the IL offset (never a
// silent miss); a size that happens to land on valid opcodes reads wrong tokens, which the negative control shows as a missing
// or extra pair.
//
// Disabled turns off one way of reaching a type, so a test can show that each way is needed: BoundarySynthetic builds an
// assembly for each, in which that way is the only evidence of a pair.
internal static partial class BoundaryScanner
{
    [Flags]
    public enum ScanPath : long
    {
        None = 0,
        BaseType = 1L << 0,
        Interfaces = 1L << 1,
        TypeConstraints = 1L << 2,
        MethodConstraints = 1L << 3,
        FieldSignature = 1L << 4,
        PropertySignature = 1L << 5,
        EventType = 1L << 6,
        MethodSignature = 1L << 7,
        LocalSignature = 1L << 8,
        CatchType = 1L << 9,
        MethodDefToken = 1L << 10,
        FieldDefToken = 1L << 11,
        TypeDefToken = 1L << 12,
        TypeSpecToken = 1L << 13,
        MemberRefParent = 1L << 14,
        MethodSpecInstantiation = 1L << 15,
        CalliSignature = 1L << 16,
        AttributeConstructor = 1L << 17,
        AttributeTypeofArgument = 1L << 18,
        AttributeEnumArgument = 1L << 19,
        AttributeArrayArgument = 1L << 20,
        AttributeTypeNameForms = 1L << 21,
        AttributesOnType = 1L << 22,
        AttributesOnField = 1L << 23,
        AttributesOnMethod = 1L << 24,
        AttributesOnParameter = 1L << 25,
        AttributesOnProperty = 1L << 26,
        AttributesOnEvent = 1L << 27,
        AttributesOnGenericParameter = 1L << 28,
        AttributesOnAssembly = 1L << 29,
        AttributesOnModule = 1L << 30,
    }

    // A namespace is decoding code where Decoding is one of its segments: X.Decoding and X.Decoding.Y, not X.DecodingTables.
    public static bool IsDecodingNamespace(string ns) => ns.Split('.').Contains("Decoding", StringComparer.Ordinal);

    public static IReadOnlyList<BoundaryViolation> Scan(Assembly assembly, ScanPath disabled = ScanPath.None)
    {
        using FileStream stream = File.OpenRead(assembly.Location);
        return Scan(stream, disabled);
    }

    public static IReadOnlyList<BoundaryViolation> Scan(Stream image, ScanPath disabled = ScanPath.None)
    {
        using var pe = new PEReader(image, PEStreamOptions.LeaveOpen);
        return new Walk(pe, pe.GetMetadataReader(), disabled).Run();
    }

    private static readonly Dictionary<ushort, OperandType> OperandTypes = BuildOperandTable();

    private static Dictionary<ushort, OperandType> BuildOperandTable()
    {
        var table = new Dictionary<ushort, OperandType>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
            {
                continue;
            }

            ushort value = (ushort)op.Value;
            if ((value >> 8) is 0 or 0xFE)
            {
                table[value] = op.OperandType;
            }
        }

        return table;
    }

    // The metadata tokens in the operands of a method body, read with the operand sizes of every opcode. An opcode that does not
    // exist, or an operand that runs past the body, means an earlier operand was read with the wrong size: the walk fails.
    public static List<EntityHandle> TokensIn(byte[] il, string method)
    {
        var tokens = new List<EntityHandle>();
        int i = 0;
        while (i < il.Length)
        {
            int at = i;
            ushort code = il[i++];
            if (code == 0xFE)
            {
                if (i >= il.Length)
                {
                    throw Lost(method, at, "the body ends inside a two-byte opcode");
                }

                code = (ushort)(0xFE00 | il[i++]);
            }

            if (!OperandTypes.TryGetValue(code, out OperandType operand))
            {
                throw Lost(method, at, $"0x{code:X} is no opcode, so an earlier operand was read with the wrong size");
            }

            int size = operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                _ => 4,
            };
            if (i + size > il.Length)
            {
                throw Lost(method, at, "the operand runs past the end of the body");
            }

            if (operand == OperandType.InlineSwitch)
            {
                long targets = BitConverter.ToUInt32(il.AsSpan(i, 4));
                if (i + 4 + (4 * targets) > il.Length)
                {
                    throw Lost(method, at, "the switch targets run past the end of the body");
                }

                size = 4 + (4 * (int)targets);
            }
            else if (operand is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok or OperandType.InlineSig)
            {
                tokens.Add(MetadataTokens.EntityHandle(BitConverter.ToInt32(il.AsSpan(i, 4))));
            }

            i += size;
        }

        return tokens;
    }

    private static InvalidDataException Lost(string method, int offset, string why) =>
        new($"The IL walk lost its place in {method} at IL offset {offset}: {why}.");

    // The type a custom attribute's blob names, as a definition of the assembly or by its serialized name.
    private readonly record struct AttributeType(TypeDefinitionHandle Definition, string? Name);

    private sealed class Walk(PEReader pe, MetadataReader md, ScanPath disabled)
    {
        private readonly TypeCollector _collector = new();
        private readonly AttributeTypes _attributeTypes = new(md);
        private readonly Dictionary<string, TypeDefinitionHandle> _byName = md.TypeDefinitions.ToDictionary(h => FullName(md, h), h => h);
        private readonly List<BoundaryViolation> _violations = [];
        private string _from = string.Empty;
        private string _member = string.Empty;

        private bool On(ScanPath path) => (disabled & path) == 0;

        public IReadOnlyList<BoundaryViolation> Run()
        {
            foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                TypeDefinition type = md.GetTypeDefinition(handle);
                if (IsDecoding(handle))
                {
                    CheckInversion(handle, type);
                }
                else
                {
                    ReadParserType(handle, type);
                }
            }

            _from = "<assembly>";
            _member = "assembly attributes";
            ReadAttributes(md.GetAssemblyDefinition().GetCustomAttributes(), ScanPath.AttributesOnAssembly);
            _member = "module attributes";
            ReadAttributes(md.GetModuleDefinition().GetCustomAttributes(), ScanPath.AttributesOnModule);

            return _violations.Distinct().ToList();
        }

        private bool IsDecoding(TypeDefinitionHandle handle) => IsDecodingNamespace(md.GetString(md.GetTypeDefinition(Outermost(handle)).Namespace));

        private TypeDefinitionHandle Outermost(TypeDefinitionHandle handle)
        {
            for (TypeDefinitionHandle outer = md.GetTypeDefinition(handle).GetDeclaringType(); !outer.IsNil; outer = md.GetTypeDefinition(handle).GetDeclaringType())
            {
                handle = outer;
            }

            return handle;
        }

        // The edges that start in a Decoding type and end in a type of the assembly that is not decoding code.
        private void CheckInversion(TypeDefinitionHandle handle, TypeDefinition type)
        {
            IEnumerable<(string Role, EntityHandle Handle)> bases = type.GetInterfaceImplementations()
                .Select(i => ("interface", md.GetInterfaceImplementation(i).Interface))
                .Prepend(("base type", type.BaseType));
            foreach ((string role, EntityHandle baseHandle) in bases)
            {
                TypeDefinitionHandle? head = baseHandle.IsNil ? null : baseHandle.Kind switch
                {
                    HandleKind.TypeDefinition => (TypeDefinitionHandle)baseHandle,
                    HandleKind.TypeSpecification => Head((TypeSpecificationHandle)baseHandle),
                    _ => null,
                };
                if (head is { } definition && !IsDecoding(definition))
                {
                    _violations.Add(new BoundaryViolation(FullName(md, Outermost(handle)), role, FullName(md, definition), IsInversion: true));
                }
            }
        }

        private TypeDefinitionHandle? Head(TypeSpecificationHandle spec)
        {
            var collector = new TypeCollector();
            EntityHandle head = md.GetTypeSpecification(spec).DecodeSignature(collector, null);
            return head.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)head : null;
        }

        private void ReadParserType(TypeDefinitionHandle handle, TypeDefinition type)
        {
            _from = FullName(md, Outermost(handle));

            _member = "base type";
            if (On(ScanPath.BaseType))
            {
                Touch(type.BaseType);
            }

            _member = "interface";
            foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
            {
                if (On(ScanPath.Interfaces))
                {
                    Touch(md.GetInterfaceImplementation(implementation).Interface);
                }
            }

            _member = "generic parameters";
            ReadGenericParameters(type.GetGenericParameters(), ScanPath.TypeConstraints);
            _member = "type attributes";
            ReadAttributes(type.GetCustomAttributes(), ScanPath.AttributesOnType);

            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = md.GetFieldDefinition(fieldHandle);
                _member = $"field {md.GetString(field.Name)}";
                field.DecodeSignature(_collector, null);
                Collect(ScanPath.FieldSignature);
                ReadAttributes(field.GetCustomAttributes(), ScanPath.AttributesOnField);
            }

            foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
            {
                PropertyDefinition property = md.GetPropertyDefinition(propertyHandle);
                _member = $"property {md.GetString(property.Name)}";
                property.DecodeSignature(_collector, null);
                Collect(ScanPath.PropertySignature);
                ReadAttributes(property.GetCustomAttributes(), ScanPath.AttributesOnProperty);
            }

            foreach (EventDefinitionHandle eventHandle in type.GetEvents())
            {
                EventDefinition declared = md.GetEventDefinition(eventHandle);
                _member = $"event {md.GetString(declared.Name)}";
                if (On(ScanPath.EventType))
                {
                    Touch(declared.Type);
                }

                ReadAttributes(declared.GetCustomAttributes(), ScanPath.AttributesOnEvent);
            }

            foreach (MethodImplementationHandle implementationHandle in type.GetMethodImplementations())
            {
                MethodImplementation implementation = md.GetMethodImplementation(implementationHandle);
                _member = "explicit implementation";
                Touch(implementation.MethodBody);
                Touch(implementation.MethodDeclaration);
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                ReadMethod(md.GetMethodDefinition(methodHandle));
            }
        }

        private void ReadMethod(MethodDefinition method)
        {
            _member = $"method {md.GetString(method.Name)}";
            method.DecodeSignature(_collector, null);
            Collect(ScanPath.MethodSignature);
            ReadGenericParameters(method.GetGenericParameters(), ScanPath.MethodConstraints);
            ReadAttributes(method.GetCustomAttributes(), ScanPath.AttributesOnMethod);
            foreach (ParameterHandle parameter in method.GetParameters())
            {
                ReadAttributes(md.GetParameter(parameter).GetCustomAttributes(), ScanPath.AttributesOnParameter);
            }

            if (method.RelativeVirtualAddress == 0)
            {
                return;
            }

            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil)
            {
                md.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(_collector, null);
                Collect(ScanPath.LocalSignature);
            }

            foreach (ExceptionRegion region in body.ExceptionRegions)
            {
                if (region.Kind == ExceptionRegionKind.Catch && On(ScanPath.CatchType))
                {
                    Touch(region.CatchType);
                }
            }

            ReadIl(body.GetILBytes() ?? [], md.GetString(method.Name));
        }

        private void ReadGenericParameters(GenericParameterHandleCollection parameters, ScanPath constraints)
        {
            foreach (GenericParameterHandle handle in parameters)
            {
                GenericParameter parameter = md.GetGenericParameter(handle);
                foreach (GenericParameterConstraintHandle constraint in parameter.GetConstraints())
                {
                    if (On(constraints))
                    {
                        Touch(md.GetGenericParameterConstraint(constraint).Type);
                    }
                }

                ReadAttributes(parameter.GetCustomAttributes(), ScanPath.AttributesOnGenericParameter);
            }
        }

        private void ReadIl(byte[] il, string method)
        {
            foreach (EntityHandle token in TokensIn(il, method))
            {
                TouchToken(token);
            }
        }

        // The way a token reaches a type decides which path can be switched off.
        private void TouchToken(EntityHandle handle)
        {
            ScanPath path = handle.Kind switch
            {
                HandleKind.MethodDefinition => ScanPath.MethodDefToken,
                HandleKind.FieldDefinition => ScanPath.FieldDefToken,
                HandleKind.TypeDefinition => ScanPath.TypeDefToken,
                HandleKind.TypeSpecification => ScanPath.TypeSpecToken,
                HandleKind.StandaloneSignature => ScanPath.CalliSignature,
                _ => ScanPath.None,
            };
            if (path == ScanPath.None || On(path))
            {
                Touch(handle);
            }
        }

        private void ReadAttributes(CustomAttributeHandleCollection attributes, ScanPath site)
        {
            if (!On(site))
            {
                return;
            }

            foreach (CustomAttributeHandle handle in attributes)
            {
                CustomAttribute attribute = md.GetCustomAttribute(handle);
                if (On(ScanPath.AttributeConstructor))
                {
                    Touch(attribute.Constructor);
                }

                CustomAttributeValue<AttributeType> value = attribute.DecodeValue(_attributeTypes);
                foreach (CustomAttributeTypedArgument<AttributeType> argument in value.FixedArguments)
                {
                    TouchArgument(argument);
                }

                foreach (CustomAttributeNamedArgument<AttributeType> argument in value.NamedArguments)
                {
                    TouchArgument(new CustomAttributeTypedArgument<AttributeType>(argument.Type, argument.Value));
                }
            }
        }

        private void TouchArgument(CustomAttributeTypedArgument<AttributeType> argument)
        {
            if (On(ScanPath.AttributeEnumArgument))
            {
                TouchAttributeType(argument.Type);
            }

            switch (argument.Value)
            {
                case AttributeType type when On(ScanPath.AttributeTypeofArgument):
                    TouchAttributeType(type);
                    break;
                case ImmutableArray<CustomAttributeTypedArgument<AttributeType>> array when On(ScanPath.AttributeArrayArgument):
                    foreach (CustomAttributeTypedArgument<AttributeType> element in array)
                    {
                        TouchArgument(element);
                    }

                    break;
            }
        }

        // A serialized type name is "Namespace.Type" for a type of this assembly and "Namespace.Type, Assembly, Version=…" for
        // another; an array adds [] (or * and &), and a generic instance lists its arguments in [[…]], each with its assembly.
        private void TouchAttributeType(AttributeType type)
        {
            if (!type.Definition.IsNil)
            {
                Report(type.Definition);
                return;
            }

            if (type.Name is not { } name)
            {
                return;
            }

            string ours = md.GetString(md.GetAssemblyDefinition().Name);
            if (!On(ScanPath.AttributeTypeNameForms))
            {
                // The first name only, as written for a plain type.
                int comma = name.IndexOf(',', StringComparison.Ordinal);
                string head = comma < 0 ? name : name[..comma];
                if (_byName.TryGetValue(head, out TypeDefinitionHandle plain))
                {
                    Report(plain);
                }

                return;
            }

            foreach (Match match in NamePart().Matches(Decorations().Replace(name, string.Empty)))
            {
                string? assembly = match.Groups[2].Success ? match.Groups[2].Value : null;
                if ((assembly is null || assembly == ours) && _byName.TryGetValue(match.Groups[1].Value, out TypeDefinitionHandle found))
                {
                    Report(found);
                }
            }
        }

        // What a signature named is in the collector; it is reported and cleared.
        private void Collect(ScanPath path = ScanPath.None)
        {
            if (path == ScanPath.None || On(path))
            {
                foreach (TypeDefinitionHandle seen in _collector.Seen)
                {
                    Report(seen);
                }
            }

            _collector.Seen.Clear();
        }

        private void Touch(EntityHandle handle)
        {
            if (handle.IsNil)
            {
                return;
            }

            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    Report((TypeDefinitionHandle)handle);
                    break;
                case HandleKind.TypeSpecification:
                    md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(_collector, null);
                    Collect();
                    break;
                case HandleKind.MethodDefinition:
                    Report(md.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType());
                    break;
                case HandleKind.FieldDefinition:
                    Report(md.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType());
                    break;
                case HandleKind.MemberReference:
                    MemberReference member = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (On(ScanPath.MemberRefParent))
                    {
                        Touch(member.Parent);
                    }

                    if (member.GetKind() == MemberReferenceKind.Method)
                    {
                        member.DecodeMethodSignature(_collector, null);
                    }
                    else
                    {
                        member.DecodeFieldSignature(_collector, null);
                    }

                    Collect();
                    break;
                case HandleKind.MethodSpecification:
                    MethodSpecification specification = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                    Touch(specification.Method);
                    specification.DecodeSignature(_collector, null);
                    Collect(ScanPath.MethodSpecInstantiation);
                    break;
                case HandleKind.StandaloneSignature:
                    StandaloneSignature signature = md.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                    if (signature.GetKind() == StandaloneSignatureKind.Method)
                    {
                        signature.DecodeMethodSignature(_collector, null);
                    }
                    else
                    {
                        signature.DecodeLocalSignature(_collector, null);
                    }

                    Collect();
                    break;
            }
        }

        private void Report(TypeDefinitionHandle handle)
        {
            if (IsDecoding(handle))
            {
                _violations.Add(new BoundaryViolation(_from, _member, FullName(md, handle), IsInversion: false));
            }
        }
    }

    private static string FullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        TypeDefinition type = md.GetTypeDefinition(handle);
        string name = md.GetString(type.Name);
        if (type.GetDeclaringType() is { IsNil: false } outer)
        {
            return $"{FullName(md, outer)}+{name}";
        }

        string ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    [GeneratedRegex(@"\[,*\]|[*&]")]
    private static partial Regex Decorations();

    [GeneratedRegex(@"([A-Za-z_][\w.+`]*)(?:,\s*([A-Za-z_][\w.]*))?")]
    private static partial Regex NamePart();

    // Collects every type definition a signature names, generic arguments, arrays, pointers, modifiers and function
    // pointers included, and returns the head of the type it decodes (the generic type of an instantiation).
    private sealed class TypeCollector : ISignatureTypeProvider<EntityHandle, object?>
    {
        public HashSet<TypeDefinitionHandle> Seen { get; } = [];

        public EntityHandle GetPrimitiveType(PrimitiveTypeCode typeCode) => default;

        public EntityHandle GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            Seen.Add(handle);
            return handle;
        }

        public EntityHandle GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => handle;

        public EntityHandle GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public EntityHandle GetSZArrayType(EntityHandle elementType) => elementType;

        public EntityHandle GetArrayType(EntityHandle elementType, ArrayShape shape) => elementType;

        public EntityHandle GetByReferenceType(EntityHandle elementType) => elementType;

        public EntityHandle GetPointerType(EntityHandle elementType) => elementType;

        public EntityHandle GetPinnedType(EntityHandle elementType) => elementType;

        public EntityHandle GetGenericInstantiation(EntityHandle genericType, ImmutableArray<EntityHandle> typeArguments) => genericType;

        public EntityHandle GetFunctionPointerType(MethodSignature<EntityHandle> signature) => default;

        public EntityHandle GetGenericMethodParameter(object? genericContext, int index) => default;

        public EntityHandle GetGenericTypeParameter(object? genericContext, int index) => default;

        public EntityHandle GetModifiedType(EntityHandle modifier, EntityHandle unmodifiedType, bool isRequired) => unmodifiedType;
    }

    // Decodes custom attribute blobs: the types they name stay as definitions of the assembly or as serialized names.
    private sealed class AttributeTypes(MetadataReader md) : ICustomAttributeTypeProvider<AttributeType>
    {
        public AttributeType GetPrimitiveType(PrimitiveTypeCode typeCode) => new(default, typeCode.ToString());

        public AttributeType GetSystemType() => new(default, "System.Type");

        public AttributeType GetSZArrayType(AttributeType elementType) => elementType;

        public AttributeType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => new(handle, null);

        public AttributeType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference reference = reader.GetTypeReference(handle);
            string name = $"{reader.GetString(reference.Namespace)}.{reader.GetString(reference.Name)}";
            return reference.ResolutionScope.Kind == HandleKind.AssemblyReference
                ? new AttributeType(default, $"{name}, {reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name)}")
                : new AttributeType(default, name);
        }

        public AttributeType GetTypeFromSerializedName(string name) => new(default, name);

        public bool IsSystemType(AttributeType type) => type.Name is { } name && (name == "System.Type" || name.StartsWith("System.Type,", StringComparison.Ordinal));

        public PrimitiveTypeCode GetUnderlyingEnumType(AttributeType type)
        {
            if (!type.Definition.IsNil)
            {
                foreach (FieldDefinitionHandle field in md.GetTypeDefinition(type.Definition).GetFields())
                {
                    FieldDefinition definition = md.GetFieldDefinition(field);
                    if (md.GetString(definition.Name) == "value__")
                    {
                        BlobReader blob = md.GetBlobReader(definition.Signature);
                        blob.ReadByte();
                        return (PrimitiveTypeCode)blob.ReadByte();
                    }
                }
            }
            else if (type.Name is { } name && Type.GetType(name) is { IsEnum: true } runtime)
            {
                return Type.GetTypeCode(Enum.GetUnderlyingType(runtime)) switch
                {
                    TypeCode.Byte => PrimitiveTypeCode.Byte,
                    TypeCode.SByte => PrimitiveTypeCode.SByte,
                    TypeCode.Int16 => PrimitiveTypeCode.Int16,
                    TypeCode.UInt16 => PrimitiveTypeCode.UInt16,
                    TypeCode.UInt32 => PrimitiveTypeCode.UInt32,
                    TypeCode.Int64 => PrimitiveTypeCode.Int64,
                    TypeCode.UInt64 => PrimitiveTypeCode.UInt64,
                    _ => PrimitiveTypeCode.Int32,
                };
            }

            return PrimitiveTypeCode.Int32;
        }
    }
}
