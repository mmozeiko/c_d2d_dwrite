using System.Diagnostics;
using System.IO.Compression;
using Mono.Cecil;

const int RetWidth = 33;
const int NameWidth = 61;

var Fields = new HashSet<FieldDefinition>();
var Typedefs = new HashSet<TypeDefinition>();
var Enums = new HashSet<TypeDefinition>();
var Structs = new HashSet<TypeDefinition>();
var Interfaces = new HashSet<TypeDefinition>();
var Functions = new HashSet<MethodDefinition>();

string GetParamType(ParameterDefinition arg)
{
    string str = "";
    if (arg.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.FullName == "Windows.Win32.Foundation.Metadata.ConstAttribute") != null)
    {
        str = "const ";
    }
    str += GetSimpleType(arg.ParameterType);
    return str;
}

long GetSimpleIntSize(MetadataType type)
{
    switch (type)
    {
        case MetadataType.SByte: return 8;
        case MetadataType.Byte: return 8;
        case MetadataType.Int16: return 16;
        case MetadataType.UInt16: return 16;
        case MetadataType.Int32: return 32;
        case MetadataType.UInt32: return 32;
        case MetadataType.Int64: return 64;
        case MetadataType.UInt64: return 64;
    }
    throw new InvalidOperationException();
}

string GetSimpleType(TypeReference type)
{
    if (type.IsPointer)
    {
        var elementType = ((TypeSpecification)type).ElementType;
        return GetSimpleType(elementType) + "*";
    }
    if (type.MetadataType == MetadataType.Void)
    {
        return "void";
    }
    if (type.FullName == "System.Guid")
    {
        return "GUID";
    }
    if (type.Name == "PWSTR")
    {
        return "WCHAR*";
    }
    if (type.IsPrimitive)
    {
        switch (type.MetadataType)
        {
            case MetadataType.SByte: return "INT8";
            case MetadataType.Byte: return "UINT8";
            case MetadataType.Int16: return "INT16";
            case MetadataType.UInt16: return "UINT16";
            case MetadataType.Int32: return "INT32";
            case MetadataType.UInt32: return "UINT32";
            case MetadataType.Int64: return "INT64";
            case MetadataType.UInt64: return "UINT64";
            case MetadataType.Single: return "FLOAT";
            case MetadataType.Double: return "DOUBLE";
            default:
                throw new NotSupportedException();
        }
    }
    if (type.Name == "DWRITE_GLYPH_IMAGE_FORMATS")
    {
        return "enum " + type.Name;
    }
    if (type.Resolve().IsInterface)
    {
        return type.Name + "*";
    }
    return type.Name;
}

bool IsReturnTypeFixNeeded(TypeReference retType)
{
    var retTypeStr = GetSimpleType(retType);
    if (retTypeStr != "HRESULT" && retTypeStr != "HANDLE" && retTypeStr != "HDC" && retTypeStr != "HWND" && retTypeStr != "BOOL"
        && retType.IsValueType && !retType.IsPrimitive && !retType.Resolve().IsEnum)
    {
        return true;
    }
    return false;
}

void WriteConstants(StreamWriter fs)
{
    fs.WriteLine();
    fs.WriteLine("// constants");
    fs.WriteLine();

    int guidMaxLen = Fields.Where(field => field.FieldType.FullName == "System.Guid").Aggregate(0, (max, field) => Math.Max(max, field.Name.Length));

    foreach (var field in Fields.OrderBy(field => field.Name))
    {
        if (field.FieldType.FullName == "System.Guid")
        {
            var guidAttr = field.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.FullName == "Windows.Win32.Foundation.Metadata.GuidAttribute");
            Debug.Assert(guidAttr != null);
            var ga = guidAttr.ConstructorArguments;
            var name = $"{field.Name},";
            fs.WriteLine($"DEFINE_GUID({name.PadRight(guidMaxLen+1)} 0x{ga[0].Value:x8}, 0x{ga[1].Value:x4}, 0x{ga[2].Value:x4}, {string.Join(", ", ga.Skip(3).Select(x => $"0x{x.Value:x2}"))});");
        }
        else
        {
            Debug.Assert(field.HasConstant);

            if (field.FieldType.Name == "HRESULT")
            {
                fs.WriteLine($"#ifndef {field.Name}");
                fs.WriteLine($"#define {field.Name} ((HRESULT)0x{(uint)(int)field.Constant:x8}L)");
                fs.WriteLine($"#endif");
            }
            else if (field.Constant is int)
            {
                fs.WriteLine($"#define {field.Name} {(int)field.Constant}");
            }
            else if (field.Constant is uint)
            {
                if ((uint)field.Constant < 0x80000000)
                {
                    fs.WriteLine($"#define {field.Name} {(int)(uint)field.Constant}");
                }
                else
                {
                    fs.WriteLine($"#define {field.Name} 0x{(uint)field.Constant:x8}");
                }
            }
            else if (field.Constant is float)
            {
                var floatStr = $"{(float)field.Constant}";
                var suffix = floatStr.Contains('.') ? "f" : ".f";
                fs.WriteLine($"#define {field.Name} {floatStr}{suffix}");
            }
            else
            {
                throw new NotImplementedException();
            }
        }
    }
}

