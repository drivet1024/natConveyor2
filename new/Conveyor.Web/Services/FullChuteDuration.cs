namespace Conveyor.Web.Services;

internal sealed class FullChuteDuration
{
    private TimeSpan _total;
    private DateTimeOffset? _since;

    public TimeSpan GetTotal(DateTimeOffset now) => _total +
        (_since is { } since && now > since ? now - since : TimeSpan.Zero);

    public void Observe(bool? full, DateTimeOffset now)
    {
        if (full == true)
            _since ??= now;
        else
        {
            _total = GetTotal(now);
            _since = null;
        }
    }
}
