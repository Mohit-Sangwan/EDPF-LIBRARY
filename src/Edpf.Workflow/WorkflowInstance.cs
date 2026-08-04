using System;
using System.Collections.Generic;

namespace Edpf.Workflow;

/// <summary>One state change that actually happened.</summary>
/// <remarks>
/// The history is append-only and is the workflow's audit trail. "How did this
/// referral end up rejected" is a question somebody will ask months later, and
/// the current state alone cannot answer it.
/// </remarks>
public sealed class WorkflowTransitionRecord
{
    /// <summary>
    /// Records a completed transition.
    /// </summary>
    /// <param name="from">The state left.</param>
    /// <param name="trigger">What caused the move.</param>
    /// <param name="to">The state arrived at.</param>
    /// <param name="actor">Who or what fired it. Never a raw patient identifier.</param>
    /// <param name="occurredUtc">When, normalised to the platform's storable resolution.</param>
    public WorkflowTransitionRecord(
        string from, string trigger, string to, string actor, DateTimeOffset occurredUtc)
    {
        From = from;
        Trigger = trigger;
        To = to;
        Actor = actor;
        OccurredUtc = occurredUtc;
    }

    /// <summary>The state left.</summary>
    public string From { get; }

    /// <summary>What caused the move.</summary>
    public string Trigger { get; }

    /// <summary>The state arrived at.</summary>
    public string To { get; }

    /// <summary>Who or what fired it.</summary>
    public string Actor { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset OccurredUtc { get; }
}

/// <summary>
/// One running workflow, scoped to a tenant and carrying its own history.
/// </summary>
/// <remarks>
/// <see cref="Version"/> exists so a workflow obeys ADR-020 like everything
/// else: two clinicians who open the same referral and both click Approve must
/// not both succeed. Optimistic by default, and there is no method here that
/// applies a transition without checking it.
/// </remarks>
public sealed class WorkflowInstance
{
    private readonly List<WorkflowTransitionRecord> _history = [];

    /// <summary>
    /// Starts an instance in a definition's initial state.
    /// </summary>
    /// <param name="definition">The workflow being run.</param>
    /// <param name="instanceId">This instance's id.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">Either id is empty.</exception>
    public WorkflowInstance(WorkflowDefinition definition, Guid instanceId, Guid tenantId)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("An instance requires an id.", nameof(instanceId));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "A workflow instance requires a tenant; an unscoped instance is not constructible.",
                nameof(tenantId));
        }

        WorkflowId = definition.WorkflowId;
        InstanceId = instanceId;
        TenantId = tenantId;
        CurrentState = definition.InitialState;
    }

    /// <summary>The definition this instance runs.</summary>
    public string WorkflowId { get; }

    /// <summary>This instance's id.</summary>
    public Guid InstanceId { get; }

    /// <summary>The owning tenant. Always set.</summary>
    public Guid TenantId { get; }

    /// <summary>Where the instance currently is.</summary>
    public string CurrentState { get; private set; }

    /// <summary>The optimistic concurrency token, incremented on every transition.</summary>
    public int Version { get; private set; }

    /// <summary>Every transition that happened, oldest first.</summary>
    public IReadOnlyList<WorkflowTransitionRecord> History => _history;

    /// <summary>
    /// Applies a transition. Called only by <see cref="WorkflowEngine"/>, after
    /// every check has passed.
    /// </summary>
    /// <param name="record">The transition to record.</param>
    internal void Apply(WorkflowTransitionRecord record)
    {
        CurrentState = record.To;
        Version++;
        _history.Add(record);
    }
}
