using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

// Dumps the REAL API surface of sts2.dll straight out of its metadata.
// No assembly load, no dependency resolution, no code execution.
//   apiprobe <sts2.dll> <out.txt> [typeNameSubstring...]

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: apiprobe <sts2.dll> <out.txt> [typeNameSubstring...]");
    return 2;
}

string dllPath = args[0];
string outPath = args[1];
string[] filters = args.Length > 2 ? args[2..] : Array.Empty<string>();

using var fs = File.OpenRead(dllPath);
using var pe = new PEReader(fs);
var mr = pe.GetMetadataReader();

var sb = new StringBuilder();
var provider = new NameProvider(mr);

foreach (var tdh in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(tdh);
    string name = mr.GetString(td.Name);
    string ns = mr.GetString(td.Namespace);
    string full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

    if (filters.Length > 0 && !filters.Any(f => full.Contains(f, StringComparison.OrdinalIgnoreCase)))
        continue;

    var attrs = td.Attributes;
    string kind = (attrs & TypeAttributes.Interface) != 0 ? "interface"
        : (attrs & TypeAttributes.Abstract) != 0 && (attrs & TypeAttributes.Sealed) != 0 ? "static class"
        : (attrs & TypeAttributes.Abstract) != 0 ? "abstract class"
        : "class";

    sb.AppendLine($"=== {kind} {full}  [{attrs}]");

    // base type
    if (!td.BaseType.IsNil)
    {
        try
        {
            string baseName = td.BaseType.Kind switch
            {
                HandleKind.TypeDefinition => provider.GetTypeFromDefinition(mr, (TypeDefinitionHandle)td.BaseType, 0),
                HandleKind.TypeReference => provider.GetTypeFromReference(mr, (TypeReferenceHandle)td.BaseType, 0),
                _ => td.BaseType.Kind.ToString()
            };
            sb.AppendLine($"    : {baseName}");
        }
        catch { }
    }

    foreach (var fh in td.GetFields())
    {
        var f = mr.GetFieldDefinition(fh);
        try
        {
            string ft = f.DecodeSignature(provider, null);
            string fa = f.Attributes.ToString();
            string constVal = "";
            if ((f.Attributes & FieldAttributes.Literal) != 0 && !f.GetDefaultValue().IsNil)
            {
                var c = mr.GetConstant(f.GetDefaultValue());
                var bytes = mr.GetBlobBytes(c.Value);
                if (c.TypeCode == ConstantTypeCode.Int32 && bytes.Length >= 4)
                    constVal = " = " + BitConverter.ToInt32(bytes, 0);
                else if (c.TypeCode == ConstantTypeCode.UInt32 && bytes.Length >= 4)
                    constVal = " = " + BitConverter.ToUInt32(bytes, 0);
            }
            sb.AppendLine($"    field  {ft} {mr.GetString(f.Name)}{constVal}   [{fa}]");
        }
        catch (Exception e) { sb.AppendLine($"    field  <decode-fail {e.GetType().Name}> {mr.GetString(f.Name)}"); }
    }

    foreach (var ph in td.GetProperties())
    {
        var p = mr.GetPropertyDefinition(ph);
        try
        {
            var sig = p.DecodeSignature(provider, null);
            var acc = p.GetAccessors();
            string get = acc.Getter.IsNil ? "" : "get;";
            string set = acc.Setter.IsNil ? "" : "set;";
            string sta = "";
            if (!acc.Getter.IsNil) sta = mr.GetMethodDefinition(acc.Getter).Attributes.HasFlag(MethodAttributes.Static) ? " static" : "";
            sb.AppendLine($"    prop   {sig.ReturnType} {mr.GetString(p.Name)} {{ {get}{set} }}{sta}");
        }
        catch (Exception e) { sb.AppendLine($"    prop   <decode-fail {e.GetType().Name}> {mr.GetString(p.Name)}"); }
    }

    foreach (var mh in td.GetMethods())
    {
        var m = mr.GetMethodDefinition(mh);
        try
        {
            var sig = m.DecodeSignature(provider, null);
            var ps = sig.ParameterTypes.Select((t, i) => $"{t} p{i}");
            string sta = m.Attributes.HasFlag(MethodAttributes.Static) ? "static " : "";
            string vis = m.Attributes.HasFlag(MethodAttributes.Public) ? "public" :
                         m.Attributes.HasFlag(MethodAttributes.Family) ? "protected" :
                         m.Attributes.HasFlag(MethodAttributes.Assembly) ? "internal" : "private";
            sb.AppendLine($"    method {vis} {sta}{sig.ReturnType} {mr.GetString(m.Name)}({string.Join(", ", ps)})");
        }
        catch (Exception e) { sb.AppendLine($"    method <decode-fail {e.GetType().Name}> {mr.GetString(m.Name)}"); }
    }

    // nested types
    foreach (var nh in td.GetNestedTypes())
    {
        var nt = mr.GetTypeDefinition(nh);
        sb.AppendLine($"    nested {mr.GetString(nt.Name)}");
    }

    sb.AppendLine();
}

File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"wrote {outPath} ({sb.Length} chars, {mr.TypeDefinitions.Count} types total)");
return 0;

internal sealed class NameProvider : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader _mr;
    public NameProvider(MetadataReader mr) => _mr = mr;

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString()
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        string n = reader.GetString(td.Name);
        string ns = reader.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        string n = reader.GetString(tr.Name);
        string ns = reader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType + "<" + string.Join(", ", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
}
