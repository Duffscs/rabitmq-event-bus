using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Library;
public static class TypeResolver {

    private static readonly Dictionary<string, Type> commonTypes = new() {
        ["bool"] = typeof(bool),
        ["byte"] = typeof(byte),
        ["sbyte"] = typeof(sbyte),
        ["char"] = typeof(char),
        ["decimal"] = typeof(decimal),
        ["double"] = typeof(double),
        ["float"] = typeof(float),
        ["int"] = typeof(int),
        ["uint"] = typeof(uint),
        ["long"] = typeof(long),
        ["ulong"] = typeof(ulong),
        ["short"] = typeof(short),
        ["ushort"] = typeof(ushort),
        ["string"] = typeof(string),
        ["object"] = typeof(object),
        ["Guid"] = typeof(Guid),
    };
    private const string Generic = "System.Collections.Generic.";
    private const string Concurrent = "System.Collections.Concurrent.";
    private static readonly Dictionary<string, string> genericType = new() {
        ["List"] = Generic + "List",
        ["Dictionary"] = Generic + "Dictionary",
        ["HashSet"] = Generic + "HashSet",
        ["Queue"] = Generic + "Queue",
        ["Stack"] = Generic + "Stack",
        ["ConcurrentDictionary"] = Concurrent + "ConcurrentDictionary",
        ["ConcurrentBag"] = Concurrent + "ConcurrentBag",
        ["ConcurrentQueue"] = Concurrent + "ConcurrentQueue",
        ["ConcurrentStack"] = Concurrent + "ConcurrentStack",
    };

    public static Type? ResolveType(string typeName) {
        typeName = typeName.Trim();
        // ✅ Type alias direct (int, string, etc.)
        if (commonTypes.TryGetValue(typeName, out Type? primitive))
            return primitive;

        // ✅ Tableau
        if (typeName.EndsWith("[]")) {
            Type? elementType = ResolveType(typeName[..^2]);
            return elementType?.MakeArrayType();
        }

        // ✅ Générique ex: List<System.Int32>
        int genStart = typeName.IndexOf('<');
        if (genStart != -1) {
            string baseName = typeName[..genStart];
            if(genericType.TryGetValue(baseName, out string? commonFullName))
                baseName = commonFullName.Split("`")[0];
            string argsPart = typeName[(genStart + 1)..^1]; // contenu entre <...>

            string[] argStrings = SplitGenericArgs(argsPart);
            Type?[] typeArgs = argStrings.Select(ResolveType).ToArray();
            if (typeArgs.Any(t => t == null))
                return null;
            Type? baseType = FindType(baseName + "`" + argStrings.Length + "[" + string.Join(",", typeArgs.Select(e => e.FullName)) + "]");
            if (baseType == null)
                return null;
            return baseType;
        }

        // ✅ Sinon, type simple
        return FindType(typeName);
    }

    private static Type? FindType(string name) {
        // Tentative directe
        Type? t = Type.GetType(name);
        if (t != null)
            return t;

        // Recherche dans les assemblies chargés
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
            t = asm.GetType(name, throwOnError: false);
            if (t != null)
                return t;
        }
        return null;
    }

    private static string[] SplitGenericArgs(string args) {
        List<string> parts = new();
        int depth = 0;
        StringBuilder current = new();

        foreach (char c in args) {
            if (c == '<')
                depth++;
            if (c == '>')
                depth--;
            if (c == ',' && depth == 0) {
                parts.Add(current.ToString().Trim());
                current.Clear();
            } else
                current.Append(c);
        }

        if (current.Length > 0)
            parts.Add(current.ToString().Trim());

        return parts.ToArray();
    }
}
