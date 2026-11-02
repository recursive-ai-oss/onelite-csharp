using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RecursiveAI.OneLite.Generators;

/// <summary>
/// Generates the row mapper for every type marked <c>[OneLiteRow]</c>: result columns map by name onto constructor parameters and settable properties
/// (records included), and the mapped members bind to statement parameters. The output uses no reflection, so it is safe for trimming and NativeAOT.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MappingGenerator : IIncrementalGenerator
{
    private const string RowAttribute = "RecursiveAI.OneLite.OneLiteRowAttribute";

    private static readonly DiagnosticDescriptor UnsupportedMember = new(
        "ONELITE001",
        "Property type cannot be mapped",
        "Property '{0}.{1}' has type '{2}', which OneLite cannot map to a column; mark it [OneLiteIgnore] or use a supported type",
        "OneLite",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor NoConstructor = new(
        "ONELITE002",
        "No usable constructor",
        "Type '{0}' has no accessible constructor; OneLite needs a public or internal constructor to create rows",
        "OneLite",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor NotAccessible = new(
        "ONELITE003",
        "Row type is not accessible",
        "Type '{0}' must be public or internal (and so must the types containing it) for OneLite to generate a mapper",
        "OneLite",
        DiagnosticSeverity.Error,
        true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<(Model? Model, ImmutableArray<Diagnostic> Diagnostics)> models = context.SyntaxProvider.ForAttributeWithMetadataName(
            RowAttribute,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, _) => Analyze(ctx));
        IncrementalValueProvider<ImmutableArray<(Model? Model, ImmutableArray<Diagnostic> Diagnostics)>> all = models.Collect();
        context.RegisterSourceOutput(all, static (spc, items) =>
        {
            var good = new List<Model>();
            foreach ((Model? model, ImmutableArray<Diagnostic> diags) in items)
            {
                foreach (Diagnostic d in diags)
                {
                    spc.ReportDiagnostic(d);
                }

                if (model is not null && diags.IsEmpty)
                {
                    good.Add(model);
                }
            }

            if (good.Count > 0)
            {
                spc.AddSource("OneLiteRowMappers.g.cs", Emit(good));
            }
        });
    }

    private sealed class Member
    {
        public string Property = "";
        public string Column = "";
        public string TypeName = "";
        public string SqlType = "";
        public bool NotNull;
        public bool IsKey;
        public bool Required;
        public bool CanRead;
        public int CtorIndex = -1;
        public bool InitOnly;
        public string ReadExpr = "";
        public string BindKind = "";
        public string DefaultExpr = "default!";
        public bool IsIntegerKey;
    }

    private sealed class Model
    {
        public string TypeName = "";
        public string DisplayName = "";
        public string Table = "";
        public bool IsStruct;
        public List<Member> Members = new List<Member>();
        public List<string> CtorParams = new List<string>();
        public bool AutoKey;
        public int AutoKeyMember = -1;
    }

    private static (Model?, ImmutableArray<Diagnostic>) Analyze(GeneratorAttributeSyntaxContext ctx)
    {
        var diags = ImmutableArray.CreateBuilder<Diagnostic>();
        var type = (INamedTypeSymbol)ctx.TargetSymbol;
        Location loc = type.Locations.FirstOrDefault() ?? Location.None;
        for (INamedTypeSymbol? t = type; t != null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility == Accessibility.Private || t.DeclaredAccessibility == Accessibility.Protected || t.DeclaredAccessibility == Accessibility.ProtectedAndInternal)
            {
                diags.Add(Diagnostic.Create(NotAccessible, loc, type.ToDisplayString()));
                return (null, diags.ToImmutable());
            }
        }

        string full = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var model = new Model { TypeName = full, DisplayName = type.Name, IsStruct = type.IsValueType };

        string? tableName = null;
        foreach (AttributeData a in type.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == RowAttribute)
            {
                foreach (KeyValuePair<string, TypedConstant> kv in a.NamedArguments)
                {
                    if (kv.Key == "TableName")
                    {
                        tableName = kv.Value.Value as string;
                    }
                }
            }
        }

        model.Table = tableName ?? (type.Name.ToLowerInvariant() + "s");

        // The constructor with the most parameters (a record's primary constructor, usually).
        IMethodSymbol? ctor = null;
        foreach (IMethodSymbol c in type.InstanceConstructors)
        {
            if (c.DeclaredAccessibility == Accessibility.Private || c.DeclaredAccessibility == Accessibility.Protected)
            {
                continue;
            }

            if (c.IsImplicitlyDeclared && type.IsRecord && c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type))
            {
                continue; // the record copy constructor
            }

            if (c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type) && type.IsRecord)
            {
                continue;
            }

            if (ctor == null || c.Parameters.Length > ctor.Parameters.Length)
            {
                ctor = c;
            }
        }

        if (ctor == null && !type.IsValueType)
        {
            diags.Add(Diagnostic.Create(NoConstructor, loc, type.ToDisplayString()));
            return (null, diags.ToImmutable());
        }

        var props = new List<IPropertySymbol>();
        var seen = new HashSet<string>();
        for (INamedTypeSymbol? t = type; t != null && t.SpecialType != SpecialType.System_Object && t.SpecialType != SpecialType.System_ValueType; t = t.BaseType)
        {
            foreach (ISymbol m in t.GetMembers())
            {
                if (m is IPropertySymbol p && !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public && p.GetMethod != null
                    && !p.Name.Equals("EqualityContract") && seen.Add(p.Name) && !HasAttribute(p, "OneLiteIgnoreAttribute"))
                {
                    props.Add(p);
                }
            }
        }

        bool anyKeyAttr = props.Any(p => HasAttribute(p, "OneLiteKeyAttribute"));
        foreach (IPropertySymbol p in props)
        {
            var mem = new Member { Property = p.Name, Column = p.Name };
            foreach (AttributeData a in p.GetAttributes())
            {
                if (a.AttributeClass?.Name == "OneLiteColumnAttribute" && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is string cn)
                {
                    mem.Column = cn;
                }
            }

            mem.IsKey = anyKeyAttr ? HasAttribute(p, "OneLiteKeyAttribute") : string.Equals(p.Name, "Id", System.StringComparison.OrdinalIgnoreCase);

            ITypeSymbol pt = p.Type;
            bool nullableValue = pt is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            ITypeSymbol core = nullableValue ? ((INamedTypeSymbol)pt).TypeArguments[0] : pt;
            bool isRef = !core.IsValueType;
            bool nullable = nullableValue || (isRef && pt.NullableAnnotation != NullableAnnotation.NotAnnotated);
            mem.NotNull = !nullable;
            mem.TypeName = pt.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string coreName = core.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).TrimEnd('?');
            string? sql = SqlTypeOf(core, out bool isEnum, out string kind);
            if (sql == null)
            {
                diags.Add(Diagnostic.Create(UnsupportedMember, p.Locations.FirstOrDefault() ?? loc, type.Name, p.Name, pt.ToDisplayString()));
                continue;
            }

            mem.SqlType = sql;
            mem.BindKind = kind;
            mem.IsIntegerKey = mem.IsKey && sql == "INTEGER" && !isEnum && kind == "long";

            string args = "st, ord[IDX], \"" + Esc(full) + "\", \"" + Esc(mem.Column) + "\", \"" + Esc(mem.Property) + "\"";
            string readType = isEnum ? "long" : coreName;
            string read;
            if (nullableValue)
            {
                read = "global::RecursiveAI.OneLite.RowReader.GetNullable<" + readType + ">(" + args + ")";
                if (isEnum)
                {
                    read = "(" + read + " is { } __e" + "IDX ? (" + mem.TypeName + ")(" + coreName + ")__e" + "IDX : default(" + mem.TypeName + "))";
                }
            }
            else if (isRef && nullable)
            {
                read = "global::RecursiveAI.OneLite.RowReader.GetRef<" + readType + ">(" + args + ")";
            }
            else
            {
                read = "global::RecursiveAI.OneLite.RowReader.Get<" + readType + ">(" + args + ")";
                if (isEnum)
                {
                    read = "(" + coreName + ")" + read;
                }
            }

            mem.ReadExpr = read;

            // Reading: a constructor parameter with this name, or a property setter.
            IParameterSymbol? cp = ctor?.Parameters.FirstOrDefault(x => string.Equals(x.Name, p.Name, System.StringComparison.OrdinalIgnoreCase));
            bool settable = p.SetMethod != null && p.SetMethod.DeclaredAccessibility != Accessibility.Private && p.SetMethod.DeclaredAccessibility != Accessibility.Protected;
            mem.InitOnly = p.SetMethod != null && p.SetMethod.IsInitOnly;
            bool requiredMember = p.IsRequired;
            if (cp != null)
            {
                mem.CtorIndex = cp.Ordinal;
                mem.CanRead = true;
                mem.Required = !cp.HasExplicitDefaultValue;
                if (cp.HasExplicitDefaultValue)
                {
                    mem.DefaultExpr = DefaultLiteral(cp, mem.TypeName);
                }
            }
            else if (settable)
            {
                mem.CanRead = true;
                mem.Required = requiredMember;
            }

            model.Members.Add(mem);
        }

        if (diags.Count > 0)
        {
            return (null, diags.ToImmutable());
        }

        // Constructor parameters that match no property are an error at runtime of the generated code; fail now with a plain message.
        model.CtorParams = ctor == null ? new List<string>() : ctor.Parameters.Select(x => x.Name).ToList();
        if (ctor != null)
        {
            foreach (IParameterSymbol cpar in ctor.Parameters)
            {
                if (!model.Members.Any(m => m.CtorIndex == cpar.Ordinal))
                {
                    diags.Add(Diagnostic.Create(UnsupportedMember, loc, type.Name, cpar.Name, "constructor parameter without a matching public property"));
                }
            }
        }

        int keyCount = model.Members.Count(m => m.IsKey);
        if (keyCount == 1)
        {
            int idx = model.Members.FindIndex(m => m.IsKey);
            if (model.Members[idx].IsIntegerKey)
            {
                model.AutoKey = true;
                model.AutoKeyMember = idx;
            }
        }

        return (model, diags.ToImmutable());
    }

    private static bool HasAttribute(ISymbol s, string name) => s.GetAttributes().Any(a => a.AttributeClass?.Name == name);

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string DefaultLiteral(IParameterSymbol p, string typeName)
    {
        object? v = p.ExplicitDefaultValue;
        if (v == null)
        {
            return "default!";
        }

        if (p.Type.TypeKind == TypeKind.Enum)
        {
            return "(" + typeName + ")" + SymbolDisplay.FormatPrimitive(v, false, false);
        }

        if (v is string || v is bool || v is char || v.GetType().IsPrimitive || v is decimal)
        {
            string lit = SymbolDisplay.FormatPrimitive(v, true, false);
            return "(" + typeName + ")" + lit;
        }

        return "default!";
    }

    /// <summary>The STRICT column type for a C# type, or null if it cannot be mapped. <paramref name="kind"/> tells the binder how to pass the value.</summary>
    private static string? SqlTypeOf(ITypeSymbol t, out bool isEnum, out string kind)
    {
        isEnum = t.TypeKind == TypeKind.Enum;
        kind = "object";
        if (isEnum)
        {
            kind = "enum";
            return "INTEGER";
        }

        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean:
                kind = "bool";
                return "INTEGER";
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                kind = "int";
                return "INTEGER";
            case SpecialType.System_Int64:
                kind = "long";
                return "INTEGER";
            case SpecialType.System_UInt64:
                kind = "object";
                return "INTEGER";
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                kind = "double";
                return "REAL";
            case SpecialType.System_String:
                kind = "string";
                return "TEXT";
            case SpecialType.System_Char:
            case SpecialType.System_Decimal:
            case SpecialType.System_DateTime:
                return "TEXT";
        }

        if (t is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte)
        {
            return "BLOB";
        }

        switch (t.ToDisplayString())
        {
            case "System.Guid":
            case "System.DateTimeOffset":
            case "System.TimeSpan":
            case "System.DateOnly":
            case "System.TimeOnly":
                return "TEXT";
            default:
                return null;
        }
    }

    private static string Emit(List<Model> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by RecursiveAI.OneLite.Generators. Do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable");
        sb.AppendLine("namespace RecursiveAI.OneLite.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class OneLiteRowMapperRegistration");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");
        for (int i = 0; i < models.Count; i++)
        {
            sb.AppendLine("            global::RecursiveAI.OneLite.RowMappers.Register<" + models[i].TypeName + ">(new RowMapper" + i + "());");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        for (int i = 0; i < models.Count; i++)
        {
            EmitMapper(sb, models[i], i);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitMapper(StringBuilder sb, Model m, int n)
    {
        string t = m.TypeName;
        sb.AppendLine("    internal sealed class RowMapper" + n + " : global::RecursiveAI.OneLite.IRowMapper<" + t + ">");
        sb.AppendLine("    {");
        sb.AppendLine("        private static readonly global::RecursiveAI.OneLite.RowColumn[] s_columns =");
        sb.AppendLine("        [");
        foreach (Member mem in m.Members)
        {
            sb.AppendLine("            new(\"" + Esc(mem.Column) + "\", \"" + Esc(mem.Property) + "\", \"" + mem.SqlType + "\", " + B(mem.NotNull) + ", " + B(mem.IsKey) + ", " + B(mem.Required) + ", " + B(mem.CanRead) + "),");
        }

        sb.AppendLine("        ];");
        sb.AppendLine("        public string TableName => \"" + Esc(m.Table) + "\";");
        sb.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::RecursiveAI.OneLite.RowColumn> Columns => s_columns;");
        sb.AppendLine("        public bool HasAutoKey => " + B(m.AutoKey) + ";");
        sb.AppendLine("        public int[] Resolve(global::RecursiveAI.OneLite.Statement statement) => global::RecursiveAI.OneLite.RowReader.Resolve(statement, s_columns, \"" + Esc(m.DisplayName) + "\");");

        // Read: locals for every readable member, then construct.
        sb.AppendLine("        public " + t + " Read(global::RecursiveAI.OneLite.Statement st, int[] ord)");
        sb.AppendLine("        {");
        for (int i = 0; i < m.Members.Count; i++)
        {
            Member mem = m.Members[i];
            if (!mem.CanRead)
            {
                continue;
            }

            string expr = mem.ReadExpr.Replace("IDX", i.ToString(CultureInfo.InvariantCulture));
            string fallback = mem.CtorIndex >= 0 ? mem.DefaultExpr : "default(" + mem.TypeName + ")!";
            sb.AppendLine("            " + mem.TypeName + " v" + i + " = ord[" + i + "] >= 0 ? " + expr + " : " + fallback + ";");
        }

        var ctorArgs = new string[m.CtorParams.Count];
        var inits = new List<string>();
        var posts = new List<string>();
        for (int i = 0; i < m.Members.Count; i++)
        {
            Member mem = m.Members[i];
            if (!mem.CanRead)
            {
                continue;
            }

            if (mem.CtorIndex >= 0)
            {
                ctorArgs[mem.CtorIndex] = "v" + i;
            }
            else if (mem.InitOnly || mem.Required)
            {
                inits.Add(mem.Property + " = v" + i);
            }
            else
            {
                posts.Add("if (ord[" + i + "] >= 0) r." + mem.Property + " = v" + i + ";");
            }
        }

        string ctor = "new " + t + "(" + string.Join(", ", ctorArgs) + ")" + (inits.Count > 0 ? " { " + string.Join(", ", inits) + " }" : string.Empty);
        sb.AppendLine("            var r = " + ctor + ";");
        foreach (string p in posts)
        {
            sb.AppendLine("            " + p);
        }

        sb.AppendLine("            return r;");
        sb.AppendLine("        }");

        // Bind
        sb.AppendLine("        public void Bind(global::RecursiveAI.OneLite.Statement st, int first, " + t + " value)");
        sb.AppendLine("        {");
        for (int i = 0; i < m.Members.Count; i++)
        {
            Member mem = m.Members[i];
            string v = "value." + mem.Property;
            string idx = "first + " + i;
            if (m.AutoKey && m.AutoKeyMember == i)
            {
                sb.AppendLine("            if (" + v + " == 0) st.BindNull(" + idx + "); else st.Bind(" + idx + ", (long)" + v + ");");
                continue;
            }

            switch (mem.BindKind)
            {
                case "long":
                case "int":
                    if (mem.NotNull)
                    {
                        sb.AppendLine("            st.Bind(" + idx + ", (long)" + v + ");");
                    }
                    else
                    {
                        sb.AppendLine("            if (" + v + " is { } n" + i + ") st.Bind(" + idx + ", (long)n" + i + "); else st.BindNull(" + idx + ");");
                    }

                    break;
                case "double":
                    if (mem.NotNull)
                    {
                        sb.AppendLine("            st.Bind(" + idx + ", (double)" + v + ");");
                    }
                    else
                    {
                        sb.AppendLine("            if (" + v + " is { } n" + i + ") st.Bind(" + idx + ", (double)n" + i + "); else st.BindNull(" + idx + ");");
                    }

                    break;
                case "string":
                    sb.AppendLine("            if (" + v + " is { } s" + i + ") st.Bind(" + idx + ", s" + i + "); else st.BindNull(" + idx + ");");
                    break;
                default:
                    sb.AppendLine("            global::RecursiveAI.OneLite.StatementValues.Bind(st, " + idx + ", (object?)" + v + ");");
                    break;
            }
        }

        sb.AppendLine("        }");

        int ki = m.Members.FindIndex(x => x.IsKey);
        int kc = m.Members.Count(x => x.IsKey);
        sb.AppendLine("        public object? KeyOf(" + t + " value) => " + (kc == 1 ? "(object?)value." + m.Members[ki].Property : "null") + ";");
        sb.AppendLine("    }");
    }

    private static string B(bool b) => b ? "true" : "false";
}
