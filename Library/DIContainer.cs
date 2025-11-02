namespace Library;

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

public record ObjectDefinition(string Id, string Type) {
    public List<JsonElement> ConstructorArgs { get; init; } = [];
    public Dictionary<string, JsonElement> Properties { get; init; } = [];
    public JsonElement? Value { get; init; }

    public void Validate() {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidOperationException("L'identifiant de l'objet est manquant ou vide.");

        if (string.IsNullOrWhiteSpace(Type))
            throw new InvalidOperationException($"Le type de l'objet '{Id}' est manquant ou vide.");

        if (Properties.Keys.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"La liste Properties de l'objet '{Id}' contient des clés nulles ou vides.");
        if (Properties.Keys.Count != Properties.Keys.Distinct().Count())
            throw new InvalidOperationException($"La liste Properties de l'objet '{Id}' contient des clés en double.");
    }

    public static object? ConvertJson(JsonElement element, Type targetType, Func<string, object>? resolver = null) {
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        // Résolution de référence @id
        if (element.ValueKind == JsonValueKind.String) {
            string value = element.GetString()!;
            if (value.StartsWith('@')) {
                if (resolver is null)
                    throw new InvalidOperationException($"Référence '{value}' rencontrée sans résolveur disponible.");
                return resolver(value[1..]);
            }
        }

        return System.Type.GetTypeCode(targetType) switch {
            _ when Nullable.GetUnderlyingType(targetType) is Type underlying => ConvertJson(element, underlying, resolver),
            _ when targetType == typeof(TimeSpan) => TimeSpan.Parse(element.GetString()!),
            _ when targetType == typeof(Uri) => new Uri(element.GetString()!, UriKind.RelativeOrAbsolute),
            _ when targetType == typeof(Guid) => Guid.Parse(element.GetString()!),
            _ when targetType.IsEnum => Enum.Parse(targetType, element.GetString()!, ignoreCase: true),
            TypeCode.Boolean => element.GetBoolean(),
            TypeCode.Char => element.GetString()![0],
            TypeCode.Byte => element.GetByte(),
            TypeCode.SByte => element.GetSByte(),
            TypeCode.Int16 => element.GetInt16(),
            TypeCode.UInt16 => element.GetUInt16(),
            TypeCode.UInt32 => element.GetUInt32(),
            TypeCode.Int32 => element.GetInt32(),
            TypeCode.UInt64 => element.GetUInt64(),
            TypeCode.Int64 => element.GetInt64(),
            TypeCode.Single => element.GetSingle(),
            TypeCode.Double => element.GetDouble(),
            TypeCode.Decimal => element.GetDecimal(),
            TypeCode.String => element.GetString(),
            TypeCode.DateTime => element.GetDateTime(),
            _ when element.ValueKind == JsonValueKind.Array => ConvertList(element, targetType, resolver),
            _ when element.ValueKind == JsonValueKind.Object && IsDictionnary(targetType, out Type? dictInterface) => ConvertDictionnary(element, targetType, resolver, dictInterface),
            // Fallback JSON → type .NET
            _ => JsonSerializer.Deserialize(element.GetRawText(), targetType)
        };
    }

    private static bool IsDictionnary(Type targetType, [NotNullWhen(true)] out Type? dictInterface) {
        dictInterface = targetType.IsGenericType &&
                              targetType.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            ? targetType
            : targetType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        return dictInterface is not null;
    }

    private static object ConvertDictionnary(JsonElement element, Type targetType, Func<string, object>? resolver, Type dictInterface) {
        Type keyType = dictInterface.GetGenericArguments()[0];
        Type valueType = dictInterface.GetGenericArguments()[1];

