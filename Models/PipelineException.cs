namespace backend.Models;

public class PipelineException : Exception
{
    public string RunId { get; }
    public string Stage { get; }

    public PipelineException(string runId, string stage, Exception innerException)
        : base($"Video processing failed during '{stage}': {innerException.Message}", innerException)
    {
        RunId = runId;
        Stage = stage;
    }
}
