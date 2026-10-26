using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

// ildump <dll> <out> <typeFilter> [methodFilter]
// Prints the IL of matched methods, resolving call/callvirt/ldfld targets to readable names.
// Read-only metadata walk: nothing is loaded or executed.

if (args.Length < 3) { Console.Error.WriteLine("usage: ildump <dll> <out> <typeFilter> [methodFilter]"); return 2; }
string dllPath = args[0], outPath = args[1], typeFilter = args[2];
string? methodFilter = args.Length > 3 ? args[3] : null;

var opcodes = new Dictionary<ushort, OpCode>();
foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
{
    if (f.GetValue(null) is OpCode op) opcodes[(ushort)op.Value] = op;
}

using var fs = File.OpenRead(dllPath);
using var pe = new PEReader(fs);
var mr = pe.GetMetadataReader();
var sb = new StringBuilder();

string Full(TypeDefinition td)
{
    string n = mr.GetString(td.Name), ns = mr.GetString(td.Namespace);
    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}

string Ref(TypeReference tr)
{
    string n = mr.GetString(tr.Name), ns = mr.GetString(tr.Namespace);
    return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
}

string MemberRefName(MemberReferenceHandle h)
{
    var r = mr.GetMemberReference(h);
    string parent = r.Parent.Kind switch
    {
        HandleKind.TypeDefinition => Full(mr.GetTypeDefinition((TypeDefinitionHandle)r.Parent)),
        HandleKind.TypeReference => Ref(mr.GetTypeReference((TypeReferenceHandle)r.Parent)),
        HandleKind.TypeSpecification => "<spec>",
        _ => r.Parent.Kind.ToString()
    };
    return parent + "::" + mr.GetString(r.Name);
}

string TokenName(int token)
{
    var h = MetadataTokens.EntityHandle(token);
    return h.Kind switch
    {
        HandleKind.MethodDefinition => Full(mr.GetTypeDefinition(mr.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType())) + "::" + mr.GetString(mr.GetMethodDefinition((MethodDefinitionHandle)h).Name),
        HandleKind.FieldDefinition => mr.GetString(mr.GetFieldDefinition((FieldDefinitionHandle)h).Name),
        HandleKind.MemberReference => MemberRefName((MemberReferenceHandle)h),
        HandleKind.MethodSpecification => TokSpec((MethodSpecificationHandle)h),
        HandleKind.TypeDefinition => Full(mr.GetTypeDefinition((TypeDefinitionHandle)h)),
        HandleKind.TypeReference => Ref(mr.GetTypeReference((TypeReferenceHandle)h)),
        HandleKind.TypeSpecification => "<typespec>",
        HandleKind.UserString => "\"" + mr.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF)) + "\"",
        HandleKind.String => "\"" + mr.GetString(MetadataTokens.StringHandle(token & 0xFFFFFF)) + "\"",
        _ => "tok:" + h.Kind
    };
}

string TokSpec(MethodSpecificationHandle h)
{
    var ms = mr.GetMethodSpecification(h);
    var inner = ms.Method.Kind switch
    {
        HandleKind.MemberReference => MemberRefName((MemberReferenceHandle)ms.Method),
        HandleKind.MethodDefinition => "<def>",
        _ => ms.Method.Kind.ToString()
    };
    return inner + "<...>";
}

void Emit(string s) => sb.AppendLine(s);

foreach (var tdh in mr.TypeDefinitions)
{
    var td = mr.GetTypeDefinition(tdh);
    string full = Full(td);
    if (!full.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

    Emit($"===== {full}");

    foreach (var mh in td.GetMethods())
    {
        var m = mr.GetMethodDefinition(mh);
        string name = mr.GetString(m.Name);
        if (methodFilter != null && !name.Contains(methodFilter, StringComparison.OrdinalIgnoreCase)) continue;
        if (m.RelativeVirtualAddress == 0) continue;

        Emit($"-- {name}");
        MethodBodyBlock body;
        try { body = pe.GetMethodBody(m.RelativeVirtualAddress); }
        catch { Emit("   <no body>"); continue; }

        var il = body.GetILBytes() ?? Array.Empty<byte>();
        int i = 0;
        while (i < il.Length)
        {
            int start = i;
            ushort key = il[i];
            if (key == 0xFE) { key = (ushort)(0xFE00 | il[i + 1]); i += 2; }
            else i += 1;

            if (!opcodes.TryGetValue(key, out var op))
            {
                Emit($"   IL_{start:X4}: <unknown 0x{key:X4}>");
                break;
            }

            string operand = "";
            int size = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.ShortInlineBrTarget:
                    {
                        size = 1;
                        int target = i + 1 + (sbyte)il[i];
                        operand = $" IL_{target:X4}";
                        break;
                    }
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI: size = 4; break;
                case OperandType.InlineBrTarget:
                    {
                        size = 4;
                        int target = i + 4 + BitConverter.ToInt32(il, i);
                        operand = $" IL_{target:X4}";
                        break;
                    }
                case OperandType.ShortInlineR: size = 4; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: size = 8; break;
                case OperandType.InlineSwitch:
                    {
                        int n = BitConverter.ToInt32(il, i);
                        size = 4 + 4 * n;
                        int next = i + size;
                        var targets = new List<string>(n);
                        for (int k = 0; k < n; k++)
                        {
                            int target = next + BitConverter.ToInt32(il, i + 4 + 4 * k);
                            targets.Add($"IL_{target:X4}");
                        }
                        operand = " " + string.Join(", ", targets);
                        break;
                    }
                case OperandType.InlineString:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    {
                        size = 4;
                        int token = BitConverter.ToInt32(il, i);
                        if (op.OperandType != OperandType.InlineSig)
                        {
                            try { operand = " " + TokenName(token); }
                            catch { operand = $" tok0x{token:X8}"; }
                        }
                        break;
                    }
            }

            if (op.OperandType == OperandType.InlineString)
            {
                int token = BitConverter.ToInt32(il, i);
                operand = " \"" + mr.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF)) + "\"";
            }

            Emit($"   IL_{start:X4}: {op.Name}{operand}");
            i += size;
        }
        Emit("");
    }
    Emit("");
}

File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"wrote {outPath} ({sb.Length} chars)");
return 0;
