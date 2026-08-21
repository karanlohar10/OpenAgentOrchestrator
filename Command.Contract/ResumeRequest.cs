namespace OpenAgentOrchestrator.Command.Contract
{
    /// <summary>
    /// Request body for resuming a session's workflow from a durable checkpoint. Intent is
    /// expressed explicitly via <see cref="Action"/> - there is no inference from which of
    /// several optional fields happen to be set:
    /// <list type="bullet">
    /// <item><description><see cref="ResumeAction.Continue"/>: approves the session's
    /// outstanding human-in-the-loop review (optionally replacing the pending step's output with
    /// <see cref="EditedOutput"/>) and advances execution forward. Requires the session to be
    /// <c>pending_approval</c>.</description></item>
    /// <item><description><see cref="ResumeAction.Reject"/>: abandons the session. Terminal - no
    /// further resume is possible. Requires the session to be
    /// <c>pending_approval</c>.</description></item>
    /// <item><description><see cref="ResumeAction.RedoFromStep"/>: discards
    /// <see cref="StepIndex"/>'s current output (and every later step's), and re-executes it -
    /// and everything after it - fresh. Works from any session status, and
    /// <see cref="StepIndex"/> may name either an already-completed step or the step currently
    /// awaiting review - both are simply "redo this step." Requires
    /// <see cref="StepIndex"/>. When <see cref="StepIndex"/> is greater than <c>0</c> and names an
    /// already-completed step under human-in-the-loop, <see cref="EditedOutput"/> may also
    /// override the immediately-preceding step's recorded output - see
    /// <see cref="EditedOutput"/>.</description></item>
    /// </list>
    /// </summary>
    public sealed class ResumeRequest
    {
        /// <summary>What to do with this session's checkpoint - see <see cref="ResumeAction"/>.</summary>
        public ResumeAction Action { get; set; }

        /// <summary>
        /// The 0-based index (into the session's checkpoint <c>Steps</c> list) of the step to
        /// redo - re-executing it, and every step after it, discarding their prior outputs.
        /// Required when <see cref="Action"/> is <see cref="ResumeAction.RedoFromStep"/>
        /// (ignored otherwise). May name an already-completed step, or the step currently
        /// awaiting human-in-the-loop review - both are redone identically; there is no need to
        /// reason about which internal checkpoint id corresponds to which step.
        /// </summary>
        public int? StepIndex { get; set; }

        /// <summary>
        /// Meaning depends on <see cref="Action"/>:
        /// <list type="bullet">
        /// <item><description><see cref="ResumeAction.Continue"/>: the edited replacement for the
        /// currently-pending step's own output before it's forwarded to the next step (omit to
        /// use the original, unedited output as-is). Always targets whichever step is currently
        /// awaiting review - there is no step-targeting for this action.</description></item>
        /// <item><description><see cref="ResumeAction.RedoFromStep"/> with <see cref="StepIndex"/>
        /// <c>0</c>: overrides the session's original input for the fresh run (omit to reuse the
        /// original input).</description></item>
        /// <item><description><see cref="ResumeAction.RedoFromStep"/> with <see cref="StepIndex"/>
        /// greater than <c>0</c>, naming an already-completed step (a genuine rewind, not a plain
        /// continue-after-crash/failure), on an orchestrator with human-in-the-loop enabled:
        /// overrides <c>Steps[StepIndex - 1]</c>'s recorded output - the input the redone step
        /// will actually run against - before it's replayed forward. Omit to reuse that prior
        /// step's output as-is. Supplying this for any other <see cref="ResumeAction.RedoFromStep"/>
        /// combination (a plain continue, or human-in-the-loop disabled) throws, since no
        /// mechanism exists to carry the override into the resumed run in either
        /// case.</description></item>
        /// </list>
        /// Ignored for <see cref="ResumeAction.Reject"/>.
        /// </summary>
        public string? EditedOutput { get; set; }
    }
}
