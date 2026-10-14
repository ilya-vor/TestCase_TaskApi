namespace TaskApi.Domain;

/// <summary>Priority of a task. Serialized as a string ("Low" / "Medium" / "High").</summary>
public enum Priority
{
    Low = 0,
    Medium = 1,
    High = 2
}
