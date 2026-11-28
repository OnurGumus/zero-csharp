// Finds static fields that zerocore and lib/ fill outside a static
// constructor without ObjectAllocator.Lasting(): caches made on first use.
//
// Why: inside an object scope (lib/Arena.cs: AllocateObjectsHere,
// OpenObjectScope) every `new` goes to the scope's arena. A cache a library
// fills the first time it is used keeps what it made in a static field for
// the rest of the program; if that first time is inside a scope, the static
// is left pointing at memory the scope frees (PR #182 fixed the culture and
// time zone caches after exactly that). Static constructors are safe: they
// run with calloc (ObjectAllocator.RunStaticConstructor). Elsewhere a store
// must be under `using (ObjectAllocator.Lasting())`, or store something that
// can't come from a scope, or be listed in allowed.txt with the reason.
//
//   static constructor      stsfld   fine: runs outside every scope
//   Lasting() in the method stsfld   fine
//   ldnull / ldstr          stsfld   fine: nothing allocated
//   anything else           stsfld   reported, unless allowed.txt says why
//
// Also reported: C# lambda caches (<>c.<>9__*) and method group caches
// (<>O.<0>__*): a lambda that captures nothing, or a static method turned
// into a delegate, is made the first time it runs and kept in a static,
// which inside a scope is the same mistake. The fix is a static readonly
// field holding the delegate, which a static constructor fills.
//
// The check reads IL, and doesn't follow values: it can't tell that a value
// came from a static constructor, or from memory that lives for good. Those
// cases go in allowed.txt, one line each, with the reason after " # ".
//
// Usage: dotnet run -- <allowed.txt> <assembly.dll> ...
// Exit code 1 if something is reported or allowed.txt lists a store that
// no longer exists.

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: <allowed.txt> <assembly.dll> ...");
    return 2;
}

var allowed = new Dictionary<string, string>();
foreach (string raw in File.ReadAllLines(args[0]))
{
    string line = raw.Trim();
    if (line.Length == 0 || line.StartsWith('#'))
        continue;
    int hash = line.IndexOf(" # ", StringComparison.Ordinal);
    if (hash < 0)
    {
        Console.Error.WriteLine($"{args[0]}: no reason (\" # why\") on: {line}");
        return 2;
    }
    allowed[line[..hash].Trim()] = line[(hash + 3)..].Trim();
}

var modules = args.Skip(1).Select(path => new Module(path)).ToList();
var types = new Dictionary<string, (Module, TypeDefinitionHandle)>();
foreach (Module m in modules)
    foreach (TypeDefinitionHandle h in m.Reader.TypeDefinitions)
        types.TryAdd(m.FullName(h), (m, h));

var found = new SortedSet<string>(StringComparer.Ordinal);
var seen = new HashSet<string>(StringComparer.Ordinal);
foreach (Module m in modules)
    m.Check(types, found, seen);

int failures = 0;
foreach (string store in found)
{
    if (allowed.ContainsKey(store))
        continue;
    Console.WriteLine($"filled on first use, outside Lasting(): {store}");
    failures++;
}
foreach (string store in allowed.Keys)
{
    if (!seen.Contains(store))
    {
        Console.WriteLine($"allowed.txt lists a store that no longer exists: {store}");
        failures++;
    }
}
if (failures > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{failures} problem(s). Make the value in a static constructor, store it under");
    Console.WriteLine("`using (ObjectAllocator.Lasting())`, or add the line to allowed.txt with the reason.");
    return 1;
}
Console.WriteLine($"ok: no static filled on first use outside Lasting(), but the {found.Count} allowed.txt explains");
return 0;

sealed class Module
{
    public readonly MetadataReader Reader;
    readonly PEReader _pe;
    readonly Dictionary<TypeDefinitionHandle, bool> _holdsReferences = new();

    public Module(string path)
    {
        _pe = new PEReader(File.OpenRead(path));
        Reader = _pe.GetMetadataReader();
    }

