namespace OpenAgentOrchestrator.Command.Domain.Model.Configuration
{
    /// <summary>
    /// Opt-in configuration for the Microsoft Agent Framework's "planning and todos" primitives -
    /// <c>TodoProvider</c> (a trackable todo list, exposed to the model as
    /// <c>todos_add</c>/<c>todos_complete</c>/<c>todos_remove</c>/<c>todos_get_remaining</c>/
    /// <c>todos_get_all</c> tools) and <c>AgentModeProvider</c> (a <c>plan</c>/<c>execute</c>
    /// mode switch, exposed as <c>mode_get</c>/<c>mode_set</c> tools) - plus a bounded
    /// todo-completion loop (<c>LoopAgent</c> + <c>TodoCompletionLoopEvaluator</c>) that keeps
    /// re-invoking the agent while todos remain incomplete in specific modes. See
    /// https://learn.microsoft.com/agent-framework/agents/planning-and-todos.
    /// Applies to both <c>agentType: chat</c> and <c>agentType: harness</c> agents; entirely
    /// optional - omitting <see cref="AgentDefinition.Planning"/> leaves agent behavior unchanged.
    /// </summary>
    public sealed class PlanningDefinition
    {
        /// <summary>
        /// Enables the todo-list context provider (<c>TodoProvider</c>) for this agent. Default:
        /// <see langword="false"/>.
        /// </summary>
        public bool EnableTodos { get; set; }

        /// <summary>
        /// Enables the plan/execute agent-mode context provider (<c>AgentModeProvider</c>) for
        /// this agent. Default: <see langword="false"/>.
        /// </summary>
        public bool EnableAgentMode { get; set; }

        /// <summary>
        /// Default mode name, used only when <see cref="EnableAgentMode"/> is
        /// <see langword="true"/>. Optional - falls back to the framework's own default
        /// (<c>"plan"</c>) when omitted.
        /// </summary>
        public string? DefaultMode { get; set; }

        /// <summary>
        /// Custom mode names and per-mode instructions, overriding the framework's built-in
        /// <c>plan</c>/<c>execute</c> pair. Optional - omit to use the framework's defaults.
        /// Only used when <see cref="EnableAgentMode"/> is <see langword="true"/>.
        /// </summary>
        public List<AgentModeDefinition>? Modes { get; set; }

        /// <summary>
        /// Enables a bounded todo-completion loop: the agent is wrapped in a <c>LoopAgent</c>
        /// paired with a <c>TodoCompletionLoopEvaluator</c>, which re-invokes the agent (up to a
        /// hardcoded 5 iterations - not configurable) while incomplete todos remain in one of
        /// <see cref="LoopModes"/>. Only meaningful when <see cref="EnableTodos"/> is also
        /// <see langword="true"/> (enforced by <c>ConfigValidator</c>). Default:
        /// <see langword="false"/>.
        /// </summary>
        public bool EnableTodoLoop { get; set; }

        /// <summary>
        /// Which agent-mode names the todo-completion loop should keep iterating in (e.g.
        /// <c>["execute"]</c>). Only used when <see cref="EnableTodoLoop"/> is
        /// <see langword="true"/>. Optional - defaults to <c>["execute"]</c> when omitted.
        /// </summary>
        public List<string>? LoopModes { get; set; }
    }

    /// <summary>A single custom agent-mode name + its steering instructions.</summary>
    public sealed class AgentModeDefinition
    {
        public required string Name { get; set; }
        public required string Instructions { get; set; }
    }
}