void WriteTypedefs(StreamWriter fs)
{
    if (Typedefs.Count == 0)
    {
        return;
    }

    fs.WriteLine();
    fs.WriteLine("// typedefs");
    fs.WriteLine();

    foreach (var td in Typedefs.OrderBy(td => td.Name))
    {
        var method = td.Methods.First(m => m.Name == "Invoke");
        fs.Write($"typedef {GetSimpleType(method.ReturnType)} (CALLBACK* {td.Name})(");
        fs.Write(string.Join(", ", method.Parameters.Select(arg => $"{GetParamType(arg)} {arg.Name}")));
        fs.Write($");");
        fs.WriteLine();
    }
}

void WriteEnums(StreamWriter fs)
{
    fs.WriteLine();
    fs.WriteLine("// enums");

    foreach (var e in Enums.OrderBy(x => x.Name))
    {
        fs.WriteLine();

        var name = e.Name;
        fs.WriteLine($"typedef enum {name} {{");

        int maxLen = e.Fields.Where(field => !field.IsSpecialName).Max(field => field.Name.Length);

        foreach (var field in e.Fields.Where(field => !field.IsSpecialName))
        {
            var fieldConst = field.Constant;
            string value;
            if (fieldConst is int)
            {
                int intValue = (int)fieldConst;
                if (intValue < 0)
                {
                    value = $"0x{(uint)intValue:x8}L";
                }
                else
                {
                    value = $"{intValue}";
                }
                }
            else if (fieldConst is uint)
            {
                value = $"0x{(uint)fieldConst:x8}";
            }
            else
            {
                throw new InvalidOperationException();
            }
            fs.WriteLine($"    {field.Name.PadRight(maxLen)} = {value},");
        }
        fs.WriteLine($"}} {name};");
    }
}

void WriteStructs(StreamWriter fs)
{
    fs.WriteLine();
    fs.WriteLine("// structs");

    var structs = new List<TypeDefinition>(Structs.OrderBy(s => s.Name));
    var structOrder = new List<TypeDefinition>(structs.Count);

    Action<Mono.Collections.Generic.Collection<FieldDefinition>>? structFieldSorter = null;
    Action<int>? structSorter = null;

    structFieldSorter = fields =>
    {
        foreach (var field in fields)
        {
            if (field.FieldType.IsArray)
            {
                var arrayType = (ArrayType)field.FieldType;
                Debug.Assert(arrayType.ElementType.IsPrimitive);
            }
            else
            {
                var type = field.FieldType.Resolve();
                if (type.IsNested)
                {
                    structFieldSorter?.Invoke(type.Fields);
                }
                else if (!type.IsPrimitive)
                {
                    var fieldType = (TypeDefinition)type.GetElementType();
                    if (fieldType != null)
                    {
                        int index = structs.FindIndex(s => s == fieldType);
                        if (index != -1)
                        {
                            structSorter?.Invoke(index);
                        }
                    }
                }
            }
        }
    };

    structSorter = index =>
    {
        var s = structs[index];
        structs.RemoveAt(index);
        structFieldSorter(s.Fields);
        structOrder.Add(s);
    };

    while (structs.Count != 0)
    {
        structSorter(0);
    }

    foreach (var s in structOrder)
    {
        fs.WriteLine();

        int maxLen = s.Fields.Where(field => !field.FieldType.Resolve().IsNested).Aggregate(0, (max, field) => Math.Max(max, GetSimpleType(field.FieldType).Length));

        var name = s.Name;
        var typeName = (s.Attributes & TypeAttributes.ExplicitLayout) != 0 ? "union" : "struct";
        
        fs.WriteLine($"typedef {typeName} {name} {{");
        foreach (var field in s.Fields)
        {
            WriteField(fs, 1, maxLen, field);
        }
        fs.WriteLine($"}} {name};");
    }
}

