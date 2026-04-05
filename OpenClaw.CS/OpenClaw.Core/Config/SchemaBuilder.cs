namespace OpenClaw.Config;

#pragma warning disable CS8981

/// <summary>
/// Fluent schema builder helper for constructing schemas via methods.
/// </summary>
public static class SchemaBuilder
{
    /// <summary>
    /// String schema shorthand.
    /// </summary>
    public static StringSchema String(
        bool optional = false,
        int? minLength = null,
        int? maxLength = null,
        string[]? allowedValues = null) => new(optional, minLength, maxLength, null, allowedValues);
    
    /// <summary>
    /// Number schema shorthand.
    /// </summary>
    public static NumberSchema Number(
        bool optional = false,
        bool isInteger = false,
        double? minimum = null,
        double? maximum = null) => new(optional, isInteger, minimum, maximum);
    
    /// <summary>
    /// Boolean schema shorthand.
    /// </summary>
    public static BooleanSchema Bool(bool optional = false) => new(optional);
    
    /// <summary>
    /// Object schema shorthand with fluent configuration.
    /// </summary>
    public static ObjectSchema Object(
        Dictionary<string, Schema>? properties = null,
        string[]? required = null) => new(properties, required);
    
    /// <summary>
    /// Array schema shorthand.
    /// </summary>
    public static ArraySchema Array(Schema? itemsSchema = null, int? minItems = null, int? maxItems = null) 
        => new(itemsSchema, minItems, maxItems);
    
    /// <summary>
    /// Create an object schema builder action for fluent configuration.
    /// </summary>
    public static ObjectSchemaBuilder ObjectBuilder() => new();
}

/// <summary>
/// Fluent object schema builder.
/// </summary>
public sealed class ObjectSchemaBuilder
{
    private readonly Dictionary<string, Schema> _properties = new();
    private readonly List<string> _required = new();
    private bool _allowAdditional = true;
    
    /// <summary>
    /// Add a required string property.
    /// </summary>
    public ObjectSchemaBuilder WithString(string name, bool required = true, int? minLength = null, int? maxLength = null)
    {
        _properties[name] = new StringSchema(false, minLength, maxLength);
        if (required) _required.Add(name);
        return this;
    }
    
    /// <summary>
    /// Add an optional string property.
    /// </summary>
    public ObjectSchemaBuilder WithOptionalString(string name, int? minLength = null, int? maxLength = null)
    {
        _properties[name] = new StringSchema(true, minLength, maxLength);
        return this;
    }
    
    /// <summary>
    /// Add an integer property.
    /// </summary>
    public ObjectSchemaBuilder WithInt(string name, bool required = true, int? minimum = null, int? maximum = null)
    {
        _properties[name] = new NumberSchema(false, true, minimum, maximum);
        if (required) _required.Add(name);
        return this;
    }
    
    /// <summary>
    /// Add a boolean property.
    /// </summary>
    public ObjectSchemaBuilder WithBool(string name, bool required = true)
    {
        _properties[name] = new BooleanSchema(false);
        if (required) _required.Add(name);
        return this;
    }
    
    /// <summary>
    /// Add an object property.
    /// </summary>
    public ObjectSchemaBuilder WithObject(string name, ObjectSchemaBuilder nested, bool required = true)
    {
        _properties[name] = nested.Build();
        if (required) _required.Add(name);
        return this;
    }
    
    /// <summary>
    /// Build the final object schema.
    /// </summary>
    public ObjectSchema Build()
    {
        return new ObjectSchema(_properties, _required.ToArray());
    }
}

#pragma warning restore CS8981