        IDictionary temp = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valueType))!;

        foreach (JsonProperty prop in element.EnumerateObject()) {
            string propName = prop.Name;
            object? key = ConvertJson(JsonSerializer.SerializeToElement(propName), keyType, resolver);
            object? value = ConvertJson(prop.Value, valueType, resolver);
            temp[key] = value;
        }
        if (!targetType.IsInterface)
            return Activator.CreateInstance(targetType, temp)!;
        return temp;
    }

    private static object ConvertList(JsonElement element, Type targetType, Func<string, object>? resolver) {
        Type elementType;
        bool isArray = false;

        if (targetType.IsArray) {
            elementType = targetType.GetElementType()!;
            isArray = true;
        } else if (targetType.IsGenericType) {
            elementType = targetType.GetGenericArguments()[0];
        } else {
            throw new InvalidOperationException($"Le type '{targetType.Name}' ne peut pas être construit à partir d'un tableau JSON.");
        }

        // Construire une List<T> temporaire
        IList tempList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (JsonElement sub in element.EnumerateArray())
            tempList.Add(ConvertJson(sub, elementType, resolver));

        if (isArray) {
            Array array = Array.CreateInstance(elementType, tempList.Count);
            for (int i = 0; i < tempList.Count; i++)
                array.SetValue(tempList[i], i);
            return array;
        }

        // Si c’est une interface connue → type par défaut
        if (targetType.IsInterface) {
            if (targetType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                || targetType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                || targetType.GetGenericTypeDefinition() == typeof(IList<>))
                return tempList;

            if (targetType.GetGenericTypeDefinition() == typeof(ISet<>))
                return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(elementType), tempList)!;
        }

        // Si targetType a un constructeur prenant IEnumerable<T>
        ConstructorInfo? enumerableCtor = targetType
            .GetConstructors()
            .FirstOrDefault(c => {
                ParameterInfo[] p = c.GetParameters();
                return p.Length == 1 &&
                       p[0].ParameterType.IsGenericType &&
                       p[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>);
            });

        if (enumerableCtor != null)
            return enumerableCtor.Invoke([tempList]);


        return tempList;
    }

}

public class DIContainer(IServiceCollection services) {
    private readonly Dictionary<string, object> _objects = [];
    private readonly Dictionary<string, ObjectDefinition> _definitions = [];
    private readonly HashSet<string> _creationStack = [];

    /// <summary>
    /// Charge et instancie tous les objets définis dans un fichier JSON.
    /// </summary>
    public void Load([StringSyntax(StringSyntaxAttribute.Uri)] string configPath) {
        if (!File.Exists(configPath))
            throw new FileNotFoundException("Fichier de configuration introuvable.", configPath);
        string json = File.ReadAllText(configPath);
        Dictionary<string, List<ObjectDefinition>>? config = JsonSerializer.Deserialize<Dictionary<string, List<ObjectDefinition>>>(json);

        if (config is null || !config.TryGetValue("Objects", out List<ObjectDefinition>? definitions))
            throw new InvalidOperationException("Fichier de configuration invalide : section 'Objects' manquante.");

        foreach (ObjectDefinition def in definitions) {
            if (_definitions.ContainsKey(def.Id))
                throw new InvalidOperationException($"L'objet '{def.Id}' est défini plusieurs fois.");
            _definitions[def.Id] = def;
        }

        foreach (ObjectDefinition def in definitions) {
            try {
                EnsureCreated(def.Id);
            } catch (Exception ex) {
                throw new InvalidOperationException($"Erreur lors de la création de l'objet '{def.Id}' : {ex.Message}", ex);
            }
        }

        foreach (object instance in _objects.Values) {
            Type implType = instance.GetType();
            Type[] interfaces = implType.GetInterfaces();

            foreach (Type iface in interfaces)
                services.AddSingleton(iface, instance);

            services.AddSingleton(implType, instance);
        }
    }

    /// <summary>
    /// Retourne une instance par son identifiant.
    /// </summary>
    public T Get<T>(string id) 
        => (T?)_objects.GetValueOrDefault(id) ?? throw new InvalidOperationException($"L'objet '{id}' n'existe pas ou n'a pas pu être instancié.");

    private object EnsureCreated(string id) {
        if (_objects.TryGetValue(id, out object? existing))
            return existing;

        if (_creationStack.Contains(id))
            throw new InvalidOperationException($"Dépendance circulaire détectée lors de la création de '{id}'.");

        if (!_definitions.TryGetValue(id, out ObjectDefinition? def))
            throw new InvalidOperationException($"L'objet '{id}' n'est pas défini dans la configuration.");

        _creationStack.Add(id);

        try {
            Type? type = TypeResolver.ResolveType(def.Type);
            if (type is null)
                throw new InvalidOperationException($"Le type '{def.Type}' de l'objet '{id}' est introuvable.");
            if (def.Value is JsonElement valueElement) {
                object? simpleValue = ObjectDefinition.ConvertJson(valueElement, type, EnsureCreated);
                return _objects[id] = simpleValue!;
            }
            object instance = CreateInstance(def, type);
            InjectProperties(def, instance);
            return _objects[id] = instance;
        } catch (TargetInvocationException ex) {
            throw new InvalidOperationException($"Le constructeur de '{id}' a levé une exception interne : {ex.InnerException?.Message}", ex.InnerException ?? ex);
        } catch (Exception ex) {
            throw new InvalidOperationException($"Impossible d’instancier l'objet '{id}' ({def.Type}) : {ex.Message}", ex);
        } finally {
            _creationStack.Remove(id);
        }
    }

