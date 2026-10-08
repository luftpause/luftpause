using Luftpause.Scheduling;

namespace Luftpause.Scheduler;

public sealed class Clock(Dispatcher dispatcher, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            dispatcher.Apply(new Tick(time.GetUtcNow().ToUnixTimeMilliseconds()));
    }
}