void WriteField(StreamWriter fs, int level, int maxLen, FieldDefinition field)
{
    string indent = string.Empty.PadRight(level * 4);
    if (field.FieldType.IsArray)
    {
        var arrayType = (ArrayType)field.FieldType;
        Debug.Assert(arrayType.Dimensions.Count == 1);
        Debug.Assert(arrayType.Dimensions[0].LowerBound == 0);

        fs.WriteLine($"{indent}{GetSimpleType(arrayType.ElementType).PadRight(maxLen)} {field.Name}[{arrayType.Dimensions[0].UpperBound + 1}];");
    }
    else
    {
        var type = field.FieldType.Resolve();
        if (type.IsNested)
        {
            maxLen = type.Fields.Where(nested => !nested.FieldType.Resolve().IsNested).Aggregate(0, (max, nested) => Math.Max(max, GetSimpleType(nested.FieldType).Length));

            fs.WriteLine($"{indent}struct {{");
            foreach (var nestedField in type.Fields)
            {
                WriteField(fs, level + 1, maxLen, nestedField);
            }
            fs.WriteLine($"{indent}}} {field.Name};");
        }
        else
        {
            var bitfields = field.CustomAttributes
                .Where(attr => attr.AttributeType.FullName == "Windows.Win32.Foundation.Metadata.NativeBitfieldAttribute")
                .OrderBy(attr => (long)attr.ConstructorArguments[1].Value)
                .ToArray();
            if (bitfields.Length == 0)
            {
                fs.WriteLine($"{indent}{GetSimpleType(field.FieldType).PadRight(maxLen)} {field.Name};");
            }
            else
            {
                Debug.Assert(field.Name == "_bitfield");

                long nextOffset = 0;
                long totalSize = GetSimpleIntSize(type.MetadataType);

                foreach (var bitfield in bitfields)
                {
                    var args = bitfield.ConstructorArguments;
                    var bitname = (string)args[0].Value;
                    var offset = (long)args[1].Value;
                    var length = (long)args[2].Value;

                    Debug.Assert(offset == nextOffset);
                    Debug.Assert(offset + length <= totalSize);

                    fs.WriteLine($"{indent}{GetSimpleType(field.FieldType).PadRight(maxLen)} {bitname} : {length};");

                    nextOffset = offset + length;
                }
            }
        }
    }
}

void WriteMethodsRecursive(StreamWriter fs, string baseType, TypeDefinition type, HashSet<string> names, ref uint index)
{
    if (type.Interfaces.Count != 0)
    {
        Debug.Assert(type.Interfaces.Count == 1);
        WriteMethodsRecursive(fs, baseType, type.Interfaces[0].InterfaceType.Resolve(), names, ref index);
    }

    foreach (var member in type.Methods)
    {
        var memberName = member.Name;

        if (names.Contains(memberName))
        {
            int idx = 1;
            while (true)
            {
                var newName = $"{memberName}{idx}";
                if (!names.Contains(newName))
                {
                    Console.WriteLine($"Renaming {baseType}_{memberName} to {baseType}_{newName}");
                    memberName = newName;
                    break;
                }
                idx++;
            }
        }
        names.Add(memberName);

        var retType = GetSimpleType(member.ReturnType);

        var obsolete = member.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.FullName == "System.ObsoleteAttribute");
        if (obsolete != null)
        {
            var message = obsolete.ConstructorArguments[0].Value as string;
            fs.WriteLine($"__declspec(deprecated(\"{message}\"))");
        }


        var name = $"{baseType}_{memberName}";
        fs.Write($"static inline {retType.PadRight(RetWidth)} {name.PadRight(NameWidth)}({baseType}* this");
        if (member.Parameters.Count != 0)
        {
            fs.Write(", ");
        }
        fs.Write(string.Join(", ", member.Parameters.Select(arg => $"{GetParamType(arg)} {arg.Name}")));
        fs.Write($") {{");

        if (IsReturnTypeFixNeeded(member.ReturnType))
        {
            Debug.Assert(member.Parameters.Count == 0);
            fs.Write($" {retType} _return;");
            fs.Write($" ((void (WINAPI*)({baseType}*, {retType}*))this->v->tbl[{index}])(this, &_return);");
            fs.Write($" return _return;");
        }
        else
        {
            if (retType != "void")
            {
                fs.Write($" return");
            }
            fs.Write($" (({retType} (WINAPI*)({baseType}*");
            if (member.Parameters.Count != 0)
            {
                fs.Write(", ");
            }
            fs.Write(string.Join(", ", member.Parameters.Select(arg => GetParamType(arg))));
            fs.Write($"))this->v->tbl[{index}])(this");
            if (member.Parameters.Count != 0)
            {
                fs.Write(", ");
            }
            fs.Write(string.Join(", ", member.Parameters.Select(arg => arg.Name)));
            fs.Write($");");
        }
        fs.WriteLine($" }}");
        index++;
    }
}

