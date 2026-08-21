namespace OpenAgentOrchestrator.Command.Contract
{
    /// <summary>
    /// The single, explicit action a <see cref="ResumeRequest"/> can express - what the caller
    /// wants to do with a checkpointed session, rather than something inferred from a combination
    /// of optional/nullable fields.
    /// </summary>
    public enum ResumeAction
    {
        /// <summary>
        /// Approves the session's currently outstanding human-in-the-loop review - optionally
        /// substituting <see cref="ResumeRequest.EditedOutput"/> for the pending step's output -
        /// and advances execution forward. Only valid while the session is
        /// <c>pending_approval</c>; requesting this for any other session status is rejected.
        /// </summary>
        Continue,

        /// <summary>
        /// Discards <see cref="ResumeRequest.StepIndex"/>'s current output (and every later
        /// step's), and re-executes it - and everything after it - fresh. Valid from any session
        /// status (<c>pending_approval</c>, <c>running</c>, <c>failed</c>, <c>completed</c>), and
        /// <see cref="ResumeRequest.StepIndex"/> may name an already-completed step or the step
        /// currently awaiting review - both are simply "redo this step," with no special-casing
        /// required by the caller. Requires <see cref="ResumeRequest.StepIndex"/> to be set.
        /// </summary>
        RedoFromStep,

        /// <summary>
        /// Abandons the session outright. Terminal: no further resume of any kind is possible
        /// afterwards. Only valid while the session is <c>pending_approval</c>; requesting this
        /// for any other session status is rejected.
        /// </summary>
        Reject
    }
}