    private object CreateInstance(ObjectDefinition def, Type type) {
        if (def.ConstructorArgs is null || def.ConstructorArgs.Count == 0) {
            ConstructorInfo? defaultCtor = type.GetConstructor(Type.EmptyTypes);
            if (defaultCtor != null)
                return Activator.CreateInstance(type)!;
        }
        (ConstructorInfo Ctor, object?[] Args) = GetConstructor(def, type);
        try {
            return Ctor.Invoke(Args);
        } catch (TargetInvocationException ex) {
            throw new InvalidOperationException($"Erreur interne lors de l’exécution du constructeur de '{def.Id}' : {ex.InnerException?.Message}", ex.InnerException ?? ex);
        } catch (Exception ex) {
            throw new InvalidOperationException($"Échec de l’invocation du constructeur pour '{def.Id}' : {ex.Message}", ex);
        }
    }

    private (ConstructorInfo Ctor, object?[] Args) GetConstructor(ObjectDefinition def, Type type) {
        ConstructorInfo[] constructors = type.GetConstructors();

        if (constructors.Length == 0)
            throw new InvalidOperationException($"Le type '{type.FullName}' ne possède aucun constructeur public.");

        List<(ConstructorInfo Ctor, object?[] Args)> candidates = [];
        List<string> errors = [];

        foreach (ConstructorInfo ctor in constructors) {
            ParameterInfo[] parameters = ctor.GetParameters();
            object?[] args = new object?[parameters.Length];
            bool compatible = true;

            for (int i = 0; i < parameters.Length; i++) {
                JsonElement? argElement = i < def.ConstructorArgs.Count ? def.ConstructorArgs[i] : null;
                ParameterInfo param = parameters[i];
                try {
                    if (argElement is null) {
                        if (param.HasDefaultValue)
                            args[i] = param.DefaultValue;
                        else {
                            compatible = false;
                            break;
                        }
                    } else if (argElement.Value.ValueKind == JsonValueKind.String &&
                               argElement.Value.GetString()!.StartsWith('@')) {
                        string refId = argElement.Value.GetString()![1..];
                        object refInstance = EnsureCreated(refId);
                        if (!param.ParameterType.IsInstanceOfType(refInstance)) {
                            compatible = false;
                            break;
                        }
                        args[i] = refInstance;
                    } else {
                        args[i] = ObjectDefinition.ConvertJson(argElement.Value, param.ParameterType, EnsureCreated);
                    }
                } catch (Exception ex) {
                    compatible = false;
                    errors.Add($"Constructeur {ctor}: erreur sur '{param.Name}' ({param.ParameterType.Name}) → {ex.Message}");
                    break;
                }
            }

            if (compatible)
                candidates.Add((ctor, args));
        }

        if (candidates.Count == 0) {
            string argsList = def.ConstructorArgs.Count == 0 ? "(aucun)" : string.Join(", ", def.ConstructorArgs);
            string joinedErrors = errors.Count > 0 ? "\nDétails:\n - " + string.Join("\n - ", errors) : "";
            throw new InvalidOperationException(
                $"Aucun constructeur compatible trouvé pour l'objet '{def.Id}' ({type.FullName}). " +
                $"Arguments fournis : {argsList}{joinedErrors}");
        }

        return candidates
            .OrderByDescending(c => c.Ctor.GetParameters().Count(p => !p.HasDefaultValue))
            .ThenByDescending(c => c.Ctor.GetParameters().Length)
            .First();
    }

    private void InjectProperties(ObjectDefinition def, object instance) {
        if (def.Properties.Count == 0)
            return;

        foreach ((string name, JsonElement element) in def.Properties) {
            PropertyInfo? prop = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null)
                throw new InvalidOperationException($"La propriété '{name}' n’existe pas sur le type '{instance.GetType().FullName}' (objet '{def.Id}').");
            if (!prop.CanWrite)
                throw new InvalidOperationException($"La propriété '{name}' du type '{instance.GetType().FullName}' n’est pas accessible en écriture (objet '{def.Id}').");

            try {
                if (element.ValueKind == JsonValueKind.String && element.GetString()!.StartsWith('@')) {
                    string refId = element.GetString()![1..];
                    object refInstance = EnsureCreated(refId);
                    if (!prop.PropertyType.IsInstanceOfType(refInstance))
                        throw new InvalidOperationException($"La référence '{refId}' n’est pas du type attendu '{prop.PropertyType.Name}'.");
                    prop.SetValue(instance, refInstance);
                } else {
                    object? converted = ObjectDefinition.ConvertJson(element, prop.PropertyType, EnsureCreated);
                    prop.SetValue(instance, converted);
                }
            } catch (Exception ex) {
                throw new InvalidOperationException($"Erreur d’injection de la propriété '{name}' pour '{def.Id}' : {ex.Message}", ex);
            }
        }
    }
}

