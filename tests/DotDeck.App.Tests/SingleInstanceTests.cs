using System.IO.Pipes;
using DotDeck.App.Infrastructure.Windows;

namespace DotDeck.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void Second_instance_requests_activation_without_acquiring_the_mutex()
    {
        var name = "DotDeck.Tests." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(name);
        Assert.True(primary.TryAcquire());
        using var activated = new SemaphoreSlim(0);
        primary.Listen(() => activated.Release());

        for (var i = 0; i < 3; i++)
        {
            RunOnWorker(() => RequestActivation(name));
            Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void Request_waits_for_primary_to_finish_creating_its_endpoint()
    {
        var name = "DotDeck.Tests." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(name);
        Assert.True(primary.TryAcquire());
        using var connecting = new ManualResetEventSlim();
        using var activated = new ManualResetEventSlim();
        var request = BeginRequest(name, connecting);
        Assert.True(connecting.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        primary.Listen(() => activated.Set());
        Complete(request);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Disconnected_client_does_not_break_later_activation()
    {
        var name = "DotDeck.Tests." + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstance(name);
        Assert.True(primary.TryAcquire());
        using var activated = new ManualResetEventSlim();
        primary.Listen(() => activated.Set());
        using (var interrupted = new NamedPipeClientStream(".", name, PipeDirection.Out))
            interrupted.Connect(5000);
        RunOnWorker(() => RequestActivation(name));
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Exiting_releases_ownership_for_a_new_launch()
    {
        var name = "DotDeck.Tests." + Guid.NewGuid().ToString("N");
        using var next = new SingleInstance(name);
        using (var primary = new SingleInstance(name))
        {
            Assert.True(primary.TryAcquire());
            RunOnWorker(() => Assert.False(next.TryAcquire()));
        }
        Assert.True(next.TryAcquire());
    }

    [Fact]
    public void Restart_arguments_are_removed_before_normal_startup()
    {
        Assert.Equal(new[] { "--home", "sandbox" }, SingleInstance.WaitForRestartParent(
            [SingleInstance.RestartSwitch, int.MaxValue.ToString(), "--home", "sandbox"]));
        Assert.Throws<InvalidOperationException>(() => SingleInstance.WaitForRestartParent(
            [SingleInstance.RestartSwitch, Environment.ProcessId.ToString()]));
    }

    [Fact]
    public void Crashed_owner_does_not_prevent_the_next_launch()
    {
        var name = "DotDeck.Tests." + Guid.NewGuid().ToString("N");
        using var abandoned = new Mutex(false, @"Local\" + name);
        // A dedicated thread exits while still owning the mutex, simulating process termination.
        var thread = new Thread(() => Assert.True(abandoned.WaitOne(0)));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        using var next = new SingleInstance(name);
        Assert.True(next.TryAcquire());
    }

    // Keep mutex acquisition/disposal on the test's original thread; async test continuations can change it.
    private static void RunOnWorker(Action action) => Complete(StartWorker(action));

    private static void Complete(Task task) => task.GetAwaiter().GetResult();

    private static Task BeginRequest(string name, ManualResetEventSlim connecting) => StartWorker(
        () => RequestActivation(name, connecting));

    // LongRunning uses a dedicated thread; a blocked wait must not inline the work on the mutex owner's thread.
    private static Task StartWorker(Action action) => Task.Factory.StartNew(action,
        TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void RequestActivation(string name, ManualResetEventSlim? connecting = null)
    {
        using var secondary = new SingleInstance(name);
        Assert.False(secondary.TryAcquire());
        connecting?.Set();
        secondary.ActivateExistingAsync().GetAwaiter().GetResult();
    }
}