void WriteInterfaces(StreamWriter fs)
{
    fs.WriteLine();
    fs.WriteLine("// interfaces");
    fs.WriteLine();

    int maxLen = Interfaces.Max(i => i.Name.Length);

    foreach (var i in Interfaces.OrderBy(i => i.Name))
    {
        var member = $"{i.Name}Vtbl*";
        fs.WriteLine($"typedef struct {i.Name.PadRight(maxLen)} {{ struct {{ void* tbl[]; }}* v; }} {i.Name};");
    }
}

void WriteMethods(StreamWriter fs)
{
    fs.WriteLine();
    fs.WriteLine("// methods");

    foreach (var i in Interfaces.OrderBy(i => i.Name))
    {
        fs.WriteLine();

        var names = new HashSet<string>();
        uint index = 0;
        WriteMethodsRecursive(fs, i.Name, i, names, ref index);
    }
}

void WriteGuids(StreamWriter fs)
{
    fs.WriteLine();
    fs.WriteLine("// guids");
    fs.WriteLine();

    int maxLen = Interfaces.Max(i => i.Name.Length);

    foreach (var i in Interfaces.OrderBy(i => i.Name))
    {
        var guidAttr = i.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.FullName == "Windows.Win32.Foundation.Metadata.GuidAttribute");
        Debug.Assert(guidAttr != null);
        var ga = guidAttr.ConstructorArguments;
        var name = $"IID_{i.Name},";
        fs.WriteLine($"DEFINE_GUID({name.PadRight(maxLen + 5)} 0x{ga[0].Value:x8}, 0x{ga[1].Value:x4}, 0x{ga[2].Value:x4}, {string.Join(", ", ga.Skip(3).Select(x => $"0x{x.Value:x2}"))});");
    }
}

void WriteFunctions(StreamWriter fs)
{
    fs.WriteLine();
    fs.WriteLine("// functions");
    fs.WriteLine();

    int maxRet = Functions.Max(fun => GetSimpleType(fun.ReturnType).Length);
    int maxLen = Functions.Max(fun => fun.Name.Length) + 1;

    foreach (var fun in Functions.OrderBy(fun => fun.Name))
    {
        fs.Write($"EXTERN_C {GetSimpleType(fun.ReturnType).PadRight(maxRet)} DECLSPEC_IMPORT WINAPI {fun.Name.PadRight(maxLen)}(");
        fs.Write(string.Join(", ", fun.Parameters.Select(arg => $"{GetParamType(arg)} {arg.Name}")));
        fs.Write($") WIN_NOEXCEPT;");
        fs.WriteLine();
    }
}

