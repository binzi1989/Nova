using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NovaDesktop.Models;

public enum TaskState
{
    Queued,
    Running,
    Waiting,
    Paused,
    Completed,
    Cancelled,
    BudgetExhausted,
    Failed,
    Stale
}

public sealed class TaskItem : INotifyPropertyChanged
{
    private TaskState _state;
    private double _progress;
    private string _stage = "准备中";
    private string _elapsed = "刚刚";
    private bool _isArchived;

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string WorkspaceRoot { get; set; } = string.Empty;
    public string Provider { get; set; } = "openai";
    public string Model { get; set; } = "gpt-5.6";
    public string? AgentPackId { get; set; }
    public AgentExecutionMode ExecutionMode { get; set; } = AgentExecutionMode.Build;
    public string Draft { get; set; } = string.Empty;
    public IReadOnlyList<AgentInputAttachment> Attachments { get; set; } = [];
    public long ExecutionSequence { get; set; }

    public bool IsArchived
    {
        get => _isArchived;
        set => SetField(ref _isArchived, value);
    }

    public TaskState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string Stage
    {
        get => _stage;
        set => SetField(ref _stage, value);
    }

    public string Elapsed
    {
        get => _elapsed;
        set => SetField(ref _elapsed, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
