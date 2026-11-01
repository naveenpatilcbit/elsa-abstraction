namespace Demo.Tasks;

// Optional engine-free identity. Keep old versions deployed while saved flows use them.
[AttributeUsage(AttributeTargets.Class)]
public sealed class TaskIdentityAttribute(string name, int version = 1) : Attribute
{
    public string Name { get; } = name;
    public int Version { get; } = version;
}
