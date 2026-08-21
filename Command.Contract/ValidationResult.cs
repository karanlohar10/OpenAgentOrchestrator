namespace OpenAgentOrchestrator.Command.Contract
{
    public sealed class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
    }
}
