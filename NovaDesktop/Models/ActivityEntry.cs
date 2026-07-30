namespace NovaDesktop.Models;

public sealed record ActivityEntry(
    string Agent,
    string Action,
    string Detail,
    string Time,
    ActivityKind Kind = ActivityKind.Working);

public enum ActivityKind
{
    Working,
    Completed,
    Waiting,
    System
}
