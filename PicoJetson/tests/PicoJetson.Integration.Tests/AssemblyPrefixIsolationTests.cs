using PicoSerDe.Gen;

namespace PicoJetson.Tests;

/// <summary>
/// Review P2-6: AssemblyPrefix is per-compilation state. A static field was
/// shared across concurrent generator runs in the same process (VBCSCompiler
/// compiles projects in parallel) and could produce mismatched helper
/// namespaces. It must be isolated per execution flow.
/// </summary>
public class AssemblyPrefixIsolationTests
{
    [Test]
    [NotInParallel("GenInfrastructure.AssemblyPrefix")]
    public async Task ConcurrentFlow_DoesNotOverwriteMainFlowPrefix()
    {
        GenInfrastructure.AssemblyPrefix = "__PicoSerDe_Main";
        var childSet = new TaskCompletionSource<bool>();

        var child = Task.Run(() =>
        {
            GenInfrastructure.AssemblyPrefix = "__PicoSerDe_Child";
            childSet.SetResult(true);
            return GenInfrastructure.AssemblyPrefix;
        });

        await childSet.Task;
        var mainValue = GenInfrastructure.AssemblyPrefix;
        var childValue = await child;

        GenInfrastructure.AssemblyPrefix = null;

        await Assert.That(mainValue).IsEqualTo("__PicoSerDe_Main");
        await Assert.That(childValue).IsEqualTo("__PicoSerDe_Child");
    }
}
