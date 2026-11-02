using System;
using System.Collections;
using System.Reflection;
using System.Text;

namespace RecursiveAI.OneLite.SqlCompare;

/// <summary>
/// A complete, span-free text form of a syntax tree, so two trees can be compared for equality. It reads the tree's record properties
/// by reflection, which is fine in a tool and a test; the parser itself uses none.
/// </summary>
internal static class AstDump
{
    internal static string Of(object? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                return;
            case bool or int or long or Enum:
                sb.Append(v);
                return;
        }

        Type t = v.GetType();
        if (t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("ImmutableArray", StringComparison.Ordinal))
        {
            if ((bool)t.GetProperty("IsDefault")!.GetValue(v)!)
            {
                sb.Append("default");
                return;
            }

            sb.Append('[');
            bool first = true;
            foreach (object? item in (IEnumerable)v)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                Write(sb, item);
            }

            sb.Append(']');
            return;
        }

        sb.Append(t.Name).Append('(');
        bool firstProp = true;
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Dump(sb, v, p, ref firstProp);
        }

        for (Type? b = t.BaseType; b != null && b.Namespace == t.Namespace && b != typeof(object); b = b.BaseType)
        {
            foreach (PropertyInfo p in b.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Dump(sb, v, p, ref firstProp);
            }
        }

        sb.Append(')');
    }

    private static void Dump(StringBuilder sb, object v, PropertyInfo p, ref bool first)
    {
        if (p.Name is "Span" or "EqualityContract" || p.GetIndexParameters().Length > 0 || p.GetMethod == null || p.GetMethod.IsStatic)
        {
            return;
        }

        // computed convenience properties (WithoutRowid, SchemaSqlStart) repeat what the stored members already say
        if (p.GetMethod.IsAbstract || p.Name is "SchemaSqlStart" or "WithoutRowid" or "Strict")
        {
            if (p.Name is "SchemaSqlStart" or "WithoutRowid" or "Strict")
            {
                return;
            }
        }

        if (!first)
        {
            sb.Append(',');
        }

        first = false;
        sb.Append(p.Name).Append('=');
        Write(sb, p.GetValue(v));
    }
}
