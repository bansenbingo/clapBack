namespace ClapBack.Core.Abstractions;

public interface ITextSender
{
    Task SendAsync(string text, CancellationToken cancellationToken = default);
}

public interface ITargetWindowAware
{
    void CaptureCurrentTarget();
}