void Parse(ModuleDefinition module, string output, string ns)
{
    Fields.Clear();
    Typedefs.Clear();
    Enums.Clear();
    Structs.Clear();
    Interfaces.Clear();
    Functions.Clear();

    foreach (var type in module.GetTypes().Where(type => type.Namespace == ns))
    {
        var name = type.Name;
        if (name == "Apis")
        {
            foreach (var field in type.Fields)
            {
                Fields.Add(field);
            }
            foreach (var method in type.Methods)
            {
                Functions.Add(method);
            }
        }
        else if (type.IsEnum)
        {
            if (type.Name != "DWRITE_MEASURING_MODE" &&
                type.Name != "DWRITE_GLYPH_IMAGE_FORMATS")
            {
                Enums.Add(type);
            }
        }
        else if (type.IsInterface)
        {
            Interfaces.Add(type);
        }
        else if (type.IsClass && type.IsValueType)
        {
            Structs.Add(type);
        }
        else if (type.BaseType.FullName == "System.MulticastDelegate")
        {
            Typedefs.Add(type);
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    if (ns == "Windows.Win32.Graphics.Direct2D")
    {
        foreach (var type in module.GetTypes().Where(type => type.Namespace == "Windows.Win32.Graphics.Direct2D.Common"))
        {
            var name = type.Name;

            // only add types that are not already in dcommon.h header
            if (name != "D2D1_COMPOSITE_MODE" &&
                name != "D2D1_BLEND_MODE" &&
                name != "D2D1_FILL_MODE" &&
                name != "D2D1_PATH_SEGMENT" &&
                name != "D2D1_FIGURE_BEGIN" &&
                name != "D2D1_FIGURE_END" &&
                name != "D2D1_BEZIER_SEGMENT" &&
                name != "ID2D1SimplifiedGeometrySink")
            {
                continue;
            }

            if (type.IsEnum)
            {
                Enums.Add(type);
            }
            else if (type.IsInterface)
            {
                Interfaces.Add(type);
            }
            else if (type.IsClass && type.IsValueType)
            {
                Structs.Add(type);
            }
            else
            {
                throw new NotImplementedException();
            }
        }
    }

    using (var fs = File.CreateText(output))
    {
        fs.WriteLine("#pragma once");
        fs.WriteLine();
        fs.WriteLine("// generated by https://github.com/mmozeiko/c_d2d_dwrite");
        fs.WriteLine();
        fs.WriteLine("#include <combaseapi.h>");
        if (ns == "Windows.Win32.Graphics.Direct2D")
        {
            fs.WriteLine("#include <dxgicommon.h>");
            fs.WriteLine("#include <d3dcommon.h>");
            fs.WriteLine("#include <d2dbasetypes.h>");
        }
        fs.WriteLine("#include <dcommon.h>");

        fs.WriteLine();
        if (ns == "Windows.Win32.Graphics.Direct2D")
        {
            fs.WriteLine("#pragma comment (lib, \"d2d1\")");
        }
        else
        {
            fs.WriteLine("#pragma comment (lib, \"dwrite\")");
        }

        fs.WriteLine();
        if (ns == "Windows.Win32.Graphics.Direct2D")
        {
            fs.WriteLine("typedef D2D_COLOR_F D2D1_COLOR_F;");
            fs.WriteLine("typedef struct DWRITE_GLYPH_RUN DWRITE_GLYPH_RUN;");
            fs.WriteLine("typedef struct DWRITE_GLYPH_RUN_DESCRIPTION DWRITE_GLYPH_RUN_DESCRIPTION;");

            fs.WriteLine();
            fs.WriteLine("typedef interface IDXGIDevice                 IDXGIDevice;");
            fs.WriteLine("typedef interface IDXGISurface                IDXGISurface;");
            fs.WriteLine("typedef interface IWICImagingFactory          IWICImagingFactory;");
            fs.WriteLine("typedef interface IDWriteTextFormat           IDWriteTextFormat;");
            fs.WriteLine("typedef interface IDWriteTextLayout           IDWriteTextLayout;");
            fs.WriteLine("typedef interface IDWriteRenderingParams      IDWriteRenderingParams;");
            fs.WriteLine("typedef interface IDWriteFontFace             IDWriteFontFace;");
            fs.WriteLine("typedef interface IWICBitmapSource            IWICBitmapSource;");
            fs.WriteLine("typedef interface IWICBitmap                  IWICBitmap;");
            fs.WriteLine("typedef interface IWICColorContext            IWICColorContext;");
            fs.WriteLine("typedef interface IPrintDocumentPackageTarget IPrintDocumentPackageTarget;");
        }
        else
        {
            fs.WriteLine("typedef interface ID2D1SimplifiedGeometrySink ID2D1SimplifiedGeometrySink;");
            fs.WriteLine("typedef interface ID2D1SimplifiedGeometrySink IDWriteGeometrySink;");
        }

        WriteInterfaces(fs);
        WriteConstants(fs);
        WriteTypedefs(fs);
        WriteEnums(fs);
        WriteStructs(fs);
        WriteMethods(fs);
        WriteGuids(fs);
        WriteFunctions(fs);
    }
}

// https://www.nuget.org/packages/Microsoft.Windows.SDK.Win32Metadata/
using var fs = File.OpenRead(File.Exists("Generator/Windows.Win32.winmd.gz") ? "Generator/Windows.Win32.winmd.gz" : "../../../Windows.Win32.winmd.gz");
using var gz = new GZipStream(fs, CompressionMode.Decompress);
using var stream = new MemoryStream();
gz.CopyTo(stream);
stream.Seek(0, SeekOrigin.Begin);
using var module = ModuleDefinition.ReadModule(stream);

Parse(module, "cdwrite.h", "Windows.Win32.Graphics.DirectWrite");
Parse(module, "cd2d.h", "Windows.Win32.Graphics.Direct2D");