    public string FullName(TypeDefinitionHandle handle)
    {
        TypeDefinition t = Reader.GetTypeDefinition(handle);
        string name = Reader.GetString(t.Name);
        if (t.IsNested)
            return FullName(t.GetDeclaringType()) + "/" + name;
        string ns = Reader.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    string TypeRefName(TypeReferenceHandle handle)
    {
        TypeReference t = Reader.GetTypeReference(handle);
        string name = Reader.GetString(t.Name);
        if (t.ResolutionScope.Kind == HandleKind.TypeReference)
            return TypeRefName((TypeReferenceHandle)t.ResolutionScope) + "/" + name;
        string ns = Reader.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    // The type a member belongs to, by name.
    string ParentName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeDefinition => FullName((TypeDefinitionHandle)parent),
        HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)parent),
        HandleKind.TypeSpecification => SpecName((TypeSpecificationHandle)parent),
        _ => "?",
    };

    string SpecName(TypeSpecificationHandle handle)
    {
        // A generic instance: its generic type's name is enough here.
        BlobReader blob = Reader.GetBlobReader(Reader.GetTypeSpecification(handle).Signature);
        var code = blob.ReadSignatureTypeCode();
        if (code == SignatureTypeCode.GenericTypeInstance)
        {
            blob.ReadSignatureTypeCode();
            return ParentName(blob.ReadTypeHandle());
        }
        return "?";
    }

    // Whether a field of this type can point at an object.
    bool HoldsReferences(BlobHandle signature, Dictionary<string, (Module, TypeDefinitionHandle)> types)
    {
        BlobReader blob = Reader.GetBlobReader(signature);
        blob.ReadSignatureHeader();
        return HoldsReferences(ref blob, types);
    }

    bool HoldsReferences(ref BlobReader blob, Dictionary<string, (Module, TypeDefinitionHandle)> types)
    {
        var code = blob.ReadSignatureTypeCode();
        switch (code)
        {
            case SignatureTypeCode.Boolean: case SignatureTypeCode.Char:
            case SignatureTypeCode.SByte: case SignatureTypeCode.Byte:
            case SignatureTypeCode.Int16: case SignatureTypeCode.UInt16:
            case SignatureTypeCode.Int32: case SignatureTypeCode.UInt32:
            case SignatureTypeCode.Int64: case SignatureTypeCode.UInt64:
            case SignatureTypeCode.Single: case SignatureTypeCode.Double:
            case SignatureTypeCode.IntPtr: case SignatureTypeCode.UIntPtr:
            case SignatureTypeCode.Pointer: case SignatureTypeCode.FunctionPointer:
                return false;
            case SignatureTypeCode.RequiredModifier: case SignatureTypeCode.OptionalModifier:
                blob.ReadTypeHandle();
                return HoldsReferences(ref blob, types);
            case SignatureTypeCode.TypeHandle:
            {
                // ELEMENT_TYPE_CLASS or VALUETYPE: the byte before says which.
                EntityHandle handle = blob.ReadTypeHandle();
                return !IsValueType(handle, types, out var def) || def is { } d && d.Item1.StructHoldsReferences(d.Item2, types);
            }
            case SignatureTypeCode.GenericTypeInstance:
            {
                int kind = blob.ReadCompressedInteger(); // CLASS or VALUETYPE
                EntityHandle handle = blob.ReadTypeHandle();
                int count = blob.ReadCompressedInteger();
                bool any = false;
                for (int i = 0; i < count; i++)
                    any |= HoldsReferences(ref blob, types);
                if (kind == 0x12)
                    return true;
                // A generic struct: its fields, or a type argument that holds them.
                return any || (IsValueType(handle, types, out var def) && def is { } d && d.Item1.StructHoldsReferences(d.Item2, types));
            }
            default:
                return true; // object, string, arrays, type parameters
        }
    }

    bool IsValueType(EntityHandle handle, Dictionary<string, (Module, TypeDefinitionHandle)> types, out (Module, TypeDefinitionHandle)? def)
    {
        string name = ParentName(handle);
        def = types.TryGetValue(name, out var d) ? d : null;
        if (def is null)
            return false;
        var (m, h) = def.Value;
        TypeDefinition t = m.Reader.GetTypeDefinition(h);
        if (t.BaseType.IsNil)
            return false;
        string baseName = m.ParentName(t.BaseType);
        return baseName == "System.ValueType" || baseName == "System.Enum";
    }

    bool StructHoldsReferences(TypeDefinitionHandle handle, Dictionary<string, (Module, TypeDefinitionHandle)> types)
    {
        if (_holdsReferences.TryGetValue(handle, out bool known))
            return known;
        _holdsReferences[handle] = false; // a struct can't contain itself
        bool any = false;
        foreach (FieldDefinitionHandle f in Reader.GetTypeDefinition(handle).GetFields())
        {
            FieldDefinition field = Reader.GetFieldDefinition(f);
            if ((field.Attributes & System.Reflection.FieldAttributes.Static) == 0)
                any |= HoldsReferences(field.Signature, types);
        }
        _holdsReferences[handle] = any;
        return any;
    }

    // A field token: its owner's name, its own name, whether it is static
    // and whether it can hold a reference. Only this module's own fields.
    (string Owner, string Name, bool Static, bool References, bool ReadOnly)? Field(int token, Dictionary<string, (Module, TypeDefinitionHandle)> types)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        // A field of a generic type of this assembly (a lambda cache in a
        // generic method's <>c class, say) is a member reference to it.
        if (handle.Kind == HandleKind.MemberReference)
        {
            MemberReference r = Reader.GetMemberReference((MemberReferenceHandle)handle);
            if (r.Parent.Kind != HandleKind.TypeSpecification || r.GetKind() != MemberReferenceKind.Field)
                return null;
            BlobReader blob = Reader.GetBlobReader(Reader.GetTypeSpecification((TypeSpecificationHandle)r.Parent).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                return null;
            blob.ReadSignatureTypeCode();
            EntityHandle generic = blob.ReadTypeHandle();
            if (generic.Kind != HandleKind.TypeDefinition)
                return null; // another assembly's: checked there
            string wanted = Reader.GetString(r.Name);
            handle = default;
            foreach (FieldDefinitionHandle fh in Reader.GetTypeDefinition((TypeDefinitionHandle)generic).GetFields())
                if (Reader.GetString(Reader.GetFieldDefinition(fh).Name) == wanted)
                    handle = fh;
            if (handle.IsNil)
                return null;
        }
        if (handle.Kind != HandleKind.FieldDefinition)
            return null; // another assembly's: checked there
        FieldDefinition f = Reader.GetFieldDefinition((FieldDefinitionHandle)handle);
        return (FullName(f.GetDeclaringType()), Reader.GetString(f.Name),
            (f.Attributes & System.Reflection.FieldAttributes.Static) != 0,
            HoldsReferences(f.Signature, types),
            (f.Attributes & System.Reflection.FieldAttributes.InitOnly) != 0);
    }

    (string Owner, string Name) Method(int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                MethodDefinition m = Reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return (FullName(m.GetDeclaringType()), Reader.GetString(m.Name));
            }
            case HandleKind.MemberReference:
            {
                MemberReference m = Reader.GetMemberReference((MemberReferenceHandle)handle);
                return (ParentName(m.Parent), Reader.GetString(m.Name));
            }
            case HandleKind.MethodSpecification:
                return Method(MetadataTokens.GetToken(Reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method));
        }
        return ("?", "?");
    }

    // Methods that run only outside scopes: static constructors, methods
    // that call Lasting() (the store is taken to be inside its using block),
    // and, until nothing changes, methods only those call. A method nothing
    // in this assembly calls may be called from anywhere, so it isn't one.
    HashSet<MethodDefinitionHandle> OutsideScopes(Dictionary<MethodDefinitionHandle, List<Instruction>> bodies)
    {
        var callers = new Dictionary<MethodDefinitionHandle, List<MethodDefinitionHandle>>();
        var outside = new HashSet<MethodDefinitionHandle>();
        // Methods made into delegates: they run whenever the delegate is
        // called, not where it was made, so never outside scopes for sure.
        var delegates = new HashSet<MethodDefinitionHandle>();
        foreach (var (method, il) in bodies)
        {
            string name = Reader.GetString(Reader.GetMethodDefinition(method).Name);
            if (name == ".cctor" || il.Any(i => IsCall(i) && Method(i.Token) is ("System.Runtime.ObjectAllocator", "Lasting")))
                outside.Add(method);
            foreach (Instruction i in il)
            {
                // A call, or a delegate made from the method (ldftn).
                if (!IsCall(i) && i.Op != 0xFE06 && i.Op != 0xFE07)
                    continue;
                EntityHandle target = MetadataTokens.EntityHandle(i.Token);
                if (target.Kind == HandleKind.MethodSpecification)
                    target = Reader.GetMethodSpecification((MethodSpecificationHandle)target).Method;
                if (target.Kind != HandleKind.MethodDefinition)
                    continue;
                var callee = (MethodDefinitionHandle)target;
                if (i.Op == 0xFE06 || i.Op == 0xFE07)
                    delegates.Add(callee);
                if (!callers.TryGetValue(callee, out var list))
                    callers[callee] = list = new();
                list.Add(method);
            }
        }
        // Virtual methods and those of other kinds may be called in ways not
        // seen here: only private and assembly methods that aren't virtual.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (callee, list) in callers)
            {
                if (outside.Contains(callee) || delegates.Contains(callee))
                    continue;
                var attributes = Reader.GetMethodDefinition(callee).Attributes;
                var access = attributes & System.Reflection.MethodAttributes.MemberAccessMask;
                if ((attributes & System.Reflection.MethodAttributes.Virtual) != 0
                    || (access != System.Reflection.MethodAttributes.Private && access != System.Reflection.MethodAttributes.Assembly))
                    continue;
                if (list.All(outside.Contains))
                {
                    outside.Add(callee);
                    changed = true;
                }
            }
        }
        return outside;
    }

    static bool IsCall(Instruction i) => i.Op == 0x28 || i.Op == 0x6F || i.Op == 0x73; // call, callvirt, newobj

    public void Check(Dictionary<string, (Module, TypeDefinitionHandle)> types, SortedSet<string> found, HashSet<string> seen)
    {
        var bodies = new Dictionary<MethodDefinitionHandle, List<Instruction>>();
        foreach (MethodDefinitionHandle mh in Reader.MethodDefinitions)
        {
            MethodDefinition method = Reader.GetMethodDefinition(mh);
            if (method.RelativeVirtualAddress != 0)
                bodies[mh] = Decode(_pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes());
        }
        var outside = OutsideScopes(bodies);

        foreach (var (mh, il) in bodies)
        {
            MethodDefinition method = Reader.GetMethodDefinition(mh);
            string name = Reader.GetString(method.Name);
            if (name == ".cctor")
                continue;
            string where = FullName(method.GetDeclaringType()) + "::" + name;
            bool lasting = outside.Contains(mh);
            for (int n = 0; n < il.Count; n++)
            {
                Instruction i = il[n];
                bool store = i.Op == 0x80; // stsfld
                // ldsflda: an address taken to write through (Interlocked,
                // a struct's field, a ref passed on).
                bool address = i.Op == 0x7F && WritesThrough(il, n);
                if (!store && !address)
                    continue;
                var field = Field(i.Token, types);
                if (field is not { Static: true, References: true } f)
                    continue;
                // A static readonly field is written only by its static
                // constructor: an address of one outside it is for reading.
                if (address && f.ReadOnly)
                    continue;
                // null, a string literal (made by the compiler, never in an
                // arena), or another static's value (checked where that one
                // is filled).
                if (store && n > 0 && il[n - 1].Op is 0x14 or 0x72 or 0x7E)
                    continue;
                string entry = $"{where} -> {f.Owner}::{f.Name}";
                if (f.Name.StartsWith("<>9__", StringComparison.Ordinal))
                    entry = $"{where} -> lambda cache {f.Owner}::{f.Name}";
                else if (f.Owner.EndsWith("/<>O", StringComparison.Ordinal))
                    entry = $"{where} -> method group cache {f.Owner}::{f.Name}";
                seen.Add(entry);
                if (!lasting)
                    found.Add(entry);
            }
        }
    }

    // After an ldsflda: does something within a few instructions write
    // through the address, or pass it to a call (which may)?
    static bool WritesThrough(List<Instruction> il, int at)
    {
        for (int n = at + 1; n < il.Count && n <= at + 8; n++)
        {
            switch (il[n].Op)
            {
                case 0x7D: case 0x81: case 0x51: case 0xDF: // stfld, stobj, stind.ref, stind.i
                case 0x28: case 0x6F: case 0xFE15:          // call, callvirt, initobj
                    return true;
                case 0x7B: case 0x7C: case 0x71:            // ldfld, ldflda, ldobj: a read
                    return false;
            }
        }
        return false;
    }

    readonly record struct Instruction(int Op, int Token);

    static List<Instruction> Decode(byte[] code)
    {
        var list = new List<Instruction>();
        int p = 0;
        while (p < code.Length)
        {
            int op = code[p++];
            if (op == 0xFE)
                op = 0xFE00 | code[p++];
            int size = OperandSize(op);
            int token = size == 4 ? BitConverter.ToInt32(code.AsSpan()[p..]) : 0;
            if (op == 0x45) // switch: a count, then that many targets
                size = 4 + 4 * BitConverter.ToInt32(code.AsSpan()[p..]);
            list.Add(new Instruction(op, token));
            p += size;
        }
        return list;
    }

    static int OperandSize(int op) => op switch
    {
        >= 0x0E and <= 0x13 => 1,             // ldarg.s ... stloc.s
        0x1F => 1, 0x20 => 4, 0x21 => 8, 0x22 => 4, 0x23 => 8,
        0x27 or 0x28 or 0x29 => 4,             // jmp, call, calli
        >= 0x2B and <= 0x37 => 1,             // short branches
        >= 0x38 and <= 0x44 => 4,             // branches
        0x45 => 0,                             // switch (handled above)
        >= 0x6F and <= 0x75 => 4,             // callvirt ... isinst
        0x79 => 4,                             // unbox
        >= 0x7B and <= 0x81 => 4,             // fields, stobj
        0x8C or 0x8D or 0x8F => 4,             // box, newarr, ldelema
        0xA3 or 0xA4 or 0xA5 => 4,             // ldelem, stelem, unbox.any
        0xC2 or 0xC6 or 0xD0 => 4,             // refanyval, mkrefany, ldtoken
        0xDD => 4, 0xDE => 1,                  // leave, leave.s
        0xFE06 or 0xFE07 => 4,                 // ldftn, ldvirtftn
        >= 0xFE09 and <= 0xFE0E => 2,         // ldarg ... stloc
        0xFE12 or 0xFE19 => 1,                 // unaligned., no.
        0xFE15 or 0xFE16 or 0xFE1C => 4,       // initobj, constrained., sizeof
        _ => 0,
    };
}
