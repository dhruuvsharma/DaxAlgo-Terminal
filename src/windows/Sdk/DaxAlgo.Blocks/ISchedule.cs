namespace DaxAlgo.Blocks;

/// <summary>Timers, and a way back onto the unit's own thread.</summary>
[BlockCard(
    "schedule",
    "timers, and running work on the unit's thread",
    Does = "Runs work repeatedly or once after a delay, and runs work handed over from another thread (a network callback, an awaited task) on the unit's own thread.",
    Needs = "Nothing.",
    Limits = "Timers and posted work run one at a time with every other handler, in order. Intervals shorter than 100 ms are raised to 100 ms. Threads and System.Threading timers are not allowed — use this.",
    Order = 60)]
public interface ISchedule
{
    /// <summary>Runs <paramref name="work"/> every <paramref name="interval"/>; dispose to stop.</summary>
    IDisposable Every(TimeSpan interval, Action work);

    /// <summary>Runs <paramref name="work"/> once after <paramref name="delay"/>; dispose to cancel.</summary>
    IDisposable After(TimeSpan delay, Action work);

    /// <summary>Runs <paramref name="work"/> on the unit's thread as soon as it is free; safe to call from any thread.</summary>
    void Post(Action work);
}
